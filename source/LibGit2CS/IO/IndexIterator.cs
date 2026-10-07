// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IO;

/// <summary>
/// Iterator that walks the entries of a git <see cref="GitIndex"/>.
/// Managed port of the index iterator backend in <c>iterator.c</c>
/// (lines 2048-2311).
/// </summary>
/// <remarks>
/// <para>
/// The index is a flat list of entries. When <c>IncludeTrees</c> is
/// requested, this iterator synthesizes virtual "pseudo-tree" directory
/// entries on-the-fly by watching for directory-prefix changes between
/// consecutive file entries.
/// </para>
/// <para>
/// In <c>DontAutoexpand</c> mode, pseudo-tree entries are returned but not
/// descended into — the caller must use <see cref="AdvanceIntoAsync"/> to enter
/// a directory. Otherwise, directories are auto-expanded (skipped over,
/// their children returned directly).
/// </para>
/// </remarks>
internal sealed class IndexIterator : IteratorBase
{
    private readonly IReadOnlyList<GitIndexEntry> _entries;
    private readonly GitIndex? _sourceIndex;
    private int _nextIdx;
    private GitIndexEntry? _entry;
    private GitIndexEntry _treeEntry;
    private GitPath _treeBuf;
    private bool _skipTree;

    /// <summary>
    /// Creates an index iterator. If <paramref name="index"/> is null,
    /// returns an <see cref="EmptyIterator"/>. Matches
    /// <c>git_iterator_for_index</c>.
    /// </summary>
    public IndexIterator(GitIndex? index, GitRepository? _, IteratorOptions? options = null)
        : base(IteratorType.Index, options ?? IteratorOptions.Default)
    {
        if (index is null)
        {
            _entries = [];
            return;
        }

        // Keep a reference to the source index for the racy-git check
        // (DiffGenerator.EntryNewerThanIndex reads index.Stamp).
        _sourceIndex = index;

        // Take a sorted snapshot of the index entries.
        _entries = index.Snapshot();

        // Resolve ignore_case from the index.
        if ((Flags & IteratorFlags.IgnoreCase) == 0 &&
            (Flags & IteratorFlags.DontIgnoreCase) == 0)
        {
            if (index.IgnoreCase)
            {
                Flags |= IteratorFlags.IgnoreCase;
            }
            else
            {
                Flags |= IteratorFlags.DontIgnoreCase;
            }
        }

        // C (iterator.c:2060-2070, git_iterator_for_index): the snapshot is
        // sorted with the ITERATOR's comparator, not the index's native order.
        // A caller that forces DONT_IGNORE_CASE (tree-to-index diff) against a
        // case-insensitive index must get a case-sensitively sorted snapshot,
        // or the lock-step walk desyncs.
        if ((Flags & IteratorFlags.DontIgnoreCase) != 0 && index.IgnoreCase)
        {
            var sorted = new List<GitIndexEntry>(_entries);
            sorted.Sort(static (a, b) => GitPath.Compare(a.Path, b.Path));
            _entries = sorted;
        }

        _treeEntry = default;
        Flags &= ~IteratorFlags.FirstAccess;
    }

    /// <summary>Creates an index iterator, or empty if index is null.</summary>
    public static IIterator ForIndex(GitIndex? index, GitRepository? repo, IteratorOptions? options = null)
    {
        return index is null
            ? new EmptyIterator(options)
            : new IndexIterator(index, repo, options);
    }

    /// <inheritdoc/>
    /// <summary>The source index (for the racy-git check).</summary>
    public override GitIndex? Index => _sourceIndex;

    /// <inheritdoc/>
    public override ValueTask<GitIndexEntry?> CurrentAsync(CancellationToken cancellationToken)
    {
        if (!HasFirstAccess)
        {
            return new ValueTask<GitIndexEntry?>(AdvanceAsync(cancellationToken));
        }

        return ValueTask.FromResult(_entry);
    }

    /// <inheritdoc/>
    /// <summary>
    /// Interface-shape wrapper: the index advance is purely in-memory
    /// (never yields), so it is a sync <see cref="Advance"/> core returning
    /// <see cref="Task.FromResult{TResult}(TResult)"/>.
    /// </summary>
    public override Task<GitIndexEntry?> AdvanceAsync(CancellationToken cancellationToken)
        => Task.FromResult(Advance());

    /// <summary>Synchronous index-advance core (never yields — in-memory walk).</summary>
    private GitIndexEntry? Advance()
    {
        MarkAccessed();

        while (true)
        {
            if (_nextIdx >= _entries.Count)
            {
                _entry = null;
                return null;
            }

            // Skip pseudo-tree contents if not expanding.
            if (_skipTree)
            {
                SkipPseudotree();
                continue;
            }

            GitIndexEntry entry = _entries[_nextIdx];

            // Range check. Gitlink entries pass isSubmodule=true so the legacy
            // start-suffixed-with-'/' match applies (iterator.c:2153-2155).
            if (!HasStarted(entry.Path, isSubmodule: entry.Mode == GitFileMode.GitLink))
            {
                _nextIdx++;
                continue;
            }

            if (HasEnded(entry.Path))
            {
                _entry = null;
                return null;
            }

            // Pathlist check.
            if (!PathlistNextIs(entry.Path))
            {
                _nextIdx++;
                continue;
            }

            // Skip conflicts unless including them.
            if (entry.IsConflict && (Flags & IteratorFlags.IncludeConflicts) == 0)
            {
                _nextIdx++;
                continue;
            }

            // If including trees, try to synthesize a pseudo-tree entry.
            if ((Flags & IteratorFlags.IncludeTrees) != 0 &&
                TryCreatePseudotree(entry.Path))
            {
                _skipTree = (Flags & IteratorFlags.DontAutoexpand) != 0;
                _entry = _treeEntry;
                return _entry;
            }

            _nextIdx++;
            _entry = entry;
            return _entry;
        }
    }

