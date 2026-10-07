// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IO;

/// <summary>
/// Iterator that walks a <see cref="GitTree"/> recursively. Managed port of
/// the tree iterator backend in <c>src/libgit2/iterator.c</c> (lines 423-978).
/// </summary>
/// <remarks>
/// <para>
/// Uses a frame-stack pattern: each frame holds the entries of one
/// <see cref="GitTree"/> level. Auto-expand mode (default) pushes a new frame
/// when a tree entry is encountered, so the next <see cref="AdvanceAsync"/> dives
/// into the directory. Manual-expand mode (<c>DontAutoexpand</c>) requires
/// <see cref="AdvanceIntoAsync"/> to descend.
/// </para>
/// <para>
/// Case-insensitive mode coalesces sibling directories that differ only in
/// case (e.g. <c>A/</c> and <c>a/</c>) into one frame, merging their children
/// and deduplicating entries with case-insensitively equal paths.
/// </para>
/// </remarks>
internal sealed class TreeIterator : IteratorBase
{
    /// <summary>
    /// A single tree entry within a frame, tracking its parent path prefix.
    /// </summary>
    private sealed class TreeIterEntry
    {
        public required GitTreeEntry Entry { get; init; }
        public required GitPath ParentPath { get; init; }
    }

    /// <summary>
    /// A stack frame representing one level of the tree hierarchy.
    /// </summary>
    private sealed class TreeFrame
    {
        public required GitTree Tree { get; init; }
        public List<TreeIterEntry> Entries { get; init; } = [];
        public required GitPath Path { get; init; }
        public int NextIdx { get; set; }
        public TreeIterEntry? Current { get; set; }
        public bool Sorted { get; set; }
        public List<GitTree> SimilarTrees { get; } = [];
        public List<GitPath> SimilarPaths { get; } = [];
    }

    private readonly GitRepository? _repo;
    private readonly GitTree? _root;
    private readonly List<TreeFrame> _frames = [];
    private GitIndexEntry _entry;
    private GitPath _entryPath;

    private bool _disposed;

    /// <summary>
    /// Creates a tree iterator. If <paramref name="tree"/> is null, returns
    /// an <see cref="EmptyIterator"/>. Matches <c>git_iterator_for_tree</c>.
    /// </summary>
    public TreeIterator(GitTree? tree, GitRepository? repo, IteratorOptions? options = null)
        : base(IteratorType.Tree, options ?? IteratorOptions.Default)
    {
        _repo = repo;

        if (tree is null)
        {
            return;
        }

        _root = tree;
        _entry = default;

        // Resolve ignore_case from repo index if not explicitly set.
        if ((Flags & IteratorFlags.IgnoreCase) == 0 &&
            (Flags & IteratorFlags.DontIgnoreCase) == 0)
        {
            // Default to case-sensitive for tree iterators when no index is
            // available (matches C behavior when repo has no index). Callers
            // that pair a tree iterator with a case-insensitive workdir
            // iterator (diff tree-to-workdir) pass the case mode explicitly
            // via the options.
            Flags |= IteratorFlags.DontIgnoreCase;
        }

        Init();
    }

    /// <summary>Creates a tree iterator, or an empty iterator if tree is null.</summary>
    public static IIterator ForTree(GitTree? tree, GitRepository? repo, IteratorOptions? options = null)
    {
        return tree is null
            ? new EmptyIterator(options)
            : new TreeIterator(tree, repo, options);
    }

    private void Init()
    {
        if (_root is null)
        {
            return;
        }

        PushFrame(_root, null);
        Flags &= ~IteratorFlags.FirstAccess;
    }

    /// <inheritdoc/>
    public override ValueTask<GitIndexEntry?> CurrentAsync(CancellationToken cancellationToken)
    {
        if (!HasFirstAccess)
        {
            return new ValueTask<GitIndexEntry?>(AdvanceAsync(cancellationToken));
        }

        return ValueTask.FromResult<GitIndexEntry?>(_frames.Count == 0 ? null : _entry);
    }

