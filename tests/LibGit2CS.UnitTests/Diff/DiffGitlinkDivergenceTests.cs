using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Golden test for a gitlink (submodule reference, mode <c>0160000</c>) in a
/// tree-to-tree diff, plus documentation of the remaining workdir-submodule
/// divergence.
/// </summary>
/// <remarks>
/// <b>Tree-to-tree gitlinks are NOT a divergence:</b> both <c>git</c> and
/// LibGit2CS report an added gitlink as <see cref="GitDeltaStatus.Added"/> with
/// mode <c>0160000</c> and an <c>index 0000000..&lt;oid&gt;</c> line. This test
/// verifies that byte-exact output.
/// <para>
/// <b>Remaining workdir-submodule divergence.</b> For a workdir submodule the
/// port derives the delta status from the submodule HEAD OID alone
/// (<c>maybe_modified_submodule</c>, diff_generate.c:738-801). C additionally
/// runs <c>git_submodule__status</c> and reports MODIFIED when the submodule
/// workdir is dirty even though HEAD is unchanged, appending <c>-dirty</c> to
/// the <c>Subproject commit</c> placeholder. The port reports that same-HEAD
/// dirty case as <see cref="GitDeltaStatus.Unmodified"/>; a moved-HEAD dirty
/// submodule matches C (MODIFIED with the <c>-dirty</c> suffix).
/// </para>
/// </remarks>
public sealed class DiffGitlinkDivergenceTests : DiffGoldenBase
{
    // 09176a9 (no gitlink) -> 9789681 adds the `testrepo` gitlink (mode 160000)
    // plus .gitmodules. Byte-exact against `git diff` — the gitlink is reported
    // as a regular Added delta with its commit OID and mode 0160000.
    [Fact]
    public async Task Gitlink_Added_TreeToTree_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("submodules");
        GitTree oldTree = await ResolveTreeAsync(repo, "09176a980273d801a3e37cc45c84af1366501ed9");
        GitTree newTree = await ResolveTreeAsync(repo, "97896810b3210244a62a82458b8e0819ecfc6850");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("gitlink_add_testrepo"),
            await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));

        // The gitlink delta is Added with GitLink mode.
        GitDiffDelta gitlink = Assert.Single(diff.Deltas, d => d.NewFile.Path?.ToUtf8String() == "testrepo");
        Assert.Equal(GitDeltaStatus.Added, gitlink.Status);
        Assert.Equal(GitFileMode.GitLink, gitlink.NewFile.Mode);
    }
}
