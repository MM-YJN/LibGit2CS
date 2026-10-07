// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Config;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.Status;

/// <summary>
/// Per-directory git ignore evaluation state. Managed port of
/// <c>git_ignores</c> in <c>src/libgit2/ignore.h</c> + the
/// <c>git_ignore__for_path</c>/<c>_push_dir</c>/<c>_pop_dir</c>/<c>_free</c>/
/// <c>_lookup</c> functions in <c>src/libgit2/ignore.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Maintains a stack of per-directory <c>.gitignore</c> files from the
/// current directory up to the repo root, plus the internal rules
/// (<c>.</c>, <c>..</c>, <c>.git</c> + user-added rules) and the global
/// rules (<c>.git/info/exclude</c> + <c>core.excludesfile</c>).
/// </para>
/// <para>
/// <b>Lifecycle:</b>
/// <list type="number">
/// <item><see cref="InitAsync"/> — load internal + per-directory (walk up from
/// start dir to workdir root) + global ignore files.</item>
/// <item><see cref="PushDirAsync"/> — descend into a subdirectory; load its
/// <c>.gitignore</c>.</item>
/// <item><see cref="LibGit2CS.Status.IgnoreContext.Lookup(string)"/> — evaluate a path against the current rule
/// stack.</item>
/// <item><see cref="PopDir"/> — ascend; pop the topmost <c>.gitignore</c>.</item>
/// <item><see cref="Free"/> — release all state.</item>
/// </list>
/// </para>
/// <para>
/// Uses <see cref="FnMatchPattern"/> + <see cref="WildMatch"/> for pattern
/// matching, and reads
/// <c>core.excludesfile</c>/<c>core.ignorecase</c> via
/// <see cref="GitConfiguration"/>.
/// </para>
/// </remarks>
internal sealed class IgnoreContext
{
    /// <summary>Ignore evaluation result. Matches the constants in <c>ignore.h</c>.</summary>
    public enum Result
    {
        /// <summary>Not yet evaluated (lazy default). Matches <c>GIT_IGNORE_NOTCHECKED = -2</c>.</summary>
        Unchecked = -2,

        /// <summary>No ignore rule found for this path. Matches <c>GIT_IGNORE_NOTFOUND = -1</c>.</summary>
        NotFound = -1,

        /// <summary>Path is NOT ignored (matched a <c>!</c>-negated rule). Matches <c>GIT_IGNORE_FALSE = 0</c>.</summary>
        False = 0,

        /// <summary>Path IS ignored. Matches <c>GIT_IGNORE_TRUE = 1</c>.</summary>
        True = 1,
    }

    private const string IgnoreFileName = ".gitignore";
    private const string InfoExcludeName = "exclude";

    private GitRepository? _repo;
    private string? _workdir;
    private bool _ignoreCase;

    // The current absolute directory path (with trailing '/').
    private string _dir = string.Empty;
    // Offset into _dir where the workdir root ends.
    private int _dirRoot;

    // Per-directory .gitignore files, from root down to current dir
    // (children first? No — loaded bottom-up during Init via path_walk_up,
    //  so index 0 is the starting dir, last is the workdir root). Lookup
    //  via LookupPathIsIgnored iterates forward (children first) and pops
    //  as it walks up; Lookup (the internal API) iterates in reverse
    //  (parents first) without popping.
    private readonly List<IgnoreFile> _ignPath = [];

    // Global ignores: .git/info/exclude (index 0) then core.excludesfile (index 1).
    private readonly List<IgnoreFile> _ignGlobal = [];

    // Internal rules (default + user-added via Ignore.AddRule).
    private IgnoreFile? _ignInternal;

    private int _depth;
    private bool _initialized;

