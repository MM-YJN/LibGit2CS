using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

// Regression coverage against libgit2 1.9.4.
// parity behaviors for Worktree:
//   TryCreateDirExcl used Directory.CreateDirectory (implicitly
//         creating all parents); C's git_futils_mkdir with GIT_MKDIR_EXCL is
//         single-level — a worktree name with '/' or a path with missing
//         parents fails (worktree.c:355-372).
//   IsPrunable consulted the cached IsLocked flag instead of re-reading
//         the locked file — an out-of-band lock could be pruned
//         (worktree.c:575-590).
public sealed class WorktreeMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public WorktreeMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_WorktreeMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature Sig() => new("t", "t@t", new GitTime(1700000000, 0));

    private async Task<GitRepository> InitRepoWithCommitAsync(string name)
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

    // ── Add Worktree Name With Slash Fails Like C ────────────────────

    [Fact]
    public async Task Add_WorktreeNameWithSlash_FailsLikeC()
    {
        // C's git_futils_mkdir(..., GIT_MKDIR_EXCL) is single-level — a
        // name containing '/' fails (ENOENT — the intermediate admin dir is
        // missing). The old Directory.CreateDirectory created the whole
        // .git/worktrees/feature/foo chain.
        await using GitRepository repo = await InitRepoWithCommitAsync("k11a");
        string wtPath = Path.Combine(_tempDir, "wt_a");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await LibGit2CS.Repository.Worktree.AddAsync(repo, "feature/foo", wtPath, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Worktree, ex.Category);
        Assert.False(Directory.Exists(Path.Combine(repo.CommonDir, "worktrees", "feature")));
    }

    [Fact]
    public async Task Add_WorktreePathWithMissingParents_FailsLikeC()
    {
        await using GitRepository repo = await InitRepoWithCommitAsync("k11b");
        string missingPath = Path.Combine(_tempDir, "missing_parents", "deeper");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await LibGit2CS.Repository.Worktree.AddAsync(repo, "wt-b", missingPath, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Worktree, ex.Category);
        Assert.False(Directory.Exists(missingPath));
    }

    // ── Is Prunable Out Of Band Lock Is Respected ────────────────────

    [Fact]
    public async Task IsPrunable_OutOfBandLock_IsRespected()
    {
        await using GitRepository repo = await InitRepoWithCommitAsync("k12");
        string wtPath = Path.Combine(_tempDir, "wt_c");
        LibGit2CS.Repository.Worktree wt = await LibGit2CS.Repository.Worktree.AddAsync(repo, "wt-c", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // Make the worktree invalid (missing workdir), then lock it
        // OUT-OF-BAND (e.g. `git worktree lock`): C's
        // git_worktree_is_prunable re-reads the locked file every call
        // (worktree.c:575-590) — the cached flag would have said "not
        // locked" and allowed the prune.
        Directory.Delete(wtPath, recursive: true);
        await File.WriteAllTextAsync(
            Path.Combine(repo.CommonDir, "worktrees", "wt-c", "locked"),
            "locked by the CLI\n",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(wt.IsPrunable(), "an out-of-band-locked worktree must not be prunable");
    }
}
