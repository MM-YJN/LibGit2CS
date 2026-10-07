using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Tests for patch header-only output (<c>--patch-header</c> format), ported
/// from libgit2's <c>tests/libgit2/diff/header.c</c> against the
/// <c>status</c> fixture.
/// </summary>
public sealed class HeaderPrintTests : DiffGoldenBase
{
    // header.c can_print_just_headers: TreeToIndex with PatchHeader format
    // emits only file headers (no hunks/lines). Expects 8 file headers.
    [Fact]
    public async Task PatchHeader_EmitsOnlyFileHeaders()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");
        GitTree tree = await ResolveTreeAsync(repo, "26a125ee1bfc5df1e1b2e9441bbe63c8a7ae989f");

        using GitDiff diff = await repo.DiffTreeToIndexAsync(tree, cancellationToken: TestContext.Current.CancellationToken);

        int headerCount = 0;
        await diff.PrintAsync(GitDiffPrintFormat.PatchHeader, (delta, hunk, line) =>
        {
            Assert.Equal(GitDiffLineOrigin.FileHeader, line.Origin);
            Assert.Null(hunk);
            headerCount++;
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8, headerCount);
    }
}
