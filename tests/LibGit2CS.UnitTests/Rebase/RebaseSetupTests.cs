using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using RebaseOps = LibGit2CS.Rebase.GitRebase;
using RebaseOpts = LibGit2CS.Rebase.GitRebaseOptions;

namespace LibGit2CS.UnitTests.Rebase;

/// <summary>
/// Rebase setup/init tests. Mirrors libgit2's <c>tests/libgit2/rebase/setup.c</c>.
/// </summary>
public sealed class RebaseSetupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RebaseSetupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseSetup_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private async ValueTask<GitRepository> OpenRebaseRepoAsync()
    {
        string repoPath = RebaseTestHelpers.OpenRebaseRepo(_extractedPaths);
        GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext());
        await repo.Config.SetBoolAsync("core.autocrlf", false);
        return repo;
    }

    private static async Task ForceCheckout(GitRepository repo, string refName)
    {
        await repo.SetHeadAsync(refName);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force });
    }

    // ── setup.c ───────────────────────────────────────────────────────

    [Fact]
    public async Task BlockedWhenInProgress()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using (RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(RepositoryState.RebaseMerge, repo.State);
        }

        // Re-init should fail because rebase is in progress.
        await Assert.ThrowsAsync<GitException>(async () => await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Merge()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryState.RebaseMerge, repo.State);

        // HEAD should be at onto (master).
        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        // State files.
        RebaseTestHelpers.AssertGitdirFile(repo.Path, "ORIG_HEAD", "b146bd7608eac53d9bf9e1a6963543588b555c64\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "da9c51a23d02d931a486f45ad18cda05cf5d2b94\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.2", "8d1f13f93c4995760ac07d129246ac1ff64c0be9\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.3", "3069cc907e6294623e5917ef6de663928c1febfb\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.4", "588e5d2f04d49707fe4aab865e1deacaf7ef6787\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.5", "b146bd7608eac53d9bf9e1a6963543588b555c64\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "5\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto", "efad0b11c47cb2f0220cbd6f5b0f93bb99064b00\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "master\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "orig-head", "b146bd7608eac53d9bf9e1a6963543588b555c64\n");
    }

    [Fact]
    public async Task MergeRoot()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit onto = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        // branch=beef, upstream=null, onto=master (rebase all commits)
        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, null, onto, cancellationToken: TestContext.Current.CancellationToken);

        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "da9c51a23d02d931a486f45ad18cda05cf5d2b94\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "5\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "master\n");
    }

    [Fact]
    public async Task MergeOntoAndUpstream()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // git checkout gravy && git rebase --merge --onto master veal
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/gravy");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");
        GitAnnotatedCommit onto = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, onto, cancellationToken: TestContext.Current.CancellationToken);

        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        RebaseTestHelpers.AssertGitdirFile(repo.Path, "ORIG_HEAD", "d616d97082eb7bb2dc6f180a7cca940993b7a56f\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "d616d97082eb7bb2dc6f180a7cca940993b7a56f\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "1\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "master\n");
    }

    [Fact]
    public async Task MergeOntoUpstreamAndBranch()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // git checkout beef && git rebase --merge --onto master gravy veal
        await ForceCheckout(repo, "refs/heads/beef");

        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/gravy");
        GitAnnotatedCommit onto = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, onto, cancellationToken: TestContext.Current.CancellationToken);

        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        RebaseTestHelpers.AssertGitdirFile(repo.Path, "ORIG_HEAD", "f87d14a4a236582a0278a916340a793714256864\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "3e8989b5a16d5258c935d998ef0e6bb139cc4757\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.2", "4cacc6f6e740a5bc64faa33e04b8ef0733d8a127\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.3", "f87d14a4a236582a0278a916340a793714256864\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "3\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "master\n");
    }

    [Fact]
    public async Task MergeOntoUpstreamAndBranchById()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // All three commits provided by OID (no ref names).
        await ForceCheckout(repo, "refs/heads/beef");

        GitAnnotatedCommit upstream = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("f87d14a4a236582a0278a916340a793714256864"), TestContext.Current.CancellationToken);
        GitAnnotatedCommit branch = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("d616d97082eb7bb2dc6f180a7cca940993b7a56f"), TestContext.Current.CancellationToken);
        GitAnnotatedCommit onto = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), TestContext.Current.CancellationToken);

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, onto, cancellationToken: TestContext.Current.CancellationToken);

        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "d616d97082eb7bb2dc6f180a7cca940993b7a56f\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "1\n");
        // onto_name = OID hex (no ref name for ID-based annotated commits).
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "efad0b11c47cb2f0220cbd6f5b0f93bb99064b00\n");
    }

    [Fact]
    public async Task BranchWithMerges()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // git checkout veal && git rebase --merge master
        // veal has merges; they should be dropped from the operation list.
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryState.RebaseMerge, repo.State);

        // 5 non-merge commits.
        Assert.Equal(5, rebase.OperationCount);

        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        RebaseTestHelpers.AssertGitdirFile(repo.Path, "ORIG_HEAD", "f87d14a4a236582a0278a916340a793714256864\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "4bed71df7017283cac61bbf726197ad6a5a18b84\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.2", "2aa3ce842094e08ebac152b3d6d5b0fff39f9c6e\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.3", "3e8989b5a16d5258c935d998ef0e6bb139cc4757\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.4", "4cacc6f6e740a5bc64faa33e04b8ef0733d8a127\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.5", "f87d14a4a236582a0278a916340a793714256864\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "5\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "master\n");
    }

    [Fact]
    public async Task OrphanBranch()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // git checkout barley && git rebase --merge master
        // barley has no common ancestor with master.
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/barley");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryState.RebaseMerge, repo.State);

        Assert.Equal(5, rebase.OperationCount);

        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        RebaseTestHelpers.AssertGitdirFile(repo.Path, "ORIG_HEAD", "12c084412b952396962eb420716df01022b847cc\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "aa4c42aecdfc7cd989bbc3209934ea7cda3f4d88\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "5\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "master\n");
    }

    [Fact]
    public async Task MergeNullBranchUsesHead()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // git checkout beef && git rebase --merge master (branch=null → use HEAD)
        await ForceCheckout(repo, "refs/heads/beef");

        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, null, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryState.RebaseMerge, repo.State);

        // The resolved branch ref must be recorded, not "HEAD".
        Assert.Equal("refs/heads/beef", rebase.OrigHeadNameProperty);

        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        RebaseTestHelpers.AssertGitdirFile(repo.Path, "ORIG_HEAD", "b146bd7608eac53d9bf9e1a6963543588b555c64\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "head-name", "refs/heads/beef\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "da9c51a23d02d931a486f45ad18cda05cf5d2b94\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.2", "8d1f13f93c4995760ac07d129246ac1ff64c0be9\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.3", "3069cc907e6294623e5917ef6de663928c1febfb\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.4", "588e5d2f04d49707fe4aab865e1deacaf7ef6787\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.5", "b146bd7608eac53d9bf9e1a6963543588b555c64\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "5\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto", "efad0b11c47cb2f0220cbd6f5b0f93bb99064b00\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "master\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "orig-head", "b146bd7608eac53d9bf9e1a6963543588b555c64\n");
    }

    [Fact]
    public async Task MergeNullBranchUnbornHeadThrows()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        // Point HEAD at a branch that does not exist (unborn HEAD).
        await repo.ReferenceCreateSymbolicAsync("HEAD", "refs/heads/unborn", force: true, cancellationToken: TestContext.Current.CancellationToken);

        // In-memory rebase skips the not-dirty checks; the branch=null HEAD
        // resolution must fail with UnbornBranch (git_repository_head
        // returns GIT_EUNBORNBRANCH for a symbolic HEAD with no target).
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await RebaseOps.InitAsync(repo, null, upstream, null, new RebaseOpts { InMemory = true }, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.UnbornBranch, ex.Code);
    }

    [Fact]
    public async Task MergeFromDetached()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // git checkout b146bd76 (detached) && git rebase --merge master
        await repo.SetHeadDetachedAsync(RebaseTestHelpers.Oid("b146bd7608eac53d9bf9e1a6963543588b555c64"), cancellationToken: TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);

        GitAnnotatedCommit branch = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("b146bd7608eac53d9bf9e1a6963543588b555c64"), TestContext.Current.CancellationToken);
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryState.RebaseMerge, repo.State);

        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "da9c51a23d02d931a486f45ad18cda05cf5d2b94\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "5\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "master\n");
    }

    [Fact]
    public async Task MergeBranchById()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // git checkout beef && git rebase --merge efad0b11 (upstream by ID)
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), TestContext.Current.CancellationToken);

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryState.RebaseMerge, repo.State);

        var head = (await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), head!.Target);

        RebaseTestHelpers.AssertStateFile(repo.Path, "cmt.1", "da9c51a23d02d931a486f45ad18cda05cf5d2b94\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "5\n");
        // onto_name = OID hex (no ref for ID-based upstream when used as onto).
        RebaseTestHelpers.AssertStateFile(repo.Path, "onto_name", "efad0b11c47cb2f0220cbd6f5b0f93bb99064b00\n");
    }

    [Fact]
    public async Task BlockedForStagedChange()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // Stage a new file.
        string newFilePath = Path.Combine(repo.Workdir!, "newfile.txt");
        await File.WriteAllTextAsync(newFilePath, "Stage an add", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        index.Add(new LibGit2CS.Index.GitIndexEntry("newfile.txt", RebaseTestHelpers.Oid("da9c51a23d02d931a486f45ad18cda05cf5d2b94"), GitFileMode.Regular));
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await RebaseIsBlocked(repo));
    }

    [Fact]
    public async Task BlockedForUnstagedChange()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // Modify a workdir file (unstaged).
        string filePath = Path.Combine(repo.Workdir!, "asparagus.txt");
        await File.WriteAllTextAsync(filePath, "Unstaged change", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await RebaseIsBlocked(repo));
    }

    [Fact]
    public async Task NotBlockedForUntrackedAdd()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        // Create an untracked file (not added to index).
        string newFilePath = Path.Combine(repo.Workdir!, "newfile.txt");
        await File.WriteAllTextAsync(newFilePath, "Untracked file", cancellationToken: TestContext.Current.CancellationToken);

        // Should NOT throw — untracked files don't block rebase.
        await RebaseIsBlocked(repo);
    }

    private static async Task RebaseIsBlocked(GitRepository repo)
    {
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");
        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null);
    }
}
