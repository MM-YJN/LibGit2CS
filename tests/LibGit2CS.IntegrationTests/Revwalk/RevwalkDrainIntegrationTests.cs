using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.IntegrationTests.Revwalk;

/// <summary>
/// Integration tests for the revwalk-related iterators and merge-analysis
/// helpers on <see cref="GitRepository"/> that the existing
/// <see cref="WalkerIntegrationTests"/> and
/// <see cref="RevParserIntegrationTests"/> do not drain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b>
/// <see cref="WalkerIntegrationTests"/> exhaustively drains
/// <see cref="GitRevWalker.WalkAsync"/> (sort modes, push/hide variants,
/// simplify-first-parent, reset, hide-callback). 
/// <see cref="RevParserIntegrationTests"/> covers
/// <see cref="GitRevParser.ParseRangeAsync"/> and
/// <see cref="GitRevParser.ParseSingleAsync"/> for the common cases.
/// Neither exercises <see cref="GitRepository.MergeHeadForEachAsync"/> —
/// the iterator over <c>.git/MERGE_HEAD</c> written by an in-progress
/// merge — nor the <see cref="GitRepository.AheadBehindAsync"/> /
/// <see cref="GitRepository.DescendantOfAsync"/> graph-query helpers that
/// run a revwalk under the hood. These tests close those gaps.
/// </para>
/// <para>
/// <b>MERGE_HEAD without a real merge.</b> The
/// <see cref="GitRepository.MergeHeadForEachAsync"/> iterator reads
/// <c>.git/MERGE_HEAD</c> directly, so tests write the file with real
/// commit OIDs and drain the iterator — no actual merge needed. This
/// mirrors how libgit2's <c>git_repository_mergehead_foreach</c> is
/// tested (<c>tests/libgit2/merge/mergehead.c</c>).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class RevwalkDrainIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-rvdrn-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── MergeHeadForEachAsync ────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeHeadForEachAsync"/> on a repo with
    /// no <c>MERGE_HEAD</c> file yields nothing. Drains the iterator to
    /// completion and asserts emptiness — exercises the
    /// <c>File.Exists(mergeHeadPath)</c> early-return path.
    /// </summary>
    [Fact]
    public async Task MergeHeadForEach_NoMergeHeadFile_YieldsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] commits = await bld.BuildLinearHistoryAsync(1, ct: ct);

        List<GitOid> heads = await bld.Repo.MergeHeadForEachAsync(ct).ToListAsync(ct);

        Assert.Empty(heads);
    }

    /// <summary>
    /// <see cref="GitRepository.MergeHeadForEachAsync"/> on a repo with
    /// a <c>MERGE_HEAD</c> file containing two real commit OIDs yields both
    /// in file order. Exercises the <see cref="AsyncFileIO.ReadLinesAsync"/>
    /// loop + <see cref="GitOid.TryParse"/> + yield path.
    /// </summary>
    [Fact]
    public async Task MergeHeadForEach_TwoOids_YieldsBothInOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] commits = await bld.BuildLinearHistoryAsync(3, ct: ct);

        // Write a MERGE_HEAD file with the OIDs of commits[1] and commits[2].
        string mergeHeadPath = Path.Combine(bld.Repo.Path, "MERGE_HEAD");
        await File.WriteAllTextAsync(
            mergeHeadPath,
            $"{commits[1]}\n{commits[2]}\n",
            ct).ConfigureAwait(false);

        List<GitOid> heads = await bld.Repo.MergeHeadForEachAsync(ct).ToListAsync(ct);

        Assert.Equal(2, heads.Count);
        Assert.Equal(commits[1], heads[0]);
        Assert.Equal(commits[2], heads[1]);
    }

    /// <summary>
    /// <see cref="GitRepository.MergeHeadForEachAsync"/> on a
    /// <c>MERGE_HEAD</c> file with a trailing blank line throws — C's
    /// <c>git_repository_mergehead_foreach</c> (merge.c:606-611) checks
    /// <c>strlen(line) != hexsize</c> for EVERY <c>git__strsep</c> token,
    /// and an empty line has length 0. Exercises the parse-error branch.
    /// </summary>
    [Fact]
    public async Task MergeHeadForEach_TrailingEmptyLine_Skipped()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] commits = await bld.BuildLinearHistoryAsync(2, ct: ct);

        string mergeHeadPath = Path.Combine(bld.Repo.Path, "MERGE_HEAD");
        // Trailing blank line.
        await File.WriteAllTextAsync(
            mergeHeadPath,
            $"{commits[1]}\n\n",
            ct).ConfigureAwait(false);

        // C (merge.c:606-611): an empty line is "unable to parse OID -
        // invalid length" (GIT_ERROR_INVALID), not silently skipped.
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await bld.Repo.MergeHeadForEachAsync(ct).ToListAsync(ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("unable to parse OID - invalid length", ex.Message);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }

    /// <summary>
    /// <see cref="GitRepository.MergeHeadForEachAsync"/> on a
    /// <c>MERGE_HEAD</c> file with a non-OID line throws
    /// <see cref="GitException"/> with <see cref="GitErrorCode.Invalid"/>.
    /// Exercises the parse-error branch.
    /// </summary>
    [Fact]
    public async Task MergeHeadForEach_InvalidOidLine_ThrowsInvalid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        await bld.BuildLinearHistoryAsync(1, ct: ct);

        string mergeHeadPath = Path.Combine(bld.Repo.Path, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHeadPath, "not-an-oid\n", ct).ConfigureAwait(false);

        await Assert.ThrowsAsync<GitException>(async () => await bld.Repo.MergeHeadForEachAsync(ct).ToListAsync(ct));
    }

    // ── graph-query helpers (revwalk under the hood) ────────────────────

    /// <summary>
    /// <see cref="GitRepository.AheadBehindAsync"/> on a linear history
    /// reports the expected ahead/behind counts. With history
    /// <c>c0 → c1 → c2</c>, comparing <c>c2</c> (local) against <c>c0</c>
    /// (upstream) yields <c>(ahead=2, behind=0)</c>. Exercises the
    /// revwalk-based ahead/behind computation.
    /// </summary>
    [Fact]
    public async Task AheadBehind_LinearHistory_ReportsCorrectCounts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] commits = await bld.BuildLinearHistoryAsync(3, ct: ct);

        (int ahead, int behind) = await bld.Repo.AheadBehindAsync(commits[2], commits[0], ct);

        Assert.Equal(2, ahead);
        Assert.Equal(0, behind);
    }

    /// <summary>
    /// <see cref="GitRepository.AheadBehindAsync"/> on a divergent
    /// history reports both sides non-zero. With a root, two children
    /// <c>A</c> and <c>B</c>, comparing <c>A</c> against <c>B</c> yields
    /// <c>(ahead=1, behind=1)</c> — each side has one commit the other
    /// doesn't.
    /// </summary>
    [Fact]
    public async Task AheadBehind_DivergentHistory_BothSidesNonZero()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        (GitOid root, GitOid mainTip, GitOid featureTip) =
            await bld.BuildDivergentFileHistoryAsync("f.txt", "base\n"u8.ToArray(), "main\n"u8.ToArray(), "feat\n"u8.ToArray(), ct: ct);

        (int ahead, int behind) = await bld.Repo.AheadBehindAsync(mainTip, featureTip, ct);

        Assert.Equal(1, ahead);
        Assert.Equal(1, behind);
    }

    /// <summary>
    /// <see cref="GitRepository.DescendantOfAsync"/> on a linear
    /// history returns <c>true</c> for an ancestor and <c>false</c> for a
    /// non-ancestor. With <c>c0 → c1 → c2</c>, <c>c0</c> is a descendant-of
    /// ancestor of <c>c2</c> (so <c>DescendantOf(c2, c0)</c> is true), and
    /// <c>DescendantOf(c0, c2)</c> is false. Exercises the revwalk-based
    /// descendant check.
    /// </summary>
    [Fact]
    public async Task DescendantOf_LinearHistory_AncestorTrue_NonAncestorFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] commits = await bld.BuildLinearHistoryAsync(3, ct: ct);

        Assert.True(await bld.Repo.DescendantOfAsync(commits[2], commits[0], ct));
        Assert.False(await bld.Repo.DescendantOfAsync(commits[0], commits[2], ct));
        Assert.True(await bld.Repo.DescendantOfAsync(commits[2], commits[1], ct));
    }

    /// <summary>
    /// <see cref="GitRepository.DescendantOfAsync"/> returns
    /// <c>false</c> when comparing a commit to itself — libgit2's
    /// <c>git_graph_descendant_of</c> is strict (a commit is not its own
    /// descendant; the walk from <c>commit</c> never reaches a distinct
    /// <c>ancestor</c>).
    /// </summary>
    [Fact]
    public async Task DescendantOf_SameCommit_ReturnsFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] commits = await bld.BuildLinearHistoryAsync(2, ct: ct);

        Assert.False(await bld.Repo.DescendantOfAsync(commits[1], commits[1], ct));
    }
}
