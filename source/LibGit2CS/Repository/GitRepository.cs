// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Status;
using LibGit2CS.Submodule;

namespace LibGit2CS.Repository;

/// <summary>
/// A git repository. Managed port of libgit2's <c>src/libgit2/repository.c</c>
/// (read/open side).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope</b>: open, discover, paths, ODB/Config wiring (read side) plus
/// init, set_head, set_index, and other write operations.
/// </para>
/// <para>
/// <see cref="OpenExtAsync"/> is the workhorse; <see cref="OpenAsync"/> and
/// <see cref="OpenBareAsync"/> are thin wrappers. <see cref="DiscoverAsync"/> is a
/// separate static (stat-only, stays sync).
/// </para>
/// <para>
/// <b>Async:</b> open/init/dispose and all IO-doing methods are
/// async-only. <see cref="IAsyncDisposable"/> — use <c>await using</c>.
/// </para>
/// </remarks>
public sealed partial class GitRepository : IAsyncDisposable
{
    private bool _disposed;

    /// <summary>Creates a repository with the given paths and subsystems.</summary>
    internal GitRepository(
        string gitdir,
        string commondir,
        string? workdir,
        bool isBare,
        bool isWorktree,
        GitHashAlgorithmKind objectFormat,
        string? @namespace,
        GitObjectDb objects,
        GitConfiguration config,
        GitContext context,
        bool useEnv = false,
        GitReferences? refs = null)
    {
        Path = gitdir;
        CommonDir = commondir;
        Workdir = workdir;
        IsBare = isBare;
        IsWorktree = isWorktree;
        ObjectFormat = objectFormat;
        Namespace = @namespace;
        Objects = objects;
        Config = config;
        Context = context;
        UseEnv = useEnv;
        Refs = refs ?? new GitReferences(new RefDatabase());
        Objects.SetOwner(this);
        Refs.SetOwner(this);
    }

    /// <summary>The git directory path (e.g. <c>/path/to/.git</c>).</summary>
    public string Path { get; }

    /// <summary>
    /// Grafts loaded from <c>&lt;commondir&gt;/info/grafts</c> at open.
    /// Matches <c>repo-&gt;grafts</c> (loaded by <c>load_grafts</c>,
    /// repository.c:877-918).
    /// </summary>
    internal Grafts? Grafts { get; private set; }

    /// <summary>
    /// Grafts loaded from <c>&lt;gitdir&gt;/shallow</c> at open. Matches
    /// <c>repo-&gt;shallow_grafts</c>.
    /// </summary>
    internal Grafts? ShallowGrafts { get; private set; }

    /// <summary>The shared common directory (for worktrees, same as <see cref="Path"/> for normal repos).</summary>
    public string CommonDir { get; }

    /// <summary>The working directory root, or <c>null</c> if bare.</summary>
    public string? Workdir { get; private set; }

    /// <summary>True if this is a bare repository (no working directory).</summary>
    public bool IsBare { get; private set; }

    /// <summary>True if this is a linked worktree (commondir != gitdir).</summary>
    public bool IsWorktree { get; }

    /// <summary>The object format (hash algorithm) used by this repository.</summary>
    public GitHashAlgorithmKind ObjectFormat { get; internal set; }

    /// <summary> Sets the repository's object format. Matches <c>git_repository__set_objectformat</c> (repository.c:2814-2825): updates
    /// <c>repo-&gt;oid_type</c>, the ODB's oid type (the ODB reads <see cref="ObjectFormat"/> via its owner), and the ref backend's OID type (the C refdb is
    /// created lazily and picks up the new type). Used by the clone paths to adopt the source/remote's format (clone.c:444-450, 522-526). </summary>
    internal void SetObjectFormat(GitHashAlgorithmKind objectFormat)
    {
        ObjectFormat = objectFormat;
        Refs.SetOidType(objectFormat);
    }

    /// <summary>The GIT_NAMESPACE env var value, or null if not set. Settable via <see cref="SetNamespace"/>.</summary>
    public string? Namespace { get; private set; }

    /// <summary> Sets the repository namespace. Matches <c>git_repository_set_namespace</c>. </summary>
    public void SetNamespace(string? @namespace) => Namespace = @namespace;

    /// <summary> The configured reflog ident (name, email), or null when unset. Matches <c>git_repository_ident</c>. </summary>
    public (string? Name, string? Email) Ident => (_identName, _identEmail);

    /// <summary> True when HEAD points directly at an object (detached HEAD). Matches <c>git_repository_head_detached</c> (repository.c:2922-2944). </summary>
    public async Task<bool> IsHeadDetachedAsync(CancellationToken cancellationToken = default)
    {
        // C (repository.c:2922-2944): a RAW HEAD lookup — a symbolic HEAD is
        // never detached, regardless of whether its target exists.
        GitReference? head = await Refs.LookupAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false);
        if (head is not GitDirectReference direct)
        {
            return false;
        }

        // C returns
        // git_odb_exists(odb, git_reference_target(ref)) for a direct HEAD
        // (repository.c:2940-2943) — a dangling detached HEAD (target object
        // pruned/missing) is NOT detached.
        // any direct HEAD with no ODB lookup.
        return await Objects.ExistsAsync(direct.Target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> True when HEAD points at a branch that does not exist yet. Matches <c>git_repository_head_unborn</c> (repository.c:3068-3081). </summary>
    public async Task<bool> IsHeadUnbornAsync(CancellationToken cancellationToken = default)
    {
        // C's
        // git_repository_head_unborn (repository.c:3068-3081) calls
        // git_repository_head — a MISSING HEAD file fails the lookup with
        // GIT_ENOTFOUND (→ error, refdb_fs.c:459 "reference 'HEAD' not
        // found"); only a symbolic HEAD whose target branch does not exist
        // maps to GIT_EUNBORNBRANCH → 1 (unborn).
        // true for both cases.
        GitReference? head = await Refs.LookupAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                "reference 'HEAD' not found",
                GitErrorCategory.Reference);
        }

        if (head is not GitSymbolicReference symbolic)
        {
            return false;
        }

