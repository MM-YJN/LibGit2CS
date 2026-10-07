using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Docker-based integration tests for the merge engine
/// (<see cref="GitRepository.MergeAsync"/> + <see cref="GitRepository.MergeAnalyzeAsync"/>)
/// exercised end-to-end over history fetched from a real OpenSSH+git
/// container via <see cref="SshGitDockerFixture"/>. The merge machinery
/// itself is local; the Docker fixture's value here is producing real
/// multi-commit, multi-branch, merge-base-bearing history via server-side
/// <c>git</c> (<see cref="SshGitDockerFixture.ExecAsync"/>) that the client
/// then fetches over SSH and operates on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests exist.</b> The unit-test project covers the merge
/// algorithms against extracted golden fixtures, but the
/// <see cref="LibGit2CS.Merge"/> namespace had <c>0%</c> integration
/// coverage — no end-to-end path through fetch → object-db population →
/// merge-base walk → 3-way diff → merge drivers → checkout → state-file
/// writes. These tests close that gap by cloning from the fixture, optionally
/// diverging the client, then merging a fetched remote-tracking ref.
/// </para>
/// <para>
/// <b>Gating.</b> All tests are skipped when Docker is not reachable
/// (<see cref="SshGitDockerFixture.SkipIfDockerNotAvailable"/>).
/// </para>
/// <para>
/// <b>Auth.</b> Uses password auth with a permissive
/// <c>CertificateCheck</c> (hostkey verification is covered separately by
/// <see cref="SshTransportDockerTests"/>).
/// </para>
/// </remarks>
public sealed class MergeDockerTests : IDisposable
{
    private readonly LoggerFactory _loggerFactory;
    private readonly AlpineNoKeyImageFixture _alpine;

