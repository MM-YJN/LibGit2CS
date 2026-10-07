using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Merge;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Merge;

/// <summary>
/// Workdir merge tests via <see cref="GitRepository.MergeAsync"/>. Ported from
/// <c>tests/libgit2/merge/workdir/{simple,trivial,recursive}.c</c>.
/// Uses the <c>merge-resolve</c> and <c>merge-recursive</c> fixtures.
/// </summary>
public sealed class MergeWorkdirTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    // branch = 7cb63eed597130ba4abb87b3e544b85021905520
    private static readonly GitOid s_branch = GitOid.Parse("7cb63eed597130ba4abb87b3e544b85021905520".AsSpan(), GitHashAlgorithmKind.Sha1);

    public MergeWorkdirTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeWorkdir_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private async Task<GitRepository> OpenMergeResolveRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/merge-resolve.zip");
        _extractedPaths.Add(path);
        GitRepository repo = await GitRepository.OpenAsync(Path.Combine(path, "merge-resolve"), new GitContext());
        await repo.Config.SetStringAsync("merge.conflictstyle", "merge");
        await repo.Config.SetBoolAsync("core.autocrlf", false);
        return repo;
    }

    private async Task<GitRepository> OpenMergeRecursiveRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/merge-recursive.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "merge-recursive"), new GitContext());
    }

    // ── simple.c: basic git_merge ────────────────────────────────────────

    [Fact]
    public async Task Simple_Automerge()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchByOidAsync(repo, "refs/heads/master", s_branch);

        string mergeHeadPath = Path.Combine(repo.Path, "MERGE_HEAD");
        Assert.True(File.Exists(mergeHeadPath));

        Assert.True((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);

        string? msg = await repo.MessageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(msg);
        Assert.Contains("Merge", msg);

        MergeTestHelpers.AssertWorkdirFile(repo, "automergeable.txt", ConflictData.AutomergeableMergedFile);
    }

    [Fact]
    public async Task Simple_ConflictFile()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchByOidAsync(repo, "refs/heads/master", s_branch);

        MergeTestHelpers.AssertWorkdirFile(repo, "conflicting.txt", ConflictData.ConflictingMergeFile);
    }

    [Fact]
    public async Task Simple_Diff3()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchByOidAsync(repo, "refs/heads/master", s_branch,
            checkoutOpts: new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Safe | GitCheckoutStrategy.ConflictStyleDiff3,
            });

        MergeTestHelpers.AssertWorkdirFile(repo, "conflicting.txt", ConflictData.ConflictingDiff3File);
    }

    [Fact]
    public async Task Simple_Union()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchByOidAsync(repo, "refs/heads/master", s_branch,
            mergeOpts: new GitMergeOptions { Favor = GitMergeFileFavor.Union });

        MergeTestHelpers.AssertWorkdirFile(repo, "conflicting.txt", ConflictData.ConflictingUnionFile);
    }

    [Fact]
    public async Task Simple_FavorOurs()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchByOidAsync(repo, "refs/heads/master", s_branch,
            mergeOpts: new GitMergeOptions { Favor = GitMergeFileFavor.Ours });

        Assert.False((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);
    }

    [Fact]
    public async Task Simple_FavorTheirs()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchByOidAsync(repo, "refs/heads/master", s_branch,
            mergeOpts: new GitMergeOptions { Favor = GitMergeFileFavor.Theirs });

        Assert.False((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);
    }

    [Fact]
    public async Task Simple_Unrelated()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        // Look up the unrelated branch OID dynamically.
        GitReference? unrelatedRef = await repo.ReferenceResolveAsync("refs/heads/unrelated", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(unrelatedRef);
        GitOid unrelatedOid = ((GitDirectReference)unrelatedRef).Target;
        await MergeTestHelpers.MergeBranchByOidAsync(repo, "refs/heads/master", unrelatedOid);

        Assert.True((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);
    }

    // ── recursive.c: conflict with virtual base ──────────────────────────

    [Fact]
    public async Task Recursive_ConflictWithVirtualBase()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();
        await MergeTestHelpers.MergeBranchesAsync(repo, "refs/heads/branchF-1", "refs/heads/branchF-2");

        // The veal.txt file should have conflict markers from the virtual base.
        Assert.True((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);
    }

    [Fact]
    public async Task Recursive_ConflictingMergeBaseWithDiff3()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();
        await MergeTestHelpers.MergeBranchesAsync(repo, "refs/heads/branchH-2", "refs/heads/branchH-1",
            mergeOpts: new GitMergeOptions { FileFlags = GitMergeFileFlags.StyleDiff3 });

        Assert.True((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);
    }

    // ── trivial.c: workdir trivial merges ────────────────────────────────

    [Fact]
    public async Task Trivial_5alt_BothAddedSame()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchesAsync(repo, "refs/heads/trivial-5alt-1", "refs/heads/trivial-5alt-1-branch");

        // Both sides added the same file — no conflict.
        Assert.False((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);
    }

    [Fact]
    public async Task Trivial_11_BothModifiedDifferently()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchesAsync(repo, "refs/heads/trivial-11", "refs/heads/trivial-11-branch");

        // Both sides modified differently — conflict.
        Assert.True((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);
    }

    [Fact]
    public async Task Trivial_13_OurModified_TheirUnchanged()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchesAsync(repo, "refs/heads/trivial-13", "refs/heads/trivial-13-branch");

        // Our side modified, theirs unchanged — no conflict.
        Assert.False((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);
    }

    [Fact]
    public async Task Trivial_14_OurUnchanged_TheirModified()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await MergeTestHelpers.MergeBranchesAsync(repo, "refs/heads/trivial-14", "refs/heads/trivial-14-branch");

        // Our side unchanged, theirs modified — no conflict.
        Assert.False((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);
    }
}
