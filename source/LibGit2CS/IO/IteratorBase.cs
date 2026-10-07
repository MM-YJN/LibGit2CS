// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Index;

namespace LibGit2CS.IO;

/// <summary> Shared base state and helpers for all iterator backends. Managed port of the <c>git_iterator</c> base struct + the shared static helpers in
/// <c>src/libgit2/iterator.c</c> (lines 29-356). </summary> <remarks> <para> Provides range checking (<c>start</c>/<c>end</c> prefix bounds), pathlist matching
/// (literal path inclusion list), and case-sensitivity management. Concrete backends implement <see cref="CurrentAsync"/>/<see cref="AdvanceAsync"/>/ <see
/// cref="AdvanceIntoAsync"/>/<see cref="AdvanceOverAsync"/>/<see cref="ResetAsync"/>. </para> <para> The pathlist, range prefixes,
/// and all comparison helpers are byte-faithful <see cref="GitPath"/> over UTF-8 bytes. The <c>string</c>-typed <see cref="IteratorOptions.Start"/>/<see
/// cref="End"/>/ <c>PathList</c> are converted to <see cref="GitPath"/> once in the constructor. This ports libgit2's raw-byte
/// <c>git__strcmp</c>/<c>git__prefixcmp</c> iterator comparisons (<c>iterator.c:129-136,233-309</c>) end-to-end; the prior <c>StringComparison.Ordinal*</c>
/// ternaries were Unicode-aware (a latent divergence on non-ASCII paths). </para> </remarks>
internal abstract class IteratorBase : IIterator
{
    private readonly List<GitPath> _pathlist = [];
    private readonly GitPathComparer _pathlistComparer;
    private int _pathlistWalkIdx;
    private bool _started;
    private bool _ended;

    /// <summary>
    /// Creates an iterator with the given type and options.
    /// </summary>
    protected IteratorBase(IteratorType type, IteratorOptions options)
    {
        Type = type;
        Flags = options.Flags;

        // DONT_AUTOEXPAND implies INCLUDE_TREES.
        if ((Flags & IteratorFlags.DontAutoexpand) != 0)
        {
            Flags |= IteratorFlags.IncludeTrees;
        }

        // IteratorOptions is byte-faithful GitPath-typed.
        Start = options.Start;
        End = options.End;

        _pathlistComparer = IgnoreCase
            ? GitPathComparer.CaseInsensitive
            : GitPathComparer.CaseSensitive;

        if (options.PathList is { Length: > 0 } paths)
        {
            foreach (GitPath p in paths)
            {
                _pathlist.Add(p);
            }

            _pathlist.Sort(_pathlistComparer);
        }
    }

    /// <inheritdoc/>
    public IteratorType Type { get; }

    /// <inheritdoc/>
    public IteratorFlags Flags { get; protected set; }

    /// <inheritdoc/>
    public bool IgnoreCase => (Flags & IteratorFlags.IgnoreCase) != 0;

    /// <inheritdoc/>
    /// <summary>
    /// Default: no index. Overridden by <see cref="FilesystemIterator"/> and
    /// <see cref="IndexIterator"/> which carry the source index.
    /// </summary>
    public virtual GitIndex? Index => null;

    /// <summary>The lower-bound path prefix, or null.</summary>
    protected GitPath? Start { get; }

    /// <summary>The upper-bound path prefix, or null.</summary>
    protected GitPath? End { get; }

    /// <summary>True if the iterator has been accessed at least once.</summary>
    protected bool HasFirstAccess => (Flags & IteratorFlags.FirstAccess) != 0;

    /// <summary>
    /// True when a pathlist (literal inclusion list) is in effect. Backends use
    /// this to short-circuit pathlist matching when no pathlist was supplied
    /// (ports the <c>iter->base.pathlist.length</c> guard in
    /// <c>filesystem_iterator_examine_path</c>, iterator.c:1230).
    /// </summary>
    protected bool HasPathlist => _pathlist.Count > 0;

    /// <summary>
    /// Gets the byte-wise <see cref="GitPath"/> comparator for this iterator's
    /// case mode (ports <c>STRCMP_CASESELECT</c>). Used by the pathlist sort
    /// and the prefix checks.
    /// </summary>
    protected GitPathComparer PathComparer => _pathlistComparer;

    /// <summary>Marks the iterator as accessed (sets FirstAccess flag).</summary>
    protected void MarkAccessed()
    {
        Flags |= IteratorFlags.FirstAccess;
    }

    /// <summary>Clears the iterator state for reset.</summary>
    protected virtual void Clear()
    {
        _started = false;
        _ended = false;
        _pathlistWalkIdx = 0;
        Flags &= ~IteratorFlags.FirstAccess;
    }

