// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

/*
 * A modified `bsearch` from the BSD glibc.
 *
 * Copyright (c) 1990 Regents of the University of California.
 * All rights reserved.
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 * 1. Redistributions of source code must retain the above copyright
 * notice, this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright
 * notice, this list of conditions and the following disclaimer in the
 * documentation and/or other materials provided with the distribution.
 * 3. [rescinded 22 July 1999]
 * 4. Neither the name of the University nor the names of its contributors
 * may be used to endorse or promote products derived from this software
 * without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE REGENTS AND CONTRIBUTORS ``AS IS'' AND
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE REGENTS OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
 * OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
 * HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
 * LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
 * OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
 * SUCH DAMAGE.
 */

using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Index;

/// <summary>
/// Git index (staging area). Managed port of <c>src/libgit2/index.c</c> +
/// <c>index.h</c> + <c>index_map.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Parses and writes <c>.git/index</c> v2/v3 binary format: header (DIRC),
/// entries, extensions (TREE, REUC, NAME), and trailing checksum. Provides
/// sorted entry access, path lookup, conflict detection, staging
/// (<c>AddByPathAsync</c>/<c>AddFromBufferAsync</c>/<c>Remove</c>), disk write
/// (<c>WriteAsync</c>), tree building (<c>WriteTreeAsync</c>), and snapshot for
/// iterator use.
/// </para>
/// <para>
/// v4 path-prefix compression is supported (read + write). The
/// <c>truncate_racily_clean</c> pre-write smudge is NOT ported — the racy-git
/// check in <see cref="DiffGenerator"/> (<c>EntryNewerThanIndex</c>)
/// plus nanosecond stat precision make it redundant (see the comment in
/// <see cref="SerializeForWrite"/>).
/// </para>
/// </remarks>
[SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's index.c — the class intentionally mirrors the C file's broad surface; coupling is inherent to the port.")]
public sealed class GitIndex : IDisposable
{
    /// <summary>"DIRC" magic in big-endian.</summary>
    private const uint HeaderSignature = 0x44495243;

    private const int HeaderSize = 12;
    private const int VersionDefault = 2;
    private const int VersionExt = 3; // extended flags
    private const int VersionComp = 4; // path-prefix compression (v4)

    private readonly List<GitIndexEntry> _entries = [];
    private readonly List<GitIndexReucEntry> _reuc = [];
    private readonly List<GitIndexNameEntry> _names = [];
    private Dictionary<(GitPath path, int stage), GitIndexEntry>? _entriesMap;
    private bool _ignoreCase;
    private GitRepository? _owner;
    private string? _indexPath;
    private bool _dirty;
    private GitOid _checksum;

    /// <summary>
    /// The index file's own mtime at the moment it was last read or written.
    /// Used by the racy-git check (<see cref="DiffGenerator"/>) to
    /// detect files modified within the same stat window as the index write.
    /// Matches <c>index->stamp</c> populated in <c>git_index_read</c> /
    /// <c>git_indexwriter_commit</c>. <see cref="IndexTime.Zero"/> for
    /// in-memory indexes (no disk file).
    /// </summary>
    private IndexTime _stamp = IndexTime.Zero;

    /// <summary>The owning repository, or null for an in-memory index.</summary>
    internal GitRepository? Owner => _owner;

    /// <summary>
    /// The owning repository, throwing if this is an in-memory index. Used by
    /// disk-writing methods (<see cref="WriteTreeToAsync"/>, etc.) that require
    /// a backing repository.
    /// </summary>
    private GitRepository RequiredOwner => _owner ?? throw new InvalidOperationException("index has no owning repository (in-memory index)");

    /// <summary>The index file path (<c>{gitdir}/index</c>), or null for in-memory.</summary>
    internal string? IndexPath => _indexPath;

    /// <summary>True if the index has unsaved changes (matches <c>index->dirty</c>).</summary>
    internal bool IsDirty => _dirty;

    /// <summary>
    /// The index file's own mtime (the <c>stamp</c>), captured when the index
    /// was last read from or written to disk. <see cref="IndexTime.Zero"/> for
    /// in-memory indexes. Used by the racy-git check
    /// <see cref="EntryNewerThanIndex"/>, matching
    /// <c>git_index_entry_newer_than_index</c> (index.h:101-118).
    /// </summary>
    internal IndexTime Stamp => _stamp;

    /// <summary>
    /// Racy-git check: returns true if <paramref name="entry"/>'s mtime is
    /// newer than OR equal to this index file's own mtime (<see cref="Stamp"/>).
    /// Matches <c>git_index_entry_newer_than_index</c> (index.h:101-118): when
    /// true, the file was modified at or after the index was written, so a
    /// modify-then-revert within the stat window could have happened — the OID
    /// must be recomputed to confirm. Returns false for an in-memory index or
    /// one with a zero stamp (never-written). This is the read-side racy-git
    /// guard used by both <see cref="DiffGenerator"/> (diff generation)
    /// and <c>Checkout.CheckoutContext.IsWorkdirModifiedAsync</c>
    /// (checkout.c:219) — libgit2 applies it at read time, not only at write
    /// time.
    /// </summary>
    internal bool EntryNewerThanIndex(in GitIndexEntry entry)
    {
        if (_stamp.Seconds == 0)
        {
            return false;
        }

        // NSEC=ON path (matching C with GIT_USE_NSEC, the libgit2 default):
        // compare seconds first, then nanoseconds.
        if (_stamp.Seconds < entry.Mtime.Seconds)
        {
            return true;
        }

        if (_stamp.Seconds > entry.Mtime.Seconds)
        {
            return false;
        }

        return _stamp.Nanoseconds <= entry.Mtime.Nanoseconds;
    }

    /// <summary>Marks the index as clean (no unsaved changes).</summary>
    internal void ClearDirty() => _dirty = false;

    /// <summary>Marks the index as dirty (has unsaved changes).</summary>
    internal void MarkDirty() => _dirty = true;

    private bool _disposed;

    private GitIndex(GitHashAlgorithmKind oidType)
    {
        OidType = oidType;
        Version = VersionDefault;
        // C's freshly-allocated git_index has a zero checksum of the right
        // size for the index's hash algorithm (git_oid_clear, index.c:630-634).
        _checksum = GitOid.FromRaw(new byte[GitOid.SizeFor(oidType)], oidType);
    }

    /// <summary>The hash algorithm used for OIDs in this index.</summary>
    public GitHashAlgorithmKind OidType { get; }

    /// <summary>The on-disk format version (2, 3, or 4).</summary>
    public int Version { get; private set; }

    /// <summary>The trailing checksum of the index from the last read or write.
    /// Matches <c>git_index_checksum</c> (index.c:630-634). Zero-OID before any
    /// read or write (matches C's freshly-allocated <c>git_index</c>).</summary>
    public GitOid Checksum => _checksum;

    /// <summary>The parsed tree cache (TREE extension), or <c>null</c> if absent.</summary>
    internal TreeCache? Tree { get; private set; }

    /// <summary>
    /// Sets the owning repository and derives the index file path. Called
    /// once by <see cref="GitRepository.GetIndexAsync"/> getter after opening or
    /// creating the index. Mirrors <c>GitObjectDb.SetOwner</c> /
    /// <c>FileRefBackend.SetOwner</c>. Write methods (<c>AddByPathAsync</c>/
    /// <c>WriteAsync</c>/<c>WriteTreeAsync</c>) require the owner to be set.
    /// </summary>
    internal void SetOwner(GitRepository repo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner = repo;
        // Keep an explicitly opened path (e.g. GIT_INDEX_FILE) — the default
        // gitdir path applies only to fresh indexes.
        _indexPath ??= Path.Join(repo.Path, "index");
    }

    /// <summary>True if the index uses case-insensitive ordering.</summary>
    public bool IgnoreCase
    {
        get => _ignoreCase;
        set
        {
            _ignoreCase = value;
            SortEntries();
            // C (index.c:384-387, git_index__set_ignore_case): the REUC
            // vector's comparator switches with the index and is re-sorted
            // too.
            _reuc.Sort((a, b) => GitPath.Compare(a.Path, b.Path, _ignoreCase));
        }
    }

    /// <summary>Number of entries in the index.</summary>
    public int EntryCount => _entries.Count;

    /// <summary>True if any entry has a non-zero stage (conflict).</summary>
    public bool HasConflicts
    {
        get
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].IsConflict)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Number of REUC (resolve-undo) entries.</summary>
    public int ReucCount => _reuc.Count;

    /// <summary>Number of conflict-name entries.</summary>
    public int NameCount => _names.Count;

    /// <summary>
    /// Opens and parses an index file from disk. Matches <c>git_index__open</c>.
    /// </summary>
    /// <param name="indexPath">Path to the <c>.git/index</c> file.</param>
    /// <param name="oidType">The hash algorithm (SHA-1 or SHA-256).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A parsed <see cref="GitIndex"/>.</returns>
    public static async Task<GitIndex> OpenAsync(string indexPath, GitHashAlgorithmKind oidType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        byte[] buffer = await File.ReadAllBytesAsync(indexPath, cancellationToken).ConfigureAwait(false);
        var index = new GitIndex(oidType);
        index.ParseIndex(buffer);
        // git_index__open sets index_file_path (index.c:416-428) — the path is
        // retained so writes go back to the opened file (e.g. GIT_INDEX_FILE).
        index._indexPath = indexPath;
        index.CaptureStamp(indexPath);
        return index;
    }

    /// <summary>
    /// Re-reads the index file from disk into this instance. Matches
    /// <c>git_index_read</c> (index.c:660-716): the reload happens only when
    /// the file's trailing checksum changed (<c>compare_checksum</c>,
    /// index.c:656-673 — NOT the mtime) or <paramref name="force"/> is set;
    /// a missing file keeps the in-memory entries unless force
    /// (index.c:672-678). On reload the in-memory state (including unsaved
    /// entries) is discarded, the TREE cache is dropped, and the index is
    /// marked clean.
    /// </summary>
    internal async Task ReadFromDiskAsync(bool force, CancellationToken cancellationToken)
    {
        if (IndexPath is null)
        {
            // C (index.c:662-664): "failed to read index: The index is
            // in-memory only", -1 (GIT_ERROR_INDEX).
            throw new GitException(
                GitErrorCode.Error,
                "failed to read index: The index is in-memory only",
                GitErrorCategory.Index);
        }

        bool onDisk = File.Exists(IndexPath);
        if (!onDisk)
        {
            // C (index.c:672-678): with the file absent only force clears the
            // in-memory entries; the index is marked clean either way.
            if (force)
            {
                Clear();
            }

            ClearDirty();
            return;
        }

        bool checksumDiffers;
        try
        {
            // C (index.c:656-673, compare_checksum): compare the file's
            // trailing checksum with the cached one — NOT the mtime.
            byte[] stored = await AsyncFileIO.ReadTailAsync(IndexPath, GitOid.SizeFor(OidType), cancellationToken).ConfigureAwait(false);
            checksumDiffers = !stored.AsSpan().SequenceEqual(_checksum.RawBytes);
        }
        catch (IOException)
        {
            // C (index.c:681-687): a vanished/unreadable file between the
            // checksum probe and the parse → "failed to read index: '%s' no
            // longer exists", GIT_ERROR_INDEX.
            throw new GitException(
                GitErrorCode.Error,
                $"failed to read index: '{IndexPath}' no longer exists",
                GitErrorCategory.Index);
        }

        if (!checksumDiffers && !force)
        {
            return;
        }

        byte[] buffer = await File.ReadAllBytesAsync(IndexPath, cancellationToken).ConfigureAwait(false);

        // C (index.c:694-716): clear entries/REUC/names, drop the TREE
        // cache, re-parse, then mark clean and capture the stamp.
        Clear();
        Tree = null;
        ParseIndex(buffer);
        ClearDirty();
        CaptureStamp(IndexPath);
    }

    /// <summary>
    /// Captures the index file's disk mtime into <see cref="_stamp"/>. Called
    /// after <see cref="OpenAsync"/> (read) and after <see cref="IndexWriter.CommitAsync"/>
    /// (write). Matches <c>git_index_read</c> / <c>git_indexwriter_commit</c>
    /// populating <c>index->stamp</c>.
    /// </summary>
    internal void CaptureStamp(string indexPath)
    {
        try
        {
            var fi = new FileInfo(indexPath);
            if (!fi.Exists)
            {
                _stamp = IndexTime.Zero;
                return;
            }

            _stamp = StatUtil.ToIndexTime(fi.LastWriteTimeUtc);
        }
        catch (IOException)
        {
            _stamp = IndexTime.Zero;
        }
        catch (UnauthorizedAccessException)
        {
            _stamp = IndexTime.Zero;
        }
    }

    /// <summary>
    /// Creates an empty in-memory index (no file). Matches <c>git_index__new</c>.
    /// </summary>
    public static GitIndex New(GitHashAlgorithmKind oidType) => new(oidType);

    /// <summary>
    /// Returns the entry at the given sorted index. Matches
    /// <c>git_index_get_byindex</c>.
    /// </summary>
    public GitIndexEntry EntryByIndex(int index)
    {
        if ((uint)index >= (uint)_entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        EnsureSorted();
        return _entries[index];
    }

    /// <summary> Looks up an entry by path and stage. Matches <c>git_index_get_bypath</c>. </summary> <param name="path">The entry path.</param> <param
    /// name="stage">The conflict stage (0 = normal, 1-3 = conflict). Out-of-range values are masked to 2 bits like C's <c>GIT_INDEX_ENTRY_STAGE_SET</c>
    /// (index.h:111-113): 4 → 0.</param> <returns>The matching entry, or <c>null</c> if not found.</returns>
    public GitIndexEntry? EntryByPath(string path, int stage = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        EnsureSorted();

        return EntriesMap.TryGetValue((GitPath.FromUtf8String(path), stage & 0x3), out GitIndexEntry entry) ? entry : null;
    }

    /// <summary> Looks up an entry by byte-faithful path and stage. The path-taking counterpart of <see cref="EntryByPath(string, int)"/>; avoids the UTF-8
    /// round-trip of the string overload (used internally by tree/index code that already holds a <see cref="GitPath"/>).
    /// Public for byte-faithful callers. The stage is masked to 2 bits like C's <c>GIT_INDEX_ENTRY_STAGE_SET</c>. </summary>
    public GitIndexEntry? EntryByPath(GitPath path, int stage = 0)
    {
        EnsureSorted();
        return EntriesMap.TryGetValue((path, stage & 0x3), out GitIndexEntry entry) ? entry : null;
    }

    /// <summary>
    /// Finds the sorted position of the first entry matching the given path
    /// (any stage). Matches <c>git_index_find</c>.
    /// </summary>
    /// <returns>The index, or -1 if not found.</returns>
    public int Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Find(GitPath.FromUtf8String(path));
    }

    /// <summary> Byte-faithful <see cref="Find(string)"/>. Binary-searches the sorted entries with the case-select path comparator (<c>entries_cmp_path</c>),
    /// then backs up to the first stage for the path — matching <c>git_index_find</c> (<c>index.c:1791-1818</c>).
    /// </summary>
    public int Find(GitPath path)
    {
        EnsureSorted();

        int lo = 0;
        int hi = _entries.Count - 1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = ComparePath(_entries[mid].Path, path);

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else if (cmp > 0)
            {
                hi = mid - 1;
            }
            else
            {
                // Back up to the first entry with this path.
                while (mid > 0 && ComparePath(_entries[mid - 1].Path, path) == 0)
                {
                    mid--;
                }

                return mid;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the first entry whose path starts with the given prefix.
    /// Matches <c>git_index_find_prefix</c>.
    /// </summary>
    public int FindPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return FindPrefix(GitPath.FromUtf8String(prefix));
    }

    /// <summary> Byte-faithful <see cref="FindPrefix(string)"/>. Ports <c>git_index_find_prefix</c> (<c>index.c:1766-1781</c>): the binary search respects <see
    /// cref="IgnoreCase"/> (it uses the staged search path), but the final confirm is the case-sensitive <see cref="LibGit2CS.IO.GitPath.ComparePrefix(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/>
    /// (<c>git__prefixcmp</c>) — matching libgit2 exactly, which always confirms prefixes with the case-sensitive comparator regardless of <c>ignore_case</c>.
    /// </summary>
    public int FindPrefix(GitPath prefix)
    {
        EnsureSorted();

        int lo = 0;
        int hi = _entries.Count - 1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int diff = GitPath.Compare(_entries[mid].Path, prefix, _ignoreCase);

            if (diff < 0)
            {
                lo = mid + 1;
            }
            else if (diff > 0)
            {
                hi = mid - 1;
            }
            else
            {
                return mid; // exact match
            }
        }

        // lo is the insertion point. Confirm with the case-sensitive
        // git__prefixcmp (matches git_index_find_prefix's confirm).
        if (lo < _entries.Count && GitPath.ComparePrefix(_entries[lo].Path, prefix) == 0)
        {
            return lo;
        }

        return -1;
    }

    /// <summary>
    /// Byte-faithful port of <c>git_index__find_pos</c> (<c>index.c:1783-1790</c>
    /// → <c>index_find</c> → <c>git_vector_bsearch2</c> → <c>git__bsearch</c>,
    /// <c>util.c:570-595</c>). Binary-searches the sorted entries with the
    /// case-select path comparator (<see cref="ComparePath"/>, mirroring
    /// <c>entries_search</c> under <c>GIT_INDEX_STAGE_ANY</c> so the stage
    /// tiebreak is skipped) and returns the <b>insertion position</b> — the
    /// index of the first entry whose path sorts at/after
    /// <paramref name="path"/> — <i>whether or not an exact match exists</i>.
    /// This is the key semantic <see cref="FindPrefix(GitPath)"/> does not
    /// reproduce: <c>git_index__find_pos</c> always writes <c>*out</c> and
    /// leaves the prefix confirm to the caller (see
    /// <c>checkout_action_wd_only</c>, checkout.c:391-413, which ignores the
    /// <c>GIT_ENOTFOUND</c> return for tree entries and re-checks with the
    /// diff's <c>pfxcomp</c>). Returns the count of entries (one past the end)
    /// when <paramref name="path"/> sorts after every entry; the caller must
    /// bounds-check before indexing (mirrors C's <c>e != NULL</c> guard on
    /// <c>git_index_get_byindex</c>).
    /// </summary>
    internal int FindInsertionPos(GitPath path)
    {
        EnsureSorted();

        int lo = 0;
        int hi = _entries.Count - 1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = ComparePath(_entries[mid].Path, path);

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else if (cmp > 0)
            {
                hi = mid - 1;
            }
            else
            {
                // Exact match — back up to the first entry with this path
                // (matches git_index_find's stage-backup; harmless under
                // STAGE_ANY since the caller re-confirms with pfxcomp).
                while (mid > 0 && ComparePath(_entries[mid - 1].Path, path) == 0)
                {
                    mid--;
                }

                return mid;
            }
        }

        // lo is the insertion point (git__bsearch sets *position = base - array).
        return lo;
    }

    /// <summary>
    /// Returns a snapshot (sorted copy) of the index entries for safe
    /// iteration. Matches <c>git_index_snapshot_new</c>. The snapshot is
    /// independent of subsequent index modifications.
    /// </summary>
    public IReadOnlyList<GitIndexEntry> Snapshot()
    {
        EnsureSorted();
        return _entries.ToArray();
    }

    /// <summary>
    /// Binary-searches a sorted index snapshot for the entry matching
    /// <paramref name="path"/> and <paramref name="stage"/>. Ports
    /// <c>git_index_snapshot_find</c> (<c>index.c:3840</c>) →
    /// <c>index_find_in_entries</c> (<c>index.c:343</c>) →
    /// <c>git_vector_bsearch2</c>: a binary search over the sorted snapshot
    /// vector using the case-select entry comparator. The comparator
    /// (path-then-stage) is identical to <see cref="CompareEntries"/> /
    /// <c>git_index_entry_srch</c>/<c>git_index_entry_isrch</c>
    /// (<c>index.c:136</c>, <c>index.c:161</c>), so it is consistent with the
    /// snapshot's sort order produced by <see cref="Snapshot"/>. Pass
    /// <paramref name="ignoreCase"/> matching the iterator's case mode so the
    /// search matches the sort (mirrors <c>iter->entry_srch</c> selection at
    /// <c>iterator.c:41</c>). Returns the matching index, or <c>-1</c> if no
    /// entry matches.
    /// </summary>
    /// <remarks>
    /// The snapshot must already be sorted by
    /// <see cref="CompareEntries"/> (<see cref="Snapshot"/> guarantees this).
    /// Caller checks <c>GIT_FILEMODE_COMMIT</c> on the returned entry, as
    /// <c>filesystem_iterator_is_submodule</c> does (<c>iterator.c:1136-1138</c>).
    /// </remarks>
    internal static int SnapshotFind(
        IReadOnlyList<GitIndexEntry> snapshot, GitPath path, int stage, bool ignoreCase)
    {
        int lo = 0, hi = snapshot.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            GitIndexEntry e = snapshot[mid];

            int cmp = GitPath.Compare(path, e.Path, ignoreCase);
            if (cmp == 0)
            {
                cmp = stage - e.Stage;
            }

            if (cmp == 0)
            {
                return mid;
            }

            if (cmp < 0)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return -1;
    }

    /// <summary>Iterates over all entries in sorted order.</summary>
    public IEnumerable<GitIndexEntry> Entries
    {
        get
        {
            EnsureSorted();
            for (int i = 0; i < _entries.Count; i++)
            {
                yield return _entries[i];
            }
        }
    }

    /// <summary>Iterates over REUC (resolve-undo) entries.</summary>
    public IReadOnlyList<GitIndexReucEntry> ReucEntries => _reuc;

    /// <summary>
    /// Iterates over conflict-name entries. C (index.c:2150-2151):
    /// <c>git_index_name_get_byindex</c> SORTS the names vector
    /// (conflict_name_cmp) before indexing, so enumeration is sorted.
    /// </summary>
    public IReadOnlyList<GitIndexNameEntry> NameEntries
    {
        get
        {
            if (_names.Count > 1)
            {
                List<GitIndexNameEntry> sorted = [.. _names];
                sorted.Sort(static (a, b) => ConflictNameCompare(a, b));
                return sorted;
            }

            return _names;
        }
    }

    /// <summary>
    /// Ports <c>conflict_name_cmp</c> (index.c:229-248): ancestor-first
    /// ordering (null ancestors last), then strcmp on ancestors, then
    /// strcmp on "ours" (null ours compare equal).
    /// </summary>
    private static int ConflictNameCompare(GitIndexNameEntry a, GitIndexNameEntry b)
    {
        if (a.Ancestor is not null && b.Ancestor is null)
        {
            return 1;
        }

        if (a.Ancestor is null && b.Ancestor is not null)
        {
            return -1;
        }

        if (a.Ancestor is not null)
        {
            return GitPath.Compare(a.Ancestor.Value, b.Ancestor!.Value);
        }

        if (a.Ours is null || b.Ours is null)
        {
            return 0;
        }

        return GitPath.Compare(a.Ours.Value, b.Ours.Value);
    }

    // ===== In-memory helpers: Add/Remove/ReadTree =====
    // Strictly in-memory: operate on _entries/_entriesMap only. The full
    // disk-writing methods (WriteAsync()/WriteTreeAsync()) are defined further below.
    // These in-memory helpers are used by the apply bridge
    // (GitRepository.ApplyToTreeAsync/ApplyAsync) for ephemeral indexes.

    /// <summary>
    /// Adds or replaces an entry in the index (in-memory only). Matches
    /// <c>git_index_add</c> for the in-memory subset: if an entry with the
    /// same (path, stage) exists, it is replaced; otherwise the new entry is
    /// inserted. Does NOT write to disk.
    /// </summary>
    /// <param name="entry">The entry to add.</param>
    /// <remarks>
    /// Strictly in-memory — operates on <c>_entries</c>/<c>_entriesMap</c>
    /// only. Does not write to disk; use <see cref="WriteAsync"/> or
    /// <see cref="WriteTreeAsync"/> for persistence.
    /// </remarks>
    public void Add(GitIndexEntry entry)
    {
        EnsureSorted();

        // C's
        // git_index_add rejects non-(file/link/commit) modes with 'invalid
        // entry mode' (index.c:1700-1703) — entries with mode 0 or
        // arbitrary 32-bit modes must not enter the index. (C's ODB
        // existence check, index.c:1401-1409, is async-only in the port and
        // cannot run on this sync surface.)
        if (!IsValidIndexMode(entry.Mode))
        {
            throw new GitException(GitErrorCode.Error, "invalid entry mode", GitErrorCategory.Index);
        }

        // C (index.c:1377-1379, index_entry_adjust_namemask): make sure the
        // path-length flag is correct — API-created entries carry Flags=0.
        entry = AdjustNameMask(entry);

        // C (index.c:1411-1413, check_file_directory_collision with
        // ok_to_replace=1): adding a path removes entries under it
        // (has_file_name) and entries that are path prefixes of it
        // (has_dir_name). TREE modes skip the check (index.c:1408-1410).
        if (entry.Mode != GitFileMode.Tree)
        {
            RemoveFileDirectoryCollisions(entry);
        }

        (GitPath, int Stage) key = (entry.Path, entry.Stage);
        Dictionary<(GitPath, int), GitIndexEntry> map = EntriesMap;
        if (map.TryGetValue(key, out GitIndexEntry existing))
        {
            // Replace the existing entry in _entries.
            int idx = _entries.IndexOf(existing);
            _entries[idx] = entry;
            map[key] = entry;
        }
        else
        {
            _entries.Add(entry);
            map[key] = entry;
            _sorted = false;
        }

        InvalidateTreeCache(entry.Path);
        _dirty = true;
    }

    /// <summary>
    /// Sets the 12-bit name-length field of an entry's flags to its path
    /// length. Matches <c>index_entry_adjust_namemask</c> (index.c:918-923);
    /// called on every insert path (git_index_add, read_tree_cb,
    /// git_index_conflict_add, add_all — index.c:1378, 3330, 3474).
    /// </summary>
    private static GitIndexEntry AdjustNameMask(GitIndexEntry entry)
    {
        int pathLength = entry.Path.Span.Length;
        ushort mask = pathLength < GitIndexEntry.NameMask ? (ushort)pathLength : GitIndexEntry.NameMask;
        return entry with { Flags = (ushort)((entry.Flags & ~GitIndexEntry.NameMask) | mask) };
    }

    /// <summary>
    /// Ports <c>has_file_name</c> + <c>has_dir_name</c> with
    /// <c>ok_to_replace = 1</c> (index.c:1126-1200): an added path removes
    /// same-stage entries whose path starts with <c>name + '/'</c>, and
    /// same-stage entries that are exact path prefixes of the new path.
    /// </summary>
    private void RemoveFileDirectoryCollisions(GitIndexEntry entry)
    {
        int stage = entry.Stage;
        ReadOnlySpan<byte> name = entry.Path.Span;

        // has_file_name: entries under the new path (adding "a" removes "a/b").
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            GitIndexEntry p = _entries[i];
            if (p.Stage != stage)
            {
                continue;
            }

            ReadOnlySpan<byte> pBytes = p.Path.Span;
            if (pBytes.Length > name.Length &&
                pBytes[..name.Length].SequenceEqual(name) &&
                pBytes[name.Length] == (byte)'/')
            {
                _entries.RemoveAt(i);
                _entriesMap = null;
            }
        }

        // has_dir_name: entries that are path prefixes (adding "a/b" removes "a").
        for (int slash = name.Length - 1; slash > 0; slash--)
        {
            if (name[slash] != (byte)'/')
            {
                continue;
            }

            var prefix = GitPath.FromUtf8Bytes(name[..slash].ToArray());
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                GitIndexEntry p = _entries[i];
                if (p.Stage == stage && p.Path == prefix)
                {
                    _entries.RemoveAt(i);
                    _entriesMap = null;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Removes the entry for the given path and stage from the index
    /// (in-memory only). Matches <c>git_index_remove</c> for the in-memory
    /// subset. Does NOT write to disk.
    /// </summary>
    /// <param name="path">The entry path.</param>
    /// <param name="stage">The conflict stage (0 = normal).</param>
    /// <returns><c>true</c> if an entry was removed; <c>false</c> if not found.</returns>
    /// <remarks>
    /// Strictly in-memory — operates on <c>_entries</c>/<c>_entriesMap</c>
    /// only. Does not write to disk.
    /// </remarks>
    public bool Remove(string path, int stage = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Remove(GitPath.FromUtf8String(path), stage);
    }

    /// <summary> Byte-faithful <see cref="Remove(string, int)"/>; operates on a <see cref="GitPath"/> key directly (no UTF-8 round-trip). </summary>
    public bool Remove(GitPath path, int stage = 0)
    {
        EnsureSorted();

        (GitPath, int Stage) key = (path, stage);
        Dictionary<(GitPath, int), GitIndexEntry> map = EntriesMap;
        if (!map.TryGetValue(key, out GitIndexEntry existing))
        {
            return false;
        }

        _entries.Remove(existing);
        map.Remove(key);
        InvalidateTreeCache(existing.Path);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// Replaces the index contents with the blobs from a tree. Matches
    /// <c>git_index_read_tree</c> (index.c:3341-3397). Clears all entries,
    /// extensions, and tree cache, then walks the tree (POST-order) creating
    /// an <see cref="GitIndexEntry"/> for each blob. Stat fields are preserved
    /// from matching old entries (same path + OID + mode). Builds the tree
    /// cache from the tree.
    /// </summary>
    /// <param name="tree">The tree to read into the index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask ReadTreeAsync(GitTree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // Build a lookup of old entries by path for stat cache preservation.
        var oldByPath = new Dictionary<GitPath, GitIndexEntry>(
            _ignoreCase ? GitPathComparer.CaseInsensitive : GitPathComparer.CaseSensitive);
        foreach (GitIndexEntry e in _entries)
        {
            if (e.Stage == 0)
            {
                oldByPath[e.Path] = e;
            }
        }

        _entries.Clear();
        _entriesMap = null;
        _reuc.Clear();
        _names.Clear();

        await foreach ((GitPath path, GitTreeEntry entry) in tree.WalkAsync(GitTreeWalkMode.PostOrder, cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsTree)
            {
                continue;
            }

            // Preserve stat fields from matching old entry (same path + OID +
            // mode). C (index.c:3326-3327, read_tree_cb) copies the old entry
            // and then ZEROES flags_extended — in-memory skip-worktree /
            // intent-to-add bits are dropped.
            if (oldByPath.TryGetValue(path, out GitIndexEntry old) &&
                old.Id == entry.Id &&
                old.Mode == entry.Mode)
            {
                _entries.Add(old.FlagsExtended == 0 ? old : old with { FlagsExtended = 0 });
            }
            else
            {
                _entries.Add(AdjustNameMask(new GitIndexEntry(path, entry.Id, entry.Mode)));
            }
        }

        SortEntries();

        // Build tree cache from the tree.
        Tree = await TreeCache.ReadTreeAsync(tree, OidType, cancellationToken).ConfigureAwait(false);
        _dirty = true;
    }

    /// <summary>
    /// Merges entries from another index into this one. Matches
    /// <c>git_index_read_index</c> (index.c:3537-3557). For each entry in
    /// <paramref name="source"/>: if this index has a matching entry (same path,
    /// OID, and mode), the stat fields are preserved; otherwise the source
    /// entry replaces it. Conflicts, REUC, and names are also merged.
    /// </summary>
    /// <param name="source">The index to read from.</param>
    public void ReadIndex(GitIndex source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Build a lookup of current entries by (path, stage) for stat preservation.
        EnsureSorted();
        var currentMap = new Dictionary<(GitPath, int), GitIndexEntry>(
            EntriesMap.Count,
            _ignoreCase ? IndexEntryKeyComparer.CaseInsensitive : IndexEntryKeyComparer.CaseSensitive);
        foreach (GitIndexEntry e in _entries)
        {
            currentMap[(e.Path, e.Stage)] = e;
        }

        _entries.Clear();
        _entriesMap = null;

        foreach (GitIndexEntry srcEntry in source.Entries)
        {
            (GitPath, int Stage) key = (srcEntry.Path, srcEntry.Stage);

            // If we have a matching entry (same path, OID, mode), preserve stat.
            if (currentMap.TryGetValue(key, out GitIndexEntry old) &&
                old.Id == srcEntry.Id &&
                old.Mode == srcEntry.Mode)
            {
                _entries.Add(old);
            }
            else
            {
                // C's
                // git_index_read_iterator uses index_entry_dup_nocache for
                // differing/new entries (index.c:3471), which copies only
                // id/mode/flags and leaves stat fields zeroed, so the
                // racy-git/stat-cache logic cannot treat a workdir file as
                // unchanged where C forces a re-stat.
                _entries.Add(srcEntry with
                {
                    Ctime = default,
                    Mtime = default,
                    Dev = 0,
                    Ino = 0,
                    Uid = 0,
                    Gid = 0,
                    FileSize = 0,
                });
            }
        }

        // C (index.c:3510-3521, git_index_read_iterator): the target's NAME
        // and REUC lists are CLEARED (git_index_name_clear +
        // git_index_reuc_clear) — they are never merged from the source —
        // and the target keeps its own tree cache (invalidated along the
        // way).
        _names.Clear();
        _reuc.Clear();
        Tree = null;

        SortEntries();
        _dirty = true;
    }

    // ===== Write side: WriteTree / WriteTreeTo / SetVersion =====

    /// <summary>
    /// Writes the index as a tree object to the owning repository's ODB.
    /// Matches <c>git_index_write_tree</c> (index.c:839-853). The index must
    /// not have conflicts.
    /// </summary>
    /// <returns>The OID of the root tree.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Unmerged"/> if the index has conflicts.
    /// <see cref="GitErrorCode.Invalid"/> if the index has no owning repository.
    /// </exception>
    public async Task<GitOid> WriteTreeAsync(CancellationToken cancellationToken = default)
    {
        EnsureOwner();
        return await GitTree.WriteIndexAsync(RequiredOwner, this, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the index as a tree object to the specified repository's ODB.
    /// Matches <c>git_index_write_tree_to</c> (index.c:855-863).
    /// </summary>
    /// <param name="repo">The repository to write the tree to.</param>
    /// <returns>The OID of the root tree.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Unmerged"/> if the index has conflicts.
    /// </exception>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitOid> WriteTreeToAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return await GitTree.WriteIndexAsync(repo, this, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Sets the index format version. Matches <c>git_index_set_version</c> (index.c:802-815). Accepts v2 (default), v3 (extended flags), and v4
    /// (path-prefix compression). </summary> <param name="version">The version to set (2, 3, or 4).</param> <exception cref="GitException"> <see
    /// cref="GitErrorCode.Error"/> with the index category if <paramref name="version"/> is not 2, 3, or 4 — C's GIT_ERROR_INDEX "invalid version number".
    /// </exception>
    public void SetVersion(int version)
    {
        if (version is not VersionDefault and not VersionExt and not VersionComp)
        {
            throw new GitException(
                GitErrorCode.Error,
                "invalid version number",
                GitErrorCategory.Index);
        }

        Version = version;
        _dirty = true;
    }

    /// <summary>
    /// Fills the index with a set of entries (bulk copy). Matches
    /// <c>git_index__fill</c> (index.c:1653-1682). Used by stash save to
    /// copy the repo index entries into an ephemeral in-memory index.
    /// </summary>
    /// <param name="entries">The entries to insert (stage-0 only).</param>
    internal void Fill(IEnumerable<GitIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (GitIndexEntry entry in entries)
        {
            Add(entry);
        }
    }

    // ===== Write side: AddByPath / AddFromBuffer / RemoveByPath / RemoveDirectory =====

    /// <summary> Adds or updates a file from the working directory to the index. Matches <c>git_index_add_bypath</c> (index.c:1578-1632). Stats the file,
    /// applies clean filters (CRLF/ident/custom), writes the filtered blob to the ODB, creates an <see cref="GitIndexEntry"/>, and inserts it. </summary>
    /// <param name="path">The path relative to the repo workdir (forward slashes).</param> <exception cref="GitException"> <see cref="GitErrorCode.Directory"/>
    /// if <paramref name="path"/> is a directory. <see cref="GitErrorCode.NotFound"/> if the file does not exist. <see cref="GitErrorCode.Invalid"/> if the
    /// path fails validation (e.g. contains <c>.git</c>). </exception> <summary> Adds a file from the working directory to the index. Matches
    /// <c>git_index_add_bypath</c> (index.c:1578-1632). Byte-faithful: the FS-boundary <c>Path.Join</c> routes through <see
    /// cref="GitPath.ToFileSystemString"/>; the <c>GitFilterList.LoadAsync</c> call uses the <see cref="GitPath"/> overload (no UTF-8 round-trip on the
    /// attribute path). The <c>string</c> overload delegates via <see cref="GitPath.FromUtf8String"/>. </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AddByPathAsync(GitPath path, CancellationToken cancellationToken = default)
    {
        EnsureOwner();

        GitRepository repo = RequiredOwner;
        string workdir = repo.Workdir
            ?? throw new GitException(GitErrorCode.BareRepo, "cannot add by path on a bare repository", GitErrorCategory.Index);

        // Validate path before any filesystem access.
        if (!await GitPathValidator.IsValidAsync(path, GitPathRejectFlags.IndexDefaults | PathRejectPresets.FilesystemDefaults, repo, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"path '{path.ToUtf8String()}' is not valid",
                GitErrorCategory.Index);
        }

        // FS boundary: single transcode point.
        string fullPath = Path.Join(workdir, path.ToFileSystemString());
        var fi = new FileInfo(fullPath);

        // lstat-style existence (as in
        // libgit2 1.9.4): a symlink — even one
        // pointing at a directory — is a FILE entry. FileInfo.Exists follows
        // the link (false for a symlink-to-directory), so the link itself is
        // detected via LinkTarget (which never follows) before the directory
        // fallback; C's git_index_add_bypath stats via p_lstat (index.c:1584).
        bool isSymlink = fi.LinkTarget is not null;
        if (!isSymlink && !fi.Exists)
        {
            // Check if it's a directory.
            if (Directory.Exists(fullPath))
            {
                throw new GitException(
                    GitErrorCode.Directory,
                    $"could not add '{path.ToUtf8String()}' — it is a directory",
                    GitErrorCategory.Index);
            }

            throw new GitException(
                GitErrorCode.NotFound,
                $"could not add '{path.ToUtf8String()}' — file does not exist",
                GitErrorCategory.Index);
        }

        // Determine mode and stat info.
        GitFileMode mode = StatUtil.GetFileMode(fi);
        (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size) = StatUtil.GetStatInfoForIndex(fi); // C index-write path stores st_rdev (index.c:909)

        // Read content and write blob to ODB.
        // For symlinks, the blob content is the link target.
        ReadOnlyMemory<byte> content;
        if (mode == GitFileMode.Symlink)
        {
            string? target = fi.LinkTarget;
            target ??= await AsyncFileIO.ReadAllTextWithNoBomAsync(fullPath, cancellationToken).ConfigureAwait(false);

            content = Encoding.UTF8.GetBytes(target);
        }
        else
        {
            content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        // Apply workdir→ODB (clean) filters before hashing. Matches
        // git_index_add_bypath (index.c:1578-1632) which calls
        // git_filter_list_load + apply. When no filters apply (no
        // text/crlf/eol/ident attrs), FilterList.Load returns null.
        GitFilterList? filters = await GitFilterList.LoadAsync(repo, path, null, GitFilterMode.ToOdb, GitFilterListFlags.None, attrCommitId: null, cancellationToken).ConfigureAwait(false);
        if (filters is not null)
        {
            content = await filters.ApplyToBufferAsync(content, cancellationToken).ConfigureAwait(false);
        }

        GitOid oid = await repo.Objects.WriteAsync(GitObjectType.Blob, content, cancellationToken).ConfigureAwait(false);

        // Build the entry.
        var entry = new GitIndexEntry
        {
            Path = path,
            Id = oid,
            Mode = mode,
            Ctime = ctime,
            Mtime = mtime,
            Dev = dev,
            Ino = ino,
            Uid = uid,
            Gid = gid,
            FileSize = size,
            Flags = 0,
            FlagsExtended = 0,
        };

        // Insert, resolve conflicts, and invalidate tree cache.
        Add(entry);
        ConflictToReuc(path);
        InvalidateTreeCache(path);

        _dirty = true;
    }

    /// <summary>Matches C's <c>valid_filemode</c> (index.c:1484-1487):
    /// is_file_or_link || COMMIT — Tree is NOT valid for index entries.
    /// </summary>
    private static bool IsValidIndexMode(GitFileMode mode)
        => mode is GitFileMode.Regular
            or GitFileMode.Executable
            or GitFileMode.Symlink
            or GitFileMode.GitLink;

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. The <see cref="GitPath"/> overload is primary; this overload keeps
    /// existing <c>string</c> callers compiling. </summary>
    public Task AddByPathAsync(string path, CancellationToken cancellationToken = default)
        => AddByPathAsync(GitPath.FromUtf8String(path), cancellationToken);

    /// <summary>
    /// Adds or updates an entry from an in-memory buffer. Matches
    /// <c>git_index_add_from_buffer</c> (index.c:1489-1536). Writes the buffer
    /// verbatim as a blob to the ODB without clean filters, then inserts the
    /// entry with zeroed stat fields.
    /// </summary>
    /// <param name="entry">The entry template (path, mode, flags). Stat fields and OID are overwritten.</param>
    /// <param name="buffer">The blob content. Keep its backing storage unchanged until the operation completes.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> if the mode is not a file or link mode.
    /// </exception>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AddFromBufferAsync(GitIndexEntry entry, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureOwner();

        // Validate mode: must be blob, executable, or symlink.
        if (entry.Mode is not GitFileMode.Regular and
            not GitFileMode.Executable and
            not GitFileMode.Symlink)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"cannot add from buffer with mode {entry.Mode}",
                GitErrorCategory.Index);
        }

        GitRepository repo = RequiredOwner;

        // Validate path.
        if (!await GitPathValidator.IsValidAsync(entry.Path, GitPathRejectFlags.IndexDefaults | PathRejectPresets.FilesystemDefaults, repo, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"path '{entry.Path.ToUtf8String()}' is not valid",
                GitErrorCategory.Index);
        }

        // C (index.c:1518, git_index_add_from_buffer): the caller's buffer is
        // hashed VERBATIM — no clean filters are applied.
        GitOid oid = await repo.Objects.WriteAsync(GitObjectType.Blob, buffer, cancellationToken).ConfigureAwait(false);

        // Build entry with zeroed stat (trust_mode = true in C).
        GitIndexEntry newEntry = entry with
        {
            Id = oid,
            Ctime = IndexTime.Zero,
            Mtime = IndexTime.Zero,
            Dev = 0,
            Ino = 0,
            Uid = 0,
            Gid = 0,
            FileSize = (uint)buffer.Length,
        };

        Add(newEntry);
        ConflictToReuc(entry.Path);
        InvalidateTreeCache(entry.Path);
        _dirty = true;
    }

    /// <summary>
    /// Removes the stage-0 entry for the given path and promotes any conflict
    /// entries (stages 1-3) to REUC, removing them in the process. A path with
    /// only conflict entries (no stage 0) is still fully cleared. Matches
    /// <c>git_index_remove_bypath</c> (index.c:1634-1651), which calls
    /// <c>git_index_remove(path, 0)</c> then <c>git_index__conflict_to_reuc</c>
    /// (the latter reads the conflict entries, builds the REUC row, and only
    /// then removes them).
    /// </summary>
    /// <param name="path">The path to remove (relative to repo workdir).</param>
    /// <returns>True if any entry was removed.</returns>
    public bool RemoveByPath(GitPath path)
    {
        // Remove stage 0 first (tolerated if absent). ConflictToReuc then reads
        // stages 1-3, builds the REUC entry, and removes the conflict stages
        // itself — so it MUST run before any stage 1-3 removal, otherwise the
        // REUC data is lost (matches C's ordering: remove(0) → conflict_to_reuc).
        // Uses the GitPath overloads of Remove/ConflictToReuc/
        // InvalidateTreeCache directly (no string round-trip).
        bool removed = Remove(path, stage: 0);
        bool conflictRemoved = ConflictToReuc(path) >= 0;

        if (removed || conflictRemoved)
        {
            InvalidateTreeCache(path);
            _dirty = true;
        }

        return removed || conflictRemoved;
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. </summary>
    public bool RemoveByPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return RemoveByPath(GitPath.FromUtf8String(path));
    }

    /// <summary>
    /// Removes all entries under the given directory prefix. Matches
    /// <c>git_index_remove_directory</c> (index.c:1735-1764).
    /// </summary>
    /// <param name="dir">The directory prefix (e.g. <c>src/subdir</c>).</param>
    /// <param name="stage">The conflict stage to match (−1 = all stages).</param>
    public void RemoveDirectory(string dir, int stage = -1)
    {
        ArgumentNullException.ThrowIfNull(dir);
        RemoveDirectory(GitPath.FromUtf8String(dir), stage);
    }

    /// <summary> Byte-faithful <see cref="RemoveDirectory(string, int)"/>. </summary>
    public void RemoveDirectory(GitPath dir, int stage = -1)
    {
        EnsureSorted();

        // Normalize: ensure trailing '/' (matches git_fs_path_to_dir). Work in
        // bytes so non-UTF-8 prefixes round-trip exactly.
        GitPath dirPath = dir;
        Span<byte> buf = stackalloc byte[dirPath.Length + 1];
        dirPath.Span.CopyTo(buf);
        if (dirPath.Length == 0 || buf[dirPath.Length - 1] != (byte)'/')
        {
            buf[dirPath.Length] = (byte)'/';
            dirPath = GitPath.FromUtf8Bytes(buf[..(dirPath.Length + 1)].ToArray());
        }

        // Locate the first entry whose path sorts at/after the directory prefix
        // (binary search on the sorted entries), then walk forward while the
        // case-sensitive prefix still matches — mirroring git_index_remove_directory
        // (index_find + git__prefixcmp confirm + forward walk).
        int i = FindPrefix(dirPath);
        if (i < 0)
        {
            _dirty = true;
            return;
        }

        while (i < _entries.Count)
        {
            GitIndexEntry entry = _entries[i];
            if (GitPath.ComparePrefix(entry.Path, dirPath) != 0)
            {
                break;
            }

            // C (index.c:1751-1754): the loop matches the stage EXACTLY —
            // with GIT_INDEX_STAGE_ANY (-1) no entry matches, so the C
            // removes nothing (the "-1 removes all" doc is a C bug the port
            // reproduces).
            if (entry.Stage != stage)
            {
                i++;
                continue;
            }

            _entries.RemoveAt(i);
            _entriesMap = null;
            InvalidateTreeCache(entry.Path);
        }

        _dirty = true;
    }

    // ===== Write side: AddAll / RemoveAll / UpdateAll =====

    /// <summary>
    /// Adds or updates all working directory entries matching the pathspec.
    /// Matches <c>git_index_add_all</c> (index.c:3571-3608). Untracked files
    /// are added; deleted files are removed. Ignored files are skipped unless
    /// <see cref="GitIndexAddOptions.Force"/> is set.
    /// </summary>
    /// <param name="pathspec">The pathspec to match (or <c>null</c> for all paths).</param>
    /// <param name="options">Add options (Force, DisablePathSpecMatch).</param>
    /// <param name="callback">Optional callback: return <c>true</c> to skip a path, <c>false</c> to proceed.</param>
    /// <returns>The list of processed paths.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<string>> AddAllAsync(
        GitPathSpec? pathspec,
        GitIndexAddOptions options = GitIndexAddOptions.Default,
        Func<string, string, bool>? callback = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner();
        GitRepository repo = RequiredOwner;
        if (repo.Workdir is null)
        {
            throw new GitException(GitErrorCode.BareRepo, "cannot add all on a bare repository", GitErrorCategory.Index);
        }

        var result = new List<string>();

        // Build diff options: include untracked + typechange.
        GitDiffOptionsFlags diffFlags = GitDiffOptionsFlags.IncludeTypechange | GitDiffOptionsFlags.IncludeUntracked | GitDiffOptionsFlags.RecurseUntrackedDirs;
        if ((options & GitIndexAddOptions.Force) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.IncludeIgnored | GitDiffOptionsFlags.RecurseIgnoredDirs;
        }

        var diffOpts = new GitDiffOptions { Flags = diffFlags };
        GitDiff diff = await GitDiff.IndexToWorkdirAsync(repo, diffOpts, cancellationToken).ConfigureAwait(false);

        GitPathSpec.MatchFlags matchFlags = GitPathSpec.MatchFlags.Default;
        if (_ignoreCase)
        {
            matchFlags |= GitPathSpec.MatchFlags.IgnoreCase;
        }

        if ((options & GitIndexAddOptions.DisablePathSpecMatch) != 0)
        {
            matchFlags |= GitPathSpec.MatchFlags.NoGlob;
        }

        for (int i = 0; i < diff.DeltaCount; i++)
        {
            GitDiffDelta delta = diff.GetDelta(i);
            GitPath path = delta.DeltaPath ?? default;

            // Pathspec matching. byte-faithful GitPath overload (C's git_pathspec_matches_path takes the raw path bytes, pathspec.c:285-296) — the string
            // bridge would decode-then-re-encode, corrupting non-UTF-8 index paths.
            if (pathspec is not null && !pathspec.MatchesPath(matchFlags, path))
            {
                continue;
            }

            // Callback.
            if (callback is not null && callback(path.ToUtf8String(), path.ToUtf8String()))
            {
                continue;
            }

            // If the file was deleted (not in workdir), remove from index.
            if (delta.Status == GitDeltaStatus.Deleted)
            {
                RemoveByPath(path);
            }
            else
            {
                // Add the file from workdir.
                try
                {
                    await AddByPathAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
                {
                    // File was deleted between diff and add — skip.
                }
            }

            result.Add(path.ToUtf8String());
        }

        return result;
    }

    /// <summary>
    /// Removes all index entries matching the pathspec. Matches
    /// <c>git_index_remove_all</c> (index.c:3786-3799).
    /// </summary>
    /// <param name="pathspec">The pathspec to match (or <c>null</c> for all paths).</param>
    /// <param name="callback">Optional callback: return <c>true</c> to skip a path, <c>false</c> to proceed.</param>
    /// <returns>The list of removed paths.</returns>
    public IReadOnlyList<string> RemoveAll(
        GitPathSpec? pathspec,
        Func<string, string, bool>? callback = null)
    {
        EnsureSorted();

        GitPathSpec.MatchFlags matchFlags = _ignoreCase ? GitPathSpec.MatchFlags.IgnoreCase : GitPathSpec.MatchFlags.Default;
        var result = new List<string>();

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            GitIndexEntry entry = _entries[i];
            GitPath path = entry.Path;

            // byte-faithful GitPath overload (C's git_pathspec_matches_path takes the raw path bytes).
            if (pathspec is not null && !pathspec.MatchesPath(matchFlags, path))
            {
                continue;
            }

            if (callback is not null && callback(path.ToUtf8String(), path.ToUtf8String()))
            {
                continue;
            }

            _entries.RemoveAt(i);
            _entriesMap = null;
            InvalidateTreeCache(path);
            result.Add(path.ToUtf8String());
        }

        if (result.Count > 0)
        {
            _dirty = true;
        }

        return result;
    }

    /// <summary>
    /// Updates index entries to match the working directory (like
    /// <c>git add -u</c>). Matches <c>git_index_update_all</c>
    /// (index.c:3801-3812). Only files already in the index are updated;
    /// untracked files are NOT added.
    /// </summary>
    /// <param name="pathspec">The pathspec to match (or <c>null</c> for all paths).</param>
    /// <param name="callback">Optional callback: return <c>true</c> to skip a path, <c>false</c> to proceed.</param>
    /// <returns>The list of updated paths.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<string>> UpdateAllAsync(
        GitPathSpec? pathspec,
        Func<string, string, bool>? callback = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner();
        GitRepository repo = RequiredOwner;

        var result = new List<string>();

        // Update only diffs index vs workdir (no untracked).
        GitDiff diff = await GitDiff.IndexToWorkdirAsync(repo, new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeTypechange,
        }, cancellationToken).ConfigureAwait(false);

        GitPathSpec.MatchFlags matchFlags = _ignoreCase ? GitPathSpec.MatchFlags.IgnoreCase : GitPathSpec.MatchFlags.Default;

        for (int i = 0; i < diff.DeltaCount; i++)
        {
            GitDiffDelta delta = diff.GetDelta(i);
            GitPath path = delta.DeltaPath ?? default;

            // byte-faithful GitPath overload (C's git_pathspec_matches_path takes the raw path bytes).
            if (pathspec is not null && !pathspec.MatchesPath(matchFlags, path))
            {
                continue;
            }

            if (callback is not null && callback(path.ToUtf8String(), path.ToUtf8String()))
            {
                continue;
            }

            if (delta.Status == GitDeltaStatus.Deleted)
            {
                RemoveByPath(path);
            }
            else
            {
                try
                {
                    await AddByPathAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
                {
                    // File was deleted between diff and add.
                }
            }

            result.Add(path.ToUtf8String());
        }

        return result;
    }

    /// <summary>
    /// Adds conflict entries (stages 1, 2, 3) for a path. Matches
    /// <c>git_index_conflict_add</c> (index.c:1820-1886). Removes existing
    /// stage-0 entry at the same path first.
    /// </summary>
    /// <param name="ancestor">Ancestor stage entry (stage 1), or null.</param>
    /// <param name="ours">Our stage entry (stage 2), or null.</param>
    /// <param name="theirs">Their stage entry (stage 3), or null.</param>
    public void ConflictAdd(GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs)
    {
        GitIndexEntry?[] entries = [ancestor, ours, theirs];

        // C (index.c:1840-1845): every non-null entry's mode must be valid —
        // "invalid filemode for stage %d entry".
        for (int i = 0; i < 3; i++)
        {
            if (entries[i] is { } e && !IsValidIndexMode(e.Mode))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"invalid filemode for stage {i + 1} entry",
                    GitErrorCategory.Index);
            }
        }

        // C (index.c:1847-1852): the stage-0 entry is removed at the path of
        // EACH non-null entry — the three sides may have different paths.
        for (int i = 0; i < 3; i++)
        {
            if (entries[i] is { } e)
            {
                Remove(e.Path, stage: 0);
            }
        }

        for (int i = 0; i < 3; i++)
        {
            GitIndexEntry? staged = entries[i];
            if (staged is null)
            {
                continue;
            }

            // Force the stage to i+1 (1=ancestor, 2=ours, 3=theirs).
            int stage = i + 1;
            GitIndexEntry entry = staged.Value.WithStage(stage);
            Add(entry);
        }

        for (int i = 0; i < 3; i++)
        {
            if (entries[i] is { } nonNull)
            {
                InvalidateTreeCache(nonNull.Path);
            }
        }

        _dirty = true;
    }

    /// <summary>
    /// Gets the conflict entries for a path. Matches
    /// <c>git_index_conflict_get</c> (index.c:1939-1969).
    /// </summary>
    /// <param name="path">The conflict path.</param>
    /// <returns>A tuple of (ancestor, ours, theirs) entries, or nulls for absent stages.</returns>
    public (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) ConflictGet(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ConflictGet(GitPath.FromUtf8String(path));
    }

    /// <summary> Byte-faithful <see cref="ConflictGet(string)"/>; looks up the three conflict stages by <see cref="GitPath"/> key (no UTF-8 round-trip). </summary>
    public (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) ConflictGet(GitPath path)
    {
        EnsureSorted();

        GitIndexEntry? ancestor = null, ours = null, theirs = null;

        for (int stage = 1; stage <= 3; stage++)
        {
            if (EntriesMap.TryGetValue((path, stage), out GitIndexEntry entry))
            {
                switch (stage)
                {
                    case 1: ancestor = entry; break;
                    case 2: ours = entry; break;
                    case 3: theirs = entry; break;
                }
            }
        }

        if (ancestor is null && ours is null && theirs is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"no conflict at path '{path.ToUtf8String()}'",
                GitErrorCategory.Index);
        }

        return (ancestor, ours, theirs);
    }

    /// <summary>
    /// Removes all conflict entries (stages > 0) for a path. Matches
    /// <c>git_index_conflict_remove</c> (index.c:1998-2003).
    /// </summary>
    public void ConflictRemove(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ConflictRemove(GitPath.FromUtf8String(path));
    }

    /// <summary>Byte-faithful <see cref="ConflictRemove(string)"/>.</summary>
    public void ConflictRemove(GitPath path)
    {
        EnsureSorted();

        for (int stage = 3; stage >= 1; stage--)
        {
            (GitPath, int Stage) key = (path, stage);
            Dictionary<(GitPath, int), GitIndexEntry> map = EntriesMap;
            if (map.TryGetValue(key, out GitIndexEntry entry))
            {
                _entries.Remove(entry);
                map.Remove(key);
            }
        }

        _dirty = true;
    }

    /// <summary>Enumerates conflict entries (stages 1/2/3) grouped by path.
    /// Matches <c>git_index_conflict_iterator_new</c>/<c>git_index_conflict_next</c>
    /// (index.c:2073-2128) over <c>index_conflict__get_byindex</c>
    /// (index.c:1888-1937). Stage-0 entries are skipped; consecutive conflict
    /// entries for the same path are grouped into one
    /// (<c>ancestor</c>, <c>ours</c>, <c>theirs</c>) triple. A missing side is
    /// <c>null</c>. The path boundary uses the index's case-sensitivity
    /// (<c>entries_cmp_path</c>, <see cref="IgnoreCase"/>).</summary>
    public IEnumerable<(GitIndexEntry? Ancestor, GitIndexEntry? Ours, GitIndexEntry? Theirs)> EnumerateConflicts()
    {
        EnsureSorted();
        int i = 0;
        while (i < _entries.Count)
        {
            if (!_entries[i].IsConflict)
            {
                i++;
                continue;
            }

            GitPath path = _entries[i].Path;
            GitIndexEntry? ancestor = null, ours = null, theirs = null;
            while (i < _entries.Count
                   && _entries[i].IsConflict
                   && GitPath.Compare(_entries[i].Path, path, _ignoreCase) == 0)
            {
                int stage = _entries[i].Stage;
                if (stage == 1)
                {
                    ancestor = _entries[i];
                }
                else if (stage == 2)
                {
                    ours = _entries[i];
                }
                else if (stage == 3)
                {
                    theirs = _entries[i];
                }

                i++;
            }

            yield return (ancestor, ours, theirs);
        }
    }

    /// <summary>
    /// Removes ALL conflict entries (all stages > 0). Matches
    /// <c>git_index_conflict_cleanup</c> (index.c:2005-2009).
    /// </summary>
    public void ConflictCleanup()
    {
        EnsureSorted();
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].IsConflict)
            {
                _entries.RemoveAt(i);
            }
        }

        _entriesMap = null;
        _dirty = true;
    }

    /// <summary>
    /// If a path has conflict entries (stages 1-3), moves them to the REUC
    /// extension and removes the conflict entries. Matches
    /// <c>git_index__conflict_to_reuc</c> (index.c:1451-1475). Called after
    /// adding a stage-0 entry to signal that the conflict is resolved.
    /// </summary>
    /// <returns>0 on success, -1 if no conflict existed (not an error).</returns>
    internal int ConflictToReuc(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ConflictToReuc(GitPath.FromUtf8String(path));
    }

    /// <summary>Byte-faithful <see cref="ConflictToReuc(string)"/>.</summary>
    public int ConflictToReuc(GitPath path)
    {
        EnsureSorted();

        // Check if any conflict entries exist for this path.
        bool hasConflict = false;
        GitIndexEntry? ancestor = null, ours = null, theirs = null;

        for (int stage = 1; stage <= 3; stage++)
        {
            if (EntriesMap.TryGetValue((path, stage), out GitIndexEntry entry))
            {
                hasConflict = true;
                switch (stage)
                {
                    case 1: ancestor = entry; break;
                    case 2: ours = entry; break;
                    case 3: theirs = entry; break;
                }
            }
        }

        if (!hasConflict)
        {
            return -1;
        }

        // Create a REUC entry from the conflict data.
        uint[] modes = new uint[3];
        var oids = new GitOid[3];

        if (ancestor is not null)
        {
            modes[0] = (uint)ancestor.Value.Mode;
            oids[0] = ancestor.Value.Id;
        }
        if (ours is not null)
        {
            modes[1] = (uint)ours.Value.Mode;
            oids[1] = ours.Value.Id;
        }
        if (theirs is not null)
        {
            modes[2] = (uint)theirs.Value.Mode;
            oids[2] = theirs.Value.Id;
        }

        ReucAdd(path, modes[0], oids[0], modes[1], oids[1], modes[2], oids[2]);
        ConflictRemove(path);
        return 0;
    }

    /// <summary>
    /// Adds a resolve-undo (REUC) entry. Matches <c>git_index_reuc_add</c>
    /// (index.c:2233-2250). If a REUC entry with the same path exists, it is
    /// replaced.
    /// </summary>
    public void ReucAdd(string path, uint ancestorMode, GitOid ancestorOid, uint ourMode, GitOid ourOid, uint theirMode, GitOid theirOid)
    {
        ArgumentNullException.ThrowIfNull(path);
        ReucAdd(GitPath.FromUtf8String(path), ancestorMode, ancestorOid, ourMode, ourOid, theirMode, theirOid);
    }

    /// <summary> Byte-faithful <see cref="ReucAdd(string, uint, GitOid, uint, GitOid, uint, GitOid)"/>; the REUC vector is sorted by the case-select path
    /// comparator (<c>reuc_cmp</c> / <c>reuc_icmp</c>, <c>index.c:284-300</c>) and searched byte-wise. </summary>
    public void ReucAdd(GitPath path, uint ancestorMode, GitOid ancestorOid, uint ourMode, GitOid ourOid, uint theirMode, GitOid theirOid)
    {
        // Remove existing entry with the same path (case-select compare).
        for (int i = 0; i < _reuc.Count; i++)
        {
            int cmp = GitPath.Compare(_reuc[i].Path, path, _ignoreCase);

            if (cmp == 0)
            {
                _reuc.RemoveAt(i);
                break;
            }

            if (cmp > 0)
            {
                break;
            }
        }

        var entry = new GitIndexReucEntry(
            path,
            [ancestorMode, ourMode, theirMode],
            [ancestorOid, ourOid, theirOid]);

        // Insert sorted.
        bool inserted = false;
        for (int i = 0; i < _reuc.Count; i++)
        {
            int cmp = GitPath.Compare(_reuc[i].Path, path, _ignoreCase);

            if (cmp > 0)
            {
                _reuc.Insert(i, entry);
                inserted = true;
                break;
            }
        }

        if (!inserted)
        {
            _reuc.Add(entry);
        }

        _dirty = true;
    }

    /// <summary>
    /// Removes a REUC entry by index. Matches <c>git_index_reuc_remove</c>
    /// (index.c:2285-2301).
    /// </summary>
    public void ReucRemove(int index)
    {
        if ((uint)index >= (uint)_reuc.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _reuc.RemoveAt(index);
        _dirty = true;
    }

    /// <summary>
    /// Clears all REUC entries. Matches <c>git_index_reuc_clear</c>
    /// (index.c:2303-2317).
    /// </summary>
    public void ReucClear()
    {
        _reuc.Clear();
        _dirty = true;
    }

    /// <summary>
    /// Adds a conflict-name (NAME) entry. Matches <c>git_index_name_add</c>
    /// (index.c:2164-2185). At least two of the three paths must be non-null.
    /// </summary>
    public void NameAdd(string? ancestor, string? ours, string? theirs)
    {
        NameAdd(
            ancestor is null ? null : GitPath.FromUtf8String(ancestor),
            ours is null ? null : GitPath.FromUtf8String(ours),
            theirs is null ? null : GitPath.FromUtf8String(theirs));
    }

    /// <summary> Byte-faithful overload of <see cref="NameAdd(string?, string?, string?)"/>. </summary>
    public void NameAdd(GitPath? ancestor, GitPath? ours, GitPath? theirs)
    {
        int nonNullCount = (ancestor is not null ? 1 : 0) + (ours is not null ? 1 : 0) + (theirs is not null ? 1 : 0);
        if (nonNullCount < 2)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "at least two of ancestor/ours/theirs must be non-null for a name entry",
                GitErrorCategory.Index);
        }

        _names.Add(new GitIndexNameEntry(ancestor, ours, theirs));
        _dirty = true;
    }

    /// <summary>
    /// Clears all conflict-name entries. Matches <c>git_index_name_clear</c>
    /// (index.c:2187-2202).
    /// </summary>
    public void NameClear()
    {
        _names.Clear();
        _dirty = true;
    }

    // ===== Write side: Clear =====

    /// <summary>
    /// Clears all entries, tree cache, REUC, and names. Matches
    /// <c>git_index_clear</c> (index.c:550-578).
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        _entriesMap = null;
        Tree = null;
        _reuc.Clear();
        _names.Clear();
        _sorted = true;
        _dirty = true;
    }

    /// <summary>
    /// Sets the tree cache (internal). Used by <see cref="GitTree.WriteIndexAsync"/>
    /// to update the cache after writing a tree from the index.
    /// </summary>
    internal void SetTreeCache(TreeCache? cache) => Tree = cache;

    /// <summary>
    /// Gets the sorted entries as a read-only list (internal). Ensures entries
    /// are sorted before returning. Used by <see cref="GitTree.WriteIndexAsync"/>.
    /// </summary>
    internal IReadOnlyList<GitIndexEntry> GetSortedEntries()
    {
        EnsureSorted();
        return _entries;
    }

    /// <summary>
    /// Invalidates the tree cache for the given path. Matches
    /// <c>git_tree_cache_invalidate_path</c>. Called after entry
    /// add/remove to mark the cache stale so <c>WriteTreeAsync</c> rebuilds.
    /// </summary>
    internal void InvalidateTreeCache(GitPath path)
    {
        TreeCache.InvalidatePath(Tree, path);
    }

    /// <summary>
    /// Throws if the index has no owning repository (in-memory index).
    /// </summary>
    private void EnsureOwner()
    {
        if (_owner is null)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "index has no owning repository (in-memory index cannot write to disk or ODB)",
                GitErrorCategory.Index);
        }
    }
    /// <summary>Finds resolve-undo information for a UTF-8 path, or returns null if absent.</summary>
    public GitIndexReucEntry? ReucByPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ReucByPath(GitPath.FromUtf8String(path));
    }

    /// <summary> Byte-faithful <see cref="ReucByPath(string)"/>; searches the sorted REUC vector byte-wise (<c>reuc_srch</c> / <c>reuc_isrch</c>). </summary>
    public GitIndexReucEntry? ReucByPath(GitPath path)
    {
        for (int i = 0; i < _reuc.Count; i++)
        {
            int cmp = GitPath.Compare(_reuc[i].Path, path, _ignoreCase);

            if (cmp == 0)
            {
                return _reuc[i];
            }

            if (cmp > 0)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a REUC entry by index. Matches <c>git_index_reuc_get_byindex</c>.
    /// </summary>
    public GitIndexReucEntry? ReucByIndex(int index)
    {
        if ((uint)index >= (uint)_reuc.Count)
        {
            return null;
        }

        return _reuc[index];
    }

    /// <summary>Disposes the index.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entries.Clear();
        _entriesMap?.Clear();
        _reuc.Clear();
        _names.Clear();
    }

    // ===== Write side: Index.Write =====

    /// <summary>
    /// Writes the index to disk atomically. Matches <c>git_index_write</c>
    /// (index.c:817-831). Creates <c>{indexPath}.lock</c>, serializes the
    /// index, then renames to <c>{indexPath}</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Locked"/> if the index is already locked.
    /// <see cref="GitErrorCode.Invalid"/> if the index has no owning repository.
    /// </exception>
    public async Task WriteAsync(CancellationToken cancellationToken = default)
    {
        using var writer = IndexWriter.Init(this);
        await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ===== Internal serialization helpers =====

    /// <summary>
    /// Ensures entries are sorted in case-sensitive order for on-disk
    /// writing. libgit2 temporarily switches to case-sensitive sort before
    /// writing (index.c:3040-3056) because the on-disk format is always
    /// case-sensitively sorted, even when the in-memory index is
    /// case-insensitive. The REUC extension is sorted with the vector's
    /// ACTIVE comparator (icase when the index is case-insensitive),
    /// matching git_indexwriter_commit (index.c:3899-3900).
    /// </summary>
    internal void EnsureSortedForWrite()
    {
        // Always sort case-sensitively for on-disk format (git_index_entry_cmp).
        // libgit2's write_entries (index.c:3040-3056) dupes the vector with the
        // case-sensitive comparator and re-sorts; the on-disk order is always
        // case-sensitive even when the in-memory index is case-insensitive.
        _entries.Sort(static (a, b) =>
        {
            int diff = GitPath.Compare(a.Path, b.Path);
            return diff != 0 ? diff : a.Stage - b.Stage;
        });
        _sorted = !_ignoreCase; // If ignorecase, next access will re-sort.
        _entriesMap = null;

        // C (index.c:3899-3900): git_indexwriter_commit re-sorts the REUC
        // vector with the active comparator — icase order for a
        // case-insensitive index, so a read index with an unsorted REUC
        // extension is normalized on write.
        _reuc.Sort((a, b) => GitPath.Compare(a.Path, b.Path, _ignoreCase));
    }

    /// <summary>
    /// Serializes the index to the on-disk binary format. Matches
    /// <c>write_index</c> (index.c:3221-3278). Produces: DIRC header (12B,
    /// big-endian) + entries (v2/v3 8-byte aligned, or v4 path-prefix
    /// compressed) + extensions (TREE, REUC, NAME) + trailing SHA checksum.
    /// </summary>
    /// <param name="writer">The buffer writer to append the serialized index
    /// to. Must be empty on entry (<see cref="PooledByteBufferWriter.WrittenCount"/>
    /// == 0); the full on-disk image (header through trailing checksum) is
    /// appended to it.</param>
    [SuppressMessage("Security", "CA5350:Do not use insecure cryptographic algorithms", Justification = "SHA-1 is the git index trailing checksum (on-disk format requirement, not a security use); parity with write_index (index.c:3271-3277)")]
    internal void SerializeForWrite(PooledByteBufferWriter writer)
    {
        Debug.Assert(writer.WrittenCount == 0);

        // Note: libgit2's truncate_racily_clean (index.c:741-793) is intentionally
        // NOT ported. It detects files modified-then-reverted within the stat
        // timestamp resolution window ("racy git") and zeroes their file_size
        // so the next diff recomputes the OID. The C# port instead relies on
        // the racy-git check in DiffGenerator.MaybeModified (EntryNewerThanIndex,
        // matching git_index_entry_newer_than_index at index.h:101-118): when a
        // file's mtime is newer than OR equal to the index file's own mtime
        // (Stamp), the diff recomputes the OID regardless of file_size. Combined
        // with nanosecond stat precision (FilesystemIterator.GetStatInfo reads
        // sub-second ticks), this covers the same race without a pre-write smudge.
        int oidSize = GitOid.SizeFor(OidType);

        // C (index.c:2855-2871 + 3238-3243, is_index_extended): the EXTENDED flag bit is RE-SYNCED from flags_extended on every entry — cleared, then set only
        // when flags_extended & 0xC000 is non-zero — but ONLY for v2/v3; a v4 index keeps the raw flags and version.
        int extendedCount = 0;
        if (Version <= VersionExt)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                GitIndexEntry e = _entries[i];
                ushort flags = (ushort)(e.Flags & ~GitIndexEntry.Extended);
                if ((e.FlagsExtended & GitIndexEntry.ExtendedFlags) != 0)
                {
                    flags |= GitIndexEntry.Extended;
                    extendedCount++;
                }

                if (flags != e.Flags)
                {
                    _entries[i] = e with { Flags = flags };
                }
            }
        }

        // Determine write version (index.c:3224-3232): a v2/v3 index is
        // written as v3 ONLY when at least one entry is extended, otherwise
        // demoted to v2. v4 is orthogonal and NOT downgraded.
        int writeVersion = Version;
        if (writeVersion <= VersionExt)
        {
            writeVersion = extendedCount > 0 ? VersionExt : VersionDefault;
        }

        // --- DIRC header (12 bytes, big-endian) ---
        {
            Span<byte> buffer = writer.GetSpan(12);
            BinaryPrimitives.WriteUInt32BigEndian(buffer, HeaderSignature);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4), (uint)writeVersion);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8), (uint)_entries.Count);
            writer.Advance(12);
        }

        // --- Entries ---
        // v4 path-prefix compression threads the previous entry's path
        // bytes through the loop (C's `last` in write_entries, index.c:3044/3064-3065).
        // Byte-faithful: thread the entry's GitPath directly (no UTF-8
        // encode round-trip); write_disk_entry compares raw path bytes.
        GitPath last = default;
        bool hasLast = false;
        foreach (GitIndexEntry entry in _entries)
        {
            WriteEntry(writer, entry, writeVersion, hasLast ? last : null);
            if (writeVersion >= VersionComp)
            {
                last = entry.Path;
                hasLast = true;
            }
        }

        // --- Extensions ---
        // Extensions are written in C's order (index.c:3255-3265):
        // TREE → NAME (conflict names) → REUC (resolve-undo).
        // TREE extension (tree cache).
        if (Tree is not null)
        {
            byte[] treeData = Tree.Write();
            WriteExtension(writer, "TREE"u8, treeData);
        }

        // NAME extension (conflict names).
        if (_names.Count > 0)
        {
            WriteNameExtension(writer);
        }

        // REUC extension (resolve-undo).
        if (_reuc.Count > 0)
        {
            WriteReucExtension(writer, oidSize);
        }

        // --- Trailing checksum (SHA-1 or SHA-256 of all preceding content) ---
        // Hash into a stack buffer, then append to the writer. Hashing
        // directly into a writer GetSpan would be wrong: with under 32 bytes
        // of free capacity the GetSpan grows the writer and returns the old
        // backing array to the ArrayPool while the body span still references it.
        Span<byte> checksum = stackalloc byte[oidSize];
        if (OidType == GitHashAlgorithmKind.Sha256)
        {
            SHA256.HashData(writer.WrittenSpan, checksum);
        }
        else
        {
            SHA1.HashData(writer.WrittenSpan, checksum);
        }

        // C (index.c:630-634): git_index_checksum returns the checksum of
        // the last write (or read); keep the freshly computed value.
        _checksum = GitOid.FromRaw(checksum, OidType);

        writer.Write(checksum);
    }

    /// <summary>
    /// Serializes a single index entry in v2/v3/v4 on-disk format. Matches
    /// <c>write_disk_entry</c> (index.c:2873-3036).
    /// </summary>
    /// <param name="last">The previous entry's path (<c>null</c> for the first
    /// entry or when writing v2/v3); v4 computes the common byte prefix with
    /// this path.</param>
    /// <param name="writer">The destination buffer writer.</param>
    /// <param name="entry">The entry to process.</param>
    /// <param name="version">The index format version.</param>
    private static void WriteEntry(PooledByteBufferWriter writer, GitIndexEntry entry, int version, GitPath? last)
    {
        Span<byte> buffer = writer.GetSpan(40 + 32 + 4); // 40B common + 32B OID + 4B flags (max)

        int bytesWritten;

        // Common fields (40 bytes, big-endian).
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)entry.Ctime.Seconds);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4), entry.Ctime.Nanoseconds);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8), (uint)entry.Mtime.Seconds);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(12), entry.Mtime.Nanoseconds);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(16), entry.Dev);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(20), entry.Ino);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(24), (uint)entry.Mode);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(28), entry.Uid);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(32), entry.Gid);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(36), entry.FileSize);

        bytesWritten = 40;
        buffer = buffer.Slice(40);

        // OID (20 or 32 bytes).
        ReadOnlySpan<byte> entryId = entry.Id.RawBytes;
        entryId.CopyTo(buffer);

        bytesWritten += entryId.Length;
        buffer = buffer.Slice(entryId.Length);

        // Flags (2 bytes, big-endian). C (index.c:2954): the in-memory flags
        // are written VERBATIM — the 12-bit name-length field is whatever the
        // entry carries (set by index_entry_adjust_namemask at insert, or
        // preserved from disk). For v4 the flags field still carries the FULL
        // path length; the on-disk suffix length is implicit in the varint +
        // suffix region and is NOT stored in the flags field.
        BinaryPrimitives.WriteUInt16BigEndian(buffer, entry.Flags);

        bytesWritten += 2;
        buffer = buffer.Slice(2);

        // Extended flags (2 bytes, only if Extended bit is set). The
        // on-disk value is masked to 0xC000 (index.c:2970-2971).
        if (version >= VersionExt && (entry.Flags & GitIndexEntry.Extended) != 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)(entry.FlagsExtended & GitIndexEntry.ExtendedFlags));

            bytesWritten += 2;
        }

        writer.Advance(bytesWritten);

        // Raw path bytes — emitted directly (no UTF-8 encode round-trip),
        // matching write_disk_entry which writes entry->path verbatim.
        ReadOnlySpan<byte> pathBytes = entry.Path.Span;

        if (version >= VersionComp)
        {
            // v4 path-prefix compression (index.c:3008-3034).
            // Compute the common byte-level prefix with `last`. When `last`
            // is null (first entry), treat it as empty so stripLen = 0 and
            // the full path is written as the suffix.
            ReadOnlySpan<byte> lastBytes = last is { } lp ? lp.Span : ReadOnlySpan<byte>.Empty;
            int sameLen = 0;
            int maxCommon = Math.Min(lastBytes.Length, pathBytes.Length);
            while (sameLen < maxCommon && lastBytes[sameLen] == pathBytes[sameLen])
            {
                sameLen++;
            }

            // stripLen = number of trailing bytes of `last` NOT shared.
            ulong stripLen = (ulong)(lastBytes.Length - sameLen);

            // Encode the strip length as a git-style varint.
            // Use a stackalloc temp to obtain the encoded length (matches C's
            // git_encode_varint(NULL, 0, value) query at index.c:2920).
            Span<byte> buffer2 = writer.GetSpan(16 + pathBytes.Length - sameLen + 1); // varint + suffix + NUL
            int vlen = GitVarint.Encode(buffer2, stripLen);

            // Write varint + suffix + NUL. No 8-byte alignment padding for v4
            // (index.c:3019-3023 asserts disk_size == path_len + 1).

            pathBytes[sameLen..].CopyTo(buffer2.Slice(vlen));
            buffer2[vlen + pathBytes.Length - sameLen] = 0;

            writer.Advance(vlen + pathBytes.Length - sameLen + 1);
        }
        else
        {
            // v2/v3: path + NUL + 8-byte alignment padding.
            Span<byte> buffer2 = writer.GetSpan(pathBytes.Length + 1 + 8); // path + NUL + up to 7 padding bytes
            buffer2.Clear(); // Zero the span so the trailing alignment padding is NUL.

            pathBytes.CopyTo(buffer2);
            buffer2[pathBytes.Length] = 0;

            bytesWritten += pathBytes.Length + 1;
            int paddedSize = (bytesWritten + 7) & ~7;
            int padding = paddedSize - bytesWritten;

            writer.Advance(pathBytes.Length + 1 + padding);
        }
    }

    /// <summary>
    /// Writes an extension header + data. Matches <c>write_extension</c>
    /// (index.c:3073-3090).
    /// </summary>
    private static void WriteExtension(PooledByteBufferWriter writer, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> data)
    {
        Debug.Assert(signature.Length == 4);

        Span<byte> buffer = writer.GetSpan(signature.Length + 4 + data.Length);

        // 4-byte signature (ASCII).
        signature.CopyTo(buffer);

        // 4-byte size (big-endian).
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(signature.Length), (uint)data.Length);

        // Extension data.
        data.CopyTo(buffer.Slice(signature.Length + 4));

        writer.Advance(signature.Length + 4 + data.Length);
    }

    /// <summary>
    /// Writes the REUC extension. Matches <c>write_reuc_extension</c>
    /// (index.c:3163-3187) + <c>create_reuc_extension_data</c> (3140-3161).
    /// </summary>
    private void WriteReucExtension(PooledByteBufferWriter writer, int _)
    {
        using var data = new PooledByteBufferWriter();

        foreach (GitIndexReucEntry reuc in _reuc)
        {
            // Path (NUL-terminated) — raw bytes, no UTF-8 encode.
            data.Write(reuc.Path.Span);
            data.Write(0);

            // 3 ASCII octal mode strings (NUL-terminated). C always emits
            // git_str_printf("%o", mode) + NUL — for mode 0 that is the two
            // bytes "0\0" (index.c:3149-3153); a bare NUL is unreadable by
            // libgit2's own reader.
            for (int i = 0; i < 3; i++)
            {
                data.WriteSpanFormattable(new OctalPadLeftFormatter(reuc.Modes[i], 0), provider: CultureInfo.InvariantCulture);
                data.Write(0);
            }

            // OIDs for non-zero modes.
            for (int i = 0; i < 3; i++)
            {
                if (reuc.Modes[i] != 0)
                {
                    data.Write(reuc.Oids[i].RawBytes);
                }
            }
        }

        WriteExtension(writer, "REUC"u8, data.WrittenSpan);
    }

    /// <summary>
    /// Writes the NAME extension. Matches <c>write_name_extension</c>
    /// (index.c:3114-3138) + <c>create_name_extension_data</c> (3085-3112).
    /// </summary>
    private void WriteNameExtension(PooledByteBufferWriter writer)
    {
        using var data = new PooledByteBufferWriter();
        foreach (GitIndexNameEntry name in _names)
        {
            // Each field is NUL-terminated; null → just NUL. Raw bytes.
            WriteNulTerminatedPath(data, name.Ancestor);
            WriteNulTerminatedPath(data, name.Ours);
            WriteNulTerminatedPath(data, name.Theirs);
        }

        WriteExtension(writer, "NAME"u8, data.WrittenSpan);
    }

    private static void WriteNulTerminatedPath(PooledByteBufferWriter writer, GitPath? value)
    {
        if (value is { } path)
        {
            ReadOnlySpan<byte> pathSpan = path.Span;
            Span<byte> buffer = writer.GetSpan(pathSpan.Length + 1);
            pathSpan.CopyTo(buffer);
            buffer[pathSpan.Length] = 0;
            writer.Advance(pathSpan.Length + 1);
        }
        else
        {
            Span<byte> buffer = writer.GetSpan(1);
            buffer[0] = 0;
            writer.Advance(1);
        }
    }

    // ===== Internal parsing =====

    /// <summary>
    /// Case-select path comparator for binary search by path. Ports
    /// <c>entries_cmp_path</c> (<c>git__strcmp_cb</c> / <c>git__strcasecmp_cb</c>,
    /// <c>index.c:367-377</c>): byte compare (case-sensitive) or ASCII-fold
    /// compare (case-insensitive) over the raw path bytes — <em>not</em>
    /// .NET's Unicode-aware <c>StringComparison.OrdinalIgnoreCase</c>.
    /// </summary>
    private int ComparePath(GitPath a, GitPath b) => GitPath.Compare(a, b, _ignoreCase);

    /// <summary>
    /// Entry sort comparator. Ports <c>git_index_entry_cmp</c> /
    /// <c>git_index_entry_icmp</c> (<c>index.c:201-227</c>): path compare
    /// (case-select) then stage difference.
    /// </summary>
    private int CompareEntries(GitIndexEntry a, GitIndexEntry b)
    {
        int diff = GitPath.Compare(a.Path, b.Path, _ignoreCase);
        return diff != 0 ? diff : a.Stage - b.Stage;
    }

    private bool _sorted = true;

    private void EnsureSorted()
    {
        if (!_sorted)
        {
            SortEntries();
        }
    }

    private void SortEntries()
    {
        _entries.Sort(CompareEntries);
        _sorted = true;
        _entriesMap = null; // force rebuild on next lookup
    }

    /// <summary>
    /// Lazily-built path/stage → entry lookup map. Rebuilt on first access
    /// after <see cref="_entriesMap"/> is invalidated (set to null). Matches
    /// the <c>git_index_entrymap</c> rebuilt-on-demand behavior in
    /// <c>index_map.c</c>. The equality/hash comparer is selected by
    /// <see cref="_ignoreCase"/> (mirroring libgit2's single map whose
    /// hash/equality swap on <c>git_index__set_ignore_case</c>).
    /// </summary>
    private Dictionary<(GitPath, int), GitIndexEntry> EntriesMap
    {
        get
        {
            if (_entriesMap is null)
            {
                _entriesMap = new Dictionary<(GitPath, int), GitIndexEntry>(
                    _entries.Count,
                    _ignoreCase ? IndexEntryKeyComparer.CaseInsensitive : IndexEntryKeyComparer.CaseSensitive);
                foreach (GitIndexEntry entry in _entries)
                {
                    _entriesMap[(entry.Path, entry.Stage)] = entry;
                }
            }

            return _entriesMap;
        }
    }

    // ===== Binary format parser =====

    private void ParseIndex(ReadOnlySpan<byte> buffer)
    {
        int oidSize = GitOid.SizeFor(OidType);
        int checksumSize = oidSize;

        if (buffer.Length < HeaderSize + checksumSize)
        {
            throw new GitException(GitErrorCode.Error, "insufficient buffer space for index", GitErrorCategory.Index);
        }

        // Capture the stored trailing checksum regardless of all-zero
        // (skipHash): C's git_index_checksum returns whatever was in the
        // file (index.c:630-634), including a zero hash.
        int bodyLen = buffer.Length - checksumSize;
        Span<byte> storedChecksum = stackalloc byte[checksumSize];
        buffer.Slice(bodyLen, checksumSize).CopyTo(storedChecksum);
        _checksum = GitOid.FromRaw(storedChecksum, OidType);

        int pos = 0;

        // --- Header ---
        ReadHeader(buffer, ref pos, out int version, out int entryCount);
        Version = version;

        if (version is < VersionDefault or > VersionComp)
        {
            throw new GitException(GitErrorCode.Error, $"incorrect header version: {version}", GitErrorCategory.Index);
        }

        // --- Entries ---
        // v4 path-prefix compression threads the previous entry's path
        // bytes through the loop (C's `last` in parse_index, index.c:2769/2800).
        // Byte-faithful: thread the GitPath directly (no UTF-8 round-trip).
        GitPath last = default;
        for (int i = 0; i < entryCount && buffer.Length - pos > checksumSize; i++)
        {
            GitIndexEntry entry = ReadEntry(buffer, ref pos, OidType, version, last, checksumSize);
            _entries.Add(entry);
            if (version >= VersionComp)
            {
                last = entry.Path;
            }
        }

        // C (index.c:2805-2808): "header entries changed while parsing" —
        // the parsed entry count must match the header.
        if (_entries.Count != entryCount)
        {
            throw new GitException(GitErrorCode.Error, "invalid data in index - header entries changed while parsing", GitErrorCategory.Index);
        }

        // On-disk entries are always case-sensitively sorted. If we are
        // case-insensitive, we need to re-sort.
        _sorted = !_ignoreCase;

        // --- Extensions ---
        while (buffer.Length - pos > checksumSize)
        {
            ReadExtension(buffer, ref pos, checksumSize, OidType, this);
        }

        if (buffer.Length - pos != checksumSize)
        {
            throw new GitException(GitErrorCode.Error, "buffer size does not match index footer size", GitErrorCategory.Index);
        }

        // C (index.c:2827-2838): the checksum is verified AFTER the header,
        // entries, and extensions have been parsed, so a double-corrupt file
        // reports the structural error first. All-zero checksums (skipHash)
        // are accepted.
        VerifyChecksum(buffer, checksumSize);
    }

    [SuppressMessage("Security", "CA5350:Do not use insecure cryptographic algorithms", Justification = "SHA1 is required by git's object format; not used for security")]
    private static void VerifyChecksum(ReadOnlySpan<byte> buffer, int checksumSize)
    {
        if (checksumSize is not SHA256.HashSizeInBytes and not SHA1.HashSizeInBytes)
        {
            throw new GitException(GitErrorCode.Error, "unsupported checksum size", GitErrorCategory.Index);
        }

        int bodyLen = buffer.Length - checksumSize;
        Span<byte> storedChecksum = stackalloc byte[checksumSize];
        buffer.Slice(bodyLen, checksumSize).CopyTo(storedChecksum);

        // Accept all-zero checksum (skipHash).
        bool allZero = !storedChecksum.ContainsAnyExcept((byte)0);

        if (allZero)
        {
            return;
        }

        Span<byte> computed = stackalloc byte[SHA256.HashSizeInBytes];
        int computedSize;
        if (checksumSize == SHA256.HashSizeInBytes)
        {
            computedSize = SHA256.HashData(buffer.Slice(0, bodyLen), computed);
        }
        else
        {
            computedSize = SHA1.HashData(buffer.Slice(0, bodyLen), computed);
        }

        Debug.Assert(computedSize == checksumSize);
        for (int i = 0; i < checksumSize; i++)
        {
            if (computed[i] != storedChecksum[i])
            {
                throw new GitException(GitErrorCode.Error, "calculated checksum does not match expected", GitErrorCategory.Index);
            }
        }
    }

    private static void ReadHeader(ReadOnlySpan<byte> buffer, ref int pos, out int version, out int entryCount)
    {
        uint sig = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos));
        pos += 4;
        if (sig != HeaderSignature)
        {
            throw new GitException(GitErrorCode.Error, "incorrect header signature", GitErrorCategory.Index);
        }

        version = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(pos));
        pos += 4;
        entryCount = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(pos));
        pos += 4;
    }

    private static GitIndexEntry ReadEntry(ReadOnlySpan<byte> buffer, ref int pos, GitHashAlgorithmKind oidType, int version, GitPath last, int checksumSize)
    {
        int oidSize = GitOid.SizeFor(oidType);
        int entryStart = pos;

        // C (index.c:2536-2538, 2657-2660): the minimal entry size is
        // index_entry_path_offset(oid, 0) = 40 + oid + 2 flags bytes, and
        // the check is against (checksum + minimal) — "invalid index checksum".
        int minimalEntrySize = 40 + oidSize + 2;
        if (checksumSize + minimalEntrySize > buffer.Length - entryStart)
        {
            throw new GitException(GitErrorCode.Error, "invalid index checksum", GitErrorCategory.Odb);
        }

        // Read the common fixed-size fields (40 bytes). C widens the on-disk
        // uint32 seconds to int64 value-preserving (index.c:2558-2561) — a
        // high-bit value is a large POSITIVE time, not a negative one.
        long ctimeSec = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos));
        pos += 4;
        uint ctimeNsec = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos));
        pos += 4;
        long mtimeSec = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos));
        pos += 4;
        uint mtimeNsec = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos, 4));
        pos += 4;
        uint dev = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos, 4));
        pos += 4;
        uint ino = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos, 4));
        pos += 4;
        uint mode = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos, 4));
        pos += 4;
        uint uid = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos, 4));
        pos += 4;
        uint gid = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos, 4));
        pos += 4;
        uint fileSize = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos, 4));
        pos += 4;

        // OID (20 or 32 bytes).
        var oid = GitOid.FromRaw(buffer.Slice(pos, oidSize), oidType);
        pos += oidSize;

        // flags (uint16 big-endian).
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(pos));
        pos += 2;
        ushort flagsExtended = 0;

        if ((flags & GitIndexEntry.Extended) != 0)
        {
            flagsExtended = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(pos, 2));
            pos += 2;
        }

        // Path offset from entry start (depends on oid type + extended flag).
        int pathOffset = pos - entryStart;

        // Path is held as an owned byte copy (the parse buffer is not retained),
        // matching index_entry_dup -> index_entry_create -> memcpy(entry->path, ...)
        // (index.c:936-973). No UTF-8 decode: the raw bytes ARE the path.
        GitPath path;
        if (version >= VersionComp)
        {
            // v4 path-prefix compression (index.c:2625-2652).
            // The path region holds: varint(stripLen) + suffix + NUL.
            // stripLen is the number of trailing bytes to strip from `last`
            // to form the prefix; the full path is last[0..prefixLen] + suffix.
            ReadOnlySpan<byte> lastBytes = last.Span;
            long stripLen = (long)GitVarint.Decode(buffer.Slice(pos), out int varintLen);
            if (varintLen == 0 || lastBytes.Length < stripLen)
            {
                throw new GitException(GitErrorCode.Error, "incorrect prefix length in v4 index entry", GitErrorCategory.Index);
            }

            int prefixLen = lastBytes.Length - (int)stripLen;

            // Suffix runs from pos+varintLen to the next NUL byte.
            int suffixStart = pos + varintLen;
            int nul = buffer.Slice(suffixStart).IndexOf((byte)0);
            if (nul < 0)
            {
                throw new GitException(GitErrorCode.Error, "invalid path name in v4 index entry", GitErrorCategory.Index);
            }

            int suffixLen = nul;

            // Reconstruct into an owned byte[]: last[0..prefixLen] + suffix.
            // Matches C's memcpy(tmp_path, last, prefix_len);
            //              memcpy(tmp_path+prefix_len, suffix, suffix_len+1).
            // C (index.c:2638-2643): the reconstructed length (incl. NUL)
            // must not exceed GIT_PATH_MAX (4096).
            if (prefixLen + suffixLen + 1 > 4096)
            {
                throw new GitException(GitErrorCode.Error, "unreasonable path length", GitErrorCategory.Index);
            }

            byte[] tmpPath = new byte[prefixLen + suffixLen];
            lastBytes[..prefixLen].CopyTo(tmpPath.AsSpan(0, prefixLen));
            buffer.Slice(suffixStart, suffixLen).CopyTo(tmpPath.AsSpan(prefixLen, suffixLen));
            path = GitPath.FromUtf8Bytes(tmpPath);

            // v4 entries are NOT padded to 8-byte boundaries (index.c:2650).
            // entry_size = pathOffset + varintLen + suffixLen + 1 (trailing NUL).
            // C (index.c:2657-2660): checksum + entry_size must fit.
            int v4EntrySize = pathOffset + varintLen + suffixLen + 1;
            if (checksumSize + v4EntrySize > buffer.Length - entryStart)
            {
                throw new GitException(GitErrorCode.Error, "invalid index checksum", GitErrorCategory.Odb);
            }

            pos = entryStart + v4EntrySize;
        }
        else
        {
            // v2/v3: path length from flags & NAMEMASK (or scan NUL if 0xFFF).
            int pathLength = flags & GitIndexEntry.NameMask;

            if (pathLength == 0xFFF)
            {
                // Long path: actual length unknown, scan for NUL.
                int nul = buffer.Slice(pos).IndexOf((byte)0);
                if (nul < 0)
                {
                    throw new GitException(GitErrorCode.Error, "invalid path name in index entry", GitErrorCategory.Index);
                }

                pathLength = nul;
            }

            // v2/v3: 8-byte alignment padding.
            // entry_size = (pathOffset + pathLength + 8) & ~7.
            // The entry size (and its bounds check) must come BEFORE the path
            // copy, so a crafted .idx whose NAMEMASK length runs past the
            // buffer fails with the clean index error C produces (C checks
            // entry_size at index.c:2657-2660 before index_entry_dup at 2662)
            // instead of a raw ArgumentOutOfRangeException.
            int entrySize = (pathOffset + pathLength + 8) & ~7;

            // C (index.c:2657-2660): "invalid index checksum" when the entry
            // (plus the trailing checksum) runs past the end of the buffer.
            if (checksumSize + entrySize > buffer.Length - entryStart)
            {
                throw new GitException(GitErrorCode.Error, "invalid index checksum", GitErrorCategory.Odb);
            }

            // Owned byte copy of the raw path region.
            path = GitPath.FromUtf8Bytes(buffer.Slice(pos, pathLength).ToArray());

            pos = entryStart + entrySize;
        }

        return new GitIndexEntry
        {
            Ctime = new IndexTime(ctimeSec, ctimeNsec),
            Mtime = new IndexTime(mtimeSec, mtimeNsec),
            Dev = dev,
            Ino = ino,
            // C (index.c:2564): the raw 32-bit mode is preserved verbatim —
            // no normalization on read (validated only at git_index_add).
            Mode = (GitFileMode)mode,
            Uid = uid,
            Gid = gid,
            FileSize = fileSize,
            Id = oid,
            Flags = flags,
            FlagsExtended = flagsExtended,
            Path = path,
        };
    }

    private static void ReadExtension(ReadOnlySpan<byte> buffer, ref int pos, int checksumSize, GitHashAlgorithmKind oidType, GitIndex index)
    {
        int extStart = pos;

        // Extension header: 4-byte signature + 4-byte size (big-endian).
        byte sig0 = buffer[pos];
        byte sig1 = buffer[pos + 1];
        byte sig2 = buffer[pos + 2];
        byte sig3 = buffer[pos + 3];
        pos += 4;

        // C (index.c:2694-2705): the size is read UNSIGNED (ntohl) and bounds-checked before any use — a high-bit size must yield "extension is truncated", not
        // a negative span length / raw ArgumentOutOfRangeException.
        uint extSizeU = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos));
        pos += 4;
        long totalSize = 8 + extSizeU;

        if (buffer.Length - pos < (long)extSizeU + checksumSize)
        {
            throw new GitException(GitErrorCode.Error, "extension is truncated", GitErrorCategory.Index);
        }

        int extSize = (int)extSizeU;

        int extDataStart = pos;

        // Optional extensions have uppercase first byte.
        if (sig0 is >= (byte)'A' and <= (byte)'Z')
        {
            if (sig0 == 'T' && sig1 == 'R' && sig2 == 'E' && sig3 == 'E')
            {
                // TREE extension → TreeCache.Read
                index.Tree = TreeCache.Read(buffer.Slice(extDataStart, extSize), oidType);
            }
            else if (sig0 == 'R' && sig1 == 'E' && sig2 == 'U' && sig3 == 'C')
            {
                ReadReuc(index, buffer, extDataStart, extSize, oidType);
            }
            else if (sig0 == 'N' && sig1 == 'A' && sig2 == 'M' && sig3 == 'E')
            {
                ReadConflictNames(index, buffer, extDataStart, extSize);
            }

            // else: unknown optional extension — skip silently.
        }
        else
        {
            // Mandatory extension (lowercase first byte) — not supported.
            string sig = $"{(char)sig0}{(char)sig1}{(char)sig2}{(char)sig3}";
            throw new GitException(GitErrorCode.Error, $"unsupported mandatory extension: '{sig}'", GitErrorCategory.Index);
        }

        pos = (int)(extStart + totalSize);
    }

    private static void ReadReuc(GitIndex index, ReadOnlySpan<byte> buffer, int start, int size, GitHashAlgorithmKind oidType)
    {
        int oidSize = GitOid.SizeFor(oidType);
        int pos = start;
        int end = start + size;

        while (pos < end)
        {
            // NUL-terminated path — owned byte copy (raw bytes, no UTF-8
            // decode). C (index.c:2345-2348): the NUL must not be the last
            // byte of the extension ("reading reuc entries").
            int nul = buffer.Slice(pos, end - pos).IndexOf((byte)0);
            if (nul < 0 || nul + 1 >= end - pos)
            {
                throw new GitException(GitErrorCode.Error, "reading reuc entries", GitErrorCategory.Index);
            }

            var path = GitPath.FromUtf8Bytes(buffer.Slice(pos, nul).ToArray());
            pos += nul + 1;

            // 3 ASCII octal mode strings (NUL-terminated). C parses each with
            // git__strntol64 base 8 (index.c:2351-2367): leading whitespace
            // skipped, empty/overflow/non-octal/truncated → error.
            uint[] modes = new uint[3];
            for (int i = 0; i < 3; i++)
            {
                modes[i] = ReadOctalNulTerminated(buffer, ref pos, end);
            }

            // Up to 3 OIDs (one per non-zero mode). C (index.c:2369-2384):
            // fewer than oid_size bytes left → "reading reuc entry oid".
            var oids = new GitOid[3];
            for (int i = 0; i < 3; i++)
            {
                if (modes[i] != 0)
                {
                    if (end - pos < oidSize)
                    {
                        throw new GitException(GitErrorCode.Error, "reading reuc entry oid", GitErrorCategory.Index);
                    }

                    oids[i] = GitOid.FromRaw(buffer.Slice(pos, oidSize), oidType);
                    pos += oidSize;
                }
            }

            index._reuc.Add(new GitIndexReucEntry(path, modes, oids));
        }
    }

    private static void ReadConflictNames(GitIndex index, ReadOnlySpan<byte> buffer, int start, int size)
    {
        int pos = start;
        int end = start + size;

        while (pos < end)
        {
            GitPath? ancestor = ReadNulTerminatedPathOrNull(buffer, ref pos, end);
            GitPath? ours = ReadNulTerminatedPathOrNull(buffer, ref pos, end);
            GitPath? theirs = ReadNulTerminatedPathOrNull(buffer, ref pos, end);

            index._names.Add(new GitIndexNameEntry(ancestor, ours, theirs));
        }
    }

    private static uint ReadOctalNulTerminated(ReadOnlySpan<byte> buffer, ref int pos, int end)
    {
        // C (index.c:2354-2359): git__strntol64 base 8 — skips leading whitespace and accepts a leading '+' sign (a leading '-' parses negative and fails the
        // tmp < 0 check); an empty field, overflow past UINT32_MAX, a non-octal digit, or a missing terminator all fail with "reading reuc entry stage".
        while (pos < end && buffer[pos] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)'\v')
        {
            pos++;
        }

        if (pos < end && buffer[pos] == (byte)'+')
        {
            pos++;
        }

        uint value = 0;
        bool anyDigit = false;
        while (pos < end && buffer[pos] != 0)
        {
            byte c = buffer[pos];
            if (c is < (byte)'0' or > (byte)'7')
            {
                throw new GitException(GitErrorCode.Error, "reading reuc entry stage", GitErrorCategory.Index);
            }

            uint digit = (uint)(c - '0');
            if (value > (uint.MaxValue - digit) / 8)
            {
                throw new GitException(GitErrorCode.Error, "reading reuc entry stage", GitErrorCategory.Index);
            }

            value = (value << 3) | digit;
            anyDigit = true;
            pos++;
        }

        if (!anyDigit || pos >= end)
        {
            // Empty mode, or the mode is not NUL-terminated within the
            // extension (C: endptr == buffer / *endptr).
            throw new GitException(GitErrorCode.Error, "reading reuc entry stage", GitErrorCategory.Index);
        }

        // C (index.c:2361-2363): the NUL must not be the last byte of the
        // extension (size <= len after consuming it).
        if (pos + 1 >= end)
        {
            throw new GitException(GitErrorCode.Error, "reading reuc entry stage", GitErrorCategory.Index);
        }

        pos++; // skip NUL
        return value;
    }

    private static GitPath? ReadNulTerminatedPathOrNull(ReadOnlySpan<byte> buffer, ref int pos, int end)
    {
        int nul = buffer.Slice(pos).IndexOf((byte)0);
        if (nul < 0 || nul >= end - pos)
        {
            throw new GitException(GitErrorCode.Error, "reading conflict name entries", GitErrorCategory.Index);
        }

        int len = nul;
        GitPath? result = len == 0 ? null : GitPath.FromUtf8Bytes(buffer.Slice(pos, len).ToArray());
        pos += nul + 1;
        return result;
    }
}
