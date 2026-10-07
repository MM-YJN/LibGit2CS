using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.IntegrationTests.Revwalk;

/// <summary>
/// Integration tests for <see cref="GitRevWalker"/> exercised end-to-end
/// against locally-initialized non-bare repos built from scratch via
/// <see cref="RepoBuilder"/>. Covers sort modes, push/hide variants,
/// ranges, first-parent simplification, reset, hide callbacks, and edge
/// cases — mirroring scenarios from <c>tests/libgit2/revwalk/basic.c</c>
/// and <c>tests/libgit2/revwalk/simplify.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit-test project (<c>RevWalkerTests</c>)
/// runs against a single fixed <c>testrepo.zip</c> and asserts behavioral
/// invariants (topological order property, time ordering, count constraints)
/// but not exact OID sequences over dynamically-built multi-commit
/// topologies. The only integration coverage came from
/// <c>SshTransportDockerTests.RevWalk_OverFetchedHistory_YieldsOrderedCommits</c>,
/// which requires Docker + SSH. These tests close the gap without Docker.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build. Mirrors
/// <see cref="Transports.LocalTransportTests"/> and
/// <see cref="Objects.TagIntegrationTests"/>.
/// </para>
/// </remarks>
public sealed class WalkerIntegrationTests
{
    // ── A. Sort modes ──────────────────────────────────────────────────

    /// <summary>
    /// Walk a 3-commit linear history with
    /// <see cref="GitSortMode.Topological"/>. Asserts the topological
    /// invariant (every parent appears after its child) and that the tip
    /// is yielded first.
    /// </summary>
    [Fact]
    public async Task Walk_Topological_YieldsChildrenBeforeParents()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        await walker.PushHeadAsync(cancellationToken: ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Equal(history.Reverse().ToArray(), commits.ToArray());
        AssertTopologicalOrder(bld.Repo, commits, ct);
    }

    /// <summary>
    /// Walk with <see cref="GitSortMode.Time"/>. For a linear history with
    /// strictly increasing timestamps, time-sort yields the same order as
    /// topological. Asserts the time invariant (non-increasing timestamps).
    /// </summary>
    [Fact]
    public async Task Walk_TimeSort_NewestFirst()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Time;
        await walker.PushHeadAsync(cancellationToken: ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Equal(history.Reverse().ToArray(), commits.ToArray());
        AssertTimeOrder(bld.Repo, commits, ct);
    }

    /// <summary>
    /// Walk with <see cref="GitSortMode.Time"/> | <see cref="GitSortMode.Reverse"/>
    /// and compare to the non-reversed time walk. The reverse walk must
    /// produce the exact reverse sequence.
    /// </summary>
    [Fact]
    public async Task Walk_ReverseSort_FlipsOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        GitOid[] forward;
        using (GitRevWalker w1 = bld.Repo.NewRevWalker())
        {
            w1.Sort = GitSortMode.Time;
            await w1.PushHeadAsync(cancellationToken: ct);
            forward = (await w1.WalkAsync(ct).ToListAsync(cancellationToken: ct)).ToArray();
        }

        GitOid[] reverse;
        using (GitRevWalker w2 = bld.Repo.NewRevWalker())
        {
            w2.Sort = GitSortMode.Time | GitSortMode.Reverse;
            await w2.PushHeadAsync(cancellationToken: ct);
            reverse = (await w2.WalkAsync(ct).ToListAsync(cancellationToken: ct)).ToArray();
        }

