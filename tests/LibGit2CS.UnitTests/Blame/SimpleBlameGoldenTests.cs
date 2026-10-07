using LibGit2CS.Blame;
using LibGit2CS.Core;
using LibGit2CS.Repository;

using GitBlame = LibGit2CS.Blame.GitBlame;

namespace LibGit2CS.UnitTests.Blame;

/// <summary>
/// Blame golden tests ported from libgit2's <c>tests/libgit2/blame/simple.c</c>.
/// Each test calls <see cref="Blame.File"/> and asserts hunk properties via
/// <see cref="BlameHunkVerifier.AssertHunkAsync"/> (matching clar's
/// <c>check_blame_hunk_index</c>).
/// </summary>
public sealed class SimpleBlameGoldenTests : BlameGoldenBase
{
    // simple.c::trivial_testrepo
    // $ git blame -s branch_file.txt
    //   c47800c7 1 (Scott Chacon 2010-05-25 11:58:14 -0700 1
    //   a65fedf3 2 (Scott Chacon 2011-08-09 19:33:46 -0700 2
    [Fact]
    public async Task TrivialTestrepo()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("testrepo");
        using GitBlame blame = await repo.BlameFileAsync("branch_file.txt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 1, 0, "c47800c7", "branch_file.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 1, 2, 1, 0, "a65fedf3", "branch_file.txt");
    }

    // simple.c::trivial_blamerepo
    // $ git blame -n b.txt — 4 hunks with boundary
    [Fact]
    public async Task TrivialBlamerepo()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 4, 0, "da237394", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 1, 5, 1, 1, "b99f7ac0", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 2, 6, 5, 0, "63d671eb", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 3, 11, 5, 0, "aa06ecca", "b.txt");
    }

    // simple.c::can_restrict_lines_min
    // opts.min_line = 8
    [Fact]
    public async Task CanRestrictLinesMin()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt", new GitBlameOptions { MinLine = 8 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 8, 3, 0, "63d671eb", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 1, 11, 5, 0, "aa06ecca", "b.txt");
    }

    // simple.c::can_ignore_whitespace_change
    // opts.flags |= GIT_BLAME_IGNORE_WHITESPACE; file = c.txt
    [Fact]
    public async Task CanIgnoreWhitespaceChange()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("c.txt",
            new GitBlameOptions { Flags = GitBlameFlags.IgnoreWhitespace }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 4, 0, "702c7aa5", "c.txt");
    }

    // simple.c::can_restrict_lines_max
    // opts.max_line = 6
    [Fact]
    public async Task CanRestrictLinesMax()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt", new GitBlameOptions { MaxLine = 6 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 4, 0, "da237394", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 1, 5, 1, 1, "b99f7ac0", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 2, 6, 1, 0, "63d671eb", "b.txt");
    }

    // simple.c::can_restrict_lines_both
    // opts.min_line = 2, opts.max_line = 7
    [Fact]
    public async Task CanRestrictLinesBoth()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt",
            new GitBlameOptions { MinLine = 2, MaxLine = 7 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 2, 3, 0, "da237394", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 1, 5, 1, 1, "b99f7ac0", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 2, 6, 2, 0, "63d671eb", "b.txt");
    }

    // simple.c::can_blame_huge_file
    // huge.txt — 65537 lines, 2 hunks
    [Fact]
    public async Task CanBlameHugeFile()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("huge.txt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 65536, 0, "4eecfea", "huge.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 1, 65537, 1, 0, "6653ff4", "huge.txt");
    }

    // simple.c::can_restrict_to_newish_commits
    // opts.oldest_commit = be3563a on testrepo.git (bare)
    [Fact]
    public async Task CanRestrictToNewishCommits()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("testrepo.git");
        GitOid oldestCommit = await ResolveOidAsync(repo, "be3563a");

        using GitBlame blame = await repo.BlameFileAsync("branch_file.txt",
            new GitBlameOptions { OldestCommit = oldestCommit }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 1, 1, "be3563a", "branch_file.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 1, 2, 1, 0, "a65fedf", "branch_file.txt");
    }

    // simple.c::can_restrict_to_first_parent_commits
    // opts.flags |= GIT_BLAME_FIRST_PARENT; file = b.txt
    [Fact]
    public async Task CanRestrictToFirstParentCommits()
    {
        await using GitRepository repo = await OpenBareFixtureRepoAsync("blametest.git");
        using GitBlame blame = await repo.BlameFileAsync("b.txt",
            new GitBlameOptions { Flags = GitBlameFlags.FirstParent }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, blame.HunkCount);
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 0, 1, 4, 0, "da237394", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 1, 5, 1, 1, "b99f7ac0", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 2, 6, 5, 0, "63d671eb", "b.txt");
        await BlameHunkVerifier.AssertHunkAsync(repo, blame, 3, 11, 5, 0, "bc7c5ac2", "b.txt");
    }
}
