// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Objects;

/// <summary> A git tree object (directory listing). Managed port of libgit2's <c>src/libgit2/tree.c</c> (read side). </summary> <remarks> <para> Trees encode
/// each entry as <c>&lt;mode-in-octal-ASCII&gt; &lt;space&gt; &lt;name&gt; \0 &lt;raw-OID-bytes&gt;</c>. The parser walks this byte stream directly. Entries
/// are sorted in the canonical tree order (see <see cref="GitTreeEntry.CompareTo"/>); the parser preserves that order. </para> <para> Entry names are byte-faithful <see cref="GitPath"/> values sliced zero-copy from the retained raw ODB buffer (matching <c>entry->filename = buffer</c> in
/// <c>tree.c:435</c>). Names are never decoded on the object side. </para> </remarks>
public sealed class GitTree : GitObject, IEnumerable<GitTreeEntry>
{
    private readonly GitTreeEntry[] _entries;

    private GitTree(GitRepository? owner, GitOid id, long size, ReadOnlyMemory<byte> raw, GitTreeEntry[] entries)
        : base(owner, id, GitObjectType.Tree, size, raw) => _entries = entries;

    /// <summary>
    /// The number of entries in this tree. Matches <c>git_tree_entrycount</c>.
    /// </summary>
    public int EntryCount => _entries.Length;

