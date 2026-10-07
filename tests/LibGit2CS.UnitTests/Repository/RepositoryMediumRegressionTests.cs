using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Reset;

namespace LibGit2CS.UnitTests.Repository;

/// <summary>
/// Regression tests for the Repository parity behaviors:
/// (load_workdir must dispatch the worktree gitdir file before
/// env/config), (Prettify must realpath and fail on missing paths),
/// (WriteGitlinkAsync must overwrite a stale .git file),
/// (detached-HEAD reset reflog message), (SetHeadAsync must not
/// resolve symbolic non-branch refs).
/// </summary>
public sealed class RepositoryMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepositoryMedium_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature Sig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> InitRepoWithCommitAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig(),
            Committer = Sig(),
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        return repo;
    }

    // ── worktree gitdir file beats env/config in load_workdir ─────

    [Fact]
    public async Task Open_WorktreeWithCoreWorktreeInCommonConfig_UsesGitdirFile()
    {
        // C's load_workdir collects env/config into `value` but
        // dispatches the is_worktree branch FIRST (repository.c:413-428), so
        // a linked worktree's gitdir file wins over core.worktree even when
        // the SHARED config sets core.worktree to another path.
        await using GitRepository repo = await InitRepoWithCommitAsync("n02");
        string wtPath = Path.Combine(_tempDir, "n02-wt");
        await LibGit2CS.Repository.Worktree.AddAsync(repo, "wt", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // Stale core.worktree in the COMMON config (shared by the worktree).
        await repo.Config.SetStringAsync("core.worktree", Path.Combine(_tempDir, "n02-bogus"), TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenAsync(wtPath, new GitContext(), TestContext.Current.CancellationToken);
        Assert.Equal(PathHelpers.PrettifyDir(wtPath), opened.Workdir);
    }

    [Fact]
    public async Task Open_WorktreeWithGitWorkTreeEnv_UsesGitdirFile()
    {
        // Env leg: with GIT_WORK_TREE set (FROM_ENV), the worktree
        // gitdir file still wins.
        await using GitRepository repo = await InitRepoWithCommitAsync("n02b");
        string wtPath = Path.Combine(_tempDir, "n02b-wt");
        await LibGit2CS.Repository.Worktree.AddAsync(repo, "wtb", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        GitContext ctx = new();
        ctx.Env["GIT_WORK_TREE"] = Path.Combine(_tempDir, "n02b-bogus");

        await using GitRepository opened = await GitRepository.OpenExtAsync(
            Path.Combine(wtPath, ".git"),
            RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv,
            ceilingDirs: null,
            ctx,
            TestContext.Current.CancellationToken);
        Assert.Equal(PathHelpers.PrettifyDir(wtPath), opened.Workdir);
    }

    // ── Prettify realpath semantics ───────────────────────────────

    [Fact]
    public async Task Discover_StartPathThroughSymlink_FindsRepoAtTarget()
    {
        // C's git_fs_path_prettify is p_realpath — discovery from a
        // symlinked start path resolves the symlink and walks the TARGET's
        // chain, not the symlink's own parent chain.
        await using GitRepository repo = await InitRepoWithCommitAsync("n03a");
        string linkPath = Path.Combine(_tempDir, "n03a-link");
        Directory.CreateSymbolicLink(linkPath, repo.Workdir!);

        string found = await GitRepository.DiscoverAsync(linkPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repo.Workdir!, ".git")), found);
    }

    [Fact]
    public async Task Open_MissingCoreWorktree_FailsToResolvePath()
    {
        // a core.worktree pointing at a nonexistent directory fails
        // the open with "failed to resolve path" (p_realpath, fs_path.c:396-401).
        await using GitRepository repo = await InitRepoWithCommitAsync("n03b");
        string missing = Path.Combine(_tempDir, "n03b-missing");
        await repo.Config.SetStringAsync("core.worktree", missing, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitRepository.OpenExtAsync(
                Path.Combine(repo.Workdir!, ".git"),
                RepositoryOpenFlags.NoSearch,
                ceilingDirs: null,
                new GitContext(),
                TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("failed to resolve path", ex.Message);
    }

    // ── stale .git gitlink file is overwritten ───────────────────

    [Fact]
    public async Task SetWorkdir_UpdateGitlink_OverwritesStaleGitFile()
    {
        // C's repo_write_gitlink rewrites a stale regular .git file
        // (O_TRUNC, repository.c:2424-2443), so the worktree no longer points
        // at a previous repository.
        await using GitRepository repo = await InitRepoWithCommitAsync("n04");
        string newWorkdir = Path.Combine(_tempDir, "n04-wd");
        Directory.CreateDirectory(newWorkdir);
        string staleGitlink = Path.Combine(newWorkdir, ".git");
        await File.WriteAllTextAsync(staleGitlink, "gitdir: /some/old/gitdir\n", cancellationToken: TestContext.Current.CancellationToken);

        await repo.SetWorkdirAsync(newWorkdir, updateGitlink: true, cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(staleGitlink, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal($"gitdir: {PathHelpers.PrettifyDir(repo.Path)}\n", content);
    }

    // ── detached-HEAD reset reflog message ────────────────────────

    [Fact]
    public async Task Reset_DetachedHead_WritesResetReflogMessage()
    {
        // C's reset writes HEAD via git_reference__update_terminal
        // with the 'reset: moving to %s' message verbatim (reset.c:146-171).
        await using GitRepository repo = await InitRepoWithCommitAsync("n05");
        GitOid first = ((GitDirectReference)(await repo.ReferenceLookupAsync("refs/heads/main", cancellationToken: TestContext.Current.CancellationToken))!).Target;
        await repo.SetHeadDetachedAsync(first, TestContext.Current.CancellationToken);

        // Second commit (reset target).
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "g.txt"), "two\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync("g.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitOid second = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [first],
            Author = Sig(),
            Committer = Sig(),
            Message = "two\n",
        }, TestContext.Current.CancellationToken);

        Commit target = (await repo.ObjectLookupAsync<Commit>(second, TestContext.Current.CancellationToken))!;
        await repo.ResetAsync(target, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken);

        GitRefLog log = (await repo.ReferenceReadLogAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.StartsWith($"reset: moving to {second}", log[0].Message);
    }

    // ── SetHeadAsync must not resolve symbolic non-branch refs ────

    [Fact]
    public async Task SetHead_SymbolicNonBranchRef_ThrowsInvalidArgument()
    {
        // C's git_repository_set_head passes git_reference_target(ref)
        // to detach(); for a symbolic (non-direct) ref that returns NULL and
        // GIT_ASSERT_ARG(id) fails with GIT_ERROR_INVALID "invalid argument"
        // (refs.c:340-346, repository.c:3558) — C never resolves and
        // detaches at the resolved OID.
        await using GitRepository repo = await InitRepoWithCommitAsync("n06");
        GitOid main = ((GitDirectReference)(await repo.ReferenceLookupAsync("refs/heads/main", cancellationToken: TestContext.Current.CancellationToken))!).Target;
        await repo.ReferenceCreateAsync("refs/remotes/origin/main", main, force: false, logMessage: "setup", cancellationToken: TestContext.Current.CancellationToken);
        await repo.ReferenceCreateSymbolicAsync("refs/remotes/origin/HEAD", "refs/remotes/origin/main", force: false, logMessage: "setup", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.SetHeadAsync("refs/remotes/origin/HEAD", TestContext.Current.CancellationToken));

        Assert.Equal("invalid argument", ex.Message);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);

        // HEAD is untouched — still symbolic at refs/heads/main.
        GitReference head = (await repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.IsType<GitSymbolicReference>(head);
        Assert.Equal("refs/heads/main", ((GitSymbolicReference)head).TargetName);
    }
}
