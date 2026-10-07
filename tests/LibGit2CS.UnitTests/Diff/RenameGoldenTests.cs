using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Byte-exact golden tests for rename detection via
/// <see cref="GitDiff.FindSimilar"/>, ported from libgit2's
/// <c>tests/libgit2/diff/rename.c</c> against the <c>renames</c> fixture.
/// <c>git diff</c> does not detect renames by default; <c>-M</c> enables it,
/// matching <see cref="GitDiffFindFlags.Renames"/>. Expected output captured by
/// <c>generate-goldens.sh</c> with <c>core.autocrlf=false</c> (set by rename.c).
/// </summary>
public sealed class RenameGoldenTests : DiffGoldenBase
{
    // rename.c match_oid base case: 31e47d8 -> 2bc7f35. Without rename
    // detection, serving.txt is deleted and sixserving.txt/songofseven.txt
    // are added. Byte-exact against `git diff` (no -M).
    [Fact]
    public async Task Rename_Nominal_31e47d8_To_2bc7f35_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, "31e47d8c1fa36d7f8d537b96158e3f024de0a9f2");
        GitTree newTree = await ResolveTreeAsync(repo, "2bc7f351d20b53f1c72c16c4b036e491c478c49a");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("rename_31e47d8_to_2bc7f35_nominal"),
            await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));

        // Before find_similar: 1 delete (serving.txt) + 2 adds (sixserving,
        // songofseven) + 1 unmodified (sevencities, not shown w/o flag).
        Assert.Equal(3, diff.DeltaCount);
        Assert.Equal(1, (await DiffCounter.CountAsync(diff))[GitDeltaStatus.Deleted]);
        Assert.Equal(2, (await DiffCounter.CountAsync(diff))[GitDeltaStatus.Added]);
    }

    // rename.c with find_similar: 31e47d8 -> 2bc7f35 with GitDiffFindFlags.Renames
    // detects serving.txt -> sixserving.txt as a 100% rename. Byte-exact
    // against `git diff -M`.
    [Fact]
    public async Task Rename_Exact_31e47d8_To_2bc7f35_WithFindSimilar_MatchesGitDiff_M()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, "31e47d8c1fa36d7f8d537b96158e3f024de0a9f2");
        GitTree newTree = await ResolveTreeAsync(repo, "2bc7f351d20b53f1c72c16c4b036e491c478c49a");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("rename_31e47d8_to_2bc7f35_M"),
            await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));

        // After find_similar: serving.txt -> sixserving.txt is a Renamed delta
        // (100% similarity), songofseven.txt remains Added.
        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(1, c[GitDeltaStatus.Renamed]);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
    }
}
