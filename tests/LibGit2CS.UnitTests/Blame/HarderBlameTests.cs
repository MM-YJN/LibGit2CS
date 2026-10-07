using LibGit2CS.Blame;
using LibGit2CS.Repository;

using GitBlame = LibGit2CS.Blame.GitBlame;

namespace LibGit2CS.UnitTests.Blame;

/// <summary>
/// Flag-implication tests ported from libgit2's
/// <c>tests/libgit2/blame/harder.c</c>. The <c>TRACK_COPIES_*</c> flags are
/// reserved/unimplemented in libgit2 1.9.4 — these tests verify that the flag
/// implication chain in <c>normalize_options</c> (blame.c:279-284) doesn't crash
/// and produces the standard blame result.
/// </summary>
public sealed class HarderBlameTests : BlameGoldenBase
{
    // harder.c::m — TRACK_COPIES_SAME_FILE flag
    [Fact]
    public async Task TrackCopiesSameFile()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt",
            new GitBlameOptions { Flags = GitBlameFlags.TrackCopiesSameFile }, cancellationToken: TestContext.Current.CancellationToken);

        // Same result as normal blame (copy detection not implemented).
        Assert.Equal(4, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 4, 0, "da237394", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 1, 5, 1, 1, "b99f7ac0", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 2, 6, 5, 0, "63d671eb", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 3, 11, 5, 0, "aa06ecca", "b.txt");
    }

    // harder.c::c — TRACK_COPIES_SAME_COMMIT_MOVES flag (implies SameFile)
    [Fact]
    public async Task TrackCopiesSameCommitMoves()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt",
            new GitBlameOptions { Flags = GitBlameFlags.TrackCopiesSameCommitMoves }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 4, 0, "da237394", "b.txt");
    }

    // harder.c::cc — TRACK_COPIES_SAME_COMMIT_COPIES flag (implies SameCommitMoves → SameFile)
    [Fact]
    public async Task TrackCopiesSameCommitCopies()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt",
            new GitBlameOptions { Flags = GitBlameFlags.TrackCopiesSameCommitCopies }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 4, 0, "da237394", "b.txt");
    }

    // harder.c::ccc — TRACK_COPIES_ANY_COMMIT_COPIES flag (implies SameCommitCopies → SameCommitMoves → SameFile)
    [Fact]
    public async Task TrackCopiesAnyCommitCopies()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt",
            new GitBlameOptions { Flags = GitBlameFlags.TrackCopiesAnyCommitCopies }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 4, 0, "da237394", "b.txt");
    }
}
