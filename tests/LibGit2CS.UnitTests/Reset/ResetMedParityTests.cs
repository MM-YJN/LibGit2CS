using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitResetMode = LibGit2CS.Reset.GitResetMode;

namespace LibGit2CS.UnitTests.Reset;

/// <summary>
/// Parity regression tests for behaviors in
/// libgit2 1.9.4.
/// C reference: reset.c.
/// </summary>
public sealed class ResetMedParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public ResetMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ResetMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
        await _repo.Config.SetBoolAsync("core.autocrlf", false);
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

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> CommitFileAsync(string fileName, string content, GitOid? parent = null)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is not null ? [parent.Value] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Creates a commit whose tree contains <paramref name="files"/> without
    /// touching the workdir — used to build a target tree that has files the
    /// workdir does not.
    /// </summary>
    private async Task<GitOid> CommitTreeWithoutWorkdirAsync(GitOid parent, params (string Name, string Content)[] files)
    {
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        foreach ((string name, string content) in files)
        {
            GitOid blob = await _repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
            await bld.InsertAsync(name, blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        }

        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parent],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "tree commit\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    // ── soft reset rejected while a merge is in progress ────────────

    [Fact]
    public async Task Soft_WithMergeInProgress_ThrowsUnmerged()
    {
        // C (reset.c:138-145): GIT_RESET_SOFT is rejected when
        // git_repository_state(repo) == GIT_REPOSITORY_STATE_MERGE OR the
        // index has conflicts — GIT_EUNMERGED, GIT_ERROR_OBJECT,
        // "Cannot perform reset (soft) in the middle of a merge".
        GitOid c1 = await CommitFileAsync("a.txt", "a\n");
        _ = await CommitFileAsync("b.txt", "b\n", c1);

        // Simulate a merge in progress: MERGE_HEAD present, index conflict-free.
        string mergeHeadPath = Path.Combine(_repo.Path, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHeadPath, $"{c1}\n", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryState.Merge, _repo.State);

        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.False(idx.HasConflicts);

        Commit target = (await _repo.ObjectLookupAsync<Commit>(c1, TestContext.Current.CancellationToken))!;
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.ResetAsync(target, GitResetMode.Soft, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Unmerged, ex.Code);
        Assert.Contains("(soft) in the middle of a merge", ex.Message);
    }

    // ── hard reset REPLACES the caller's checkout strategy ──────────

    [Fact]
    public async Task Hard_ReplacesCallerCheckoutStrategyWithForce()
    {
        // C (reset.c:150-156): opts.checkout_strategy = GIT_CHECKOUT_FORCE —
        // the caller's strategy bits are discarded, not OR'd in. With a
        // caller strategy of UPDATE_ONLY, C still CREATES missing files
        // (pure FORCE).
        GitOid c1 = await CommitFileAsync("a.txt", "a\n");

        // Build commit2 whose tree has a.txt + b.txt, WITHOUT writing b.txt
        // to the workdir.
        GitOid c2 = await CommitTreeWithoutWorkdirAsync(c1, ("a.txt", "a\n"), ("b.txt", "b\n"));
        Assert.False(File.Exists(Path.Combine(_repo.Workdir!, "b.txt")));

        Commit target = (await _repo.ObjectLookupAsync<Commit>(c2, TestContext.Current.CancellationToken))!;
        await _repo.ResetAsync(target, GitResetMode.Hard,
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.UpdateOnly },
            cancellationToken: TestContext.Current.CancellationToken);

        // Pure FORCE creates the missing b.txt despite the caller's UPDATE_ONLY.
        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "b.txt")));
    }
}
