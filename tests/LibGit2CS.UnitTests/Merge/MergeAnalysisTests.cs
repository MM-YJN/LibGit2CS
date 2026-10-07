using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Merge;

public sealed class MergeAnalysisTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public MergeAnalysisTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeAnalysisTests_" + Guid.NewGuid().ToString("N")[..8]);
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
        return await GitRepository.OpenAsync(Path.Combine(path, "merge-resolve"), new GitContext());
    }

    // master = bd593285fc7fe4ca18ccdbabf027f5d689101452
    // branch = 7cb63eed597130ba4abb87b3e544b85021905520
    // ff_branch = fd89f8cffb663ac89095a0f9764902e93ceaca6a
    // previous = c607fc30883e335def28cd686b51f6cfa02b06ec

    /// <summary>
    /// Helper: runs merge analysis from a branch name (our branch) against
    /// a their branch. Mirrors the C test helper <c>analysis_from_branch</c>.
    /// </summary>
    private static async Task<GitMergeAnalysisResult> AnalyzeFromBranchAsync(
        GitRepository repo,
        string? ourBranchName,
        string theirBranchName)
    {
        GitReference ourRef;
        if (ourBranchName is not null)
        {
            ourRef = (await repo.ReferenceLookupAsync($"refs/heads/{ourBranchName}"))!;
        }
        else
        {
            ourRef = (await repo.ReferenceLookupAsync(GitReferences.HeadFile))!;
        }

        GitReference theirRef = (await repo.ReferenceLookupAsync($"refs/heads/{theirBranchName}"))!;
        using GitAnnotatedCommit theirHead = await repo.AnnotatedCommitFromRefAsync(theirRef);

        return await repo.MergeAnalyzeForRefAsync(ourRef, [theirHead]);
    }

    // ── Basic analysis (matches merge/analysis.c golden tests) ───────────

    [Fact]
    public async Task FastForward_AnalysisIsNormalAndFastForward()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, null, "ff_branch");

        Assert.Equal(GitMergeAnalysis.Normal | GitMergeAnalysis.FastForward, result.Analysis);
    }

    [Fact]
    public async Task NoFastForward_AnalysisIsNormal()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, null, "branch");

        Assert.Equal(GitMergeAnalysis.Normal, result.Analysis);
    }

    [Fact]
    public async Task UpToDate_AnalysisIsUpToDate()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // Merging master into HEAD (which is master) → up-to-date.
        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, null, "master");

        Assert.Equal(GitMergeAnalysis.UpToDate, result.Analysis);
    }

    [Fact]
    public async Task UpToDate_MergingPreviousCommit_AnalysisIsUpToDate()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // previous is an ancestor of HEAD (master) → up-to-date.
        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, null, "previous");

        Assert.Equal(GitMergeAnalysis.UpToDate, result.Analysis);
    }

    [Fact]
    public async Task BetweenUpToDateRefs_AnalysisIsUpToDate()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // our=branch, their=previous → previous is ancestor of branch → up-to-date.
        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, "branch", "previous");

        Assert.Equal(GitMergeAnalysis.UpToDate, result.Analysis);
    }

    [Fact]
    public async Task BetweenNoFastForwardRefs_AnalysisIsNormal()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // our=branch, their=ff_branch → these diverge (no FF possible).
        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, "branch", "ff_branch");

        Assert.Equal(GitMergeAnalysis.Normal, result.Analysis);
    }

    // ── Config preferences (merge.ff) ───────────────────────────────────

    [Fact]
    public async Task FastForward_WithConfigNoFF_PreferenceIsNoFastForward()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        await repo.Config.SetStringAsync("merge.ff", "false", cancellationToken: TestContext.Current.CancellationToken);

        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, null, "ff_branch");

        Assert.Equal(GitMergeAnalysis.Normal | GitMergeAnalysis.FastForward, result.Analysis);
        Assert.Equal(GitMergePreference.NoFastForward, result.Preference & GitMergePreference.NoFastForward);
    }

    [Fact]
    public async Task NoFastForward_WithConfigFFOnly_PreferenceIsFastForwardOnly()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        await repo.Config.SetStringAsync("merge.ff", "only", cancellationToken: TestContext.Current.CancellationToken);

        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, null, "branch");

        Assert.Equal(GitMergeAnalysis.Normal, result.Analysis);
        Assert.Equal(GitMergePreference.FastForwardOnly, result.Preference & GitMergePreference.FastForwardOnly);
    }

    [Fact]
    public async Task NoConfig_PreferenceIsNone()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, null, "branch");

        Assert.Equal(GitMergePreference.None, result.Preference);
    }

    [Fact]
    public async Task MergeFF_RawNonUtf8Value_NoPreference()
    {
        // Pin: merge.ff is parsed over the raw value bytes (merge.c:3233-3238) — a raw 0xE9 byte is neither a bool nor "only", so the
        // preference is None. A string tier would have decoded it to U+FFFD (also no match); the byte tier must not crash and must not treat the raw
        // byte as a bool.
        await using GitRepository repo = await OpenMergeResolveRepoAsync();
        await repo.Config.SetBytesAsync("merge.ff", new byte[] { 0xE9 }, cancellationToken: TestContext.Current.CancellationToken);

        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, null, "ff_branch");

        Assert.Equal(GitMergePreference.None, result.Preference);
    }

    // ── Unborn HEAD ──────────────────────────────────────────────────────

    [Fact]
    public async Task Unborn_AnalysisIsFastForwardAndUnborn()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // Delete the master ref to make HEAD unborn.
        string masterPath = Path.Combine(repo.Path, "refs", "heads", "master");
        File.Delete(masterPath);

        GitMergeAnalysisResult result = await AnalyzeFromBranchAsync(repo, null, "branch");

        Assert.Equal(GitMergeAnalysis.FastForward | GitMergeAnalysis.Unborn, result.Analysis);
    }

    // ── Error cases ──────────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_MultipleTheirHeads_Throws()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitReference theirRef1 = (await repo.ReferenceLookupAsync("refs/heads/branch", cancellationToken: TestContext.Current.CancellationToken))!;
        GitReference theirRef2 = (await repo.ReferenceLookupAsync("refs/heads/ff_branch", cancellationToken: TestContext.Current.CancellationToken))!;
        using GitAnnotatedCommit their1 = await repo.AnnotatedCommitFromRefAsync(theirRef1, cancellationToken: TestContext.Current.CancellationToken);
        using GitAnnotatedCommit their2 = await repo.AnnotatedCommitFromRefAsync(theirRef2, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await repo.MergeAnalyzeAsync([their1, their2], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Analyze_NullArguments_Throw()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await repo.MergeAnalyzeAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }
}
