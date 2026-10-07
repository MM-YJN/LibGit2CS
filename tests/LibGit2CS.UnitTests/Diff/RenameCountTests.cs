using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Count-assertion tests for rename/copy detection, ported from libgit2's
/// <c>tests/libgit2/diff/rename.c</c> against the <c>renames</c> fixture. These
/// use STATIC pre-baked commits (no runtime mutation) and exercise the full
/// <see cref="GitDiff.FindSimilar"/> flag matrix on
/// <see cref="DiffTransform"/>. Counts mirror clar's <c>diff_expects</c>.
/// </summary>
/// <remarks>
/// <c>renames</c> fixture commit OIDs (rename.c:18-25):
/// <c>INITIAL</c>=31e47d8, <c>COPY_RENAME</c>=2bc7f35,
/// <c>REWRITE_COPY</c>=1c068de, <c>RENAME_MODIFICATION</c>=19dd32d.
/// </remarks>
public sealed class RenameCountTests : DiffGoldenBase
{
    private const string Initial = "31e47d8c1fa36d7f8d537b96158e3f024de0a9f2";
    private const string CopyRename = "2bc7f351d20b53f1c72c16c4b036e491c478c49a";
    private const string RewriteCopy = "1c068dee5790ef1580cfc4cd670915b48d790084";
    private const string RenameModification = "19dd32dfb1520a64e5bbaae8dce6ef423dfa2f13";

    private static readonly GitDiffOptions s_treeUnmod = new()
    {
        Flags = GitDiffOptionsFlags.IncludeUnmodified
    };

    // ━━ match_oid (rename.c) — INITIAL vs COPY_RENAME ━━
    // serving.txt → sixserving.txt (100% rename); songofseven.txt added;
    // sevencities.txt unmodified.

