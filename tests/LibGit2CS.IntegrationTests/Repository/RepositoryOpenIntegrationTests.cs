using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Repository;

/// <summary>
/// Integration tests for the repository open/discover path
/// (<see cref="GitRepository.OpenExtAsync"/>,
/// <see cref="GitRepository.OpenBareAsync"/>,
/// <see cref="GitRepository.DiscoverAsync"/>) exercised end-to-end against
/// locally-initialized repos with various flag combinations, ceiling
/// directories, and per-context <c>GIT_DIR</c>/<c>GIT_WORK_TREE</c> env overrides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Every other integration test calls
/// <see cref="GitRepository.OpenAsync(string, GitContext, CancellationToken)"/>
/// with the exact repo path — the upward-walk discovery, ceiling-dir
/// termination, <c>NoSearch</c>/<c>Bare</c> flag matrix, bare-repo open,
/// <c>DiscoverAsync</c>, and the <c>GIT_DIR</c>/<c>GIT_WORK_TREE</c> env-var
/// branches of <c>RepositoryOpener</c> were entirely cold in the default
/// integration run. These tests build a fresh local repo, place it at known
/// paths, and exercise each branch.
/// </para>
/// <para>
/// <b>Env-var model.</b> <see cref="GitEnvironment"/> is a per-context
/// snapshot with a public indexer setter, so the env-var tests mutate
/// <c>ctx.Env[...]</c> directly rather than <c>Environment.SetEnvironmentVariable</c>
/// — no process-wide mutation, no parallel-test interference.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/repo/open.c</c>
/// (<c>test_repo_open__open_with_discover</c>,
/// <c>test_repo_open__open_with_no_search</c>,
/// <c>test_repo_open__open_bare</c>),
/// <c>tests/libgit2/repo/env.c</c> (<c>test_repo_env__open</c>,
/// <c>test_repo_env__work_tree</c> — via
/// <c>git_repository_open_ext</c> with <c>GIT_REPOSITORY_OPEN_FROM_ENV</c>),
/// and <c>tests/libgit2/repo/discover.c</c> (<c>test_repo_discover__*</c>),
/// adapted to build the sandbox from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class RepositoryOpenIntegrationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path under the system temp root.</summary>
    private static string NewDirPath(string tag)
        => Path.Combine(Path.GetTempPath(), "libgit2cs-open-" + tag + "-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Inits a non-bare repo at <paramref name="path"/> (creating the directory
    /// tree as needed). Caller disposes the returned repo and cleans up
    /// <paramref name="path"/> via <see cref="Cleanup"/>.
    /// </summary>
    private static async Task<GitRepository> InitNonBareAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(path);
        return await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
    }

    /// <summary>
    /// Inits a bare repo at <paramref name="path"/> and disposes the handle.
    /// The on-disk repo remains and can be re-opened.
    /// </summary>
    private static async Task InitBareAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(path);
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: true, new GitContext(), cancellationToken: ct);
    }

    // ── Upward-walk discovery ──────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.OpenExtAsync"/> with default flags (0)
    /// from a subdirectory walks upward and finds the enclosing <c>.git</c>.
    /// Exercises the <c>find_repo_traverse</c> upward-walk loop in
    /// <c>RepositoryOpener.FindRepoAsync</c> (the parent-walk branch).
    /// Mirrors <c>test_repo_open__open_with_discover</c> (open_ext with
    /// flags = 0). Note: <see cref="GitRepository.OpenAsync"/> maps to C's
    /// <c>git_repository_open</c>, which passes
    /// <c>GIT_REPOSITORY_OPEN_NO_SEARCH</c> and does NOT walk upward — the
    /// upward walk requires the flag-free <c>OpenExtAsync</c>.
    /// </summary>
    [Fact]
    public async Task Open_FromSubdirectory_WalksUpward_AndFindsRepo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = NewDirPath("ro1");
        await using (GitRepository init = await InitNonBareAsync(repoPath, ct))
        {
            await init.DisposeAsync();
        }

        // Create a nested subdirectory two levels deep.
        string nested = Path.Combine(repoPath, "sub", "deep");
        Directory.CreateDirectory(nested);
        try
        {
            await using GitRepository repo = await GitRepository.OpenExtAsync(nested, RepositoryOpenFlags.None, ceilingDirs: null, new GitContext(), cancellationToken: ct);
            Assert.False(repo.IsBare);
            // repo.Path is the .git directory (prettified, with trailing
            // separator — C: git_repository_path ends in "attr/.git/").
            Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repoPath, ".git")), repo.Path);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.OpenExtAsync"/> with
    /// <see cref="RepositoryOpenFlags.NoSearch"/> does NOT walk upward — opening
    /// from a subdirectory that has no <c>.git</c> throws
    /// <see cref="GitErrorCode.NotFound"/>. Exercises the <c>noSearch</c>
    /// break in <c>FindRepoAsync</c>. Mirrors <c>test_open__open_with_no_search</c>.
    /// </summary>
    [Fact]
    public async Task OpenExt_NoSearch_FromSubdir_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = NewDirPath("ro2");
        await using (GitRepository init = await InitNonBareAsync(repoPath, ct))
        {
            await init.DisposeAsync();
        }

        string nested = Path.Combine(repoPath, "sub");
        Directory.CreateDirectory(nested);
        try
        {
            GitException ex = await Assert.ThrowsAsync<GitException>(
                () => GitRepository.OpenExtAsync(nested, RepositoryOpenFlags.NoSearch, ceilingDirs: null, new GitContext(), ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── Ceiling directories ────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.OpenExtAsync"/> stops the upward walk at a
    /// ceiling directory and throws <see cref="GitErrorCode.NotFound"/> when no
    /// repo is found below it. Exercises the <c>ceilings.Contains(current)</c>
    /// termination branch in <c>FindRepoAsync</c>.
    /// </summary>
    /// <remarks>
    /// Setup: ceiling at <c>C</c>, repo above <c>C</c>, start inside <c>C</c>.
    /// The walk runs once at the start path, fails to find <c>.git</c>, walks
    /// up to <c>C</c>, fails to find <c>.git</c>, hits the ceiling and breaks.
    /// </remarks>
    [Fact]
    public async Task OpenExt_Ceiling_StopsWalkBelowRepo_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewDirPath("ro3-root");
        Directory.CreateDirectory(root);
        try
        {
            // Repo at root/repo, ceiling at root/below, start under root/below.
            string repoPath = Path.Combine(root, "repo");
            string ceiling = Path.Combine(root, "below");
            string start = Path.Combine(ceiling, "start");
            await using (GitRepository init = await InitNonBareAsync(repoPath, ct))
            {
                await init.DisposeAsync();
            }

            Directory.CreateDirectory(start);

            GitException ex = await Assert.ThrowsAsync<GitException>(
                () => GitRepository.OpenExtAsync(start, RepositoryOpenFlags.None, ceiling, new GitContext(), ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.OpenExtAsync"/> with a ceiling dir finds a
    /// repo that lives at or below the start path (ceiling only stops the
    /// upward walk — it does not block discovery of a repo above the start
    /// path's first iteration). Mirrors <c>test_open__finds_via_ceiling_dirs</c>.
    /// </summary>
    /// <remarks>
    /// Setup: start path is INSIDE the repo workdir, ceiling is an unrelated
    /// directory. The first iteration of the walk finds <c>.git</c> before any
    /// ceiling check, so the open succeeds and the ceiling is never consulted.
    /// </remarks>
    [Fact]
    public async Task OpenExt_WithCeiling_FindsRepoAtStartPath()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = NewDirPath("ro4");
        await using (GitRepository init = await InitNonBareAsync(repoPath, ct))
        {
            await init.DisposeAsync();
        }

        string nested = Path.Combine(repoPath, "sub");
        Directory.CreateDirectory(nested);
        // An unrelated ceiling dir that does not lie on the upward walk.
        string ceiling = Path.Combine(Path.GetTempPath(), "libgit2cs-open-ro4-unrelated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ceiling);
        try
        {
            await using GitRepository repo = await GitRepository.OpenExtAsync(
                nested, RepositoryOpenFlags.None, ceiling, new GitContext(), ct);
            Assert.False(repo.IsBare);
        }
        finally
        {
            Cleanup(repoPath);
            Cleanup(ceiling);
        }
    }

    // ── Bare-repo open ─────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.OpenBareAsync"/> opens a bare repo's gitdir
    /// directly with <see cref="RepositoryOpenFlags.Bare"/>|<see cref="RepositoryOpenFlags.NoSearch"/>,
    /// and the result reports <see cref="GitRepository.IsBare"/> = true with no
    /// workdir. Mirrors <c>test_open__open_bare</c>.
    /// </summary>
    [Fact]
    public async Task OpenBare_FromGitDir_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = NewDirPath("ro5");
        await InitBareAsync(repoPath, ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenBareAsync(repoPath, new GitContext(), cancellationToken: ct);
            Assert.True(repo.IsBare);
            Assert.Null(repo.Workdir);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── Per-context env vars ───────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.OpenExtAsync"/> with an empty path and
    /// <see cref="RepositoryOpenFlags.FromEnv"/> honors the per-context
    /// <c>GIT_DIR</c> env var, opening the repo at that gitdir. Exercises the
    /// <c>useEnv &amp;&amp; IsNullOrEmpty(startPath)</c> branch of
    /// <c>RepositoryOpener.FindRepoAsync</c> (repository.c:903-910 — C reads
    /// <c>GIT_DIR</c> only when <c>FROM_ENV</c> is set and the start path is
    /// NULL). No <c>Environment.SetEnvironmentVariable</c> — purely per-context.
    /// Mirrors <c>test_repo_env__open</c> (which uses
    /// <c>git_repository_open_ext(NULL, GIT_REPOSITORY_OPEN_FROM_ENV, NULL)</c>).
    /// </summary>
    [Fact]
    public async Task Open_WithGitDirEnv_FindsRepo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = NewDirPath("ro6");
        await using (GitRepository init = await InitNonBareAsync(repoPath, ct))
        {
            await init.DisposeAsync();
        }

        try
        {
            GitContext ctx = new();
            ctx.Env["GIT_DIR"] = Path.Combine(repoPath, ".git");
            await using GitRepository repo = await GitRepository.OpenExtAsync(string.Empty, RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, cancellationToken: ct);
            Assert.False(repo.IsBare);
            // repo.Path is the prettified gitdir (trailing separator, as in C).
            Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repoPath, ".git")), repo.Path);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.OpenExtAsync"/> with
    /// <see cref="RepositoryOpenFlags.FromEnv"/> honors both <c>GIT_DIR</c>
    /// (gitdir) and <c>GIT_WORK_TREE</c> (workdir) env vars simultaneously.
    /// Exercises the <c>GIT_WORK_TREE</c> take-first branch of
    /// <c>RepositoryOpener.LoadWorkdirAsync</c>. The discovered repo has
    /// <c>repo.Path</c> = the env gitdir and <c>repo.Workdir</c> = the env
    /// work-tree (with trailing separator), regardless of where the gitdir
    /// physically lives. Mirrors <c>test_repo_env__work_tree</c>.
    /// </summary>
    [Fact]
    public async Task Open_WithGitDirAndWorkTreeEnv_ResolvesWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = NewDirPath("ro7");
        await using (GitRepository init = await InitNonBareAsync(repoPath, ct))
        {
            await init.DisposeAsync();
        }

        // Place a "worktree" directory elsewhere and point GIT_WORK_TREE at it.
        string workTree = NewDirPath("ro7-wt");
        Directory.CreateDirectory(workTree);
        try
        {
            GitContext ctx = new();
            ctx.Env["GIT_DIR"] = Path.Combine(repoPath, ".git");
            ctx.Env["GIT_WORK_TREE"] = workTree;

            await using GitRepository repo = await GitRepository.OpenExtAsync(string.Empty, RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, cancellationToken: ct);
            Assert.False(repo.IsBare);
            Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repoPath, ".git")), repo.Path);
            Assert.Equal(PathHelpers.PrettifyDir(workTree), repo.Workdir);
        }
        finally
        {
            Cleanup(repoPath);
            Cleanup(workTree);
        }
    }

    // ── Discover ───────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.DiscoverAsync"/> on a path that itself
    /// contains a <c>.git</c> returns the gitdir path. Exercises
    /// <c>RepositoryOpener.DiscoverAsync</c> (which mirrors
    /// <c>git_repository_discover</c>: an upward walk with no
    /// <see cref="RepositoryOpenFlags.NoSearch"/>, but the first iteration at
    /// the start path finds the immediate <c>.git</c>). The returned gitdir
    /// is prettified with a trailing separator — C's
    /// <c>test_repo_discover__*</c> compare against
    /// <c>realpath + git_fs_path_to_dir</c>.
    /// </summary>
    [Fact]
    public async Task Discover_FromRepoPath_ReturnsGitDir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = NewDirPath("ro8");
        await using (GitRepository init = await InitNonBareAsync(repoPath, ct))
        {
            await init.DisposeAsync();
        }

        try
        {
            string discovered = await GitRepository.DiscoverAsync(repoPath, context: new GitContext(), cancellationToken: ct);
            Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repoPath, ".git")), discovered);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.DiscoverAsync"/> from a path with no repo
    /// on it throws <see cref="GitErrorCode.NotFound"/>. Exercises the
    /// not-found throw path of <c>FindRepoAsync</c> when discover's
    /// <see cref="RepositoryOpenFlags.NoSearch"/> mode misses.
    /// </summary>
    [Fact]
    public async Task Discover_FromNonRepoPath_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string nonRepo = NewDirPath("ro9");
        Directory.CreateDirectory(nonRepo);
        try
        {
            GitException ex = await Assert.ThrowsAsync<GitException>(
                () => GitRepository.DiscoverAsync(nonRepo, context: new GitContext(), cancellationToken: ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);
        }
        finally
        {
            Cleanup(nonRepo);
        }
    }
}
