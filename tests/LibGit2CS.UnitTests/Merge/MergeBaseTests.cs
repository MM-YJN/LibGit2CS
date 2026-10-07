using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Merge;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Merge;

public sealed class MergeBaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public MergeBaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeBaseTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    // previous = c607fc30883e335def28cd686b51f6cfa02b06ec (common ancestor of master and branch)
    private static readonly GitOid s_master = GitOid.Parse("bd593285fc7fe4ca18ccdbabf027f5d689101452".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_branch = GitOid.Parse("7cb63eed597130ba4abb87b3e544b85021905520".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_ffBranch = GitOid.Parse("fd89f8cffb663ac89095a0f9764902e93ceaca6a".AsSpan(), GitHashAlgorithmKind.Sha1);
    private static readonly GitOid s_previous = GitOid.Parse("c607fc30883e335def28cd686b51f6cfa02b06ec".AsSpan(), GitHashAlgorithmKind.Sha1);

    // ── MergeBaseFindAsync (2-commit) ───────────────────────────────────

    [Fact]
    public async Task Find_DivergingCommits_ReturnsCommonAncestor()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitOid? mb = await repo.MergeBaseFindAsync(s_master, s_branch, CancellationToken.None);

        Assert.NotNull(mb);
        Assert.Equal(s_previous, mb);
    }

    [Fact]
    public async Task Find_FastForwardCommits_ReturnsAncestor()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // ff_branch's parent is master, so master is the merge base.
        GitOid? mb = await repo.MergeBaseFindAsync(s_master, s_ffBranch, CancellationToken.None);

        Assert.NotNull(mb);
        Assert.Equal(s_master, mb);
    }

    [Fact]
    public async Task Find_SameCommit_ReturnsSameCommit()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitOid? mb = await repo.MergeBaseFindAsync(s_master, s_master, CancellationToken.None);

        Assert.NotNull(mb);
        Assert.Equal(s_master, mb);
    }

    [Fact]
    public async Task Find_AncestorAndDescendant_ReturnsAncestor()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // previous is an ancestor of master.
        GitOid? mb = await repo.MergeBaseFindAsync(s_master, s_previous, CancellationToken.None);

        Assert.NotNull(mb);
        Assert.Equal(s_previous, mb);
    }

    [Fact]
    public async Task Find_UnrelatedCommits_DoesNotThrow()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // The "unrelated" branch has a different root commit.
        // d6cf6c7 is a valid 40-char SHA1 in this repo.
        var unrelated = GitOid.Parse("d6cf6c7741b3316826af1314042550c97ded1d50".AsSpan(), GitHashAlgorithmKind.Sha1);
        _ = await repo.MergeBaseFindAsync(s_master, unrelated, CancellationToken.None);
    }

    // ── MergeBaseFindAllAsync (2-commit) ────────────────────────────────

    [Fact]
    public async Task FindAll_DivergingCommits_ReturnsSingleBase()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        IReadOnlyList<GitOid> bases = await repo.MergeBaseFindAllAsync(s_master, s_branch, CancellationToken.None);

        // For a simple fork (no criss-cross), there is exactly one merge base.
        Assert.Single(bases);
        Assert.Equal(s_previous, bases[0]);
    }

    // ── MergeBaseFindManyAsync (N commits) ────────────────────────────────

    [Fact]
    public async Task FindMany_ThreeCommits_ReturnsBestMergeBase()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // git_merge_base_many([master, branch, ff_branch]) finds commits
        // reachable from master (PARENT1) AND from {branch OR ff_branch} (PARENT2).
        // master is reachable from ff_branch → master is a merge base.
        // c607fc3 is also a merge base, but master is newer (best).
        GitOid? mb = await repo.MergeBaseFindManyAsync([s_master, s_branch, s_ffBranch], CancellationToken.None);

        Assert.NotNull(mb);
        Assert.Equal(s_master, mb);
    }

    [Fact]
    public async Task FindMany_TwoCommits_ThrowsForLessThanTwo()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        await Assert.ThrowsAsync<GitException>(() => repo.MergeBaseFindManyAsync([s_master], CancellationToken.None));
    }

    // ── MergeBaseFindAllManyAsync (N commits) ────────────────────────────

    [Fact]
    public async Task FindAllMany_ThreeCommits_ReturnsMergeBases()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // master is reachable from ff_branch → master is a merge base.
        // c607fc3 is also a merge base but is redundant (ancestor of master).
        // After remove_redundant, only master remains.
        IReadOnlyList<GitOid> bases = await repo.MergeBaseFindAllManyAsync([s_master, s_branch, s_ffBranch], CancellationToken.None);

        Assert.NotEmpty(bases);
        Assert.Equal(s_master, bases[0]);
    }

    // ── MergeBaseOctopusAsync ─────────────────────────────────────────────

    [Fact]
    public async Task Octopus_ThreeCommits_ReturnsCommonAncestor()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitOid? mb = await repo.MergeBaseOctopusAsync([s_master, s_branch, s_ffBranch], CancellationToken.None);

        Assert.NotNull(mb);
        Assert.Equal(s_previous, mb);
    }

    [Fact]
    public async Task Octopus_TwoCommits_ReturnsMergeBase()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitOid? mb = await repo.MergeBaseOctopusAsync([s_master, s_branch], CancellationToken.None);

        Assert.NotNull(mb);
        Assert.Equal(s_previous, mb);
    }

    [Fact]
    public async Task Octopus_FastForwardCommits_ReturnsAncestor()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitOid? mb = await repo.MergeBaseOctopusAsync([s_master, s_ffBranch], CancellationToken.None);

        Assert.NotNull(mb);
        Assert.Equal(s_master, mb);
    }

    // ── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindMany_NullCommits_Throw()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => repo.MergeBaseFindManyAsync(null!, CancellationToken.None));
    }
}
