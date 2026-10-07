// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Status;
using LibGit2CS.Utils;

namespace LibGit2CS.IO;

/// <summary>
/// Iterator that walks a filesystem directory. Managed port of the
/// filesystem iterator backend in <c>src/libgit2/iterator.c</c>
/// (lines 1015-2045).
/// </summary>
/// <remarks>
/// <para>
/// This single class serves as both the workdir iterator and the filesystem
/// iterator — matching the C code where <c>filesystem_iterator</c> is shared
/// between <c>GIT_ITERATOR_WORKDIR</c> and <c>GIT_ITERATOR_FS</c>, differing
/// only in flags (<c>HONOR_IGNORES</c>, <c>IGNORE_DOT_GIT</c>).
/// </para>
/// <para>
/// Uses <see cref="System.IO.DirectoryInfo.EnumerateFileSystemInfos()"/> to replace the C
/// <c>readdir</c>+<c>stat</c> loop. Stat data is mapped from
/// <see cref="FileSystemInfo"/> properties. Submodule detection checks the
/// HEAD tree and index for <c>GIT_FILEMODE_COMMIT</c> entries.
/// </para>
/// <para>
/// Ignore evaluation (including <c>.gitignore</c> support) is handled by
/// <see cref="IgnoreContext"/>.
/// </para>
/// </remarks>
internal sealed class FilesystemIterator : IteratorBase
{
    private const int MaxDepth = 100;

    /// <summary> A filesystem entry within a frame. Holds the path, stat data, and pathlist match result. Byte-faithful: <see cref="Path"/> is a <see
    /// cref="GitPath"/> over the raw UTF-8 bytes of the relative path (matching libgit2's <c>filesystem_iterator</c> which stores <c>entry->path</c> as raw
    /// bytes; the bytes come from <see cref="FileSystemInfo.FullName"/> via the <see cref="GitPath.FromFileSystemString(string, bool)"/> ingress hook, which
    /// routes it through <see cref="PathPrecompose"/> when <c>core.precomposeunicode</c> is set). </summary>
    private sealed class FsEntry
    {
        public required GitPath Path { get; init; }
        public required GitFileMode Mode { get; init; }
        public required IndexTime Ctime { get; init; }
        public required IndexTime Mtime { get; init; }
        public required uint Dev { get; init; }
        public required uint Ino { get; init; }
        public required uint Uid { get; init; }
        public required uint Gid { get; init; }
        public required uint FileSize { get; init; }
        public GitOid Id { get; init; }
        public PathlistSearch Match { get; init; } = PathlistSearch.Full;
    }

    /// <summary>A stack frame representing one directory level.</summary>
    private sealed class FsFrame
    {
        public List<FsEntry> Entries { get; init; } = [];
        public int NextIdx { get; set; }
        public FsEntry? Current { get; set; }
        public IgnoreContext.Result IsIgnored { get; set; } = IgnoreContext.Result.Unchecked;
    }

    private readonly string _root;
    private readonly int _rootLen;
    private readonly GitRepository? _repo;
    private readonly GitIndex? _index;
    private readonly GitTree? _tree;
    private readonly IReadOnlyList<GitIndexEntry>? _indexSnapshot;
    private readonly IgnoreContext _ignores = new();
    private readonly List<FsFrame> _frames = [];
    private GitIndexEntry _entry;
    private IgnoreContext.Result _currentIsIgnored = IgnoreContext.Result.Unchecked;

    private bool _disposed;

    /// <inheritdoc/>
    /// <summary>
    /// The index associated with this workdir/filesystem iterator, or null.
    /// Used by <see cref="Diff.DiffGenerator"/> for the racy-git check.
    /// </summary>
    public override GitIndex? Index => _index;

