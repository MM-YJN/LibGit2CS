using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.IntegrationTests.Revwalk;

/// <summary>
/// Integration tests for <see cref="GitRepository"/> graph-query methods
/// (<see cref="GitRepository.DescendantOfAsync"/>,
/// <see cref="GitRepository.AheadBehindAsync"/>,
/// <see cref="GitRepository.ReachableFromAnyAsync"/>,
/// <see cref="GitRepository.DescribeAsync"/>,
/// <see cref="GitRepository.DescribeWorkdirAsync"/>)
/// exercised end-to-end against locally-initialized non-bare repos built
/// from scratch via <see cref="RepoBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit-test project
/// (<c>CommitGraphQueriesTests</c>, <c>DescriberTests</c>) covers these
/// against <c>testrepo.zip</c>. The integration coverage was low — these
/// methods are exercised by Docker tests (clone + fetch) but never
/// against dynamically-built local repos. These tests close the gap
/// without Docker.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build. Mirrors
/// <see cref="Transports.LocalTransportTests"/> and
/// <see cref="Objects.TagIntegrationTests"/>.
/// </para>
/// </remarks>
public sealed class GraphQueryIntegrationTests
{
    // ── A. DescendantOfAsync ──────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.DescendantOfAsync"/> returns <c>true</c>
    /// when the first commit is a descendant of the second. For a 3-commit
    /// linear history [c0, c1, c2], c2 is a descendant of c0.
    /// </summary>
    [Fact]
    public async Task DescendantOf_AncestorIsDescendant_ReturnsTrue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        bool result = await bld.Repo.DescendantOfAsync(history[2], history[0], ct);

