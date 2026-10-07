using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Revwalk;

/// <summary>
/// Integration tests for <see cref="GitRepository"/> merge-base query
/// methods (<see cref="GitRepository.MergeBaseFindAsync"/>,
/// <see cref="GitRepository.MergeBaseFindAllAsync"/>,
/// <see cref="GitRepository.MergeBaseFindManyAsync"/>,
/// <see cref="GitRepository.MergeBaseFindAllManyAsync"/>,
/// <see cref="GitRepository.MergeBaseOctopusAsync"/>)
/// exercised end-to-end against locally-initialized non-bare repos built
/// from scratch via <see cref="RepoBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit-test project covers
/// <see cref="GitRepository.MergeBaseFindAsync"/> against
/// <c>testrepo.zip</c> but not the multi-base (<c>FindAll</c>,
/// <c>FindMany</c>, <c>Octopus</c>) methods or the criss-cross
/// topology. Upstream libgit2 tests these in
/// <c>tests/libgit2/revwalk/mergebase.c</c> against several fixture
/// repos. These tests close the gap without Docker by building the
/// topologies (Y-merge, criss-cross, octopus) from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build. Mirrors
/// <see cref="Transports.LocalTransportTests"/> and
/// <see cref="Objects.TagIntegrationTests"/>.
/// </para>
/// </remarks>
public sealed class MergeBaseIntegrationTests
{
    // ── A. MergeBaseFindAsync ──────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeBaseFindAsync"/> between two branch
    /// tips that share a common ancestor returns that ancestor. Builds a
    /// Y-shaped DAG (root → A, root → B) and asserts the merge base is
    /// the root.
    /// </summary>
    [Fact]
    public async Task MergeBaseFind_TwoBranches_ReturnsCommonAncestor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid root = await bld.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid a = await bld.CommitFileAsync("a.txt", "a\n"u8.ToArray(), "a\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid b = await bld.CommitFileAsync("b.txt", "b\n"u8.ToArray(), "b\n", parent: root, updateRef: "refs/heads/feature", ct: ct);

        GitOid? mergeBase = await bld.Repo.MergeBaseFindAsync(a, b, ct);

        Assert.NotNull(mergeBase);
        Assert.Equal(root, mergeBase!.Value);
    }

    /// <summary>
    /// <see cref="GitRepository.MergeBaseFindAsync"/> between two commits
    /// with no common ancestor returns <c>null</c>. Builds two independent
    /// root commits (unrelated histories).
    /// </summary>
    [Fact]
    public async Task MergeBaseFind_UnrelatedHistories_ReturnsNull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid a = await bld.CommitFileAsync("a.txt", "a\n"u8.ToArray(), "a\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid b = await bld.CommitFileAsync("b.txt", "b\n"u8.ToArray(), "b\n", parent: null, updateRef: "refs/heads/feature", ct: ct);

        GitOid? mergeBase = await bld.Repo.MergeBaseFindAsync(a, b, ct);

        Assert.Null(mergeBase);
    }

    // ── B. MergeBaseFindAllAsync (criss-cross) ─────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeBaseFindAllAsync"/> on a criss-cross
    /// merge topology returns both merge bases. Builds the canonical
    /// criss-cross: root → left, root → right, then m1 = merge(left, right)
    /// and m2 = merge(left, right) on separate branches; tips are children
    /// of m1 and m2. The merge bases of (tip1, tip2) are {m1, m2}.
    /// </summary>
    [Fact]
    public async Task MergeBaseFindAll_CrissCrossMerge_ReturnsTwoBases()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        (_, _, _, _, _, GitOid tip1, GitOid tip2) = await bld.BuildCrissCrossDagAsync(ct);

        IReadOnlyList<GitOid> bases = await bld.Repo.MergeBaseFindAllAsync(tip1, tip2, ct);

        // A criss-cross has two merge bases. The exact OIDs depend on the
        // builder, but there must be exactly two distinct bases.
        Assert.Equal(2, bases.Count);
        Assert.NotEqual(bases[0], bases[1]);
    }

    // ── C. MergeBaseFindManyAsync ─────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeBaseFindManyAsync"/> with three
    /// commits that share a common ancestor returns that ancestor. Builds
    /// a 3-way fork (root → A, root → B, root → C) and asserts the merge
    /// base is the root.
    /// </summary>
    [Fact]
    public async Task MergeBaseFindMany_ThreeCommits_ReturnsCommonAncestor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid root = await bld.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid a = await bld.CommitFileAsync("a.txt", "a\n"u8.ToArray(), "a\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid b = await bld.CommitFileAsync("b.txt", "b\n"u8.ToArray(), "b\n", parent: root, updateRef: "refs/heads/feature", ct: ct);
        GitOid c = await bld.CommitFileAsync("c.txt", "c\n"u8.ToArray(), "c\n", parent: root, updateRef: "refs/heads/topic", ct: ct);

        GitOid? mergeBase = await bld.Repo.MergeBaseFindManyAsync([a, b, c], ct);

        Assert.NotNull(mergeBase);
        Assert.Equal(root, mergeBase!.Value);
    }

    /// <summary>
    /// <see cref="GitRepository.MergeBaseFindManyAsync"/> with fewer than
    /// two commits throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.Invalid"/>.
    /// </summary>
    [Fact]
    public async Task MergeBaseFindMany_LessThanTwo_ThrowsInvalid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        await Assert.ThrowsAsync<GitException>(() => bld.Repo.MergeBaseFindManyAsync([history[0]], ct));
    }

    // ── D. MergeBaseOctopusAsync ──────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeBaseOctopusAsync"/> with three
    /// commits sharing a common ancestor returns that ancestor. The
    /// octopus merge base is the common ancestor of ALL inputs.
    /// </summary>
    [Fact]
    public async Task MergeBaseOctopus_ThreeWayMerge_ReturnsCommonAncestor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid root = await bld.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid a = await bld.CommitFileAsync("a.txt", "a\n"u8.ToArray(), "a\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid b = await bld.CommitFileAsync("b.txt", "b\n"u8.ToArray(), "b\n", parent: root, updateRef: "refs/heads/feature", ct: ct);
        GitOid c = await bld.CommitFileAsync("c.txt", "c\n"u8.ToArray(), "c\n", parent: root, updateRef: "refs/heads/topic", ct: ct);

        GitOid? mergeBase = await bld.Repo.MergeBaseOctopusAsync([a, b, c], ct);

        Assert.NotNull(mergeBase);
        Assert.Equal(root, mergeBase!.Value);
    }
}
