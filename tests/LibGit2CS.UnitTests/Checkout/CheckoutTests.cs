using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

// Use a namespace that doesn't conflict with LibGit2CS.Checkout.
namespace LibGit2CS.UnitTests.Checkout;

public sealed class CheckoutTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public CheckoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommitWithFile(string fileName, string content, string refName = "refs/heads/master")
    {
        GitIndex idx = await _repo.GetIndexAsync();
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName);
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        GitSignature sig = TestSig();

        // C (commit.c:113-117): a commit created with update_ref must have
        // the current tip as its first parent — chain onto it.
        List<GitOid> parentChain = [];
        if (await _repo.ReferenceResolveAsync(refName, TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parentChain.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parentChain,
            Author = sig,
            Committer = sig,
            Message = $"add {fileName}\n",
            UpdateRef = refName,
        });
    }

    private async Task<GitOid> WriteSecondCommitWithFile(string fileName, string content, GitOid parent)
    {
        GitIndex idx = await _repo.GetIndexAsync();
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName);
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parent],
            Author = sig,
            Committer = sig,
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        });
    }

    // ── CheckoutHead ───────────────────────────────────────────────────────

    [Fact]
    public async Task Head_Force_ChecksOutAllFiles()
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        // Create initial commit with two files.
        await WriteCommitWithFile("file1.txt", "content1\n");
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "file2.txt"), "content2\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file2.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();
        Commit? head = await _repo.ObjectLookupAsync<Commit>(
            (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) is GitDirectReference dr ? dr.Target : default,
            TestContext.Current.CancellationToken);
        Assert.NotNull(head);

        // Make a second commit that adds file2.
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [head.Id],
            Author = sig,
            Committer = sig,
            Message = "add file2\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Delete file2 from workdir.
        File.Delete(Path.Combine(workdir, "file2.txt"));

        // Checkout HEAD with Force → should recreate file2.
        await _repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(workdir, "file2.txt")));
        Assert.Equal("content2\n", await File.ReadAllTextAsync(Path.Combine(workdir, "file2.txt"), cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── CheckoutTree ───────────────────────────────────────────────────────

    [Fact]
    public async Task Tree_Force_ChecksOutTreeContent()
    {
        GitOid commitOid = await WriteCommitWithFile("README.md", "hello\n");
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        // Remove the file from workdir.
        File.Delete(Path.Combine(_repo.Workdir!, "README.md"));

        // Checkout the tree with Force → should recreate the file.
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "README.md")));
        Assert.Equal("hello\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "README.md"), cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <see cref="GitCheckoutStrategy.Safe"/> (default) aborts checkout
    /// with <see cref="GitErrorCode.Conflict"/> when the workdir file is
    /// modified relative to the baseline AND the baseline→target delta
    /// would overwrite it (GIT_DELTA_MODIFIED, checkout.c:524-529). The
    /// dirty file is left untouched (no write happens before the throw).
    /// </summary>
    [Fact]
    public async Task Tree_Safe_ThrowsConflict_OnModified()
    {
        GitOid commitOid = await WriteCommitWithFile("README.md", "hello\n");
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        // A second commit changes README.md so the checkout target differs
        // from HEAD (the baseline is the HEAD tree, checkout.c:2476-2484).
        await WriteCommitWithFile("README.md", "hello2\n");

        // Modify the file in the workdir.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "README.md"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);

        // The dirty file is untouched (no write happened before the throw).
        Assert.Equal("modified\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "README.md"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tree_Force_OverwritesModified()
    {
        GitOid commitOid = await WriteCommitWithFile("README.md", "hello\n");
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        // Modify the file in the workdir.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "README.md"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);

        // Force checkout should overwrite.
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hello\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "README.md"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tree_ChecksOutNewFile()
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commitOid = await WriteCommitWithFile("file1.txt", "content1\n");

        // Create a second commit with a new file.
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "file2.txt"), "content2\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file2.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();
        GitOid secondCommit = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [commitOid],
            Author = sig,
            Committer = sig,
            Message = "add file2\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Now create a branch at the first commit and check it out.
        await _repo.BranchCreateAsync("old", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        // Switch to the "old" branch.
        await _repo.SetHeadAsync("refs/heads/old", cancellationToken: TestContext.Current.CancellationToken);

        // Remove file2 from workdir and index (simulating a branch switch).
        File.Delete(Path.Combine(workdir, "file2.txt"));

        // Now checkout the second commit's tree.
        Commit? secondCommitObj = await _repo.ObjectLookupAsync<Commit>(secondCommit, TestContext.Current.CancellationToken);
        Assert.NotNull(secondCommitObj);
        GitTree? secondTree = await _repo.ObjectLookupAsync<GitTree>(secondCommitObj.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(secondTree);
        await _repo.CheckoutTreeAsync(secondTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(workdir, "file2.txt")));
        Assert.Equal("content2\n", await File.ReadAllTextAsync(Path.Combine(workdir, "file2.txt"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tree_RemovesDeletedFile()
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        // Create commit with two files.
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "file1.txt"), "content1\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workdir, "file2.txt"), "content2\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file1.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file2.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();
        GitOid commit1 = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "two files\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Create a second commit that removes file2.
        idx.RemoveByPath("file2.txt");
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid2 = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid2,
            Parents = [commit1],
            Author = sig,
            Committer = sig,
            Message = "remove file2\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Checkout the first tree (which has both files) with Force.
        // baseline=HEAD(tree2, only file1), target=tree1(both files).
        // Diff: file2 is Added → checkout writes it.
        Commit? commit1Obj = await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken);
        Assert.NotNull(commit1Obj);
        GitTree? tree1 = await _repo.ObjectLookupAsync<GitTree>(commit1Obj.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree1);
        await _repo.CheckoutTreeAsync(tree1, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);

        // file2 should be restored.
        Assert.True(File.Exists(Path.Combine(workdir, "file2.txt")));
    }

    // ── Checkout.Index ──────────────────────────────────────────────────

    [Fact]
    public async Task Index_Force_WritesIndexToWorkdir()
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await WriteCommitWithFile("README.md", "hello\n");

        // Stage a new file in the index without writing to workdir.
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "staged\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var entry = new GitIndexEntry("staged.txt", blobOid, GitFileMode.Regular);
        idx.Add(entry);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The file doesn't exist in workdir yet.
        Assert.False(File.Exists(Path.Combine(_repo.Workdir!, "staged.txt")));

        // Checkout index with Force → should write the staged file.
        await _repo.CheckoutIndexAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "staged.txt")));
        Assert.Equal("staged\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "staged.txt"), cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Checkout notifications ──────────────────────────────────────────

    /// <summary>
    /// Under <see cref="GitCheckoutStrategy.Safe"/>, a dirty workdir file
    /// triggers a <see cref="GitCheckoutNotifyFlags.Dirty"/> notification
    /// (via <see cref="GitCheckoutOptions.Notify"/>) and the checkout then
    /// aborts with <see cref="GitErrorCode.Conflict"/>. The notify callback
    /// fires before the throw (matches libgit2's
    /// <c>checkout_get_actions</c> at checkout.c:1372-1380 — actions and
    /// notifications are collected first, then the ECONFLICT throw happens).
    /// </summary>
    [Fact]
    public async Task Tree_Notify_Conflict_AbortsCheckout()
    {
        GitOid commitOid = await WriteCommitWithFile("README.md", "hello\n");
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        // A second commit changes README.md so the target differs from HEAD.
        await WriteCommitWithFile("README.md", "hello2\n");

        // Modify the file.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "README.md"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);

        var notifications = new List<GitCheckoutNotification>();
        var opts = new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Safe,
            NotifyFlags = GitCheckoutNotifyFlags.Conflict | GitCheckoutNotifyFlags.Dirty,
            Notify = n =>
            {
                notifications.Add(n);
                return false; // don't abort via notify (the throw comes from ThrowIfConflicts)
            },
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await _repo.CheckoutTreeAsync(tree, opts, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);

        // The Dirty notification fired before the throw.
        Assert.NotEmpty(notifications);
    }

    // ── Bare repo ───────────────────────────────────────────────────────

    [Fact]
    public async Task Tree_BareRepo_Throws()
    {
        string bareDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutTests_bare_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using GitRepository bare = await GitRepository.InitAsync(bareDir, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<GitException>(async () => await bare.CheckoutTreeAsync(null, new GitCheckoutOptions(), cancellationToken: TestContext.Current.CancellationToken));
            await bare.DisposeAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(bareDir, recursive: true);
            }
            catch (IOException) { }
        }
    }

    // ── Subdirectory checkout ───────────────────────────────────────────

    [Fact]
    public async Task Tree_Force_ChecksOutSubdirectories()
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        // Create a commit with a file in a subdirectory.
        string workdir = _repo.Workdir!;
        Directory.CreateDirectory(Path.Combine(workdir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "sub", "file.txt"), "content\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("sub/file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "subdir\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Delete the subdirectory from workdir.
        Directory.Delete(Path.Combine(workdir, "sub"), recursive: true);

        // Checkout HEAD with Force → should recreate the subdirectory.
        await _repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(workdir, "sub", "file.txt")));
        Assert.Equal("content\n", await File.ReadAllTextAsync(Path.Combine(workdir, "sub", "file.txt"), cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Dry run ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Tree_DryRun_DoesNotModifyWorkdir()
    {
        GitOid commitOid = await WriteCommitWithFile("README.md", "hello\n");
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        // Remove the file.
        File.Delete(Path.Combine(_repo.Workdir!, "README.md"));

        // Dry run → should NOT recreate the file.
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RecreateMissing | GitCheckoutStrategy.DryRun,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_repo.Workdir!, "README.md")));
    }

    // ── Empty tree checkout ─────────────────────────────────────────────

    [Fact]
    public async Task Tree_EmptyTree_ChecksOutNothing()
    {
        // Create an empty tree.
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        GitOid emptyTreeOid = await bld.WriteAsync(TestContext.Current.CancellationToken);
        GitTree? emptyTree = await _repo.ObjectLookupAsync<GitTree>(emptyTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(emptyTree);

        // Should not throw — empty tree checkout is valid.
        await _repo.CheckoutTreeAsync(emptyTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── Checkout with pathspec ──────────────────────────────────────────

    [Fact]
    public async Task Tree_WithPaths_LimitsCheckout()
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        // Create a commit with two files.
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "file1.txt"), "content1\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workdir, "file2.txt"), "content2\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file1.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file2.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();
        GitOid commitOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "two files\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Delete both files from workdir.
        File.Delete(Path.Combine(workdir, "file1.txt"));
        File.Delete(Path.Combine(workdir, "file2.txt"));

        // Checkout with pathspec limiting to file1 only.
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RecreateMissing,
            PathsStrings = ["file1.txt"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Only file1 should be recreated.
        Assert.True(File.Exists(Path.Combine(workdir, "file1.txt")));
        Assert.False(File.Exists(Path.Combine(workdir, "file2.txt")));
    }

    // ── Progress callback ───────────────────────────────────────────────

    [Fact]
    public async Task Tree_Force_ReportsProgress()
    {
        GitOid commitOid = await WriteCommitWithFile("README.md", "hello\n");
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        File.Delete(Path.Combine(_repo.Workdir!, "README.md"));

        var progressEvents = new List<GitCheckoutProgress>();
        var progress = new SynchronousProgress<GitCheckoutProgress>(progressEvents.Add);
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RecreateMissing,
            Progress = progress,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(progressEvents);
    }

    // ── Perfdata callback ──────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitCheckoutOptions.Perfdata"/> receives a non-zero
    /// <see cref="GitCheckoutPerformance.MkdirCalls"/> count when checkout
    /// creates a parent directory for a blob in a subdirectory. Matches
    /// libgit2's <c>test_checkout_tree__can_collect_perfdata</c>: the perf
    /// struct must not be copied at construction (via the <c>ref</c>
    /// parameter), or the counters never propagate back to
    /// <see cref="CheckoutContext"/> and the perfdata callback always
    /// reports zeros.
    /// </summary>
    [Fact]
    public async Task Tree_Perfdata_ReportsMkdirCallsForSubdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // WriteCommitWithFile doesn't create parent dirs, so stage manually.
        GitIndex idx = await _repo.GetIndexAsync(ct);
        string workdir = _repo.Workdir!;
        Directory.CreateDirectory(Path.Combine(workdir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "sub", "file.txt"), "hello\n", ct);
        await idx.AddByPathAsync("sub/file.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);
        GitSignature sig = TestSig();
        GitOid commitOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = "refs/heads/master",
        }, ct);
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, ct);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, ct);
        Assert.NotNull(tree);

        // Wipe the workdir subdir so checkout must recreate it.
        Directory.Delete(Path.Combine(_repo.Workdir!, "sub"), recursive: true);

        GitCheckoutPerformance? captured = null;
        var perfdata = new SynchronousProgress<GitCheckoutPerformance>(p => captured = p);
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
            Perfdata = perfdata,
        }, cancellationToken: ct);

        Assert.NotNull(captured);
        Assert.True(captured.Value.MkdirCalls > 0,
            $"MkdirCalls should be > 0 after creating sub/, got {captured.Value.MkdirCalls}");
    }

    // ── TargetDirectory ────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitCheckoutOptions.TargetDirectory"/> redirects checkout
    /// writes to an alternative directory instead of the repository workdir.
    /// Matches libgit2's <c>test_checkout_index__target_directory</c>:
    /// <see cref="WorkdirWriter"/> must honor
    /// <see cref="GitCheckoutOptions.TargetDirectory"/> rather than always
    /// writing to <see cref="GitRepository.Workdir"/>.
    /// </summary>
    [Fact]
    public async Task Index_TargetDirectory_WritesToAltDir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await WriteCommitWithFile("README.md", "hello\n");
        string workdir = _repo.Workdir!;
        string altDir = Path.Combine(_tempDir, "alternative");
        Directory.CreateDirectory(altDir);

        // Wipe workdir so a write there would be visible.
        File.Delete(Path.Combine(workdir, "README.md"));

        await _repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
            TargetDirectory = altDir,
        }, cancellationToken: ct);

        // File lands in altDir, not workdir.
        Assert.True(File.Exists(Path.Combine(altDir, "README.md")),
            "TargetDirectory file should exist in altDir");
        Assert.False(File.Exists(Path.Combine(workdir, "README.md")),
            "workdir should not receive the file when TargetDirectory is set");
    }

    /// <summary>
    /// <see cref="GitCheckoutOptions.TargetDirectory"/> allows checkout
    /// from a bare repository (which has no workdir) when an alternative
    /// directory is provided. Matches libgit2's
    /// <c>test_checkout_tree__target_directory_from_bare</c>:
    /// <see cref="WorkdirWriter"/> must not throw
    /// <see cref="GitErrorCode.BareRepo"/> when
    /// <see cref="GitCheckoutOptions.TargetDirectory"/> is set.
    /// </summary>
    [Fact]
    public async Task Tree_TargetDirectory_FromBare_WritesToAltDir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string barePath = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutTests_bare_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(barePath);
        try
        {
            await using GitRepository bare = await GitRepository.InitAsync(barePath, isBare: true, new GitContext(), cancellationToken: ct);
            GitOid blobOid = await bare.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
            using GitTreeBuilder tb = bare.NewTreeBuilder();
            await tb.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await tb.WriteAsync(ct);
            GitTree? tree = await bare.ObjectLookupAsync<GitTree>(treeOid, ct);
            Assert.NotNull(tree);

            string altDir = Path.Combine(barePath, "alternative");
            Directory.CreateDirectory(altDir);

            await bare.CheckoutTreeAsync(tree, new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force,
                TargetDirectory = altDir,
            }, cancellationToken: ct);

            Assert.True(File.Exists(Path.Combine(altDir, "hello.txt")),
                "TargetDirectory file should exist in altDir from a bare repo");
        }
        finally
        {
            try
            {
                Directory.Delete(barePath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ── FileMode exec-bit revert ───────────────────────────────────────

    /// <summary>
    /// <see cref="GitCheckoutStrategy.Force"/> checkout reverts the
    /// executable bit on a workdir file when the target tree's mode is
    /// non-executable (<see cref="GitFileMode.Regular"/>) but the existing
    /// workdir file is executable. libgit2 achieves this by removing the
    /// old file and creating a fresh one with the correct mode via
    /// <c>p_open(path, ..., mode)</c> (checkout.c:274-277, 1520-1548):
    /// <see cref="WorkdirWriter"/> must chmod both TO exec (target mode ==
    /// <see cref="GitFileMode.Executable"/>) and FROM exec back to non-exec.
    /// POSIX-only.
    /// </summary>
    [Fact]
    public async Task FileMode_Force_RevertsExecBitToNonExec()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // chmod exec semantics are POSIX-only
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        await WriteCommitWithFile("file.txt", "hello\n");
        string workdir = _repo.Workdir!;
        string file = Path.Combine(workdir, "file.txt");

        // Make the workdir file executable.
        File.SetUnixFileMode(file, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);

        GitReference? head = await _repo.ReferenceResolveAsync("HEAD", ct);
        Assert.NotNull(head);
        GitOid commitOid = Assert.IsType<GitDirectReference>(head).Target;
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, ct);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, ct);
        Assert.NotNull(tree);

        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: ct);

        // The exec bit should be reverted to non-exec.
        UnixFileMode mode = File.GetUnixFileMode(file);
        Assert.True((mode & UnixFileMode.UserExecute) == 0,
            $"file.txt should be reverted to non-exec by Force checkout, but stayed {mode}");
    }

    // ── UpdateOnly skips absent files ──────────────────────────────────

    /// <summary>
    /// <see cref="GitCheckoutStrategy.UpdateOnly"/> skips creating files
    /// that do not already exist in the workdir (matches libgit2's
    /// <c>checkout_safe_for_update_only</c>, checkout.c:1727-1749, called
    /// from <c>checkout_blob</c> at 1786-1803). Existing files are still
    /// updated; <c>Remove</c> actions are suppressed. Both the <c>Remove</c>
    /// suppression under <c>UpdateOnly</c> and the skipping of absent files
    /// are required.
    /// </summary>
    [Fact]
    public async Task Tree_UpdateOnly_SkipsAbsentFiles()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // main: existing.txt
        // feature: existing.txt (modified) + new.txt
        GitOid mainCommit = await WriteCommitWithFile("existing.txt", "base\n", refName: "refs/heads/main");

        // feature branch: existing.txt modified + new.txt added.
        string workdir = _repo.Workdir!;
        GitIndex idx = await _repo.GetIndexAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "existing.txt"), "changed\n", ct);
        await idx.AddByPathAsync("existing.txt", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "new.txt"), "new\n", ct);
        await idx.AddByPathAsync("new.txt", ct);
        await idx.WriteAsync(ct);
        GitOid featureTreeOid = await idx.WriteTreeAsync(ct);
        GitSignature sig = TestSig();
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = featureTreeOid,
            Parents = [mainCommit],
            Author = sig,
            Committer = sig,
            Message = "feature\n",
            UpdateRef = "refs/heads/feature",
        }, ct);

        // Force-checkout main to get a clean workdir (only existing.txt).
        GitReference? mainRef = await _repo.ReferenceLookupAsync("refs/heads/main", ct);
        GitOid mainOid = Assert.IsType<GitDirectReference>(mainRef).Target;
        Commit? mainCommitObj = await _repo.ObjectLookupAsync<Commit>(mainOid, ct);
        GitTree? mainTree = await _repo.ObjectLookupAsync<GitTree>(mainCommitObj!.Tree, ct);
        Assert.NotNull(mainTree);
        await _repo.CheckoutTreeAsync(mainTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: ct);
        Assert.False(File.Exists(Path.Combine(workdir, "new.txt")));

        // UpdateOnly checkout of feature: HEAD is still unborn, so the
        // baseline is EMPTY (checkout.c:2476-2484) and both files are ADDED
        // deltas. existing.txt exists in the workdir → the ADDED action is a
        // CONFLICT (checkout.c:508-511: IF(FORCE, UPDATE_BLOB, CONFLICT)) —
        // C-verified: git_checkout_tree(feature, UPDATE_ONLY) → -13 "1
        // conflict prevents checkout". new.txt (removed by the force checkout
        // above as a tracked-but-not-in-target file) is ADDED with no workdir
        // entry → UPDATE_BLOB (absent, so nothing to create the file for).
        GitReference? featureRef = await _repo.ReferenceLookupAsync("refs/heads/feature", ct);
        GitOid featureOid = Assert.IsType<GitDirectReference>(featureRef).Target;
        Commit? featureCommit = await _repo.ObjectLookupAsync<Commit>(featureOid, ct);
        GitTree? featureTree = await _repo.ObjectLookupAsync<GitTree>(featureCommit!.Tree, ct);
        Assert.NotNull(featureTree);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(featureTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.UpdateOnly }, cancellationToken: ct));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
        Assert.Equal("1 conflict prevents checkout", ex.Message);

        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(workdir, "existing.txt"), ct));
        Assert.False(File.Exists(Path.Combine(workdir, "new.txt")));
    }

    // ── UseOurs/Theirs preserves index conflicts ───────────────────────

    /// <summary>
    /// <see cref="GitCheckoutStrategy.UseOurs"/> writes the "ours" (stage 2)
    /// content to the workdir but PRESERVES the index conflict entries
    /// (stages 1/2/3) — the path remains conflicted in the index until the
    /// user explicitly <c>git add</c>s it. Matches libgit2's
    /// <c>checkout_conflict_update_index</c> (checkout.c:2167-2179, 2264-2265)
    /// which re-adds the stage 1/2/3 entries via
    /// <c>checkout_conflict_add</c> → <c>git_index_add</c>; the
    /// <c>git_index_conflict_remove</c> path is only used for both-deleted
    /// conflicts. <see cref="CheckoutContext"/> must NOT call
    /// <see cref="GitIndex.ConflictRemove"/> for ALL loaded conflicts before
    /// writing the resolved side, which would clear the index conflicts.
    /// </summary>
    [Fact]
    public async Task Conflict_UseOurs_PreservesIndexConflicts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Need an initial commit so checkout has a baseline tree to diff against.
        await WriteCommitWithFile("base.txt", "base\n");
        await _repo.SetHeadAsync("refs/heads/master", ct);

        // Build a 3-stage conflict in the index.
        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), ct);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), ct);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), ct);
        GitIndex idx = await _repo.GetIndexAsync(ct);
        string path = "conflict.txt";
        var ancestor = new GitIndexEntry(path, ancestorOid, GitFileMode.Regular);
        var ours = new GitIndexEntry(path, oursOid, GitFileMode.Regular);
        var theirs = new GitIndexEntry(path, theirsOid, GitFileMode.Regular);
        idx.ConflictAdd(ancestor, ours, theirs);
        await idx.WriteAsync(ct);

        await _repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts | GitCheckoutStrategy.UseOurs,
        }, cancellationToken: ct);

        // Workdir has ours content.
        Assert.Equal("ours\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, path), ct));
        // Index still shows the conflict (stages 1/2/3 preserved).
        GitIndex idxAfter = await _repo.GetIndexAsync(ct);
        Assert.True(idxAfter.HasConflicts,
            "UseOurs should preserve index conflict entries (libgit2 leaves stages 1/2/3 in the index)");
    }

    /// <summary>
    /// <see cref="GitCheckoutStrategy.UseTheirs"/> writes the "theirs"
    /// (stage 3) content and PRESERVES index conflict entries. See
    /// <see cref="Conflict_UseOurs_PreservesIndexConflicts"/> for the
    /// libgit2 reference.
    /// </summary>
    [Fact]
    public async Task Conflict_UseTheirs_PreservesIndexConflicts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await WriteCommitWithFile("base.txt", "base\n");
        await _repo.SetHeadAsync("refs/heads/master", ct);

        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), ct);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), ct);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), ct);
        GitIndex idx = await _repo.GetIndexAsync(ct);
        string path = "conflict.txt";
        var ancestor = new GitIndexEntry(path, ancestorOid, GitFileMode.Regular);
        var ours = new GitIndexEntry(path, oursOid, GitFileMode.Regular);
        var theirs = new GitIndexEntry(path, theirsOid, GitFileMode.Regular);
        idx.ConflictAdd(ancestor, ours, theirs);
        await idx.WriteAsync(ct);

        await _repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts | GitCheckoutStrategy.UseTheirs,
        }, cancellationToken: ct);

        Assert.Equal("theirs\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, path), ct));
        GitIndex idxAfter = await _repo.GetIndexAsync(ct);
        Assert.True(idxAfter.HasConflicts,
            "UseTheirs should preserve index conflict entries");
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        internal SynchronousProgress(Action<T> callback) => _callback = callback;
        void IProgress<T>.Report(T value) => _callback(value);
    }

    // ── Unborn HEAD ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.CheckoutHeadAsync"/> on an unborn HEAD
    /// (HEAD points at a branch that has no commits yet) throws
    /// <see cref="GitErrorCode.UnbornBranch"/>. Matches libgit2's
    /// <c>git_checkout_head</c> → <c>git_checkout_tree(repo, NULL, opts)</c>
    /// → <c>checkout_lookup_head_tree</c> (checkout.c:1928-1941, 2777-2783)
    /// which propagates <c>GIT_EUNBORNBRANCH</c> from
    /// <c>git_repository_head</c>.
    /// <see cref="GitRepository.ResolveHeadTreeForCheckoutAsync"/> must
    /// propagate <c>GIT_EUNBORNBRANCH</c> instead of returning null for
    /// unborn HEAD, which would make <see cref="GitRepository.CheckoutTreeAsync"/>
    /// silently no-op.
    /// </summary>
    [Fact]
    public async Task Head_UnbornHead_ThrowsUnbornBranch()
    {
        // Fresh repo with unborn HEAD — no commits.
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await _repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.UnbornBranch, ex.Code);
    }

    [Fact]
    public async Task Index_WithExplicitIndex_WritesToWorkdir()
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sig = new GitSignature("test", "test@test.com", new GitTime(1700000000, 0));
        string workdir = _repo.Workdir!;

        // Create initial commit with "hello".
        await File.WriteAllTextAsync(Path.Combine(workdir, "file.txt"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Modify and stage to get the modified tree.
        await File.WriteAllTextAsync(Path.Combine(workdir, "file.txt"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid modTreeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitTree? modTree = await _repo.ObjectLookupAsync<GitTree>(modTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(modTree);

        // Reset workdir + index to base.
        await File.WriteAllTextAsync(Path.Combine(workdir, "file.txt"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        idx.Clear();
        GitTree? baseTree = await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(baseTree);
        await idx.ReadTreeAsync(baseTree, TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Create ephemeral index with modified tree.
        var ephemeral = GitIndex.New(_repo.ObjectFormat);
        await ephemeral.ReadTreeAsync(modTree, TestContext.Current.CancellationToken);

        // Checkout the ephemeral index with Force strategy.
        await _repo.CheckoutIndexAsync(ephemeral, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir should now have the modified content.
        string content = await File.ReadAllTextAsync(Path.Combine(workdir, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("modified\n", content);
    }

    // Regression: a tree-target checkout (git_checkout_tree) with a conflicted
    // index must write the target tree content — never conflict markers. The
    // create-conflicts pass must be inert for tree checkouts (libgit2 gates it
    // on the target being an index — checkout.c:974-991 "Only write conflicts
    // from sources that have them: indexes"). See CheckoutContext.LoadUpdateConflictsAsync.
    [Fact]
    public async Task Tree_WithConflictedIndex_WritesTreeContentNotMarkers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid commitOid = await WriteCommitWithFile("file.txt", "base\n");

        // Build a mid-merge conflicted index for "file.txt" (stages 1/2/3).
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: ct);
        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "base\n"u8.ToArray(), ct);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "base\nours\n"u8.ToArray(), ct);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "base\ntheirs\n"u8.ToArray(), ct);
        idx.ConflictAdd(
            new GitIndexEntry("file.txt", ancestorOid, GitFileMode.Regular).WithStage(1),
            new GitIndexEntry("file.txt", oursOid, GitFileMode.Regular).WithStage(2),
            new GitIndexEntry("file.txt", theirsOid, GitFileMode.Regular).WithStage(3));
        await idx.WriteAsync(cancellationToken: ct);
        Assert.True(idx.HasConflicts);

        // Conflict-marker workdir file (what a merge would have produced).
        string markerContent = "<<<<<<< ours\nbase\nours\n=======\nbase\ntheirs\n>>>>>>> theirs\n";
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), markerContent, cancellationToken: ct);

        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, ct);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit!.Tree, ct);
        Assert.NotNull(tree);

        // Forced tree checkout over the conflicted index.
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, cancellationToken: ct);

        // Workdir must hold target tree content, never markers.
        string workdirContent = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: ct);
        Assert.Equal("base\n", workdirContent);
        Assert.DoesNotContain("<<<<<<<", workdirContent);
        Assert.DoesNotContain(">>>>>>>", workdirContent);

        // Conflicts dropped from the index (tree checkout removes all conflict
        // entries — checkout_remove_conflicts, checkout.c:2279-2289).
        GitIndex idxAfter = await _repo.GetIndexAsync(cancellationToken: ct);
        Assert.False(idxAfter.HasConflicts);
        Assert.NotNull(idxAfter.EntryByPath("file.txt"));
    }
}