    // no find_similar (raw add/delete view).
    [Fact]
    public async Task MatchOid_NoFindSimilar_4files_2added_1deleted_1unmod()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, Initial);
        GitTree newTree = await ResolveTreeAsync(repo, CopyRename);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(4, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(2, c[GitDeltaStatus.Added]);
        Assert.Equal(1, c[GitDeltaStatus.Deleted]);
    }

    // FIND_RENAMES → serving.txt→sixserving.txt detected (100%).
    [Fact]
    public async Task MatchOid_FindRenames_3files_1renamed_1added_1unmod()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, Initial);
        GitTree newTree = await ResolveTreeAsync(repo, CopyRename);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(3, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
        Assert.Equal(1, c[GitDeltaStatus.Renamed]);
    }

    // COPIES_FROM_UNMODIFIED → sevencities→songofseven detected as copy.
    [Fact]
    public async Task MatchOid_CopiesFromUnmodified_3files_1copied_1renamed_1unmod()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, Initial);
        GitTree newTree = await ResolveTreeAsync(repo, CopyRename);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.CopiesFromUnmodified }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(3, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(1, c[GitDeltaStatus.Copied]);
        Assert.Equal(1, c[GitDeltaStatus.Renamed]);
    }

    // COPIES_FROM_UNMODIFIED | EXACT_MATCH_ONLY (all matches are 100%, so
    // exact-only yields the same result as the plain copies-from-unmodified case).
    [Fact]
    public async Task MatchOid_CopiesFromUnmodifiedExact_3files_1copied_1renamed()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, Initial);
        GitTree newTree = await ResolveTreeAsync(repo, CopyRename);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions
        { Flags = GitDiffFindFlags.CopiesFromUnmodified | GitDiffFindFlags.ExactMatchOnly }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(3, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(1, c[GitDeltaStatus.Copied]);
        Assert.Equal(1, c[GitDeltaStatus.Renamed]);
    }

    // ━━ not_exact_match tree A (rename.c) — COPY_RENAME vs REWRITE_COPY ━━
    // sixserving.txt indentation change (MOD); songofseven.txt major rewrite;
    // ikeepsix.txt new (copy of sixserving, >80%); sevencities.txt unmodified.

    // no find_similar.
    [Fact]
    public async Task NotExact_TreeA_NoFind_4files_2mod_1added_1unmod()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, CopyRename);
        GitTree newTree = await ResolveTreeAsync(repo, RewriteCopy);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(4, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(2, c[GitDeltaStatus.Modified]);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
    }

    // FIND_RENAMES only — songofseven rewrite too dissimilar; ikeepsix not a
    // copy without COPIES flag. Same counts as the plain no-find case.
    [Fact]
    public async Task NotExact_TreeA_FindRenames_4files_2mod_1added()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, CopyRename);
        GitTree newTree = await ResolveTreeAsync(repo, RewriteCopy);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(4, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(2, c[GitDeltaStatus.Modified]);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
    }

    // RENAMES | COPIES → ikeepsix.txt detected as copy from sixserving.
    [Fact]
    public async Task NotExact_TreeA_RenamesCopies_4files_2mod_1copied()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, CopyRename);
        GitTree newTree = await ResolveTreeAsync(repo, RewriteCopy);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames | GitDiffFindFlags.Copies }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(4, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(2, c[GitDeltaStatus.Modified]);
        Assert.Equal(1, c[GitDeltaStatus.Copied]);
    }

    // FIND_ALL + break_rewrite_threshold=70 → songofseven rewrite split into
    // add+delete (similarity below 70... actually break threshold splits MOD<70).
    [Fact]
    public async Task NotExact_TreeA_FindAll_BreakThreshold70_5files_split()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, CopyRename);
        GitTree newTree = await ResolveTreeAsync(repo, RewriteCopy);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions
        { Flags = GitDiffFindFlags.All, BreakRewriteThreshold = 70 }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(5, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Unmodified]);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
        Assert.Equal(1, c[GitDeltaStatus.Deleted]);
        Assert.Equal(1, c[GitDeltaStatus.Copied]);
    }

    // ━━ not_exact_match tree B (rename.c) — REWRITE_COPY vs RENAME_MODIFICATION ━━
    // ikeepsix/sixserving modified; songofseven→untimely, sevencities→songof7cities.

    // no find_similar.
    [Fact]
    public async Task NotExact_TreeB_NoFind_6files_2mod_2added_2deleted()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, RewriteCopy);
        GitTree newTree = await ResolveTreeAsync(repo, RenameModification);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(6, c.Files);
        Assert.Equal(2, c[GitDeltaStatus.Modified]);
        Assert.Equal(2, c[GitDeltaStatus.Added]);
        Assert.Equal(2, c[GitDeltaStatus.Deleted]);
    }

    // RENAMES | COPIES → 2 add/del pairs fold into renames.
    [Fact]
    public async Task NotExact_TreeB_RenamesCopies_4files_2mod_2renamed()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, RewriteCopy);
        GitTree newTree = await ResolveTreeAsync(repo, RenameModification);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames | GitDiffFindFlags.Copies }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(4, c.Files);
        Assert.Equal(2, c[GitDeltaStatus.Modified]);
        Assert.Equal(2, c[GitDeltaStatus.Renamed]);
    }

    // FIND_ALL (default similarity) → sixserving internal whitespace changes
    // are significant enough to split into add+delete.
    [Fact]
    public async Task NotExact_TreeB_FindAll_5files_split()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, RewriteCopy);
        GitTree newTree = await ResolveTreeAsync(repo, RenameModification);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.All }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(5, c.Files);
        Assert.Equal(1, c[GitDeltaStatus.Modified]);
        Assert.Equal(1, c[GitDeltaStatus.Added]);
        Assert.Equal(1, c[GitDeltaStatus.Deleted]);
        Assert.Equal(2, c[GitDeltaStatus.Renamed]);
    }

    // FIND_ALL | IGNORE_WHITESPACE → sixserving stays modified (not split).
    [Fact]
    public async Task NotExact_TreeB_FindAllIgnoreWs_4files_2mod_2renamed()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, RewriteCopy);
        GitTree newTree = await ResolveTreeAsync(repo, RenameModification);

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, s_treeUnmod, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions
        { Flags = GitDiffFindFlags.All | GitDiffFindFlags.IgnoreWhitespace }, cancellationToken: TestContext.Current.CancellationToken);

        DiffCounter c = await DiffCounter.CountAsync(diff);
        Assert.Equal(4, c.Files);
        Assert.Equal(2, c[GitDeltaStatus.Modified]);
        Assert.Equal(2, c[GitDeltaStatus.Renamed]);
    }
}
