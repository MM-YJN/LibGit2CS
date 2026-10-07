// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Refs;

/// <summary>
/// Filesystem reference backend. Reads and writes <c>.git/HEAD</c>, <c>refs/*</c>
/// (loose), <c>packed-refs</c>, and reflogs. Managed port of libgit2's
/// <c>refdb_fs.c</c> (read + write side, ~2,575 LOC).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-worktree vs common refs</b>: <c>HEAD</c>, <c>ORIG_HEAD</c>,
/// <c>FETCH_HEAD</c>, <c>MERGE_HEAD</c>, <c>refs/bisect/*</c>, <c>refs/worktree/*</c>,
/// <c>refs/rewritten/*</c> are per-worktree (read from <c>gitpath</c>). All other
/// <c>refs/*</c> are shared (read from <c>commonpath</c>). For non-worktree repos,
/// <c>gitpath == commonpath</c>. Matches <c>is_per_worktree_ref</c>
/// (<c>refdb_fs.c:409-415</c>).
/// </para>
/// <para>
/// <b>Loose vs packed priority</b>: loose refs shadow packed refs. When both exist
/// for the same name, the loose value wins. Matches <c>refdb_fs_read</c>.
/// </para>
/// <para>
/// <b>Packed-refs</b>: parsed into a <see cref="Dictionary{TKey, TValue}"/> for O(1)
/// lookup (simplification from C's mmap binary search + sortedcache fallback).
/// Reloaded when mtime+size stamp changes. The <c># pack-refs with:</c> header
/// controls peel availability (<c>peeled</c>/<c>fully-peeled</c> traits).
/// </para>
/// <para>
/// <b>Write atomicity</b>: loose ref writes use a <c>&lt;refname&gt;.lock</c> temp
/// file + atomic rename; packed-refs writes use a full atomic rewrite. Reflog
/// entries are appended to <c>.git/logs/&lt;refname&gt;</c>.
/// </para>
/// <para>
/// <b>Async IO</b>: all content reads/writes go through
/// <see cref="AsyncFileIO"/>. Stat-only probes (<see cref="File.Exists"/>,
/// <see cref="Directory.Exists"/>, <see cref="FileInfo"/> metadata) and
/// metadata ops (<see cref="System.IO.File.Move(string, string)"/>, <see cref="File.Delete"/>,
/// <see cref="System.IO.Directory.CreateDirectory(string)"/>) stay sync per the AGENTS.md stat/metadata exemption.
/// <see cref="Lock"/> keeps its <c>FileStream</c> O_EXCL probe sync (the
/// open is a metadata operation; matches <c>FileConfigBackend.LockAsync</c>
/// precedent). Writes go through <see cref="AsyncFileIO.WriteAtomicTextAsync"/>.
/// </para>
/// </remarks>
internal sealed class FileRefBackend : IRefBackend
{
    private const string PackedRefsFile = "packed-refs";
    private const string PackedRefsHeader = "# pack-refs with:";
    private const string RefsDir = "refs";
    private const string LogsDir = "logs";

    private readonly string _gitpath;
    private readonly string _commonpath;
    private GitHashAlgorithmKind _oidType;
    private int _hexSize;
    private GitRepository? _repo;

    // Packed-refs cache. byte-keyed on the raw refname bytes (C's sortedcache stores inline char name[] bytes compared with strcmp, refdb_fs.c:46-51, 107-110)
    // — non-UTF-8 refnames round-trip byte-exact.
    private Dictionary<RefNameKey, PackedRef> _packedRefs = [];
    private PeelingMode _peelingMode = PeelingMode.None;
    private bool _packedSorted;
    private long _packedStampSize = -1;
    private DateTime _packedStampTime = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// Creates a filesystem ref backend.
    /// </summary>
    /// <param name="gitpath">Per-worktree gitdir (e.g. <c>.git/</c> or <c>.git/worktrees/foo/</c>).</param>
    /// <param name="commonpath">Shared commondir (same as <paramref name="gitpath"/> for non-worktree repos).</param>
    /// <param name="oidType">Hash algorithm for OID parsing.</param>
    internal FileRefBackend(string gitpath, string commonpath, GitHashAlgorithmKind oidType)
    {
        _gitpath = gitpath;
        _commonpath = commonpath;
        _oidType = oidType;
        _hexSize = GitOid.HexSizeFor(oidType);
    }

    /// <summary> Updates the OID type used to parse ref files. Called by <c>git_repository__set_objectformat</c> (clone.c:444-450, 522-526) so the ref database
    /// parses the adopted object format. </summary>
    public void SetOidType(GitHashAlgorithmKind oidType)
    {
        _oidType = oidType;
        _hexSize = GitOid.HexSizeFor(oidType);
    }

    /// <summary>
    /// Sets the owning repository. Used to query <c>core.logallrefupdates</c>
    /// and the default reflog signature. Called once by <see cref="GitReferences.SetOwner"/>.
    /// </summary>
    internal void SetOwner(GitRepository repo) => _repo = repo;

    /// <inheritdoc/>
    public async Task<GitReference?> LookupAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // precompose the incoming ref name when core.precomposeunicode is set, matching reference_normalize_for_repo (refs.c:201-218). An NFD-typed lookup must
        // match the NFC file that libgit2 would have written under precompose.
        RefNameKey lookupName = await PrecomposeRefNameAsync(refName, cancellationToken).ConfigureAwait(false);

        // Try loose first (loose shadows packed).
        bool hasFileSystemName = lookupName.TryGetFileSystemString(out _);
        GitReference? loose = hasFileSystemName
            ? await LooseLookupAsync(lookupName, cancellationToken).ConfigureAwait(false) : null;
        if (loose is not null)
        {
            return loose with { NameKey = refName };
        }

