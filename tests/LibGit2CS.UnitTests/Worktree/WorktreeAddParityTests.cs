using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Worktree;

/// <summary>
/// Regression tests for the worktree-add parity behavior in
/// libgit2 1.9.4. C creates
/// the admin dir and the worktree dir with GIT_MKDIR_EXCL (worktree.c:355-372)
/// — rc=-4 "failed to make directory '%s': directory exists" when either
/// already exists (probe-verified).
/// </summary>
public sealed class WorktreeAddParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public WorktreeAddParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_WorktreeAddParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "f.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0)),
            Committer = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0)),
            Message = "c1\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Add_ExistingWorktreeDir_ThrowsExists()
    {
        string existing = Path.Combine(_tempDir, "existing");
        Directory.CreateDirectory(existing);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.WorktreeAddAsync("wt1", existing, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
    }

    [Fact]
    public async Task Add_ExistingAdminDir_ThrowsExists()
    {
        // .git/worktrees/wt2 already exists → C fails the EXCL mkdir.
        string admin = Path.Combine(_repo.Path, "worktrees", "wt2");
        Directory.CreateDirectory(admin);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.WorktreeAddAsync("wt2", Path.Combine(_tempDir, "wt2"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
    }

    [Fact]
    public async Task Add_Fresh_Succeeds()
    {
        // Worktree lives in LibGit2CS.Repository (Worktree.cs) — the local
        // namespace LibGit2CS.UnitTests.Worktree shadows it, so fully qualify.
        using LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("wt3", Path.Combine(_tempDir, "wt3"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "wt3")));
    }
}