    /// <summary> Checks whether the given path has crossed the start boundary. Matches <c>iterator_has_started</c> (iterator.c:178-213). Byte-faithful: the
    /// prefix check uses <see cref="LibGit2CS.IO.GitPath.ComparePrefix(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/> (ports <c>git__prefixcmp</c>); the directory-recurse check uses the bounded case-select compare
    /// (ports <c>strncomp</c>). </summary> <param name="path">The current iterator path (directories carry a trailing <c>/</c> on the tree side; gitlink
    /// entries do not).</param> <param name="isSubmodule"><see langword="true"/> for a gitlink index entry (<c>S_ISGITLINK</c>); enables the legacy
    /// <c>start</c>-suffixed-with-<c>/</c> match. The tree iterator always passes <see langword="false"/>.</param> <returns><see langword="true"/> if the path
    /// is at or past the start boundary, or is a directory beneath which <c>start</c> lives (so the iterator should recurse into it), or is a submodule
    /// matching a <c>/</c>-suffixed <c>start</c>.</returns>
    protected bool HasStarted(GitPath path, bool isSubmodule)
    {
        if (Start is null || _started)
        {
            return true;
        }

        // The starting path is generally a prefix — we have started once we
        // are prefixed by it. (iterator.c:189)
        _started = PrefixCompare(path, Start.Value) >= 0;

        if (_started)
        {
            return true;
        }

        ReadOnlySpan<byte> pathSpan = path.Span;
        int pathLen = pathSpan.Length;
        ReadOnlySpan<byte> startSpan = Start.Value.Span;
        int startLen = startSpan.Length;

        // Submodule: support `start` suffixed with '/' for legacy reasons —
        // match gitlink `submod` against start `submod/`. (iterator.c:200-202)
        // NOTE: faithful to C — no path-vs-start byte comparison is performed,
        // only length equality plus the trailing slash; the C reference omits
        // a content comparison here as well.
        if (isSubmodule && startLen > 0 && pathLen == startLen - 1 &&
            startSpan[startLen - 1] == (byte)'/')
        {
            return true;
        }

        // Directory: if the current path is a directory (trailing '/') and the
        // starting path is _beneath_ it, recurse into the directory even though
        // we have not yet "started". (iterator.c:208-210). This is what lets a
        // pathspec'd tree walk descend through ancestor directories when
        // Start == End == <full nested file path>.
        if (pathLen > 0 && pathSpan[pathLen - 1] == (byte)'/' &&
            Compare(path, Start.Value, pathLen) == 0)
        {
            return true;
        }

        return false;
    }

    /// <summary> Checks whether the given path has crossed the end boundary. Matches <c>iterator_has_ended</c>. Byte-faithful. </summary>
    protected bool HasEnded(GitPath path)
    {
        if (End is null)
        {
            return false;
        }

        if (_ended)
        {
            return true;
        }

        _ended = PrefixCompare(path, End.Value) > 0;
        return _ended;
    }

