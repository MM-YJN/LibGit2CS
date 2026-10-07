using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Count-assertion tests for tree-to-index diff, ported from libgit2's
/// <c>tests/libgit2/diff/index.c::test_diff_index__0</c> against the
/// <c>status</c> fixture. The fixture ships with a pre-baked index (staged
/// changes baked in), so no runtime mutation is needed. Counts mirror clar's
/// <c>diff_expects</c>.
/// </summary>
public sealed class IndexDiffCountTests : DiffGoldenBase
{
    // status fixture commits: a = current HEAD, b = the start.
    private const string CommitA = "26a125ee1bfc5df1e1b2e9441bbe63c8a7ae989f";
    private const string CommitB = "0017bd4ab1ec30440b17bae1680cff124ab5f1f6";

    // tree a (HEAD) vs index: 8 files (3 added, 2 deleted, 3 modified),
    // 8 hunks, 11 lines (3 ctxt, 6 adds, 2 dels).
    [Fact]
    public async Task TreeToIndex_HeadTree_8files_8hunks_11lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");
        GitTree tree = await ResolveTreeAsync(repo, CommitA);

        using GitDiff diff = await repo.DiffTreeToIndexAsync(tree, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(8, c.Files);
        Assert.Equal(3, c[GitDeltaStatus.Added]);
        Assert.Equal(2, c[GitDeltaStatus.Deleted]);
        Assert.Equal(3, c[GitDeltaStatus.Modified]);
        Assert.Equal(8, c.Hunks);
        Assert.Equal(11, c.Lines);
        Assert.Equal(3, c.LineContext);
        Assert.Equal(6, c.LineAdds);
        Assert.Equal(2, c.LineDels);
    }

    // tree b (the start) vs index: 12 files (7 added, 2 deleted, 3 modified),
    // 12 hunks, 16 lines (3 ctxt, 11 adds, 2 dels).
    [Fact]
    public async Task TreeToIndex_StartTree_12files_12hunks_16lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");
        GitTree tree = await ResolveTreeAsync(repo, CommitB);

        using GitDiff diff = await repo.DiffTreeToIndexAsync(tree, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(12, c.Files);
        Assert.Equal(7, c[GitDeltaStatus.Added]);
        Assert.Equal(2, c[GitDeltaStatus.Deleted]);
        Assert.Equal(3, c[GitDeltaStatus.Modified]);
        Assert.Equal(12, c.Hunks);
        Assert.Equal(16, c.Lines);
        Assert.Equal(3, c.LineContext);
        Assert.Equal(11, c.LineAdds);
        Assert.Equal(2, c.LineDels);
    }
}