    private FilesystemIterator(IteratorType type, string root, GitRepository? repo, GitIndex? index, GitTree? tree, IteratorOptions? options)
        : base(type, options ?? IteratorOptions.Default)
    {
        // Normalize the root to forward slashes: entry full paths are
        // normalized via Replace('\\', '/') before the StartsWith(_root)
        // prefix check, so a backslash-bearing root (Path.Join output on
        // Windows) would never match and every entry would be skipped.
        _root = root.Replace('\\', '/');
        _root = _root.EndsWith('/', StringComparison.Ordinal) ? _root : _root + "/";
        _rootLen = _root.Length;
        _repo = repo;
        _index = index;
        _tree = tree;

        // Workdir iterator sets honor-ignores and ignore-dot-git.
        if (type == IteratorType.Workdir)
        {
            Flags |= IteratorFlags.HonorIgnores | IteratorFlags.IgnoreDotGit;
        }

        // Resolve ignore_case from index if available.
        if ((Flags & IteratorFlags.IgnoreCase) == 0 &&
            (Flags & IteratorFlags.DontIgnoreCase) == 0)
        {
            if (_index is not null && _index.IgnoreCase)
            {
                Flags |= IteratorFlags.IgnoreCase;
            }
            else
            {
                Flags |= IteratorFlags.DontIgnoreCase;
            }
        }

        // Snapshot the index for submodule detection.
        if (_index is not null)
        {
            _indexSnapshot = _index.Snapshot();
        }

        // NOTE: ignore-context initialization is deferred to InitializeAsync
        // (called by the async factories) because it is now async.

        _entry = default;
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        // Initialize the ignore context.
        if (Type == IteratorType.Workdir && _repo is not null && _repo.Workdir is not null)
        {
            await _ignores.SetRepositoryAsync(_repo, cancellationToken).ConfigureAwait(false);
            await _ignores.InitAsync(_repo.Workdir, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Resolve core.precomposeunicode into the PrecomposeUnicode flag unless the caller pinned the decision via the options. Ports iterator_init_common
        // (iterator.c:111-167): when neither GIT_ITERATOR_PRECOMPOSE_UNICODE nor GIT_ITERATOR_DONT_PRECOMPOSE_UNICODE is set and we have a repo, read
        // core.precomposeunicode (default false, matching GIT_PRECOMPOSE_DEFAULT) and set the flag.
        if (_repo is not null
            && (Flags & IteratorFlags.PrecomposeUnicode) == 0
            && (Flags & IteratorFlags.DontPrecomposeUnicode) == 0)
        {
            bool precompose = await _repo.Config.GetBoolAsync(
                "core.precomposeunicode", false, cancellationToken).ConfigureAwait(false);
            if (precompose)
            {
                Flags |= IteratorFlags.PrecomposeUnicode;
            }
            else
            {
                Flags |= IteratorFlags.DontPrecomposeUnicode;
            }
        }

        await PushFrameAsync(null, cancellationToken).ConfigureAwait(false);
        Flags &= ~IteratorFlags.FirstAccess;
    }

    /// <summary> True when this iterator should precompose (NFD-&gt;NFC) filesystem-native entry names at ingress. Combines the resolved <see
    /// cref="IteratorFlags.PrecomposeUnicode"/> flag with the macOS-only runtime gate that lives inside <see cref="PathPrecompose.PrecomposeIfNeeded"/> (the
    /// <c>__APPLE__</c> / <c>GIT_PATH_NATIVE_ENCODING = "UTF-8-MAC"</c> gate at <c>fs_path.h:452-456</c>). </summary>
    private bool ShouldPrecompose => (Flags & IteratorFlags.PrecomposeUnicode) != 0;

    /// <summary>
    /// Creates a workdir iterator. Matches <c>git_iterator_for_workdir_ext</c>.
    /// </summary>
    public static async ValueTask<IIterator> ForWorkdirAsync(GitRepository repo, GitIndex? index, GitTree? tree, IteratorOptions? options = null, CancellationToken cancellationToken = default)
        => await ForWorkdirAsync(repo, index, tree, workdirRoot: null, options, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a workdir iterator rooted at <paramref name="workdirRoot"/>.
    /// When <paramref name="workdirRoot"/> is null, defaults to
    /// <see cref="GitRepository.Workdir"/>. Matches libgit2's
    /// <c>git_iterator_for_workdir_ext</c> with the <c>workdir_path</c>
    /// override used by checkout's <c>target_directory</c> (checkout.c:2601).
    /// </summary>
    public static async ValueTask<IIterator> ForWorkdirAsync(GitRepository repo, GitIndex? index, GitTree? tree, string? workdirRoot, IteratorOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        string root = workdirRoot ?? repo.Workdir
            ?? throw new GitException(GitErrorCode.BareRepo, "repository is bare", GitErrorCategory.Repository);

        var iter = new FilesystemIterator(IteratorType.Workdir, root, repo, index, tree, options);
        await iter.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return iter;
    }

    /// <summary>
    /// Creates a filesystem iterator for an arbitrary path. Matches
    /// <c>git_iterator_for_filesystem</c>.
    /// </summary>
    public static async ValueTask<IIterator> ForFilesystemAsync(string root, IteratorOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var iter = new FilesystemIterator(IteratorType.Filesystem, root, null, null, null, options);
        await iter.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return iter;
    }

    /// <inheritdoc/>
    public override async ValueTask<GitIndexEntry?> CurrentAsync(CancellationToken cancellationToken)
    {
        if (!HasFirstAccess)
        {
            return await AdvanceAsync(cancellationToken).ConfigureAwait(false);
        }

        return _frames.Count == 0 ? null : _entry;
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

            FsFrame frame = _frames[^1];

            if (frame.NextIdx >= frame.Entries.Count)
            {
                PopFrame();
                continue;
            }

            FsEntry entry = frame.Entries[frame.NextIdx];
            frame.NextIdx++;

            bool isDir = entry.Mode is GitFileMode.Tree or GitFileMode.GitLink;

            if (isDir && entry.Mode == GitFileMode.Tree)
            {
                // Auto-expand: push the directory.
                if ((Flags & IteratorFlags.DontAutoexpand) == 0)
                {
                    if (!await PushFrameAsync(entry, cancellationToken).ConfigureAwait(false))
                    {
                        continue; // directory disappeared — skip
                    }
                }

                // If not including trees, skip this directory entry.
                if ((Flags & IteratorFlags.IncludeTrees) == 0)
                {
                    continue;
                }
            }

            SetCurrent(entry);
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

        FsFrame frame = _frames[^1];
        FsEntry? prevEntry = frame.Current;

        if (prevEntry is not null)
        {
            if (prevEntry.Mode is not GitFileMode.GitLink and not GitFileMode.Tree)
            {
                // C (iterator.c:1678-1681): advance_into on a non-dir current
                // entry returns 0 with *out = NULL and leaves the current
                // entry unchanged — it does NOT advance.
                return null;
            }

            await PushFrameAsync(prevEntry, cancellationToken).ConfigureAwait(false);
        }

        return await AdvanceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<(GitIndexEntry? Entry, IteratorStatus Status)> AdvanceOverAsync(CancellationToken cancellationToken)
    {
        if (_frames.Count == 0)
        {
            return (null, IteratorStatus.Normal);
        }

        FsFrame frame = _frames[^1];
        FsEntry? currentEntry = frame.Current;

        if (currentEntry is null)
        {
            GitIndexEntry? advanced = await AdvanceAsync(cancellationToken).ConfigureAwait(false);
            return (advanced, IteratorStatus.Normal);
        }

        // If current is not a directory, just advance.
        if (currentEntry.Mode != GitFileMode.Tree)
        {
            IteratorStatus fileStatus = IteratorStatus.Normal;
            if (CurrentIsIgnored())
            {
                fileStatus = IteratorStatus.Ignored;
            }

            GitIndexEntry? advanced = await AdvanceAsync(cancellationToken).ConfigureAwait(false);
            return (advanced, fileStatus);
        }

        // Scan inside the directory looking for files.
        GitPath basePath = currentEntry.Path;
        IteratorStatus status = currentEntry.Match == PathlistSearch.IsParent
            ? IteratorStatus.Filtered
            : IteratorStatus.Empty;

        // C (iterator.c:1829, 1859): the scan loop is bounded at the TOP by
        // `entry && !prefixcomp(entry->path, base)` — when advance_into exits
        // the base directory (e.g. a directory whose last child is an empty
        // subdirectory pops both frames and yields the sibling), the loop
        // terminates with `status` still EMPTY/IGNORED and yields the sibling.
        GitIndexEntry entry = _entry;
        while (!entry.Path.IsEmpty && PathPrefixOf(entry.Path, basePath))
        {
            if (CurrentIsIgnored())
            {
                status = IteratorStatus.Ignored;
            }
            else if (entry.Mode == GitFileMode.Tree)
            {
                GitIndexEntry? result = await AdvanceIntoAsync(cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    entry = result.Value;
                    continue;
                }

                entry = default;
                break;
            }
            else
            {
                status = IteratorStatus.Normal;
                break;
            }

            GitIndexEntry? next = await AdvanceAsync(cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                entry = default;
                break;
            }

            entry = next.Value;
        }

        // Scan back to the base directory level. The C version guards with
        // `entry && !prefixcomp(...)` — entry is NULL when the scan above
        // exhausted the iterator (iterator.c:1858-1862). The default
        // GitIndexEntry now carries an empty GitPath (was a null string), so
        // guard on IsEmpty instead of null.
        while (!entry.Path.IsEmpty && PathPrefixOf(entry.Path, basePath))
        {
            GitIndexEntry? next = await AdvanceAsync(cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                return (null, status);
            }

            entry = next.Value;
        }

        _entry = entry;
        return (_entry, status);
    }

    /// <inheritdoc/>
    public override async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        while (_frames.Count > 0)
        {
            PopFrame();
        }

        _frames.Clear();
        Clear();

        await PushFrameAsync(null, cancellationToken).ConfigureAwait(false);
        Flags &= ~IteratorFlags.FirstAccess;
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
        _ignores.Free();
        base.Dispose();
    }

    /// <summary>
    /// Returns true if the current entry is ignored. Matches
    /// <c>filesystem_iterator_current_is_ignored</c>.
    /// </summary>
    public bool CurrentIsIgnored()
    {
        if (_currentIsIgnored == IgnoreContext.Result.Unchecked)
        {
            UpdateIgnored();
        }

        return _currentIsIgnored == IgnoreContext.Result.True;
    }

    /// <summary>
    /// Returns true if the current entry is contained in an ignored
    /// directory. Matches <c>git_iterator_current_tree_is_ignored</c>
    /// (iterator.c:1765-1774).
    /// </summary>
    public bool CurrentTreeIsIgnored()
    {
        if (Type != IteratorType.Workdir || _frames.Count == 0)
        {
            return false;
        }

        return _frames[^1].IsIgnored == IgnoreContext.Result.True;
    }

    /// <summary>
    /// Gets the full workdir path of the current entry. Matches
    /// <c>git_iterator_current_workdir_path</c>. Joins the OS-native
    /// root with the byte-faithful relative path via the FS-boundary transcode
    /// (<see cref="GitPath.ToFileSystemString"/>).
    /// </summary>
    public string? CurrentWorkdirPath()
    {
        if (_frames.Count == 0)
        {
            return null;
        }

        return _root + _entry.Path.ToFileSystemString();
    }

    // ===== Internal helpers =====

    private void UpdateIgnored()
    {
        _currentIsIgnored = _ignores.Lookup(_entry.Path);

        // Inherit from containing frame if not found.
        if (_currentIsIgnored <= IgnoreContext.Result.NotFound && _frames.Count > 0)
        {
            _currentIsIgnored = _frames[^1].IsIgnored;
        }
    }

    private void SetCurrent(FsEntry entry)
    {
        _entry = new GitIndexEntry
        {
            Path = entry.Path,
            Mode = entry.Mode,
            Id = entry.Id,
            Ctime = entry.Ctime,
            Mtime = entry.Mtime,
            Dev = entry.Dev,
            Ino = entry.Ino,
            Uid = entry.Uid,
            Gid = entry.Gid,
            FileSize = entry.FileSize,
        };

        _currentIsIgnored = IgnoreContext.Result.Unchecked;

        // Track the current FsEntry in the top frame so AdvanceOver can
        // access it (matches C's iterator frame->current). Without this,
        // AdvanceOver sees frame.Current == null and falls through to
        // Advance() without classifying the entry as ignored/untracked.
        if (_frames.Count > 0)
        {
            _frames[^1].Current = entry;
        }
    }

    /// <summary>
    /// Pushes a new frame for a directory. Returns false if the directory
    /// doesn't exist (race condition or permission issue). Matches
    /// <c>filesystem_iterator_frame_push</c>.
    /// </summary>
    private async ValueTask<bool> PushFrameAsync(FsEntry? frameEntry, CancellationToken cancellationToken)
    {
        if (_frames.Count >= MaxDepth)
        {
            throw new GitException(GitErrorCode.Error,
                $"directory nesting too deep ({_frames.Count})", GitErrorCategory.Repository);
        }

        // Build the full path for this directory. The entry path is
        // a byte-faithful GitPath; route through ToFileSystemString() at the
        // single FS-boundary join (ports git_str_joinpath(iter->root, entry->path)).
        string dirPath;
        if (frameEntry is null)
        {
            dirPath = _root;
        }
        else
        {
            // Remove trailing '/' from the entry path for path joining.
            GitPath entryPath = frameEntry.Path;
            ReadOnlySpan<byte> entrySpan = entryPath.Span;
            if (entrySpan.Length > 0 && entrySpan[^1] == (byte)'/')
            {
                entryPath = entryPath.Slice(0, entrySpan.Length - 1);
            }

            dirPath = Path.Join(_root, entryPath.ToFileSystemString());
        }

        if (!Directory.Exists(dirPath))
        {
            return false;
        }

        var frame = new FsFrame();

        // Enumerate directory contents.
        DirectoryInfo dirInfo;
        try
        {
            dirInfo = new DirectoryInfo(dirPath);
        }
        catch (UnauthorizedAccessException)
        {
            // Skip unreadable directories.
            _frames.Add(frame);
            return true;
        }

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = dirInfo.EnumerateFileSystemInfos();
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Skip unreadable directories.
            _frames.Add(frame);
            return true;
        }

        foreach (FileSystemInfo fsInfo in entries)
        {
            // Compute relative path from root. Ingress hook: the relative path is converted to a byte-faithful GitPath via
            // FromFileSystemString, with the precompose flag resolved from core.precomposeunicode in InitializeAsync (ports git_fs_path_diriter_next's iconv
            // call at fs_path.c:1466-1469). On non-macOS the transcode is a no-op inside PrecomposeCore.
            string fullPath = fsInfo.FullName.Replace('\\', '/');
            if (!fullPath.StartsWith(_root, StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = GitPath.FromFileSystemString(fullPath[_rootLen..], ShouldPrecompose);

            // Skip .git directory if flag is set.
            if ((Flags & IteratorFlags.IgnoreDotGit) != 0 && IsDotGit(relativePath))
            {
                continue;
            }

            // Pre-stat pathlist/range check.
            if (!ExaminePath(relativePath, frameEntry, out bool dirExpected, out PathlistSearch pathlistMatch))
            {
                continue;
            }

            // Determine mode.
            GitFileMode mode = GetFileMode(fsInfo);

            // Skip weird file types (FIFOs, sockets, etc.).
            if (mode == 0)
            {
                continue;
            }

            // Check for submodules (directories that are gitlinks).
            if (mode == GitFileMode.Tree)
            {
                if (await IsSubmoduleAsync(relativePath, cancellationToken).ConfigureAwait(false))
                {
                    mode = GitFileMode.GitLink;
                }
            }

            // Ensure the pathlist/start-range expectation lines up: if we
            // expected a directory (dir_expected) but this is not one, skip it.
            // (iterator.c:1468-1470 — the else branch of the S_ISDIR check.)
            else if (dirExpected)
            {
                continue;
            }

            // Append trailing '/' for directories (byte concat).
            GitPath entryPath = relativePath;
            if (mode == GitFileMode.Tree)
            {
                entryPath = AppendSeparator(entryPath);
            }

            // Compute stat data.
            (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size) = GetStatInfo(fsInfo);

            var id = default(GitOid);
            if ((Flags & IteratorFlags.IncludeHash) != 0 && mode != GitFileMode.Tree)
            {
                id = await ComputeOidAsync(fsInfo, cancellationToken).ConfigureAwait(false);
            }

            frame.Entries.Add(new FsEntry
            {
                Path = entryPath,
                Mode = mode,
                Ctime = ctime,
                Mtime = mtime,
                Dev = dev,
                Ino = ino,
                Uid = uid,
                Gid = gid,
                FileSize = size,
                Id = id,
                Match = pathlistMatch,
            });
        }

        // Sort entries. Byte-faithful: uses GitPath.Compare / CompareIgnoreCase (ports git__strcmp / strcasecmp, ASCII-only fold — not .NET's Unicode-aware
        // OrdinalIgnoreCase).
        TimSort.Sort(frame.Entries, (a, b) =>
            IgnoreCase
                ? GitPath.CompareIgnoreCase(a.Path, b.Path)
                : GitPath.Compare(a.Path, b.Path));

        _frames.Add(frame);

        // Evaluate whether this directory itself is ignored, then push its
        // ignore rules. Matches filesystem_iterator_frame_push_ignores
        // (iterator.c:1146-1178): the lookup runs BEFORE the directory's own
        // .gitignore rules are pushed, so a rule inside the directory never
        // applies to the directory itself.
        if ((Flags & IteratorFlags.HonorIgnores) != 0)
        {
            frame.IsIgnored = _ignores.Lookup(frameEntry?.Path ?? default);

            // Inherit ignored status from the parent frame if no rule
            // matched (iterator.c:1172-1174). The new frame is at ^1 (added
            // above); the parent is at ^2.
            if (frameEntry is not null &&
                frame.IsIgnored <= IgnoreContext.Result.NotFound &&
                _frames.Count > 1)
            {
                frame.IsIgnored = _frames[^2].IsIgnored;
            }

            if (frameEntry is not null)
            {
                // Push only the single directory component. libgit2 computes
                // this as `frame_entry->path + previous_frame->path_len`;
                // PushDirAsync appends its argument onto _dir, so passing the
                // full relative path double-counts the parent prefix and
                // corrupts nested .gitignore resolution at depth >= 2.
                // IgnoreContext.PushDirAsync is string-internal;
                // the component is ASCII (a directory basename), so the
                // ToUtf8String decode is safe.
                string component = DirectoryComponent(frameEntry.Path);
                await _ignores.PushDirAsync(component, cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }

    private void PopFrame()
    {
        if (_frames.Count == 0)
        {
            return;
        }

        _frames.RemoveAt(_frames.Count - 1);

        if ((Flags & IteratorFlags.HonorIgnores) != 0)
        {
            _ignores.PopDir();
        }
    }

    /// <summary> Pre-stat path/range/pathlist check. Returns false if the path should be skipped without stat'ing. Faithful port of
    /// <c>filesystem_iterator_examine_path</c> (iterator.c:1187-1249). Byte-faithful: all checks operate on raw <see cref="GitPath"/> bytes. </summary> <param
    /// name="path">The relative path (no trailing <c>/</c> — not yet stat'd, so the on-disk type is unknown).</param> <param name="frameEntry">The parent
    /// directory's entry (whose <see cref="FsEntry.Match"/> enables the "parent was explicitly included" pathlist short-circuit), or <see langword="null"/> at
    /// the root.</param> <param name="dirExpected">Receives <see langword="true"/> when the start prefix or pathlist implies this path should be a directory
    /// (so a non-directory stat result should later be rejected).</param> <param name="pathlistMatch">Receives the pathlist match result (<see
    /// cref="IteratorBase.PathlistSearch.Full"/> when no pathlist is active).</param> <returns><see langword="true"/> if the path survives the range/pathlist filters and
    /// should be stat'd.</returns>
    private bool ExaminePath(GitPath path, FsEntry? frameEntry, out bool dirExpected, out PathlistSearch pathlistMatch)
    {
        dirExpected = false;
        pathlistMatch = PathlistSearch.Full;

        ReadOnlySpan<byte> pathSpan = path.Span;
        int pathLen = pathSpan.Length;

        if (Start is GitPath start)
        {
            ReadOnlySpan<byte> startSpan = start.Span;

            // strncomp(path, start, path_len) — bounded by the (un-stat'd) path
            // length, not the start length. (iterator.c:1202)
            int cmp = IgnoreCase
                ? GitPath.CompareIgnoreCase(path, start, pathLen)
                : GitPath.Compare(path, start, pathLen);

            // We haven't stat'd `path` yet, so we don't know if it's a
            // directory. Special-case a path that may be a directory matching
            // the start prefix. (iterator.c:1208-1214)
            if (cmp == 0)
            {
                if (pathLen < startSpan.Length && startSpan[pathLen] == (byte)'/')
                {
                    dirExpected = true;
                }
                else if (pathLen < startSpan.Length && startSpan[pathLen] != 0)
                {
                    cmp = -1;
                }
            }

            if (cmp < 0)
            {
                return false;
            }
        }

        if (End is GitPath end)
        {
            ReadOnlySpan<byte> endSpan = end.Span;

            // strncomp(path, end, end_len). (iterator.c:1221)
            int cmp = IgnoreCase
                ? GitPath.CompareIgnoreCase(path, end, endSpan.Length)
                : GitPath.Compare(path, end, endSpan.Length);

            if (cmp > 0)
            {
                return false;
            }
        }

        // If we have a pathlist, examine now to avoid a stat for paths we don't
        // care about. (iterator.c:1230-1244)
        if (HasPathlist)
        {
            PathlistSearch match;

            // If our parent was explicitly included, so too are we.
            if (frameEntry is not null && frameEntry.Match != PathlistSearch.IsParent)
            {
                match = PathlistSearch.Full;
            }
            else
            {
                match = PathlistSearchResult(path);
            }

            if (match == PathlistSearch.None)
            {
                return false;
            }

            // A directory match means we expect this path to be a directory.
            if (match is PathlistSearch.IsDir or PathlistSearch.IsParent)
            {
                dirExpected = true;
            }

            pathlistMatch = match;
        }

        return true;
    }

    /// <summary> Checks if the path is a <c>.git</c> directory. Matches <c>filesystem_iterator_is_dot_git</c>. Byte-faithful: compares raw bytes with
    /// ASCII-only case fold (ports <c>git__strcasecmp</c>, not .NET's Unicode-aware <c>OrdinalIgnoreCase</c>). </summary>
    private static bool IsDotGit(GitPath path)
    {
        ReadOnlySpan<byte> span = path.Span;
        if (span.Length < 4)
        {
            return false;
        }

        // Check for ".git" (case-insensitive, ASCII fold).
        if (span.Length == 4 && IsDotGitBytes(span))
        {
            return true;
        }

        // Check for "foo/.git" — the last component is ".git".
        if (span.Length >= 5 && span[^5] == (byte)'/' && IsDotGitBytes(span[^4..]))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Compares a 4-byte span against <c>.git</c> with ASCII-only case fold
    /// (ports <c>git__strcasecmp</c> over 4 bytes).
    /// </summary>
    private static bool IsDotGitBytes(ReadOnlySpan<byte> span)
    {
        ReadOnlySpan<byte> dotGit = ".git"u8;
        for (int i = 0; i < 4; i++)
        {
            if (char.ToLowerInvariant((char)span[i]) != dotGit[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary> Returns the last path segment of a directory entry path, with a trailing '/'. This is the single-component delta that <see
    /// cref="IgnoreContext.PushDirAsync"/> expects — mirroring libgit2's <c>frame_entry->path + previous_frame->path_len</c>. e.g. ".agents/skills/" ->
    /// "skills/", "top/" -> "top/". Byte-faithful: operates on raw bytes; the returned string is UTF-8-decoded (the component is an ASCII directory basename,
    /// so the decode is safe; <c>PushDirAsync</c> is string-internal). </summary>
    private static string DirectoryComponent(GitPath entryPath)
    {
        ReadOnlySpan<byte> span = entryPath.Span;
        ReadOnlySpan<byte> trimmed = span.Length > 0 && span[^1] == (byte)'/' ? span[..^1] : span;
        int lastSlash = trimmed.LastIndexOf((byte)'/');
        ReadOnlySpan<byte> name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return System.Text.Encoding.UTF8.GetString(name) + "/";
    }

    /// <summary>
    /// Checks if a path is a submodule (gitlink) by looking it up in the
    /// HEAD tree and index. Matches <c>filesystem_iterator_is_submodule</c>
    /// (<c>iterator.c:1104-1144</c>). The HEAD-tree check uses the internal
    /// <see cref="GitTree.EntryByPathAsync(GitPath, CancellationToken)"/>
    /// overload; the index check binary-searches the snapshot via
    /// <see cref="GitIndex.SnapshotFind"/> (ports
    /// <c>git_index_snapshot_find</c> + <c>git_vector_bsearch2</c> —
    /// <c>index.c:343</c>, <c>index.c:3840</c>), with <see cref="IteratorBase.IgnoreCase"/>
    /// selecting the case mode to match the snapshot's sort order (mirrors
    /// <c>iter->entry_srch</c> selection at <c>iterator.c:41</c>).
    /// </summary>
    private async ValueTask<bool> IsSubmoduleAsync(GitPath path, CancellationToken cancellationToken)
    {
        // Check HEAD tree.
        if (_tree is not null)
        {
            GitTreeEntry? entry = await _tree.EntryByPathAsync(path, cancellationToken).ConfigureAwait(false);
            if (entry is { } e && e.Mode == GitFileMode.GitLink)
            {
                return true;
            }
        }

        // Check index snapshot via binary search.
        if (_indexSnapshot is not null)
        {
            int idx = GitIndex.SnapshotFind(_indexSnapshot, path, stage: 0, IgnoreCase);
            if (idx >= 0 && _indexSnapshot[idx].Mode == GitFileMode.GitLink)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary> Returns true if <paramref name="path"/> starts with <paramref name="prefix"/> (a byte-wise prefix match, dispatching on <see
    /// cref="IteratorBase.IgnoreCase"/>). Ports <c>git__prefixcmp</c> / <c>git__prefixcmp_icase</c>. Used by <see cref="AdvanceOverAsync"/>'s directory-walk bounds check.
    /// </summary>
    private bool PathPrefixOf(GitPath path, GitPath prefix)
        => IgnoreCase
            ? GitPath.ComparePrefixIgnoreCase(path, prefix) == 0
            : GitPath.ComparePrefix(path, prefix) == 0;

    /// <summary> Appends a trailing <c>'/'</c> to <paramref name="path"/> (byte concat). Used for directory entries. </summary>
    private static GitPath AppendSeparator(GitPath path)
    {
        ReadOnlySpan<byte> s = path.Span;
        byte[] result = new byte[s.Length + 1];
        s.CopyTo(result);
        result[^1] = (byte)'/';
        return GitPath.FromUtf8Bytes(result);
    }

    /// <summary>
    /// Maps a <see cref="FileSystemInfo"/> to a <see cref="GitFileMode"/>.
    /// Matches <c>git_futils_canonical_mode</c> + the workdir iterator's
    /// special-file skip (iterator.c:1448-1451): FIFOs/sockets/devices are
    /// skipped (mode 0) - C checks <c>!S_ISDIR &amp;&amp; !S_ISREG &amp;&amp; !S_ISLNK</c>
    /// against the stat mode.
    /// </summary>
    private static GitFileMode GetFileMode(FileSystemInfo fsInfo)
    {
        FileAttributes attrs = fsInfo.Attributes;

        // check the
        // reparse-point bit BEFORE the directory bit. On .NET a symlink to
        // a directory reports both 'Directory' and 'ReparsePoint'; C stats
        // each entry with lstat (filesystem_iterator_frame_push /
        // git_fs_path_diriter_stat, iterator.c) and never follows directory
        // symlinks, and its Windows lstat maps every reparse point to
        // S_IFLNK (w32_util.c:110-114), so a reparse point must always win.
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            // Symlinks on Unix, junctions on Windows.
            return GitFileMode.Symlink;
        }

        if ((attrs & FileAttributes.Directory) != 0)
        {
            return GitFileMode.Tree;
        }

        // C (iterator.c:1448-1451): special file types (FIFOs, sockets,
        // character/block devices) are skipped - they are neither regular
        // files, directories, nor symlinks. The BCL maps them to "Normal",
        // so the native stat type bits are consulted (0 = unavailable).
        ushort typeBits = NativeStat.GetStat(fsInfo).TypeBits;
        // S_IFREG | S_IFDIR | S_IFLNK are the only accepted types.
        if (typeBits is not (0 or 0x8000 or 0x4000 or 0xA000))
        {
            return (GitFileMode)0; // skipped by the caller
        }

        // Check executable bit on Unix.
        if (!OperatingSystem.IsWindows())
        {
            var info = fsInfo as FileInfo;
            if (info is not null &&
                (info.UnixFileMode & UnixFileMode.UserExecute) != 0)
            {
                return GitFileMode.Executable;
            }
        }

        return GitFileMode.Regular;
    }

    /// <summary>
    /// Extracts stat data from a <see cref="FileSystemInfo"/>. Delegates to
    /// <see cref="StatUtil.GetStatInfo"/> to avoid duplication. Reads
    /// sub-second precision (nanoseconds) from the filesystem, matching
    /// <c>GIT_USE_NSEC</c>.
    /// </summary>
    private static (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size) GetStatInfo(FileSystemInfo fsInfo)
        => StatUtil.GetStatInfo(fsInfo);

    /// <summary>
    /// Computes the OID of a file's content (with "blob" header). Matches
    /// <c>filesystem_iterator_entry_hash</c>.
    /// </summary>
    private async Task<GitOid> ComputeOidAsync(FileSystemInfo fsInfo, CancellationToken cancellationToken)
    {
        if (!File.Exists(fsInfo.FullName))
        {
            return default;
        }

        using PooledByteBufferWriter content = await AsyncFileIO.ReadAllBytesToBufferAsync(fsInfo.FullName, cancellationToken).ConfigureAwait(false);

        return GitOid.ComputeOid(GitObjectType.Blob, content.WrittenSpan, OidType);
    }

    private GitHashAlgorithmKind OidType => _repo?.ObjectFormat ?? GitHashAlgorithmKind.Sha1;
}
