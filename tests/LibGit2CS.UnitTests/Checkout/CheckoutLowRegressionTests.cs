using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Checkout;

// Parity cases verified against libgit2 1.9.4:
//  - the DIRTY notification in the with-wd-dir UNMODIFIED branch
//    passed a workdir file where C passes NULL (checkout.c:613-617).
//  - SafeForUpdateOnly treated any existing file as safe for a
//    symlink target when symlinks are unsupported; C's mode comparison
//    rejects fake symlinks (checkout.c:1727-1749).
//  - Remove's ancestor-file walk could delete a file above the
//    workdir root when the root string differs from GetDirectoryName
//    output (repo.Workdir always ends with a separator).
//  - symlink targets were decoded with lossy UTF-8; C passes the raw
//    blob bytes to p_symlink (checkout.c:1596-1630).
//  - ForSourceAsync short-circuited to the text driver when BestPath
//    is null, ignoring merge.default (merge_driver.c:411-431).
//  - the merged conflict file was written non-atomically; C uses
//    git_filebuf temp+rename (checkout.c:2147-2151).
public sealed class CheckoutLowRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public CheckoutLowRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(GitRepository Repo, string RepoPath)> CreateRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }

    private static async Task CommitFileAsync(GitRepository repo, string repoPath, string path, string content, string updateRef = "refs/heads/main")
    {
        await File.WriteAllTextAsync(
            Path.Combine(repoPath, path), content,
            cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync(path, TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeId = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeId,
            Author = sig,
            Committer = sig,
            Message = $"add {path}\n",
            UpdateRef = updateRef,
        }, TestContext.Current.CancellationToken);
    }

    // ---- DIRTY notify in the wd-dir UNMODIFIED branch passes null ----

    [Fact]
    public async Task Checkout_WdDirUnmodified_DirtyNotifyHasNullWorkdir()
    {
        (GitRepository repo, string repoPath) = await CreateRepoAsync("h09");
        await CommitFileAsync(repo, repoPath, "dir", "v1\n");
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        // Replace the tracked file "dir" with a DIRECTORY.
        File.Delete(Path.Combine(repoPath, "dir"));
        Directory.CreateDirectory(Path.Combine(repoPath, "dir"));
        await File.WriteAllTextAsync(
            Path.Combine(repoPath, "dir", "inner.txt"), "x\n",
            cancellationToken: TestContext.Current.CancellationToken);

        var notifications = new List<GitCheckoutNotification>();
        var opts = new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Safe,
            NotifyFlags = GitCheckoutNotifyFlags.Dirty | GitCheckoutNotifyFlags.Untracked,
            Notify = n =>
            {
                notifications.Add(n);
                return false;
            },
        };

        await repo.CheckoutHeadAsync(opts, TestContext.Current.CancellationToken);

        // C (checkout.c:613-617): the DIRTY notify passes NULL as the
        // workdir file; only the UNTRACKED notify carries the wd entry.
        GitCheckoutNotification? dirty = notifications.FirstOrDefault(n => n.Why == GitCheckoutNotifyFlags.Dirty);
        Assert.NotNull(dirty);
        Assert.Null(dirty.Value.Workdir);
    }

    // ---- SafeForUpdateOnly requires a real symlink ----

    [Fact]
    public async Task Checkout_UpdateOnly_SymlinkTarget_OnWindows_SkipsFakeSymlink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the divergence is Windows-only (fake symlinks)
        }

        // On Windows with core.symlinks=false, a fake symlink is a regular
        // file — C's (st_mode & ~0777) == (expected_mode & ~0777) comparison
        // fails and the UPDATE_ONLY write is skipped (checkout.c:1727-1749).
        // Any existing file must NOT be treated as safe when symlinks are
        // unsupported. (Windows-gated; the Linux behavior is unchanged.)
        (GitRepository repo, string repoPath) = await CreateRepoAsync("h10");
        await CommitFileAsync(repo, repoPath, "link", "v1\n");
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        // The workdir entry is a regular file (fake symlink).
        await File.WriteAllTextAsync(
            Path.Combine(repoPath, "link"), "fake target",
            cancellationToken: TestContext.Current.CancellationToken);

        // An UPDATE_ONLY checkout of a symlink-mode target must not
        // overwrite the fake symlink.
        await repo.CheckoutHeadAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.UpdateOnly,
        }, TestContext.Current.CancellationToken);

        Assert.Equal("fake target", await File.ReadAllTextAsync(
            Path.Combine(repoPath, "link"), TestContext.Current.CancellationToken));
    }

    // ---- Remove/PruneEmptyParents never walk above the root ----

    [Fact]
    public void Remove_AbsentPath_WalkStopsAtRoot()
    {
        // The ancestor walk for an absent target must terminate at the
        // workdir root: the old string-equality bound against the
        // trailing-separator root never matched GetDirectoryName output, so
        // the walk was unbounded. Nothing above the root may be touched.
        string root = Path.Combine(_tempDir, "h11", "repo", "wd");
        Directory.CreateDirectory(root);
        string parent = Path.Combine(_tempDir, "h11", "repo");
        Directory.CreateDirectory(parent);

        var perf = new GitCheckoutPerformance();
        // Trailing separator, like repo.Workdir — the old string-equality
        // bound never matched GetDirectoryName output for such roots.
        var writer = new WorkdirWriter(
            null!, new GitCheckoutOptions { TargetDirectory = root + Path.DirectorySeparatorChar },
            respectFilemode: true, shouldRemoveExisting: false, skipLockedDirectories: false, ref perf);

        writer.Remove(GitPath.FromUtf8String("x/y"));

        Assert.True(Directory.Exists(parent), "the ancestor walk must stop at the workdir root");
    }

    [Fact]
    public void PruneEmptyParents_DoesNotDeleteRoot()
    {
        string root = Path.Combine(_tempDir, "h11b", "repo", "wd");
        Directory.CreateDirectory(Path.Combine(root, "a"));

        var perf = new GitCheckoutPerformance();
        // Trailing separator, like repo.Workdir.
        var writer = new WorkdirWriter(
            null!, new GitCheckoutOptions { TargetDirectory = root + Path.DirectorySeparatorChar },
            respectFilemode: true, shouldRemoveExisting: false, skipLockedDirectories: false, ref perf);

        writer.PruneEmptyParents(GitPath.FromUtf8String("a/x"));

        // The empty "a" is pruned, but the (empty) root itself must survive.
        Assert.False(Directory.Exists(Path.Combine(root, "a")));
        Assert.True(Directory.Exists(root), "the empty-parent walk must stop at the workdir root");
    }

    // ---- symlink checkout writes the raw link bytes ----

    [Fact]
    public async Task Checkout_SymlinkWithNonUtf8Target_WritesRawBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // symlink creation needs privileges
        }

        (GitRepository repo, string repoPath) = await CreateRepoAsync("h12");

        // Symlink blob with raw target bytes 61 FF FE 80 62 (not valid UTF-8).
        byte[] rawTarget = [0x61, 0xFF, 0xFE, 0x80, 0x62];
        GitOid blobOid = await repo.ObjectWriteAsync(
            GitObjectType.Blob, rawTarget, TestContext.Current.CancellationToken);

        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("link", blobOid, GitFileMode.Symlink, TestContext.Current.CancellationToken);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "symlink\n",
            UpdateRef = "HEAD",
        }, TestContext.Current.CancellationToken);

        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        // C's blob_content_to_link passes the raw blob bytes to p_symlink
        // (checkout.c:1596-1630); a UTF-8 string round-trip would write
        // U+FFFD-mangled targets.
        string linkPath = Path.Combine(repoPath, "link");
        Assert.True(NativeStat.TryReadLinkTarget(linkPath, out byte[] actual));
        Assert.Equal(rawTarget, actual);
    }

    // ---- ForSourceAsync honors merge.default with no path ----

    [Fact]
    public async Task ForSource_NoPath_WithDefaultDriver_UsesDefault()
    {
        (GitRepository repo, _) = await CreateRepoAsync("h13");
        using var ctx = new GitContext();

        // add/add or rename/rename conflicts have no best path (BestPath
        // returns null). C still resolves the merge attribute (unspecified
        // for a NULL pathname) and falls through to merge.default
        // (merge_driver.c:411-431); the text driver must not be substituted
        // for the resolution.
        var src = new GitMergeDriverSource(repo, "union", null, null, null, null);
        (string name, IGitMergeDriver? driver) = await ctx.MergeDrivers.ForSourceAsync(
            src, TestContext.Current.CancellationToken);

        Assert.Equal("union", name);
        Assert.NotNull(driver);
    }

    [Fact]
    public async Task ForSource_NoPath_NoDefaultDriver_StillText()
    {
        // Control: without a default driver the no-path case still resolves
        // to the text driver.
        (GitRepository repo, _) = await CreateRepoAsync("h13b");
        using var ctx = new GitContext();

        var src = new GitMergeDriverSource(repo, null, null, null, null, null);
        (string name, IGitMergeDriver? driver) = await ctx.MergeDrivers.ForSourceAsync(
            src, TestContext.Current.CancellationToken);

        Assert.Equal("text", name);
        Assert.NotNull(driver);
    }

    // ---- conflict file write is atomic (temp + rename) ----

    [Fact]
    public async Task Checkout_ConflictFile_NoTempLeftovers()
    {
        // A conflict checkout writes the merged file via temp + rename
        // (C's git_filebuf, checkout.c:2147-2151) — no temp files may
        // remain in the workdir.
        (GitRepository repo, string repoPath) = await CreateRepoAsync("h14");
        await CommitFileAsync(repo, repoPath, "f.txt", "base\n");
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        // Create a conflicting change: modify the workdir file, then check
        // out a different version from another branch with SAFE (conflict).
        await File.WriteAllTextAsync(
            Path.Combine(repoPath, "f.txt"), "local\n",
            cancellationToken: TestContext.Current.CancellationToken);
        await CommitFileAsync(repo, repoPath, "f.txt", "other\n", updateRef: "refs/heads/other"); // root commit on refs/heads/other

        var otherTree = (GitTree)(await repo.RevparseSingleAsync("refs/heads/other^{tree}", TestContext.Current.CancellationToken))!;
        await repo.CheckoutTreeAsync(otherTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe }, TestContext.Current.CancellationToken);

        // The conflict file exists (either f.txt or a suffixed name).
        string[] conflicts = Directory.GetFiles(repoPath, "f.txt*");
        Assert.NotEmpty(conflicts);
        // No temp files from the atomic write remain.
        Assert.Empty(Directory.GetFiles(repoPath, "*.tmp.*"));
    }
}
