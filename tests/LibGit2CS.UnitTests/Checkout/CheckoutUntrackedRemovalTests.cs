using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitCheckoutOptions = LibGit2CS.Checkout.GitCheckoutOptions;
using GitCheckoutStrategy = LibGit2CS.Checkout.GitCheckoutStrategy;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary>
/// Tests for untracked/ignored removal via the workdir lockstep walk.
/// Verifies that <see cref="GitCheckoutStrategy.RemoveUntracked"/>/
/// <see cref="GitCheckoutStrategy.RemoveIgnored"/> correctly remove workdir-only
/// files (not in the baseline→target diff) via the dedicated
/// <c>HandleWorkdirOnly</c> pass.
/// </summary>
public sealed class CheckoutUntrackedRemovalTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public CheckoutUntrackedRemovalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_UntrackedRem_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommitWithFile(string fileName, string content)
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync();
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName);
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        });
    }

    private List<string> WorkdirFiles()
    {
        var result = new List<string>();
        if (_repo.Workdir is null)
        {
            return result;
        }

        foreach (string f in Directory.EnumerateFiles(_repo.Workdir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_repo.Workdir, f);
            // Skip .git/ directory contents (but not .gitignore or .gitattributes).
            if (rel.StartsWith(".git/", StringComparison.Ordinal) || rel == ".git")
            {
                continue;
            }

            result.Add(rel);
        }

        return result;
    }

    // ── RemoveUntracked ─────────────────────────────────────────────────

    [Fact]
    public async Task RemoveUntracked_Flag_RemovesUntrackedFiles()
    {
        await WriteCommitWithFile("tracked.txt", "tracked content\n");

        // Add an untracked file to the workdir.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "untracked\n", cancellationToken: TestContext.Current.CancellationToken);

        // Checkout HEAD with RemoveUntracked + Force.
        var head = (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(head.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RemoveUntracked,
        }, cancellationToken: TestContext.Current.CancellationToken);

        List<string> files = WorkdirFiles();
        Assert.Contains("tracked.txt", files);
        Assert.DoesNotContain("untracked.txt", files);
    }

    [Fact]
    public async Task NoRemoveUntracked_PreservesUntracked()
    {
        await WriteCommitWithFile("tracked.txt", "tracked content\n");

        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "untracked\n", cancellationToken: TestContext.Current.CancellationToken);

        var head = (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(head.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
        }, cancellationToken: TestContext.Current.CancellationToken);

        List<string> files = WorkdirFiles();
        Assert.Contains("tracked.txt", files);
        Assert.Contains("untracked.txt", files);
    }

    [Fact]
    public async Task RemoveUntracked_RemovesUntrackedInSubdirectory()
    {
        await WriteCommitWithFile("tracked.txt", "content\n");

        Directory.CreateDirectory(Path.Combine(_repo.Workdir!, "subdir"));
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "subdir", "untracked.txt"), "untracked\n", cancellationToken: TestContext.Current.CancellationToken);

        var head = (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(head.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RemoveUntracked,
        }, cancellationToken: TestContext.Current.CancellationToken);

        List<string> files = WorkdirFiles();
        Assert.DoesNotContain(Path.Join("subdir", "untracked.txt"), files);
    }

    // ── RemoveIgnored ───────────────────────────────────────────────────

    [Fact]
    public async Task RemoveIgnored_Flag_RemovesIgnoredFiles()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await WriteCommitWithFile("tracked.txt", "content\n");

        // Set up a .gitignore and an ignored file.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, ".gitignore"), "*.log\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(".gitignore", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Re-commit with .gitignore.
        var head = (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [head!.Target],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "add gitignore\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "debug.log"), "log\n", cancellationToken: TestContext.Current.CancellationToken);

        head = (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(head.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        var notified = new List<(GitCheckoutNotifyFlags Flags, string Path)>();

        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RemoveIgnored,
            NotifyFlags = GitCheckoutNotifyFlags.Ignored | GitCheckoutNotifyFlags.Untracked,
            Notify = n =>
            {
                notified.Add((n.Why, n.Path.ToUtf8String()));
                return false;
            },
        }, cancellationToken: TestContext.Current.CancellationToken);

        List<string> files = WorkdirFiles();
        Assert.DoesNotContain("debug.log", files);
        Assert.Contains(".gitignore", files);
        Assert.Contains("tracked.txt", files);
    }

    // ── Safety: .git and submodules ─────────────────────────────────────

    [Fact]
    public async Task RemoveUntracked_DoesNotRemoveDotGit()
    {
        await WriteCommitWithFile("tracked.txt", "content\n");

        var head = (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(head.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RemoveUntracked,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // .git directory must survive.
        Assert.True(Directory.Exists(Path.Combine(_repo.Workdir!, ".git")));
    }

    // ── Untracked file just written by checkout is not removed ──────────

    [Fact]
    public async Task RemoveUntracked_AfterCheckout_WritesRemovesCorrectly()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        // Commit two files.
        await WriteCommitWithFile("file_a.txt", "a\n");
        GitOid firstHead = ((await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference)!.Target;

        // Add file_b and commit.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file_b.txt"), "b\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file_b.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [firstHead],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "add file_b\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Add an untracked file.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "u\n", cancellationToken: TestContext.Current.CancellationToken);

        // Checkout HEAD (both files) with RemoveUntracked.
        var head = (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        Assert.NotNull(head);
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(head.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        await _repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RemoveUntracked,
        }, cancellationToken: TestContext.Current.CancellationToken);

        List<string> files = WorkdirFiles();
        // Both tracked files should survive.
        Assert.Contains("file_a.txt", files);
        Assert.Contains("file_b.txt", files);
        // Untracked should be removed.
        Assert.DoesNotContain("untracked.txt", files);
    }
}