    /// <summary> Walker-mode pathlist matching: walks the sorted pathlist alongside sorted iterator entries. Returns true if the path is covered by a pathlist
    /// entry. Matches <c>iterator_pathlist_next_is</c>. Byte-faithful. </summary>
    protected bool PathlistNextIs(GitPath path)
    {
        if (_pathlist.Count == 0)
        {
            return true;
        }

        ReadOnlySpan<byte> pathSpan = path.Span;
        int pathLen = pathSpan.Length;

        // Drop trailing slash for comparison.
        if (pathLen > 0 && pathSpan[pathLen - 1] == (byte)'/')
        {
            pathLen--;
        }

        for (int i = _pathlistWalkIdx; i < _pathlist.Count; i++)
        {
            GitPath p = _pathlist[i];
            ReadOnlySpan<byte> pSpan = p.Span;
            int pLen = pSpan.Length;

            if (pLen > 0 && pSpan[pLen - 1] == (byte)'/')
            {
                pLen--;
            }

            int cmpLen = Math.Min(pathLen, pLen);
            // Compare pathlist entry against path (NOT path against pathlist)
            // — the sign determines the walk direction. diff < 0 means the
            // pathlist entry sorts before the path → advance the walk index.
            // diff > 0 means the pathlist entry sorts after the path → no
            // match (the path cannot appear later in the sorted pathlist).
            int diff = Compare(p, path, cmpLen);

            if (diff == 0)
            {
                // Matches iterator_pathlist_next_is (iterator.c:262-277).
                //
                // Branch 1 — the pathlist entry has NO trailing slash and is
                // fully consumed: matches a file OR a directory/prefix. The
                // entry's original span length equals cmpLen (p[cmp_len]=='\0').
                if (pSpan.Length == cmpLen &&
                    (pathLen == cmpLen ||
                     (pathSpan.Length > cmpLen && pathSpan[cmpLen] == (byte)'/')))
                {
                    return true;
                }

                // Branch 2 — the pathlist entry HAS a trailing slash at
                // cmpLen (p[cmp_len]=='/'): matches only directories.
                if (cmpLen < pSpan.Length && pSpan[cmpLen] == (byte)'/' &&
                    pathSpan.Length > cmpLen && pathSpan[cmpLen] == (byte)'/')
                {
                    return true;
                }
            }
            else if (diff < 0)
            {
                // This pathlist entry sorts before the path — advance.
                _pathlistWalkIdx++;
                continue;
            }
            else
            {
                // This pathlist entry sorts after the path — no match.
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Pathlist search result. Matches <c>iterator_pathlist_search_t</c>.
    /// </summary>
    protected enum PathlistSearch
    {
        /// <summary>No pathlist match.</summary>
        None = 0,

        /// <summary>Exact file match.</summary>
        IsFile = 1,

        /// <summary>Directory match (pathlist has trailing <c>/</c>).</summary>
        IsDir = 2,

        /// <summary>Path is a parent of a pathlist entry.</summary>
        IsParent = 3,

        /// <summary>No pathlist (match all).</summary>
        Full = 4,
    }

    /// <summary> Binary-search pathlist matching. Matches <c>iterator_pathlist_search</c>. Byte-faithful: uses <see cref="LibGit2CS.IO.GitPath.Compare(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/>/ <see
    /// cref="LibGit2CS.IO.GitPath.CompareIgnoreCase(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/> + <see cref="LibGit2CS.IO.GitPath.ComparePrefix(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/>. </summary>
    protected PathlistSearch PathlistSearchResult(GitPath path)
    {
        if (_pathlist.Count == 0)
        {
            return PathlistSearch.Full;
        }

        ReadOnlySpan<byte> pathSpan = path.Span;
        int pathLen = pathSpan.Length;

        // Binary search for the path in the sorted pathlist.
        int idx = _pathlist.BinarySearch(path, _pathlistComparer);
        if (idx >= 0)
        {
            // Exact match found.
            if (pathLen > 0 && pathSpan[pathLen - 1] == (byte)'/')
            {
                return PathlistSearch.IsDir;
            }

            return PathlistSearch.IsFile;
        }

        // Not found — check if path is a directory prefix of any pathlist entry.
        idx = ~idx;
        while (idx < _pathlist.Count)
        {
            GitPath p = _pathlist[idx];
            ReadOnlySpan<byte> pSpan = p.Span;

            // Keep entries p where *p starts with path* (C iterator.c:329:
            // iter->prefixcomp(p, path) == 0 — the icase-aware prefix
            // comparator). The argument order matters: testing whether the
            // *path* starts with the entry would make the IsDir/IsParent
            // branches unreachable, so workdir pathlist filtering would never
            // descend parent directories.
            if (PrefixCompare(p, path) != 0)
            {
                break;
            }

            if (pathLen < pSpan.Length && pSpan[pathLen] == (byte)'/')
            {
                return (pathLen + 1 == pSpan.Length)
                    ? PathlistSearch.IsDir
                    : PathlistSearch.IsParent;
            }

            if (pathLen < pSpan.Length && pSpan[pathLen] > (byte)'/')
            {
                break;
            }

            idx++;
        }

        return PathlistSearch.None;
    }

    /// <summary> Compares path against prefix. Returns &lt; 0 if path &lt; prefix, 0 if path starts with prefix, &gt; 0 if path &gt; prefix. Matches
    /// <c>git__prefixcmp</c> (util.c:241-255): compares only up to the prefix length and returns 0 if the prefix is exhausted (i.e. path starts with prefix),
    /// regardless of any remaining characters in path. Byte-faithful: uses <see cref="char.ToLowerInvariant"/> for the icase variant (ASCII-only fold,
    /// not .NET's Unicode-aware fold). </summary>
    private int PrefixCompare(GitPath path, GitPath prefix)
        => IgnoreCase
            ? GitPath.ComparePrefixIgnoreCase(path, prefix)
            : GitPath.ComparePrefix(path, prefix);

    /// <summary>
    /// Byte-wise compare bounded by <paramref name="length"/>. Dispatches on
    /// <see cref="IgnoreCase"/> (ports <c>STRCMP_CASESELECT</c>). Used by
    /// <see cref="PathlistNextIs"/> for the pathlist walk comparison.
    /// </summary>
    private int Compare(GitPath a, GitPath b, int length)
        => IgnoreCase
            ? GitPath.CompareIgnoreCase(a, b, length)
            : GitPath.Compare(a, b, length);

    /// <inheritdoc/>
    public abstract ValueTask<GitIndexEntry?> CurrentAsync(CancellationToken cancellationToken);
    public abstract Task<GitIndexEntry?> AdvanceAsync(CancellationToken cancellationToken);
    public abstract Task<GitIndexEntry?> AdvanceIntoAsync(CancellationToken cancellationToken);
    public abstract Task<(GitIndexEntry? Entry, IteratorStatus Status)> AdvanceOverAsync(CancellationToken cancellationToken);
    public abstract ValueTask ResetAsync(CancellationToken cancellationToken);

    /// <inheritdoc/>
    public virtual void Dispose()
    {
    }
}
