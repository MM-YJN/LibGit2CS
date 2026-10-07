// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Globalization;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Utils;

namespace LibGit2CS.Refs;

/// <summary>
/// Access to a reference's reflog. Managed port of libgit2's
/// <c>git_reflog</c> (<c>src/libgit2/reflog.c</c> + <c>refdb_fs.c:reflog_parse</c>).
/// </summary>
/// <remarks>
/// <para>
/// The reflog file lives at <c>.git/logs/&lt;refname&gt;</c>. Format per line:
/// <c>&lt;old-oid&gt; &lt;new-oid&gt; &lt;name&gt; &lt;&lt;email&gt;&gt; &lt;epoch&gt; &lt;tz&gt;\t&lt;message&gt;</c>
/// </para>
/// <para>
/// Entries are stored in chronological order (oldest first). Index 0 = most recent
/// (inverse index, matching libgit2's <c>git_reflog_entry_byindex</c>).
/// </para>
/// <para>
/// <b>Write side:</b> <see cref="LibGit2CS.Refs.GitRefLog.Append(LibGit2CS.Core.GitOid, LibGit2CS.Core.GitSignature, string?)"/>/<see cref="Drop"/> mutate the
/// in-memory list. Use <see cref="GitTransaction.SetReflog(string, GitRefLog)"/> and
/// <see cref="GitTransaction.CommitAsync"/> to persist through the backend. The backend's
/// <c>ReflogAppend</c> writes a single entry directly to disk.
/// </para>
/// </remarks>
public sealed class GitRefLog : IEnumerable<GitRefLogEntry>
{
    private readonly List<GitRefLogEntry> _entries;
    private readonly RefNameKey _refName;

    internal GitRefLog(RefNameKey refName, GitHashAlgorithmKind algorithm, string content)
    {
        _refName = RefNameKey.From(refName.Bytes.ToArray());
        Algorithm = algorithm;
        _entries = Parse(content, algorithm);
    }

    /// <summary> Creates a reflog from raw file bytes. byte-parity surface — C's <c>reflog_parse</c> parses the raw file bytes (refdb_fs.c:2006-2058); the
    /// string ctor decodes lossily, corrupting non-UTF-8 messages. </summary>
    internal GitRefLog(RefNameKey refName, GitHashAlgorithmKind algorithm, ReadOnlyMemory<byte> content)
    {
        _refName = RefNameKey.From(refName.Bytes.ToArray());
        Algorithm = algorithm;
        _entries = ParseBytes(content.Span, algorithm);
    }

    /// <summary>
    /// Creates an empty reflog for the given ref name. Used by the write path
    /// to build a new reflog in memory before persisting. Matches
    /// <c>git_reflog_new</c> (reflog.c:60-75).
    /// </summary>
    public GitRefLog(string refName, GitHashAlgorithmKind algorithm)
    {
        _refName = refName;
        Algorithm = algorithm;
        _entries = [];
    }

    /// <summary>Creates an empty reflog with a raw reference name. Copies the input bytes.</summary>
    public GitRefLog(ReadOnlyMemory<byte> refName, GitHashAlgorithmKind algorithm)
    {
        _refName = RefNameKey.From(refName.ToArray());
        Algorithm = algorithm;
        _entries = [];
    }

    /// <summary>Raw name of the reference owning this reflog.</summary>
    public ReadOnlyMemory<byte> RefNameBytes => _refName.Bytes;
    /// <summary>UTF-8 display name with replacement.</summary>
    public string RefName => _refName.ToString();

    /// <summary>The hash algorithm of the OIDs in this reflog.</summary>
    public GitHashAlgorithmKind Algorithm { get; }

    /// <summary>Number of reflog entries.</summary>
    public int EntryCount => _entries.Count;

