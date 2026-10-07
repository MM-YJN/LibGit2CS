// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Utils;

namespace LibGit2CS.Repository;

/// <summary>
/// Implements the repository open/discover path. Managed port of
/// libgit2's <c>src/libgit2/repository.c</c> open functions:
/// <c>find_repo</c>, <c>find_repo_traverse</c>, <c>git_repository_open_ext</c>,
/// <c>git_repository_discover</c>, and the helper functions
/// <c>obtain_config_and_set_oid_type</c>, <c>load_config_data</c>,
/// <c>load_workdir</c>, <c>repo_is_worktree</c>, <c>validate_ownership</c>,
/// <c>repo_load_namespace</c>, <c>load_grafts</c>.
/// </summary>
/// <remarks>
/// This is the workhorse for <see cref="GitRepository.OpenExtAsync"/> and
/// <see cref="GitRepository.DiscoverAsync"/>. Internal — not part of the public API.
/// </remarks>
internal static class RepositoryOpener
{
    /// <summary> Opens a bare repository. Matches <c>git_repository_open_bare</c> (repository.c:1021-1064): a self-contained fast path — prettify →
    /// is_valid_repository_path → alloc with is_bare=1, is_worktree=0, workdir=NULL → obtain_config_and_set_oid_type. It NEVER calls <c>load_grafts</c>,
    /// <c>repo_load_namespace</c>, or <c>validate_ownership</c>, and never consults the environment. The port constructs the ODB/refdb eagerly (the C# model);
    /// unlike <c>open_ext</c>, a missing objects dir does NOT fail the open (C's ODB is lazy — the failure surfaces on first use). </summary>
    public static async Task<GitRepository> OpenBareAsync(string path, GitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        // C (repository.c:1025-1026): git_fs_path_prettify_dir.
        string gitdir = PathHelpers.PrettifyDir(path);

        // C (repository.c:1027-1035, is_valid_repository_path + common dir).
        (bool isValid, string commonDir) = await IsValidRepositoryPathAsync(gitdir, RepositoryOpenFlags.None, context, cancellationToken).ConfigureAwait(false);
        if (!isValid)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"path is not a repository: {path}",
                GitErrorCategory.Repository);
        }

        // C (repository.c:1055-1063, obtain_config_and_set_oid_type).
        GitConfiguration config = await OpenRepoConfigAsync(commonDir, gitdir, useEnv: false, context, cancellationToken).ConfigureAwait(false);
        (_, GitHashAlgorithmKind objectFormat) = await ObtainConfigAndSetOidTypeAsync(config, cancellationToken).ConfigureAwait(false);

        // C (repository_odb_path, repository.c:1485-1497): no env override
        // for open_bare; the ODB is constructed lazily in C, so a missing
        // objects dir is NOT an open error here.
        string objectsDir = Path.Join(commonDir, "objects");
        var odb = new GitObjectDb(owner: null, context.Settings);
        if (Directory.Exists(objectsDir))
        {
            await odb.AddDefaultBackendsAsync(objectsDir, objectFormat, alternateDepth: 0, cancellationToken).ConfigureAwait(false);
        }

        // C creates the refdb lazily; the port constructs it eagerly.
        var refDb = new RefDatabase();
        var refBackend = new FileRefBackend(gitdir, commonDir, objectFormat);
        refDb.SetBackend(refBackend);
        var refs = new GitReferences(refDb);

        // No grafts (LoadGraftsAsync is skipped), no namespace, no ownership validation.
        return new GitRepository(
            gitdir: gitdir,
            commondir: commonDir,
            workdir: null,
            isBare: true,
            isWorktree: false,
            objectFormat: objectFormat,
            @namespace: null,
            objects: odb,
            config: config,
            context: context,
            useEnv: false,
            refs: refs);
    }

    /// <summary>
    /// Opens a repository. Matches <c>git_repository_open_ext</c>.
    /// </summary>
    public static async Task<GitRepository> OpenAsync(string startPath, RepositoryOpenFlags flags, string? ceilingDirs, GitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startPath);

        bool useEnv = (flags & RepositoryOpenFlags.FromEnv) != 0;

        // Discover the git directory. GIT_DIR and friends are only consulted
        // with GIT_REPOSITORY_OPEN_FROM_ENV (C repository.c:920-975).
        RepoPaths paths = await FindRepoAsync(startPath, flags, ceilingDirs, context, cancellationToken).ConfigureAwait(false);

        // Read config to determine bare status and object format.
        GitConfiguration config = await OpenRepoConfigAsync(paths.CommonDir, paths.GitDir, useEnv, context, cancellationToken).ConfigureAwait(false);
        (bool isBare, GitHashAlgorithmKind objectFormat) = await ObtainConfigAndSetOidTypeAsync(config, cancellationToken).ConfigureAwait(false);

        // Detect worktree: commondir != gitdir AND a gitdir file exists inside gitdir.
        bool isWorktree = DetectWorktree(paths);

        // C (load_config_data, repository.c:359-374): is_bare = is_bare && !is_worktree.
        isBare = isBare && !isWorktree;

        // Determine workdir.
        string? workdir = null;
        if ((flags & RepositoryOpenFlags.Bare) == 0 && !isBare)
        {
            workdir = await LoadWorkdirAsync(paths, config, context, useEnv, cancellationToken).ConfigureAwait(false);
        }

        // C (repo_load_namespace, repository.c:1066-1082): GIT_NAMESPACE is
        // only read when use_env is set.
        string? ns = null;
        if (useEnv)
        {
            ns = context.Env["GIT_NAMESPACE"];
        }

        // Validate ownership (safe.directory).
        if (context.Settings.OwnerValidation)
        {
            await ValidateOwnershipAsync(paths, workdir, context, useEnv, cancellationToken).ConfigureAwait(false);
        }

        // Create the ODB. C (repository_odb_path, repository.c:1485-1497): GIT_OBJECT_DIRECTORY (FROM_ENV only) overrides <commondir>/objects. C
        // (repository.c:1485-1497, repository_odb_path): git__getenv honors ANY set value — including an EMPTY one — as the objects dir override, just
        // like its own GIT_INDEX_FILE handling.
        string objectsDir = useEnv && context.Env["GIT_OBJECT_DIRECTORY"] is { } envObjects
            ? PathHelpers.PrettifyLexicalDir(envObjects)
            : Path.Join(paths.CommonDir, "objects");
        var odb = new GitObjectDb(owner: null, context.Settings);

        // C (git_odb__add_default_backends, odb.c:695-705): on POSIX a
        // missing objects dir fails the ODB creation with GIT_ERROR_ODB
        // "failed to load object database in '%s'" (the alternate case
        // silently warns instead). The ODB is constructed eagerly at open
        // (C does so lazily on the first git_repository_odb), so the failure
        // surfaces at open (reads return not-found instead of this
        // error).
        bool objectsExists = Directory.Exists(objectsDir) || File.Exists(objectsDir);
        if (!OperatingSystem.IsWindows() && !objectsExists)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to load object database in '{objectsDir}'",
                GitErrorCategory.Odb);
        }

        if (Directory.Exists(objectsDir))
        {
            await odb.AddDefaultBackendsAsync(objectsDir, objectFormat, alternateDepth: 0, cancellationToken).ConfigureAwait(false);
        }

        // C (repository_odb_alternates, repository.c:1499-1522): with use_env,
        // GIT_ALTERNATE_OBJECT_DIRECTORIES is split on the path-list separator
        // and every token is added as a disk alternate (no '#'/empty filtering
        // — that only applies to the info/alternates file form). The C
        // strchr loop adds interior empty tokens ("a::b" → "a","","b") but
        // stops at the NUL terminator, so a trailing separator produces no
        // final empty token ("a:" → "a" only).
        if (useEnv && context.Env["GIT_ALTERNATE_OBJECT_DIRECTORIES"] is { } alternatesEnv)
        {
            int start = 0;
            while (start < alternatesEnv.Length)
            {
#pragma warning disable CA1307 // char overload: no StringComparison form exists
                int sep = alternatesEnv.IndexOf(Path.PathSeparator, start);
#pragma warning restore CA1307
                string token = sep < 0 ? alternatesEnv[start..] : alternatesEnv[start..sep];
                await odb.AddDefaultBackendsAsync(token, objectFormat, alternateDepth: 0, cancellationToken).ConfigureAwait(false);
                if (sep < 0)
                {
                    break;
                }

                start = sep + 1;
            }
        }

        // Create the ref database.
        var refDb = new RefDatabase();
        var refBackend = new FileRefBackend(paths.GitDir, paths.CommonDir, objectFormat);
        refDb.SetBackend(refBackend);
        var refs = new GitReferences(refDb);

        return await LoadGraftsAndCreateAsync(
            gitdir: paths.GitDir,
            commondir: paths.CommonDir,
            workdir: workdir,
            isBare: isBare || (flags & RepositoryOpenFlags.Bare) != 0,
            isWorktree: isWorktree,
            objectFormat: objectFormat,
            @namespace: ns,
            objects: odb,
            config: config,
            context: context,
            refs: refs,
            useEnv: useEnv,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the <see cref="GitRepository"/> and loads its grafts. Matches
    /// C's open sequence: <c>git_repository_open_ext</c> ends with
    /// <c>load_grafts</c> (repository.c:1153), which reads
    /// <c>&lt;commondir&gt;/info/grafts</c> and <c>&lt;gitdir&gt;/shallow</c>
    /// so that every commit parse applies them (commit.c:554-567).
    /// </summary>
    private static async Task<GitRepository> LoadGraftsAndCreateAsync(
        string gitdir, string commondir, string? workdir, bool isBare, bool isWorktree,
        GitHashAlgorithmKind objectFormat, string? @namespace, GitObjectDb objects,
        GitConfiguration config, GitContext context, GitReferences refs, bool useEnv,
        CancellationToken cancellationToken)
    {
        var repo = new GitRepository(
            gitdir: gitdir,
            commondir: commondir,
            workdir: workdir,
            isBare: isBare,
            isWorktree: isWorktree,
            objectFormat: objectFormat,
            @namespace: @namespace,
            objects: objects,
            config: config,
            context: context,
            useEnv: useEnv,
            refs: refs);

        await repo.LoadGraftsAsync(cancellationToken).ConfigureAwait(false);
        return repo;
    }

    /// <summary>
    /// Decides whether the discovery walk may continue past a candidate whose
    /// device differs from the walk's baseline device. Matches the device
    /// check in <c>find_repo_traverse</c> (repository.c:788-794): the first
    /// existing candidate fixes <paramref name="initialDevice"/>; a later
    /// candidate on a different device stops the walk unless
    /// <c>GIT_REPOSITORY_OPEN_CROSS_FS</c> is set. A null device (stat
    /// unavailable) never stops the walk.
    /// </summary>
    internal static bool DeviceBoundaryStopsWalk(uint? initialDevice, uint? candidateDevice, bool crossFs)
        => initialDevice is not null
            && candidateDevice is not null
            && initialDevice != candidateDevice
            && !crossFs;

    /// <summary>
    /// Discovers the git directory starting from <paramref name="startPath"/>.
    /// Matches <c>git_repository_discover</c> — a full upward search with no
    /// env-var consultation (C passes flags = CROSS_FS?across:0 only).
    /// </summary>
    public static async Task<string> DiscoverAsync(string startPath, bool acrossFs, string? ceilingDirs, GitContext context, CancellationToken cancellationToken)
    {
        RepositoryOpenFlags flags = acrossFs ? RepositoryOpenFlags.CrossFs : RepositoryOpenFlags.None;

        RepoPaths paths = await FindRepoAsync(startPath, flags, ceilingDirs, context, cancellationToken).ConfigureAwait(false);
        return paths.GitDir;
    }

    /// <summary>
    /// Walks upward from <paramref name="startPath"/> looking for a
    /// repository. Matches <c>find_repo</c> (repository.c:900-975) +
    /// <c>find_repo_traverse</c> (repository.c:754-865).
    /// </summary>
    /// <remarks>
    /// <b>Gitlink handling</b>: <c>.git</c> may be a file (worktree pointer) containing
    /// <c>gitdir: /path/to/.git/worktrees/foo</c>. Ceiling dirs terminate the
    /// upward walk. <c>GIT_DIR</c> env var is honored only with
    /// <c>GIT_REPOSITORY_OPEN_FROM_ENV</c> and no start path. Bare repos have
    /// no workdir.
    /// </remarks>
    internal static async Task<RepoPaths> FindRepoAsync(string startPath, RepositoryOpenFlags flags, string? ceilingDirs, GitContext context, CancellationToken cancellationToken)
    {
        bool useEnv = (flags & RepositoryOpenFlags.FromEnv) != 0;

        // find_repo (repository.c:900-975): FROM_ENV gating.
        if (useEnv && string.IsNullOrEmpty(startPath))
        {
            string? gitDirEnv = context.Env["GIT_DIR"];
            if (!string.IsNullOrEmpty(gitDirEnv))
            {
                startPath = gitDirEnv;
                flags |= RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.NoDotgit;
            }
            else
            {
                startPath = ".";
            }
        }

        if (useEnv && string.IsNullOrEmpty(ceilingDirs))
        {
            ceilingDirs = context.Env["GIT_CEILING_DIRECTORIES"];
        }

        if (useEnv)
        {
            string? acrossFs = context.Env["GIT_DISCOVERY_ACROSS_FILESYSTEM"];
            if (acrossFs is not null && ParseBoolStrict(acrossFs))
            {
                flags |= RepositoryOpenFlags.CrossFs;
            }
        }

        bool noSearch = (flags & RepositoryOpenFlags.NoSearch) != 0;
        bool noDotGit = (flags & RepositoryOpenFlags.NoDotgit) != 0;
        bool crossFs = (flags & RepositoryOpenFlags.CrossFs) != 0;

        string start = PathHelpers.Prettify(startPath);
        if (string.IsNullOrEmpty(start))
        {
            start = ".";
        }

        // find_repo_traverse (repository.c:754-865): with BARE/NO_DOTGIT the
        // start path is checked first (no .git appended), otherwise start/.git
        // is checked first. min_iterations = 2 (or 1) keeps the first
        // iteration(s) from counting as a "search"; with NO_SEARCH the walk
        // stops after checking start/.git and start.
        string path = start.TrimEnd(Path.DirectorySeparatorChar, '/');
        if (path.Length == 0)
        {
            path = start;
        }

        bool inDotGit = (flags & (RepositoryOpenFlags.Bare | RepositoryOpenFlags.NoDotgit)) != 0;
        int minIterations = inDotGit ? 1 : 2;
        int ceilingOffset = -1;

        // C (find_repo_traverse, repository.c:788-794): st_dev of the first
        // existing candidate fixes the baseline device; a later candidate on a
        // different device stops the walk unless GIT_REPOSITORY_OPEN_CROSS_FS.
        uint? initialDevice = null;

        string? foundGitDir = null;
        string? foundGitlink = null;
        string? foundCommonDir = null;

        while (true)
        {
            string candidate = path;
            if (!noDotGit)
            {
                if (!inDotGit)
                {
                    candidate = Path.Join(path, ".git");
                }

                inDotGit = !inDotGit;
            }

            // C (find_repo_traverse, repository.c:788-794): the device check
            // applies to every candidate that exists (p_stat succeeded) — a
            // device crossing stops the walk unless CROSS_FS.
            if (DeviceCheckStops(candidate, ref initialDevice, crossFs))
            {
                break;
            }

            if (Directory.Exists(candidate))
            {
                // C: is_valid_repository_path — an invalid candidate keeps the
                // walk going.
                (bool valid, string commonDir) = await IsValidRepositoryPathAsync(candidate, flags, context, cancellationToken).ConfigureAwait(false);
                if (valid)
                {
                    foundGitDir = PathHelpers.PrettifyDir(candidate);
                    foundCommonDir = commonDir;

                    // C's find_repo_traverse directory branch attaches
                    // git_worktree__read_link(path, GIT_GITDIR_FILE) — the
                    // content of the gitdir file, i.e. the worktree's .git
                    // file path — to out->gitlink (repository.c:823-826), so
                    // validate_ownership checks [workdir, gitlink, gitdir]. A
                    // directory-discovered linked worktree counts as a gitlink,
                    // so it is included in the ownership check.
                    foundGitlink = await ReadWorktreeGitlinkAsync(foundGitDir, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
            else if (File.Exists(candidate) && (candidate.EndsWith("/.git", StringComparison.Ordinal) || candidate.EndsWith(Path.DirectorySeparatorChar + ".git", StringComparison.Ordinal)))
            {
                // C: a regular ".git" file — read_gitfile, validate, and break
                // REGARDLESS of validity (repository.c:812-825).
                string repoLink = await ReadGitfileAsync(candidate, cancellationToken).ConfigureAwait(false);
                (bool valid, string commonDir) = await IsValidRepositoryPathAsync(repoLink, flags, context, cancellationToken).ConfigureAwait(false);
                if (valid)
                {
                    foundGitDir = PathHelpers.PrettifyDir(repoLink);
                    foundGitlink = candidate;
                    foundCommonDir = commonDir;
                }

                break;
            }

            // Port extension (not in libgit2): the
            // test fixtures use a ".gitted" name for the gitdir. Kept so the
            // fixture repos stay discoverable; treated exactly like ".git".
            if (!noDotGit)
            {
                string gitted = Path.Join(path, ".gitted");
                if (DeviceCheckStops(gitted, ref initialDevice, crossFs))
                {
                    break;
                }

                if (Directory.Exists(gitted))
                {
                    (bool valid, string commonDir) = await IsValidRepositoryPathAsync(gitted, flags, context, cancellationToken).ConfigureAwait(false);
                    if (valid)
                    {
                        foundGitDir = PathHelpers.PrettifyDir(gitted);
                        foundCommonDir = commonDir;
                        break;
                    }
                }
                else if (File.Exists(gitted))
                {
                    string repoLink = await ReadGitfileAsync(gitted, cancellationToken).ConfigureAwait(false);
                    (bool valid, string commonDir) = await IsValidRepositoryPathAsync(repoLink, flags, context, cancellationToken).ConfigureAwait(false);
                    if (valid)
                    {
                        foundGitDir = PathHelpers.PrettifyDir(repoLink);
                        foundGitlink = gitted;
                        foundCommonDir = commonDir;
                    }

                    break;
                }
            }

            // Move up one directory — C dirnames the CANDIDATE (so after
            // checking start/.git the walk continues from start).
            string parent = PathHelpers.Dirname(candidate);
            if (parent == candidate)
            {
                break; // reached root
            }

            path = parent;

            // Once the start path (and .git) have been checked, compute the
            // ceiling offset for the walk (repository.c:842-848).
            if (minIterations > 0 && --minIterations == 0)
            {
                ceilingOffset = FindCeilingDirOffset(path, ceilingDirs);
            }

            if (minIterations == 0 && (path.Length <= ceilingOffset || noSearch))
            {
                break;
            }
        }

        if (foundGitDir is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"could not find repository at '{startPath}'",
                GitErrorCategory.Repository);
        }

        return new RepoPaths(foundGitDir, foundCommonDir ?? await ResolveCommonDirAsync(foundGitDir, flags, context, cancellationToken).ConfigureAwait(false), foundGitlink);
    }

    /// <summary>
    /// Resolves the commondir. For worktrees, reads the <c>commondir</c> file
    /// inside the gitdir. For normal repos, commondir == gitdir.
    /// Matches <c>lookup_commondir</c> (repository.c:199-248). The
    /// <c>GIT_COMMON_DIR</c> env override is gated on FROM_ENV.
    /// </summary>
    private static ValueTask<string> ResolveCommonDirAsync(string gitdir, RepositoryOpenFlags flags, GitContext context, CancellationToken cancellationToken)
    {
        if ((flags & RepositoryOpenFlags.FromEnv) != 0 && !string.IsNullOrEmpty(context.Env["GIT_COMMON_DIR"]))
        {
            return ValueTask.FromResult(PathHelpers.PrettifyDir(context.Env["GIT_COMMON_DIR"]!));
        }

        string commonFile = Path.Join(gitdir, "commondir");
        if (!File.Exists(commonFile))
        {
            // No commondir file — commondir == gitdir (majority for main repos).
            return ValueTask.FromResult(PathHelpers.ToDir(gitdir));
        }

        return new ValueTask<string>(ResolveCommonDirSlowAsync(gitdir, commonFile, cancellationToken));
    }

    /// <summary>Slow path of <see cref="ResolveCommonDirAsync"/>: reads the commondir file (linked worktrees).</summary>
    private static async Task<string> ResolveCommonDirSlowAsync(string gitdir, string commonFile, CancellationToken cancellationToken)
    {
        // C (repository.c:230-243, lookup_commondir): git_str_rtrim is ASCII trailing-whitespace ONLY — leading whitespace is preserved, so a
        // leading-space entry fails to resolve.
        ReadOnlySpan<char> content = AsciiText.Rtrim(await AsyncFileIO.ReadAllTextWithNoBomAsync(commonFile, cancellationToken).ConfigureAwait(false));
        string path;
        if (!Path.IsPathRooted(content))
        {
            path = Path.GetFullPath(Path.Join(gitdir, content));
        }
        else
        {
            path = content.ToString();
        }

        // C (git_fs_path_prettify → p_realpath, fs_path.c:387-401): the
        // resolved commondir must exist — "failed to resolve path '%s'"
        // (GIT_ENOTFOUND, GIT_ERROR_OS). A leading-space entry joins
        // into a nonexistent path and fails here.
        if (!PathHelpers.Exists(path))
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"failed to resolve path '{path}'",
                GitErrorCategory.Os);
        }

        return PathHelpers.PrettifyDir(path);
    }

    /// <summary>
    /// Validates a candidate gitdir and resolves its commondir. Matches
    /// <c>is_valid_repository_path</c> (repository.c:271-304) +
    /// <c>lookup_commondir</c>: HEAD must exist in the candidate, and
    /// <c>objects/</c> + <c>refs/</c> must exist in the COMMON dir (which a
    /// <c>commondir</c> file may redirect — linked worktrees).
    /// </summary>
    internal static async ValueTask<(bool Valid, string CommonDir)> IsValidRepositoryPathAsync(string repositoryPath, RepositoryOpenFlags flags, GitContext context, CancellationToken cancellationToken)
    {
        string commonDir = await ResolveCommonDirAsync(repositoryPath, flags, context, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(Path.Join(repositoryPath, "HEAD")))
        {
            return (false, commonDir);
        }

        if (!Directory.Exists(Path.Join(commonDir, "objects")))
        {
            return (false, commonDir);
        }

        if (!Directory.Exists(Path.Join(commonDir, "refs")))
        {
            return (false, commonDir);
        }

        return (true, commonDir);
    }

    /// <summary>
    /// Reads a <c>.git</c> gitfile and resolves the target gitdir. Matches
    /// <c>read_gitfile</c> (repository.c:519-552): case-sensitive
    /// <c>gitdir:</c> prefix, rtrimmed content, leading whitespace after the
    /// prefix skipped, relative targets resolved against the gitfile's
    /// directory. Malformed files raise an error (which aborts discovery).
    /// </summary>
    private static async Task<string> ReadGitfileAsync(string filePath, CancellationToken cancellationToken)
    {
        const string prefix = "gitdir:";
        string content = await AsyncFileIO.ReadAllTextWithNoBomAsync(filePath, cancellationToken).ConfigureAwait(false);

        // C: git_str_rtrim then git_fs_path_mkposix; the prefix compare is a
        // case-sensitive memcmp, and "gitdir:" alone is malformed.
        string trimmed = AsciiText.Rtrim(content).ToString().Replace('\\', '/');
        if (trimmed.Length <= prefix.Length ||
            !trimmed.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"the `.git` file at '{filePath}' is malformed",
                GitErrorCategory.Repository);
        }

        string gitlink = trimmed[prefix.Length..];
        int i = 0;
        while (i < gitlink.Length && IsAsciiSpace(gitlink[i]))
        {
            i++;
        }

        gitlink = gitlink[i..];

        string baseDir = PathHelpers.Dirname(filePath);
        string resolved = Path.IsPathRooted(gitlink)
            ? gitlink
            : PathHelpers.Join(baseDir, gitlink);
        return PathHelpers.PrettifyDir(resolved);
    }

    /// <summary>
    /// Reads the <c>gitdir</c> file inside a found gitdir — the linked
    /// worktree's gitlink path (the worktree root's <c>.git</c> file).
    /// Matches <c>git_worktree__read_link</c> (worktree.c:75-99): rtrimmed
    /// content; relative targets resolved against the gitdir; null when the
    /// file is absent or empty.
    /// </summary>
    private static ValueTask<string?> ReadWorktreeGitlinkAsync(string gitDir, CancellationToken cancellationToken)
    {
        string gitdirFilePath = PathHelpers.Join(gitDir, "gitdir");
        if (!File.Exists(gitdirFilePath))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return new ValueTask<string?>(ReadWorktreeGitlinkSlowAsync(gitDir, gitdirFilePath, cancellationToken));
    }

    /// <summary>Slow path of <see cref="ReadWorktreeGitlinkAsync"/>: reads the gitdir file (linked worktrees).</summary>
    private static async Task<string?> ReadWorktreeGitlinkSlowAsync(string gitDir, string gitdirFilePath, CancellationToken cancellationToken)
    {
        string content = await AsyncFileIO.ReadAllTextWithNoBomAsync(gitdirFilePath, cancellationToken).ConfigureAwait(false);
        string trimmed = AsciiText.Rtrim(content).ToString().Replace('\\', '/');
        if (trimmed.Length == 0)
        {
            return null;
        }

        return Path.IsPathRooted(trimmed)
            ? trimmed
            : PathHelpers.Join(gitDir, trimmed);
    }

    /// <summary>Matches C's <c>git__isspace</c> (ASCII whitespace only).</summary>
    private static bool IsAsciiSpace(char c)
        => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    /// <summary>
    /// Parses a boolean the way <c>git_config_parse_bool</c> does (config.c):
    /// the <c>git__parse_bool</c> word set (case-insensitive, util.c:647-668)
    /// first, then an int32 fallback (any nonzero is true). An unparseable
    /// value raises GIT_EINVALID with C's message — used for
    /// <c>GIT_DISCOVERY_ACROSS_FILESYSTEM</c>, where find_repo propagates the
    /// parse error instead of ignoring the variable (repository.c:951-961).
    /// Internal for the parity tests.
    /// </summary>
    internal static bool ParseBoolStrict(string? value)
    {
        if (value is null ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
            value.Length == 0)
        {
            return false;
        }

        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n))
        {
            return n != 0;
        }

        throw new GitException(
            GitErrorCode.Invalid,
            $"failed to parse '{value}' as a boolean",
            GitErrorCategory.Config);
    }

    /// <summary>
    /// Feeds <see cref="DeviceBoundaryStopsWalk"/> with the candidate's
    /// device, recording the baseline on the first existing candidate (C's
    /// <c>initial_device</c>). Returns true when the walk must stop.
    /// </summary>
    private static bool DeviceCheckStops(string candidate, ref uint? initialDevice, bool crossFs)
    {
        uint? device = DeviceOf(candidate);
        if (device is not null && initialDevice is null)
        {
            initialDevice = device;
            return false;
        }

        return DeviceBoundaryStopsWalk(initialDevice, device, crossFs);
    }

    /// <summary>
    /// The <c>st_dev</c> of <paramref name="path"/>, or null when the path
    /// does not exist (C's <c>p_stat</c> failure — no device data there
    /// either) or the device cannot be read.
    /// </summary>
    private static uint? DeviceOf(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return null;
        }

        return NativeStat.GetStat(new FileInfo(path)).Dev;
    }

    /// <summary>
    /// Returns the offset into <paramref name="path"/> where the longest
    /// ceiling-directory prefix ends. Matches <c>find_ceiling_dir_offset</c>
    /// (repository.c:470-512): non-rooted entries are skipped, each entry is
    /// realpathed (a nonexistent entry is skipped, symlinks are resolved) and
    /// must be a prefix of the path ending at a component boundary; the
    /// longest match wins.
    /// </summary>
    private static int FindCeilingDirOffset(string path, string? ceilingDirectories)
    {
        // C (repository.c:470-512): min_len = git_fs_path_root(path) + 1 —
        // the root component length (1 for "/", 3 for "C:\", 0 for a
        // relative path). Root() covers Windows drive paths too, so the
        // ceiling applies on every platform.
        int minLen = PathHelpers.Root(path).Length;
        if (ceilingDirectories is null || minLen == 0)
        {
            return minLen;
        }

        int maxLen = 0;
        foreach (string entry in ceilingDirectories.Split(Path.PathSeparator))
        {
            if (entry.Length == 0)
            {
                continue;
            }

            // C (repository.c:489-491): an entry that is not rooted
            // (git_fs_path_root == -1) is skipped.
            if (!PathHelpers.IsAbsolute(entry))
            {
                continue;
            }

            // C (repository.c:497-500): p_realpath — nonexistent entries are
            // skipped and symlinks are resolved before the prefix compare.
            string? real = NativeStat.TryRealpath(entry);
            if (real is null)
            {
                continue;
            }

            // Normalize to forward slashes: the walk path is prettified
            // (forward slashes) while Path.GetFullPath on Windows returns
            // backslashes — the prefix compare would never match.
            real = real.Replace('\\', '/');

            int len = real.Length;
            if (len > 0 && real[len - 1] == '/')
            {
                len--;
            }

            if (len > 0 &&
                path.Length >= len &&
                string.CompareOrdinal(path, 0, real, 0, len) == 0 &&
                (path.Length == len || path[len] == '/') &&
                len > maxLen)
            {
                maxLen = len;
            }
        }

        return maxLen <= minLen ? minLen : maxLen;
    }

    /// <summary>
    /// Opens the repository configuration: local and worktree levels followed
    /// by the global, XDG, system, and ProgramData levels. Missing files are
    /// silently skipped. Matches the backend ordering of libgit2's
    /// <c>load_config</c> (invoked via
    /// <c>git_repository_config__weakptr</c> / <c>git_repository_config_snapshot</c>).
    /// </summary>
    private static async Task<GitConfiguration> OpenRepoConfigAsync(string commondir, string gitdir, bool useEnv, GitContext context, CancellationToken cancellationToken)
    {
        var config = new GitConfiguration(context);

        // Add local config (commondir/config). C (repository.c:1279-1286)
        // adds the LOCAL backend unconditionally — config_file_open opens a
        // nonexistent file as an empty backend.
        string localConfigPath = Path.Join(commondir, "config");
        await config.AddFileOnDiskAsync(localConfigPath, GitConfigLevel.Local, gitdir, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Add worktree config if this is a worktree (gitdir/config.worktree). C (repository.c:1261-1277, 1305-1311): the extension is read UNCONDITIONALLY once
        // the local backend is attached, and a non-ENOTFOUND parse error (e.g. "banana") FAILS the whole open; a truthy value then adds the
        // worktree file (a missing file opens as an empty backend).
        string worktreeConfigPath = Path.Join(gitdir, "config.worktree");
        var localConfig = new GitConfiguration(context);
        await localConfig.AddFileOnDiskAsync(localConfigPath, GitConfigLevel.Local, gitdir, cancellationToken: cancellationToken).ConfigureAwait(false);
        GitConfigEntry? wtConfigEntry = await localConfig.GetEntryAsync("extensions.worktreeconfig", cancellationToken).ConfigureAwait(false);
        bool? worktreeConfig = await localConfig.TryGetConfigmapBoolAsync("extensions.worktreeconfig", false, cancellationToken).ConfigureAwait(false);
        await localConfig.DisposeAsync().ConfigureAwait(false);

        if (worktreeConfig is null)
        {
            string? value = wtConfigEntry?.Value ?? "(null)";
            throw new GitException(
                GitErrorCode.Error,
                $"failed to parse '{value}' as a boolean",
                GitErrorCategory.Config);
        }

        if (worktreeConfig.Value)
        {
            await config.AddFileOnDiskAsync(worktreeConfigPath, GitConfigLevel.Worktree, gitdir, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Add the global/XDG/system/ProgramData levels so the opened repo sees
        // the same merged view libgit2 produces in `load_config`. Unlike
        // git_config_open_default, the repo path has no missing-file fallback
        // for the GLOBAL level (config_path_global, repository.c:1392-1397).
        await config.AddDefaultLevelsAsync(globalFallbackToLocation: false, useEnv: useEnv, cancellationToken).ConfigureAwait(false);

        // C's load_config
        // calls git_config_set_writeorder(cfg, &write_order /* LOCAL */, 1)
        // (repository.c:1342-1344), so ONLY the LOCAL backend is writable —
        // the WORKTREE/GLOBAL/XDG/SYSTEM/PROGRAMDATA backends get
        // write_order -1 and writes always target commondir/config.
        config.SetWriteOrder(GitConfigLevel.Local);

        return config;
    }

    /// <summary>
    /// Reads config to determine bare status and object format. Matches
    /// <c>obtain_config_and_set_oid_type</c> + <c>check_repositoryformatversion</c>
    /// + <c>check_extensions</c> + <c>load_objectformat</c>.
    /// </summary>
    private static async ValueTask<(bool IsBare, GitHashAlgorithmKind ObjectFormat)> ObtainConfigAndSetOidTypeAsync(GitConfiguration config, CancellationToken cancellationToken)
    {
        // check_repositoryformatversion (repository.c:1836-1862): a negative
        // version sets an error message but opens; > 1 fails with -1; an
        // unparseable value fails git_config_get_int32 (config.c:1512-1513,
        // "failed to parse '%s' as a 32-bit integer") — NOT silently the
        // default.
        int version;
        GitConfigEntry? versionEntry = await config.GetEntryAsync("core.repositoryformatversion", cancellationToken).ConfigureAwait(false);
        if (versionEntry is null)
        {
            version = 0; // C: GIT_ENOTFOUND is ignored (repository.c:1841-1842)
        }
        else if (versionEntry.Value.ValueBytes is null
            || !ConfigurationValueParser.TryParseInt32(versionEntry.Value.ValueBytes.GetValueOrDefault().Span, out version))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to parse '{versionEntry.Value.Value ?? "(null)"}' as a 32-bit integer",
                GitErrorCategory.Config);
        }

        if (version > 1)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"unsupported repository version {version}; only versions up to 1 are supported",
                GitErrorCategory.Repository);
        }

        // check_extensions (repository.c:1864-1931): the builtin list plus
        // user-registered extensions; unknown names fail with -1.
        if (version > 0)
        {
            await foreach (GitConfigEntry entry in config.EnumerateAsync("extensions.*", cancellationToken).ConfigureAwait(false))
            {
                // byte-domain extraction — C's check_extensions (repository.c:1864-1931) strcmps the raw name bytes; the known-extension compares are ASCII
                // literals vs bytes.
                ReadOnlySpan<byte> name = entry.NameBytes.Span["extensions.".Length..];
                if (ConfigKeyName.AsciiEquals(name, "noop"u8)
                    || ConfigKeyName.AsciiEquals(name, "objectformat"u8)
                    || ConfigKeyName.AsciiEquals(name, "worktreeconfig"u8)
                    || ConfigKeyName.AsciiEquals(name, "preciousobjects"u8)
                    || ConfigKeyName.AsciiEquals(name, "relativeworktrees"u8))
                {
                    continue;
                }

                throw new GitException(
                    GitErrorCode.Error,
                    $"unsupported extension name {Encoding.UTF8.GetString(name)}",
                    GitErrorCategory.Repository);
            }
        }

        // Determine object format.
        GitHashAlgorithmKind objectFormat = GitHashAlgorithmKind.Sha1;
        if (version > 0)
        {
            string? of = await config.GetStringAsync("extensions.objectformat", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(of))
            {
                objectFormat = of switch
                {
                    "sha1" => GitHashAlgorithmKind.Sha1,
                    "sha256" => GitHashAlgorithmKind.Sha256,
                    _ => throw new GitException(
                        GitErrorCode.Invalid,
                        $"unknown object format '{of}'",
                        GitErrorCategory.Repository),
                };
            }
        }

        // Determine bare status. Matches <c>load_config_data</c> — the
        // !is_worktree factor is applied by the caller.
        bool isBare = await config.GetBoolAsync("core.bare", false, cancellationToken).ConfigureAwait(false);

        return (isBare, objectFormat);
    }

    /// <summary>
    /// Detects if this is a linked worktree (commondir != gitdir AND a gitdir
    /// file exists inside gitdir). Matches <c>repo_is_worktree</c>.
    /// </summary>
    private static bool DetectWorktree(RepoPaths paths)
    {
        if (paths.GitDir == paths.CommonDir)
        {
            return false;
        }

        // A worktree has a "gitdir" file inside its gitdir pointing back to the
        // worktree's working directory.
        string gitdirFile = Path.Join(paths.GitDir, "gitdir");
        return File.Exists(gitdirFile);
    }

    /// <summary>
    /// Resolves the working directory. Matches <c>load_workdir</c>
    /// (repository.c:423-460): GIT_WORK_TREE env (FROM_ENV only) → core.worktree
    /// config → worktree gitdir file → the found gitdir's parent directory
    /// (regardless of its name — C has no ".git-name" heuristic).
    /// </summary>
    private static async ValueTask<string?> LoadWorkdirAsync(RepoPaths paths, GitConfiguration config, GitContext context, bool useEnv, CancellationToken cancellationToken)
    {
        // C's load_workdir (repository.c:360-460) COLLECTS the GIT_WORK_TREE env
        // and core.worktree config into a single `value` (repository.c:395-410)
        // but dispatches the is_worktree branch (gitdir file) FIRST
        // (repository.c:413-428) — a linked worktree's gitdir file wins over
        // env/config.
        // core.worktree config (used by linked worktrees and gitlink
        // submodules). libgit2's load_workdir prettifies the value
        // relative to the gitdir (`git_fs_path_prettify_dir(&worktree,
        // value, repo->gitdir)`), so a relative core.worktree resolves
        // against the gitdir, not the process CWD.
        string? value = useEnv && !string.IsNullOrEmpty(context.Env["GIT_WORK_TREE"])
            ? context.Env["GIT_WORK_TREE"]
            : (await config.GetEntryAsync("core.worktree", cancellationToken).ConfigureAwait(false))?.Value;

        // 1. For linked worktrees, read the "gitdir" file inside the gitdir
        // (is_worktree branch, repository.c:413-428) — BEFORE env/config.
        if (paths.GitDir != paths.CommonDir)
        {
            string gitdirFile = Path.Join(paths.GitDir, "gitdir");
            if (File.Exists(gitdirFile))
            {
                // C (repository.c:413-428, git_worktree__read_link): the pointer file is ASCII-rtrimmed only — leading whitespace is preserved.
                ReadOnlySpan<char> content = AsciiText.Rtrim(await AsyncFileIO.ReadAllTextWithNoBomAsync(gitdirFile, cancellationToken).ConfigureAwait(false));
                // The gitdir file points to the .git file in the worktree root.
                // The worktree root is the parent of that .git file.
                string worktreeGitFile = content.ToString();
                if (File.Exists(worktreeGitFile))
                {
                    return PathHelpers.PrettifyDir(PathHelpers.Dirname(worktreeGitFile));
                }
            }
        }

        // 2. GIT_WORK_TREE env / core.worktree config (else-if value, repository.c:429-437). C (repository.c:423-434): a PRESENT core.worktree with an EMPTY
        // value fails the open with GIT_ERROR_NET "working directory cannot be set to empty path"; an empty-but-set value is not treated as
        // absent.
        if (value is not null)
        {
            if (value.Length == 0)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "working directory cannot be set to empty path",
                    GitErrorCategory.Net);
            }

            if (PathHelpers.IsAbsolute(value))
            {
                return PathHelpers.PrettifyDir(value);
            }

            return PathHelpers.PrettifyDir(PathHelpers.Join(paths.GitDir, value));
        }

        // 3. C (load_workdir's parent_path branch / find_repo_traverse): the
        // workdir is the parent of the FOUND gitdir, whatever its name.
        return PathHelpers.PrettifyDir(PathHelpers.Dirname(paths.GitDir));
    }

    /// <summary>
    /// Validates that the current user owns the workdir/gitlink/gitdir.
    /// Matches <c>validate_ownership</c> (repository.c:646-719):
    /// <c>validate_ownership_path</c> over each present path, then
    /// <c>validate_ownership_config</c> — <c>safe.directory</c> read from the
    /// GLOBAL config surface (system/global/XDG/ProgramData, never the local
    /// level). Failure is GIT_EOWNER (<see cref="GitErrorCode.Owner"/>) with
    /// C's exact message ("repository path '%.*s' is not owned by current
    /// user", GIT_ERROR_CONFIG category, path shown without its trailing
    /// slash). On Windows the check is skipped (C uses SID comparisons
    /// there — fs_path.c GIT_WIN32 branch — not ported; documented
    /// divergence).
    /// </summary>
    private static ValueTask ValidateOwnershipAsync(RepoPaths paths, string? workdir, GitContext context, bool useEnv, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return ValueTask.CompletedTask;
        }

        // C (repository.c:660-671): validation paths are [workdir, gitlink,
        // gitdir] — the first two only when present; ALL of them must be
        // owned, and the FIRST is the safe.directory lookup key.
        var validationPaths = new List<string>(3);
        if (workdir is not null)
        {
            validationPaths.Add(workdir);
        }

        if (paths.Gitlink is not null)
        {
            validationPaths.Add(paths.Gitlink);
        }

        validationPaths.Add(paths.GitDir);

        string? failingPath = null;
        bool isSafe = false;
        foreach (string path in validationPaths)
        {
            (bool safe, GitException? error) = ValidateOwnershipPath(path, context);
            if (error is not null)
            {
                throw error;
            }

            isSafe = safe;
            if (!isSafe)
            {
                failingPath = path;
                break;
            }
        }

        if (isSafe)
        {
            return ValueTask.CompletedTask;
        }

        // C (validate_ownership_config, repository.c:618-644): safe.directory
        // is read via load_global_config — the system/global/XDG/ProgramData
        // levels only; the LOCAL repository config is deliberately excluded.
        return new ValueTask(ValidateOwnershipSlowAsync(validationPaths, failingPath, context, useEnv, cancellationToken));
    }

    /// <summary>Slow path of <see cref="ValidateOwnershipAsync"/>: consults safe.directory (global config IO).</summary>
    private static async Task ValidateOwnershipSlowAsync(List<string> validationPaths, string? failingPath, GitContext context, bool useEnv, CancellationToken cancellationToken)
    {
        bool isSafe = await IsSafeDirectoryAsync(validationPaths[0], context, useEnv, cancellationToken).ConfigureAwait(false);

        if (!isSafe)
        {
            string path = failingPath ?? validationPaths[0];
            string display = path == "/" ? path : path.TrimEnd('/');
            throw new GitException(
                GitErrorCode.Owner,
                $"repository path '{display}' is not owned by current user",
                GitErrorCategory.Config);
        }
    }

    /// <summary>
    /// Checks one path against the mocked or real owner. Matches
    /// <c>validate_ownership_path</c> + <c>git_fs_path_owner_is</c>
    /// (repository.c:635-645, fs_path.c POSIX branch): geteuid vs lstat
    /// st_uid, with the SUDO_UID escape hatch for root. GIT_ENOTFOUND (path
    /// missing) is treated as safe; other stat failures are an OS error.
    /// </summary>
    private static (bool Safe, GitException? Error) ValidateOwnershipPath(string path, GitContext context)
    {
        // C (validate_ownership_path, repository.c:636-641): owner_level =
        // CURRENT_USER | USER_IS_ADMINISTRATOR | RUNNING_SUDO.
        const GitFsPathOwner ownerLevel = GitFsPathOwner.CurrentUser | GitFsPathOwner.UserIsAdministrator | GitFsPathOwner.RunningSudo;

        // git_fs_path__set_owner mock (fs_path.c:1935-1938) — used by the
        // parity tests to exercise the GIT_EOWNER path without chown.
        GitFsPathOwner mock = context.MockOwner;
        if (mock != GitFsPathOwner.None)
        {
            return ((mock & ownerLevel) != 0, null);
        }

        uint euid = NativeStat.GetEuid();
        NativeStat.StatResult st = NativeStat.GetStat(new FileInfo(path));
        if (!st.Valid || st.Uid is null)
        {
            // p_lstat failed (fs_path.c:1943-1952): ENOENT → GIT_ENOTFOUND →
            // safe; any other failure is an OS error.
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return (true, null);
            }

            return (false, new GitException(GitErrorCode.Error, $"could not stat '{path}'", GitErrorCategory.Os));
        }

        uint uid = st.Uid.Value;
        if (uid == euid)
        {
            return (true, null); // GIT_FS_PATH_OWNER_CURRENT_USER
        }

        if (euid == 0 && TryGetSudoUid(context, out uint sudoUid) && uid == sudoUid)
        {
            return (true, null); // GIT_FS_PATH_OWNER_RUNNING_SUDO
        }

        return (false, null);
    }

    /// <summary>
    /// Reads <c>$SUDO_UID</c> as a uid_t. Matches <c>sudo_uid_lookup</c>
    /// (fs_path.c:2006-2017): the value must parse as a non-negative uid.
    /// </summary>
    private static bool TryGetSudoUid(GitContext context, out uint sudoUid)
    {
        string? value = context.Env["SUDO_UID"];
        if (value is null ||
            !long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed) ||
            parsed < 0 ||
            parsed > uint.MaxValue)
        {
            sudoUid = 0;
            return false;
        }

        sudoUid = (uint)parsed;
        return true;
    }

    /// <summary>
    /// Reads <c>safe.directory</c> from the global config surface and checks
    /// whether <paramref name="lookupPath"/> (the first validation path —
    /// workdir, else gitlink, else gitdir) is allowlisted. Matches
    /// <c>validate_ownership_config</c> + <c>validate_ownership_cb</c>
    /// (repository.c:560-644): "*" or an exact path match is safe (a
    /// non-root value is compared with a trailing slash appended, so a value
    /// already ending in '/' is skipped); an empty value is explicitly
    /// unsafe; the last relevant entry wins.
    /// </summary>
    private static async Task<bool> IsSafeDirectoryAsync(string lookupPath, GitContext context, bool useEnv, CancellationToken cancellationToken)
    {
        // C (load_global_config, repository.c:2156-2178): system, global,
        // XDG, and ProgramData levels only — never the local config.
        var globalConfig = new GitConfiguration(context);
        IReadOnlyList<string> safeDirs;
        try
        {
            await globalConfig.AddDefaultLevelsAsync(globalFallbackToLocation: false, useEnv: useEnv, cancellationToken).ConfigureAwait(false);
            safeDirs = await globalConfig.GetMultiAsync("safe.directory", cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            // C: git_config_get_multivar_foreach returns GIT_ENOTFOUND for an
            // unset key — "no safe directories".
            safeDirs = [];
        }
        finally
        {
            await globalConfig.DisposeAsync().ConfigureAwait(false);
        }

        bool isSafe = false;
        foreach (string value in safeDirs)
        {
            // validate_ownership_cb (repository.c:560-618).
            if (value.Length == 0)
            {
                isSafe = false;
            }
            else if (value == "*")
            {
                isSafe = true;
            }
            else
            {
                string testPath = value;
                if (testPath.StartsWith("%(prefix)//", StringComparison.Ordinal))
                {
                    testPath = testPath["%(prefix)/".Length..];
                }

                if (testPath != "/")
                {
                    // "Input must not have trailing backslash": a value that
                    // already ends in '/' is skipped entirely (no match).
                    if (testPath.EndsWith('/', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    testPath = PathHelpers.ToDir(testPath);
                }

                if (string.Equals(testPath, lookupPath, StringComparison.Ordinal))
                {
                    isSafe = true;
                }
            }
        }

        return isSafe;
    }
}

/// <summary>Discovered repository paths.</summary>
internal sealed record RepoPaths(string GitDir, string CommonDir, string? Gitlink);
