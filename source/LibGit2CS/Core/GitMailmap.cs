// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Text;

using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

using LibGit2CS.Utils;

namespace LibGit2CS.Core;

/// <summary> Maps alternate author/committer names and emails to canonical real names and emails. Managed port of libgit2's <c>src/libgit2/mailmap.c</c>.
/// </summary> <remarks> <para> A mailmap file has entries of the form: <c>Real Name &lt;real@email&gt; Replace Name &lt;replace@email&gt;</c> or just <c>Real
/// Name &lt;replace@email&gt;</c> (nameless replacement). </para> <para> <b>All reads, no write side.</b> </para>
/// <para> entries are byte-primary (<see cref="MailmapEntry"/> fields are raw bytes) — libgit2 parses the raw buffer bytes (mailmap.c:98-149) and compares with
/// <c>git__strcmp</c> (mailmap.c:42-59), so non-UTF-8 name/email bytes round-trip byte-exact through parse and lookup. The string surface (<see
/// cref="Resolve(string, string)"/>, <see cref="AddEntry"/>) is the UTF-8 convenience tier. </para> </remarks>
public sealed class GitMailmap : IDisposable
{
    private const string MmFile = ".mailmap";
    private const string MmFileConfig = "mailmap.file";
    private const string MmBlobConfig = "mailmap.blob";
    private const string MmBlobDefault = "HEAD:" + MmFile;

    private readonly List<MailmapEntry> _entries = [];

    /// <summary>
    /// Creates an empty mailmap. Matches <c>git_mailmap_new</c>.
    /// </summary>
    public GitMailmap()
    {
    }