        Assert.Equal(forward, reverse.Reverse().ToArray());
    }

    /// <summary>
    /// Walk with <see cref="GitSortMode.Topological"/> | <see cref="GitSortMode.Time"/>.
    /// For linear history this matches plain topological; the combined mode
    /// matters for merge DAGs where time breaks ties. Asserts both
    /// invariants hold.
    /// </summary>
    [Fact]
    public async Task Walk_TopoTimeSort_ProducesValidOrdering()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological | GitSortMode.Time;
        await walker.PushHeadAsync(cancellationToken: ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Equal(history.Reverse().ToArray(), commits.ToArray());
        AssertTopologicalOrder(bld.Repo, commits, ct);
        AssertTimeOrder(bld.Repo, commits, ct);
    }

    // ── B. Push / Hide variants ───────────────────────────────────────

    /// <summary>
    /// <see cref="GitRevWalker.PushAsync"/> by OID yields the pushed
    /// commit and all its ancestors.
    /// </summary>
    [Fact]
    public async Task Walk_PushAsync_ByOid_YieldsAncestry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        await walker.PushAsync(history[2], ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Equal(history.Reverse().ToArray(), commits.ToArray());
    }

    /// <summary>
    /// <see cref="GitRevWalker.PushHeadAsync"/> walks HEAD's ancestry.
    /// After <see cref="RepoBuilder.BuildLinearHistoryAsync"/>, HEAD is
    /// <c>refs/heads/main</c> at the tip.
    /// </summary>
    [Fact]
    public async Task Walk_PushHeadAsync_YieldsHeadAncestry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        await walker.PushHeadAsync(cancellationToken: ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Equal(history.Reverse().ToArray(), commits.ToArray());
    }

    /// <summary>
    /// <see cref="GitRevWalker.PushRefAsync"/> resolves a fully-qualified
    /// ref name and walks its ancestry.
    /// </summary>
    [Fact]
    public async Task Walk_PushRefAsync_ResolvesRefName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        await walker.PushRefAsync("refs/heads/main", cancellationToken: ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Equal(history.Reverse().ToArray(), commits.ToArray());
    }

    /// <summary>
    /// <see cref="GitRevWalker.PushGlobAsync"/> with <c>"heads/*"</c>
    /// matches all branches and walks their combined ancestry. Builds a
    /// DAG with two branches off a shared root and asserts both tips
    /// appear and the root is included exactly once.
    /// </summary>
    [Fact]
    public async Task Walk_PushGlobAsync_MatchesMultipleBranches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid root = await bld.CommitFileAsync("base.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid a = await bld.CommitFileAsync("a.txt", "a\n"u8.ToArray(), "a\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid b = await bld.CommitFileAsync("b.txt", "b\n"u8.ToArray(), "b\n", parent: root, updateRef: "refs/heads/feature", ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        await walker.PushGlobAsync("heads/*", cancellationToken: ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        // Both tips and the root must be present.
        Assert.Contains(a, commits);
        Assert.Contains(b, commits);
        Assert.Contains(root, commits);
        // Root must appear exactly once (deduplicated).
        Assert.Single(commits, c => c == root);
    }

    /// <summary>
    /// <see cref="GitRevWalker.HideAsync"/> removes a commit and all its
    /// ancestors from the walk. Hiding the root of a 3-commit history
    /// yields only the two children.
    /// </summary>
    [Fact]
    public async Task Walk_HideAsync_RemovesCommitAndAncestors()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        await walker.PushHeadAsync(cancellationToken: ct);
        await walker.HideAsync(history[1], ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        // Hiding the middle commit removes it and its ancestor (root).
        Assert.Single(commits);
        Assert.Equal(history[2], commits[0]);
    }

    /// <summary>
    /// <see cref="GitRevWalker.HideRefAsync"/> hides the ancestry of a
    /// ref. Hiding <c>refs/heads/main</c> on a 3-commit history walked
    /// from HEAD (which is main) yields nothing.
    /// </summary>
    [Fact]
    public async Task Walk_HideRefAsync_RemovesRefAncestry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        await walker.PushHeadAsync(cancellationToken: ct);
        await walker.HideRefAsync("refs/heads/main", cancellationToken: ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Empty(commits);
    }

    // ── C. Ranges ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRevWalker.PushRangeAsync"/> with <c>"A..B"</c>
    /// (exclusive range) walks B's ancestry excluding A and its ancestors.
    /// For a 3-commit history [c0, c1, c2], <c>c0..c2</c> yields [c2, c1].
    /// </summary>
    [Fact]
    public async Task Walk_PushRangeAsync_ExcludesLeftExclusive()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        await walker.PushRangeAsync($"{history[0]}..{history[2]}", cancellationToken: ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Equal([history[2], history[1]], commits);
    }

    /// <summary>
    /// <see cref="GitRevWalker.PushRangeAsync"/> with a bare spec (no
    /// <c>..</c>) throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.Invalid"/>.
    /// </summary>
    [Fact]
    public async Task Walk_PushRangeAsync_BareSpec_ThrowsInvalidSpec()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        await bld.BuildLinearHistoryAsync(1, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        await Assert.ThrowsAsync<GitException>(() => walker.PushRangeAsync("HEAD", cancellationToken: ct));
    }

    /// <summary>
    /// <see cref="GitRevWalker.PushRangeAsync"/> with symmetric
    /// difference <c>"A...B"</c> (three dots) throws
    /// <see cref="GitException"/> with <see cref="GitErrorCode.Invalid"/>
    /// — symmetric-difference ranges are not supported by revwalk
    /// (matches <c>git_revwalk_push_range</c> behavior).
    /// </summary>
    [Fact]
    public async Task Walk_PushRangeAsync_SymmetricDiff_ThrowsInvalidSpec()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        await Assert.ThrowsAsync<GitException>(() => walker.PushRangeAsync($"{history[0]}...{history[1]}", cancellationToken: ct));
    }

    // ── D. SimplifyFirstParent ─────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRevWalker.SimplifyFirstParent"/> restricts the walk
    /// to the first-parent chain. Builds a merge DAG (root → A, root → B,
    /// A+B → M) and asserts the first-parent walk yields [M, A, root],
    /// skipping the B branch.
    /// </summary>
    [Fact]
    public async Task Walk_SimplifyFirstParent_FollowsOnlyFirstParent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        (GitOid root, GitOid a, GitOid b, GitOid merge) = await bld.BuildMergeDagAsync(ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        walker.SimplifyFirstParent();
        await walker.PushAsync(merge, ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        // First-parent chain: merge → a → root. B branch is skipped.
        Assert.Equal([merge, a, root], commits);
        Assert.DoesNotContain(b, commits);
    }

    // ── E. Reset ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRevWalker.Reset"/> clears all walk state, allowing a
    /// fresh walk. After pushing HEAD and walking partway, reset + re-push
    /// must yield the full ancestry again.
    /// </summary>
    [Fact]
    public async Task Walk_Reset_AfterPartialWalk_RestartsFromBeginning()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        await walker.PushHeadAsync(cancellationToken: ct);

        // Walk partway — take only the first commit.
        await using IAsyncEnumerator<GitOid> e = walker.WalkAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await e.MoveNextAsync());
        Assert.Equal(history[2], e.Current);

        // Reset and re-walk — all 3 commits must reappear.
        walker.Reset();
        await walker.PushHeadAsync(cancellationToken: ct);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Equal(history.Reverse().ToArray(), commits.ToArray());
    }

    // ── F. Hide callbacks ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRevWalker.AddHideCallback"/> with a callback that
    /// hides a specific commit excludes that commit and its ancestors
    /// from the walk. Mirrors <c>hidecb.c::hide_some_commits</c>.
    /// </summary>
    [Fact]
    public async Task Walk_AddHideCallback_HidesMatchingCommits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        await walker.PushHeadAsync(cancellationToken: ct);

        // Hide the middle commit — the callback returns true for it.
        walker.AddHideCallback(oid => oid == history[1]);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        // Middle commit and its ancestor (root) are hidden; only tip remains.
        Assert.Single(commits);
        Assert.Equal(history[2], commits[0]);
    }

    /// <summary>
    /// <see cref="GitRevWalker.AddHideCallback"/> with a callback that
    /// hides every commit yields an empty walk. Mirrors
    /// <c>hidecb.c::hide_all_cb</c>.
    /// </summary>
    [Fact]
    public async Task Walk_AddHideCallback_HideAll_YieldsEmpty()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        await walker.PushHeadAsync(cancellationToken: ct);
        walker.AddHideCallback(_ => true);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Empty(commits);
    }

    /// <summary>
    /// <see cref="GitRevWalker.AddHideCallback"/> with <c>null</c>
    /// removes the hide callback, restoring the full walk. Mirrors
    /// <c>hidecb.c::unset_cb_before_walk</c>.
    /// </summary>
    [Fact]
    public async Task Walk_AddHideCallback_Null_RestoresFullWalk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;
        await walker.PushHeadAsync(cancellationToken: ct);

        // Install hide-everything, then unset it before walking.
        walker.AddHideCallback(_ => true);
        walker.AddHideCallback(null);

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Equal(history.Reverse().ToArray(), commits.ToArray());
    }

    // ── G. Edge cases ─────────────────────────────────────────────────

    /// <summary>
    /// A walker with no pushes yields no commits. Mirrors
    /// <c>basic.c</c>'s implicit "no roots pushed" case.
    /// </summary>
    [Fact]
    public async Task Walk_NoPushes_YieldsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        await bld.BuildLinearHistoryAsync(3, ct: ct);

        using GitRevWalker walker = bld.Repo.NewRevWalker();

        List<GitOid> commits = await walker.WalkAsync(ct).ToListAsync(cancellationToken: ct);

        Assert.Empty(commits);
    }

    /// <summary>
    /// Pushing a non-commit OID (a tree) throws
    /// <see cref="GitException"/>. Mirrors <c>basic.c::disallow_non_commit</c>.
    /// </summary>
    [Fact]
    public async Task Walk_PushNonCommit_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        await bld.BuildLinearHistoryAsync(1, ct: ct);

        // Resolve HEAD's tree OID.
        GitReference? headRef = await bld.Repo.ReferenceResolveAsync("HEAD", ct);
        Assert.NotNull(headRef);
        GitOid headOid = ((GitDirectReference)headRef!).Target;
        Commit head = await bld.Repo.ObjectLookupAsync<Commit>(headOid, ct) ?? throw new InvalidOperationException("HEAD missing");
        GitOid treeOid = head.Tree;
        head.Dispose();

        using GitRevWalker walker = bld.Repo.NewRevWalker();
        await Assert.ThrowsAsync<GitException>(() => walker.PushAsync(treeOid, ct));
    }

    // ── Invariant helpers ─────────────────────────────────────────────

    /// <summary>
    /// Asserts the topological invariant: every parent that appears in
    /// the list must appear after its child.
    /// </summary>
    private static void AssertTopologicalOrder(GitRepository repo, IReadOnlyList<GitOid> commits, CancellationToken ct)
    {
        var positions = commits.Select((oid, idx) => (oid, idx)).ToDictionary(x => x.oid, x => x.idx);
        for (int i = 0; i < commits.Count; i++)
        {
            Commit? commit = repo.ObjectLookupAsync<Commit>(commits[i], ct).GetAwaiter().GetResult();
            Assert.NotNull(commit);
            foreach (GitOid parentId in commit!.Parents)
            {
                if (positions.TryGetValue(parentId, out int parentIdx))
                {
                    Assert.True(parentIdx > i, $"parent {parentId} must come after child at index {i}");
                }
            }

            commit.Dispose();
        }
    }

    /// <summary>
    /// Asserts the time-order invariant: timestamps are non-increasing.
    /// </summary>
    private static void AssertTimeOrder(GitRepository repo, IReadOnlyList<GitOid> commits, CancellationToken ct)
    {
        long prevTime = long.MaxValue;
        foreach (GitOid oid in commits)
        {
            Commit? commit = repo.ObjectLookupAsync<Commit>(oid, ct).GetAwaiter().GetResult();
            Assert.NotNull(commit);
            Assert.True(commit!.Time.Seconds <= prevTime, $"time sort: {commit.Time.Seconds} > {prevTime}");
            prevTime = commit.Time.Seconds;
            commit.Dispose();
        }
    }
}