    /// <summary>
    /// Initializes ignore data for a workdir. Matches
    /// <c>git_ignore__for_path</c> (ignore.c:292-376). Loads internal
    /// defaults, walks up from the start directory to the workdir root
    /// loading <c>.gitignore</c> files, then loads <c>.git/info/exclude</c>
    /// and <c>core.excludesfile</c>.
    /// </summary>
    /// <param name="workdir">The workdir root path (absolute, no trailing slash required).</param>
    /// <param name="startRelPath">
    /// The relative path (from workdir) of the file/dir to initialize for.
    /// The directory portion is resolved and used as the starting directory
    /// for the upward <c>.gitignore</c> walk. Pass empty string for the
    /// workdir root.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask InitAsync(string workdir, string startRelPath = "", CancellationToken cancellationToken = default)
    {
        _workdir = workdir.EndsWith('/', StringComparison.Ordinal) ? workdir : workdir + "/";
        _dirRoot = _workdir.Length;
        _dir = _workdir + startRelPath;
        _initialized = true;

        await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        await LoadPerDirectoryIgnoresAsync(cancellationToken).ConfigureAwait(false);
        await LoadGlobalIgnoresAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the internal ignore file with the default rules.
    /// Matches <c>get_internal_ignores</c> + the
    /// <c>GIT_IGNORE_DEFAULT_RULES</c> seeding (ignore.c:275-290).
    /// </summary>
    private async ValueTask LoadInternalAsync(CancellationToken cancellationToken)
    {
        // Use the shared per-repo internal ignore file so that
        // Ignore.AddRule/ClearInternalRules affect all active contexts.
        _ignInternal = _repo is not null
            ? await _repo.IgnoreState.GetInternalAsync(cancellationToken).ConfigureAwait(false)
            : new IgnoreFile("[internal]exclude");
    }

    /// <summary>
    /// Walks up from the current directory to the workdir root loading
    /// <c>.gitignore</c> files. Matches <c>git_fs_path_walk_up</c> +
    /// <c>push_one_ignore</c> (ignore.c:268-273, 347-352). Files are loaded
    /// deepest-first (index 0 = start dir, last = workdir root).
    /// </summary>
    private async ValueTask LoadPerDirectoryIgnoresAsync(CancellationToken cancellationToken)
    {
        if (_workdir is null || _repo is null)
        {
            return;
        }

        // Walk up from _dir to _workdir, loading .gitignore at each level.
        // Build the list of directories from deepest to shallowest.
        var dirs = new List<string>();
        string current = _dir;
        while (current.Length >= _dirRoot)
        {
            dirs.Add(current);
            if (current.Length <= _dirRoot)
            {
                break;
            }

            // Go up one directory.
            string parent = current[..^1];
            int lastSlash = parent.LastIndexOf('/', StringComparison.Ordinal);
            if (lastSlash >= _dirRoot - 1)
            {
                current = parent[..(lastSlash + 1)];
            }
            else
            {
                current = _workdir;
            }

            if (current == _workdir && dirs[0] != _workdir)
            {
                // Include the workdir root itself.
                if (!dirs.Contains(_workdir))
                {
                    dirs.Add(_workdir);
                }

                break;
            }
        }

        foreach (string dir in dirs)
        {
            await PushIgnoreFileForDirAsync(dir, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary> Port of <c>git_fs_path_resolve_relative</c> (fs_path.c:822-899) over the directory portion of a relative path: "." segments are dropped and
    /// ".." segments pop the previous segment (a leading ".." that cannot pop is kept as a base prefix). Matches the dirname resolution in <c>ignores_init</c>
    /// (ignore.c:322-326) —. </summary>
    private static string ResolveRelativeDir(string dir)
    {
        if (dir.Length == 0)
        {
            return dir;
        }

        var segments = new List<string>();
        int start = 0;
        while (start < dir.Length)
        {
            int slash = dir.IndexOf('/', start, StringComparison.Ordinal);
            string seg = slash < 0 ? dir[start..] : dir[start..slash];
            if (seg.Length > 0)
            {
                if (seg == ".")
                {
                    // Singleton dot: dropped.
                }
                else if (seg == "..")
                {
                    if (segments.Count > 0 && segments[^1] != "..")
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }
                    else
                    {
                        // No segment to pop: "../" becomes a base prefix.
                        segments.Add("..");
                    }
                }
                else
                {
                    segments.Add(seg);
                }
            }

            if (slash < 0)
            {
                break;
            }

            start = slash + 1;
        }

        string result = string.Join('/', segments);
        return result.Length == 0 ? string.Empty : result + "/";
    }

    /// <summary>
    /// Loads <c>.git/info/exclude</c> and <c>core.excludesfile</c> into
    /// <c>_ignGlobal</c>. Matches ignore.c:354-368.
    /// </summary>
    private async ValueTask LoadGlobalIgnoresAsync(CancellationToken cancellationToken)
    {
        if (_repo is null)
        {
            return;
        }

        // .git/info/exclude. C (ignore.c:355) uses GIT_REPOSITORY_ITEM_INFO, whose parent is the COMMONDIR with a gitdir fallback (repository.c:60) — linked
        // worktrees share the main repo's info/exclude.
        string infoExclude = Path.Join(_repo.CommonDir, "info", InfoExcludeName);
        IgnoreFile? infoFile = await IgnoreFile.LoadFromPathAsync(infoExclude, _ignoreCase, cancellationToken).ConfigureAwait(false);
        if (infoFile is not null)
        {
            _ignGlobal.Add(infoFile);
        }

        // core.excludesfile. C (attrcache.c:334-347, attr_cache__lookup_path): the XDG ignore file ($XDG_CONFIG_HOME/git/ignore) is the fallback ONLY when the
        // key is ABSENT — a present-but-empty or lone value is taken literally (no global excludes file; the idiomatic way to disable it).
        string? excludesFile = null;
        GitConfigEntry? excludesEntry = await _repo.Config.GetEntryAsync("core.excludesfile", cancellationToken).ConfigureAwait(false);
        if (excludesEntry is { Value: { } excludesValue })
        {
            // C (attrcache.c:338-343, attr_cache__lookup_path): core.excludesfile expands ONLY a leading "~/"; any other leading-~ value is taken VERBATIM (a
            // literal relative path) — git_config__parse_path's "~user is not supported" rejection does not apply at this site.
            excludesFile = excludesValue.StartsWith("~/", StringComparison.Ordinal)
                ? _repo.Context.Dirs.ExpandHomedirFile(excludesValue.AsSpan(2).ToString())
                : excludesValue;
        }
        else if (excludesEntry is null)
        {
            excludesFile = _repo.Context.Dirs.FindXdgFile("ignore");
        }

        if (!string.IsNullOrEmpty(excludesFile))
        {
            IgnoreFile? globalFile = await IgnoreFile.LoadFromPathAsync(excludesFile, _ignoreCase, cancellationToken).ConfigureAwait(false);
            if (globalFile is not null)
            {
                _ignGlobal.Add(globalFile);
            }
        }
    }

    /// <summary>
    /// Pushes a <c>.gitignore</c> file for a directory onto the per-dir
    /// stack. Matches <c>push_one_ignore</c>/<c>push_ignore_file</c>
    /// (ignore.c:245-273): the depth counts *directories walked*
    /// (incremented unconditionally, even when the ignore file is absent —
    /// C increments in <c>push_one_ignore</c> and <c>git_ignore__push_dir</c>
    /// before the file lookup).
    /// <c>.gitignore</c> was found, so <see cref="PopDir"/>'s truncation
    /// bottomed out early on .gitignore-less directories and sibling
    /// directories' ignore files were looked up from stale paths.
    /// </summary>
    private async ValueTask PushIgnoreFileForDirAsync(string absoluteDirPath, CancellationToken cancellationToken)
    {
        if (_workdir is null)
        {
            return;
        }

        // Compute the relative path of the .gitignore for context extraction.
        string dir = absoluteDirPath.EndsWith('/', StringComparison.Ordinal) ? absoluteDirPath : absoluteDirPath + "/";
        string relPath;
        if (dir.Length <= _dirRoot)
        {
            relPath = IgnoreFileName;
        }
        else
        {
            relPath = dir[_dirRoot..] + IgnoreFileName;
        }

        _depth++;

        IgnoreFile? file = await IgnoreFile.LoadFromWorkdirAsync(_workdir, _ignoreCase, cancellationToken, relPath).ConfigureAwait(false);
        if (file is not null)
        {
            _ignPath.Add(file);
        }
    }

    /// <summary>
    /// Pushes a subdirectory onto the ignore stack and loads its
    /// <c>.gitignore</c>. Matches <c>git_ignore__push_dir</c>
    /// (ignore.c:378-387) — the depth increment is performed by
    /// <see cref="PushIgnoreFileForDirAsync"/> (matching C's increment in
    /// <c>git_ignore__push_dir</c> before the file lookup).
    /// </summary>
    /// <param name="relativeDir">The subdirectory relative path (e.g. <c>subdir/</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask PushDirAsync(string relativeDir, CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            return;
        }

        _dir = $"{_dir.AsSpan().TrimEnd('/')}/{relativeDir.AsSpan().TrimEnd('/')}/";

        await PushIgnoreFileForDirAsync(_dir, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pops the topmost directory from the ignore stack. Matches
    /// <c>git_ignore__pop_dir</c> (ignore.c:389-422). If the topmost
    /// <c>.gitignore</c> file corresponds to the directory being popped,
    /// it is removed from the stack.
    /// </summary>
    public void PopDir()
    {
        if (!_initialized || _depth <= 0)
        {
            return;
        }

        // Check if the topmost ign_path entry corresponds to the current dir.
        if (_ignPath.Count > 0)
        {
            IgnoreFile top = _ignPath[^1];
            string relThisDir = _dir[_dirRoot..];

            // The file's relative path looks like "a/b/.gitignore";
            // the dir's relative path looks like "a/b/".
            if (top.RelativePath is { } fileRel)
            {
                string dirPart = fileRel;
                if (dirPart.EndsWith('/' + IgnoreFileName, StringComparison.Ordinal))
                {
                    dirPart = dirPart[..(dirPart.Length - IgnoreFileName.Length)];
                }
                else if (dirPart == IgnoreFileName)
                {
                    dirPart = string.Empty;
                }

                if (dirPart == relThisDir)
                {
                    _ignPath.RemoveAt(_ignPath.Count - 1);
                }
            }
        }

        _depth--;

        // Truncate _dir back to the parent.
        if (_depth > 0)
        {
            string parent = _dir[..^1];
            int lastSlash = parent.LastIndexOf('/', StringComparison.Ordinal);
            if (lastSlash >= _dirRoot - 1)
            {
                _dir = parent[..(lastSlash + 1)];
            }
            else
            {
                _dir = _workdir ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Looks up whether a path is ignored using the current rule stack.
    /// Matches <c>git_ignore__lookup</c> (ignore.c:466-501) — the internal
    /// API that does NOT walk up the directory tree. Used by the
    /// FilesystemIterator which already manages the directory stack via
    /// <see cref="PushDirAsync"/>/<see cref="PopDir"/>.
    /// </summary>
    /// <param name="path">The relative path (from workdir root).</param>
    /// <returns>The ignore result.</returns>
    public Result Lookup(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Lookup(GitPath.FromUtf8String(path));
    }

    /// <summary> Byte-faithful overload. The <paramref name="path"/> is kept as <see cref="GitPath"/> for the <c>AttrPath</c> decomposition and rule matching.
    /// </summary>
    public Result Lookup(GitPath path)
    {
        if (!_initialized || _repo is null)
        {
            return Result.Unchecked;
        }

        AttrPath.DirFlag dirFlag = AttrPath.DirFlag.Unknown;
        // If the path ends with '/', it's a directory.
        if (path.Span.Length > 0 && path.Span[^1] == (byte)'/')
        {
            dirFlag = AttrPath.DirFlag.True;
        }

        var attrPath = new AttrPath();
        attrPath.Init(path, GitPath.FromUtf8String(_workdir ?? string.Empty), dirFlag);

        try
        {
            int result = (int)Result.NotFound;

            // Internal rules first.
            if (_ignInternal is not null && LookupInRules(ref result, _ignInternal, attrPath))
            {
                return (Result)result;
            }

            // Per-directory rules in REVERSE order (parents first — parent
            // .gitignore overrides child .gitignore). Matches ignore.c:487-490.
            for (int i = _ignPath.Count - 1; i >= 0; i--)
            {
                if (LookupInRules(ref result, _ignPath[i], attrPath))
                {
                    return (Result)result;
                }
            }

            // Global rules forward (info/exclude first, then excludesfile).
            foreach (IgnoreFile file in _ignGlobal)
            {
                if (LookupInRules(ref result, file, attrPath))
                {
                    return (Result)result;
                }
            }

            return (Result)result;
        }
        finally
        {
            attrPath.Free();
        }
    }

    /// <summary>
    /// Scans a single ignore file's rules (in reverse order) for a match.
    /// Matches <c>ignore_lookup_in_rules</c> (ignore.c:446-464).
    /// </summary>
    /// <returns>True if a rule matched (and <paramref name="result"/> was set).</returns>
    private static bool LookupInRules(ref int result, IgnoreFile file, AttrPath path)
    {
        IReadOnlyList<IgnoreRule> rules = file.Rules;
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            IgnoreRule rule = rules[i];

            // Skip directory-only rules if the path is a file.
            if ((rule.Pattern.Flags & FnMatchPattern.Flag.Directory) != 0 &&
                !path.IsDir)
            {
                continue;
            }

            if (rule.Match(path))
            {
                result = rule.IsNegative ? (int)Result.False : (int)Result.True;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Releases all ignore state. Matches <c>git_ignore__free</c>
    /// (ignore.c:424-444).
    /// </summary>
    public void Free()
    {
        _ignInternal = null;
        _ignPath.Clear();
        _ignGlobal.Clear();
        _dir = string.Empty;
        _dirRoot = 0;
        _depth = 0;
        _initialized = false;
    }

    /// <summary>
    /// Adds in-memory ignore rules to the internal ignore file. Matches
    /// <c>git_ignore_add_rule</c> (ignore.c:503-515). Delegates to
    /// <see cref="IgnoreState.AddRuleAsync"/> so the rules are shared across
    /// all <see cref="IgnoreContext"/> instances for this repository.
    /// </summary>
    internal async ValueTask AddRuleAsync(string rules, CancellationToken cancellationToken = default)
    {
        if (_repo is not null)
        {
            await _repo.IgnoreState.AddRuleAsync(rules, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears internal rules and re-seeds the defaults. Matches
    /// <c>git_ignore_clear_internal_rules</c> (ignore.c:517-531).
    /// </summary>
    internal async ValueTask ClearInternalRulesAsync(CancellationToken cancellationToken = default)
    {
        if (_repo is not null)
        {
            await _repo.IgnoreState.ClearInternalRulesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets the repository context (called by <see cref="FilesystemIterator"/>
    /// when constructing with a repository). Reads <c>core.ignorecase</c>.
    /// </summary>
    internal async ValueTask SetRepositoryAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        _repo = repo;
        _workdir = repo.Workdir;
        // C (ignore.c:305-313, git_ignore__push_dir → ignores_init): the
        // core.ignorecase configmap lookup error PROPAGATES — an unparseable
        // value fails ignore/iterator initialization.
        _ignoreCase = await repo.Config.GetConfigmapBoolOrThrowAsync("core.ignorecase", false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates whether a path is ignored by walking up the directory
    /// tree. Matches <c>git_ignore_path_is_ignored</c> (ignore.c:533-601).
    /// This is the public check-ignore entry point that does a full
    /// parent-directory walk.
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="path">The relative path (from workdir root).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the path is ignored; false otherwise.</returns>
    internal static async ValueTask<bool> PathIsIgnoredAsync(GitRepository repo, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return await PathIsIgnoredAsync(repo, GitPath.FromUtf8String(path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-faithful overload. The <paramref name="path"/> is kept as <see cref="GitPath"/> throughout the walk-up; the <c>AttrPath</c> slices and
    /// the <c>FnMatchPattern</c> matching are all byte-domain. </summary>
    internal static async ValueTask<bool> PathIsIgnoredAsync(GitRepository repo, GitPath path, CancellationToken cancellationToken = default)
    {
        if (repo.Workdir is null)
        {
            return false;
        }

        // C (ignore.c:305-313): git_ignore_path_is_ignored → push_dir →
        // ignores_init — the configmap lookup error propagates.
        bool ignoreCase = await repo.Config.GetConfigmapBoolOrThrowAsync("core.ignorecase", false, cancellationToken).ConfigureAwait(false);

        AttrPath.DirFlag dirFlag = AttrPath.DirFlag.Unknown;
        if (path.Span.Length > 0 && path.Span[^1] == (byte)'/')
        {
            dirFlag = AttrPath.DirFlag.True;
        }
        else if (repo.IsBare)
        {
            dirFlag = AttrPath.DirFlag.False;
        }

        var workdirPath = GitPath.FromUtf8String(repo.Workdir);

        var attrPath = new AttrPath();
        attrPath.Init(path, workdirPath, dirFlag);

        var ctx = new IgnoreContext
        {
            _repo = repo,
            _workdir = repo.Workdir.EndsWith('/', StringComparison.Ordinal) ? repo.Workdir : repo.Workdir + "/",
            _ignoreCase = ignoreCase,
        };
        ctx._dirRoot = ctx._workdir.Length;
        // Initialize for the directory containing the path.
        GitPath dirPart = attrPath.Path;
        int lastSlash = dirPart.Span.LastIndexOf((byte)'/');
        string startRelDir = lastSlash >= 0
            ? dirPart.Slice(0, lastSlash + 1).ToUtf8String()
            : string.Empty;
        // C (ignore.c:322-326): the directory portion is resolved via git_fs_path_resolve_relative BEFORE joining the workdir — "." and ".." segments collapse,
        // so "a/../b/file.txt" walks from "b/" and never consults an intermediate "a/.gitignore".
        startRelDir = ResolveRelativeDir(startRelDir);
        ctx._dir = ctx._workdir + startRelDir;
        ctx._initialized = true;
        await ctx.LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        await ctx.LoadPerDirectoryIgnoresAsync(cancellationToken).ConfigureAwait(false);
        await ctx.LoadGlobalIgnoresAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Walk up the directory tree, checking at each level. The relPath
            // and basename are kept as GitPath slices into attrPath.Full so the
            // walk-up is byte-faithful (no lossy string decode).
            GitPath relPath = attrPath.Path;
            GitPath basename = attrPath.Basename;
            int basenameOffset = relPath.Length - basename.Length;

            while (true)
            {
                int result = (int)Result.NotFound;

                if (ctx._ignInternal is not null && LookupInRules(ref result, ctx._ignInternal, attrPath))
                {
                    return result == (int)Result.True;
                }

                // Per-directory rules FORWARD (children first), popping as
                // we walk up — matches ignore.c:570-573.
                foreach (IgnoreFile file in ctx._ignPath)
                {
                    if (LookupInRules(ref result, file, attrPath))
                    {
                        return result == (int)Result.True;
                    }
                }

                foreach (IgnoreFile file in ctx._ignGlobal)
                {
                    if (LookupInRules(ref result, file, attrPath))
                    {
                        return result == (int)Result.True;
                    }
                }

                // Move up one directory.
                if (basenameOffset <= 0)
                {
                    break; // reached the root of the relative path.
                }

                // Truncate the path at the parent separator and advance
                // basename. C does in-place pointer arithmetic; C# rebuilds
                // the AttrPath with the parent path. The parent path is a
                // byte-faithful GitPath slice.
                GitPath parentPath = relPath.Slice(0, basenameOffset - 1);
                relPath = parentPath;
                lastSlash = relPath.Span.LastIndexOf((byte)'/');
                basename = lastSlash >= 0 ? relPath.Slice(lastSlash + 1) : relPath;
                basenameOffset = relPath.Length - basename.Length;

                attrPath.Free();
                attrPath.Init(relPath, workdirPath, AttrPath.DirFlag.True);

                ctx.PopDir();
            }

            return false;
        }
        finally
        {
            attrPath.Free();
            ctx.Free();
        }
    }
}
