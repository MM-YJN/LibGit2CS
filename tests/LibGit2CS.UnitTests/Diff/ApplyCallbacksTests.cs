using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Repository;

using GitDiff = LibGit2CS.Diff.GitDiff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Tests for <see cref="GitRepository.ApplyAsync"/> with delta/hunk callbacks.
/// Ported from <c>tests/libgit2/apply/callbacks.c</c> (3 tests). Verifies
/// that delta and hunk callbacks can abort or skip application.
/// </summary>
public class ApplyCallbacksTests : ApplyGoldenBase
{
    /// <summary>
    /// Port of <c>test_apply_callbacks__delta_aborts</c> (callbacks.c:36-52).
    /// A delta callback that returns -99 for veal.txt aborts the apply.
    /// Index and workdir should be unchanged.
    /// </summary>
    [Fact]
    public async Task ApplyCallbacks_DeltaAborts()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);

        var opts = new GitApplyOptions
        {
            DeltaCallback = delta =>
            {
                if (delta.OldFile.Path?.ToUtf8String() == "veal.txt")
                {
                    return -99;
                }
                return 0;
            },
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Index, opts, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.ApplyFail, ex.Code);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);
        await ApplyTestHelpers.AssertWorkdirUnchangedAsync(repo);
    }

    /// <summary>
    /// Port of <c>test_apply_callbacks__delta_can_skip</c> (callbacks.c:64-90).
    /// A delta callback that returns 1 for asparagus.txt skips that delta.
    /// Only veal.txt is modified in the workdir.
    /// </summary>
    [Fact]
    public async Task ApplyCallbacks_DeltaCanSkip()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);

        var opts = new GitApplyOptions
        {
            DeltaCallback = delta =>
            {
                if (delta.OldFile.Path?.ToUtf8String() == "asparagus.txt")
                {
                    return 1;
                }
                return 0;
            },
        };

        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, opts, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_callbacks__hunk_can_skip</c> (callbacks.c:100-128).
    /// A hunk callback that skips odd-numbered hunks. With DIFF_MANY_CHANGES_ONE
    /// (5 hunks), hunks 1 and 3 are skipped, producing a specific result.
    /// </summary>
    [Fact]
    public async Task ApplyCallbacks_HunkCanSkip()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ManyChangesOne);

        int count = 0;
        var opts = new GitApplyOptions
        {
            HunkCallback = _ => count++ % 2 == 1 ? 1 : 0,
        };

        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, opts, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "06f751b6ba4f017ddbf4248015768300268e092a", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }
}