    /// <summary>
    /// Looks up an entry by name using the two-phase homing binary search.
    /// Matches <c>git_tree_entry_byname</c>.
    /// </summary>
    /// <returns>The entry, or null if not found.</returns>
    public GitTreeEntry? this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            return EntryByName(GitPath.FromUtf8String(name));
        }
    }

    /// <summary>
    /// Returns the entry at the given 0-based index, or null if out of range.
    /// Matches <c>git_tree_entry_byindex</c>.
    /// </summary>
    public GitTreeEntry? EntryByIndex(int index)
        => (uint)index < (uint)_entries.Length ? _entries[index] : null;

    /// <summary>
    /// Looks up an entry by name using the two-phase homing binary search.
    /// Matches <c>git_tree_entry_byname</c> + <c>tree_key_search</c>.
    /// </summary>
    /// <returns>The entry, or null if not found.</returns>
    public GitTreeEntry? EntryByName(GitPath name)
    {
        // Phase 1: binary search using prefix-match comparator.
        int homing = HomingSearch(name);

        if (homing < 0)
        {
            return null;
        }

        // Phase 2a: scan forward from homing for an exact (byte-equal) match.
        // Break only on a NEGATIVE comparison: the homing comparator is not
        // monotonic over tree order (a positive entry such as "foa" for key
        // "foo" can sit between prefix matches), so C skips such entries
        // (tree.c:184) instead of stopping. The exact-match test mirrors C's
        // `filename_len == filename_len && memcmp(...) == 0` (tree.c:188-190):
        // a same-length entry whose bytes differ (e.g. "foa" vs "foo") is not
        // a match even though the homing comparison is positive.
        for (int i = homing; i < _entries.Length; i++)
        {
            GitTreeEntry e = _entries[i];
            if (GitPath.CompareHoming(name, e.Name) < 0)
            {
                break;
            }

            if (e.Name.Length == name.Length && GitPath.Compare(name, e.Name) == 0)
            {
                return e;
            }
        }

        // Phase 2b: scan backward from homing - 1. Break only on a POSITIVE
        // comparison (tree.c:204).
        for (int i = homing - 1; i >= 0; i--)
        {
            GitTreeEntry e = _entries[i];
            if (GitPath.CompareHoming(name, e.Name) > 0)
            {
                break;
            }

            if (e.Name.Length == name.Length && GitPath.Compare(name, e.Name) == 0)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary> <c>string</c> convenience overload of <see cref="EntryByName(GitPath)"/>. </summary>
    public GitTreeEntry? EntryByName(string name)
        => EntryByName(GitPath.FromUtf8String(name));

    /// <summary>
    /// Looks up an entry by OID via linear scan. Matches <c>git_tree_entry_by_id</c>.
    /// </summary>
    public GitTreeEntry? EntryById(GitOid id)
    {
        foreach (GitTreeEntry e in _entries)
        {
            if (e.Id.Equals(id))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// Walks a slash-separated path and returns the final entry. Cascades into
    /// sub-trees via <c>Owner.Objects.LookupAsync&lt;GitTree&gt;</c>. Matches
    /// <c>git_tree_entry_bypath</c>.
    /// </summary>
    /// <param name="path">Slash-separated path (e.g. <c>"src/foo.txt"</c>).</param>
    /// <returns>The entry, or null if any path component is missing.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> if the owner repository is unset (needed for cascading lookups).
    /// </exception>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask<GitTreeEntry?> EntryByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return EntryByPathAsync(GitPath.FromUtf8String(path), cancellationToken);
    }

    /// <summary> Walks a slash-separated byte path and returns the final entry. Cascades into sub-trees via <c>Owner.Objects.LookupAsync&lt;GitTree&gt;</c>.
    /// Byte-faithful variant of <see cref="EntryByPathAsync(string, CancellationToken)"/>; matches <c>git_tree_entry_bypath</c> (tree.c:915-975). </summary>
    public ValueTask<GitTreeEntry?> EntryByPathAsync(GitPath path, CancellationToken cancellationToken = default)
    {
        GitTree current = this;
        int pos = 0;

        while (NextComponent(path, pos, out GitPath component, out bool more, out bool followedBySlash, out pos))
        {
            GitTreeEntry? entry = current.EntryByName(component);
            if (entry is null)
            {
                return ValueTask.FromResult<GitTreeEntry?>(null);
            }

            // C (tree.c:942-961): a component followed by a '/' (whether the path continues with non-empty components or ends right there) must be a tree —
            // "the path '...' exists but is not a tree". A component at the very end of the path (no slash) is returned as-is.
            if (followedBySlash && entry.Value.Type is not GitObjectType.Tree)
            {
                return ValueTask.FromResult<GitTreeEntry?>(null);
            }

            // Last component — return as-is (the majority single-component case).
            if (!more)
            {
                return ValueTask.FromResult(entry);
            }

            if (current.Owner is null)
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    "tree has no owning repository; cannot cascade into sub-tree",
                    GitErrorCategory.Object);
            }

            // Multi-component path — descend into the sub-tree (ODB read).
            return new ValueTask<GitTreeEntry?>(EntryByPathSlowAsync(current, entry.Value.Id, path, pos, cancellationToken));
        }

        return ValueTask.FromResult<GitTreeEntry?>(null);
    }

    /// <summary>
    /// Slow path of <see cref="EntryByPathAsync(GitPath, CancellationToken)"/>:
    /// cascades into a sub-tree via an ODB lookup, then continues the walk
    /// on the remaining path (C's recursion, tree.c:972-974).
    /// </summary>
    private static async Task<GitTreeEntry?> EntryByPathSlowAsync(GitTree current, GitOid subTreeId, GitPath path, int posAfterComponent, CancellationToken cancellationToken)
    {
        GitTree? sub = await current.Owner!.Objects.LookupAsync<GitTree>(subTreeId, cancellationToken).ConfigureAwait(false);
        if (sub is null)
        {
            return null;
        }

        // posAfterComponent points at the separator slash — skip it so the
        // next component starts past it; a slash at the component start is
        // then an empty component (leading/doubled slash → "invalid tree
        // path given").
        return await sub.EntryByPathAsync(path.Slice(posAfterComponent + 1), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the next path component starting at <paramref name="pos"/>.
    /// All <c>Span</c> work is confined to this synchronous helper so no span is
    /// held across an <c>await</c> boundary in the caller.
    /// </summary>
    /// <returns>False if no more components; otherwise true with <paramref name="component"/>,
    /// <paramref name="more"/> (any further non-empty component exists),
    /// <paramref name="followedBySlash"/> (the component is immediately followed by a
    /// <c>/</c>, i.e. the path continues with a slash after it), and the updated
    /// <paramref name="nextPos"/>.</returns>
    private static bool NextComponent(GitPath path, int pos, out GitPath component, out bool more, out bool followedBySlash, out int nextPos)
    {
        ReadOnlySpan<byte> bytes = path.Span;

        // C (tree.c:929-932): an empty component (leading or doubled slash)
        // is "invalid tree path given" — subpath_len == 0 → GIT_ENOTFOUND.
        // Slash runs must NOT be skipped.
        if (pos < bytes.Length && bytes[pos] == (byte)'/')
        {
            component = default;
            more = false;
            followedBySlash = false;
            nextPos = pos;
            return false;
        }

        if (pos >= bytes.Length)
        {
            component = default;
            more = false;
            followedBySlash = false;
            nextPos = pos;
            return false;
        }

        int compStart = pos;
        while (pos < bytes.Length && bytes[pos] != (byte)'/')
        {
            pos++;
        }

        component = path.Slice(compStart, pos - compStart);
        followedBySlash = pos < bytes.Length && bytes[pos] == (byte)'/';

        // Determine whether more (non-empty) components follow.
        more = false;
        for (int j = pos; j < bytes.Length; j++)
        {
            if (bytes[j] != (byte)'/')
            {
                more = true;
                break;
            }
        }

        nextPos = pos;
        return true;
    }

    /// <summary>
    /// Returns an enumerator over the entries in tree order. Matches
    /// <c>git_tree_walk</c> for a single tree (non-recursive).
    /// </summary>
    public IEnumerator<GitTreeEntry> GetEnumerator()
    {
        foreach (GitTreeEntry e in _entries)
        {
            yield return e;
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Walks this tree and all sub-trees recursively, depth-first. Matches
    /// <c>git_tree_walk</c>. Sub-trees are loaded via <c>Owner.Objects.LookupAsync&lt;GitTree&gt;</c>.
    /// </summary>
    /// <param name="mode">Pre-order visits parents before children; post-order the reverse.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A sequence of (root-relative path, entry) tuples. Paths use <c>/</c> as separator.
    /// </returns>
    /// <remarks>
    /// <b>Async model:</b> <c>Walk</c> →
    /// <see cref="WalkAsync"/> returning <see cref="IAsyncEnumerable{T}"/> —
    /// the sync <c>yield return</c> iterator loaded sub-trees one by one, which
    /// is now async. C# forbids <c>yield</c>
    /// in async methods returning <c>Task</c>/<c>ValueTask</c>;
    /// <see cref="IAsyncEnumerable{T}"/> is the AOT-safe lazy alternative.
    /// </remarks>
    public IAsyncEnumerable<(GitPath path, GitTreeEntry entry)> WalkAsync(GitTreeWalkMode mode, CancellationToken cancellationToken = default)
        => WalkInternalAsync(mode, prefix: default, cancellationToken);

    private async IAsyncEnumerable<(GitPath path, GitTreeEntry entry)> WalkInternalAsync(
        GitTreeWalkMode mode,
        GitPath prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (GitTreeEntry entry in _entries)
        {
            GitPath path = prefix.IsEmpty ? entry.Name : prefix + "/" + entry.Name;

            if (mode == GitTreeWalkMode.PreOrder)
            {
                yield return (path, entry);
            }

            // Recurse into sub-trees (excluding submodules — gitlinks don't have a local tree).
            if (entry.IsTree && Owner is not null)
            {
                GitTree? sub = await Owner.Objects.LookupAsync<GitTree>(entry.Id, cancellationToken).ConfigureAwait(false);
                if (sub is not null)
                {
                    await foreach ((GitPath path, GitTreeEntry entry) item in sub.WalkInternalAsync(mode, path, cancellationToken).ConfigureAwait(false))
                    {
                        yield return item;
                    }
                }
            }

            if (mode == GitTreeWalkMode.PostOrder)
            {
                yield return (path, entry);
            }
        }
    }

    /// <summary>
    /// Parses a raw tree body (no header) into a <see cref="GitTree"/>. Matches
    /// <c>git_tree__parse_raw</c> (tree.c:394-443).
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> if the tree body is malformed.
    /// </exception>
    internal static GitTree Parse(GitRepository? owner, GitOid id, ReadOnlyMemory<byte> raw, GitHashAlgorithmKind algorithm)
    {
        ReadOnlySpan<byte> span = raw.Span;
        int oidSize = GitOid.SizeFor(algorithm);
        var entries = new List<GitTreeEntry>(capacity: 16);
        int offset = 0;

        while (offset < span.Length)
        {
            // 1. Parse octal mode until space.
            int modeStart = offset;
            while (offset < span.Length && span[offset] != ' ')
            {
                if (!IsOctalDigit(span[offset]))
                {
                    throw new GitException(
                        GitErrorCode.Invalid,
                        $"tree entry at byte {modeStart} has non-octal mode character 0x{span[offset]:X2}",
                        GitErrorCategory.Object);
                }

                offset++;
            }

            if (offset == modeStart)
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    $"tree entry at byte {offset} missing mode",
                    GitErrorCategory.Object);
            }

            int modeValue = ParseOctal(span[modeStart..offset]);
            if (modeValue is < 0 or > ushort.MaxValue)
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    $"tree entry at byte {modeStart} has out-of-range mode {modeValue}",
                    GitErrorCategory.Object);
            }

            // 2. Expect a space separator after the mode.
            if (offset >= span.Length || span[offset] != ' ')
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    $"tree entry at byte {offset} missing space after mode",
                    GitErrorCategory.Object);
            }

            offset++; // skip space

            // 3. Find the NUL terminator for the filename.
            int nulIndex = span[offset..].IndexOf((byte)'\0');
            if (nulIndex < 0)
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    $"tree entry at byte {offset} has no NUL terminator",
                    GitErrorCategory.Object);
            }

            if (nulIndex > ushort.MaxValue)
            {
                // C (tree.c:423): filename_len > UINT16_MAX fails with
                // "can't parse filename".
                throw new GitException(
                    GitErrorCode.Invalid,
                    "failed to parse tree: can't parse filename",
                    GitErrorCategory.Object);
            }

            if (nulIndex == 0)
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    $"tree entry at byte {offset} has empty filename",
                    GitErrorCategory.Object);
            }

            // Zero-copy slice into the retained raw buffer — byte-faithful, matches tree.c:435 `entry->filename = buffer`. Paths are never decoded to a string
            // on the object side; decoding happens only at display/FS egress (GitPath.ToString / ToFileSystemString).
            var name = GitPath.FromUtf8Bytes(raw.Slice(offset, nulIndex));
            offset += nulIndex + 1; // skip name + NUL

            // 4. Copy raw OID bytes.
            if (offset + oidSize > span.Length)
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    $"tree entry '{name}' has truncated OID (need {oidSize} bytes, have {span.Length - offset})",
                    GitErrorCategory.Object);
            }

            var oid = GitOid.FromRaw(span.Slice(offset, oidSize), algorithm);
            offset += oidSize;

            // 5. Construct the entry. C (tree.c:433-439) stores the RAW mode
            // bits (attr); the normalized value is computed on access only.
            GitFileMode mode = GitFileModeExtensions.Normalize((ushort)modeValue);
            GitObjectType type = GitFileModeExtensions.TypeFromMode((ushort)modeValue);
            entries.Add(new GitTreeEntry(mode, oid, name, type, (ushort)modeValue));
        }

        return new GitTree(owner, id, raw.Length, raw, [.. entries]);
    }

    // Two-phase binary-search helper: find the leftmost entry whose name shares
    // a byte prefix with `name`. Returns -1 if none found. Matches
    // `tree_key_search`'s homing step (tree.c:157-194) using `homing_search_cmp`
    // (memcmp over the shorter length; 0 when either is a byte-prefix of the
    // other). The linear scan in EntryByName resolves the exact match.
    private int HomingSearch(GitPath name)
    {
        int lo = 0;
        int hi = _entries.Length - 1;
        int best = -1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            GitPath entryName = _entries[mid].Name;

            int cmp = GitPath.CompareHoming(name, entryName);
            if (cmp == 0)
            {
                best = mid;
                // Continue searching backward to find the FIRST prefix match.
                hi = mid - 1;
            }
            else if (cmp < 0)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return best;
    }

    private static bool IsOctalDigit(byte b) => b is >= (byte)'0' and <= (byte)'7';

    private static int ParseOctal(ReadOnlySpan<byte> digits)
    {
        int value = 0;
        foreach (byte b in digits)
        {
            value = value * 8 + (b - '0');
            if (value > ushort.MaxValue)
            {
                return value; // caller validates upper bound
            }
        }

        return value;
    }

    /// <summary>
    /// Builds a tree from the flat entries of an <paramref name="index"/>,
    /// writing each tree object to the ODB bottom-up. Matches
    /// <c>git_tree__write_index</c> (tree.c:688-742) + <c>write_tree</c>
    /// (tree.c:586-686).
    /// </summary>
    /// <param name="repo">The repository (for ODB access).</param>
    /// <param name="index">The index to build from. Must not have conflicts.</param>
    /// <returns>The OID of the root tree.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Unmerged"/> if the index has conflicts.
    /// </exception>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static ValueTask<GitOid> WriteIndexAsync(GitRepository repo, GitIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(index);

        if (index.HasConflicts)
        {
            throw new GitException(
                GitErrorCode.Unmerged,
                "cannot create a tree from a not fully merged index",
                GitErrorCategory.Index);
        }

        // Fast path: if the index has a valid tree cache, reuse its OID
        // (majority for repeated writes).
        if (index.Tree is { } cached && cached.EntryCount >= 0)
        {
            return ValueTask.FromResult(cached.Oid);
        }

        return new ValueTask<GitOid>(WriteIndexSlowAsync(repo, index, cancellationToken));
    }

    /// <summary>Slow path of <see cref="WriteIndexAsync"/>: builds and writes the tree (ODB IO).</summary>
    private static async Task<GitOid> WriteIndexSlowAsync(GitRepository repo, GitIndex index, CancellationToken cancellationToken)
    {
        // Build the tree recursively from flat index entries.
        IReadOnlyList<GitIndexEntry> entries = index.GetSortedEntries();
        (GitOid oid, int _) = await WriteTreeRecursiveAsync(repo, index, entries, dirname: default, start: 0, cancellationToken).ConfigureAwait(false);

        // Rebuild the tree cache from the written tree.
        GitTree? tree = await repo.Objects.LookupAsync<GitTree>(oid, cancellationToken).ConfigureAwait(false);
        if (tree is not null)
        {
            index.SetTreeCache(await TreeCache.ReadTreeAsync(tree, repo.ObjectFormat, cancellationToken).ConfigureAwait(false));
        }

        return oid;
    }

    /// <summary>
    /// Recursive bottom-up tree builder. Matches <c>write_tree</c>
    /// (tree.c:586-686). Groups flat index entries into nested tree objects.
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="index">The index (for tree cache fast path).</param>
    /// <param name="entries">The sorted index entries.</param>
    /// <param name="dirname">The current directory prefix (e.g. <c>"src"</c> or <c>""</c> for root).</param>
    /// <param name="start">The index to start scanning from.</param>
    /// <returns>The written tree OID and the index where scanning stopped.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static ValueTask<(GitOid oid, int next)> WriteTreeRecursiveAsync(
        GitRepository repo,
        GitIndex index,
        IReadOnlyList<GitIndexEntry> entries,
        GitPath dirname,
        int start,
        CancellationToken cancellationToken)
    {
        // Tree cache fast path: if we have a cached entry for this dirname,
        // reuse its OID and skip to the next directory (majority for repeated
        // writes).
        if (index.Tree is { } cache)
        {
            TreeCache? node = cache.Get(dirname);
            if (node is not null && node.EntryCount >= 0)
            {
                return ValueTask.FromResult((node.Oid, FindNextDir(entries, dirname, start)));
            }
        }

        return new ValueTask<(GitOid oid, int next)>(WriteTreeRecursiveSlowAsync(repo, index, entries, dirname, start, cancellationToken));
    }

    /// <summary>Slow path of <see cref="WriteTreeRecursiveAsync"/>: builds the subtree (ODB IO).</summary>
    private static async Task<(GitOid oid, int next)> WriteTreeRecursiveSlowAsync(
        GitRepository repo,
        GitIndex index,
        IReadOnlyList<GitIndexEntry> entries,
        GitPath dirname,
        int start,
        CancellationToken cancellationToken)
    {
        using var bld = new GitTreeBuilder(repo);
        int dirnameLen = dirname.Length;
        int i = start;

        while (i < entries.Count)
        {
            GitIndexEntry entry = entries[i];
            GitPath entryPath = entry.Path;

            // Check if this entry is still in our (sub)tree.
            // 1. Path must be at least as long as dirname.
            // 2. Path must start with dirname (byte-wise prefix, memcmp-equivalent).
            // 3. If dirname is non-empty, the byte after dirname must be '/'.
            // Matches C: strlen(entry->path) < dirlen || memcmp(...) || path[dirlen] != '/'
            if (entryPath.Length < dirnameLen
                || GitPath.ComparePrefix(entryPath, dirname) != 0
                || (dirnameLen > 0 && entryPath.Span[dirnameLen] != (byte)'/'))
            {
                break;
            }

            // Extract the filename part (after dirname + optional '/').
            int filenameOffset = dirnameLen;
            if (dirnameLen > 0 && entryPath.Length > dirnameLen && entryPath.Span[dirnameLen] == (byte)'/')
            {
                filenameOffset = dirnameLen + 1;
            }

            ReadOnlySpan<byte> remainingPath = entryPath.Span[filenameOffset..];
            int nextSlash = remainingPath.IndexOf((byte)'/');
            if (nextSlash >= 0)
            {
                // Subdirectory: recursively write the subtree.
                GitPath subdir = entryPath.Slice(0, filenameOffset + nextSlash);
                (GitOid subOid, int next) = await WriteTreeRecursiveAsync(repo, index, entries, subdir, i, cancellationToken).ConfigureAwait(false);
                if (next < 0)
                {
                    throw new GitException(
                        GitErrorCode.Error,
                        $"failed to write subtree for '{subdir}'",
                        GitErrorCategory.Tree);
                }

                // Insert the subtree entry using its last path component.
                ReadOnlySpan<byte> subdirSpan = subdir.Span;
                int lastSlash = subdirSpan.LastIndexOf((byte)'/');
                GitPath lastComp = subdir.Slice(lastSlash + 1);
                await bld.InsertAsync(lastComp, subOid, GitFileMode.Tree, cancellationToken).ConfigureAwait(false);

                i = next; // continue from where the recursive call left off
            }
            else
            {
                // Leaf entry: insert directly.
                GitPath filename = entryPath.Slice(filenameOffset);
                await bld.InsertAsync(filename, entry.Id, entry.Mode, cancellationToken).ConfigureAwait(false);
                i++;
            }
        }

        GitOid oid = await bld.WriteAsync(cancellationToken).ConfigureAwait(false);
        return (oid, i);
    }

    /// <summary>
    /// Finds the first entry index that is NOT in <paramref name="dirname"/>.
    /// Matches <c>find_next_dir</c> (tree.c:459-474).
    /// </summary>
    private static int FindNextDir(IReadOnlyList<GitIndexEntry> entries, GitPath dirname, int start)
    {
        int dirlen = dirname.Length;
        for (int i = start; i < entries.Count; i++)
        {
            GitPath path = entries[i].Path;
            if (path.Length < dirlen
                || GitPath.ComparePrefix(path, dirname) != 0
                || (dirlen > 0 && path.Span[dirlen] != (byte)'/'))
            {
                return i;
            }
        }

        return entries.Count;
    }

    /// <summary>
    /// Creates a new tree by applying <paramref name="updates"/> to a
    /// <paramref name="baseline"/> tree. Matches <c>git_tree_create_updated</c>
    /// (tree.c:1156-1325). Uses a stack of <see cref="GitTreeBuilder"/> instances
    /// to walk the path hierarchy, writing subtrees bottom-up.
    /// </summary>
    /// <param name="repo">The repository (for ODB access).</param>
    /// <param name="baseline">The baseline tree to modify, or null for an empty tree.</param>
    /// <param name="updates">The updates to apply, sorted by path.</param>
    /// <returns>The OID of the new tree.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> on D/F conflict or unknown action.
    /// </exception>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<GitOid> CreateUpdatedAsync(GitRepository repo, GitTree? baseline, IReadOnlyList<GitTreeUpdate> updates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(updates);

        if (updates.Count == 0)
        {
            // No updates — just write the baseline (or empty tree).
            if (baseline is not null)
            {
                return baseline.Id;
            }

            return await repo.Objects.WriteAsync(GitObjectType.Tree, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
        }

        // Sort updates by path (C: git_vector_insert_sorted with compare_entries, tree.c:1159-1168). C REJECTS duplicate update paths via on_dup_entry
        // ("duplicate entries given for update", GIT_ERROR_TREE).
        var sorted = new List<GitTreeUpdate>(updates);
        TimSort.Sort(sorted, (a, b) => GitPath.Compare(a.Path, b.Path));
        for (int i = 1; i < sorted.Count; i++)
        {
            if (GitPath.Compare(sorted[i - 1].Path, sorted[i].Path) == 0)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "duplicate entries given for update",
                    GitErrorCategory.Tree);
            }
        }

        // Stack of (name, builder, tree) entries. The root entry has name=""
        // and a builder seeded from the baseline. `Tree` is the level's
        // ORIGINAL baseline tree (C's tree_stack_entry.tree) — the
        // create_popped_tree and walk-down D/F checks consult it first.
        var stack = new Stack<TreeStackEntry>();
        var rootBuilder = new GitTreeBuilder(repo, baseline);
        stack.Push(new TreeStackEntry(default, rootBuilder, baseline));

        GitPath? lastPath = null;

        foreach (GitTreeUpdate update in sorted)
        {
            // Compute common directory prefix with the previous update's path.
            int commonPrefix = lastPath is not null
                ? CommonDirLength(lastPath.Value, update.Path)
                : 0;

            // Steps up = number of slashes in the previous path after the common prefix.
            int stepsUp = lastPath is not null
                ? CountSlashes(lastPath.Value.Span[commonPrefix..])
                : 0;

            // Pop stack entries (writing each popped subtree into its parent).
            for (int j = 0; j < stepsUp; j++)
            {
                TreeStackEntry popped = stack.Pop();
                TreeStackEntry parent = stack.Peek();
                await CreatePoppedTreeAsync(parent, popped, cancellationToken).ConfigureAwait(false);
            }

            // Walk down to the update's directory, pushing new stack entries.
            GitPath pathRemainder = update.Path.Slice(commonPrefix);
            int pos = 0;
            while (TryGetNextComponent(pathRemainder, ref pos, out GitPath component))
            {
                TreeStackEntry top = stack.Peek();

                // C (tree.c:1220-1228): the D/F check consults the level's ORIGINAL baseline tree FIRST, then the builder — an entry removed by an earlier
                // update at this level still counts as a conflict.
                GitTreeEntry? entry = top.Tree?.EntryByName(component) ?? top.Builder.Get(component);

                // Check for D/F conflict: existing entry must be a tree.
                if (entry is { } existing && !existing.IsTree)
                {
                    throw new GitException(
                        GitErrorCode.Invalid,
                        $"D/F conflict when updating tree at '{update.Path.ToUtf8String()}'",
                        GitErrorCategory.Tree);
                }

                // Load the subtree from the ODB if it exists.
                GitTree? subtree = null;
                if (entry is { } treeEntry)
                {
                    subtree = await repo.Objects.LookupAsync<GitTree>(treeEntry.Id, cancellationToken).ConfigureAwait(false);
                }

                var newBuilder = new GitTreeBuilder(repo, subtree);
                stack.Push(new TreeStackEntry(component, newBuilder, subtree));
            }

            // Perform the update on the top builder.
            GitTreeBuilder topBuilder = stack.Peek().Builder;
            GitPath basename = Basename(update.Path);

            switch (update.Action)
            {
                case GitTreeUpdateAction.Upsert:
                    // Check type compatibility with existing entry.
                    if (topBuilder.Get(basename) is { } existingEntry)
                    {
                        GitObjectType existingType = existingEntry.Type;
                        GitObjectType newType = GitFileModeExtensions.TypeFromMode((ushort)update.FileMode);
                        if (existingType != newType)
                        {
                            throw new GitException(
                                GitErrorCode.Invalid,
                                $"cannot replace '{existingType}' with '{newType}' at '{update.Path.ToUtf8String()}'",
                                GitErrorCategory.Tree);
                        }
                    }

                    await topBuilder.InsertAsync(basename, update.Id, update.FileMode, cancellationToken).ConfigureAwait(false);
                    break;

                case GitTreeUpdateAction.Remove:
                    topBuilder.Remove(basename);
                    break;

                default:
                    throw new GitException(
                        GitErrorCode.Invalid,
                        $"unknown tree update action: {update.Action}",
                        GitErrorCategory.Tree);
            }

            lastPath = update.Path;
        }

        // Pop all remaining stack entries, writing each subtree into its parent.
        while (stack.Count > 1)
        {
            TreeStackEntry popped = stack.Pop();
            TreeStackEntry parent = stack.Peek();
            await CreatePoppedTreeAsync(parent, popped, cancellationToken).ConfigureAwait(false);
        }

        // Write the root tree.
        TreeStackEntry root = stack.Pop();
        GitOid rootOid = await root.Builder.WriteAsync(cancellationToken).ConfigureAwait(false);
        root.Builder.Dispose();
        return rootOid;
    }

    /// <summary> Writes the popped builder's tree and inserts it into the parent builder. If the popped builder is empty, removes the entry from the parent
    /// instead. Matches <c>create_popped_tree</c> (tree.c:1113-1148), including the D/F check against the parent's ORIGINAL tree (tree.c:1144-1151): inserting
    /// a subtree whose name is a non-tree in the baseline is rejected. </summary>
    private static async Task CreatePoppedTreeAsync(TreeStackEntry parent, TreeStackEntry popped, CancellationToken cancellationToken)
    {
        if (popped.Builder.EntryCount == 0)
        {
            // Empty subtree — remove the entry from the parent.
            parent.Builder.Remove(popped.Name);
        }
        else
        {
            // C (tree.c:1144-1151): "Error out if this would create a D/F
            // conflict in this update" — the parent's baseline tree must not
            // hold a non-tree with the popped name.
            GitTreeEntry? toReplace = parent.Tree?.EntryByName(popped.Name);
            if (toReplace is { } existing && !existing.IsTree)
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    "D/F conflict when updating tree",
                    GitErrorCategory.Tree);
            }

            GitOid oid = await popped.Builder.WriteAsync(cancellationToken).ConfigureAwait(false);
            await parent.Builder.InsertAsync(popped.Name, oid, GitFileMode.Tree, cancellationToken).ConfigureAwait(false);
        }

        popped.Builder.Dispose();
    }

    /// <summary> Computes the common directory prefix length of two paths — the position *after* the last shared <c>/</c>-delimited boundary. Matches
    /// <c>git_fs_path_common_dirlen</c> (fs_path.c:932-944) which returns <c>(dirsep - one) + 1</c>. Returns 0 if no shared directory boundary exists.
    /// Byte-faithful. NOTE: this differs from <see cref="PathByteHelpers.CommonDirLength"/> which returns the position OF the separator (not after it).
    /// </summary>
    private static int CommonDirLength(GitPath a, GitPath b)
    {
        ReadOnlySpan<byte> sa = a.Span;
        ReadOnlySpan<byte> sb = b.Span;
        int minLen = Math.Min(sa.Length, sb.Length);
        int lastSep = -1;

        for (int i = 0; i < minLen; i++)
        {
            if (sa[i] != sb[i])
            {
                break;
            }

            if (sa[i] == (byte)'/')
            {
                lastSep = i;
            }
        }

        return lastSep >= 0 ? lastSep + 1 : 0;
    }

    private static int CountSlashes(ReadOnlySpan<byte> s)
    {
        int count = 0;
        foreach (byte c in s)
        {
            if (c == (byte)'/')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary> Extracts the next directory component from <paramref name="path"/> — i.e., the part before the next <c>/</c>. Advances <paramref name="pos"/>
    /// past the component and its trailing slash. Returns false when no more directory components remain (the rest is the leaf basename). Matches
    /// <c>next_component</c> (tree.c:1101-1111). Byte-faithful. </summary>
    private static bool TryGetNextComponent(GitPath path, ref int pos, out GitPath component)
    {
        component = default;

        if (pos >= path.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> span = path.Span;
        int slash = span.Slice(pos).IndexOf((byte)'/');
        if (slash < 0)
        {
            // No slash — the remaining path is the leaf basename, not a directory.
            return false;
        }

        component = path.Slice(pos, slash);
        pos = slash + 1 + pos;
        return true;
    }

    private static GitPath Basename(GitPath path)
    {
        ReadOnlySpan<byte> span = path.Span;
        int lastSlash = span.LastIndexOf((byte)'/');
        return lastSlash < 0 ? path : path.Slice(lastSlash + 1);
    }

    private readonly record struct TreeStackEntry(GitPath Name, GitTreeBuilder Builder, GitTree? Tree);
}
