using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

/// <summary>
/// Regression tests for the repository parity behaviors
/// in libgit2 1.9.4. Expected behaviors
/// were verified against the C reference (libgit2 1.9.4, repository.c,
/// odb.c, fs_path.c).
/// </summary>
public sealed class RepositoryMedParityTests2 : IDisposable
{
    private readonly string _tempDir;

    public RepositoryMedParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepoMed2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string NewDir(string name)
    {
        string path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static async ValueTask<GitRepository> InitAsync(string path, GitContext ctx)
        => await GitRepository.InitAsync(path, isBare: false, ctx);

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async ValueTask<GitOid> WriteCommitAsync(GitRepository repo, GitOid[] parents, string message)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = message,
            Parents = parents,
            UpdateRef = null,
        });
    }

    // ── ceiling directories — prefix+realpath semantics, CROSS_FS ──

    [Fact]
    public async Task Discover_CeilingBelowRepo_NotFound()
    {
        using GitContext ctx = new();
        string repo = NewDir("discover-ceiling");
        _ = await InitAsync(repo, ctx);
        string deep = Path.Combine(repo, "sub", "deep");
        Directory.CreateDirectory(deep);

        // C (repository.c:842-848): the ceiling offset is computed at the
        // parent of the start path; a ceiling below the repo stops the walk
        // before the repo's .git is ever checked.
        string ceiling = Path.Combine(repo, "sub");
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await GitRepository.DiscoverAsync(deep, acrossFs: false, ceilingDirs: ceiling, ctx, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Discover_StartEqualsCeiling_WalksPastCeiling()
    {
        using GitContext ctx = new();
        string repo = NewDir("discover-ceiling-eq");
        _ = await InitAsync(repo, ctx);

        // C: with start == ceiling the offset is computed at the PARENT, so
        // the ceiling never matches and start/.git is still found.
        string gitDir = await GitRepository.DiscoverAsync(repo, acrossFs: false, ceilingDirs: repo, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repo, ".git")), gitDir);
    }

    [Fact]
    public async Task Discover_SymlinkedCeiling_StopsBeforeRepo()
    {
        // Directory symlinks require privileges/developer mode on Windows;
        // the realpath mechanism is exercised on POSIX.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using GitContext ctx = new();
        string repo = NewDir("discover-symlink");
        _ = await InitAsync(repo, ctx);
        string deep = Path.Combine(repo, "sub", "deep");
        Directory.CreateDirectory(deep);

        // C realpaths each ceiling entry (p_realpath, repository.c:497-500):
        // a symlinked ceiling that resolves to the repo's parent stops the
        // walk before repo/.git is checked (GetFullPath without symlink
        // resolution would let the walk continue and find the repo).
        string alias = Path.Combine(_tempDir, "discover-symlink-alias");
        Directory.CreateSymbolicLink(alias, repo);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await GitRepository.DiscoverAsync(deep, acrossFs: false, ceilingDirs: alias, ctx, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Discover_NonexistentCeiling_IsSkipped()
    {
        using GitContext ctx = new();
        string repo = NewDir("discover-missing");
        _ = await InitAsync(repo, ctx);
        string deep = Path.Combine(repo, "sub", "deep");
        Directory.CreateDirectory(deep);

        // C: p_realpath fails on a nonexistent entry → the entry is skipped
        // (repository.c:499) — the walk is unaffected.
        string missing = Path.Combine(_tempDir, "discover-missing-absent");
        string gitDir = await GitRepository.DiscoverAsync(deep, acrossFs: false, ceilingDirs: missing, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repo, ".git")), gitDir);
    }

    [Fact]
    public async Task InvalidAcrossFsEnv_AbortsWithCBoolError()
    {
        using GitContext ctx = new();
        string start = NewDir("env-across-fs");
        ctx.Env["GIT_DISCOVERY_ACROSS_FILESYSTEM"] = "garbage";

        // C (find_repo, repository.c:951-961): git_config_parse_bool on the
        // env value fails → find_repo returns GIT_EINVALID and the open
        // aborts. Garbage must NOT be silently treated as false.
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await GitRepository.OpenExtAsync(start, RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Equal("failed to parse 'garbage' as a boolean", ex.Message);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
    }

    [Fact]
    public void AcrossFsBoolParse_MatchesC()
    {
        // git_config_parse_bool (config.c): git__parse_bool's word set is
        // case-insensitive; an int32 fallback treats any nonzero value as
        // true; anything else is GIT_EINVALID "failed to parse '%s' as a
        // boolean". The word set is case-insensitive, an int32 fallback treats
        // any nonzero value as true, and a fixed set of
        // lowercase/TitleCase words is NOT enough — "2"/"TRUE"/garbage must
        // be handled like C.
        Assert.True(RepositoryOpener.ParseBoolStrict("true"));
        Assert.True(RepositoryOpener.ParseBoolStrict("TRUE"));
        Assert.True(RepositoryOpener.ParseBoolStrict("Yes"));
        Assert.True(RepositoryOpener.ParseBoolStrict("on"));
        Assert.True(RepositoryOpener.ParseBoolStrict("1"));
        Assert.True(RepositoryOpener.ParseBoolStrict("2"));   // int32 → !!2
        Assert.True(RepositoryOpener.ParseBoolStrict("-1"));  // int32 → !!-1
        Assert.False(RepositoryOpener.ParseBoolStrict("false"));
        Assert.False(RepositoryOpener.ParseBoolStrict("NO"));
        Assert.False(RepositoryOpener.ParseBoolStrict("0"));
        Assert.False(RepositoryOpener.ParseBoolStrict(""));
        Assert.False(RepositoryOpener.ParseBoolStrict("off"));

        GitException ex = Assert.Throws<GitException>(() => RepositoryOpener.ParseBoolStrict("garbage"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Equal("failed to parse 'garbage' as a boolean", ex.Message);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
    }

    [Fact]
    public void DeviceBoundary_StopsWalkWithoutCrossFs()
    {
        // C (find_repo_traverse, repository.c:788-794): initial_device is the
        // first existing candidate's st_dev; a later candidate on another
        // device stops the walk unless GIT_REPOSITORY_OPEN_CROSS_FS.
        Assert.True(RepositoryOpener.DeviceBoundaryStopsWalk(1, 2, crossFs: false));
        Assert.False(RepositoryOpener.DeviceBoundaryStopsWalk(1, 2, crossFs: true));
        Assert.False(RepositoryOpener.DeviceBoundaryStopsWalk(1, 1, crossFs: false));
        Assert.False(RepositoryOpener.DeviceBoundaryStopsWalk(null, 2, crossFs: false));
        Assert.False(RepositoryOpener.DeviceBoundaryStopsWalk(1, null, crossFs: false));
    }

    [Fact]
    public void DeviceIds_ReadableFromNativeStat()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The mechanism feeding DeviceBoundaryStopsWalk must produce real
        // st_dev values. /dev/shm is a tmpfs with a distinct device from the
        // root filesystem in this environment.
        uint? devTmp = NativeStat.GetStat(new DirectoryInfo(Path.GetTempPath())).Dev;
        Assert.NotNull(devTmp);

        if (Directory.Exists("/dev/shm"))
        {
            uint? devShm = NativeStat.GetStat(new DirectoryInfo("/dev/shm")).Dev;
            Assert.NotNull(devShm);
            Assert.NotEqual(devTmp, devShm);
        }
    }

    // ── ODB wiring — missing objects dir / GIT_OBJECT_DIRECTORY ──

    [Fact]
    public async Task GitObjectDirectoryMissing_OpenFailsWithOdbError()
    {
        // C (odb.c:695-705): the missing-objects-dir failure is POSIX-only —
        // on Windows the inode check is skipped and the backends are added
        // regardless (GIT_WIN32 branch, odb.c:697-704).
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using GitContext ctx = new();
        string repo = NewDir("gitobjdir-missing");
        _ = await InitAsync(repo, ctx);
        string missing = Path.Combine(_tempDir, "gitobjdir-missing-absent");
        ctx.Env["GIT_OBJECT_DIRECTORY"] = missing;

        // C (repository_odb_path, repository.c:1485-1497 + odb.c:695-705):
        // GIT_OBJECT_DIRECTORY (FROM_ENV) replaces <commondir>/objects; the
        // ODB creation then fails with GIT_ERROR_ODB "failed to load object
        // database in '%s'".
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Error, ex.Code);
        // PrettifyDir is realpath now and would throw for the missing
        // dir — the ODB error message carries the lexically-normalized path.
        Assert.Equal($"failed to load object database in '{PathHelpers.PrettifyLexicalDir(missing)}'", ex.Message);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
    }

    [Fact]
    public async Task GitObjectDirectory_RedirectsObjectReads()
    {
        using GitContext ctx = new();
        string repo = NewDir("gitobjdir-redirect");
        _ = await InitAsync(repo, ctx);
        await using GitRepository writer = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken);
        GitOid commit = await WriteCommitAsync(writer, [], "c1\n").ConfigureAwait(false);
        Commit parsed = (await writer.ObjectLookupAsync<Commit>(commit, TestContext.Current.CancellationToken))!;

        // Point GIT_OBJECT_DIRECTORY at a second repo whose objects dir
        // holds the commit: opening repo A with the redirect must read
        // A's refs but B's objects.
        string alt = NewDir("gitobjdir-redirect-alt");
        _ = await InitAsync(alt, ctx);
        await using (GitRepository altRepo = await GitRepository.OpenAsync(Path.Combine(alt, ".git"), ctx, TestContext.Current.CancellationToken))
        {
            _ = await altRepo.ObjectWriteAsync(GitObjectType.Commit, parsed.Raw, TestContext.Current.CancellationToken);
        }

        ctx.Env["GIT_OBJECT_DIRECTORY"] = Path.Combine(alt, ".git", "objects");
        await using GitRepository redirected = await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.True(await redirected.Objects.ExistsAsync(commit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingObjectsDir_FailsDiscovery()
    {
        using GitContext ctx = new();
        string repo = NewDir("missing-objects-dir");
        _ = await InitAsync(repo, ctx);
        Directory.Delete(Path.Combine(repo, ".git", "objects"), recursive: true);

        // C (is_valid_repository_path, repository.c:271-304): a candidate
        // without <commondir>/objects is not a repository — the open fails
        // with GIT_ENOTFOUND before any ODB is constructed.
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── load_grafts at open (info/grafts + shallow) ──

    [Fact]
    public async Task GraftsLoadedAtOpen_CommitParentsReplaced()
    {
        using GitContext ctx = new();
        string repo = NewDir("grafts-open");
        _ = await InitAsync(repo, ctx);
        await using GitRepository writer = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken);
        GitOid parent = await WriteCommitAsync(writer, [], "parent\n").ConfigureAwait(false);
        GitOid child = await WriteCommitAsync(writer, [parent], "child\n").ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);

        // info/grafts rewrites the child to a root commit (no parents).
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "info", "grafts"), $"{child}\n", TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken);
        Assert.NotNull(opened.Grafts);
        Assert.Equal(1, opened.Grafts!.Count);

        Commit parsed = (await opened.ObjectLookupAsync<Commit>(child, TestContext.Current.CancellationToken))!;
        Assert.Empty(parsed.Parents);
    }

    [Fact]
    public async Task ShallowLoadedAtOpen_CommitBecomesRoot()
    {
        using GitContext ctx = new();
        string repo = NewDir("shallow-open");
        _ = await InitAsync(repo, ctx);
        await using GitRepository writer = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken);
        GitOid parent = await WriteCommitAsync(writer, [], "parent\n").ConfigureAwait(false);
        GitOid child = await WriteCommitAsync(writer, [parent], "child\n").ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);

        // <gitdir>/shallow marks the child as a shallow boundary (no parents
        // visible). C: load_grafts (repository.c:877-918) reads it at open.
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "shallow"), $"{child}\n", TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken);
        Assert.NotNull(opened.ShallowGrafts);
        Assert.Equal(1, opened.ShallowGrafts!.Count);

        Commit parsed = (await opened.ObjectLookupAsync<Commit>(child, TestContext.Current.CancellationToken))!;
        Assert.Empty(parsed.Parents);
    }

    [Fact]
    public async Task MalformedGrafts_OpenFails()
    {
        using GitContext ctx = new();
        string repo = NewDir("grafts-malformed");
        _ = await InitAsync(repo, ctx);
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "info", "grafts"), "not-an-oid\n", TestContext.Current.CancellationToken);

        // C: load_grafts error propagates out of git_repository_open_ext
        // (repository.c:1153) — a malformed grafts file fails the open.
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        Assert.Equal(GitErrorCategory.Grafts, ex.Category);
    }

    [Fact]
    public async Task NoGrafts_OpensWithEmptySets()
    {
        using GitContext ctx = new();
        string repo = NewDir("grafts-none");
        _ = await InitAsync(repo, ctx);

        await using GitRepository opened = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken);

        Assert.NotNull(opened.Grafts);
        Assert.Equal(0, opened.Grafts!.Count);
        Assert.NotNull(opened.ShallowGrafts);
        Assert.Equal(0, opened.ShallowGrafts!.Count);
    }

    // ── ownership validation (uid check + global safe.directory) ──

    [Fact]
    public async Task OwnerNotCurrentUser_OpenFailsWithOwnerError()
    {
        // Ownership validation is POSIX-only in C (fs_path.c GIT_WIN32 branch
        // uses ACLs; the port documents the Windows branch as not ported).
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using GitContext ctx = new();
        ctx.Env["GIT_CONFIG_NOSYSTEM"] = "1";
        ctx.Dirs.Set(GitSystemDir.Xdg, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        string repo = NewDir("owner-not-current");
        _ = await InitAsync(repo, ctx);
        // Isolate from the real global config; mock the owner to "another
        // user" (git_fs_path__set_owner(GIT_FS_PATH_OWNER_OTHER), fs_path.c).
        string emptyGlobal = Path.Combine(_tempDir, "owner-not-current-global");
        await File.WriteAllTextAsync(emptyGlobal, "", TestContext.Current.CancellationToken);
        ctx.Env["GIT_CONFIG_GLOBAL"] = emptyGlobal;
        ctx.MockOwner = GitFsPathOwner.Other;

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Owner, ex.Code);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
        Assert.Equal($"repository path '{PathHelpers.PrettifyDir(repo).TrimEnd('/')}' is not owned by current user", ex.Message);
    }

    [Fact]
    public async Task SafeDirectory_GlobalConfig_Exempts()
    {
        using GitContext ctx = new();
        ctx.Env["GIT_CONFIG_NOSYSTEM"] = "1";
        ctx.Dirs.Set(GitSystemDir.Xdg, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        string repo = NewDir("safe-dir-global");
        _ = await InitAsync(repo, ctx);
        string global = Path.Combine(_tempDir, "safe-dir-global-cfg");
        await File.WriteAllTextAsync(global, $"[safe]\n\tdirectory = {PathHelpers.PrettifyDir(repo).TrimEnd('/')}\n", TestContext.Current.CancellationToken);
        ctx.Env["GIT_CONFIG_GLOBAL"] = global;
        ctx.MockOwner = GitFsPathOwner.Other;

        // C (validate_ownership_config, repository.c:618-644): safe.directory
        // comes from the GLOBAL config (system/global/XDG/ProgramData) and
        // matches the workdir (the first validation path).
        await using GitRepository opened = await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.NotNull(opened);
    }

    [Fact]
    public async Task SafeDirectory_LocalConfig_DoesNotExempt()
    {
        // Ownership validation is POSIX-only in C (fs_path.c GIT_WIN32 branch
        // uses ACLs; the port documents the Windows branch as not ported).
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using GitContext ctx = new();
        ctx.Env["GIT_CONFIG_NOSYSTEM"] = "1";
        ctx.Dirs.Set(GitSystemDir.Xdg, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        string repo = NewDir("safe-dir-local");
        _ = await InitAsync(repo, ctx);
        // safe.directory in the REPO's own config must NOT exempt — C reads
        // only the global surface (repository.c:618-644), never the local
        // level.
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), $"\n[safe]\n\tdirectory = {PathHelpers.PrettifyDir(repo).TrimEnd('/')}\n", TestContext.Current.CancellationToken);
        string emptyGlobal = Path.Combine(_tempDir, "safe-dir-local-cfg");
        await File.WriteAllTextAsync(emptyGlobal, "", TestContext.Current.CancellationToken);
        ctx.Env["GIT_CONFIG_GLOBAL"] = emptyGlobal;
        ctx.MockOwner = GitFsPathOwner.Other;

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Owner, ex.Code);
    }

    [Fact]
    public async Task SafeDirectory_Star_Exempts()
    {
        using GitContext ctx = new();
        ctx.Env["GIT_CONFIG_NOSYSTEM"] = "1";
        ctx.Dirs.Set(GitSystemDir.Xdg, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        string repo = NewDir("safe-dir-star");
        _ = await InitAsync(repo, ctx);
        string global = Path.Combine(_tempDir, "safe-dir-star-cfg");
        await File.WriteAllTextAsync(global, "[safe]\n\tdirectory = *\n", TestContext.Current.CancellationToken);
        ctx.Env["GIT_CONFIG_GLOBAL"] = global;
        ctx.MockOwner = GitFsPathOwner.Other;

        await using GitRepository opened = await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.NotNull(opened);
    }

    [Fact]
    public async Task OwnedByCurrentUser_Opens()
    {
        using GitContext ctx = new();
        string repo = NewDir("owner-current");
        _ = await InitAsync(repo, ctx);

        // Real uid check (st_uid == geteuid): the repo is owned by the test
        // user, so the open succeeds without any safe.directory entry.
        await using GitRepository opened = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken);

        Assert.NotNull(opened);
    }

    // ── init directory creation — modes/umask, MKDIR, error path ──

    [Fact]
    public async Task SharedGroup_GitdirGetsSetgidExactMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using GitContext ctx = new();
        string repo = NewDir("setgid-gitdir");

        _ = await GitRepository.InitExtAsync(repo, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath,
            Mode = GitInitMode.SharedGroup,
        }, ctx, TestContext.Current.CancellationToken);

        // C (repo_init_directories, repository.c:2772-2780): dirmode =
        // 0775|S_ISGID with GIT_MKDIR_CHMOD — the gitdir is chmod'd to the
        // exact 02775 regardless of umask (Directory.CreateDirectory alone
        // performs no mode handling).
        int gitDirMode = (int)new DirectoryInfo(Path.Combine(repo, ".git")).UnixFileMode;
        Assert.Equal(0x5FD, gitDirMode);
        Assert.NotEqual(0, gitDirMode & (int)UnixFileMode.SetGroup);
    }

    [Fact]
    public async Task SharedGroup_StructureDirsGetSetgidMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using GitContext ctx = new();
        string repo = NewDir("setgid-dirs");

        _ = await GitRepository.InitExtAsync(repo, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath,
            Mode = GitInitMode.SharedGroup,
        }, ctx, TestContext.Current.CancellationToken);

        // C (repo_init_structure, repository.c:2610-2615): with a shared mode
        // the template directories are created with GIT_MKDIR_CHMOD → exact
        // dirmode (02775) as well.
        foreach (string sub in new[] { "objects", "refs", "hooks", "info" })
        {
            Assert.Equal(0x5FD, (int)new DirectoryInfo(Path.Combine(repo, ".git", sub)).UnixFileMode);
        }
    }

    [Fact]
    public async Task SharedAll_GitdirMode_Exact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using GitContext ctx = new();
        string repo = NewDir("sharedall-gitdir");

        _ = await GitRepository.InitExtAsync(repo, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath,
            Mode = GitInitMode.SharedAll,
        }, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(0x5FF, (int)new DirectoryInfo(Path.Combine(repo, ".git")).UnixFileMode);
    }

    [Fact]
    public async Task SharedUmask_GitdirNoSetgid()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using GitContext ctx = new();
        string repo = NewDir("umask-gitdir");

        _ = await InitAsync(repo, ctx);

        int gitDirMode = (int)new DirectoryInfo(Path.Combine(repo, ".git")).UnixFileMode;
        Assert.Equal(0, gitDirMode & (int)UnixFileMode.SetGroup);
    }

    [Fact]
    public async Task NoDotgitDirNonBare_WithoutWorkdir_Throws()
    {
        using GitContext ctx = new();
        string path = NewDir("nodotgit-throws");

        // C (repository.c:2719-2722): a non-bare init whose path isn't a
        // '.git' directory and has no explicit workdir cannot pick one.
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await GitRepository.InitExtAsync(path, new GitRepositoryInitOptions
            {
                Flags = GitRepositoryInitFlags.NoDotgitDir | GitRepositoryInitFlags.Mkpath,
            }, ctx, TestContext.Current.CancellationToken)).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("cannot pick working directory for non-bare repository that isn't a '.git' directory", ex.Message);
        Assert.Equal(GitErrorCategory.Repository, ex.Category);
    }

    [Fact]
    public async Task Mkdir_CreatesGitdirAndWorkdir()
    {
        using GitContext ctx = new();
        string parent = NewDir("mkdir-gitdir");
        string gitDir = Path.Combine(parent, "gitdir");
        string workDir = Path.Combine(parent, "workdir");

        // C (repo_init_directories #1/#4): MKDIR creates both the gitdir and
        // the workdir (with the setgid/other-write bits stripped on the
        // workdir, repository.c:2740-2748).
        _ = await GitRepository.InitExtAsync(gitDir, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkdir | GitRepositoryInitFlags.NoDotgitDir,
            WorkdirPath = workDir,
        }, ctx, TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(gitDir));
        Assert.True(Directory.Exists(workDir));
        Assert.True(File.Exists(Path.Combine(workDir, ".git")));
    }

    [Fact]
    public async Task Mkpath_CreatesAncestors()
    {
        using GitContext ctx = new();
        string deep = Path.Combine(_tempDir, "mkpath-ancestors", "a", "b", "c");

        _ = await GitRepository.InitAsync(deep, isBare: false, ctx, TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Combine(deep, ".git")));
    }
}
