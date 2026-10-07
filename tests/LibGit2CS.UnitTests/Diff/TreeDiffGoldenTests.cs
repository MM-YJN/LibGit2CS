using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Byte-exact + statistical golden tests for <see cref="GitDiff.TreeToTree"/>,
/// ported from libgit2's <c>tests/libgit2/diff/tree.c</c>. Expected patch output is
/// captured by <c>generate-goldens.sh</c> via the reference <c>git diff</c>; count
/// assertions mirror clar's <c>diff_expects</c> (<c>diff_helpers.c</c>).
/// </summary>
public sealed class TreeDiffGoldenTests : DiffGoldenBase
{
    // tree.c test_0 first diff: a=605812a -> b=370fe9e (context=1, interhunk=1).
    [Fact]
    public async Task TreeToTree_605812a_To_370fe9e_Ctx1_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitTree oldTree = await ResolveTreeAsync(repo, "605812ab7fe421fdd325a935d35cb06a9234a7d7");
        GitTree newTree = await ResolveTreeAsync(repo, "370fe9ec224ce33e71f9e5ec2bd1142ce9937a6a");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree,
            new GitDiffOptions { ContextLines = 1, InterHunkLines = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("tree_attr_605812a_to_370fe9e_ctx1"),
            await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));

        // Count assertions (clar tree.c:54-64).
        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(5, c.Files);
        Assert.Equal(2, c[GitDeltaStatus.Added]);
        Assert.Equal(1, c[GitDeltaStatus.Deleted]);
        Assert.Equal(2, c[GitDeltaStatus.Modified]);
        Assert.Equal(5, c.Hunks);
        Assert.Equal(44, c.Lines);
        Assert.Equal(1, c.LineContext);
        Assert.Equal(35, c.LineAdds);
        Assert.Equal(8, c.LineDels);
    }

    // tree.c test_0 second diff: c=f5b0af1 -> b=370fe9e (context=1, interhunk=1).
    [Fact]
    public async Task TreeToTree_f5b0af1_To_370fe9e_Ctx1_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitTree oldTree = await ResolveTreeAsync(repo, "f5b0af1fb4f5c0cd7aad880711d368a07333c307");
        GitTree newTree = await ResolveTreeAsync(repo, "370fe9ec224ce33e71f9e5ec2bd1142ce9937a6a");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree,
            new GitDiffOptions { ContextLines = 1, InterHunkLines = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("tree_attr_f5b0af1_to_370fe9e_ctx1"),
            await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));

        // Count assertions (clar tree.c:76-86).
        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(2, c.Files);
        Assert.Equal(2, c[GitDeltaStatus.Modified]);
        Assert.Equal(2, c.Hunks);
        Assert.Equal(23, c.Lines);
        Assert.Equal(1, c.LineContext);
        Assert.Equal(1, c.LineAdds);
        Assert.Equal(21, c.LineDels);
    }

    // tree.c options: 6bab5c7 -> 605812a, default context=3. Byte-exact.
    [Fact]
    public async Task TreeToTree_6bab5c7_To_605812a_Default_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitTree oldTree = await ResolveTreeAsync(repo, "6bab5c79cd5140d0f800917f550eb2a3dc32b0da");
        GitTree newTree = await ResolveTreeAsync(repo, "605812ab7fe421fdd325a935d35cb06a9234a7d7");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree,
            new GitDiffOptions { ContextLines = 3, InterHunkLines = 0 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("tree_attr_6bab5c7_to_605812a_ctx3"),
            await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));

        // Count assertions (clar tree.c:130, NORMAL ctx=3 row).
        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(5, c.Files);
        Assert.Equal(3, c[GitDeltaStatus.Added]);
        Assert.Equal(2, c[GitDeltaStatus.Modified]);
        Assert.Equal(4, c.Hunks);
        Assert.Equal(53, c.Lines);
        Assert.Equal(4, c.LineContext);
        Assert.Equal(46, c.LineAdds);
        Assert.Equal(3, c.LineDels);
    }

    // tree.c options: --ignore-all-space. Byte-exact.
    [Fact]
    public async Task TreeToTree_6bab5c7_To_605812a_IgnoreWhitespace_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitTree oldTree = await ResolveTreeAsync(repo, "6bab5c79cd5140d0f800917f550eb2a3dc32b0da");
        GitTree newTree = await ResolveTreeAsync(repo, "605812ab7fe421fdd325a935d35cb06a9234a7d7");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree,
            new GitDiffOptions
            {
                ContextLines = 3,
                InterHunkLines = 0,
                Flags = GitDiffOptionsFlags.IgnoreWhitespace,
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("tree_attr_6bab5c7_to_605812a_ctx3_ignorews"),
            await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    // tree.c options: -R (reverse). Verifies the reverse flag handling
    // (status swap + old/new entry swap) produces byte-exact reversed output.
    [Fact]
    public async Task TreeToTree_6bab5c7_To_605812a_Reverse_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        GitTree oldTree = await ResolveTreeAsync(repo, "6bab5c79cd5140d0f800917f550eb2a3dc32b0da");
        GitTree newTree = await ResolveTreeAsync(repo, "605812ab7fe421fdd325a935d35cb06a9234a7d7");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree,
            new GitDiffOptions
            {
                ContextLines = 3,
                InterHunkLines = 0,
                Flags = GitDiffOptionsFlags.Reverse,
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LoadExpectedBytes("tree_attr_6bab5c7_to_605812a_ctx3_reverse"),
            await diff.ToBufferAsync(GitDiffPrintFormat.Patch, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ━━ options table c-vs-d rows (tree.c:151-163) — count assertions ━━
    // c=f5b0af1 vs d=a97cc0198. Whitespace-flag variants at context=3 (row 8 at
    // context=1). These complement the byte-exact a-vs-b rows above; they
    // exercise the whitespace-handling matrix in the xdiff bridge.

    private static async Task<(GitTree c, GitTree d)> ResolveCDAsync(GitRepository repo)
    {
        GitTree c = await ResolveTreeAsync(repo, "f5b0af1fb4f5c0cd7aad880711d368a07333c307");
        GitTree d = await ResolveTreeAsync(repo, "a97cc019851d401a4f1d091cb91a15890a0dd1ba");
        return (c, d);
    }
    // row 4: NORMAL ctx=3 → 1 hunk, 22 lines (9 ctxt, 10 adds, 3 dels).
    [Fact]
    public async Task TreeToTree_f5b0af1_To_a97cc01_Normal_Ctx3_22lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        (GitTree c, GitTree d) = await ResolveCDAsync(repo);
        using GitDiff diff = await repo.DiffTreeToTreeAsync(c, d,
            new GitDiffOptions { ContextLines = 3, InterHunkLines = 0 }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter ct = await DiffCounter.CountAsync(diff);
        Assert.Equal(1, ct.Files);
        Assert.Equal(1, ct[GitDeltaStatus.Modified]);
        Assert.Equal(1, ct.Hunks);
        Assert.Equal(22, ct.Lines);
        Assert.Equal(9, ct.LineContext);
        Assert.Equal(10, ct.LineAdds);
        Assert.Equal(3, ct.LineDels);
    }

    // row 5: IGNORE_WHITESPACE ctx=3 → 19 lines (12 ctxt, 7 adds, 0 dels).
    [Fact]
    public async Task TreeToTree_f5b0af1_To_a97cc01_IgnoreWhitespace_Ctx3_19lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        (GitTree c, GitTree d) = await ResolveCDAsync(repo);
        using GitDiff diff = await repo.DiffTreeToTreeAsync(c, d,
            new GitDiffOptions { ContextLines = 3, InterHunkLines = 0, Flags = GitDiffOptionsFlags.IgnoreWhitespace }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter ct = await DiffCounter.CountAsync(diff);
        Assert.Equal(1, ct[GitDeltaStatus.Modified]);
        Assert.Equal(1, ct.Hunks);
        Assert.Equal(19, ct.Lines);
        Assert.Equal(12, ct.LineContext);
        Assert.Equal(7, ct.LineAdds);
        Assert.Equal(0, ct.LineDels);
    }

    // row 6: IGNORE_WHITESPACE_CHANGE ctx=3 → 20 lines (11 ctxt, 8 adds, 1 del).
    [Fact]
    public async Task TreeToTree_f5b0af1_To_a97cc01_IgnoreWsChange_Ctx3_20lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        (GitTree c, GitTree d) = await ResolveCDAsync(repo);
        using GitDiff diff = await repo.DiffTreeToTreeAsync(c, d,
            new GitDiffOptions { ContextLines = 3, InterHunkLines = 0, Flags = GitDiffOptionsFlags.IgnoreWhitespaceChange }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter ct = await DiffCounter.CountAsync(diff);
        Assert.Equal(1, ct.Hunks);
        Assert.Equal(20, ct.Lines);
        Assert.Equal(11, ct.LineContext);
        Assert.Equal(8, ct.LineAdds);
        Assert.Equal(1, ct.LineDels);
    }

    // row 7: IGNORE_WHITESPACE_EOL ctx=3 → 20 lines (11 ctxt, 8 adds, 1 del).
    [Fact]
    public async Task TreeToTree_f5b0af1_To_a97cc01_IgnoreWsEol_Ctx3_20lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        (GitTree c, GitTree d) = await ResolveCDAsync(repo);
        using GitDiff diff = await repo.DiffTreeToTreeAsync(c, d,
            new GitDiffOptions { ContextLines = 3, InterHunkLines = 0, Flags = GitDiffOptionsFlags.IgnoreWhitespaceEol }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter ct = await DiffCounter.CountAsync(diff);
        Assert.Equal(1, ct.Hunks);
        Assert.Equal(20, ct.Lines);
        Assert.Equal(11, ct.LineContext);
        Assert.Equal(8, ct.LineAdds);
        Assert.Equal(1, ct.LineDels);
    }

    // row 8: IGNORE_WHITESPACE | REVERSE ctx=1 → 18 lines (11 ctxt, 0 adds, 7 dels).
    [Fact]
    public async Task TreeToTree_f5b0af1_To_a97cc01_IgnoreWsReverse_Ctx1_18lines()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        (GitTree c, GitTree d) = await ResolveCDAsync(repo);
        using GitDiff diff = await repo.DiffTreeToTreeAsync(c, d,
            new GitDiffOptions
            {
                ContextLines = 1,
                InterHunkLines = 0,
                Flags = GitDiffOptionsFlags.IgnoreWhitespace | GitDiffOptionsFlags.Reverse
            }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter ct = await DiffCounter.CountAsync(diff);
        Assert.Equal(1, ct[GitDeltaStatus.Modified]);
        Assert.Equal(1, ct.Hunks);
        Assert.Equal(18, ct.Lines);
        Assert.Equal(11, ct.LineContext);
        Assert.Equal(0, ct.LineAdds);
        Assert.Equal(7, ct.LineDels);
    }
}