        GitReference? resolved = await Refs.ResolveAsync(symbolic.TargetNameKey, cancellationToken).ConfigureAwait(false);
        return resolved is null;
    }

    /// <summary> True when this repository is shallow (a <c>shallow</c> file exists in the gitdir). Matches <c>git_repository_is_shallow</c>
    /// (repository.c:3846-3865). </summary>
    public bool IsShallow => File.Exists(System.IO.Path.Join(Path, "shallow"));

    /// <summary> True when the repository is empty: HEAD is symbolic, points at the initial branch, and no references exist. Matches
    /// <c>git_repository_is_empty</c> (repository.c:3141-3161). </summary>
    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default)
    {
        GitReference? head = await Refs.LookupAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false);
        if (head is not GitSymbolicReference symbolic)
        {
            return false;
        }

        string initialBranch = await InitialBranchAsync(cancellationToken).ConfigureAwait(false);
        if (symbolic.TargetNameKey != (RefNameKey)initialBranch)
        {
            return false;
        }

        await foreach (GitReference _ in Refs.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return false; // at least one reference exists
        }

        return true;
    }

    /// <summary>
    /// Whether the repository was opened with
    /// <see cref="RepositoryOpenFlags.FromEnv"/> — mirrors <c>repo-&gt;use_env</c>.
    /// The index path and ODB alternates consult the environment lazily when
    /// this is set (repository.c:1638-1649, 1499-1522).
    /// </summary>
    internal bool UseEnv { get; }

    /// <summary>The object database for reading objects.</summary>
    public GitObjectDb Objects { get; }

    /// <summary>The configuration for this repository.</summary>
    public GitConfiguration Config { get; }

    /// <summary>The references database for this repository.</summary>
    public GitReferences Refs { get; }

    /// <summary>The library context owning settings, registries, and trace.</summary>
    internal GitContext Context { get; }

    private GitIndex? _index;
    private bool _indexChecked;

    private IgnoreState? _ignoreState;

    private AttributeCache? _attrCache;

    private SubmoduleCache? _submoduleCache;

    /// <summary>
    /// The repository's ignore state (internal rules + shared across
    /// <see cref="IgnoreContext"/> instances). Lazily initialized on first
    /// access. Matches the <c>repo->attrcache</c> internal-ignores slot.
    /// </summary>
    internal IgnoreState IgnoreState => _ignoreState ??= new IgnoreState(this);

    /// <summary>
    /// The repository's attribute cache (macros + cached
    /// <c>.gitattributes</c> files from all sources). Lazily initialized on
    /// first access. Matches <c>git_repository_attr_cache</c> /
    /// <c>git_attr_cache__init</c> (attrcache.c:401-452).
    /// </summary>
    /// <remarks>
    /// The lazy load reads <c>core.ignorecase</c> from config
    /// and loads attr files from disk (async), so this is a method, not a
    /// property getter.
    /// </remarks>
    internal ValueTask<AttributeCache> GetAttributeCacheAsync(CancellationToken cancellationToken = default)
    {
        if (_attrCache is { } cache)
        {
            return ValueTask.FromResult(cache);
        }

        return new ValueTask<AttributeCache>(GetAttributeCacheSlowAsync(cancellationToken));
    }

    /// <summary>Cold path of <see cref="GetAttributeCacheAsync"/>: builds the cache (config + attr-file IO).</summary>
    private async Task<AttributeCache> GetAttributeCacheSlowAsync(CancellationToken cancellationToken)
    {
        _attrCache ??= await AttributeCache.CreateAsync(this, cancellationToken).ConfigureAwait(false);

        return _attrCache;
    }

    /// <summary>
    /// The repository's submodule cache, lazily initialized on first access.
    /// </summary>
    internal SubmoduleCache GetOrInitSubmoduleCache()
    {
        return _submoduleCache ??= new SubmoduleCache(this);
    }

    /// <summary>
    /// Reloads the submodule cache if it has already been created (no-op when
    /// no lookup has populated it yet). Matches C's no-persistent-cache
    /// behavior: the cache may have been loaded mid-checkout (before gitlinks
    /// were written to the index), so a caller that mutated the index must
    /// refresh the cache before the next lookup returns stale IndexId state.
    /// </summary>
    internal async Task ReloadSubmoduleCacheIfLoadedAsync(CancellationToken cancellationToken)
    {
        SubmoduleCache? cache = _submoduleCache;
        if (cache is not null && cache.IsLoaded)
        {
            await cache.ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The repository's index (staging area), lazily loaded from
    /// <c>{Path}/index</c>. Creates an empty in-memory index when no index
    /// file exists (e.g. fresh repo). Matches <c>git_repository_index</c>.
    /// </summary>
    /// <remarks>
    /// The lazy load reads <c>core.ignorecase</c> from config
    /// (async), so this is a method, not a property getter.
    /// </remarks>
    public ValueTask<GitIndex> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        if (_indexChecked)
        {
            // Cache hit — synchronous majority path.
            return ValueTask.FromResult(_index ?? throw new InvalidOperationException("index failed to initialize"));
        }

        return new ValueTask<GitIndex>(GetIndexSlowAsync(cancellationToken));
    }

    /// <summary>Cold path of <see cref="GetIndexAsync"/>: loads the index from disk (real IO).</summary>
    private async Task<GitIndex> GetIndexSlowAsync(CancellationToken cancellationToken)
    {
        if (!_indexChecked)
        {
            // C (repository.c:1638-1649, repository_index_path): with
            // use_env the index path comes from GIT_INDEX_FILE — the RAW
            // value (no prettify; a relative value is CWD-relative; even an
            // empty-but-set variable is honored) — and falls back to the
            // gitdir's index.
            string indexPath = UseEnv && Context.Env["GIT_INDEX_FILE"] is { } envIndex
                ? envIndex
                : System.IO.Path.Join(Path, "index");

            if (File.Exists(indexPath))
            {
                // C (index.c:430): a corrupt index FAILS git_index_open — the
                // parse error propagates. The load is NOT cached as failed
                // (C's repo->index stays NULL), so a later call retries the
                // open instead of throwing InvalidOperationException.
                _index = await GitIndex.OpenAsync(indexPath, ObjectFormat, cancellationToken).ConfigureAwait(false);
                _indexChecked = true;
            }
            else
            {
                // If no index file, create an empty in-memory one. Matches
                // libgit2's git_index__open which always succeeds even when
                // the file is absent (on_disk=0).
                _index ??= GitIndex.New(ObjectFormat);
                _indexChecked = true;
            }

            _index!.SetOwner(this);

            // Wire core.ignorecase from config. C's default is
            // GIT_IGNORECASE_DEFAULT = GIT_CONFIGMAP_FALSE on every platform
            // (repository.h:96), including macOS/Windows.
            if (await Config.GetBoolAsync("core.ignorecase", defaultValue: false, cancellationToken).ConfigureAwait(false))
            {
                _index.IgnoreCase = true;
            }
        }

        return _index ?? throw new InvalidOperationException("index failed to initialize");
    }

    /// <summary>
    /// Re-reads the index file from disk so external edits are visible.
    /// Matches <c>git_index_read_safely</c> (index.c:717-726): with the
    /// unsaved-safety flag OFF (the default — index.c:123), a dirty in-memory
    /// index does NOT block the read; <c>git_index_read(index, false)</c>
    /// reloads only when the file's checksum changed and keeps the in-memory
    /// entries when the file is absent. Used by status (status.c:296-299) and
    /// checkout (checkout.c:2424-2442).
    /// </summary>
    internal async Task RefreshIndexAsync(CancellationToken cancellationToken)
    {
        if (!_indexChecked)
        {
            _ = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_index is not { } idx)
        {
            return;
        }

        await idx.ReadFromDiskAsync(force: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>.git/objects/info/commit-graph</c> for this repository, lazily
    /// opened on first access. Null when the file is absent (revwalk falls back
    /// to ODB reads). Matches <c>git_odb__get_commit_graph_file</c> +
    /// <c>git_odb_set_commit_graph</c>.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="GitObjectDb.GetCommitGraphAsync"/> — the commit-graph is
    /// owned by the ODB (single source of truth), matching C's
    /// <c>odb-&gt;cgraph</c> field. The lazy-open + the explicit setter both
    /// live on <see cref="GitObjectDb"/>.
    /// </remarks>
    internal ValueTask<CommitGraph?> GetCommitGraphAsync(CancellationToken cancellationToken)
        => Objects.GetCommitGraphAsync(cancellationToken);

    /// <summary>
    /// Opens a repository at <paramref name="path"/>. Matches
    /// <c>git_repository_open</c> — which is
    /// <c>git_repository_open_ext(path, GIT_REPOSITORY_OPEN_NO_SEARCH, NULL)</c>:
    /// only <c>path/.git</c> and <c>path</c> itself are checked; there is no
    /// upward search and no env-var consultation.
    /// </summary>
    public static Task<GitRepository> OpenAsync(string path, GitContext context, CancellationToken cancellationToken = default)
        => OpenExtAsync(path, RepositoryOpenFlags.NoSearch, ceilingDirs: null, context, cancellationToken);

    /// <summary>
    /// Opens a repository with extended flags. Matches
    /// <c>git_repository_open_ext</c> — the workhorse.
    /// </summary>
    /// <param name="path">Starting path for the search.</param>
    /// <param name="flags">Open flags controlling search behavior.</param>
    /// <param name="ceilingDirs">Colon-separated ceiling directories that stop the upward search.</param>
    /// <param name="context">The library context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<GitRepository> OpenExtAsync(string path, RepositoryOpenFlags flags, string? ceilingDirs, GitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RepositoryOpener.OpenAsync(path, flags, ceilingDirs, context, cancellationToken);
    }

    /// <summary> Opens a bare repository. Matches <c>git_repository_open_bare</c> (repository.c:1021-1064): a self-contained fast path with no env, no
    /// discovery, no ownership validation, and no grafts loading, failing with GIT_ENOTFOUND "path is not a repository: %s". </summary>
    public static Task<GitRepository> OpenBareAsync(string path, GitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RepositoryOpener.OpenBareAsync(path, context, cancellationToken);
    }

    /// <summary>
    /// Discovers the git directory starting from <paramref name="startPath"/> and
    /// walking upward. Matches <c>git_repository_discover</c>. Stays sync — pure
    /// stat (File.Exists/Directory.Exists), no config IO.
    /// </summary>
    /// <param name="startPath">Starting path for the upward search.</param>
    /// <param name="acrossFs">Continue across filesystem boundaries.</param>
    /// <param name="ceilingDirs">Colon-separated ceiling directories that stop the upward search.</param>
    /// <param name="context">The library context supplying the env-var snapshot consulted for <c>GIT_DIR</c>/<c>GIT_COMMON_DIR</c>. When <c>null</c>, a transient default context is constructed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered git directory path.</returns>
    public static async Task<string> DiscoverAsync(string startPath, bool acrossFs = false, string? ceilingDirs = null, GitContext? context = null, CancellationToken cancellationToken = default)
    {
        GitContext ctx = context ?? new GitContext();
        return await RepositoryOpener.DiscoverAsync(startPath, acrossFs, ceilingDirs, ctx, cancellationToken).ConfigureAwait(false);
    }

    // ==============================
    // Init / write side
    // ==============================

    /// <summary>
    /// Creates a new repository at <paramref name="path"/>. Matches
    /// <c>git_repository_init</c> (repository.c:2843-2853). Thin wrapper around
    /// <see cref="InitExtAsync"/> with <see cref="GitRepositoryInitFlags.Mkpath"/> and
    /// optional <see cref="GitRepositoryInitFlags.Bare"/>.
    /// </summary>
    /// <param name="path">The path to create the repository at.</param>
    /// <param name="isBare">If true, create a bare repository.</param>
    /// <param name="context">The library context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The newly opened repository.</returns>
    public static Task<GitRepository> InitAsync(string path, bool isBare, GitContext context, CancellationToken cancellationToken = default)
    {
        GitRepositoryInitFlags flags = GitRepositoryInitFlags.Mkpath;
        if (isBare)
        {
            flags |= GitRepositoryInitFlags.Bare;
        }

        return InitExtAsync(path, new GitRepositoryInitOptions { Flags = flags }, context, cancellationToken);
    }

    /// <summary>
    /// Creates a new repository with extended options. Matches
    /// <c>git_repository_init_ext</c> (repository.c:2855-2920). Creates the
    /// git directory structure, writes HEAD, writes config, and opens the repo.
    /// </summary>
    /// <param name="path">The path to create the repository at.</param>
    /// <param name="options">Init options (flags, mode, initial head, etc.).</param>
    /// <param name="context">The library context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The newly opened repository.</returns>
    public static async Task<GitRepository> InitExtAsync(string path, GitRepositoryInitOptions options, GitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        bool isBare = options.IsBare;

        // repo_init_directories (repository.c:2660-2780).
        bool addDotgit = (options.Flags & GitRepositoryInitFlags.NoDotgitDir) == 0
            && !isBare
            && !path.EndsWith("/.git", StringComparison.Ordinal)
            && !path.EndsWith("/.git/", StringComparison.Ordinal)
            && !path.EndsWith(@"\.git", StringComparison.Ordinal)
            && !path.EndsWith(@"\.git\", StringComparison.Ordinal);

        // C joins with GIT_DIR = ".git/" (or "" — git_str_joinpath always
        // yields a trailing slash), so the gitdir keeps a trailing slash.
        // The paths are built LEXICALLY here — C's repo_init_directories
        // creates the directories first and only THEN prettifies (realpath)
        // them (repository.c:2782-2787);
        // directories existed. The realpath prettify happens below, after
        // creation.
        string gitDir = addDotgit
            ? PathHelpers.PrettifyLexicalDir(PathHelpers.Join(PathHelpers.PrettifyLexicalDir(path), ".git"))
            : PathHelpers.PrettifyLexicalDir(path);
        bool hasDotgit = gitDir.EndsWith("/.git/", StringComparison.Ordinal) || gitDir.EndsWith(@"\.git\", StringComparison.Ordinal);

        string? workDir;
        if (!isBare)
        {
            if (options.WorkdirPath is not null)
            {
                workDir = PathHelpers.PrettifyLexicalDir(options.WorkdirPath);
            }
            else if (hasDotgit)
            {
                workDir = PathHelpers.PrettifyLexicalDir(PathHelpers.Dirname(gitDir));
            }
            else
            {
                // C (repository.c:2719-2722): a non-bare, non-'.git' init
                // without an explicit workdir cannot pick one.
                throw new GitException(
                    GitErrorCode.Error,
                    "cannot pick working directory for non-bare repository that isn't a '.git' directory",
                    GitErrorCategory.Repository);
            }
        }
        else
        {
            workDir = null;
        }

        bool naturalWd = hasDotgit && workDir is not null
            && string.Equals(workDir, PathHelpers.PrettifyLexicalDir(PathHelpers.Dirname(gitDir)), StringComparison.Ordinal);

        // C (pick_dir_mode, repository.c:2521-2530): 0777 for UMASK,
        // 0775|S_ISGID for SHARED_GROUP, 0777|S_ISGID for SHARED_ALL.
        int dirmode = options.Mode switch
        {
            GitInitMode.SharedGroup => 0x5FD, // 0o2775
            GitInitMode.SharedAll => 0x5FF,   // 0o2777
            _ => 0x1FF,                       // 0o777
        };

        // Create directories. C (repo_init_directories, repository.c:2742-2780): the gitdir (path #1) is created under MKDIR/MKPATH or whenever the layout has
        // a .git component, but GIT_MKDIR_VERIFY_DIR semantics apply — only MKPATH creates missing PARENTS; MKDIR and the has_dotgit path create single
        // components non-recursively and FAIL when a parent is missing — even
        // with Flags = None.
        bool mkpath = (options.Flags & GitRepositoryInitFlags.Mkpath) != 0;
        if ((options.Flags & (GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.Mkdir)) != 0 || hasDotgit)
        {
            if ((options.Flags & GitRepositoryInitFlags.Mkdir) != 0 && !mkpath)
            {
                // path #2: the gitdir's parent (GIT_MKDIR_SKIP_LAST),
                // non-recursive.
                CreateInitDirNonRecursive(PathHelpers.Dirname(gitDir));
            }

            if (mkpath)
            {
                Directory.CreateDirectory(gitDir);
            }
            else
            {
                CreateInitDirNonRecursive(gitDir);
            }

            // With a shared mode the gitdir is chmod'd to the EXACT dirmode
            // (GIT_MKDIR_CHMOD — mkdir+umask would strip the S_ISGID bit and
            // other bits).
            ApplyInitDirectoryMode(gitDir, dirmode);
        }

        // C (repo_init_directories #4, repository.c:2740-2748): the workdir
        // is created under MKDIR/MKPATH as well (with the S_ISGID bit
        // stripped — mkdir-mode, no chmod). Non-recursive unless MKPATH.
        if (workDir is not null
            && (options.Flags & (GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.Mkdir)) != 0
            && !Directory.Exists(workDir))
        {
            if (mkpath)
            {
                Directory.CreateDirectory(workDir);
            }
            else
            {
                CreateInitDirNonRecursive(workDir);
            }
        }

        // C (repo_init_directories, repository.c:2782-2787): prettify BOTH
        // directories AFTER creation — realpath resolves symlinks and
        // requires existence. The created gitdir always exists here;
        // the workdir exists when it was created above (or pre-existed).
        gitDir = PathHelpers.PrettifyDir(gitDir);
        if (workDir is not null && Directory.Exists(workDir))
        {
            workDir = PathHelpers.PrettifyDir(workDir);
        }

        if (!Directory.Exists(gitDir))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"could not create repository directory '{gitDir}'",
                GitErrorCategory.Repository);
        }

        // C (git_repository_init_ext, repository.c:2883-2905): a valid
        // existing repo takes the reinit path (config only — the structure,
        // templates and HEAD are preserved); NO_REINIT makes that an error.
        (bool isValid, _) = await RepositoryOpener.IsValidRepositoryPathAsync(gitDir, RepositoryOpenFlags.None, context, cancellationToken).ConfigureAwait(false);
        if (isValid)
        {
            if ((options.Flags & GitRepositoryInitFlags.NoReinit) != 0)
            {
                throw new GitException(
                    GitErrorCode.Exists,
                    $"attempt to reinitialize '{path}'",
                    GitErrorCategory.Repository);
            }

            await WriteInitConfigAsync(gitDir, workDir, options, context, isReinit: true, naturalWd, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Fresh init: structure (dirs + template + gitlink) → config → HEAD.
            await CreateRepoStructureAsync(gitDir, workDir, naturalWd, options, context, cancellationToken).ConfigureAwait(false);
            await WriteInitConfigAsync(gitDir, workDir, options, context, isReinit: false, naturalWd, cancellationToken).ConfigureAwait(false);
            await WriteHeadFileAsync(gitDir, options, context, cancellationToken).ConfigureAwait(false);
        }

        // Open the newly created repo.
        return await OpenAsync(gitDir, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Returns the initial branch name bytes from <c>init.defaultBranch</c> config (via <see cref="GitConfiguration.OpenDefaultAsync"/>). Matches
    /// <c>repo_init_head</c> (repository.c:2793-2829). Returns null if unset (caller falls back to "master"). byte-primary — C feeds the raw config <c>char
    /// *</c> bytes to <c>git_repository_create_head</c> verbatim. </summary>
    private static async Task<byte[]?> GetInitialBranchBytesAsync(GitContext context, CancellationToken cancellationToken)
    {
        // C (repo_init_head, repository.c:2811-2817): only init.defaultBranch
        // in the global config; GIT_INIT_BRANCH is not a libgit2 env var.
        GitConfiguration defaultConfig = await GitConfiguration.OpenDefaultAsync(context, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable defaultConfigDisposable = defaultConfig.ConfigureAwait(false);
        byte[]? configBranch = await defaultConfig.GetBytesAsync("init.defaultBranch", cancellationToken).ConfigureAwait(false);
        if (configBranch is { Length: > 0 })
        {
            return configBranch;
        }

        return null;
    }

    /// <summary> Returns the initial branch name for this repository as <c>refs/heads/&lt;branch&gt;</c>. Matches <c>git_repository_initialbranch</c>
    /// (repository.c:3105-3139). Reads <c>init.defaultBranch</c> from the repository's config; falls back to <c>"master"</c> if unset. The value and the name
    /// validation are byte-domain (C's <c>git_reference_name_is_valid</c> runs over the raw bytes); the return string is the UTF-8 display decode. </summary>
    /// <param name="cancellationToken">Cancellation token.</param> <returns>The full ref name (<c>refs/heads/&lt;branch&gt;</c>).</returns> <exception
    /// cref="GitException"> <see cref="GitErrorCode.Invalid"/> if <c>init.defaultBranch</c> is set but is not a valid branch name. </exception>
    public async ValueTask<string> InitialBranchAsync(CancellationToken cancellationToken = default)
    {
        byte[]? branch = await Config.GetBytesAsync("init.defaultBranch", cancellationToken).ConfigureAwait(false);
        if (branch is not { Length: > 0 })
        {
            branch = "master"u8.ToArray();
        }

        byte[] full = [.. "refs/heads/"u8.ToArray(), .. branch];
        if (!GitReferences.IsNameValid(full, GitReferenceFormatFlags.AllowOneLevel))
        {
            throw new GitException(GitErrorCode.Invalid,
                "the value of init.defaultBranch is not a valid branch name",
                GitErrorCategory.Repository);
        }

        return Encoding.UTF8.GetString(full);
    }

    /// <summary>
    /// Creates the repository structure. Matches <c>repo_init_structure</c>
    /// (repository.c:2534-2646): the gitlink file first, then the external
    /// template (when requested) or the internal template files, plus the
    /// standard directories.
    /// </summary>
    private static async Task CreateRepoStructureAsync(string gitDir, string? workDir, bool naturalWd, GitRepositoryInitOptions options, GitContext context, CancellationToken cancellationToken)
    {
        // Create the .git gitlink file first (C writes it before the template).
        if (!options.IsBare && !naturalWd && workDir is not null)
        {
            await WriteGitlinkAsync(workDir, gitDir, (options.Flags & GitRepositoryInitFlags.RelativeGitlink) != 0, cancellationToken).ConfigureAwait(false);
        }

        // Directories to create.
        string[] dirs =
        [
            PathHelpers.Join(gitDir, "objects"),
            PathHelpers.Join(PathHelpers.Join(gitDir, "objects"), "info"),
            PathHelpers.Join(PathHelpers.Join(gitDir, "objects"), "pack"),
            PathHelpers.Join(gitDir, "refs"),
            PathHelpers.Join(PathHelpers.Join(gitDir, "refs"), "heads"),
            PathHelpers.Join(PathHelpers.Join(gitDir, "refs"), "tags"),
            PathHelpers.Join(gitDir, "hooks"),
            PathHelpers.Join(gitDir, "info"),
        ];

        // C (repo_init_structure, repository.c:2610-2615): with a shared mode
        // the template directories are created with GIT_MKDIR_CHMOD → exact
        // dirmode; under UMASK the mkdir+umask mode stands (BCL default).
        int dirmode = options.Mode switch
        {
            GitInitMode.SharedGroup => 0x5FD, // 0o2775
            GitInitMode.SharedAll => 0x5FF,   // 0o2777
            _ => 0x1FF,                       // 0o777
        };

        foreach (string? dir in dirs)
        {
            Directory.CreateDirectory(dir);
            ApplyInitDirectoryMode(dir, dirmode);
        }

        // External template copy: opts->template_path, or
        // init.templatedir from the default config, or the system template
        // dir; the internal template files are then skipped.
        bool externalTpl = options.TemplatePath is not null || (options.Flags & GitRepositoryInitFlags.ExternalTemplate) != 0;
        if (externalTpl)
        {
            string? tdir = options.TemplatePath;
            if (tdir is null)
            {
                GitConfiguration defaultConfig = await GitConfiguration.OpenDefaultAsync(context, cancellationToken).ConfigureAwait(false);
                await using ConfiguredAsyncDisposable d = defaultConfig.ConfigureAwait(false);

                // C (repository.c:2576): git_config__get_path — the config path parser expands '~' (git_config__parse_path). GetPathAsync + a single decode at
                // the FS boundary.
                tdir = (await defaultConfig.GetPathAsync("init.templatedir", cancellationToken).ConfigureAwait(false))?.ToFileSystemString();
                if (string.IsNullOrEmpty(tdir))
                {
                    tdir = context.Dirs.FindTemplateDir();
                }
            }

            if (string.IsNullOrEmpty(tdir) || !Directory.Exists(tdir))
            {
                // C: a missing template dir is a warning; fall back to the
                // internal template.
                externalTpl = false;
            }
            else
            {
                CopyTemplateDirectory(tdir, gitDir, cancellationToken);
            }
        }

        // Internal template files — only when no external template was used.
        if (!externalTpl)
        {
            string description = options.Description
                ?? "Unnamed repository; edit this file 'description' to name the repository.\n";
            await AsyncFileIO.WriteAllTextWithNoBomAsync(PathHelpers.Join(gitDir, "description"), description, cancellationToken).ConfigureAwait(false);

            string hooksReadme =
                "#!/bin/sh\n" +
                "#\n" +
                "# Place appropriately named executable hook scripts into this directory\n" +
                "# to intercept various actions that git takes.  See `git help hooks` for\n" +
                "# more information.\n";
            await AsyncFileIO.WriteAllTextWithNoBomAsync(PathHelpers.Join(PathHelpers.Join(gitDir, "hooks"), "README.sample"), hooksReadme, cancellationToken).ConfigureAwait(false);

            string excludeContent =
                "# File patterns to ignore; see `git help ignore` for more information.\n" +
                "# Lines that start with '#' are comments.\n";
            await AsyncFileIO.WriteAllTextWithNoBomAsync(PathHelpers.Join(PathHelpers.Join(gitDir, "info"), "exclude"), excludeContent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the <c>.git</c> gitlink file into <paramref name="inDir"/>.
    /// Matches <c>repo_write_gitlink</c> (repository.c:2471-2519): the target
    /// is absolute unless <paramref name="useRelativePath"/> is set, and an
    /// existing non-regular target is GIT_EEXISTS.
    /// </summary>
    private static async Task WriteGitlinkAsync(string inDir, string toRepo, bool useRelativePath, CancellationToken cancellationToken)
    {
        // Don't write the gitlink to the natural workdir (C returns
        // GIT_PASSTHROUGH for that — the caller skips).
        string gitlinkPath = PathHelpers.Join(inDir, ".git");

        if (Directory.Exists(gitlinkPath))
        {
            throw new GitException(
                GitErrorCode.Exists,
                $"cannot overwrite gitlink file into path '{inDir}'",
                GitErrorCategory.Repository);
        }

        // A stale
        // REGULAR .git file is OVERWRITTEN, not kept — C's
        // repo_write_gitlink errors only when the path exists and is NOT a
        // regular file (repository.c:2488-2495), then rewrites the pointer
        // via repo_write_template(..., allow_overwrite=true) →
        // O_WRONLY|O_CREAT|O_TRUNC (repository.c:2424-2443). The rewrite is
        // unconditional, so the worktree never keeps pointing at a previous
        // repo.

        if (!Directory.Exists(inDir))
        {
            Directory.CreateDirectory(inDir);
        }

        string pathToRepo = PathHelpers.PrettifyDir(toRepo);
        if (useRelativePath)
        {
            pathToRepo = PathHelpers.MakeRelative(pathToRepo, inDir);
        }

        await AsyncFileIO.WriteAllTextWithNoBomAsync(gitlinkPath, $"gitdir: {pathToRepo}\n", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recursively copies a template directory (files, directories and
    /// dotfiles). Matches <c>git_futils_cp_r</c> with
    /// GIT_CPDIR_COPY_SYMLINKS|GIT_CPDIR_SIMPLE_TO_MODE|GIT_CPDIR_COPY_DOTFILES.
    /// </summary>
    private static void CopyTemplateDirectory(string sourceDir, string destDir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string entry in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = System.IO.Path.GetFileName(entry);
            string dest = PathHelpers.Join(destDir, name);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(dest);
                CopyTemplateDirectory(entry, dest, cancellationToken);
            }
            else
            {
                Directory.CreateDirectory(PathHelpers.Dirname(dest));
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(entry, dest, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Writes the HEAD file as a symbolic ref. Matches <c>repo_init_head</c>
    /// (repository.c:2793-2829) + <c>git_repository_create_head</c>: a
    /// template-provided HEAD is preserved unless an initial head was given.
    /// Format: <c>ref: refs/heads/&lt;branch&gt;\n</c>.
    /// </summary>
    private static async Task WriteHeadFileAsync(string gitDir, GitRepositoryInitOptions options, GitContext context, CancellationToken cancellationToken)
    {
        string headPath = PathHelpers.Join(gitDir, "HEAD");

        // C (repository.c:2803-2805): "A template may have set a HEAD; use
        // that unless it's been overridden by the caller's given initial head."
        if (File.Exists(headPath) && options.InitialHead is null)
        {
            return;
        }

        // byte-primary HEAD write — C's repo_init_head feeds the raw init.defaultBranch char* bytes to git_repository_create_head, which writes "ref: <name>\n"
        // verbatim; a non-UTF-8 branch name round-trips byte-exact.
        byte[] branchName = options.InitialHead is { } givenHead
            ? Encoding.UTF8.GetBytes(givenHead)
            : await GetInitialBranchBytesAsync(context, cancellationToken).ConfigureAwait(false)
              ?? "master"u8.ToArray();

        byte[] content;
        if (branchName.AsSpan().StartsWith("refs/"u8))
        {
            content = [.. "ref: "u8.ToArray(), .. branchName, .. "\n"u8.ToArray()];
        }
        else
        {
            content = [.. "ref: refs/heads/"u8.ToArray(), .. branchName, .. "\n"u8.ToArray()];
        }

        await File.WriteAllBytesAsync(headPath, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the initial config file. Matches <c>repo_init_config</c>
    /// (repository.c:2312-2387) + <c>repo_init_fs_configs</c>
    /// (repository.c:2270-2310): the key set AND order mirror C —
    /// core.bare → core.repositoryformatversion → core.filemode →
    /// [core.symlinks] → [core.ignorecase] → [core.precomposeunicode] →
    /// core.logallrefupdates → [core.worktree] → [shared keys].
    /// </summary>
    private static async Task WriteInitConfigAsync(string gitDir, string? workDir, GitRepositoryInitOptions options, GitContext context, bool isReinit, bool naturalWd, CancellationToken cancellationToken)
    {
        string configPath = PathHelpers.Join(gitDir, "config");
        var backend = new FileConfigBackend(configPath, null, context.Dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken).ConfigureAwait(false);

        bool isBare = options.IsBare;

        int version = 0;
        if (isReinit)
        {
            // C (repository.c:2330-2332 → check_repositoryformatversion, 1836-1862): the reinit path fails on an UNPARSEABLE value ("failed to parse '%s' as a
            // 32-bit integer"); a missing key keeps the default.
            GitConfigEntry? existing = await backend.GetAsync("core.repositoryformatversion", cancellationToken).ConfigureAwait(false);
            if (existing is { } entry && entry.ValueBytes is { } v)
            {
                if (!ConfigurationValueParser.TryParseInt32(v.Span, out version))
                {
                    throw new GitException(
                        GitErrorCode.Error,
                        $"failed to parse '{entry.Value}' as a 32-bit integer",
                        GitErrorCategory.Config);
                }
            }
        }

        // C order: core.bare first, then repositoryformatversion.
        await backend.SetAsync("core.bare", isBare ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await backend.SetAsync("core.repositoryformatversion", version.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);

        // repo_init_fs_configs (repository.c:2270-2310).
        await backend.SetAsync("core.filemode", IsChmodSupported(configPath) ? "true" : "false", cancellationToken).ConfigureAwait(false);

        if (!AreSymlinksSupported())
        {
            await backend.SetAsync("core.symlinks", "false", cancellationToken).ConfigureAwait(false);
        }

        if (!isReinit && IsFilesystemCaseInsensitive(gitDir))
        {
            await backend.SetAsync("core.ignorecase", "true", cancellationToken).ConfigureAwait(false);
        }

        // core.precomposeunicode: C (iconv builds) writes the probe result;
        // the probe only decomposes on macOS (fs_path.c:1102-1151). The key is
        // omitted elsewhere (matching a reference build with iconv off).
        if (OperatingSystem.IsMacOS())
        {
            bool decomposes = DoesFilesystemDecomposeUnicode(workDir ?? gitDir);
            await backend.SetAsync("core.precomposeunicode", decomposes ? "true" : "false", cancellationToken).ConfigureAwait(false);
        }

        if (!isBare)
        {
            await backend.SetAsync("core.logallrefupdates", "true", cancellationToken).ConfigureAwait(false);

            if (!naturalWd)
            {
                string worktreePath = PathHelpers.PrettifyDir(workDir ?? gitDir);
                if ((options.Flags & GitRepositoryInitFlags.RelativeGitlink) != 0)
                {
                    worktreePath = PathHelpers.MakeRelative(worktreePath, gitDir);
                }

                await backend.SetAsync("core.worktree", worktreePath, cancellationToken).ConfigureAwait(false);
            }
            else if (isReinit)
            {
                try
                {
                    await backend.DeleteKeyAsync("core.worktree", cancellationToken).ConfigureAwait(false);
                }
                catch (GitException)
                {
                    // C: git_config_delete_entry(config, "core.worktree") < 0 →
                    // git_error_clear() (repository.c:2362-2363).
                }
            }
        }

        if (options.Mode == GitInitMode.SharedGroup)
        {
            await backend.SetAsync("core.sharedrepository", "1", cancellationToken).ConfigureAwait(false);
            await backend.SetAsync("receive.denyNonFastforwards", "true", cancellationToken).ConfigureAwait(false);
        }
        else if (options.Mode == GitInitMode.SharedAll)
        {
            await backend.SetAsync("core.sharedrepository", "2", cancellationToken).ConfigureAwait(false);
            await backend.SetAsync("receive.denyNonFastforwards", "true", cancellationToken).ConfigureAwait(false);
        }

        await backend.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary> Applies C's <c>GIT_MKDIR_CHMOD</c> step (futils.c:mkdir_validate_mode) for shared-mode inits: the directory is chmod'd to the exact <c>mode</c> so the umask cannot strip bits — notably <c>S_ISGID</c> (repository.c:2776-2780). No-op on Windows (no POSIX modes) and for the UMASK
    /// mode, where C has no chmod either (the mkdir+umask mode is what BCL <see cref="System.IO.Directory.CreateDirectory(string)"/> produces). </summary> <summary> Creates ONE
    /// directory component, failing when its parent is missing — matches <c>git_futils_mkdir</c> with <c>GIT_MKDIR_VERIFY_DIR</c> (no recursive flag): the
    /// parent must already exist. </summary>
    private static void CreateInitDirNonRecursive(string dir)
    {
        if (Directory.Exists(dir))
        {
            return;
        }

        string? parent = PathHelpers.Dirname(dir);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"could not create directory '{dir}': parent directory does not exist",
                GitErrorCategory.Repository);
        }

        Directory.CreateDirectory(dir);
    }

    private static void ApplyInitDirectoryMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if ((mode & (int)System.IO.UnixFileMode.SetGroup) == 0)
        {
            return;
        }

        try
        {
            new DirectoryInfo(path).UnixFileMode = (UnixFileMode)mode;
        }
        catch (Exception)
        {
            // C (mkdir_validate_mode, futils.c:451-460): p_chmod failure is
            // an OS error that fails the init.
            throw new GitException(
                GitErrorCode.Error,
                $"failed to set permissions on '{path}'",
                GitErrorCategory.Os);
        }
    }

    /// <summary>
    /// Checks if the filesystem supports chmod-based filemode changes.
    /// Matches <c>is_chmod_supported</c> (repository.c:2124-2137): flips the
    /// owner-execute bit on the config file and stats it back.
    /// </summary>
    private static bool IsChmodSupported(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var info = new FileInfo(filePath);
            UnixFileMode original = info.UnixFileMode;
            UnixFileMode flipped = original ^ UnixFileMode.UserExecute;
            info.UnixFileMode = flipped;
            UnixFileMode after = info.UnixFileMode;
            info.UnixFileMode = original;
            return after != original;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Matches <c>are_symlinks_supported</c> (POSIX: always supported).</summary>
    private static bool AreSymlinksSupported() => !OperatingSystem.IsWindows();

    /// <summary>
    /// Probes whether the filesystem is case-insensitive. Matches
    /// <c>is_filesystem_case_insensitive</c> (repository.c:2140-2150): checks
    /// whether the <c>CoNfIg</c> probe name resolves.
    /// </summary>
    private static bool IsFilesystemCaseInsensitive(string gitdirPath)
    {
        try
        {
            return File.Exists(PathHelpers.Join(gitdirPath, "CoNfIg"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if the filesystem supports chmod-based filemode changes. On Linux
    /// this is typically true. Matches <c>is_chmod_supported</c> (repository.c:2124-2137).
    /// </summary>
    private static bool IsFilemodeSupported()
    {
        // On POSIX (Linux/macOS), chmod is always supported.
        // On Windows, it's not.
        return !OperatingSystem.IsWindows();
    }

    /// <summary>
    /// Probes whether the filesystem at <paramref name="workDir"/> decomposes
    /// Unicode filenames (NFC storage is read back as NFD). Faithful managed
    /// port of <c>git_fs_path_does_decompose_unicode</c>
    /// (<c>src/util/fs_path.c:1090-1141</c>): creates a temporary file with a
    /// precomposed (NFC) name, then stats the decomposed (NFD) form of the same
    /// name — if the stat succeeds, the filesystem decomposes and
    /// <c>core.precomposeunicode</c> should be set. The temp file is deleted
    /// before returning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On non-macOS this returns <see langword="false"/> unconditionally,
    /// matching the <c>#else</c> stub at <c>fs_path.c:1143-1151</c> (the probe
    /// is meaningless on filesystems that do not decompose). Callers
    /// (<see cref="WriteInitConfigAsync"/>) additionally gate the call on
    /// <see cref="OperatingSystem.IsMacOS"/> to mirror the
    /// <c>#ifdef GIT_USE_ICONV</c> build guard.
    /// </para>
    /// <para>
    /// All filesystem access here is synchronous metadata operations
    /// (<see cref="System.IO.File.Create(string)"/>, <see cref="File.Exists"/>,
    /// <see cref="File.Delete"/>) — consistent with the AGENTS.md stat/metadata
    /// guidance and with the sibling <see cref="IsFilemodeSupported"/> probe.
    /// </para>
    /// </remarks>
    /// <param name="workDir">The workdir root to probe.</param>
    /// <returns>
    /// <see langword="true"/> if the filesystem decomposes Unicode filenames;
    /// <see langword="false"/> otherwise (including on non-macOS).
    /// </returns>
    private static bool DoesFilesystemDecomposeUnicode(string workDir)
    {
        // The non-iconv stub (fs_path.c:1143-1151): returns false without
        // touching the filesystem. The macOS gate is the faithful equivalent
        // of GIT_USE_ICONV being undefined.
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        // The NFC/NFD probe filenames used by libgit2 (fs_path.c:1090-1091):
        // "Åström" in precomposed and decomposed UTF-8.
        const string nfcFile = "Åström";
        const string nfdFile = "A\u030Astro\u0308m";

        // Use a random suffix (the C mktmp appends 6 random chars; we use
        // Path.GetRandomFileName and trim to 6 chars to match). The suffix is
        // appended to BOTH the NFC and NFD probe names so the only variable
        // is the composition of the Unicode base name.
        string trailer = System.IO.Path.GetRandomFileName().AsSpan(0, 6).ToString();

        string nfcPath = System.IO.Path.Join(workDir, nfcFile + trailer);
        string nfdPath = System.IO.Path.Join(workDir, nfdFile + trailer);

        try
        {
            // Create the temp file with the precomposed (NFC) name. The C
            // probe uses git_futils_mktmp + p_close; File.Create is the BCL
            // equivalent (open-create-close in one call). The using-dispose
            // flushes and closes the handle, ensuring the directory entry is
            // committed before the stat below.
            using (File.Create(nfcPath))
            {
            }

            // Stat the decomposed form (git_fs_path_exists). On HFS+/APFS
            // the file is stored as NFD, so this succeeds; on non-decomposing
            // filesystems the NFC and NFD names are distinct and this fails.
            return File.Exists(nfdPath);
        }
        catch (IOException)
        {
            // Any I/O error during the probe (e.g. permissions, read-only FS)
            // is treated as "does not decompose" — matching the C goto-done
            // path which returns the initial found_decomposed=false.
            return false;
        }
        finally
        {
            // Clean up the temp file (p_unlink in C). Best-effort: ignore
            // failures (the C probe also does (void)p_unlink).
            try
            {
                File.Delete(nfcPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// Sets the working directory for this repository. Matches
    /// <c>git_repository_set_workdir</c> (repository.c:3259-3307). Updates the
    /// in-memory <see cref="Workdir"/> and writes <c>core.worktree</c> to config
    /// if <paramref name="updateGitlink"/> is true and the workdir is not the
    /// gitdir's parent.
    /// </summary>
    public async Task SetWorkdirAsync(string workdir, bool updateGitlink = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workdir);

        // C's
        // git_repository_set_workdir calls git_fs_path_prettify_dir first
        // (repository.c:3262-3264) — realpath("") fails with ENOENT →
        // GIT_ENOTFOUND "failed to resolve path ''". PrettifyDir("") would
        // return "/" (Prettify's IsNullOrEmpty early-return + ToDir's
        // empty→"/" fallback), silently re-pointing the repository at the
        // filesystem root (and writing "/.git" under updateGitlink).
        if (workdir.Length == 0)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                "failed to resolve path ''",
                GitErrorCategory.Os);
        }

        string normalized = PathHelpers.PrettifyDir(workdir);

        // C (repository.c:3259-3307): no-op only when the new path equals the
        // CURRENT workdir (not "the gitdir's parent").
        if (Workdir is not null && string.Equals(Workdir, normalized, StringComparison.Ordinal))
        {
            return;
        }

        if (updateGitlink)
        {
            // repo_write_gitlink into the new workdir (absolute target).
            string gitlinkPath = PathHelpers.Join(normalized, ".git");
            if (Directory.Exists(gitlinkPath))
            {
                throw new GitException(
                    GitErrorCode.Exists,
                    $"cannot overwrite gitlink file into path '{normalized}'",
                    GitErrorCategory.Repository);
            }

            // A stale
            // REGULAR .git file is OVERWRITTEN (repo_write_gitlink →
            // repo_write_template with allow_overwrite → O_TRUNC,
            // repository.c:2424-2443);
            // the worktree pointed at a previous repository.
            if (!Directory.Exists(normalized))
            {
                Directory.CreateDirectory(normalized);
            }

            await AsyncFileIO.WriteAllTextWithNoBomAsync(gitlinkPath, $"gitdir: {PathHelpers.PrettifyDir(Path)}\n", cancellationToken).ConfigureAwait(false);

            // C: PASSTHROUGH (natural workdir) → delete core.worktree;
            // otherwise set it to the absolute path; core.bare = false.
            string gitDirParent = PathHelpers.PrettifyDir(PathHelpers.Dirname(Path));
            if (string.Equals(normalized, gitDirParent, StringComparison.Ordinal))
            {
                await Config.DeleteAsync("core.worktree", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Config.SetStringAsync("core.worktree", normalized, cancellationToken).ConfigureAwait(false);
            }

            await Config.SetStringAsync("core.bare", "false", cancellationToken).ConfigureAwait(false);
        }

        // In-memory update regardless of update_gitlink (C: repo->workdir and
        // repo->is_bare = 0 are always updated).
        Workdir = normalized;
        IsBare = false;
    }

    /// <summary> Sets the repository to bare mode. Matches <c>git_repository_set_bare</c> (repository.c:3321-3345): an already-bare repo returns early; after
    /// writing <c>core.bare=true</c> and deleting <c>core.worktree</c>, the in-memory workdir is freed and is_bare set. </summary>
    public async Task SetBareAsync(CancellationToken cancellationToken = default)
    {
        // C (repository.c:3323-3324): an already-bare repo is a no-op.
        if (IsBare)
        {
            return;
        }

        await Config.SetBoolAsync("core.bare", true, cancellationToken).ConfigureAwait(false);

        // Remove core.worktree if present.
        if (await Config.GetEntryAsync("core.worktree", cancellationToken).ConfigureAwait(false) is not null)
        {
            await Config.DeleteAsync("core.worktree", cancellationToken).ConfigureAwait(false);
        }

        // C (repository.c:3342-3344): git__free(repo->workdir); repo->workdir
        // = NULL; repo->is_bare = 1.
        Workdir = null;
        IsBare = true;
    }

    /// <summary>
    /// Sets the repository's index. Matches <c>git_repository_set_index</c>
    /// (repository.c:1694-1699).
    /// </summary>
    public void SetIndex(GitIndex? index)
    {
        _index = index;
        _indexChecked = true;
        index?.SetOwner(this);
    }

    /// <summary>
    /// Sets <c>HEAD</c> to point at the given branch ref (symbolic) or commit
    /// (detached). Matches <c>git_repository_set_head</c> (repository.c:3589-3637).
    /// If <paramref name="refName"/> is a branch (<c>refs/heads/...</c>), HEAD
    /// becomes a symbolic ref pointing at it. Otherwise HEAD is detached at
    /// the ref's target commit.
    /// </summary>
    /// <param name="refName">A fully-qualified ref name (e.g. <c>refs/heads/master</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetHeadAsync(string refName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refName);

        GitReference? current = await Refs.LookupAsync("HEAD", cancellationToken).ConfigureAwait(false);
        string logMessage = BuildCheckoutMessage(current, refName);

        GitReference? target = await Refs.LookupAsync(refName, cancellationToken).ConfigureAwait(false);
        if (target is not null)
        {
            if (target.IsBranch)
            {
                // C (repository.c:3603-3616): reject a branch that is the current HEAD of a linked repository.
                if (current is GitSymbolicReference sym &&
                    sym.TargetNameKey != target.NameKey &&
                    await target.IsCheckedOutAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new GitException(
                        GitErrorCode.Error,
                        $"cannot set HEAD to reference '{target.Name}' as it is the current HEAD of a linked repository.",
                        GitErrorCategory.Repository);
                }

                // Branch → symbolic HEAD.
                await Refs.CreateSymbolicAsync((RefNameKey)"HEAD", target.NameKey, force: true, logMessage, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Tag/remote/other → detach at the ref's target OID.
                GitOid targetId;
                if (target.IsSymbolic)
                {
                    //
                    // C's git_repository_set_head passes
                    // git_reference_target(ref) to detach(); for a SYMBOLIC
                    // (non-direct) ref that returns NULL (refs.c:340-346) and
                    // GIT_ASSERT_ARG(id) fails with GIT_ERROR_INVALID
                    // "invalid argument" (repository.c:3558) — C never
                    // resolves and detaches.
                    // chain (e.g. refs/remotes/origin/HEAD) and silently
                    // detach at the resolved OID.
                    throw new GitException(
                        GitErrorCode.Error,
                        "invalid argument",
                        GitErrorCategory.Invalid);
                }
                else
                {
                    targetId = ((GitDirectReference)target).Target;
                }

                await DetachHeadAtAsync(targetId, refName, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            // Unborn branch → symbolic HEAD pointing at the (nonexistent) branch.
            await Refs.CreateSymbolicAsync("HEAD", refName, force: true, logMessage, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{refName}' not found",
                GitErrorCategory.Reference);
        }
    }

    /// <summary>
    /// Detaches HEAD at the given commit OID. Matches
    /// <c>git_repository_set_head_detached</c> (repository.c:3639-3644).
    /// </summary>
    public async Task SetHeadDetachedAsync(GitOid commitId, CancellationToken cancellationToken = default)
    {
        await DetachHeadAtAsync(commitId, refName: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches HEAD at the commit identified by an annotated tag/commit ref.
    /// Matches <c>git_repository_set_head_detached_from_annotated</c>
    /// (repository.c:3646-3654).
    /// </summary>
    /// <param name="refName">The ref name whose target is detached (used for the reflog message).</param>
    /// <param name="commitId">The commit OID to detach at.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetHeadDetachedFromAnnotatedAsync(string refName, GitOid commitId, CancellationToken cancellationToken = default)
    {
        await DetachHeadAtAsync(commitId, refName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches HEAD: writes the current HEAD's target OID as a direct ref.
    /// Matches <c>git_repository_detach_head</c> (repository.c:3656-3695).
    /// </summary>
    public async Task DetachHeadAsync(CancellationToken cancellationToken = default)
    {
        GitReference current = await Refs.LookupAsync("HEAD", cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "HEAD not found",
                GitErrorCategory.Reference);

        GitReference resolved = await Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.UnbornBranch,
                "HEAD points at unborn branch",
                GitErrorCategory.Reference);

        GitOid targetId = ((GitDirectReference)resolved).Target;
        string logMessage = BuildCheckoutMessage(current, targetId.ToString());
        await Refs.CreateAsync("HEAD", targetId, force: true, logMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Internal: detaches HEAD at the given commit OID with an optional reflog
    /// message derived from the ref name. Matches <c>detach</c>
    /// (repository.c:3545-3587).
    /// </summary>
    private async Task DetachHeadAtAsync(GitOid commitId, string? refName, CancellationToken cancellationToken)
    {
        // C's detach
        // (repository.c:3564-3585) does git_object_lookup(GIT_OBJECT_ANY) +
        // git_object_peel(GIT_OBJECT_COMMIT) and creates HEAD at the PEELED
        // commit's OID.
        // an annotated tag put the tag-object OID (and the tag OID in the
        // reflog) into HEAD, diverging from C bit-for-bit and breaking
        // HEAD-as-commit consumers.
        GitObject obj = await Objects.LookupAsync(commitId, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"object {commitId} not found",
                GitErrorCategory.Object);
        using (obj)
        {
            Commit commit = await obj.PeelAsync<Commit>(cancellationToken).ConfigureAwait(false);
            commitId = commit.Id;
        }

        string logMessage = refName is not null
            ? $"checkout: moving from {await DescribeHeadAsync(cancellationToken).ConfigureAwait(false)} to {Shorten(refName)}"
            : $"checkout: moving from {await DescribeHeadAsync(cancellationToken).ConfigureAwait(false)} to {commitId}";

        await Refs.CreateAsync("HEAD", commitId, force: true, logMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a checkout-style reflog message: <c>"checkout: moving from X to Y"</c>.
    /// Matches <c>checkout_message</c> (repository.c:3535-3543).
    /// </summary>
    private static string BuildCheckoutMessage(GitReference? currentHead, string toRefName)
    {
        string from = DescribeRef(currentHead);
        string to = Shorten(toRefName);
        return $"checkout: moving from {from} to {to}";
    }

    /// <summary>Describes the current HEAD for reflog messages.</summary>
    private async Task<string> DescribeHeadAsync(CancellationToken cancellationToken)
    {
        GitReference? head = await Refs.LookupAsync("HEAD", cancellationToken).ConfigureAwait(false);
        return DescribeRef(head);
    }

    /// <summary>Describes a ref for a reflog "from" field: branch short name or OID.</summary>
    private static string DescribeRef(GitReference? reference)
    {
        if (reference is null)
        {
            return "HEAD";
        }

        if (reference.IsSymbolic)
        {
            return Shorten(((GitSymbolicReference)reference).TargetNameKey.ToUtf8StringStrict());
        }

        return ((GitDirectReference)reference).Target.ToString();
    }

    /// <summary>Shortens a ref name to its branch/tag short name.</summary>
    private static string Shorten(string refName)
    {
        if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            return refName["refs/heads/".Length..];
        }

        if (refName.StartsWith("refs/tags/", StringComparison.Ordinal))
        {
            return refName["refs/tags/".Length..];
        }

        if (refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            return refName["refs/remotes/".Length..];
        }

        return refName;
    }

    /// <summary>
    /// Reads the merge message from <c>MERGE_MSG</c>. Matches
    /// <c>git_repository_message</c> (repository.c:3429-3432).
    /// </summary>
    /// <returns>The message content, or null if <c>MERGE_MSG</c> doesn't exist.</returns>
    public ValueTask<string?> MessageAsync(CancellationToken cancellationToken = default)
    {
        string msgPath = System.IO.Path.Join(Path, "MERGE_MSG");
        // Absence of MERGE_MSG is the majority case for an idle repo.
        if (!File.Exists(msgPath))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return new ValueTask<string?>(MessageSlowAsync(msgPath, cancellationToken));
    }

    /// <summary>Slow path of <see cref="MessageAsync"/>: reads MERGE_MSG from disk.</summary>
    private static async Task<string?> MessageSlowAsync(string msgPath, CancellationToken cancellationToken)
        => await AsyncFileIO.ReadAllTextWithNoBomAsync(msgPath, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Removes the merge message file. Matches
    /// <c>git_repository_message_remove</c> (repository.c:3434-3446).
    /// </summary>
    public void RemoveMessage()
    {
        string msgPath = System.IO.Path.Join(Path, "MERGE_MSG");
        if (File.Exists(msgPath))
        {
            File.Delete(msgPath);
        }
    }

    /// <summary> Iterates over each OID listed in <c>.git/MERGE_HEAD</c>. Matches <c>git_repository_mergehead_foreach</c> (<c>merge.c:588-641</c>): each
    /// <c>\n</c>-split line must be exactly the OID hex size (GIT_ERROR_INVALID "unable to parse OID - invalid length"); leftover bytes without a trailing
    /// newline abort with GIT_ERROR_MERGE "no EOL at line N". Yields one <see cref="GitOid"/> per line. </summary> <returns>An async enumerable of merge-head
    /// OIDs. Empty if no <c>MERGE_HEAD</c> file exists.</returns> <exception cref="GitException"> <see cref="GitErrorCode.Error"/> on a malformed file.
    /// </exception>
    public async IAsyncEnumerable<GitOid> MergeHeadForEachAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string mergeHeadPath = System.IO.Path.Join(Path, "MERGE_HEAD");
        if (!File.Exists(mergeHeadPath))
        {
            yield break;
        }

        // C reads the raw buffer and splits on '\n' via git__strsep: a line
        // WITHOUT a newline terminator is NOT parsed — the leftover bytes
        // trigger "no EOL at line N". CRLF lines are 41 bytes ≠ hexsize.
        string content = await AsyncFileIO.ReadAllTextWithNoBomAsync(mergeHeadPath, cancellationToken).ConfigureAwait(false);
        int hexSize = GitOid.HexSizeFor(ObjectFormat);
        int lineNum = 1;
        int start = 0;

        while (start < content.Length)
        {
            int nl = content.IndexOf('\n', start, StringComparison.Ordinal);
            if (nl < 0)
            {
                // C (merge.c:635-638): leftover bytes without a trailing
                // newline → GIT_ERROR_MERGE "no EOL at line N".
                throw new GitException(
                    GitErrorCode.Error,
                    $"no EOL at line {lineNum}",
                    GitErrorCategory.Merge);
            }

            string line = content[start..nl];

            // C (merge.c:606-611): strlen(line) != hexsize → GIT_ERROR_INVALID.
            if (line.Length != hexSize)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "unable to parse OID - invalid length",
                    GitErrorCategory.Invalid);
            }

            if (!GitOid.TryParse(line.AsSpan(), ObjectFormat, out GitOid oid))
            {
                // C (merge.c:612-614, git_oid__fromstr → git_oid__fromstrn,
                // oid.c:57): a non-hex character → GIT_ERROR_INVALID
                // "unable to parse OID - contains invalid characters".
                throw new GitException(
                    GitErrorCode.Error,
                    "unable to parse OID - contains invalid characters",
                    GitErrorCategory.Invalid);
            }

            yield return oid;
            lineNum++;
            start = nl + 1;
        }
    }

    /// <summary>
    /// Cleans up repository state files. Matches
    /// <c>git_repository_state_cleanup</c> (repository.c:3779-3784). Removes:
    /// MERGE_HEAD, MERGE_MODE, MERGE_MSG, REVERT_HEAD, CHERRY_PICK_HEAD,
    /// BISECT_LOG, rebase-merge/, rebase-apply/, sequencer/.
    /// </summary>
    public void StateCleanup()
        => StateCleanup(
            ["MERGE_HEAD", "MERGE_MODE", "MERGE_MSG", "REVERT_HEAD", "CHERRY_PICK_HEAD", "BISECT_LOG"],
            ["rebase-merge", "rebase-apply", "sequencer"]);

    /// <summary>
    /// Removes only the given state files/dirs. The merge/cherry-pick/revert
    /// error paths pass their OWN operation's files, matching C's
    /// per-operation cleanup (merge.c:3165-3174, cherrypick.c:99-104,
    /// revert.c:100-105) — a failing operation must not delete state
    /// belonging to another in-progress operation.
    /// </summary>
    private void StateCleanup(string[] stateFiles, string[] stateDirs)
    {
        foreach (string? file in stateFiles)
        {
            string filePath = System.IO.Path.Join(Path, file);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        foreach (string? dir in stateDirs)
        {
            string dirPath = System.IO.Path.Join(Path, dir);
            if (Directory.Exists(dirPath))
            {
                Directory.Delete(dirPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// Determines the current repository state (in-progress merge/rebase/
    /// cherry-pick/revert/bisect). Matches <c>git_repository_state</c>
    /// (repository.c:3699-3737). Examines the gitdir for state files and
    /// directories in C's exact priority order: rebase-merge/interactive,
    /// rebase-merge/, rebase-apply/rebasing, rebase-apply/applying,
    /// rebase-apply/, MERGE_HEAD, REVERT_HEAD (+sequencer/todo),
    /// CHERRY_PICK_HEAD (+sequencer/todo), BISECT_LOG.
    /// </summary>
    public RepositoryState State
    {
        get
        {
            // 1. rebase-merge/interactive → REBASE_INTERACTIVE
            if (File.Exists(System.IO.Path.Join(Path, "rebase-merge", "interactive")))
            {
                return RepositoryState.RebaseInteractive;
            }

            // 2. rebase-merge/ dir → REBASE_MERGE
            if (Directory.Exists(System.IO.Path.Join(Path, "rebase-merge")))
            {
                return RepositoryState.RebaseMerge;
            }

            // 3. rebase-apply/rebasing → REBASE
            if (File.Exists(System.IO.Path.Join(Path, "rebase-apply", "rebasing")))
            {
                return RepositoryState.Rebase;
            }

            // 4. rebase-apply/applying → APPLY_MAILBOX
            if (File.Exists(System.IO.Path.Join(Path, "rebase-apply", "applying")))
            {
                return RepositoryState.ApplyMailbox;
            }

            // 5. rebase-apply/ dir → APPLY_MAILBOX_OR_REBASE
            if (Directory.Exists(System.IO.Path.Join(Path, "rebase-apply")))
            {
                return RepositoryState.ApplyMailboxOrRebase;
            }

            // 6. MERGE_HEAD → MERGE
            if (File.Exists(System.IO.Path.Join(Path, "MERGE_HEAD")))
            {
                return RepositoryState.Merge;
            }

            // 7. REVERT_HEAD → REVERT (+ sequencer/todo → REVERT_SEQUENCE)
            if (File.Exists(System.IO.Path.Join(Path, "REVERT_HEAD")))
            {
                return File.Exists(System.IO.Path.Join(Path, "sequencer", "todo"))
                    ? RepositoryState.RevertSequence
                    : RepositoryState.Revert;
            }

            // 8. CHERRY_PICK_HEAD → CHERRYPICK (+ sequencer/todo → CHERRYPICK_SEQUENCE)
            if (File.Exists(System.IO.Path.Join(Path, "CHERRY_PICK_HEAD")))
            {
                return File.Exists(System.IO.Path.Join(Path, "sequencer", "todo"))
                    ? RepositoryState.CherryPickSequence
                    : RepositoryState.CherryPick;
            }

            // 9. BISECT_LOG → BISECT
            if (File.Exists(System.IO.Path.Join(Path, "BISECT_LOG")))
            {
                return RepositoryState.Bisect;
            }

            return RepositoryState.None;
        }
    }

    /// <summary>
    /// Sets the repository identity (overrides <c>user.name</c>/<c>user.email</c>).
    /// Matches <c>git_repository_set_ident</c> (repository.c:3894-3911).
    /// Stored in-memory only.
    /// </summary>
    public void SetIdent(string? name, string? email)
    {
        _identName = name;
        _identEmail = email;
    }

    private string? _identName;
    private string? _identEmail;

    /// <summary>The overridden user name (set by <see cref="SetIdent"/>), or null.</summary>
    internal string? IdentName => _identName;

    /// <summary>The overridden user email (set by <see cref="SetIdent"/>), or null.</summary>
    internal string? IdentEmail => _identEmail;

    /// <summary>
    /// Writes the ORIG_HEAD file. Matches <c>git_repository__set_orig_head</c>
    /// (repository.c:3385-3405).
    /// </summary>
    public async Task SetOrigHeadAsync(GitOid origHead, CancellationToken cancellationToken = default)
    {
        string origHeadPath = System.IO.Path.Join(Path, "ORIG_HEAD");
        await AsyncFileIO.WriteAllTextWithNoBomAsync(origHeadPath, $"{origHead}\n", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _index?.Dispose();
        await Config.DisposeAsync().ConfigureAwait(false);
        await Objects.DisposeAsync().ConfigureAwait(false);
        await Refs.DisposeAsync().ConfigureAwait(false);
        Grafts?.Dispose();
        ShallowGrafts?.Dispose();
    }

    /// <summary>
    /// Loads the grafts and shallow files. Matches <c>load_grafts</c>
    /// (repository.c:877-918): <c>&lt;commondir&gt;/info/grafts</c> and
    /// <c>&lt;gitdir&gt;/shallow</c>, with a missing file opening as an
    /// empty graft set.
    /// </summary>
    internal async Task LoadGraftsAsync(CancellationToken cancellationToken)
    {
        string graftsPath = System.IO.Path.Join(CommonDir, "info", "grafts");
        Grafts = await Grafts.OpenAsync(graftsPath, ObjectFormat, cancellationToken).ConfigureAwait(false);
        ShallowGrafts = await Grafts.OpenAsync(System.IO.Path.Join(Path, "shallow"), ObjectFormat, cancellationToken).ConfigureAwait(false);
    }
}