        Assert.True(result);
    }

    /// <summary>
    /// <see cref="GitRepository.DescendantOfAsync"/> returns <c>false</c>
    /// when the first commit is an ancestor of the second (the
    /// relationship is directional). c0 is not a descendant of c2.
    /// </summary>
    [Fact]
    public async Task DescendantOf_ReversedArguments_ReturnsFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        bool result = await bld.Repo.DescendantOfAsync(history[0], history[2], ct);

        Assert.False(result);
    }

    // ── B. AheadBehindAsync ────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.AheadBehindAsync"/> on the same commit
    /// returns (0, 0) — no divergence.
    /// </summary>
    [Fact]
    public async Task AheadBehind_SameCommit_ReturnsZeroZero()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        (int ahead, int behind) = await bld.Repo.AheadBehindAsync(history[2], history[2], ct);

        Assert.Equal(0, ahead);
        Assert.Equal(0, behind);
    }

    /// <summary>
    /// <see cref="GitRepository.AheadBehindAsync"/> on divergent branches
    /// returns the correct ahead/behind counts. Builds a DAG where
    /// <c>main</c> is 2 commits ahead of the fork point and <c>feature</c>
    /// is 1 commit ahead: <c>fork → A1 → A2</c> (main) and
    /// <c>fork → B1</c> (feature). Describing A2 vs B1: ahead=2, behind=1.
    /// </summary>
    [Fact]
    public async Task AheadBehind_DivergentBranches_ReturnsCorrectCounts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid fork = await bld.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "fork\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid a1 = await bld.CommitFileAsync("a1.txt", "a1\n"u8.ToArray(), "a1\n", parent: fork, updateRef: "refs/heads/main", ct: ct);
        GitOid a2 = await bld.CommitFileAsync("a2.txt", "a2\n"u8.ToArray(), "a2\n", parent: a1, updateRef: "refs/heads/main", ct: ct);
        GitOid b1 = await bld.CommitFileAsync("b1.txt", "b1\n"u8.ToArray(), "b1\n", parent: fork, updateRef: "refs/heads/feature", ct: ct);

        (int ahead, int behind) = await bld.Repo.AheadBehindAsync(a2, b1, ct);

        Assert.Equal(2, ahead);
        Assert.Equal(1, behind);
    }

    // ── C. DescribeAsync ──────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.DescribeAsync"/> on a commit with an
    /// exact (lightweight) tag match returns the tag name. Using
    /// <see cref="GitDescribeStrategy.Tags"/> so lightweight tags are
    /// considered.
    /// </summary>
    [Fact]
    public async Task Describe_ExactTagMatch_ReturnsTagName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        await bld.CreateTagAsync("v1.0", history[0], ct);

        string description = await bld.Repo.DescribeAsync(history[0], new GitDescribeOptions { Strategy = GitDescribeStrategy.Tags }, ct);

        Assert.Equal("v1.0", description);
    }

    /// <summary>
    /// <see cref="GitRepository.DescribeAsync"/> on a commit with no tags
    /// throws <see cref="GitException"/> (without
    /// <see cref="GitDescribeOptions.ShowCommitOidAsFallback"/>).
    /// </summary>
    [Fact]
    public async Task Describe_NoTagMatch_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        await Assert.ThrowsAsync<GitException>(() => bld.Repo.DescribeAsync(history[0], cancellationToken: ct));
    }

    /// <summary>
    /// <see cref="GitRepository.DescribeAsync"/> with
    /// <see cref="GitDescribeOptions.ShowCommitOidAsFallback"/> returns
    /// the abbreviated commit OID when no tag is found, instead of
    /// throwing.
    /// </summary>
    [Fact]
    public async Task Describe_Fallback_ReturnsAbbreviatedOid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        string description = await bld.Repo.DescribeAsync(history[0], new GitDescribeOptions { ShowCommitOidAsFallback = true }, ct);

        // The fallback format is the abbreviated commit OID (min 7 chars).
        string expectedAbbrev = history[0].ToString().AsSpan(0, 7).ToString();
        Assert.Equal(expectedAbbrev, description);
    }

    // ── D. DescribeWorkdirAsync ───────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.DescribeWorkdirAsync"/> on a clean workdir
    /// with a tag at HEAD returns the tag name without a dirty suffix.
    /// </summary>
    [Fact]
    public async Task DescribeWorkdir_CleanWorkdir_ReturnsTagWithoutDirtySuffix()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        await bld.CreateTagAsync("v1.0", history[0], ct);

        string description = await bld.Repo.DescribeWorkdirAsync(new GitDescribeOptions { Strategy = GitDescribeStrategy.Tags }, ct);

        Assert.Equal("v1.0", description);
        Assert.DoesNotContain("-dirty", description);
    }

    /// <summary>
    /// <see cref="GitRepository.DescribeWorkdirAsync"/> on a dirty workdir
    /// with DEFAULT options returns the tag name with no suffix — C's
    /// <c>GIT_DESCRIBE_FORMAT_OPTIONS_INIT</c> leaves <c>dirty_suffix</c>
    /// NULL (describe.c:814-815 appends only when set). With the explicit
    /// <c>DirtySuffix = "-dirty"</c> (as libgit2's own describe tests do),
    /// the suffix IS appended — but only for TRACKED changes: C runs status
    /// with flags = 0 (describe.c:730), so an untracked-only workdir stays
    /// clean.
    /// </summary>
    [Fact]
    public async Task DescribeWorkdir_UntrackedOnly_NotDirty_TrackedChangeDirty()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        await bld.CreateTagAsync("v1.0", history[0], ct);
        // Dirty the workdir with an UNTRACKED file: C's status flags = 0 do
        // not enumerate it, so the workdir is clean (no suffix).
        string workdir = bld.Repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "untracked.txt"), "dirty\n", ct);
        string @default = await bld.Repo.DescribeWorkdirAsync(new GitDescribeOptions { Strategy = GitDescribeStrategy.Tags }, ct);
        Assert.Equal("v1.0", @default);
        Assert.DoesNotContain("-dirty", @default);
        string explicitSuffix = await bld.Repo.DescribeWorkdirAsync(new GitDescribeOptions
        {
            Strategy = GitDescribeStrategy.Tags,
            DirtySuffix = "-dirty",
        }, ct);
        Assert.Equal("v1.0", explicitSuffix);
        // A TRACKED modification makes the workdir dirty (status flags = 0
        // still enumerate tracked changes).
        await File.WriteAllTextAsync(Path.Combine(workdir, "untracked.txt"), "tracked!\n", ct);
        GitIndex idx = await bld.Repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("untracked.txt", ct);
        await idx.WriteAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "untracked.txt"), "modified!\n", ct);
        string trackedDirty = await bld.Repo.DescribeWorkdirAsync(new GitDescribeOptions
        {
            Strategy = GitDescribeStrategy.Tags,
            DirtySuffix = "-dirty",
        }, ct);
        Assert.Equal("v1.0-dirty", trackedDirty);
    }

    // ── E. ReachableFromAnyAsync ───────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.ReachableFromAnyAsync"/> returns <c>true</c>
    /// when the commit is an ancestor of exactly one of several
    /// descendants. In the divergent DAG <c>root → a1 → a2</c> (main) and
    /// <c>root → b1</c> (feature), <c>a1</c> is an ancestor of <c>a2</c>
    /// but not of <c>b1</c> — a single call replaces two
    /// <see cref="GitRepository.DescendantOfAsync"/> calls.
    /// </summary>
    [Fact]
    public async Task ReachableFromAny_AncestorOfOneOfMultipleDescendants_ReturnsTrue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid root = await bld.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid a1 = await bld.CommitFileAsync("a1.txt", "a1\n"u8.ToArray(), "a1\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid a2 = await bld.CommitFileAsync("a2.txt", "a2\n"u8.ToArray(), "a2\n", parent: a1, updateRef: "refs/heads/main", ct: ct);
        GitOid b1 = await bld.CommitFileAsync("b1.txt", "b1\n"u8.ToArray(), "b1\n", parent: root, updateRef: "refs/heads/feature", ct: ct);

        bool result = await bld.Repo.ReachableFromAnyAsync(a1, [a2, b1], ct);

        Assert.True(result);
    }

    /// <summary>
    /// <see cref="GitRepository.ReachableFromAnyAsync"/> returns <c>false</c>
    /// when the commit is an ancestor of none of the descendants. In the
    /// divergent DAG <c>root → a1 → a2</c> (main) and <c>root → b1</c>
    /// (feature), <c>a2</c> is not an ancestor of <c>b1</c> or <c>root</c>.
    /// </summary>
    [Fact]
    public async Task ReachableFromAny_AncestorOfNone_ReturnsFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid root = await bld.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid a1 = await bld.CommitFileAsync("a1.txt", "a1\n"u8.ToArray(), "a1\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid a2 = await bld.CommitFileAsync("a2.txt", "a2\n"u8.ToArray(), "a2\n", parent: a1, updateRef: "refs/heads/main", ct: ct);
        GitOid b1 = await bld.CommitFileAsync("b1.txt", "b1\n"u8.ToArray(), "b1\n", parent: root, updateRef: "refs/heads/feature", ct: ct);

        bool result = await bld.Repo.ReachableFromAnyAsync(a2, [b1, root], ct);

        Assert.False(result);
    }

    /// <summary>
    /// <see cref="GitRepository.ReachableFromAnyAsync"/> returns <c>true</c>
    /// when the commit is a common ancestor of all the descendants. In the
    /// divergent DAG <c>root → a1 → a2</c> (main) and <c>root → b1</c>
    /// (feature), <c>root</c> is an ancestor of both <c>a2</c> and <c>b1</c>.
    /// </summary>
    [Fact]
    public async Task ReachableFromAny_AncestorOfAllDescendants_ReturnsTrue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid root = await bld.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid a1 = await bld.CommitFileAsync("a1.txt", "a1\n"u8.ToArray(), "a1\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid a2 = await bld.CommitFileAsync("a2.txt", "a2\n"u8.ToArray(), "a2\n", parent: a1, updateRef: "refs/heads/main", ct: ct);
        GitOid b1 = await bld.CommitFileAsync("b1.txt", "b1\n"u8.ToArray(), "b1\n", parent: root, updateRef: "refs/heads/feature", ct: ct);

        bool result = await bld.Repo.ReachableFromAnyAsync(root, [a2, b1], ct);

        Assert.True(result);
    }

    /// <summary>
    /// <see cref="GitRepository.ReachableFromAnyAsync"/> with an empty
    /// descendants list returns <c>false</c> — no descendant can make the
    /// commit reachable. Matches the <c>length == 0</c> short-circuit in
    /// the C reference (graph.c:199-200).
    /// </summary>
    [Fact]
    public async Task ReachableFromAny_EmptyDescendants_ReturnsFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        bool result = await bld.Repo.ReachableFromAnyAsync(history[0], [], ct);

        Assert.False(result);
    }

    /// <summary>
    /// <see cref="GitRepository.ReachableFromAnyAsync"/> returns <c>true</c>
    /// when the commit is itself one of the descendants, without walking.
    /// Matches the equality short-circuit in the C reference
    /// (graph.c:202-205).
    /// </summary>
    [Fact]
    public async Task ReachableFromAny_CommitInDescendants_ReturnsTrue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);

        bool result = await bld.Repo.ReachableFromAnyAsync(history[0], [history[1], history[0]], ct);

        Assert.True(result);
    }

    /// <summary>
    /// <see cref="GitRepository.ReachableFromAnyAsync"/> over a criss-cross
    /// DAG whose tips have two merge bases: the merge commit <c>m1</c> is
    /// an ancestor of <c>tip1</c> only (not of <c>tip2</c>), so the
    /// multi-descendant walk must find it among the multiple merge bases.
    /// </summary>
    [Fact]
    public async Task ReachableFromAny_CrissCrossDag_AncestorOfOneTip_ReturnsTrue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        (_, _, _, GitOid m1, _, GitOid tip1, GitOid tip2) = await bld.BuildCrissCrossDagAsync(ct);

        bool result = await bld.Repo.ReachableFromAnyAsync(m1, [tip1, tip2], ct);

        Assert.True(result);
    }

    /// <summary>
    /// <see cref="GitRepository.ReachableFromAnyAsync"/> throws
    /// <see cref="GitException"/> when the commit does not exist in the
    /// repository. Matches the <c>git_revwalk__commit_lookup</c> failure
    /// path in the C reference (graph.c:215-218, 226-229).
    /// </summary>
    [Fact]
    public async Task ReachableFromAny_NotFoundCommit_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);
        var missing = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), bld.Repo.ObjectFormat);

        await Assert.ThrowsAsync<GitException>(
            () => bld.Repo.ReachableFromAnyAsync(missing, [history[1]], ct));
    }
}
