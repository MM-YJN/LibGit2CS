using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Worktree;

/// <summary>
/// End-to-end tests for the worktree-add parity behavior in
/// libgit2 1.9.4.
/// </summary>
public sealed class WorktreeAddParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public WorktreeAddParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_WorktreeAddParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task Add_ExistingDir_ThrowsExists_LikeC()
    {
        string path = Path.Combine(_tempDir, "r");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "f.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0)),
            Committer = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0)),
            Message = "c1\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        // Existing worktree dir → C: GIT_EEXISTS "failed to make directory".
        string existing = Path.Combine(_tempDir, "existing");
        Directory.CreateDirectory(existing);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.WorktreeAddAsync("wt1", existing, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
        Assert.Contains("failed to make directory", ex.Message);

        // Fresh add succeeds.
        using LibGit2CS.Repository.Worktree wt = await repo.WorktreeAddAsync("wt2", Path.Combine(_tempDir, "wt2"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "wt2")));
    }
}
