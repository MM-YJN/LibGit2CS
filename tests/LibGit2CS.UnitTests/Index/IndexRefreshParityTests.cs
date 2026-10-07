using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Index;

/// <summary>
/// Regression tests for the index and checkout behaviors: <c>git_index_read_safely</c>/refresh semantics,
/// <c>git_indexwriter_init_for_operation</c> early-lock + should_write
/// equivalence for merge/cherry-pick/revert, the index lock error text, and
/// the REUC write-order normalization. Expectations are C-verified against
/// libgit2 1.9.4 via a C probe harness and index.c / checkout.c code reading.
/// </summary>
public sealed class IndexRefreshParityTests
{
    private static GitSignature TestSig() => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async Task<(GitRepository repo, string path)> InitRepoAsync()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexRefresh_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }

    /// <summary>Writes a.txt + commits on master, returns the tree.</summary>
    private static async Task<GitTree> CommitAsync(GitRepository repo, string content)
    {
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("a.txt", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);
        return (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
    }

    /// <summary>Creates a side branch off master with different a.txt content.</summary>
    private static async Task<GitOid> SideBranchAsync(GitRepository repo, string content)
    {
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("a.txt", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitOid master = ((GitDirectReference)(await repo.ReferenceLookupAsync("refs/heads/master", TestContext.Current.CancellationToken))!).Target;
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [master],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "side\n",
            UpdateRef = "refs/heads/side",
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task CleanupAsync(GitRepository repo, string repoPath)
    {
        await repo.DisposeAsync();
        try
        {
            Directory.Delete(repoPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── read_safely: unchanged file keeps in-memory (dirty) entries ──────

    [Fact]
    public async Task CheckoutTree_DirtyIndexFileUnchanged_KeepsInMemoryEntry()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            GitTree tree = await CommitAsync(repo, "one\n");
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "a.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);

            // Stage an entry WITHOUT writing the index to disk (dirty).
            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "staged\n"u8.ToArray(), TestContext.Current.CancellationToken);
            index.Add(new GitIndexEntry("staged.txt", blob, GitFileMode.Regular));

            // C (index.c:717-726): git_index_read_safely with the
            // unsaved-safety flag off (the 1.9.4 default) keeps the in-memory
            // entries when the file is unchanged. The checkout itself blocks
            // on the index/workdir divergence ("1 conflict prevents
            // checkout"), but the staged entry must survive the refresh.
            GitException ex = await Assert.ThrowsAsync<GitException>(() => repo.CheckoutTreeAsync(
                tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
                TestContext.Current.CancellationToken));

            Assert.True((await repo.GetIndexAsync(TestContext.Current.CancellationToken)).Find("staged.txt") >= 0);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    [Fact]
    public async Task StatusRefresh_MissingIndexFile_KeepsInMemoryEntries()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            await CommitAsync(repo, "one\n");
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "a.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);

            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "staged\n"u8.ToArray(), TestContext.Current.CancellationToken);
            index.Add(new GitIndexEntry("staged.txt", blob, GitFileMode.Regular));

            // Delete the index file; C (index.c:672-678): with force=0 the
            // in-memory entries are KEPT when the file is absent.
            File.Delete(Path.Combine(repo.Path, "index"));

            using LibGit2CS.Status.GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
            _ = list.Entries;

            Assert.True((await repo.GetIndexAsync(TestContext.Current.CancellationToken)).Find("staged.txt") >= 0);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── indexwriter early lock (merge/cherry-pick) ───────────────────────

    [Fact]
    public async Task Merge_LockedIndex_FailsEarlyWithoutMergeHead()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            await CommitAsync(repo, "one\n");
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "a.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);
            GitOid side = await SideBranchAsync(repo, "two\n");

            // C: the lock is taken BEFORE the merge state files —
            // the merge fails with GIT_ELOCKED and no MERGE_HEAD is created.
            string lockPath = Path.Combine(repo.Path, "index.lock");
            await File.WriteAllTextAsync(lockPath, "", cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                using GitAnnotatedCommit theirs = await repo.AnnotatedCommitLookupAsync(side, TestContext.Current.CancellationToken);
                GitException ex = await Assert.ThrowsAsync<GitException>(() => repo.MergeAsync([theirs], cancellationToken: TestContext.Current.CancellationToken));
                Assert.Equal(GitErrorCode.Locked, ex.Code);
                Assert.Equal(GitErrorCategory.Index, ex.Category);
                Assert.Equal("the index is locked; this might be due to a concurrent or crashed process", ex.Message);
            }
            finally
            {
                File.Delete(lockPath);
            }

            Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_HEAD")));
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    [Fact]
    public async Task CherryPick_LockedIndex_FailsEarlyWithoutCherryPickHead()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            await CommitAsync(repo, "one\n");
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "a.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);
            GitOid side = await SideBranchAsync(repo, "two\n");
            Commit sideCommit = (await repo.ObjectLookupAsync<Commit>(side, TestContext.Current.CancellationToken))!;

            // C: the lock is taken BEFORE CHERRY_PICK_HEAD is
            // written.
            string lockPath = Path.Combine(repo.Path, "index.lock");
            await File.WriteAllTextAsync(lockPath, "", cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                GitException ex = await Assert.ThrowsAsync<GitException>(() => repo.CherryPickAsync(sideCommit, cancellationToken: TestContext.Current.CancellationToken));
                Assert.Equal(GitErrorCode.Locked, ex.Code);
                Assert.Equal(GitErrorCategory.Index, ex.Category);
            }
            finally
            {
                File.Delete(lockPath);
            }

            Assert.False(File.Exists(Path.Combine(repo.Path, "CHERRY_PICK_HEAD")));
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── should_write: DONT_WRITE_INDEX leaves the index file untouched ───

    [Fact]
    public async Task Merge_DontWriteIndex_LeavesIndexFileUntouched()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            await CommitAsync(repo, "one\n");
            // Populate the workdir + index from HEAD so the merge has a clean
            // base (the cached index starts empty until a checkout runs).
            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);
            byte[] before = await File.ReadAllBytesAsync(Path.Combine(repo.Path, "index"), TestContext.Current.CancellationToken);

            GitOid side = await SideBranchAsync(repo, "two\n");
            using GitAnnotatedCommit theirs = await repo.AnnotatedCommitLookupAsync(side, TestContext.Current.CancellationToken);
            await repo.MergeAsync(
                [theirs],
                checkoutOpts: new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe | GitCheckoutStrategy.DontWriteIndex },
                cancellationToken: TestContext.Current.CancellationToken);

            // C (index.c:3891): should_write = false → the index file is NOT
            // rewritten even though the merge succeeded.
            byte[] after = await File.ReadAllBytesAsync(Path.Combine(repo.Path, "index"), TestContext.Current.CancellationToken);
            Assert.Equal(before, after);
            Assert.False(File.Exists(Path.Combine(repo.Path, "index.lock")));
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── lock error message ───────────────────────────────────────────────

    [Fact]
    public async Task IndexWrite_Locked_ThrowsCMessage()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            await CommitAsync(repo, "one\n");
            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            string lockPath = Path.Combine(repo.Path, "index.lock");
            await File.WriteAllTextAsync(lockPath, "", cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                // C: err=-14 "the index is locked; this might be due
                // to a concurrent or crashed process".
                GitException ex = await Assert.ThrowsAsync<GitException>(() => index.WriteAsync(TestContext.Current.CancellationToken));
                Assert.Equal(GitErrorCode.Locked, ex.Code);
                Assert.Equal(GitErrorCategory.Index, ex.Category);
                Assert.Equal("the index is locked; this might be due to a concurrent or crashed process", ex.Message);
            }
            finally
            {
                File.Delete(lockPath);
            }
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── REUC write order follows the active comparator ───────────────────

    [Fact]
    public async Task ReucWrite_IgnoreCaseSwitch_WritesIcaseOrder()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            GitOid zero = GitOid.Empty;

            // On Windows the init probe writes core.ignorecase=true (NTFS),
            // so the index is already case-insensitive; on POSIX it starts
            // case-sensitive. Force the case-sensitive state first so the
            // switch below is observable on every platform.
            index.IgnoreCase = false;

            // Case-sensitive insertion order: "B.txt" sorts before "a.txt".
            index.ReucAdd("B.txt", 0x81A4, zero, 0, zero, 0, zero);
            index.ReucAdd("a.txt", 0x81A4, zero, 0, zero, 0, zero);
            Assert.Equal("B.txt", index.ReucEntries[0].Path.ToString());

            // C (index.c:384-387): switching to case-insensitive re-sorts the
            // REUC vector with the icase comparator — "a.txt" first.
            index.IgnoreCase = true;
            Assert.Equal("a.txt", index.ReucEntries[0].Path.ToString());

            // C (index.c:3899-3900): the writer re-sorts with the active
            // comparator, so the on-disk REUC extension is in icase order.
            await index.WriteAsync(TestContext.Current.CancellationToken);

            GitIndex reread = await GitIndex.OpenAsync(Path.Combine(repo.Path, "index"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
            Assert.Equal(2, reread.ReucCount);
            Assert.Equal("a.txt", reread.ReucEntries[0].Path.ToString());
            Assert.Equal("B.txt", reread.ReucEntries[1].Path.ToString());
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }
}