        // Try packed.
        await EnsurePackedLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_packedRefs.TryGetValue(lookupName, out PackedRef packed))
        {
            return new GitDirectReference
            {
                NameKey = refName,
                Target = packed.Oid,
                Peel = packed.Peel,
            };
        }

        if (!hasFileSystemName)
        {
            // A packed miss would require a loose-file probe that the managed path API cannot express.
            _ = lookupName.ToFileSystemString();
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await LookupAsync(refName, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<GitReference> EnumerateAsync(RefNameKey? glob, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EnumerateCoreAsync(glob, includeRefs: true, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<RefNameKey> EnumerateNamesAsync(RefNameKey? glob, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EnumerateNamesCoreAsync(glob, cancellationToken);
    }

    private static RefNameKey GetName(GitReference r) => r.NameKey;

    /// <summary>
    /// Core enumeration: walks <c>commonpath/refs/</c> for loose refs, merges with
    /// packed-refs dict (loose shadows packed), optionally filters by glob.
    /// </summary>
    private async IAsyncEnumerable<GitReference> EnumerateCoreAsync(RefNameKey? glob, bool includeRefs, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsurePackedLoadedAsync(cancellationToken).ConfigureAwait(false);

        var seen = new HashSet<RefNameKey>();
        bool isWorktree = _gitpath != _commonpath;

        // C (iter_load_loose_paths, refdb_fs.c:937-950): the loose walk covers commonpath/refs (shared) AND, for a linked worktree, gitpath/refs (per-worktree)
        // — with is_per_worktree_ref keeping only the per-worktree names on the gitpath pass and dropping them on the commonpath pass.
        await foreach (RefNameKey refName in EnumerateLooseRefsAsync(_commonpath, isWorktree ? static n => !IsPerWorktreeRef(n) : null, glob, cancellationToken).ConfigureAwait(false))
        {
            GitReference? loose = await LoadEnumeratedLooseRefAsync(refName, seen, cancellationToken).ConfigureAwait(false);
            if (loose is not null)
            {
                yield return includeRefs ? loose : new GitDirectReference { NameKey = refName, Target = default };
            }
        }

        if (isWorktree)
        {
            await foreach (RefNameKey refName in EnumerateLooseRefsAsync(_gitpath, static n => IsPerWorktreeRef(n), glob, cancellationToken).ConfigureAwait(false))
            {
                GitReference? loose = await LoadEnumeratedLooseRefAsync(refName, seen, cancellationToken).ConfigureAwait(false);
                if (loose is not null)
                {
                    yield return includeRefs ? loose : new GitDirectReference { NameKey = refName, Target = default };
                }
            }
        }

        // Add packed-only refs. C yields packed refs from the sortedcache (sorted by name, packref_cmp = strcmp); the in-memory dictionary is unordered, so
        // sort here (refdb_fs.c:1015-1020). byte-ordinal sort over the raw name bytes (C's strcmp) and a byte-keyed dedup set (loose names UTF-8-encoded once —
        // no lossy decode in the compare domain).
        foreach (RefNameKey key in _packedRefs.Keys.OrderBy(static k => k.Bytes, ByteOrdinalComparer.Instance))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackedRef packed = _packedRefs[key];
            if (seen.Contains(key))
            {
                continue;
            }

            RefNameKey name = key;
            if (!MatchesGlob(name, glob))
            {
                continue;
            }

            if (includeRefs)
            {
                yield return new GitDirectReference
                {
                    NameKey = name,
                    Target = packed.Oid,
                    Peel = packed.Peel,
                };
            }
            else
            {
                yield return new GitDirectReference { NameKey = name, Target = default };
            }
        }
    }

    private async IAsyncEnumerable<RefNameKey> EnumerateNamesCoreAsync(RefNameKey? glob, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (GitReference r in EnumerateCoreAsync(glob, includeRefs: false, cancellationToken).ConfigureAwait(false))
        {
            yield return r.NameKey;
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> HasLogAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult(File.Exists(GetReflogPath(refName)));
    }

    /// <inheritdoc/>
    public async Task<GitRefLog?> ReadLogAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string path = GetReflogPath(refName);
        if (!File.Exists(path))
        {
            // C (refdb_fs.c:2149-2155): git_reflog_read creates the missing
            // log file (create_new_reflog_file) and returns an empty reflog.
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(path, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
            return new GitRefLog(refName, _oidType, string.Empty);
        }

        // byte-domain read (C's reflog_parse parses the raw file bytes, refdb_fs.c:2006-2058) — non-UTF-8 messages round-trip byte-exact.
        byte[] content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new GitRefLog(refName, _oidType, content);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _packedRefs.Clear();
        return ValueTask.CompletedTask;
    }

    // ── Loose ref reading ──────────────────────────────────────────────

    /// <summary> Applies <c>core.precomposeunicode</c> NFD-&gt;NFC transcode to a ref name read from (or about to be looked up against) the filesystem. Ports
    /// the per-call iconv pattern from <c>reference_normalize_for_repo</c> (<c>refs.c:201-218</c>) and the <c>direach_flags</c> propagation from
    /// <c>refdb_fs.c:2535-2542</c>. Reads the config per call (matching the established <c>core.logallrefupdates</c> read pattern in <see
    /// cref="ShouldWriteReflogAsync"/>). </summary> <param name="refName">The ref name from the filesystem or caller.</param> <param
    /// name="cancellationToken">Cancellation token.</param> <returns>The precomposed name if <c>core.precomposeunicode</c> is set; otherwise the input
    /// unchanged.</returns>
    private async ValueTask<RefNameKey> PrecomposeRefNameAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        if (_repo?.Config is not { } config)
        {
            return refName;
        }

        bool precompose = await config.GetBoolAsync(
            "core.precomposeunicode", false, cancellationToken).ConfigureAwait(false);
        return precompose && refName.TryGetFileSystemString(out string? text) ? (RefNameKey)PathPrecompose.PrecomposeCore(text) : refName;
    }

    /// <summary>
    /// Reads a loose ref file by name. Matches <c>loose_lookup</c>
    /// (<c>refdb_fs.c:417-455</c>).
    /// </summary>
    private async Task<GitReference?> LooseLookupAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        string refDir = IsPerWorktreeRef(refName) ? _gitpath : _commonpath;
        string path = Path.Join(refDir, refName.ToFileSystemString());

        return await LooseLookupByPathAsync(refName, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a loose ref file at the given path. Returns null if the file doesn't
    /// exist (matching C's GIT_ENOTFOUND → error propagation).
    /// </summary>
    private async Task<GitReference?> LooseLookupByPathAsync(RefNameKey refName, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        // byte-domain read (C's loose_parse reads the raw file bytes, refdb_fs.c:244-253) — non-UTF-8 symref targets round-trip byte-exact.
        byte[] content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        // Symbolic ref: "ref: <target>"
        if (content.AsSpan().StartsWith("ref: "u8))
        {
            // C (loose_parse_symbolic, refdb_fs.c:381-400): the content is
            // trimmed and must exceed the header — "ref: " alone (or a bare
            // "ref:") is a corrupted loose reference file.
            // C (refdb_fs.c:433-435): git_str_rtrim — ASCII whitespace only.
            ReadOnlySpan<byte> target = AsciiText.Rtrim(content.AsSpan("ref: ".Length));
            if (target.Length == 0)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "corrupted loose reference file",
                    GitErrorCategory.Reference);
            }

            return new GitSymbolicReference { NameKey = refName, TargetNameBytes = content.AsMemory("ref: ".Length, target.Length) };
        }

        // Direct ref: "<oid-hex>[\n...]"
        return ParseDirectRef(refName, content);
    }

    /// <summary>
    /// Parses a direct ref file content into a <see cref="GitDirectReference"/>.
    /// Matches <c>loose_parse_oid</c> (<c>refdb_fs.c:218-242</c>).
    /// </summary>
    private GitDirectReference ParseDirectRef(RefNameKey refName, ReadOnlySpan<byte> content)
    {
        if (content.Length < _hexSize)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"corrupted loose reference file: {refName}",
                GitErrorCategory.Reference);
        }

        if (!GitOid.TryParse(content[.._hexSize], _oidType, out GitOid oid))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"corrupted loose reference file: {refName}",
                GitErrorCategory.Reference);
        }

        // If longer than hex, the char after must be ASCII whitespace or end
        // (C: git__isspace, refdb_fs.c:234-237 — NOT Unicode char.IsWhiteSpace).
        if (content.Length > _hexSize && !IsAsciiSpace((char)content[_hexSize]))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"corrupted loose reference file: {refName}",
                GitErrorCategory.Reference);
        }

        return new GitDirectReference { NameKey = refName, Target = oid };
    }

    /// <summary>Matches C's <c>git__isspace</c> (ASCII whitespace only).</summary>
    private static bool IsAsciiSpace(char c)
        => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // ── Packed-refs reading ────────────────────────────────────────────

    /// <summary>
    /// Checks that <paramref name="newRef"/> doesn't prefix-collide with an
    /// existing packed ref (unless it IS <paramref name="oldRef"/>). Matches
    /// <c>ref_is_available</c> + <c>reference_path_available</c>
    /// (refdb_fs.c:1083-1144): a new name that is a prefix of — or has as a
    /// prefix — an existing packed ref fails with "path to reference '%s'
    /// collides with existing one" (code -1, class GIT_ERROR_REFERENCE).
    /// </summary>
    private async ValueTask EnsurePathAvailableAsync(RefNameKey newRef, RefNameKey? oldRef, CancellationToken cancellationToken)
    {
        await EnsurePackedLoadedAsync(cancellationToken).ConfigureAwait(false);

        // byte-domain prefix-collision scan (C's ref_is_available strcmps the raw refname bytes, refdb_fs.c:1083-1144).
        ReadOnlyMemory<byte> newRefBytes = newRef.Bytes;
        ReadOnlyMemory<byte>? oldRefBytes = oldRef?.Bytes;
        foreach (RefNameKey thisKey in _packedRefs.Keys)
        {
            ReadOnlySpan<byte> thisRef = thisKey.Bytes.Span;
            if (oldRefBytes is not null && thisRef.SequenceEqual(oldRefBytes.Value.Span))
            {
                continue;
            }

            int cmplen = Math.Min(thisRef.Length, newRefBytes.Length);
            ReadOnlySpan<byte> lead = thisRef.Length < newRefBytes.Length ? newRefBytes.Span : thisRef;

            if (thisRef[..cmplen].SequenceEqual(newRefBytes.Span[..cmplen]) &&
                lead.Length > cmplen && lead[cmplen] == (byte)'/')
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"path to reference '{newRef}' collides with existing one",
                    GitErrorCategory.Reference);
            }
        }
    }

    /// <summary>
    /// Reloads packed-refs if the file stamp changed. Matches <c>packed_reload</c>
    /// (<c>refdb_fs.c:113-216</c>).
    /// </summary>
    private ValueTask EnsurePackedLoadedAsync(CancellationToken cancellationToken)
    {
        string packedPath = Path.Join(_commonpath, PackedRefsFile);
        if (!File.Exists(packedPath))
        {
            _packedRefs.Clear();
            _packedStampSize = -1;
            return ValueTask.CompletedTask;
        }

        var info = new FileInfo(packedPath);
        if (info.Length == _packedStampSize && info.LastWriteTimeUtc == _packedStampTime)
        {
            return ValueTask.CompletedTask; // up to date
        }

        return new ValueTask(LoadPackedRefsAsync(packedPath, info, cancellationToken));
    }

    private async Task LoadPackedRefsAsync(string packedPath, FileInfo info, CancellationToken cancellationToken)
    {
        // byte-domain parse (C's packed_reload parses the raw file bytes, refdb_fs.c:113-216) — non-UTF-8 refnames round-trip byte-exact as dictionary keys.
        byte[] content = await File.ReadAllBytesAsync(packedPath, cancellationToken).ConfigureAwait(false);
        var refs = new Dictionary<RefNameKey, PackedRef>();
        PeelingMode peeling = PeelingMode.None;
        bool sorted = false;

        ReadOnlySpan<byte> lines = content;
        int pos = 0;

        // Parse header: "# pack-refs with: peeled [fully-peeled] [sorted]"
        if (lines.Length > PackedRefsHeader.Length &&
            lines.StartsWith("# pack-refs with:"u8))
        {
            int eol = lines.IndexOf((byte)'\n');
            if (eol < 0)
            {
                throw CorruptPackedRefs();
            }

            ReadOnlySpan<byte> header = lines[PackedRefsHeader.Length..eol];
            if (ContainsWord(header, "fully-peeled"u8))
            {
                peeling = PeelingMode.Full;
            }
            else if (ContainsWord(header, "peeled"u8))
            {
                peeling = PeelingMode.Standard;
            }

            sorted = ContainsWord(header, "sorted"u8);
            pos = eol + 1;
        }

        // Skip comment lines.
        while (pos < lines.Length && lines[pos] == (byte)'#')
        {
            int eol = lines[pos..].IndexOf((byte)'\n');
            if (eol < 0)
            {
                throw CorruptPackedRefs();
            }

            pos += eol + 1;
        }

        // Parse entries: "<oid> <refname>\n[^<peel-oid>]\n"
        while (pos < lines.Length)
        {
            int eol = lines[pos..].IndexOf((byte)'\n');
            if (eol < 0)
            {
                // C (refdb_fs.c:171-172): a final entry line without '\n' is
                // a corrupted packed references file.
                throw CorruptPackedRefs();
            }

            int lineEnd = pos + eol;
            ReadOnlySpan<byte> line = lines[pos..lineEnd];

            // Strip trailing \r.
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            // Parse "<oid> <refname>"
            if (line.Length < _hexSize + 1)
            {
                // A blank line fails C's git_oid__fromstr (refdb_fs.c:165).
                throw CorruptPackedRefs();
            }

            if (!GitOid.TryParse(line[.._hexSize], _oidType, out GitOid oid))
            {
                throw CorruptPackedRefs();
            }

            if (line[_hexSize] != (byte)' ')
            {
                throw CorruptPackedRefs();
            }

            int nameEnd = line.Length;
            ReadOnlySpan<byte> refName = line.Slice(_hexSize + 1, nameEnd - _hexSize - 1);

            pos = lineEnd + (eol < 0 ? 0 : 1);

            // Check for peel line: "^<oid>"
            GitOid? peel = null;
            if (pos < lines.Length && lines[pos] == (byte)'^')
            {
                int peelEol = lines[pos..].IndexOf((byte)'\n');
                int peelLineEnd = peelEol < 0 ? lines.Length : pos + peelEol;

                // "^" + hexSize
                if (peelLineEnd - pos - 1 < _hexSize)
                {
                    throw CorruptPackedRefs();
                }

                if (!GitOid.TryParse(lines.Slice(pos + 1, _hexSize), _oidType, out GitOid peelOid))
                {
                    throw CorruptPackedRefs();
                }

                // C (refdb_fs.c:193-198): when anything follows the peel OID,
                // the line must be '\n'-terminated — "^<oid>garbage" at EOF
                // is corrupted.
                if (peelEol < 0 && pos + 1 + _hexSize < lines.Length)
                {
                    throw CorruptPackedRefs();
                }

                peel = peelOid;
                pos = peelLineEnd + (peelEol < 0 ? 0 : 1);
            }

            // C's
            // packed_reload marks a ref lacking a peel line as
            // PACKREF_CANNOT_PEEL when the header advertises peeling
            // (refdb_fs.c:197-202) — FULL for any ref, STANDARD for
            // refs/tags/ — and packed_find_peel then skips it, so a rewrite
            // emits no ^ line for it.
            bool cannotPeel = peel is null &&
                (peeling == PeelingMode.Full ||
                 (peeling == PeelingMode.Standard && refName.StartsWith("refs/tags/"u8)));

            refs[RefNameKey.From(refName.ToArray())] = new PackedRef(oid, peel, CannotPeel: cannotPeel);
        }

        _packedRefs = refs;
        _peelingMode = peeling;
        _packedSorted = sorted;
        _packedStampSize = info.Length;
        _packedStampTime = info.LastWriteTimeUtc;
    }

    private static GitException CorruptPackedRefs()
        => new(GitErrorCode.Error, "corrupted packed references file", GitErrorCategory.Reference);

    /// <summary>Checks if a space-delimited header contains a word with leading/trailing spaces.</summary>
    private static bool ContainsWord(ReadOnlySpan<byte> header, ReadOnlySpan<byte> word)
    {
        // " word " with leading/trailing spaces — byte-domain.
        byte[] padded = new byte[header.Length + 2];
        padded[0] = (byte)' ';
        header.CopyTo(padded.AsSpan(1));
        padded[^1] = (byte)' ';
        return padded.AsSpan().IndexOf(word) >= 0;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the ref is per-worktree (stored in gitpath, not commonpath).
    /// Matches <c>is_per_worktree_ref</c> (<c>refdb_fs.c:409-415</c>).
    /// </summary>
    private static bool IsPerWorktreeRef(RefNameKey refName)
    {
        return !refName.StartsWith("refs/"u8)
            || refName.StartsWith("refs/bisect/"u8)
            || refName.StartsWith("refs/worktree/"u8)
            || refName.StartsWith("refs/rewritten/"u8);
    }

    /// <summary>
    /// Gets the reflog file path. HEAD → <c>gitpath/logs/HEAD</c>; others →
    /// <c>commonpath/logs/&lt;refname&gt;</c>. Matches <c>reflog_path</c>
    /// (<c>refdb_fs.c:90-105</c>).
    /// </summary>
    private string GetReflogPath(RefNameKey refName)
    {
        string baseDir = refName == "HEAD" ? _gitpath : _commonpath;
        return Path.Join(baseDir, LogsDir, refName.ToFileSystemString());
    }

    /// <summary> Enumerates the loose ref NAMES under <paramref name="root"/>'s <c>refs/</c> directory, optionally filtered by <paramref name="nameFilter"/>
    /// (the worktree per-ref filter — C's <c>iter_load_paths</c>, refdb_fs.c:894-901). Names are root-relative with <c>/</c> separators. </summary>
    private async IAsyncEnumerable<RefNameKey> EnumerateLooseRefsAsync(
        string root,
        Func<RefNameKey, bool>? nameFilter,
        RefNameKey? glob,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string refsRoot = Path.Join(root, RefsDir);
        if (!Directory.Exists(refsRoot))
        {
            yield break;
        }

        foreach (string filePath in EnumerateRefFilesSorted(refsRoot))
        {
            // Skip .lock files.
            if (filePath.EndsWith(".lock", StringComparison.Ordinal))
            {
                continue;
            }

            // Convert file path to ref name, applying core.precomposeunicode when enabled (ports refdb_fs's direach_flags at refdb_fs.c:2535-2542 — each
            // enumerated loose ref name is precomposed).
            string relPath = Path.GetRelativePath(root, filePath);
            RefNameKey refName = relPath.Replace('\\', '/');
            refName = await PrecomposeRefNameAsync(refName, cancellationToken).ConfigureAwait(false);

            if (nameFilter is not null && !nameFilter(refName))
            {
                continue;
            }

            if (!MatchesGlob(refName, glob))
            {
                continue;
            }

            yield return refName;
        }
    }

    /// <summary>
    /// Loads one enumerated loose ref name, recording it in
    /// <paramref name="seen"/>. Corrupt/non-ref files are skipped (the
    /// previous path-based enumeration's GitException catch).
    /// </summary>
    private async Task<GitReference?> LoadEnumeratedLooseRefAsync(
        RefNameKey refName, HashSet<RefNameKey> seen, CancellationToken cancellationToken)
    {
        seen.Add(refName);
        try
        {
            return await LooseLookupAsync(refName, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException)
        {
            // Skip corrupt/non-ref files (e.g. marker files like .gitkeep).
            return null;
        }
    }

    /// <summary>
    /// Enumerates the loose-ref files under <paramref name="dir"/> in C's
    /// filesystem-iterator order (iterator.c:1380-1483): each directory's
    /// entries are sorted byte-wise (<c>git__strcmp</c>) and walked
    /// depth-first, so <c>refs/heads/a</c> sorts before
    /// <c>refs/heads/a/b</c> and a subdirectory's contents come right after
    /// the directory entry.
    /// </summary>
    private static IEnumerable<string> EnumerateRefFilesSorted(string dir)
    {
        string[] entries = Directory.GetFileSystemEntries(dir);
        // C's fs
        // iterator sorts each directory byte-wise with git__strcmp
        // (iterator.c:1085); StringComparer.Ordinal orders UTF-16 code
        // units, which diverges for non-BMP (surrogate-pair) and
        // invalid-UTF-8 names.
        if (entries.Length > 1)
        {
            // Encode each key once, rather than allocating two UTF-8 buffers
            // on every comparison. Sort paths alongside their byte keys.
            var keys = new ReadOnlyMemory<byte>[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                keys[i] = Encoding.UTF8.GetBytes(entries[i]);
            }

            Array.Sort(keys, entries, ByteOrdinalComparer.Instance);
        }
        foreach (string entry in entries)
        {
            if (Directory.Exists(entry))
            {
                foreach (string sub in EnumerateRefFilesSorted(entry))
                {
                    yield return sub;
                }
            }
            else if (File.Exists(entry))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Removes an empty directory tree, keeping any non-empty directory in
    /// place. Matches <c>git_futils_rmdir_r</c> with
    /// <c>GIT_RMDIR_SKIP_NONEMPTY</c> (futils.c:699-770) as used by
    /// <c>loose_lock</c> (refdb_fs.c:1155-1160).
    /// </summary>
    private static void RemoveEmptyDirTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (Directory.Exists(entry))
            {
                RemoveEmptyDirTree(entry);
            }
        }

        try
        {
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // SKIP_NONEMPTY: a non-empty directory is kept.
        }
    }

    /// <summary>
    /// Checks if a ref name matches a glob pattern. Uses <see cref="WildMatch"/>
    /// with <see cref="WildMatchFlags.None"/> so <c>*</c> crosses <c>/</c>, matching
    /// canonical libgit2 (<c>wildmatch(glob, name, 0)</c> in <c>refdb_fs.c</c>).
    /// Null/empty glob matches everything.
    /// </summary>
    private static bool MatchesGlob(RefNameKey refName, RefNameKey? glob)
    {
        if ((glob is null || glob.Value.IsEmpty))
        {
            return true;
        }

        return WildMatch.IsMatch(glob.Value.Span, refName.Span, WildMatchFlags.None);
    }

    // ── Write side ─────────────────────────────────────────────────────

    private const string LockSuffix = ".lock";

    /// <inheritdoc/>
    public IRefLock Lock(RefNameKey refName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!GitReferences.IsNameValid(refName.Span, GitReferenceFormatFlags.AllowOneLevel))
        {
            throw new GitException(
                GitErrorCode.InvalidSpec,
                $"invalid reference name '{refName}'",
                GitErrorCategory.Reference);
        }

        string refDir = IsPerWorktreeRef(refName) ? _gitpath : _commonpath;
        string refPath = Path.Join(refDir, refName.ToFileSystemString());
        string lockPath = refPath + LockSuffix;

        // Remove a possibly existing empty directory hierarchy which name would
        // collide with the reference name (matches loose_lock's rmdir_r).
        string? parentDir = Path.GetDirectoryName(refPath);
        if (parentDir is not null && Directory.Exists(parentDir))
        {
            // If the ref path itself is a directory, remove the whole empty
            // sub-tree (C's git_futils_rmdir_r with
            // GIT_RMDIR_SKIP_NONEMPTY, refdb_fs.c:1155-1160): removing only the
            // top-level directory would leave nested empty dirs and make the
            // later write fail with a raw IOException.
            RemoveEmptyDirTree(refPath);
        }

        // Create leading directories.
        Debug.Assert(parentDir is not null, "refPath is always nested under the git dir, so it has a parent.");
        Directory.CreateDirectory(parentDir);

        // Create the .lock file. If it already exists, the ref is locked.
        // The FileStream open is a metadata op (O_EXCL probe) — stays sync,
        // matching the FileConfigBackend.LockAsync precedent.
        try
        {
            using var fs = new FileStream(
                lockPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
        }
        catch (IOException)
        {
            // C (filebuf.c:44-51): a pre-existing .lock is GIT_ELOCKED, not EEXISTS.
            throw new GitException(
                GitErrorCode.Locked,
                $"failed to lock file '{lockPath}' for writing",
                GitErrorCategory.Reference);
        }

        return new FileRefLock(refName, lockPath);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(GitReference reference, IRefLock lockHandle, bool updateReflog, GitOid oldId, RefNameKey? oldTarget, GitSignature? committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FileRefLock fileLock = AssertLock(lockHandle, reference.NameKey);

        // C (refdb_fs.c:1609): reference_path_available runs before the CAS —
        // a new name that prefix-collides with an existing packed ref fails
        // with "path to reference '%s' collides with existing one" (-1).
        await EnsurePathAvailableAsync(reference.NameKey, oldRef: null, cancellationToken).ConfigureAwait(false);

        // Compare old value for atomic compare-and-set. C (write_tail,
        // refdb_fs.c:1630-1638): a CAS against a missing ref propagates
        // GIT_ENOTFOUND (see CompareOldRefAsync).
        (bool cmpError, int cmp) = await CompareOldRefAsync(reference.NameKey, oldId, oldTarget, cancellationToken).ConfigureAwait(false);
        if (cmpError)
        {
            fileLock.Dispose();
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{reference.NameKey}' not found",
                GitErrorCategory.Reference);
        }

        if (cmp != 0)
        {
            fileLock.Dispose();
            throw new GitException(
                GitErrorCode.Modified,
                "old reference value does not match",
                GitErrorCategory.Reference);
        }

        // Determine new value.
        GitOid newId = default;
        RefNameKey? newTarget = null;
        if (reference.IsSymbolic)
        {
            newTarget = ((GitSymbolicReference)reference).TargetNameKey;
        }
        else
        {
            newId = ((GitDirectReference)reference).Target;
        }

        // Don't write if the value didn't change (matches write_tail's no-op
        // check, refdb_fs.c:1643-1646). The zero OID is a REAL target value
        // here: C compares via a non-NULL pointer, so a zero-oid write
        // over a non-zero current value must proceed.
        bool unchanged;
        if (newId.IsZero && newTarget is null)
        {
            GitReference? cur = await LookupAsync(reference.NameKey, cancellationToken).ConfigureAwait(false);
            unchanged = cur is null || (cur is GitDirectReference d && d.Target.IsZero);
        }
        else
        {
            (bool newCmpError, int newCmp) = await CompareOldRefAsync(reference.NameKey, newId, newTarget, cancellationToken).ConfigureAwait(false);
            // A missing ref (newCmpError) means there is nothing to compare
            // against — C tolerates ENOTFOUND here and proceeds to write.
            unchanged = !newCmpError && newCmp == 0;
        }

        if (unchanged)
        {
            // No change — just unlock (discard the lock without writing).
            fileLock.Dispose();
            return;
        }

        // Append reflog if appropriate.
        if (updateReflog && await ShouldWriteReflogAsync(reference, cancellationToken).ConfigureAwait(false))
        {
            GitSignature sig = committer ?? await DefaultReflogSignatureAsync(cancellationToken).ConfigureAwait(false);
            await ReflogAppendCoreAsync(reference, oldId, newId, sig, message, cancellationToken).ConfigureAwait(false);
            await MaybeAppendHeadReflogAsync(reference, sig, message, cancellationToken).ConfigureAwait(false);
        }

        // Write the ref content and commit (atomic rename of .lock over the real ref).
        await WriteRefContentAsync(fileLock, reference, cancellationToken).ConfigureAwait(false);
        fileLock.Commit();
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(RefNameKey refName, IRefLock lockHandle, GitOid oldId, RefNameKey? oldTarget, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FileRefLock fileLock = AssertLock(lockHandle, refName);

        // Compare old value.
        (bool cmpError, int cmp) = await CompareOldRefAsync(refName, oldId, oldTarget, cancellationToken).ConfigureAwait(false);
        if (cmpError)
        {
            fileLock.Dispose();
            throw new GitException(
                GitErrorCode.Error,
                $"failed to read reference '{refName}' for comparison",
                GitErrorCategory.Reference);
        }

        if (cmp != 0)
        {
            fileLock.Dispose();
            throw new GitException(
                GitErrorCode.Modified,
                "old reference value does not match",
                GitErrorCategory.Reference);
        }

        // Delete the reflog file.
        await ReflogDeleteAsync(refName, cancellationToken).ConfigureAwait(false);

        // Remove from packed-refs if present (C's delete_tail does this FIRST,
        // tracking whether the packed entry existed; packed_delete reloads the
        // file first, refdb_fs.c:1483-1510).
        await EnsurePackedLoadedAsync(cancellationToken).ConfigureAwait(false);
        bool wasPacked = _packedRefs.ContainsKey(refName);
        await PackedDeleteAsync(refName, cancellationToken).ConfigureAwait(false);

        // Remove the loose ref file (the .lock file stays as a tombstone marker).
        string refDir = IsPerWorktreeRef(refName) ? _gitpath : _commonpath;
        string refPath = Path.Join(refDir, refName.ToFileSystemString());
        bool looseExists = File.Exists(refPath);
        if (looseExists)
        {
            File.Delete(refPath);
        }

        // C (refdb_fs.c:1817-1822): deleting a ref that exists nowhere (neither
        // loose nor packed) fails with GIT_ENOTFOUND "reference '%s' not found".
        if (!looseExists && !wasPacked)
        {
            fileLock.Dispose();
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{refName}' not found",
                GitErrorCategory.Reference);
        }

        // Remove the .lock file (discard — delete doesn't commit the lock).
        fileLock.Dispose();

        // Prune empty parent directories up to refs/.
        PruneEmptyDirs(refDir, refName);
    }

    /// <inheritdoc/>
    public async Task RenameAsync(IRefLock lockHandle, RefNameKey newRefName, GitOid newId, RefNameKey? newTarget, bool force, GitSignature? committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = newRefName.ToFileSystemString(); // Validate before any rename side effects.

        FileRefLock fileLock = AssertLock(lockHandle, lockHandle.RefName);

        if (!GitReferences.IsNameValid(newRefName.Span, GitReferenceFormatFlags.AllowOneLevel))
        {
            fileLock.Dispose();
            throw new GitException(
                GitErrorCode.InvalidSpec,
                $"invalid reference name '{newRefName}'",
                GitErrorCategory.Reference);
        }

        // Check that the new ref name doesn't exist (unless force). C
        // (reference_path_available, refdb_fs.c:1112-1126) reports EEXISTS
        // with this message.
        if (!force && await LookupAsync(newRefName, cancellationToken).ConfigureAwait(false) is not null)
        {
            fileLock.Dispose();
            throw new GitException(
                GitErrorCode.Exists,
                $"failed to write reference '{newRefName}': a reference with that name already exists.",
                GitErrorCategory.Reference);
        }

        // C (refdb_fs.c:1850): reference_path_available(backend, new_name,
        // old_name, force) — prefix collisions with existing packed refs
        // fail the rename.
        await EnsurePathAvailableAsync(newRefName, oldRef: fileLock.RefName, cancellationToken).ConfigureAwait(false);

        // Read the old ref to compute old value for reflog.
        GitReference? oldRef = await LookupAsync(fileLock.RefName, cancellationToken).ConfigureAwait(false);

        // Rename the reflog. C (refdb_fs.c:1878-1885): the rename is fine
        // when the old log is missing (GIT_ENOTFOUND); when the destination
        // is a non-empty directory the reflog placement fails and the append
        // is skipped — but the ref rename still proceeds (loose_commit runs
        // regardless).
        bool reflogPlaced = true;
        if (oldRef is not null)
        {
            reflogPlaced = await ReflogRenameAsync(fileLock.RefName, newRefName, cancellationToken).ConfigureAwait(false);
        }

        // Build the new reference content.
        GitReference newRef;
        if (newTarget is not null)
        {
            newRef = new GitSymbolicReference { NameKey = newRefName, TargetNameKey = newTarget.Value };
        }
        else
        {
            newRef = new GitDirectReference { NameKey = newRefName, Target = newId };
        }

        // Append reflog for the new ref. C (refdb_fs.c:1878-1885) appends
        // UNCONDITIONALLY — regardless of core.logallrefupdates — when the
        // reflog rename succeeded or the old log was missing; the log file
        // is created if needed.
        if (oldRef is not null && reflogPlaced)
        {
            GitOid oldIdValue = oldRef.IsSymbolic ? default : ((GitDirectReference)oldRef).Target;
            GitSignature sig = committer ?? await DefaultReflogSignatureAsync(cancellationToken).ConfigureAwait(false);
            await ReflogAppendCoreAsync(newRef, oldIdValue, newId, sig, message, cancellationToken).ConfigureAwait(false);
            await MaybeAppendHeadReflogAsync(newRef, sig, message, cancellationToken).ConfigureAwait(false);
        }

        // C's
        // refdb_fs_backend__rename runs delete_tail(old) FIRST (packed_delete
        // + loose_delete, refdb_fs.c:1867) and only then locks/writes the new
        // name (loose_lock, 1873). The old loose file must be gone BEFORE the
        // new path's parent directory is created, or a namespace-colliding
        // rename (refs/heads/a/b → refs/heads/a/b/c) throws a raw
        // IOException from Directory.CreateDirectory/File.Move — so the old
        // loose file is deleted BEFORE the new path is written.
        string oldRefDir = IsPerWorktreeRef(fileLock.RefName) ? _gitpath : _commonpath;
        string oldRefPath = Path.Join(oldRefDir, fileLock.RefName.ToFileSystemString());
        if (File.Exists(oldRefPath))
        {
            File.Delete(oldRefPath);
        }

        // C's rename runs the full delete tail on the old name
        // (refdb_fs.c:1867), which removes the old entry from packed-refs —
        // a packed-only ref must not keep resolving under its old name.
        await PackedDeleteAsync(fileLock.RefName, cancellationToken).ConfigureAwait(false);

        // Write the new ref content to the new ref path (atomic write). byte-domain write (C's loose_commit writes raw bytes, refdb_fs.c:1188-1205) — non-UTF-8
        // symref targets round-trip byte-exact.
        string newRefDir = IsPerWorktreeRef(newRefName) ? _gitpath : _commonpath;
        string newRefPath = Path.Join(newRefDir, newRefName.ToFileSystemString());
        await AsyncFileIO.WriteAtomicAsync(
            newRefPath,
            newRef.IsSymbolic
                ? ((GitSymbolicReference)newRef).TargetNameKey.SymbolicContent()
                : Encoding.UTF8.GetBytes($"{((GitDirectReference)newRef).Target}\n"),
            cancellationToken).ConfigureAwait(false);

        // Discard the old ref's .lock file (no longer needed).
        fileLock.Dispose();

        PruneEmptyDirs(oldRefDir, fileLock.RefName);
    }

    /// <inheritdoc/>
    public ValueTask UnlockAsync(IRefLock lockHandle, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FileRefLock fileLock = AssertLock(lockHandle, lockHandle.RefName);
        fileLock.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task EnsureLogAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string logPath = GetReflogPath(refName);
        if (File.Exists(logPath))
        {
            return; // already exists
        }

        // C (refdb_fs.c:2076-2095): ensure_log creates the file
        // unconditionally — there is NO core.logallrefupdates policy check
        // (the stash flow relies on this to force a reflog).
        string? dir = Path.GetDirectoryName(logPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        // Create an empty reflog file.
        await File.WriteAllBytesAsync(logPath, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ReflogWriteAsync(RefNameKey refName, GitRefLog reflog, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string logPath = GetReflogPath(refName);
        string? dir = Path.GetDirectoryName(logPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        // byte-domain write (C writes the raw reflog bytes, refdb_fs.c:2368).
        byte[] content = reflog.SerializeBytes();
        await AsyncFileIO.WriteAtomicAsync(logPath, content, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ReflogAppendAsync(RefNameKey refName, GitOid oldId, GitOid newId, GitSignature committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string logPath = GetReflogPath(refName);
        string? dir = Path.GetDirectoryName(logPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        // If the reflog path is a directory (left over), remove it.
        if (Directory.Exists(logPath))
        {
            try
            {
                Directory.Delete(logPath, recursive: false);
            }
            catch (IOException)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"cannot create reflog at '{refName}', there are reflogs beneath that folder",
                    GitErrorCategory.Reference);
            }
        }

        // byte-domain append (C appends the raw serialized entry bytes, refdb_fs.c:2368).
        using var line = new PooledByteBufferWriter(256);
        using var scratch = new PooledByteBufferWriter(128);
        ReflogFormatter.WriteEntry(line, scratch, oldId, newId, committer, message);
        await AsyncFileIO.AppendAllBytesAsync(logPath, line.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask<bool> ReflogRenameAsync(RefNameKey oldName, RefNameKey newName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string oldPath = GetReflogPath(oldName);
        string newPath = GetReflogPath(newName);

        if (!File.Exists(oldPath))
        {
            return ValueTask.FromResult(true); // nothing to rename (C: GIT_ENOTFOUND, tolerated)
        }

        // Two-phase rename like C (refdb_fs.c:2410-2444): the old reflog is
        // moved to a temp file first, then placed at the new path. This
        // copes with namespace collisions (a/b → a/b/c).
        string logsRoot = Path.Join(IsPerWorktreeRef(oldName) ? _gitpath : _commonpath, LogsDir);
        string tempPath = Path.Join(logsRoot, "temp_reflog_git2_" + Guid.NewGuid().ToString("N")[..16]);
        File.Move(oldPath, tempPath);

        string? newDir = Path.GetDirectoryName(newPath);
        if (newDir is not null)
        {
            Directory.CreateDirectory(newDir);
        }

        // If newPath is a directory, remove it only when EMPTY. C runs
        // git_futils_rmdir_r with GIT_RMDIR_SKIP_NONEMPTY (refdb_fs.c:2435-2439):
        // a non-empty directory is left in place and the placement fails —
        // the reflog stays stranded at the temp path, and the caller skips
        // the reflog append but still completes the ref rename.
        if (Directory.Exists(newPath))
        {
            try
            {
                Directory.Delete(newPath, recursive: false);
            }
            catch (IOException)
            {
                return ValueTask.FromResult(false); // stranded, like C
            }
        }

        // POSIX rename replaces an existing file (the C# delete-then-move
        // would leave a crash window).
        File.Move(tempPath, newPath, overwrite: true);

        // Prune empty parent directories of the old reflog.
        string? parentDir = Path.GetDirectoryName(oldPath);
        Debug.Assert(parentDir is not null, "oldPath is always nested under the git dir.");
        PruneEmptyDirs(parentDir, oldName);
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc/>
    public ValueTask ReflogDeleteAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string logPath = GetReflogPath(refName);
        if (File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        return ValueTask.CompletedTask;
    }

    // ── Write helpers ──────────────────────────────────────────────────

    private static FileRefLock AssertLock(IRefLock lockHandle, RefNameKey expectedName)
    {
        if (lockHandle is not FileRefLock fileLock)
        {
            throw new ArgumentException("invalid lock handle type", nameof(lockHandle));
        }

        if (fileLock.RefName != expectedName)
        {
            throw new ArgumentException(
                $"lock handle is for '{fileLock.RefName}', not '{expectedName}'",
                nameof(lockHandle));
        }

        return fileLock;
    }

    /// <summary>
    /// Writes the ref content into the <c>.lock</c> file (does not commit yet).
    /// Matches <c>loose_commit</c> (refdb_fs.c:1188-1205) — but writes to the
    /// lock file rather than committing directly.
    /// </summary>
    private static async Task WriteRefContentAsync(FileRefLock fileLock, GitReference reference, CancellationToken cancellationToken)
    {
        // byte-domain write (C's loose_commit writes raw bytes, refdb_fs.c:1188-1205).
        ReadOnlyMemory<byte> content = reference.IsSymbolic
            ? ((GitSymbolicReference)reference).TargetNameKey.SymbolicContent()
            : Encoding.UTF8.GetBytes($"{((GitDirectReference)reference).Target}\n");

        await AsyncFileIO.WriteAtomicAsync(fileLock.LockPath, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compares the current stored ref value against the expected old value.
    /// Returns (error, cmp): error=true if the lookup failed unexpectedly;
    /// cmp=0 if match (or no old value to check), cmp!=0 if mismatch.
    /// Matches <c>cmp_old_ref</c> (refdb_fs.c:1514-1545).
    /// </summary>
    private ValueTask<(bool error, int cmp)> CompareOldRefAsync(RefNameKey refName, GitOid oldId, RefNameKey? oldTarget, CancellationToken cancellationToken)
    {
        // C (cmp_old_ref, refdb_fs.c:1514-1545): "It matches if there is no
        // old value to compare against" — the NULL-pointer case, mapped here
        // to (zero OID, null target). A non-zero oldId is a real CAS value.
        // Zero oldId + null target (no CAS) is the majority short-circuit.
        if (oldId.IsZero && oldTarget is null)
        {
            return ValueTask.FromResult((false, 0));
        }

        return new ValueTask<(bool error, int cmp)>(CompareOldRefSlowAsync(refName, oldId, oldTarget, cancellationToken));
    }

    /// <summary>Slow path of <see cref="CompareOldRefAsync"/>: reads the current ref (disk IO) and compares.</summary>
    private async Task<(bool error, int cmp)> CompareOldRefSlowAsync(RefNameKey refName, GitOid oldId, RefNameKey? oldTarget, CancellationToken cancellationToken)
    {
        GitReference? current = await LookupAsync(refName, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            // C (refdb_fs.c:1525-1528): ENOTFOUND with a zero old_id matches
            // (creating); a non-zero old_id CAS on a missing ref propagates
            // GIT_ENOTFOUND — not GIT_EMODIFIED. Signalled as (error, 0) so
            // the CAS caller maps it to NotFound while the no-change check
            // treats a missing ref as "nothing to compare" (C tolerates
            // ENOTFOUND there, refdb_fs.c:1640-1642).
            return (true, 0);
        }

        // Type mismatch.
        if (!oldId.IsZero && current.IsSymbolic)
        {
            return (false, -1);
        }

        if (oldTarget is not null && !current.IsSymbolic)
        {
            return (false, 1);
        }

        if (!oldId.IsZero && !current.IsSymbolic)
        {
            GitOid currentId = ((GitDirectReference)current).Target;
            return (false, currentId.Equals(oldId) ? 0 : 1);
        }

        if (oldTarget is not null && current.IsSymbolic)
        {
            RefNameKey currentTarget = ((GitSymbolicReference)current).TargetNameKey;
            return (false, currentTarget == oldTarget ? 0 : 1);
        }

        return (false, 0);
    }

    /// <summary>
    /// Decides whether a reflog entry should be written for the given reference,
    /// based on <c>core.logallrefupdates</c>. Matches <c>git_refdb_should_write_reflog</c>
    /// (refdb.c:303-338).
    /// </summary>
    private async ValueTask<bool> ShouldWriteReflogAsync(GitReference reference, CancellationToken cancellationToken)
    {
        if (_repo is null)
        {
            return true;
        }

        GitConfigEntry? entry = await _repo.Config.GetEntryAsync("core.logallrefupdates", cancellationToken).ConfigureAwait(false);

        // C (config_cache.c:61-64): the string "always" maps to
        // GIT_LOGALLREFUPDATES_ALWAYS — a reflog is written for EVERY ref
        // (refdb.c:334-336). The map lookup is git_config_lookup_map_value
        // (config.c:1381-1415), whose GIT_CONFIGMAP_STRING arm uses
        // strcasecmp — the match is case-insensitive.
        if (entry is not null && entry.Value.ValueBytes is { } alwaysBytes
            && ConfigKeyName.AsciiEqualsIgnoreCase(alwaysBytes.Span, "always"u8))
        {
            return true;
        }

        bool logAll;
        if (entry is null)
        {
            logAll = !_repo.IsBare; // unset → defaults to !bare
        }
        else if (entry.Value.ValueBytes is { } logBytes && ConfigurationValueParser.TryParseBool(logBytes.Span, out bool parsed))
        {
            logAll = parsed;
        }
        else
        {
            // C (config.c:1415-1416 + refdb.c:306-308): a value that maps to
            // neither FALSE/TRUE nor "always" fails the configmap lookup and
            // the error PROPAGATES out of git_refdb_should_write_reflog —
            // the ref write fails.
            throw new GitException(
                GitErrorCode.Error,
                $"failed to map '{entry.Value.Value}'",
                GitErrorCategory.Config);
        }

        if (!logAll)
        {
            return false;
        }

        // C (refdb.c:325-332): write if it already has a log, or if it's under
        // heads/, remotes/, notes/, or is HEAD. There is no refs/stash rule.
        return File.Exists(GetReflogPath(reference.NameKey))
            || reference.NameKey.StartsWith("refs/heads/"u8)
            || reference.NameKey == "HEAD"
            || reference.NameKey.StartsWith("refs/remotes/"u8)
            || reference.NameKey.StartsWith("refs/notes/"u8);
    }

    /// <summary>
    /// Returns the default reflog signature (the repo's ident or user.name/email
    /// from config). Matches <c>git_reference__log_signature</c>.
    /// </summary>
    private async ValueTask<GitSignature> DefaultReflogSignatureAsync(CancellationToken cancellationToken)
    {
        if (_repo is not null)
        {
            // C (refs.c:439-446, refs_configured_ident): the repo ident is used ONLY when BOTH name and email are set — a partial SetIdent falls through to the
            // config defaults instead of mixing the ident name with the config email.
            string? name;
            string? email;
            if (_repo.IdentName is { } identName && _repo.IdentEmail is { } identEmail)
            {
                name = identName;
                email = identEmail;
            }
            else
            {
                // read the raw value bytes — C's git_reference__log_signature takes the config char* bytes verbatim, so a non-UTF-8 user.name/user.email must
                // not degrade to U+FFFD here.
                byte[]? nameBytes = await _repo.Config.GetBytesAsync("user.name", cancellationToken).ConfigureAwait(false);
                byte[]? emailBytes = await _repo.Config.GetBytesAsync("user.email", cancellationToken).ConfigureAwait(false);
                if (nameBytes is not null && emailBytes is not null)
                {
                    DateTimeOffset now = DateTimeOffset.Now;
                    return GitSignature.Create(nameBytes, emailBytes, new GitTime(now.ToUnixTimeSeconds(), (int)now.Offset.TotalMinutes));
                }

                name = nameBytes is null ? null : Encoding.UTF8.GetString(nameBytes);
                email = emailBytes is null ? null : Encoding.UTF8.GetString(emailBytes);
            }

            if (name is not null && email is not null)
            {
                return GitSignature.Now(name, email);
            }
        }

        // Fallback (matches git's behavior when no ident is configured).
        return GitSignature.Now("unknown", "unknown");
    }

    /// <summary>
    /// Appends a reflog entry for HEAD if the ref is the current branch.
    /// Matches <c>maybe_append_head</c> (refdb_fs.c:2284-2375).
    /// </summary>
    private async Task MaybeAppendHeadReflogAsync(GitReference reference, GitSignature sig, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken)
    {
        // Only write a HEAD reflog if HEAD is symbolic and points at this ref's
        // parent branch (or the ref IS HEAD).
        if (reference.NameKey == "HEAD")
        {
            return; // already appended above
        }

        // C (maybe_append_head, refdb_fs.c:1567-1592 via
        // git_refdb_should_write_head_reflog, refdb.c:348-351): symbolic ref
        // updates never produce a HEAD entry.
        if (reference.IsSymbolic)
        {
            return;
        }

        GitReference? head = await LookupAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is null || !head.IsSymbolic)
        {
            return;
        }

        // C's
        // git_refdb_should_write_head_reflog resolves the WHOLE symbolic
        // chain (refdb.c:360-369) before comparing with the written ref's
        // name — a chain HEAD → master → stable writes no HEAD reflog when
        // stable is written.
        RefNameKey headTarget;
        if (_repo is not null)
        {
            GitReference? resolved = await _repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                // Dangling chain (C's GIT_ENOTFOUND) — fall back to the raw
                // symbolic target (refdb.c:364-366).
                headTarget = ((GitSymbolicReference)head).TargetNameKey;
            }
            else if (resolved.IsSymbolic)
            {
                headTarget = ((GitSymbolicReference)resolved).TargetNameKey;
            }
            else
            {
                headTarget = resolved.NameKey;
            }
        }
        else
        {
            headTarget = ((GitSymbolicReference)head).TargetNameKey;
        }

        if (headTarget != reference.NameKey)
        {
            return;
        }

        // HEAD points at this ref — append a HEAD reflog entry.
        var oldId = default(GitOid);
        GitOid newId = ((GitDirectReference)reference).Target;
        await ReflogAppendCoreAsync(
            new GitDirectReference { NameKey = "HEAD", Target = newId },
            oldId,
            newId,
            sig,
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core reflog-append: computes old/new IDs and writes the entry.
    /// </summary>
    private async Task ReflogAppendCoreAsync(GitReference reference, GitOid explicitOldId, GitOid explicitNewId, GitSignature sig, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken)
    {
        // "normal" symbolic updates (non-HEAD) do not write a reflog.
        if (reference.IsSymbolic && reference.NameKey != "HEAD" && explicitOldId.IsZero && explicitNewId.IsZero)
        {
            return;
        }

        GitOid oldId = explicitOldId;
        if (oldId.IsZero && _repo is not null)
        {
            // Resolve the current value of the ref for old_id.
            GitReference? resolved = await _repo.Refs.ResolveAsync(reference.NameKey, cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                oldId = ((GitDirectReference)resolved).Target;
            }
        }

        GitOid newId = explicitNewId;
        if (newId.IsZero)
        {
            if (!reference.IsSymbolic)
            {
                newId = ((GitDirectReference)reference).Target;
            }
            else if (reference.NameKey == "HEAD" && _repo is not null)
            {
                // C (refdb_fs.c:2318-2332): resolve the symbolic target — an
                // entry is appended when the target exists; detaching HEAD
                // (missing target) does not create an entry.
                RefNameKey targetName = ((GitSymbolicReference)reference).TargetNameKey;
                GitReference? resolved = await _repo.Refs.ResolveAsync(targetName, cancellationToken).ConfigureAwait(false);
                if (resolved is not GitDirectReference direct)
                {
                    return;
                }

                newId = direct.Target;
            }
        }

        await ReflogAppendAsync(reference.NameKey, oldId, newId, sig, message, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteOidBytes(PooledByteBufferWriter buffer, GitOid oid)
    {
        Span<byte> hex = buffer.GetSpan(oid.HexSize);
        int written = oid.FormatHex(hex);
        buffer.Advance(written);
    }

    // ── Packed-refs write ──────────────────────────────────────────────

    /// <summary>
    /// Loads all loose refs into the in-memory packed-refs table. Matches
    /// <c>packed_loadloose</c> (refdb_fs.c:326-345) +
    /// <c>_dirent_loose_load</c> (refdb_fs.c:295-318) +
    /// <c>loose_lookup_to_packfile</c> (refdb_fs.c:255-293). Loose files
    /// shadow/overwrite packed entries of the same name; symbolic refs are
    /// skipped; a loose read failure is treated as a filesystem race
    /// (<c>git_error_clear</c>) and the ref is skipped. Peels are computed
    /// lazily by <see cref="WritePackedRefsAsync"/>.
    /// </summary>
    private async Task PackedLoadLooseAsync(CancellationToken cancellationToken)
    {
        // C (packed_loadloose, refdb_fs.c:326-333): the loose scan walks the BACKEND's gitpath — for a linked worktree that is the worktree gitdir, whose refs/
        // holds the per-worktree refs; for a normal repo gitpath == commonpath.
        string refsRoot = Path.Join(_gitpath, RefsDir);
        if (!Directory.Exists(refsRoot))
        {
            return;
        }

        foreach (string filePath in EnumerateRefFilesSorted(refsRoot))
        {
            // C (refdb_fs.c:295-298): _dirent_loose_load skips ".lock" files.
            if (filePath.EndsWith(LockSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            RefNameKey refName = Path.GetRelativePath(_gitpath, filePath).Replace('\\', '/');
            refName = await PrecomposeRefNameAsync(refName, cancellationToken).ConfigureAwait(false);

            GitReference? loose;
            try
            {
                loose = await LooseLookupByPathAsync(refName, filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException)
            {
                // C (refdb_fs.c:262-267): a loose read failure is assumed to
                // be someone changing the filesystem under us — clear and skip.
                continue;
            }

            if (loose is null || loose.IsSymbolic)
            {
                // null = file vanished (race); symbolic refs are not packed
                // (C skips "ref: " files, refdb_fs.c:268-270).
                continue;
            }

            var direct = (GitDirectReference)loose;
            // WasLoose marks refs packed_remove_loose must prune after the write (refdb_fs.c:1348-1398, 1465-1468). byte-keyed insert (C's sortedcache
            // upsert over the raw name bytes, refdb_fs.c:175).
            _packedRefs[refName] = new PackedRef(direct.Target, Peel: null, WasLoose: true);
        }
    }

    /// <summary>
    /// Packs loose refs into packed-refs. Matches
    /// <c>refdb_fs_backend__compress</c> (refdb_fs.c:1897-1910):
    /// <c>packed_reload</c> → <c>packed_loadloose</c> → <c>packed_write</c>
    /// — and <c>packed_write</c> ends with <c>packed_remove_loose</c>
    /// (refdb_fs.c:1465-1468), which prunes the loose files that were just
    /// packed.
    /// </summary>
    public async Task CompressAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // packed_reload
        await EnsurePackedLoadedAsync(cancellationToken).ConfigureAwait(false);
        // packed_loadloose
        await PackedLoadLooseAsync(cancellationToken).ConfigureAwait(false);
        // packed_write (incl. packed_remove_loose at refdb_fs.c:1465-1468)
        await WritePackedRefsAsync(cancellationToken).ConfigureAwait(false);
        await PackedRemoveLooseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the loose ref files that were just packed. Matches
    /// <c>packed_remove_loose</c> (refdb_fs.c:1348-1398): each packed
    /// was-loose ref is re-locked (EEXISTS/ENOTFOUND → someone else is
    /// updating it — skip), re-read, and unlinked only when it still holds
    /// the packed OID (a moved ref, a symref, or an unparseable file is
    /// kept).
    /// </summary>
    private async Task PackedRemoveLooseAsync(CancellationToken cancellationToken)
    {
        foreach ((RefNameKey key, PackedRef pr) in _packedRefs)
        {
            if (!pr.WasLoose)
            {
                continue;
            }

            RefNameKey name = key;

            // C (refdb_fs.c:1364-1368): a lock held by someone else (ELOCKED
            // → EEXISTS) or a vanished ref (ENOTFOUND) — let them proceed.
            IRefLock lockHandle;
            try
            {
                lockHandle = Lock(name);
            }
            catch (GitException ex) when (ex.Code is GitErrorCode.Locked or GitErrorCode.Exists)
            {
                continue;
            }

            try
            {
                // Re-read the loose ref; only unlink when it still holds the
                // packed OID (refdb_fs.c:1370-1390).
                GitReference? current = await LookupAsync(name, cancellationToken).ConfigureAwait(false);
                if (current is not GitDirectReference dr || dr.Target != pr.Oid)
                {
                    continue;
                }

                string refDir = IsPerWorktreeRef(name) ? _gitpath : _commonpath;
                string refPath = Path.Join(refDir, name.ToFileSystemString());
                if (File.Exists(refPath))
                {
                    File.Delete(refPath);
                }
            }
            finally
            {
                lockHandle.Dispose();
            }
        }
    }

    /// <summary>
    /// Removes a ref from the packed-refs file (if present) and rewrites the
    /// file atomically. Matches <c>packed_delete</c> (refdb_fs.c:1483-1510).
    /// </summary>
    private async Task PackedDeleteAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        await EnsurePackedLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!_packedRefs.ContainsKey(refName))
        {
            return; // not in packed-refs
        }

        _packedRefs.Remove(refName);
        await WritePackedRefsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the packed-refs file atomically with the current in-memory
    /// packed refs. Matches <c>packed_write</c> (refdb_fs.c:1412-1481).
    /// </summary>
    private async Task WritePackedRefsAsync(CancellationToken cancellationToken)
    {
        string packedPath = Path.Join(_commonpath, PackedRefsFile);

        // byte-domain write (C writes the raw refname bytes, refdb_fs.c:1308-1335) — non-UTF-8 refnames round-trip byte-exact.
        using var buffer = new PooledByteBufferWriter(256);
        buffer.Write("# pack-refs with: peeled fully-peeled sorted \n"u8);

        // Sort refs by name (matches C's sortedcache which keeps refs sorted). byte-ordinal sort over the raw name bytes (C's packref_cmp = strcmp,
        // refdb_fs.c:107-110).
        var sorted = new SortedDictionary<RefNameKey, PackedRef>(RefNameKeyComparer.Ordinal);
        foreach ((RefNameKey key, PackedRef pr) in _packedRefs)
        {
            sorted[key] = pr;
        }

        foreach ((RefNameKey key, PackedRef pr) in sorted)
        {
            // C (refdb_fs.c:1453-1458): packed_find_peel runs for every ref
            // lacking a peel — a tag object's target is resolved and emitted
            // as a "^<oid>" line even when the original file had none.
            // refs
            // marked PACKREF_CANNOT_PEEL at load (no peel line in a
            // peeled/fully-peeled file) are skipped — no ^ line is emitted
            // for them (refdb_fs.c:1271, 1329-1331).
            GitOid? peel = pr.Peel;
            if (peel is null && !pr.CannotPeel && _repo is not null)
            {
                GitObject? obj = await _repo.Objects.LookupAsync(pr.Oid, cancellationToken).ConfigureAwait(false);
                if (obj is GitTag tag)
                {
                    peel = tag.Target;
                }

                obj?.Dispose();
            }

            WriteOidBytes(buffer, pr.Oid);
            buffer.WriteByte((byte)' ');
            buffer.Write(key.Bytes.Span);
            buffer.WriteByte((byte)'\n');
            if (peel is { } peelValue)
            {
                buffer.WriteByte((byte)'^');
                WriteOidBytes(buffer, peelValue);
                buffer.WriteByte((byte)'\n');
            }
        }

        await AsyncFileIO.WriteAtomicAsync(packedPath, buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        // Update the stamp so we don't reload.
        var info = new FileInfo(packedPath);
        _packedStampSize = info.Length;
        _packedStampTime = info.LastWriteTimeUtc;
    }

    // ── File helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Prunes empty parent directories of a deleted ref, up to (but not
    /// including) the refs root. Matches <c>prune_ref</c> / C's directory
    /// cleanup after ref deletion.
    /// </summary>
    private static void PruneEmptyDirs(string baseDir, RefNameKey refName)
    {
        string refPath = Path.Join(baseDir, refName.ToFileSystemString());
        string? parent = Path.GetDirectoryName(refPath);
        if (parent is null)
        {
            return;
        }

        // Walk up from the ref's parent, removing empty directories until we hit baseDir.
        while (parent is not null && parent.Length > baseDir.Length)
        {
            if (!Directory.Exists(parent))
            {
                break;
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(parent).GetEnumerator().MoveNext())
                {
                    break; // not empty
                }

                Directory.Delete(parent);
            }
            catch (IOException)
            {
                break;
            }

            parent = Path.GetDirectoryName(parent);
        }
    }

    private sealed class FileRefLock : IRefLock
    {
        public FileRefLock(RefNameKey refName, string lockPath)
        {
            RefName = refName;
            LockPath = lockPath;
        }

        public RefNameKey RefName { get; }
        public string LockPath { get; }
        public bool Committed { get; private set; }

        /// <summary> Commits the lock: atomically renames the <c>.lock</c> file over the real ref file. Matches <c>loose_commit</c> → <c>git_filebuf_commit</c>
        /// (p_rename, filebuf.c:447) — no crash window with a missing ref
        /// file. Stays sync: only metadata ops (File.Move).
        /// </summary>
        public void Commit()
        {
            if (Committed)
            {
                return;
            }

            string realPath = LockPath[..^LockSuffix.Length];
            File.Move(LockPath, realPath, overwrite: true);
            Committed = true;
        }

        public void Dispose()
        {
            if (Committed)
            {
                return;
            }

            try
            {
                if (File.Exists(LockPath))
                {
                    File.Delete(LockPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private readonly record struct PackedRef(GitOid Oid, GitOid? Peel, bool WasLoose = false, bool CannotPeel = false);

    private enum PeelingMode
    {
        None = 0,
        Standard = 1,  // only refs/tags/* are peeled
        Full = 2,      // all refs are peeled
    }
}