    /// <inheritdoc/>
    public override Task<GitIndexEntry?> AdvanceIntoAsync(CancellationToken cancellationToken)
    {
        // C (iterator.c:2208-2213): a non-tree current entry returns 0 with
        // *out = NULL — advance_into does NOT advance on a file entry.
        if (_treeEntry.Mode != GitFileMode.Tree)
        {
            return Task.FromResult<GitIndexEntry?>(null);
        }

        _skipTree = false;
        return AdvanceAsync(cancellationToken);
    }

    /// <inheritdoc/>
    /// <summary>
    /// Synchronous (never yields): the index advance is an in-memory walk,
    /// so this implements the interface's <c>Task</c> shape with a
    /// <see cref="Task.FromResult{TResult}(TResult)"/> completion.
    /// </summary>
    public override Task<(GitIndexEntry? Entry, IteratorStatus Status)> AdvanceOverAsync(CancellationToken cancellationToken)
    {
        // C (iterator.c:2228-2235): index_iterator_current is called FIRST —
        // on a fresh iterator this triggers the initial advance — and a
        // first-entry pseudo-tree (S_ISDIR) is skipped before advancing. The
        // cached _entry is null before the first access, so we must advance
        // to populate it before the pseudo-tree check.
        if (!HasFirstAccess)
        {
            _ = Advance();
        }

        if (_entry is not null && _entry.Value.Mode == GitFileMode.Tree)
        {
            SkipPseudotree();
        }

        GitIndexEntry? entry = Advance();
        return Task.FromResult((entry, IteratorStatus.Normal));
    }

    /// <inheritdoc/>
    public override ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        Clear();
        _nextIdx = 0;
        _skipTree = false;
        _entry = null;
        _treeEntry = default;
        _treeBuf = default;
        Flags &= ~IteratorFlags.FirstAccess;
        return ValueTask.CompletedTask;
    }

    // ===== Internal helpers =====

    /// <summary> Synthesizes a pseudo-tree entry if the given path is in a different directory from the previous entry. Matches
    /// <c>index_iterator_create_pseudotree</c>. Byte-faithful: operates on raw <see cref="GitPath"/> bytes. </summary> <returns>True if a pseudo-tree was
    /// created.</returns>
    private bool TryCreatePseudotree(GitPath path)
    {
        GitPath prevPath = _entry?.Path ?? default;

        // Find the common directory prefix length.
        int commonLen = CommonDirLen(prevPath, path);
        ReadOnlySpan<byte> pathSpan = path.Span;
        ReadOnlySpan<byte> relativeSpan = pathSpan[commonLen..];

        // If there's no '/' in the relative part, no new directory.
        int dirSep = relativeSpan.IndexOf((byte)'/');
        if (dirSep < 0)
        {
            return false;
        }

        // The pseudo-tree path is the path up to and including the first '/'
        // in the relative portion.
        _treeBuf = path.Slice(0, commonLen + dirSep + 1);

        _treeEntry = new GitIndexEntry
        {
            Path = _treeBuf,
            Mode = GitFileMode.Tree,
            Id = default,
            Ctime = IndexTime.Zero,
            Mtime = IndexTime.Zero,
        };

        return true;
    }

    /// <summary> Skips all entries under the current pseudo-tree directory. Matches <c>index_iterator_skip_pseudotree</c>. Byte-faithful: uses <see
    /// cref="LibGit2CS.IO.GitPath.ComparePrefix(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/> / <see cref="LibGit2CS.IO.GitPath.ComparePrefixIgnoreCase(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/> (ports <c>git__prefixcmp</c>). </summary>
    private void SkipPseudotree()
    {
        while (true)
        {
            _nextIdx++;

            if (_nextIdx >= _entries.Count)
            {
                break;
            }

            GitIndexEntry nextEntry = _entries[_nextIdx];

            // Check if the entry path starts with the pseudo-tree prefix.
            if (IgnoreCase)
            {
                if (GitPath.ComparePrefixIgnoreCase(nextEntry.Path, _treeBuf) != 0)
                {
                    break;
                }
            }
            else
            {
                if (GitPath.ComparePrefix(nextEntry.Path, _treeBuf) != 0)
                {
                    break;
                }
            }
        }

        _skipTree = false;
    }

    /// <summary> Computes the length of the common directory prefix of two paths. Matches <c>git_fs_path_common_dirlen</c> (fs_path.c:932-944): the last
    /// position where BOTH bytes are <c>'/'</c>, plus one — 0 when the paths share no <c>'/'</c> (a file <c>abc</c> followed by <c>abc/def</c> has a common dir
    /// length of 0, NOT 4, so the iterator still synthesizes the <c>abc/</c> pseudo-tree for the file/dir conflict). Byte-faithful: operates on raw bytes.
    /// </summary>
    private static int CommonDirLen(GitPath a, GitPath b)
    {
        ReadOnlySpan<byte> sa = a.Span;
        ReadOnlySpan<byte> sb = b.Span;
        int dirsep = -1;
        int minLen = Math.Min(sa.Length, sb.Length);

        for (int i = 0; i < minLen; i++)
        {
            if (sa[i] == (byte)'/' && sb[i] == (byte)'/')
            {
                dirsep = i;
            }
            else if (sa[i] != sb[i])
            {
                break;
            }
        }

        return dirsep < 0 ? 0 : dirsep + 1;
    }
}
