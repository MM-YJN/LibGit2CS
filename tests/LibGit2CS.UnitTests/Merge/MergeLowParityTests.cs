using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Merge;

/// <summary> Parity tests for the merge subsystem. </summary>
public sealed class MergeLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public MergeLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitRepository> InitRepoAsync()
        => await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());

    private static async Task CommitFileAsync(GitRepository repo, string name, string content, string updateRef, CancellationToken ct)
    {
        GitIndex idx = await repo.GetIndexAsync(cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, name), content, cancellationToken: ct);
        await idx.AddByPathAsync(name, cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: ct);

        GitReference? head = await repo.ReferenceResolveAsync(updateRef, cancellationToken: ct);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "commit " + name + "\n",
            UpdateRef = updateRef,
        }, ct);
    }

    // ── failed merge cleans ONLY the MERGE state files ──────────────

    [Fact]
    public async Task FailedMerge_CleansOnlyMergeStateFiles()
    {
        // C (merge.c:3165-3174): merge_state_cleanup removes only
        // MERGE_HEAD/MERGE_MODE/MERGE_MSG - never another operation's state
        // files (REVERT_HEAD, CHERRY_PICK_HEAD, BISECT_LOG, sequencer/...).
        await using GitRepository repo = await InitRepoAsync();
        await CommitFileAsync(repo, "file.txt", "base\n", "refs/heads/master", TestContext.Current.CancellationToken);

        // Side branch modifies the same file.
        await CommitFileAsync(repo, "file.txt", "side\n", "refs/heads/side", TestContext.Current.CancellationToken);

        // Local staged change on master that the merge would overwrite.
        GitIndex masterIdx = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), "local\n", cancellationToken: TestContext.Current.CancellationToken);
        await masterIdx.AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await masterIdx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Decoy state from OTHER operations that must survive the failed merge.
        string gitDir = repo.Path;
        await File.WriteAllTextAsync(Path.Combine(gitDir, "REVERT_HEAD"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(gitDir, "CHERRY_PICK_HEAD"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(gitDir, "BISECT_LOG"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(gitDir, "sequencer"));
        await File.WriteAllTextAsync(Path.Combine(gitDir, "sequencer", "todo"), "pick x\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitAnnotatedCommit side = await repo.AnnotatedCommitFromRevspecAsync("refs/heads/side", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => repo.MergeAsync([side], mergeOpts: null, checkoutOpts: null, TestContext.Current.CancellationToken));
        Assert.Contains("would be overwritten by merge", ex.Message);

        // The MERGE state written by git_merge__setup is cleaned up...
        Assert.False(File.Exists(Path.Combine(gitDir, "MERGE_HEAD")), "MERGE_HEAD should be removed");
        Assert.False(File.Exists(Path.Combine(gitDir, "MERGE_MODE")), "MERGE_MODE should be removed");
        Assert.False(File.Exists(Path.Combine(gitDir, "MERGE_MSG")), "MERGE_MSG should be removed");

        // ...but ORIG_HEAD (written by setup, never cleaned by C) and every
        // OTHER operation's state survive.
        Assert.True(File.Exists(Path.Combine(gitDir, "ORIG_HEAD")), "ORIG_HEAD should survive");
        Assert.True(File.Exists(Path.Combine(gitDir, "REVERT_HEAD")), "REVERT_HEAD must survive a failed merge");
        Assert.True(File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")), "CHERRY_PICK_HEAD must survive a failed merge");
        Assert.True(File.Exists(Path.Combine(gitDir, "BISECT_LOG")), "BISECT_LOG must survive a failed merge");
        Assert.True(File.Exists(Path.Combine(gitDir, "sequencer", "todo")), "sequencer/ must survive a failed merge");
    }

    // ── merge check on unborn HEAD propagates the head-tree error ────

    [Fact]
    public async Task MergeCheckResult_UnbornHead_ThrowsUnbornBranch()
    {
        // C (merge.c:3079-3083): git_repository_head_tree fails with
        // GIT_EUNBORNBRANCH on an unborn HEAD and git_merge__check_result
        // propagates it.
        await using GitRepository repo = await InitRepoAsync();
        using GitIndex idx = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => repo.MergeCheckResultAsync(idx, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.UnbornBranch, ex.Code);
        Assert.Contains("reference 'HEAD' not found", ex.Message);
    }

    // ── merge-analysis HEAD-lookup error uses the MERGE category ─────

    [Fact]
    public async Task MergeAnalyze_MissingHeadRef_ThrowsMergeCategory()
    {
        // C (merge.c:3316-3319): git_merge_analysis sets GIT_ERROR_MERGE
        // "failed to lookup HEAD reference" when the HEAD lookup fails; the
        await using GitRepository repo = await InitRepoAsync();
        await CommitFileAsync(repo, "file.txt", "one\n", "refs/heads/master", TestContext.Current.CancellationToken);

        // Remove the HEAD file so the reference lookup fails.
        File.Delete(Path.Combine(repo.Path, "HEAD"));

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => repo.MergeAnalyzeAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("failed to lookup HEAD reference", ex.Message);
        Assert.Equal(GitErrorCategory.Merge, ex.Category);
    }
}
