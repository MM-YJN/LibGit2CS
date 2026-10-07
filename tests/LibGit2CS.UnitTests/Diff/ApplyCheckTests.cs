using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitDiff = LibGit2CS.Diff.GitDiff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Tests for <see cref="GitRepository.ApplyAsync"/> in CHECK mode.
/// Ported from <c>tests/libgit2/apply/check.c</c> — validates that
/// a diff would apply cleanly without performing any writes.
/// </summary>
public class ApplyCheckTests : ApplyGoldenBase
{
    // DIFF_MODIFY_TWO_FILES from apply_helpers.h — modifies asparagus.txt
    // and veal.txt (both present in the merge-recursive fixture at 539bd01).
    private const string DiffModifyTwoFiles =
        "diff --git a/asparagus.txt b/asparagus.txt\n" +
        "index f516580..ffb36e5 100644\n" +
        "--- a/asparagus.txt\n" +
        "+++ b/asparagus.txt\n" +
        "@@ -1 +1 @@\n" +
        "-ASPARAGUS SOUP!\n" +
        "+ASPARAGUS SOUP.\n" +
        "diff --git a/veal.txt b/veal.txt\n" +
        "index 94d2c01..a7b0665 100644\n" +
        "--- a/veal.txt\n" +
        "+++ b/veal.txt\n" +
        "@@ -1 +1 @@\n" +
        "-VEAL SOUP!\n" +
        "+VEAL SOUP.\n" +
        "@@ -7 +7 @@ occasionally, then put into it a shin of veal, let it boil two hours\n" +
        "-longer. take out the slices of ham, and skim off the grease if any\n" +
        "+longer; take out the slices of ham, and skim off the grease if any\n";

    // DIFF_MODIFY_TWO_FILES_BINARY from apply_helpers.h — binary equivalent.
    private const string DiffModifyTwoFilesBinary =
        "diff --git a/asparagus.txt b/asparagus.txt\n" +
        "index f51658077d85f2264fa179b4d0848268cb3475c3..ffb36e513f5fdf8a6ba850a20142676a2ac4807d 100644\n" +
        "GIT binary patch\n" +
        "delta 24\n" +
        "fcmX@ja+-zTF*v|6$k9DCSRvRyG(c}7zYP-rT_OhP\n" +
        "\n" +
        "delta 24\n" +
        "fcmX@ja+-zTF*v|6$k9DCSRvRyG(d49zYP-rT;T@W\n" +
        "\n" +
        "diff --git a/veal.txt b/veal.txt\n" +
        "index 94d2c01087f48213bd157222d54edfefd77c9bba..a7b066537e6be7109abfe4ff97b675d4e077da20 100644\n" +
        "GIT binary patch\n" +
        "delta 26\n" +
        "hcmX@kah!uI%+=9HA=p1OKyM?L03)OIW@$zpW&mXg25bNT\n" +
        "\n" +
        "delta 26\n" +
        "hcmX@kah!uI%+=9HA=p1OKyf3N03)N`W@$zpW&mU#22ub3\n" +
        "\n";

    /// <summary>
    /// Port of <c>test_apply_check__generate_diff</c> (check.c:26-55).
    /// Generates a tree-to-tree diff (539bd01 → 7c7bf85), then validates via
    /// CHECK mode that it would apply to BOTH (workdir+index). The fixture is
    /// pre-reset to 539bd01 so the workdir+index match the preimage.
    /// </summary>
    [Fact]
    public async Task Apply_Check_GeneratedDiff_DoesNotThrow()
    {
        const string aOid = "539bd011c4822c560c1d17cab095006b7a10f707";
        const string bOid = "7c7bf85e978f1d18c0566f702d2cb7766b9c8d4f";

        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();
        GitTree aTree = await ResolveTreeAsync(repo, aOid);
        GitTree bTree = await ResolveTreeAsync(repo, bOid);

        GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: TestContext.Current.CancellationToken);

        // CHECK mode: validate the diff would apply to workdir+index. No writes.
        await repo.ApplyAsync(diff, GitApplyLocation.Both,
            new GitApplyOptions { Flags = GitApplyFlags.Check }, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Port of <c>test_apply_check__parsed_diff</c> (check.c:57-71). Parses a
    /// diff from a buffer, then validates via CHECK mode that it would apply
    /// to the index.
    /// </summary>
    [Fact]
    public async Task Apply_Check_ParsedDiff_DoesNotThrow()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(DiffModifyTwoFiles);

        // CHECK mode: validate the diff would apply to the index. No writes.
        await repo.ApplyAsync(diff, GitApplyLocation.Index,
            new GitApplyOptions { Flags = GitApplyFlags.Check }, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Port of <c>test_apply_check__binary</c> (check.c:73-88). Parses a
    /// binary diff from a buffer, then validates via CHECK mode that it
    /// would apply to the index.
    /// </summary>
    [Fact]
    public async Task Apply_Check_Binary_DoesNotThrow()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(DiffModifyTwoFilesBinary);

        // CHECK mode: validate the binary diff would apply to the index.
        await repo.ApplyAsync(diff, GitApplyLocation.Index,
            new GitApplyOptions { Flags = GitApplyFlags.Check }, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies that CHECK mode with a diff that cannot apply (preimage not
    /// found) throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.ApplyFail"/>.
    /// </summary>
    [Fact]
    public async Task Apply_Check_PreimageNotFound_ThrowsApplyFail()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // A patch that modifies a file that does not exist in the index.
        // The patch structure is valid (references a path not in the repo),
        // so Diff.FromBuffer succeeds, but Apply fails at preimage read.
        const string badDiff =
            "diff --git a/nonexistent.txt b/nonexistent.txt\n" +
            "index f45ad73..f45ad73 100644\n" +
            "--- a/nonexistent.txt\n" +
            "+++ b/nonexistent.txt\n" +
            "@@ -1 +1 @@\n" +
            "-This file does not exist in the preimage.\n" +
            "+This file does not exist in the preimage.\n";

        var diff = GitDiff.FromBuffer(badDiff);

        // Apply to Index: the preimage (nonexistent.txt) is not in the index.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Index,
                new GitApplyOptions { Flags = GitApplyFlags.Check }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.ApplyFail, ex.Code);
    }
}