    /// <summary>
    /// Gets the entry at the given index. Index 0 = most recent (inverse index,
    /// matching <c>git_reflog_entry_byindex</c>). Negative indices count from the
    /// oldest (e.g. -1 = oldest).
    /// </summary>
    public GitRefLogEntry this[int index]
    {
        get
        {
            if (index < 0)
            {
                // Negative: count from oldest (Python-style). -1 = oldest = _entries[0].
                index = -index - 1;
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _entries.Count);
            }
            else
            {
                // Positive: 0 = most recent (inverse index).
                index = _entries.Count - 1 - index;
                ArgumentOutOfRangeException.ThrowIfNegative(index);
            }

            return _entries[index];
        }
    }

    /// <inheritdoc/>
    public IEnumerator<GitRefLogEntry> GetEnumerator() => EnumerateMostRecentFirst().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    // ── Write side ──────────────────────────────────────────────────────

    /// <summary>
    /// Appends a reflog entry. The <c>OldId</c> is filled from the previous
    /// most-recent entry if zero. Matches <c>git_reflog_append</c>
    /// (reflog.c:60-124). Does NOT persist to disk — queue the log with <see cref="GitTransaction.SetReflog(string, GitRefLog)"/>
    /// and call <see cref="GitTransaction.CommitAsync"/>, or use <see cref="LibGit2CS.Repository.GitRepository.Refs"/> reflog-append which
    /// writes directly. UTF-8 convenience — the byte-parity surface is
    /// <see cref="Append(GitOid, GitSignature, ReadOnlyMemory{byte}?)"/>.
    /// </summary>
    public void Append(GitOid newId, GitSignature committer, string? message)
        => Append(newId, committer, message is null ? null : Encoding.UTF8.GetBytes(message));

    /// <summary> Appends a reflog entry with byte-faithful message bytes. byte-primary surface — C's <c>git_reflog_append</c>
    /// (reflog.c:83-110) stores the raw <c>msg</c> bytes with every <c>'\n'</c> replaced by a space; non-UTF-8 message bytes round-trip byte-exact. </summary>
    public void Append(GitOid newId, GitSignature committer, ReadOnlyMemory<byte>? message)
    {
        byte[] msg = Array.Empty<byte>();
        if (message is { } m)
        {
            // Replace all newlines with spaces (matches reflog.c:100-105).
            msg = m.ToArray();
            for (int i = 0; i < msg.Length; i++)
            {
                if (msg[i] == (byte)'\n')
                {
                    msg[i] = (byte)' ';
                }
            }
        }

        // OldId = previous most-recent entry's NewId, or zero if none.
        GitOid oldId = default;
        if (_entries.Count > 0)
        {
            oldId = _entries[^1].NewId;
        }

        var entry = new GitRefLogEntry(committer, oldId, newId, Encoding.UTF8.GetString(msg), msg);
        _entries.Add(entry);
    }

    /// <summary>
    /// Drops the entry at the given inverse index (0 = most recent). If
    /// <paramref name="rewritePreviousEntry"/> is true, the preceding entry's
    /// <c>OldId</c> is updated to reflect the dropped entry's <c>NewId</c>.
    /// Matches <c>git_reflog_drop</c> (reflog.c:189-232).
    /// </summary>
    public void Drop(int index, bool rewritePreviousEntry = false)
    {
        int entryCount = _entries.Count;

        // Convert inverse index to file-order index.
        int fileIndex;
        if (index < 0)
        {
            fileIndex = -index - 1; // -1 = oldest = _entries[0]
        }
        else
        {
            fileIndex = entryCount - 1 - index;
        }

        // C (reflog.c:196-201): an out-of-range index is GIT_ENOTFOUND
        // "no reflog entry at index %zu", not a .NET range exception.
        if (fileIndex < 0 || fileIndex >= entryCount)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"no reflog entry at index {index}",
                GitErrorCategory.Reference);
        }

        _entries.RemoveAt(fileIndex);

        if (!rewritePreviousEntry)
        {
            return;
        }

        // No need to rewrite when removing the most recent entry (index 0).
        if (index == 0)
        {
            return;
        }

        // Have the latest entry just been dropped? (only entry)
        if (entryCount == 1)
        {
            return;
        }

        // If the oldest entry has just been removed (file-order index 0,
        // i.e. inverse index entryCount-1), clear OldId of the new oldest.
        // Matches the `if (idx == entrycount - 1)` branch in libgit2's
        // git_reflog_drop (C uses inverse index; this port converts to
        // file-order up-front, so the equivalent check is fileIndex == 0).
        if (fileIndex == 0)
        {
            if (_entries.Count > 0)
            {
                GitRefLogEntry oldest = _entries[0];
                _entries[0] = oldest with { OldId = default };
            }

            return;
        }

        // Otherwise, restore the chain across the gap left by the dropped
        // entry: the entry now at fileIndex (the newer neighbor) has its
        // OldId updated to the older neighbor's NewId. Matches
        // `git_oid_cpy(&entry->oid_old, &previous->oid_cur)` in
        // git_reflog_drop (C: entry=byindex(idx-1)=newer, previous=byindex(idx)=older).
        if (fileIndex < _entries.Count && fileIndex > 0)
        {
            GitRefLogEntry prev = _entries[fileIndex - 1];
            GitRefLogEntry entry = _entries[fileIndex];
            _entries[fileIndex] = entry with { OldId = prev.NewId };
        }
    }

    /// <summary>
    /// Serializes all entries (oldest-first) to the reflog file format.
    /// </summary>
    internal string Serialize()
    {
        const int InitialCapacity = 128 * 16; // 16 entries of ~128 chars each
        using var sb = new ValueStringBuilder(InitialCapacity);
        foreach (GitRefLogEntry entry in _entries)
        {
            sb.Append(SerializeEntry(entry));
        }

        return sb.ToString();
    }

    /// <summary> Serializes all entries (oldest-first) to the reflog file format as bytes. byte-parity surface — C writes the raw message bytes
    /// (refdb_fs.c:2174-2213). </summary>
    internal byte[] SerializeBytes()
    {
        using var buffer = new PooledByteBufferWriter(128 * 16);
        using var scratch = new PooledByteBufferWriter(256);
        foreach (GitRefLogEntry entry in _entries)
        {
            ReflogFormatter.WriteEntry(buffer, scratch, entry.OldId, entry.NewId, entry.Committer, entry.MessageBytes);
        }

        return buffer.WrittenMemory.ToArray();
    }

    /// <summary>
    /// Serializes a single reflog entry to the on-disk format. Matches
    /// <c>serialize_reflog_entry</c> (refdb_fs.c:2174-2213). Newlines
    /// anywhere in the line (message AND signature) are replaced with
    /// spaces.
    /// </summary>
    private static string SerializeEntry(in GitRefLogEntry entry)
    {
        const int InitialCapacity = 256;
        var sb = new ValueStringBuilder(InitialCapacity); // `using` is not added because we need to set Length property and ToString also implicitly disposes the builder
        sb.AppendSpanFormattable(entry.OldId, provider: CultureInfo.InvariantCulture);
        sb.Append(' ');
        sb.AppendSpanFormattable(entry.NewId, provider: CultureInfo.InvariantCulture);
        sb.Append(' ');

        sb.AppendSpanFormattable(entry.Committer, provider: CultureInfo.InvariantCulture);

        if (entry.Message.Length > 0)
        {
            sb.Append('\t');
            sb.Append(entry.Message);
        }

        // C (refdb_fs.c:2197-2202): the whole buffer is scanned for
        // newlines — a user.name/user.email containing a newline (legal in
        // config values) must be sanitized too.
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\n')
            {
                sb[i] = ' ';
            }
        }

        // Trim trailing whitespace (C's git_buf_rtrim).
        while (sb.Length > 0 && sb[^1] is ' ' or '\t' or '\n' or '\r' or '\v' or '\f')
        {
            sb.Length--;
        }

        sb.Append('\n');
        return sb.ToString();
    }

    // ── Read side ───────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates entries in most-recent-first order (index 0 = most recent).
    /// This matches the public indexer semantics.
    /// </summary>
    private IEnumerable<GitRefLogEntry> EnumerateMostRecentFirst()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            yield return _entries[i];
        }
    }

    /// <summary>
    /// Parses reflog content. Matches <c>reflog_parse</c> (<c>refdb_fs.c:2006-2058</c>).
    /// Entries are stored in file order (oldest first). Malformed lines are silently
    /// skipped (matching C's <c>goto next</c> on parse error).
    /// </summary>
    private static List<GitRefLogEntry> Parse(string content, GitHashAlgorithmKind algorithm)
    {
        var entries = new List<GitRefLogEntry>();
        int hexSize = GitOid.HexSizeFor(algorithm);

        int lineStart = 0;
        while (lineStart < content.Length)
        {
            int lineEnd = content.IndexOf('\n', lineStart, StringComparison.Ordinal);
            ReadOnlySpan<char> line = lineEnd < 0
                ? content.AsSpan(lineStart)
                : content.AsSpan(lineStart, lineEnd - lineStart);

            // C (refdb_fs.c:2035-2045) removes only the final '\n' — a
            // trailing '\r' (CRLF reflog) stays in the entry MESSAGE.
            if (line.Length > 0)
            {
                if (TryParseLine(line, hexSize, algorithm, out GitRefLogEntry entry))
                {
                    entries.Add(entry);
                }
            }

            lineStart = lineEnd < 0 ? content.Length : lineEnd + 1;
        }

        return entries;
    }

    /// <summary> Parses reflog content from raw bytes. byte-parity surface — C's <c>reflog_parse</c> parses the raw file bytes (refdb_fs.c:2006-2058); the
    /// string overload decodes lossily, corrupting non-UTF-8 messages. Per-entry <see cref="GitRefLogEntry.MessageBytes"/> is a slice of the input buffer (no
    /// copy). </summary>
    private static List<GitRefLogEntry> ParseBytes(ReadOnlySpan<byte> content, GitHashAlgorithmKind algorithm)
    {
        var entries = new List<GitRefLogEntry>();
        int hexSize = GitOid.HexSizeFor(algorithm);

        int lineStart = 0;
        while (lineStart < content.Length)
        {
            int lineEnd = content[lineStart..].IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = lineEnd < 0
                ? content[lineStart..]
                : content.Slice(lineStart, lineEnd);

            // C (refdb_fs.c:2035-2045) removes only the final '\n' — a
            // trailing '\r' (CRLF reflog) stays in the entry MESSAGE.
            if (line.Length > 0)
            {
                if (TryParseLineBytes(line, hexSize, algorithm, out GitRefLogEntry entry))
                {
                    entries.Add(entry);
                }
            }

            lineStart = lineEnd < 0 ? content.Length : lineStart + lineEnd + 1;
        }

        return entries;
    }

    /// <summary>
    /// Parses a single reflog line from bytes:
    /// <c>&lt;old-oid&gt; &lt;new-oid&gt; &lt;sig&gt;\t&lt;msg&gt;</c>
    /// </summary>
    private static bool TryParseLineBytes(ReadOnlySpan<byte> line, int hexSize, GitHashAlgorithmKind algorithm, out GitRefLogEntry entry)
    {
        entry = default;

        // Parse old OID (first hexSize chars).
        if (line.Length < hexSize)
        {
            return false;
        }

        if (!GitOid.TryParse(line[..hexSize], algorithm, out GitOid oldId))
        {
            return false;
        }

        int pos = hexSize;

        // Skip space.
        if (pos >= line.Length || line[pos] != (byte)' ')
        {
            return false;
        }

        pos++;

        // Parse new OID.
        if (pos + hexSize > line.Length)
        {
            return false;
        }

        if (!GitOid.TryParse(line.Slice(pos, hexSize), algorithm, out GitOid newId))
        {
            return false;
        }

        pos += hexSize;

        // Skip space.
        if (pos >= line.Length || line[pos] != (byte)' ')
        {
            return false;
        }

        pos++;

        // Find tab (message separator) or end of line.
        int tabIdx = line[pos..].IndexOf((byte)'\t');
        int sigEnd = tabIdx < 0 ? line.Length : pos + tabIdx;

        // Parse signature: "Name <email> epoch tz" over the raw bytes — C's reflog_parse hands the raw line bytes to git_signature__parse
        // (refdb_fs.c:2035-2045), so non-UTF-8 name/email bytes are preserved verbatim.
        ReadOnlySpan<byte> sigSpan = line[pos..sigEnd];
        if (!GitSignature.TryParse(sigSpan, out GitSignature? sig, out _))
        {
            return false;
        }

        // Parse message (after tab, if present). The message bytes are a slice of the input buffer (C stores the raw bytes, refdb_fs.c:2035-2045); the string
        // is the UTF-8 display decode.
        ReadOnlyMemory<byte> messageBytes = ReadOnlyMemory<byte>.Empty;
        string message = string.Empty;
        if (tabIdx >= 0)
        {
            int msgStart = sigEnd + 1;
            messageBytes = line[msgStart..].ToArray();
            message = Encoding.UTF8.GetString(line[msgStart..]);
        }

        entry = new GitRefLogEntry(sig, oldId, newId, message, messageBytes);
        return true;
    }

    /// <summary>
    /// Parses a single reflog line:
    /// <c>&lt;old-oid&gt; &lt;new-oid&gt; &lt;sig&gt;\t&lt;msg&gt;</c>
    /// </summary>
    private static bool TryParseLine(ReadOnlySpan<char> line, int hexSize, GitHashAlgorithmKind algorithm, out GitRefLogEntry entry)
    {
        entry = default;

        // Parse old OID (first hexSize chars).
        if (line.Length < hexSize)
        {
            return false;
        }

        if (!GitOid.TryParse(line[..hexSize], algorithm, out GitOid oldId))
        {
            return false;
        }

        int pos = hexSize;

        // Skip space.
        if (pos >= line.Length || line[pos] != ' ')
        {
            return false;
        }

        pos++;

        // Parse new OID.
        if (pos + hexSize > line.Length)
        {
            return false;
        }

        if (!GitOid.TryParse(line.Slice(pos, hexSize), algorithm, out GitOid newId))
        {
            return false;
        }

        pos += hexSize;

        // Skip space.
        if (pos >= line.Length || line[pos] != ' ')
        {
            return false;
        }

        pos++;

        // Find tab (message separator) or end of line.
        int tabIdx = line[pos..].IndexOf('\t');
        int sigEnd = tabIdx < 0 ? line.Length : pos + tabIdx;
        ReadOnlySpan<char> sigSpan = line[pos..sigEnd];

        // Parse signature: "Name <email> epoch tz"
        if (!GitSignature.TryParse(sigSpan, out GitSignature? sig, out _))
        {
            return false;
        }

        // Parse message (after tab, if present).
        string message = string.Empty;
        if (tabIdx >= 0)
        {
            int msgStart = sigEnd + 1;
            message = line[msgStart..].ToString();
        }

        entry = new GitRefLogEntry(sig, oldId, newId, message);
        return true;
    }
}
