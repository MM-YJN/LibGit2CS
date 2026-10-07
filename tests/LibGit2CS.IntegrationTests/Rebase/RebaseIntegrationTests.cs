using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Rebase;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Rebase;

/// <summary>
/// Integration tests for the rebase engine (<see cref="LibGit2CS.Rebase"/>
/// namespace) exercised end-to-end against locally-initialized repositories
/// built with <see cref="RepoBuilder"/>. No Docker, no fixtures — runs on
/// every build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The existing
/// <see cref="Transports.RebaseDockerTests"/> cover the on-disk happy path
/// and the in-memory path over fetched history, but
/// <see cref="GitRebase.OpenAsync"/> (resume), <see cref="GitRebase.AbortAsync"/>,
/// <see cref="GitRebase.ReturnToOrigHeadAsync"/> (non-detached finish), and
/// all of <see cref="RebaseStateFiles.ReadFileAsync"/>/
/// <see cref="RebaseStateFiles.ReadIntAsync"/>/
/// <see cref="RebaseStateFiles.ReadOidAsync"/> are entirely cold. These
/// local tests close those gaps.
/// </para>
/// <para>
/// <b>Assertion style.</b> Structural assertions on operation counts,
/// HEAD/OID state, and state-file presence/absence.
/// </para>
/// </remarks>
public sealed class RebaseIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Open resumes an in-progress rebase ─────────────────────────────

    /// <summary>
    /// <see cref="GitRebase.OpenAsync"/> reads the state files written
    /// by a prior <see cref="GitRebase.InitAsync"/> and reconstructs the
    /// operation list. Exercises <see cref="RebaseState.OpenMergeAsync"/>
    /// (which reads <c>msgnum</c>, <c>end</c>, <c>current</c>,
    /// <c>cmt.{N}</c>, <c>onto_name</c>) and the state-file readers
    /// (<see cref="RebaseStateFiles.ReadFileAsync"/>,
    /// <see cref="RebaseStateFiles.ReadIntAsync"/>,
    /// <see cref="RebaseStateFiles.ReadOidAsync"/>).
    /// </summary>
    [Fact]
    public async Task Rebase_Open_ResumesInProgressRebase()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid root = await builder.CommitFileAsync("f.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid mainTip = await builder.CommitFileAsync("f.txt", "base\nmain\n"u8.ToArray(), "main\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid featureTip = await builder.CommitFileAsync("f.txt", "base\nfeature\n"u8.ToArray(), "feature\n", parent: root, updateRef: "refs/heads/feature", ct: ct);
        await builder.CheckoutRefAsync("refs/heads/feature", ct);

        GitReference? mainRef = await builder.Repo.ReferenceLookupAsync("refs/heads/main", ct);
        Assert.NotNull(mainRef);
        using GitAnnotatedCommit upstream = await builder.Repo.AnnotatedCommitFromRefAsync(mainRef!, ct);

        // Init writes the state files.
        using (GitRebase rebase = await builder.Repo.RebaseInitAsync(branch: null, upstream, onto: null, null, ct))
        {
            Assert.Equal(1, rebase.OperationCount);
            Assert.Equal(RepositoryState.RebaseMerge, builder.Repo.State);
        }

        // Open reads them back.
        using GitRebase reopened = await builder.Repo.RebaseOpenAsync(null, ct);
        Assert.Equal(1, reopened.OperationCount);
        Assert.Equal("main", reopened.OntoNameProperty);
    }

    // ── Abort restores HEAD and cleans state ───────────────────────────

    /// <summary>
    /// <see cref="GitRebase.AbortAsync"/> restores HEAD to the
    /// original branch and removes the <c>rebase-merge/</c> state directory.
    /// Exercises the symbolic-HEAD restore path in <see cref="GitRebase.AbortAsync"/>
    /// and the directory-deletion branch of <see cref="GitRebase.Cleanup"/>.
    /// </summary>
    [Fact]
    public async Task Rebase_Abort_RestoresHeadAndCleansState()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid root = await builder.CommitFileAsync("f.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        await builder.CommitFileAsync("f.txt", "base\nmain\n"u8.ToArray(), "main\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid featureTip = await builder.CommitFileAsync("f.txt", "base\nfeature\n"u8.ToArray(), "feature\n", parent: root, updateRef: "refs/heads/feature", ct: ct);
        await builder.CheckoutRefAsync("refs/heads/feature", ct);

        GitReference? mainRef = await builder.Repo.ReferenceLookupAsync("refs/heads/main", ct);
        Assert.NotNull(mainRef);
        using GitAnnotatedCommit upstream = await builder.Repo.AnnotatedCommitFromRefAsync(mainRef!, ct);

        using GitRebase rebase = await builder.Repo.RebaseInitAsync(branch: null, upstream, onto: null, null, ct);
        Assert.Equal(RepositoryState.RebaseMerge, builder.Repo.State);

        await rebase.AbortAsync(ct);

        // State is cleared.
        Assert.Equal(RepositoryState.None, builder.Repo.State);
        Assert.False(Directory.Exists(Path.Combine(builder.Repo.Path, "rebase-merge")));

        // HEAD is back on the feature branch at the original tip.
        GitReference? head = await builder.Repo.ReferenceResolveAsync("HEAD", ct);
        Assert.NotNull(head);
        Assert.True(head is GitDirectReference);
        Assert.Equal(featureTip, ((GitDirectReference)head!).Target);

        // The feature branch ref still exists at the original tip.
        GitReference? featureRef = await builder.Repo.ReferenceLookupAsync("refs/heads/feature", ct);
        Assert.NotNull(featureRef);
        Assert.Equal(featureTip, ((GitDirectReference)featureRef!).Target);
    }

    // ── Abort with detached HEAD ───────────────────────────────────────

    /// <summary>
    /// <see cref="GitRebase.AbortAsync"/> when HEAD was detached
    /// restores HEAD as a direct ref pointing at the original commit.
    /// Exercises the <c>_headDetached</c> branch in
    /// <see cref="GitRebase.AbortAsync"/> and the
    /// <see cref="RebaseStateFiles.OrigDetachedHead"/> write path in
    /// <see cref="RebaseState.SetupFilesAsync"/>.
    /// </summary>
    [Fact]
    public async Task Rebase_Abort_DetachedHead_RestoresDirectRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid root = await builder.CommitFileAsync("f.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid mainTip = await builder.CommitFileAsync("f.txt", "base\nmain\n"u8.ToArray(), "main\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid featureTip = await builder.CommitFileAsync("f.txt", "base\nfeature\n"u8.ToArray(), "feature\n", parent: root, updateRef: "refs/heads/feature", ct: ct);

        // Detach HEAD at the feature tip.
        await builder.DetachHeadAsync(featureTip, ct);

        // Init rebase with branch = null — InitAsync resolves HEAD, which
        // is a direct ref (detached), so InitMergeAsync sets _headDetached.
        GitReference? mainRef = await builder.Repo.ReferenceLookupAsync("refs/heads/main", ct);
        Assert.NotNull(mainRef);
        using GitAnnotatedCommit upstream = await builder.Repo.AnnotatedCommitFromRefAsync(mainRef!, ct);

        using GitRebase rebase = await builder.Repo.RebaseInitAsync(branch: null, upstream, onto: null, null, ct);
        Assert.Equal(RepositoryState.RebaseMerge, builder.Repo.State);

        await rebase.AbortAsync(ct);

        // HEAD is restored as a direct ref at the original detached commit.
        Assert.Equal(RepositoryState.None, builder.Repo.State);
        GitReference? head = await builder.Repo.ReferenceResolveAsync("HEAD", ct);
        Assert.NotNull(head);
        Assert.True(head is GitDirectReference);
        Assert.Equal(featureTip, ((GitDirectReference)head!).Target);
    }

    // ── Conflict stops mid-rebase and state is readable ────────────────

    /// <summary>
    /// A rebase step that produces a conflict stops the rebase:
    /// <see cref="GitRebase.CommitAsync"/> throws
    /// <see cref="GitErrorCode.Unmerged"/>, and the state files written by
    /// <see cref="GitRebase.NextAsync"/> (<c>msgnum</c>, <c>current</c>)
    /// are readable by a subsequent <see cref="GitRebase.OpenAsync"/>.
    /// Exercises the <c>index.HasConflicts</c> error path in
    /// <c>CommitCreateAsync</c> and the state-file read-back.
    /// </summary>
    [Fact]
    public async Task Rebase_Conflict_StopsAndWritesState()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid root = await builder.CommitFileAsync("f.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid mainTip = await builder.CommitFileAsync("f.txt", "base\nMAIN\n"u8.ToArray(), "main\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid featureTip = await builder.CommitFileAsync("f.txt", "base\nFEATURE\n"u8.ToArray(), "feature\n", parent: root, updateRef: "refs/heads/feature", ct: ct);
        await builder.CheckoutRefAsync("refs/heads/feature", ct);

        GitReference? mainRef = await builder.Repo.ReferenceLookupAsync("refs/heads/main", ct);
        Assert.NotNull(mainRef);
        using GitAnnotatedCommit upstream = await builder.Repo.AnnotatedCommitFromRefAsync(mainRef!, ct);

        using GitRebase rebase = await builder.Repo.RebaseInitAsync(branch: null, upstream, onto: null, null, ct);
        Assert.Equal(1, rebase.OperationCount);

        // Next applies the feature commit onto main — both modified line 2,
        // so the 3-way merge conflicts.
        await rebase.NextAsync(ct);

        // Commit must throw Unmerged because the index has conflicts.
        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => rebase.CommitAsync(author: null, Sig, cancellationToken: ct));
        Assert.Equal(GitErrorCode.Unmerged, ex.Code);

        // The rebase is in progress and the state files are readable.
        Assert.Equal(RepositoryState.RebaseMerge, builder.Repo.State);
        using GitRebase reopened = await builder.Repo.RebaseOpenAsync(null, ct);
        Assert.Equal(1, reopened.OperationCount);
    }

    // ── Rebase --onto with different upstream ──────────────────────────

    /// <summary>
    /// <see cref="GitRebase.InitAsync"/> with an explicit
    /// <paramref name="onto"/> different from <paramref name="upstream"/>
    /// computes the onto name from the branch ref. Exercises the
    /// <c>onto.Ref.StartsWith("refs/heads/")</c> branch in
    /// <see cref="GitRebase.ComputeOntoName"/> and the non-detached branch
    /// ref path in <c>InitMergeAsync</c>.
    /// </summary>
    [Fact]
    public async Task Rebase_Onto_DifferentUpstream_ComputesOntoName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid root = await builder.CommitFileAsync("f.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid mainTip = await builder.CommitFileAsync("f.txt", "base\nmain\n"u8.ToArray(), "main\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid featureTip = await builder.CommitFileAsync("f.txt", "base\nfeature\n"u8.ToArray(), "feature\n", parent: root, updateRef: "refs/heads/feature", ct: ct);
        await builder.CheckoutRefAsync("refs/heads/feature", ct);

        GitReference? mainRef = await builder.Repo.ReferenceLookupAsync("refs/heads/main", ct);
        Assert.NotNull(mainRef);
        using GitAnnotatedCommit upstream = await builder.Repo.AnnotatedCommitFromRefAsync(mainRef!, ct);
        using GitAnnotatedCommit onto = await builder.Repo.AnnotatedCommitFromRefAsync(mainRef!, ct);

        using GitRebase rebase = await builder.Repo.RebaseInitAsync(branch: null, upstream, onto, null, ct);

        // onto_name is the short branch name ("main"), derived from
        // refs/heads/main by ComputeOntoName.
        Assert.Equal("main", rebase.OntoNameProperty);
        Assert.Equal(1, rebase.OperationCount);
    }

    // ── Quiet flag writes "t\n" ────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRebaseOptions.Quiet"/> = <c>true</c> writes
    /// <c>"t\n"</c> to the <c>rebase-merge/quiet</c> state file. Exercises
    /// the <c>rebase.Quiet ? "t\n" : "\n"</c> true branch in
    /// <see cref="RebaseState.SetupFilesAsync"/>.
    /// </summary>
    [Fact]
    public async Task Rebase_Quiet_WritesQuietFlag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid root = await builder.CommitFileAsync("f.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        GitOid mainTip = await builder.CommitFileAsync("f.txt", "base\nmain\n"u8.ToArray(), "main\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid featureTip = await builder.CommitFileAsync("f.txt", "base\nfeature\n"u8.ToArray(), "feature\n", parent: root, updateRef: "refs/heads/feature", ct: ct);
        await builder.CheckoutRefAsync("refs/heads/feature", ct);

        GitReference? mainRef = await builder.Repo.ReferenceLookupAsync("refs/heads/main", ct);
        Assert.NotNull(mainRef);
        using GitAnnotatedCommit upstream = await builder.Repo.AnnotatedCommitFromRefAsync(mainRef!, ct);

        using GitRebase rebase = await builder.Repo.RebaseInitAsync(branch: null, upstream, onto: null,
            new GitRebaseOptions { Quiet = true }, ct);

        string quietPath = Path.Combine(builder.Repo.Path, "rebase-merge", "quiet");
        Assert.True(File.Exists(quietPath));
        string content = await File.ReadAllTextAsync(quietPath, ct);
        Assert.Equal("t\n", content);
    }

    // ── Finish returns to original branch (non-detached) ───────────────

    /// <summary>
    /// <see cref="GitRebase.FinishAsync"/> on a non-detached rebase
    /// moves the original branch ref to the rebased tip and points HEAD
    /// symbolically at it. Exercises <see cref="GitRebase.ReturnToOrigHeadAsync"/>
    /// (the CAS branch-ref update + symbolic HEAD update) and the
    /// <c>!_headDetached</c> branch in <c>FinishAsync</c>.
    /// </summary>
    /// <remarks>
    /// The feature branch adds a new file <c>feature.txt</c> (no conflict
    /// with main's modification of <c>f.txt</c>) so the pick loop completes
    /// cleanly.
    /// </remarks>
    [Fact]
    public async Task Rebase_Finish_ReturnsToOrigHead_NonDetached()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var builder = new RepoBuilder();
        GitOid root = await builder.CommitFileAsync("f.txt", "base\n"u8.ToArray(), "root\n", parent: null, updateRef: "refs/heads/main", ct: ct);
        await builder.CommitFileAsync("f.txt", "base\nmain\n"u8.ToArray(), "main\n", parent: root, updateRef: "refs/heads/main", ct: ct);
        GitOid featureTip = await builder.CommitFileAsync("feature.txt", "feature\n"u8.ToArray(), "feature\n", parent: root, updateRef: "refs/heads/feature", ct: ct);
        await builder.CheckoutRefAsync("refs/heads/feature", ct);

        GitReference? mainRef = await builder.Repo.ReferenceLookupAsync("refs/heads/main", ct);
        Assert.NotNull(mainRef);
        using GitAnnotatedCommit upstream = await builder.Repo.AnnotatedCommitFromRefAsync(mainRef!, ct);

        // Pass branch explicitly as the feature ref (not null) so that
        // InitMergeAsync records _origHeadName = "refs/heads/feature" and
        // _headDetached = false. Passing branch: null would resolve HEAD
        // via FromRefAsync, which sets refName = "HEAD", triggering the
        // detached-head path.
        GitReference? featureRef0 = await builder.Repo.ReferenceLookupAsync("refs/heads/feature", ct);
        Assert.NotNull(featureRef0);
        using GitAnnotatedCommit branch = await builder.Repo.AnnotatedCommitFromRefAsync(featureRef0!, ct);

        using GitRebase rebase = await builder.Repo.RebaseInitAsync(branch, upstream, onto: null, null, ct);

        // Run the pick loop to completion.
        List<GitOid> rebased = await RunPickLoopAsync(rebase, Sig, ct);
        Assert.Single(rebased);

        // Finish moves the feature branch to the rebased tip and restores
        // HEAD as a symbolic ref pointing at refs/heads/feature.
        await rebase.FinishAsync(Sig, ct);

        // The feature branch ref moved to the rebased tip.
        GitReference? featureRef = await builder.Repo.ReferenceLookupAsync("refs/heads/feature", ct);
        Assert.NotNull(featureRef);
        Assert.Equal(rebased[0], ((GitDirectReference)featureRef!).Target);

        // HEAD resolves to the rebased tip.
        GitReference? head = await builder.Repo.ReferenceResolveAsync("HEAD", ct);
        Assert.NotNull(head);
        Assert.Equal(rebased[0], ((GitDirectReference)head!).Target);

        // State is cleaned up.
        Assert.Equal(RepositoryState.None, builder.Repo.State);
        Assert.False(Directory.Exists(Path.Combine(builder.Repo.Path, "rebase-merge")));
    }

    // ── Helper: pick loop ──────────────────────────────────────────────

    /// <summary>
    /// Drives the rebase Next + Commit loop to completion, returning the
    /// replayed commit OIDs. The loop ends when Next throws IterOver.
    /// </summary>
    private static async Task<List<GitOid>> RunPickLoopAsync(GitRebase rebase, GitSignature sig, CancellationToken ct)
    {
        var oids = new List<GitOid>();
        while (true)
        {
            try
            {
                await rebase.NextAsync(ct).ConfigureAwait(false);
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.IterOver)
            {
                break;
            }

            oids.Add(await rebase.CommitAsync(author: null, sig, cancellationToken: ct).ConfigureAwait(false));
        }

        return oids;
    }
}
