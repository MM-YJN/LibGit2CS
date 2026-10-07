using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Merge;
using LibGit2CS.Notes;
using LibGit2CS.Objects;
using LibGit2CS.Rebase;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitRebaseOperationType = LibGit2CS.Rebase.GitRebaseOperationType;
using RebaseOps = LibGit2CS.Rebase.GitRebase;

namespace LibGit2CS.UnitTests.Rebase;

/// <summary>
/// Rebase merge (on-disk) tests. Mirrors libgit2's
/// <c>tests/libgit2/rebase/merge.c</c>.
/// </summary>
public sealed class RebaseMergeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RebaseMergeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseMerge_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task Next()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("refs/heads/beef", rebase.OrigHeadNameProperty);
        Assert.Equal(RebaseTestHelpers.Oid("b146bd7608eac53d9bf9e1a6963543588b555c64"), rebase.OrigHeadIdProperty);
        Assert.Equal("master", rebase.OntoNameProperty);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), rebase.OntoIdProperty);

        GitRebaseOperation op = await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitRebaseOperationType.Pick, op.Type);
        Assert.Equal(RebaseTestHelpers.Oid("da9c51a23d02d931a486f45ad18cda05cf5d2b94"), op.Id);

        // State files.
        RebaseTestHelpers.AssertStateFile(repo.Path, "current", "da9c51a23d02d931a486f45ad18cda05cf5d2b94\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "msgnum", "1\n");
    }

    [Fact]
    public async Task NextWithConflicts()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/asparagus");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);

        GitRebaseOperation op = await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitRebaseOperationType.Pick, op.Type);
        Assert.Equal(RebaseTestHelpers.Oid("33f915f9e4dbd9f4b24430e48731a59b45b15500"), op.Id);

        RebaseTestHelpers.AssertStateFile(repo.Path, "current", "33f915f9e4dbd9f4b24430e48731a59b45b15500\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "msgnum", "1\n");

        // The index should have conflicts.
        Assert.True((await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).HasConflicts);

        // Commit should fail with Unmerged.
        await Assert.ThrowsAsync<GitException>(async () => await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NextStopsWithIterOver()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);

        for (int i = 0; i < 5; i++)
        {
            await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
            await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<GitException>(async () => await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken));

        RebaseTestHelpers.AssertStateFile(repo.Path, "end", "5\n");
        RebaseTestHelpers.AssertStateFile(repo.Path, "msgnum", "5\n");
    }

    [Fact]
    public async Task Commit()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commitId = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        // The commit should have the onto commit as parent.
        Commit? commit = await repo.ObjectLookupAsync<Commit>(commitId, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Single(commit.Parents);
        Assert.Equal(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), commit.ParentId(0));

        // The commit message should match the original.
        Assert.Contains("Modification 1 to beef", commit.Message);

        // The rewritten file should have the old→new mapping.
        string rewrittenPath = Path.Combine(repo.Path, "rebase-merge", "rewritten");
        Assert.True(File.Exists(rewrittenPath));
        string content = await File.ReadAllTextAsync(rewrittenPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("da9c51a23d02d931a486f45ad18cda05cf5d2b94", content);
        Assert.Contains(commitId.ToString(), content);
    }

    [Fact]
    public async Task CommitDropsAlreadyApplied()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/green_pea");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);

        // The first commit of beef is already applied in green_pea.
        // rebase_next should succeed (the merge produces no changes),
        // but rebase_commit should return EAPPLIED.
        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Finish()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/gravy");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, rebase.OperationCount);

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        await rebase.FinishAsync(sig, cancellationToken: TestContext.Current.CancellationToken);

        // State should be None after finish.
        Assert.Equal(RepositoryState.None, repo.State);

        // HEAD should be symbolic, pointing at the original branch.
        GitReference? head = await repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<GitSymbolicReference>(head);
        Assert.Equal("refs/heads/gravy", ((GitSymbolicReference)head).TargetName);
    }

    [Fact]
    public async Task FinishNullBranchMovesBranch()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();

        // git checkout beef && git rebase --merge master (branch=null → use HEAD).
        await ForceCheckout(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, null, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/beef", rebase.OrigHeadNameProperty);

        GitOid last = GitOid.Empty;
        int count = 0;
        while (true)
        {
            try
            {
                await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.IterOver)
            {
                break;
            }

            last = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);
            count++;
        }

        Assert.Equal(5, count);

        await rebase.FinishAsync(sig, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RepositoryState.None, repo.State);

        // The original branch must be moved to the terminal commit.
        GitReference? beef = await repo.ReferenceLookupAsync("refs/heads/beef", cancellationToken: TestContext.Current.CancellationToken);
        GitDirectReference beefDirect = Assert.IsType<GitDirectReference>(beef);
        Assert.Equal(last, beefDirect.Target);

        // HEAD must be symbolic at the original branch again.
        GitReference? head = await repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        GitSymbolicReference headSymbolic = Assert.IsType<GitSymbolicReference>(head);
        Assert.Equal("refs/heads/beef", headSymbolic.TargetName);

        // The rebase-merge state directory must be gone.
        Assert.False(Directory.Exists(Path.Combine(repo.Path, "rebase-merge")));
    }

    [Fact]
    public async Task DetachedFinish()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/gravy");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");

        // git_repository_set_head_detached_from_annotated + checkout HEAD.
        await repo.SetHeadDetachedAsync(branch.Id, TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, null, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, rebase.OperationCount);

        // A detached HEAD stays "detached HEAD" in the state files.
        Assert.Null(rebase.OrigHeadNameProperty);
        RebaseTestHelpers.AssertStateFile(repo.Path, "head-name", "detached HEAD\n");

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        await rebase.FinishAsync(sig, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RepositoryState.None, repo.State);

        // HEAD should be direct (detached), not symbolic.
        GitReference? head = await repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<GitDirectReference>(head);
    }

    [Fact]
    public async Task NoCommonAncestor()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/barley");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, rebase.OperationCount);

        GitOid commitId = default;
        for (int i = 0; i < 5; i++)
        {
            await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
            commitId = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);
        }

        await rebase.FinishAsync(sig, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RebaseTestHelpers.Oid("71e7ee8d4fe7d8bf0d107355197e0a953dfdb7f3"), commitId);
    }

    [Fact]
    public async Task WithDirectories()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/deep_gravy");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, rebase.OperationCount);

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commitId = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken));

        await rebase.FinishAsync(sig, cancellationToken: TestContext.Current.CancellationToken);

        Commit? commit = await repo.ObjectLookupAsync<Commit>(commitId, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Equal(RebaseTestHelpers.Oid("a4d6d9c3d57308fd8e320cf2525bae8f1adafa57"), commit.Tree);
    }

    // ── merge.c: blocked_when_dirty ──────────────────────────────────────

    [Fact]
    public async Task BlockedWhenDirty()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, cancellationToken: TestContext.Current.CancellationToken);

        // Allow untracked files.
        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "untracked_file.txt"), "This is untracked\n", cancellationToken: TestContext.Current.CancellationToken);
        await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        // Do not allow unstaged changes to tracked files.
        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "veal.txt"), "This is an unstaged change\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Unmerged, ex.Code);
    }

    // ── merge.c: notes rewriting ─────────────────────────────────────────

    private static async Task TestCopyNote(
        GitRepository repo,
        GitRebaseOptions? opts,
        bool shouldExist)
    {
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitReference? branchRef = await repo.ReferenceLookupAsync("refs/heads/gravy");
        Assert.NotNull(branchRef);
        GitReference? upstreamRef = await repo.ReferenceLookupAsync("refs/heads/veal");
        Assert.NotNull(upstreamRef);
        GitAnnotatedCommit branchHead = await repo.AnnotatedCommitFromRefAsync(branchRef);
        GitAnnotatedCommit upstreamHead = await repo.AnnotatedCommitFromRefAsync(upstreamRef);

        // Peel branch ref to get the commit for the note target.
        Commit? branchCommit = await repo.ObjectLookupAsync<Commit>(((GitDirectReference)branchRef).Target, TestContext.Current.CancellationToken);
        Assert.NotNull(branchCommit);

        // Add a note to the branch tip commit.
        await repo.NotesCreateAsync("refs/notes/test", sig, sig, branchCommit.Id, "This is a commit note.");

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branchHead, upstreamHead, null, opts);
        await rebase.NextAsync();
        GitOid commitId = await rebase.CommitAsync(null, sig);
        await rebase.FinishAsync(sig);

        Assert.Equal(RepositoryState.None, repo.State);

        if (shouldExist)
        {
            using GitNote? note = await repo.NotesReadAsync("refs/notes/test", commitId);
            Assert.NotNull(note);
            Assert.Equal("This is a commit note.", note!.Message);
        }
        else
        {
            // C's git_note_read fails with GIT_ENOTFOUND when no note
            // exists for the target (the ref exists but has no note for the
            // rebased commit).
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.NotesReadAsync("refs/notes/test", commitId));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);
        }

        branchHead.Dispose();
        upstreamHead.Dispose();
    }

    [Fact]
    public async Task CopyNotesOffByDefault()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        await TestCopyNote(repo, null, shouldExist: false);
    }

    [Fact]
    public async Task CopyNotesSpecifiedInOptions()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        var opts = new GitRebaseOptions { RewriteNotesRef = "refs/notes/test" };
        await TestCopyNote(repo, opts, shouldExist: true);
    }

    [Fact]
    public async Task CopyNotesSpecifiedInConfig()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        await repo.Config.SetStringAsync("notes.rewriteRef", "refs/notes/test", cancellationToken: TestContext.Current.CancellationToken);
        await TestCopyNote(repo, null, shouldExist: true);
    }

    [Fact]
    public async Task CopyNotesDisabledInConfig()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        await repo.Config.SetBoolAsync("notes.rewrite.rebase", false, cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("notes.rewriteRef", "refs/notes/test", cancellationToken: TestContext.Current.CancellationToken);
        await TestCopyNote(repo, null, shouldExist: false);
    }

    // ── merge.c: custom_checkout_options ─────────────────────────────────

    private sealed class ProgressCounter : IProgress<GitCheckoutProgress>
    {
        public int Called { get; set; }

        public void Report(GitCheckoutProgress value) => Called++;
    }

    [Fact]
    public async Task CustomCheckoutOptions()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        // Check out beef first so that Init's checkout of master produces
        // actual file changes (and thus progress callbacks).
        await ForceCheckout(repo, "refs/heads/beef");

        var counter = new ProgressCounter();
        var opts = new GitRebaseOptions
        {
            CheckoutOptions = new GitCheckoutOptions { Progress = counter },
        };

        counter.Called = 0;
        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(counter.Called > 0);

        counter.Called = 0;
        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(counter.Called > 0);

        counter.Called = 0;
        await rebase.AbortAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(counter.Called > 0);
    }

    // ── merge.c: custom_merge_options ────────────────────────────────────

    [Fact]
    public async Task CustomMergeOptions()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/asparagus");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        var opts = new GitRebaseOptions
        {
            MergeOptions = new GitMergeOptions
            {
                Flags = GitMergeFlags.FailOnConflict | GitMergeFlags.SkipReuc,
            },
        };

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.MergeConflict, ex.Code);
    }
}
