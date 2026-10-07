using LibGit2CS.Diff;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Byte-exact golden tests for working-directory diffs, ported from
/// libgit2's <c>tests/libgit2/diff/workdir.c</c> against the <c>status</c>
/// fixture (which ships with workdir modifications baked in). Exercises the
/// <see cref="GitDiff.IndexToWorkdir"/> path — workdir OID
/// computation (<see cref="DiffFileContent.ComputeWorkdirOid"/>), the stat
/// heuristic in <c>MaybeModified</c>, and the filesystem iterator.
/// </summary>
/// <remarks>
/// <b>Known gap:</b> <see cref="GitDiff.TreeToWorkdir"/> (i.e.
/// <c>git diff HEAD</c>, which merges tree-to-index + index-to-workdir) is not
/// yet golden-tested. The merge status-combination (<c>MergeLikeCgit</c>) was
/// fixed (deltas are appended instead of combining
/// them), so the merged <em>delta list</em> is correct, but patch <em>content
/// loading</em> for merged deltas whose new side comes from the workdir (not
/// the index) still sources content from the wrong iterator. That is tracked
/// as a follow-up; it does not affect the index-to-workdir path verified here.
/// </remarks>
public sealed class WorkdirEofnlGoldenTests : DiffGoldenBase
{
    // index-to-workdir: `git diff` with no revs. Verifies DiffFileContent.LoadWorkdir
    // + ComputeWorkdirOid + the stat-based change detection in MaybeModified.
    [Fact]
    public async Task IndexToWorkdir_StatusFixture_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("workdir_index_to_workdir"),
            await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }
}