    public MergeDockerTests(AlpineNoKeyImageFixture alpine, ITestOutputHelper testOutputHelper)
    {
        _alpine = alpine;
        _loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);
    }

    public void Dispose() => _loggerFactory.Dispose();

    /// <summary>
    /// Password-auth callbacks shared by every test in this class. The
    /// permissive <c>CertificateCheck</c> accepts the container's ephemeral
    /// hostkey (known_hosts behavior is covered by
    /// <see cref="SshTransportDockerTests.KnownHosts_Match_Accepts"/>).
    /// </summary>
    private static GitRemoteCallbacks PasswordCallbacks => new()
    {
        Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
            new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
        CertificateCheck = _ => true,
    };

    private static string Url(SshGitDockerContainer fixture)
    {
        return $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
    }

    // ── Fast-forward over fetched history ────────────────────────────────

    /// <summary>
    /// Fast-forward merge over fetched history: the server grows a
    /// descendant <c>next</c> branch (one commit ahead of <c>main</c>); the
    /// client clones (HEAD at <c>main</c>), so
    /// <see cref="GitRepository.MergeAnalyzeAsync"/> merging
    /// <c>refs/remotes/origin/next</c> reports
    /// <see cref="GitMergeAnalysis.Normal"/> |
    /// <see cref="GitMergeAnalysis.FastForward"/> (merge base == our head).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GitRepository.MergeAsync"/> performs a 3-way merge regardless of
    /// FF-ness (parity with libgit2's <c>git_merge</c>, which does NOT
    /// special-case FF — that is the caller's job after analysis). With
    /// base==our, the merge result is exactly theirs' tree and is conflict
    /// free, so we additionally assert the merged workdir reflects theirs'
    /// added file. This exercises the full engine path —
    /// <see cref="GitRepository.MergeCommitsAsync"/> →
    /// <see cref="GitRepository.MergeAsync"/> → checkout → state files —
    /// against history pulled over SSH, which is otherwise cold in the
    /// integration suite.
    /// </para>
    /// <para>
    /// Also exercises <see cref="GitRepository.MergeBaseFindAsync"/> over fetched
    /// commits, confirming the merge-base walk resolves the seeded common
    /// ancestor.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Merge_FastForward_AnalysisAndCleanMerge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        // Server: grow a descendant 'next' branch (c2, child of main's c1).
        await fixture.ExecAsync(
            $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            "git checkout -b next && echo next > next.txt && git add next.txt && git commit -m next && " +
            "git checkout main'", ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-mergeff-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // our HEAD = main @ c1; theirs = origin/next @ c2 (fetched via the
            // default heads/* refspec at clone time).
            GitReference? headRef = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(headRef);
            GitOid ourOid = ((GitDirectReference)headRef).Target;

            GitReference? theirRef = await cloned.ReferenceResolveAsync("refs/remotes/origin/next", ct);
            Assert.NotNull(theirRef);
            GitOid theirOid = ((GitDirectReference)theirRef).Target;

            // Merge base over fetched history: base == our (the FF precondition).
            GitOid? baseOid = await cloned.MergeBaseFindAsync(ourOid, theirOid, ct);
            Assert.Equal(ourOid, baseOid);

            using GitAnnotatedCommit theirHead = await cloned.AnnotatedCommitFromRefAsync(theirRef, ct);

            GitMergeAnalysisResult analysis = await cloned.MergeAnalyzeAsync([theirHead], ct);
            Assert.Equal(GitMergeAnalysis.Normal | GitMergeAnalysis.FastForward, analysis.Analysis);

            // RunAsync: 3-way merge with base==our → theirs' tree, clean.
            await cloned.MergeAsync([theirHead], cancellationToken: ct);

            GitIndex index = await cloned.GetIndexAsync(ct);
            Assert.False(index.HasConflicts);

            // The merged workdir reflects theirs' added file.
            string nextPath = Path.Combine(targetPath, "next.txt");
            Assert.True(File.Exists(nextPath), "next.txt should be checked out after merge");
            Assert.Equal("next\n", await File.ReadAllTextAsync(nextPath, ct));

            // git_merge writes MERGE_HEAD state regardless (it does not
            // auto-commit; finalizing — even for an FF-shaped merge — is the
            // caller's job, matching libgit2).
            Assert.True(File.Exists(Path.Combine(cloned.Path, "MERGE_HEAD")));
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Up-to-date analysis ──────────────────────────────────────────────

    /// <summary>
    /// Up-to-date merge analysis: after a plain clone, our HEAD equals
    /// <c>refs/remotes/origin/main</c> (both at the seeded commit), so
    /// merging <c>origin/main</c> reports
    /// <see cref="GitMergeAnalysis.UpToDate"/> (merge base == our == their).
    /// No merge is performed — this exercises only the analysis + merge-base
    /// resolution path over fetched history.
    /// </summary>
    [Fact]
    public async Task Merge_UpToDate_AnalysisReportsUpToDate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-mergeuptodate-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            GitReference? theirRef = await cloned.ReferenceResolveAsync("refs/remotes/origin/main", ct);
            Assert.NotNull(theirRef);
            using GitAnnotatedCommit theirHead = await cloned.AnnotatedCommitFromRefAsync(theirRef, ct);

            GitMergeAnalysisResult analysis = await cloned.MergeAnalyzeAsync([theirHead], ct);
            Assert.Equal(GitMergeAnalysis.UpToDate, analysis.Analysis);

            // Nothing to merge — MERGE_HEAD must NOT be written by analysis.
            Assert.False(File.Exists(Path.Combine(cloned.Path, "MERGE_HEAD")));
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Clean 3-way merge of disjoint changes ────────────────────────────

    /// <summary>
    /// Clean 3-way merge over fetched history: after cloning, the client
    /// diverges <c>main</c> with a local commit adding <c>local.txt</c>; the
    /// server then grows a <c>feature</c> branch adding <c>feature.txt</c>
    /// (disjoint file). Analysis reports <see cref="GitMergeAnalysis.Normal"/>
    /// and <see cref="GitRepository.MergeAsync"/> produces a conflict-free merge
    /// whose workdir contains both sides' files.
    /// </summary>
    /// <remarks>
    /// Exercises the real 3-way path —
    /// <see cref="GitRepository.MergeAsync"/> →
    /// <see cref="MergeDiffList"/>/<see cref="MergeDiff"/> diff enumeration,
    /// the builtin text/union merge drivers on non-conflicting paths, the
    /// post-merge <see cref="GitRepository.CheckoutIndexAsync"/> checkout, index write,
    /// and <c>MERGE_HEAD</c>/<c>MERGE_MSG</c> state-file writes — against a
    /// fetched object database. This is the path that was entirely cold under
    /// integration coverage.
    /// </remarks>
    [Fact]
    public async Task Merge_ThreeWay_CleanMergeOfDisjointFiles()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        GitRemoteCallbacks callbacks = new()
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-mergeclean-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // Capture our base (the seeded c1) before diverging.
            GitReference? headRef = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(headRef);
            GitOid oursBase = ((GitDirectReference)headRef).Target;

            // Local commit c1' on main: add local.txt (diverges from
            // origin/main=c1). Built from the base tree so README.md is kept.
            GitOid localBlob = await cloned.ObjectWriteAsync(GitObjectType.Blob, "local\n"u8.ToArray(), ct);
            Commit baseCommit = (await cloned.ObjectLookupAsync<Commit>(oursBase, ct))
                ?? throw new InvalidOperationException("base commit missing after clone");
            GitTree baseTree = (await cloned.ObjectLookupAsync<GitTree>(baseCommit.Tree, ct))
                ?? throw new InvalidOperationException("base tree missing after clone");
            using GitTreeBuilder tb = cloned.NewTreeBuilder(baseTree);
            await tb.InsertAsync("local.txt", localBlob, GitFileMode.Regular, ct);
            GitOid oursTree = await tb.WriteAsync(ct);
            await cloned.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = oursTree,
                Parents = [oursBase],
                Author = new GitSignature("t", "t@t", new GitTime(1700000001, 0)),
                Committer = new GitSignature("t", "t@t", new GitTime(1700000001, 0)),
                Message = "local\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            // Sync the workdir to the new HEAD so the post-merge checkout has
            // no stale local modifications (Commit.CreateAsync moves the ref
            // but does not touch the workdir).
            await cloned.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct);

            // Server: grow feature branch adding feature.txt (disjoint from
            // the client's local.txt).
            await fixture.ExecAsync(
                $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
                "git config user.email t@t && git config user.name T && " +
                "git checkout -b feature && echo feature-only > feature.txt && git add feature.txt && git commit -m feature && " +
                "git checkout main'", ct);

            // Fetch the new feature branch into refs/remotes/origin/feature.
            GitRemote remote = await cloned.RemoteLookupAsync("origin", ct);
            await remote.FetchAsync(["+refs/heads/feature:refs/remotes/origin/feature"], new GitFetchOptions { RemoteCallbacks = callbacks }, reflogMessage: null, ct);

            GitReference? theirRef = await cloned.ReferenceResolveAsync("refs/remotes/origin/feature", ct);
            Assert.NotNull(theirRef);
            using GitAnnotatedCommit theirHead = await cloned.AnnotatedCommitFromRefAsync(theirRef, ct);

            // base=c1, our=c1', their=feature — divergent → Normal.
            GitMergeAnalysisResult analysis = await cloned.MergeAnalyzeAsync([theirHead], ct);
            Assert.Equal(GitMergeAnalysis.Normal, analysis.Analysis);

            await cloned.MergeAsync([theirHead], cancellationToken: ct);

            GitIndex index = await cloned.GetIndexAsync(ct);
            Assert.False(index.HasConflicts);

            // Both sides' files present in the merged workdir.
            Assert.True(File.Exists(Path.Combine(targetPath, "local.txt")), "our local.txt should remain after merge");
            Assert.True(File.Exists(Path.Combine(targetPath, "feature.txt")), "their feature.txt should be merged in");
            Assert.Equal("feature-only\n", await File.ReadAllTextAsync(Path.Combine(targetPath, "feature.txt"), ct));

            // State files written; MERGE_MSG records the merge.
            Assert.True(File.Exists(Path.Combine(cloned.Path, "MERGE_HEAD")));
            string? msg = await cloned.MessageAsync(ct);
            Assert.NotNull(msg);
            Assert.Contains("Merge", msg, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── 3-way merge with a content conflict ──────────────────────────────

    /// <summary>
    /// Conflicting 3-way merge over fetched history: the server seeds
    /// <c>data.txt</c> on <c>main</c>, then both sides edit the same line
    /// divergently (client → "ours", <c>feature</c> branch → "theirs").
    /// Analysis reports <see cref="GitMergeAnalysis.Normal"/> and
    /// <see cref="GitRepository.MergeAsync"/> leaves the index conflicted with
    /// conflict markers written into the working-tree file. Exercises the
    /// file-level conflict path (<see cref="GitMergeFile"/> → Xdiff
    /// <c>Merger.Merge</c>), 3-stage conflict index entries, and
    /// <see cref="GitCheckoutStrategy.AllowConflicts"/> checkout.
    /// </summary>
    [Fact]
    public async Task Merge_ThreeWay_ContentConflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        // Server: seed data.txt on main, then branch feature at that commit
        // (so origin/feature is advertised at clone time).
        await fixture.ExecAsync(
            $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            "echo base > data.txt && git add data.txt && git commit -m base && " +
            "git branch feature'", ct);

        string url = Url(fixture);
        GitRemoteCallbacks callbacks = new()
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-mergeconflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // our base = main @ (base commit with data.txt). feature is at the
            // same commit server-side, so origin/feature == our base here.
            GitReference? headRef = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(headRef);
            GitOid oursBase = ((GitDirectReference)headRef).Target;

            // Local commit: change data.txt to "ours" (diverges from
            // origin/main). Built from the base tree with data.txt overridden.
            GitOid oursBlob = await cloned.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), ct);
            Commit baseCommit = (await cloned.ObjectLookupAsync<Commit>(oursBase, ct))
                ?? throw new InvalidOperationException("base commit missing after clone");
            GitTree baseTree = (await cloned.ObjectLookupAsync<GitTree>(baseCommit.Tree, ct))
                ?? throw new InvalidOperationException("base tree missing after clone");
            using GitTreeBuilder tb = cloned.NewTreeBuilder(baseTree);
            await tb.InsertAsync("data.txt", oursBlob, GitFileMode.Regular, ct);
            GitOid oursTree = await tb.WriteAsync(ct);
            await cloned.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = oursTree,
                Parents = [oursBase],
                Author = new GitSignature("t", "t@t", new GitTime(1700000002, 0)),
                Committer = new GitSignature("t", "t@t", new GitTime(1700000002, 0)),
                Message = "ours\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            await cloned.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct);

            // Server: on feature, change data.txt to "theirs" and commit.
            await fixture.ExecAsync(
                $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
                "git config user.email t@t && git config user.name T && " +
                "git checkout feature && echo theirs > data.txt && git add data.txt && git commit -m theirs && " +
                "git checkout main'", ct);

            // Fetch the advanced feature branch (force-update origin/feature).
            GitRemote remote = await cloned.RemoteLookupAsync("origin", ct);
            await remote.FetchAsync(["+refs/heads/feature:refs/remotes/origin/feature"], new GitFetchOptions { RemoteCallbacks = callbacks }, reflogMessage: null, ct);

            GitReference? theirRef = await cloned.ReferenceResolveAsync("refs/remotes/origin/feature", ct);
            Assert.NotNull(theirRef);
            using GitAnnotatedCommit theirHead = await cloned.AnnotatedCommitFromRefAsync(theirRef, ct);

            // base has data.txt; both sides changed it divergently → Normal.
            GitMergeAnalysisResult analysis = await cloned.MergeAnalyzeAsync([theirHead], ct);
            Assert.Equal(GitMergeAnalysis.Normal, analysis.Analysis);

            await cloned.MergeAsync([theirHead], cancellationToken: ct);

            GitIndex index = await cloned.GetIndexAsync(ct);
            Assert.True(index.HasConflicts, "merge of divergent edits to data.txt must leave the index conflicted");

            // MERGE_HEAD written (conflicted merges keep state for resolution).
            Assert.True(File.Exists(Path.Combine(cloned.Path, "MERGE_HEAD")));

            // The working-tree file must carry conflict markers from the Xdiff
            // file-level merge, containing both sides' content.
            string dataPath = Path.Combine(targetPath, "data.txt");
            Assert.True(File.Exists(dataPath), "data.txt should be in the workdir (with conflict markers)");
            string merged = await File.ReadAllTextAsync(dataPath, ct);
            Assert.Contains("<<<<<<<", merged, StringComparison.Ordinal);
            Assert.Contains("=======", merged, StringComparison.Ordinal);
            Assert.Contains(">>>>>>>", merged, StringComparison.Ordinal);
            Assert.Contains("ours", merged, StringComparison.Ordinal);
            Assert.Contains("theirs", merged, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}
