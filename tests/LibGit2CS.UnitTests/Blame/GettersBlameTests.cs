using LibGit2CS.Blame;
using LibGit2CS.Repository;

using GitBlame = LibGit2CS.Blame.GitBlame;

namespace LibGit2CS.UnitTests.Blame;

/// <summary>
/// Tests for <see cref="Blame.GetHunk"/> and <see cref="Blame.GetHunkByLine"/>.
/// Ported from libgit2's <c>tests/libgit2/blame/getters.c</c>.
/// </summary>
/// <remarks>
/// The C test creates a synthetic blame with 5 hunks (lines 1, 4, 7, 10, 13)
/// and no repository. Since <see cref="Blame.File"/> requires a repo, we use
/// the blametest fixture (b.txt has 4 hunks at lines 1, 5, 6, 11) to verify the
/// getter APIs instead. The out-of-range assertions are the key tests.
/// </remarks>
public sealed class GettersBlameTests : BlameGoldenBase
{
    // getters.c::byindex — test hunk retrieval by index
    [Fact]
    public async Task ByIndex()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt", cancellationToken: TestContext.Current.CancellationToken);

        // b.txt has 4 hunks: lines 1, 5, 6, 11
        BlameHunk hunk = blame.GetHunk(2);
        Assert.NotNull(hunk);
        Assert.Equal(6, hunk.FinalStartLineNumber);

        // Out of range returns throws (C returns NULL; our API throws ArgumentOutOfRangeException).
        Assert.Throws<ArgumentOutOfRangeException>(() => blame.GetHunk(95));
    }

    // getters.c::byline — test hunk retrieval by line number
    [Fact]
    public async Task ByLine()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt", cancellationToken: TestContext.Current.CancellationToken);

        // b.txt hunk at line 5 is the boundary hunk (line 5 → b99f7ac0)
        BlameHunk? hunk = blame.GetHunkByLine(5);
        Assert.NotNull(hunk);
        Assert.Equal(5, hunk.FinalStartLineNumber);

        // Line 95 is out of range — returns null
        BlameHunk? nullHunk = blame.GetHunkByLine(95);
        Assert.Null(nullHunk);
    }
}