    /// <inheritdoc/>
    public override async Task<GitIndexEntry?> AdvanceAsync(CancellationToken cancellationToken)
    {
        MarkAccessed();

        while (true)
        {
            if (_frames.Count == 0)
            {
                return null;
            }

            TreeFrame frame = _frames[^1];

            // No more entries in this frame — pop and continue.
            if (frame.NextIdx >= frame.Entries.Count)
            {
                PopFrame();
                continue;
            }

            // Lazy sort for icase coalesced frames.
            if (frame.NextIdx == 0 && !frame.Sorted)
            {
                SortFrameEntries(frame);
            }

            TreeIterEntry? prevEntry = frame.Current;
            TreeIterEntry entry = frame.Entries[frame.NextIdx];
            frame.NextIdx++;

            // Skip icase duplicates (entries with case-insensitively equal
            // names AND dirness — no parent-path tiebreak, matching the C
            // dedup via tree_iterator_entry_cmp_icase, iterator.c:792-795).
            if (IgnoreCase && prevEntry is not null &&
                TreeEntryCmp(prevEntry, entry, icase: true) == 0)
            {
                continue;
            }

            // Compute the full path.
            _entryPath = ComputePath(entry);

            // Range check: before start? Byte-faithful: Start/End are ASCII pathspec prefixes; the byte-wise GitPath.ComparePrefix compares raw bytes
            // (ASCII-safe). The tree iterator passes isSubmodule=false (iterator.c:801).
            if (!HasStarted(_entryPath, isSubmodule: false))
            {
                continue;
            }

            // Range check: after end?
            if (HasEnded(_entryPath))
            {
                return null;
            }

            // Pathlist check.
            if (!PathlistNextIs(_entryPath))
            {
                continue;
            }

            bool isTree = entry.Entry.IsTree;

            // If not including trees, skip tree entries.
            if (isTree && (Flags & IteratorFlags.IncludeTrees) == 0)
            {
                // Auto-expand: push frame so next advance dives in.
                if ((Flags & IteratorFlags.DontAutoexpand) == 0)
                {
                    await PushFrameForEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            // Set current entry.
            SetCurrent(frame, entry);

            // Auto-expand: push frame for next advance to dive in.
            if (isTree && (Flags & IteratorFlags.DontAutoexpand) == 0)
            {
                await PushFrameForEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            }

            return _entry;
        }
    }

    /// <inheritdoc/>
    public override async Task<GitIndexEntry?> AdvanceIntoAsync(CancellationToken cancellationToken)
    {
        if (_frames.Count == 0)
        {
            return null;
        }

        TreeFrame frame = _frames[^1];
        TreeIterEntry? prevEntry = frame.Current;

        if (prevEntry is not null)
        {
            if (!prevEntry.Entry.IsTree)
            {
                // C (iterator.c:871-873): advance_into on a non-tree current
                // entry returns 0 with *out = NULL and leaves the current
                // entry unchanged — it does NOT advance.
                return null;
            }

            await PushFrameForEntryAsync(prevEntry, cancellationToken).ConfigureAwait(false);
        }

        return await AdvanceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<(GitIndexEntry? Entry, IteratorStatus Status)> AdvanceOverAsync(CancellationToken cancellationToken)
    {
        GitIndexEntry? entry = await AdvanceAsync(cancellationToken).ConfigureAwait(false);
        return (entry, IteratorStatus.Normal);
    }

    /// <inheritdoc/>
    public override ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        while (_frames.Count > 0)
        {
            PopFrame();
        }

        _frames.Clear();
        _entryPath = default;
        Clear();

        if (_root is not null)
        {
            PushFrame(_root, null);
            Flags &= ~IteratorFlags.FirstAccess;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        while (_frames.Count > 0)
        {
            PopFrame();
        }

        _frames.Clear();
        base.Dispose();
    }

    // ===== Internal helpers =====

    private void SetCurrent(TreeFrame frame, TreeIterEntry entry)
    {
        frame.Current = entry;
        _entry = new GitIndexEntry
        {
            Path = _entryPath,
            Id = entry.Entry.Id,
            Mode = entry.Entry.Mode,
            Ctime = IndexTime.Zero,
            Mtime = IndexTime.Zero,
        };
    }

    /// <summary> Computes the full slash-joined path as raw bytes (byte-faithful — no UTF-8 decode/encode round-trip). The parent path already carries a
    /// trailing <c>/</c> for directories, so this is a plain byte concat, with a <c>/</c> appended for tree entries. </summary>
    private static GitPath ComputePath(TreeIterEntry entry)
    {
        GitPath name = entry.Entry.Name;
        GitPath path = entry.ParentPath.IsEmpty ? name : ConcatPath(entry.ParentPath, name);

        if (entry.Entry.IsTree)
        {
            path = AppendSeparator(path);
        }

        return path;
    }

    private static GitPath ConcatPath(GitPath a, GitPath b)
    {
        ReadOnlySpan<byte> sa = a.Span;
        ReadOnlySpan<byte> sb = b.Span;
        byte[] result = new byte[sa.Length + sb.Length];
        sa.CopyTo(result);
        sb.CopyTo(result.AsSpan(sa.Length));
        return GitPath.FromUtf8Bytes(result);
    }

    private static GitPath AppendSeparator(GitPath p)
    {
        ReadOnlySpan<byte> s = p.Span;
        byte[] result = new byte[s.Length + 1];
        s.CopyTo(result);
        result[^1] = (byte)'/';
        return GitPath.FromUtf8Bytes(result);
    }

    private void PushFrame(GitTree tree, TreeIterEntry? frameEntry)
    {
        GitPath path = frameEntry is null ? default : ComputePath(frameEntry);

        var frame = new TreeFrame
        {
            Tree = tree,
            Path = path,
            Sorted = !IgnoreCase,
        };

        for (int i = 0; i < tree.EntryCount; i++)
        {
            GitTreeEntry? te = tree.EntryByIndex(i);
            if (te is null)
            {
                continue;
            }

            frame.Entries.Add(new TreeIterEntry
            {
                Entry = te.Value,
                ParentPath = path,
            });
        }

        if (IgnoreCase)
        {
            frame.Sorted = false;
        }

        _frames.Add(frame);
    }

    private async Task PushFrameForEntryAsync(TreeIterEntry entry, CancellationToken cancellationToken)
    {
        if (_repo is null)
        {
            return;
        }

        GitTree? tree = await _repo.Objects.LookupAsync<GitTree>(entry.Entry.Id, cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return;
        }

        PushFrame(tree, entry);

        // Case-insensitive coalescing: merge sibling dirs with same icase name.
        if (IgnoreCase && _frames.Count >= 2)
        {
            TreeFrame parentFrame = _frames[^2];
            TreeFrame currentFrame = _frames[^1];
            await PushNeighborsAsync(parentFrame, currentFrame, entry.Entry.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Coalesces case-insensitively equal sibling directories into the
    /// current frame. Matches <c>tree_iterator_frame_push_neighbors</c>.
    /// </summary>
    private async Task PushNeighborsAsync(TreeFrame parentFrame, TreeFrame frame, GitPath filename, CancellationToken cancellationToken)
    {
        if (_repo is null)
        {
            return;
        }

        while (parentFrame.NextIdx < parentFrame.Entries.Count)
        {
            TreeIterEntry entry = parentFrame.Entries[parentFrame.NextIdx];

            if (GitPath.CompareIgnoreCase(filename, entry.Entry.Name) != 0)
            {
                break;
            }

            GitTree? tree = await _repo.Objects.LookupAsync<GitTree>(entry.Entry.Id, cancellationToken).ConfigureAwait(false);
            if (tree is null)
            {
                break;
            }

            parentFrame.SimilarTrees.Add(tree);

            GitPath similarPath = ComputePath(entry);
            parentFrame.SimilarPaths.Add(similarPath);

            // Merge this tree's children into the current frame.
            for (int i = 0; i < tree.EntryCount; i++)
            {
                GitTreeEntry? te = tree.EntryByIndex(i);
                if (te is null)
                {
                    continue;
                }

                frame.Entries.Add(new TreeIterEntry
                {
                    Entry = te.Value,
                    ParentPath = similarPath,
                });
            }

            parentFrame.NextIdx++;
        }
    }

    private void PopFrame()
    {
        if (_frames.Count == 0)
        {
            return;
        }

        TreeFrame frame = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);

        // Dispose similar trees (coalesced icase siblings).
        foreach (GitTree t in frame.SimilarTrees)
        {
            t.Dispose();
        }

        // Dispose the frame's tree (but not the root, which is owned by caller).
        // Actually, in C, git_tree_dup is called, so each frame owns its tree.
        // In C#, Lookup creates a new reference, so we should dispose.
        // But the root tree was passed in by the caller — don't dispose it.
        //
        // NOTE: when ObjectCache.AllowLargeTrees is active (blame walk), the
        // cache also owns this tree. This is safe: GitObject.Dispose() is
        // idempotent and GitTree has no DisposeCore override — dispose is a
        // no-op that sets a flag, and ObjectCache.Get() returns objects
        // regardless of _disposed. If a real DisposeCore override is ever
        // added to GitTree, this double-dispose (cache + iterator) must be
        // revisited.
        if (!ReferenceEquals(frame.Tree, _root))
        {
            frame.Tree.Dispose();
        }
    }

    private void SortFrameEntries(TreeFrame frame)
    {
        if (IgnoreCase)
        {
            frame.Entries.Sort(EntrySortIcase);
        }
        else
        {
            TimSort.Sort(frame.Entries, (a, b) =>
                GitPath.Compare(a.Entry.Name, b.Entry.Name));
        }

        frame.Sorted = true;
    }

    /// <summary>
    /// Case-insensitive frame sort comparator. Matches
    /// <c>tree_iterator_entry_sort_icase</c> (iterator.c:490-501):
    /// <c>tree_entry_cmp</c> (icase) first — with the directory-as-'/' rule
    /// from <c>git_fs_path_cmp</c> — then a case-sensitive parent-path
    /// tiebreak to stabilize equal names, then a case-sensitive
    /// <c>tree_entry_cmp</c>.
    /// </summary>
    private static int EntrySortIcase(TreeIterEntry a, TreeIterEntry b)
    {
        int diff = TreeEntryCmp(a, b, icase: true);
        if (diff != 0)
        {
            return diff;
        }

        // Tie-break: parent path (case-sensitive).
        diff = GitPath.Compare(a.ParentPath, b.ParentPath);
        if (diff != 0)
        {
            return diff;
        }

        return TreeEntryCmp(a, b, icase: false);
    }

    /// <summary>
    /// Compares two tree iterator entries by filename using the
    /// directory-as-trailing-'/' rule. Ports <c>tree_entry_cmp</c> →
    /// <c>git_fs_path_cmp</c> (iterator.c:479-488, fs_path.c:907-924): the
    /// names compare byte-wise (icase via ASCII fold) up to the common length;
    /// a tree's virtual terminator is <c>'/'</c> (0x2F), a non-tree's is NUL
    /// (0x00), so a tree sorts after a same-named blob. NO parent-path
    /// tiebreak — this is also the icase dedup comparator
    /// (<c>tree_iterator_entry_cmp_icase</c>).
    /// </summary>
    private static int TreeEntryCmp(TreeIterEntry a, TreeIterEntry b, bool icase)
    {
        GitPath nameA = a.Entry.Name;
        GitPath nameB = b.Entry.Name;
        int len = Math.Min(nameA.Length, nameB.Length);
        int cmp = icase
            ? GitPath.CompareIgnoreCase(nameA, nameB, len)
            : GitPath.Compare(nameA, nameB, len);
        if (cmp != 0)
        {
            return cmp;
        }

        int c1 = NameByteAtOrVirtual(nameA, len, a.Entry.IsTree);
        int c2 = NameByteAtOrVirtual(nameB, len, b.Entry.IsTree);
        return c1 < c2 ? -1 : c1 > c2 ? 1 : 0;
    }

    private static int NameByteAtOrVirtual(GitPath name, int index, bool isDir)
    {
        ReadOnlySpan<byte> span = name.Span;
        if (index < span.Length)
        {
            return span[index];
        }

        return isDir ? (byte)'/' : 0;
    }
}