    /// <summary>
    /// Creates a mailmap from a text buffer. Matches <c>git_mailmap_from_buffer</c>.
    /// UTF-8 convenience tier — the byte-parity surface is
    /// <see cref="FromBuffer(ReadOnlySpan{byte})"/>.
    /// </summary>
    public static GitMailmap FromBuffer(string buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        byte[] bufferBytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(buffer));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(buffer, bufferBytes);
            return FromBuffer(bufferBytes.AsSpan(0, bytesWritten));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferBytes);
        }
    }

    /// <summary>
    /// Creates a mailmap from a byte buffer. Matches <c>git_mailmap_from_buffer</c>
    /// (the byte-oriented variant).
    /// </summary>
    public static GitMailmap FromBuffer(ReadOnlySpan<byte> buffer)
    {
        var mm = new GitMailmap();
        mm.AddBuffer(buffer);
        return mm;
    }

    /// <summary>
    /// Creates a mailmap from a repository, loading mailmap files based on the
    /// repository's configuration. Matches <c>git_mailmap_from_repository</c>.
    /// </summary>
    /// <remarks>
    /// Loads in order (later sources override earlier entries):
    /// <list type="number">
    /// <item><c>.mailmap</c> in the repository workdir root (non-bare only).</item>
    /// <item>The blob described by <c>mailmap.blob</c> config (default <c>HEAD:.mailmap</c> in bare repos).</item>
    /// <item>The file described by <c>mailmap.file</c> config.</item>
    /// </list>
    /// Errors from individual sources are silently ignored (files may not exist).
    /// </remarks>
    internal static async Task<GitMailmap> FromRepositoryAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var mm = new GitMailmap();
        await mm.LoadFromRepositoryAsync(repo, cancellationToken).ConfigureAwait(false);
        return mm;
    }

    /// <summary>
    /// Adds a single entry to this mailmap. If an entry with the same
    /// <paramref name="replaceEmail"/> + <paramref name="replaceName"/> already
    /// exists, it is replaced. Matches <c>git_mailmap_add_entry</c>. UTF-8
    /// convenience tier — the strings are UTF-8-encoded into the byte-domain
    /// entry store.
    /// </summary>
    public void AddEntry(string? realName, string? realEmail, string? replaceName, string replaceEmail)
    {
        ArgumentNullException.ThrowIfNull(replaceEmail);
        AddEntryInternal(
            realName is null ? null : Encoding.UTF8.GetBytes(realName),
            realEmail is null ? null : Encoding.UTF8.GetBytes(realEmail),
            replaceName is null ? null : Encoding.UTF8.GetBytes(replaceName),
            Encoding.UTF8.GetBytes(replaceEmail));
    }

    /// <summary>
    /// Resolves a name/email pair to the canonical real name and email.
    /// Matches <c>git_mailmap_resolve</c>. If no entry matches, the original
    /// name/email are returned unchanged. UTF-8 convenience tier — the
    /// byte-parity surface is <see cref="ResolveBytes"/>.
    /// </summary>
    public (string Name, string Email) Resolve(string name, string email)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);

        (ReadOnlyMemory<byte> nameBytes, ReadOnlyMemory<byte> emailBytes) = ResolveBytes(
            Encoding.UTF8.GetBytes(name), Encoding.UTF8.GetBytes(email));
        return (Encoding.UTF8.GetString(nameBytes.Span), Encoding.UTF8.GetString(emailBytes.Span));
    }

    /// <summary> Resolves a name/email pair to the canonical real name and email over raw bytes. Byte-parity surface — C's <c>git_mailmap_resolve</c>
    /// (mailmap.c:457-475) compares the raw <c>char *</c> bytes with <c>git__strcmp</c> and returns the original pointers unchanged on no-match. </summary>
    internal (ReadOnlyMemory<byte> Name, ReadOnlyMemory<byte> Email) ResolveBytes(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> email)
    {
        MailmapEntry? entry = LookupBytes(name, email);
        ReadOnlyMemory<byte> realName = entry?.RealNameBytes ?? name;
        ReadOnlyMemory<byte> realEmail = entry?.RealEmailBytes ?? email;
        return (realName, realEmail);
    }

    /// <summary>
    /// Resolves a commit's author signature through this mailmap.
    /// Matches <c>git_commit_author_with_mailmap</c>.
    /// </summary>
    public GitSignature ApplyAuthor(Commit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return ResolveSignature(commit.Author);
    }

    /// <summary>
    /// Resolves a commit's committer signature through this mailmap.
    /// Matches <c>git_commit_committer_with_mailmap</c>.
    /// </summary>
    public GitSignature ApplyCommitter(Commit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return ResolveSignature(commit.Committer);
    }

    /// <summary> Resolves a signature through this mailmap. Returns a new signature with the canonical name/email if a mailmap entry exists, otherwise returns
    /// the original signature unchanged. Matches <c>git_mailmap_resolve_signature</c>. The lookup runs over the raw name/email bytes (<see
    /// cref="GitSignature.NameBytes"/>/<see cref="GitSignature.EmailBytes"/>) — C's <c>git_mailmap_resolve_signature</c> (mailmap.c:479-498) passes the raw
    /// <c>char *</c> bytes, so a non-UTF-8 signature name matches a non-UTF-8 mailmap entry byte-exact (a U+FFFD re-encode would mismatch). </summary>
    public GitSignature ResolveSignature(GitSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        (ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> email) = ResolveBytes(signature.NameBytes, signature.EmailBytes);
        if (name.Span.SequenceEqual(signature.NameBytes.Span) && email.Span.SequenceEqual(signature.EmailBytes.Span))
        {
            return signature;
        }

        return GitSignature.Create(name, email, signature.When);
    }

    /// <summary>
    /// Looks up a mailmap entry by name and email. Returns null if not found.
    /// Matches <c>git_mailmap_entry_lookup</c>. UTF-8 convenience tier — the
    /// byte-parity surface is <see cref="LookupBytes"/>.
    /// </summary>
    /// <remarks>
    /// The lookup first searches for a name-specific entry (both replace_email
    /// and replace_name match), falling back to a nameless entry (replace_email
    /// only). Entries with replace_name are sorted after nameless entries for
    /// the same replace_email.
    /// </remarks>
    internal MailmapEntry? Lookup(string? name, string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        byte[] buffer = ArrayPool<byte>.Shared.Rent((name is null ? 0 : Encoding.UTF8.GetByteCount(name)) + Encoding.UTF8.GetByteCount(email));
        try
        {
            ReadOnlyMemory<byte>? nameBytes = null;
            ReadOnlyMemory<byte> emailBytes;
            int nameBytesWritten = 0;
            if (name is not null)
            {
                nameBytesWritten = Encoding.UTF8.GetBytes(name, buffer);
                nameBytes = buffer.AsMemory(0, nameBytesWritten);
            }
            int emailBytesWritten = Encoding.UTF8.GetBytes(email, buffer.AsSpan(nameBytesWritten));
            emailBytes = buffer.AsMemory(nameBytesWritten, emailBytesWritten);

            return LookupBytes(nameBytes, emailBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary> Looks up a mailmap entry by raw name/email bytes. Byte-parity surface — C's <c>git_mailmap_entry_lookup</c> (mailmap.c:411-455) compares the
    /// raw <c>char *</c> bytes with <c>git__strcmp</c> (case-sensitive, ordinal). </summary>
    internal MailmapEntry? LookupBytes(ReadOnlyMemory<byte>? name, ReadOnlyMemory<byte> email)
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        // Binary search for the nameless fallback entry (replace_email only).
        var needle = new MailmapEntry { ReplaceEmailBytes = email };
        int idx = _entries.BinarySearch(needle, MailmapEntryComparer.Instance);
        int fallback = -1;

        if (idx >= 0)
        {
            // Found a nameless entry — this is the fallback.
            fallback = idx;
            idx++; // Start linear search after it for a named match.
        }
        else
        {
            // Not found — BinarySearch returned the complement of the insertion point.
            idx = ~idx;
        }

        // Linear search for an exact match (replace_email + replace_name).
        for (; idx < _entries.Count; idx++)
        {
            MailmapEntry entry = _entries[idx];
            if (!entry.ReplaceEmailBytes.Span.SequenceEqual(email.Span))
            {
                break; // different email, done
            }

            if (entry.ReplaceNameBytes is not null && (name is null || entry.ReplaceNameBytes.GetValueOrDefault().Span.SequenceEqual(name.GetValueOrDefault().Span)))
            {
                return entry;
            }
        }

        if (fallback < 0)
        {
            return null;
        }

        return _entries[fallback];
    }

    /// <summary> Parses a mailmap buffer and adds all valid entries. Invalid lines are silently skipped. Matches <c>mailmap_add_buffer</c> (mailmap.c:230-270).
    /// the buffer is parsed byte-wise — C's <c>git_parse_ctx</c> operates on the raw bytes, so non-UTF-8 name/email bytes are preserved verbatim. </summary>
    internal void AddBuffer(ReadOnlySpan<byte> buffer)
    {
        // Reject NUL bytes (matches mailmap.c:242-243).
        if (buffer.Contains((byte)0))
        {
            throw new ArgumentException("Mailmap buffer contains NUL bytes.", nameof(buffer));
        }

        ReadOnlySpan<byte> lines = buffer;

        while (!lines.IsEmpty)
        {
            // Extract one line.
            int nlIdx = lines.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line;
            if (nlIdx < 0)
            {
                line = lines;
                lines = ReadOnlySpan<byte>.Empty;
            }
            else
            {
                line = lines[..nlIdx];
                lines = lines[(nlIdx + 1)..];
            }

            if (TryParseEntryBytes(line,
                out ReadOnlyMemory<byte>? realName, out ReadOnlyMemory<byte>? realEmail,
                out ReadOnlyMemory<byte>? replaceName, out ReadOnlyMemory<byte> replaceEmail))
            {
                AddEntryInternal(realName, realEmail, replaceName, replaceEmail);
            }
        }
    }

    /// <summary>
    /// Parses a single mailmap entry from a line. Returns false if the line is
    /// blank, a comment, or malformed. Matches <c>parse_mailmap_entry</c>
    /// (mailmap.c:98-149). Byte-domain port — C's <c>git_parse_ctx</c> and
    /// <c>advance_until</c> (mailmap.c:77-90) operate on the raw bytes.
    /// </summary>
    /// <remarks>
    /// Format: <c>Real Name &lt;real@email&gt; Replace Name &lt;replace@email&gt;</c>
    /// or <c>Real Name &lt;replace@email&gt;</c> (the single-email form, where the
    /// email is what gets replaced and only the name is canonicalized).
    /// </remarks>
    private static bool TryParseEntryBytes(
        ReadOnlySpan<byte> line,
        out ReadOnlyMemory<byte>? realName, out ReadOnlyMemory<byte>? realEmail,
        out ReadOnlyMemory<byte>? replaceName, out ReadOnlyMemory<byte> replaceEmail)
    {
        realName = null;
        realEmail = null;
        replaceName = null;
        replaceEmail = default;

        // Skip leading whitespace (C's git_parse_advance_ws, parse.c:65-71 —
        // the ASCII git__isspace set only).
        int pos = 0;
        while (pos < line.Length && AsciiText.IsAsciiSpace((char)line[pos]))
        {
            pos++;
        }

        if (pos >= line.Length || line[pos] == (byte)'#')
        {
            return false; // blank or comment
        }

        // Parse the real name (everything up to '<').
        int nameStart = pos;
        while (pos < line.Length && line[pos] != (byte)'<' && line[pos] != (byte)'#')
        {
            pos++;
        }

        if (pos >= line.Length || line[pos] == (byte)'#')
        {
            return false; // no '<' found
        }

        // C (mailmap.c:120): the name is rtrimmed with git_str_rtrim (ASCII only) — TrimEnd also removed non-ASCII whitespace.
        realName = AsciiText.Rtrim(line[nameStart..pos]).ToArray();

        // Skip the '<'.
        pos++;

        // Parse the first email (up to '>'). C's advance_until (mailmap.c:81-85)
        // also stops at '#', so a '#' before '>' rejects the whole line.
        int emailStart = pos;
        while (pos < line.Length && line[pos] != (byte)'>' && line[pos] != (byte)'#')
        {
            pos++;
        }

        if (pos >= line.Length || line[pos] == (byte)'#')
        {
            return false; // no '>' found (or comment before it)
        }

        int emailEnd = pos;
        pos++; // skip '>'

        // Skip whitespace after the first email (ASCII set).
        while (pos < line.Length && AsciiText.IsAsciiSpace((char)line[pos]))
        {
            pos++;
        }

        // If we're at end of line or a comment, this is the single-email form:
        // the first email is the replace_email.
        if (pos >= line.Length || line[pos] == (byte)'#')
        {
            replaceEmail = line[emailStart..emailEnd].ToArray();
            return true;
        }

        // Two-email form: first email is real_email, parse replace_name + replace_email.
        realEmail = line[emailStart..emailEnd].ToArray();

        // Parse the replace name (up to '<').
        int replaceNameStart = pos;
        while (pos < line.Length && line[pos] != (byte)'<' && line[pos] != (byte)'#')
        {
            pos++;
        }

        if (pos >= line.Length || line[pos] == (byte)'#')
        {
            return false;
        }

        // C (mailmap.c:137): git_str_rtrim (ASCII only).
        replaceName = AsciiText.Rtrim(line[replaceNameStart..pos]).ToArray();
        pos++; // skip '<'

        // Parse the replace email (up to '>'). Same '#' stop as the first
        // email scan (C's advance_until, mailmap.c:81-85).
        int replaceEmailStart = pos;
        while (pos < line.Length && line[pos] != (byte)'>' && line[pos] != (byte)'#')
        {
            pos++;
        }

        if (pos >= line.Length || line[pos] == (byte)'#')
        {
            return false;
        }

        replaceEmail = line[replaceEmailStart..pos].ToArray();
        pos++; // skip '>'

        // Must be at end of line (allowing trailing whitespace or comment).
        while (pos < line.Length && AsciiText.IsAsciiSpace((char)line[pos]))
        {
            pos++;
        }

        if (pos < line.Length && line[pos] != (byte)'#')
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Adds an entry from byte segments. Matches
    /// <c>mailmap_add_entry_unterminated</c> (mailmap.c:180-216) — empty
    /// segments are stored as NULL (C's <c>git__strndup</c> is skipped for
    /// zero-length segments, leaving the field NULL).
    /// </summary>
    private void AddEntryInternal(
        ReadOnlyMemory<byte>? realName,
        ReadOnlyMemory<byte>? realEmail,
        ReadOnlyMemory<byte>? replaceName,
        ReadOnlyMemory<byte> replaceEmail)
    {
        if (replaceEmail.Length == 0)
        {
            throw new ArgumentException("replace_email must not be empty.", nameof(replaceEmail));
        }

        var entry = new MailmapEntry
        {
            RealNameBytes = realName is { Length: > 0 } ? realName : null,
            RealEmailBytes = realEmail is { Length: > 0 } ? realEmail : null,
            ReplaceNameBytes = replaceName is { Length: > 0 } ? replaceName : null,
            ReplaceEmailBytes = replaceEmail,
        };

        // Insert sorted with replace-on-duplicate semantics (mailmap.c:209).
        int idx = _entries.BinarySearch(entry, MailmapEntryComparer.Instance);
        if (idx >= 0)
        {
            // Replace existing entry (mailmap_entry_replace returns GIT_EEXISTS,
            // which the caller treats as OK).
            _entries[idx] = entry;
        }
        else
        {
            _entries.Insert(~idx, entry);
        }
    }

    /// <summary>
    /// Loads mailmap files from the repository. Matches
    /// <c>mailmap_add_from_repository</c> (mailmap.c:354-395).
    /// </summary>
    private async Task LoadFromRepositoryAsync(GitRepository repo, CancellationToken cancellationToken)
    {
        string? rev = null;
        string? path = null;

        // Bare repos default to HEAD:.mailmap.
        if (repo.IsBare)
        {
            rev = MmBlobDefault;
        }

        // Read mailmap.blob and mailmap.file from config.
        try
        {
            // No byte-domain gap: the value feeds the string revspec tier (GitRevParser.ParseSingleAsync); GetBytesAsync+decode would be identical to
            // GetStringAsync.
            string? revValue = await repo.Config.GetStringAsync(MmBlobConfig, cancellationToken).ConfigureAwait(false);
            if (revValue is not null)
            {
                rev = revValue;
            }

            GitPath? pathValue = await repo.Config.GetPathAsync(MmFileConfig, cancellationToken).ConfigureAwait(false);
            if (pathValue is not null)
            {
                // Filesystem boundary: the raw config path bytes convert to a filesystem string here.
                path = pathValue.Value.ToFileSystemString();
            }
        }
        catch
        {
            // Config read errors are silently ignored (mailmap.c:367-372).
        }

        // Load in order, ignoring errors (files may not exist):
        // 1. .mailmap in workdir root (non-bare only)
        if (!repo.IsBare)
        {
            await TryAddFileOnDiskAsync(MmFile, repo, cancellationToken).ConfigureAwait(false);
        }

        // 2. Blob from mailmap.blob config (or HEAD:.mailmap for bare)
        if (rev is not null)
        {
            await TryAddBlobAsync(repo, rev, cancellationToken).ConfigureAwait(false);
        }

        // 3. File from mailmap.file config
        if (path is not null)
        {
            await TryAddFileOnDiskAsync(path, repo, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryAddFileOnDiskAsync(GitPath path, GitRepository repo, CancellationToken cancellationToken)
    {
        try
        {
            string fullpath = path.ToFileSystemString();
            string? baseDir = repo.Workdir;
            if (!Path.IsPathRooted(fullpath) && baseDir is not null)
            {
                // FS boundary: single transcode point.
                fullpath = Path.Join(baseDir, fullpath);
            }

            if (!File.Exists(fullpath))
            {
                return;
            }

            byte[] bytes = await File.ReadAllBytesAsync(fullpath, cancellationToken).ConfigureAwait(false);
            AddBuffer(bytes);
        }
        catch
        {
            // Silently ignore (mailmap.c:385 comment: "we ignore errors").
        }
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. </summary>
    private Task TryAddFileOnDiskAsync(string path, GitRepository repo, CancellationToken cancellationToken)
        => TryAddFileOnDiskAsync(GitPath.FromUtf8String(path), repo, cancellationToken);

    private async Task TryAddBlobAsync(GitRepository repo, string revspec, CancellationToken cancellationToken)
    {
        try
        {
            GitObject? obj = await GitRevParser.ParseSingleAsync(repo, revspec, cancellationToken).ConfigureAwait(false);
            if (obj is null)
            {
                return;
            }

            GitBlob? blob = await obj.PeelAsync<GitBlob>(cancellationToken).ConfigureAwait(false);
            if (blob is null)
            {
                return;
            }

            AddBuffer(blob.Content.Span);
        }
        catch
        {
            // Silently ignore (mailmap.c:385 comment).
        }
    }

    /// <summary>
    /// Disposes the mailmap. No unmanaged resources — just clears entries.
    /// </summary>
    public void Dispose()
    {
        _entries.Clear();
    }
}
