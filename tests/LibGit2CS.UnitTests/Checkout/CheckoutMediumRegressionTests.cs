using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Checkout;

// Regression coverage against libgit2 1.9.4.
// parity behaviors for Checkout:
//   Missing-blob lookups in conflict binary-detection (and conflict
//         side merges) propagate git_blob_lookup
//         errors (checkout.c:874-909, merge_file.c:29-44).
//   Conflict passes must honor pathspec filtering, the index-target
//         remove set, and SKIP_UNMERGED (checkout.c:958, 1277-1286,
//         1233-1250).
//   The submodule pass must create the directory and not call
//         sm.UpdateAsync — a network clone/fetch during checkout (C:
//         checkout_submodule, checkout.c:1676-1715).
//   GIT_CHECKOUT_SKIP_LOCKED_DIRECTORIES must keep locked (non-empty)
//         directories instead of force-deleting them (checkout.c:1831-1832).
//   GIT_CHECKOUT_NONE must be honored by the action tables
//         (checkout.c:297, 500, 572, 609).
//   RunIndexAsync must honor index-on-disk state / RECREATE_MISSING
//         (checkout.c:2451-2454, 2476-2483).
public sealed class CheckoutMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public CheckoutMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature Sig() => new("t", "t@t", new GitTime(1700000000, 0));

    private async Task<GitRepository> InitRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, string fileName, string content)
    {
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitReference? head = await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = Sig(),
            Committer = Sig(),
            Message = $"commit {fileName}\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        return commitOid;
    }

    /// <summary>Creates a 3-stage conflict at <paramref name="path"/> in the repo index.</summary>
    private static async Task AddConflictAsync(GitRepository repo, string path, GitOid ancestorOid, GitOid oursOid, GitOid theirsOid)
    {
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        GitFileMode mode = GitFileMode.Regular;
        index.ConflictAdd(
            new GitIndexEntry(path, ancestorOid, mode).WithStage(1),
            new GitIndexEntry(path, oursOid, mode).WithStage(2),
            new GitIndexEntry(path, theirsOid, mode).WithStage(3));
        await index.WriteAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<GitTree> HeadTreeAsync(GitRepository repo)
    {
        GitReference? head = await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken);
        Commit? commit = await repo.ObjectLookupAsync<Commit>(((GitDirectReference)head!).Target, TestContext.Current.CancellationToken);
        return (await repo.ObjectLookupAsync<GitTree>(commit!.Tree, TestContext.Current.CancellationToken))!;
    }

    // ── Missing Conflict Blob Fails Checkout ─────────────────────────

    [Fact]
    public async Task MissingConflictBlob_FailsCheckout()
    {
        await using GitRepository repo = await InitRepoAsync("h03");
        await CommitFileAsync(repo, "f.txt", "base\n");
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "base\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // A conflict whose "theirs" side references a blob that is NOT in the
        // ODB — C propagates git_blob_lookup's failure (checkout.c:874-909).
        var missing = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);
        await AddConflictAsync(repo, "f.txt", blobOid, blobOid, missing);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.CheckoutIndexAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.AllowConflicts }, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── Pathspec Limited Checkout Skips Out Of Scope Conflicts ───────

    [Fact]
    public async Task PathspecLimitedCheckout_SkipsOutOfScopeConflicts()
    {
        await using GitRepository repo = await InitRepoAsync("h04a");
        await CommitFileAsync(repo, "a.txt", "a\n");
        await CommitFileAsync(repo, "b.txt", "b\n");
        await CommitFileAsync(repo, "base.txt", "base\n");
        GitOid ancestor = await repo.ObjectWriteAsync(GitObjectType.Blob, "base\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid ours = await repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirs = await repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), TestContext.Current.CancellationToken);

        await AddConflictAsync(repo, "a.txt", ancestor, ours, theirs);
        await AddConflictAsync(repo, "b.txt", ancestor, ours, theirs);

        // C filters BOTH conflict passes by the pathspec (checkout.c:958 via
        // checkout_get_remove_conflicts/update_conflicts) — only a.txt is
        // touched: a.txt gets conflict markers, b.txt is untouched (no
        // markers, index conflict preserved).
        await repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts,
            PathsStrings = ["a.txt"],
        }, TestContext.Current.CancellationToken);

        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.True(index.HasConflicts, "b.txt conflict must remain");
        (_, GitIndexEntry? aOurs, _) = index.ConflictGet("a.txt");
        (_, GitIndexEntry? bOurs, _) = index.ConflictGet("b.txt");
        Assert.True(aOurs is not null, "a.txt stages re-added by the update pass");
        Assert.True(bOurs is not null, "b.txt conflict untouched");

        // Out-of-scope b.txt must not have conflict markers written.
        string bContent = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "b.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("<<<<<<<", bContent);
        Assert.Equal("b\n", bContent);

        // In-scope a.txt DOES get conflict markers.
        string aContent = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "a.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("<<<<<<<", aContent);
    }

    [Fact]
    public async Task ExplicitIndexCheckout_DropsRepoConflictsAbsentFromTarget()
    {
        await using GitRepository repo = await InitRepoAsync("h04b");
        await CommitFileAsync(repo, "a.txt", "a\n");
        await CommitFileAsync(repo, "b.txt", "b\n");
        GitOid blobA = await repo.ObjectWriteAsync(GitObjectType.Blob, "a\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobB = await repo.ObjectWriteAsync(GitObjectType.Blob, "b\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await AddConflictAsync(repo, "a.txt", blobA, blobA, blobA);
        await AddConflictAsync(repo, "b.txt", blobB, blobB, blobB);

        // Target index: a fresh empty index. C removes ALL repo-index
        // conflicts (checkout_get_remove_conflicts iterates data->index,
        // checkout.c:1277-1286) and re-adds only the target stages — with an
        // empty target, every conflict is dropped.
        using var target = GitIndex.New(repo.ObjectFormat);

        await repo.CheckoutIndexAsync(target, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.AllowConflicts,
        }, TestContext.Current.CancellationToken);

        GitIndex repoIndex = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.False(repoIndex.HasConflicts, "repo conflicts absent from the target index must be dropped");
    }

    [Fact]
    public async Task SkipUnmerged_LeavesRemovePassAlone()
    {
        await using GitRepository repo = await InitRepoAsync("h04c");
        await CommitFileAsync(repo, "a.txt", "a\n");
        GitOid blobA = await repo.ObjectWriteAsync(GitObjectType.Blob, "a\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await AddConflictAsync(repo, "a.txt", blobA, blobA, blobA);

        // C gates checkout_get_update_conflicts on SKIP_UNMERGED
        // (checkout.c:1240-1241) but NOT the remove pass — conflicts are
        // cleared from the index and nothing is re-added.
        await repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.SkipUnmerged | GitCheckoutStrategy.AllowConflicts,
        }, TestContext.Current.CancellationToken);

        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.False(index.HasConflicts, "SKIP_UNMERGED clears conflicts via the remove pass (checkout.c:1240, 1277)");
    }

    // ── Submodule Checkout Creates Directory And Stats Index ─────────

    [Fact]
    public async Task SubmoduleCheckout_CreatesDirectory_AndStatsIndex()
    {
        await using GitRepository repo = await InitRepoAsync("h05");
        await CommitFileAsync(repo, "f.txt", "base\n");

        // A tree containing a gitlink with NO .gitmodules entry — C
        // "just makes an empty directory" (checkout.c:1687-1693).
        GitOid dummy = GitObjectDb.HashObject(GitObjectType.Commit, "x"u8.ToArray(), repo.ObjectFormat);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        var gitlink = new GitIndexEntry("sub", dummy, GitFileMode.GitLink);
        index.Add(gitlink);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        await repo.CheckoutTreeAsync(tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        // The directory exists...
        Assert.True(Directory.Exists(Path.Combine(repo.Workdir!, "sub")), "checkout must create the submodule directory");

        // ...and the index entry carries the directory stat
        // (checkout_submodule_update_index, checkout.c:1650-1674).
        GitIndex idx2 = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        GitIndexEntry? entry = idx2.EntryByPath("sub");
        Assert.NotNull(entry);
        Assert.NotEqual(0, entry.Value.Mtime.Seconds);
    }

    // ── Skip Locked Directories Keeps Non Empty Directory ────────────

    [Fact]
    public async Task SkipLockedDirectories_KeepsNonEmptyDirectory()
    {
        // The C reference tests this on Windows (a directory held in use);
        // on POSIX the equivalent "cannot be emptied" condition is a
        // subdirectory the walk cannot write into (its entries cannot be
        // unlinked). The SKIP_NONEMPTY behavior must keep such
        // directories instead of failing or force-deleting them.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // permission-based locking needs POSIX non-root
        }

        await using GitRepository repo = await InitRepoAsync("h06");
        await CommitFileAsync(repo, "f.txt", "base\n");

        string locked = Path.Combine(repo.Workdir!, "locked");
        string sub = Path.Combine(locked, "sub");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "keep.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {
            await repo.CheckoutTreeAsync(
                await HeadTreeAsync(repo),
                new GitCheckoutOptions
                {
                    Strategy = GitCheckoutStrategy.RemoveUntracked | GitCheckoutStrategy.SkipLockedDirectories,
                }, TestContext.Current.CancellationToken);

            // C leaves the locked (un-emptiable) directory in place
            // (checkout.c:1831-1832 + futils SKIP_NONEMPTY).
            Assert.True(Directory.Exists(locked), "locked directory must survive a SKIP_LOCKED_DIRECTORIES checkout");
            Assert.True(File.Exists(Path.Combine(sub, "keep.txt")));
        }
        finally
        {
            File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
    }

    // ── None Strategy Checkout Writes Nothing ────────────────────────

    [Fact]
    public async Task NoneStrategy_CheckoutWritesNothing()
    {
        await using GitRepository repo = await InitRepoAsync("h07");
        await CommitFileAsync(repo, "f.txt", "one\n");

        // Second commit changes the file; checkout back to the FIRST tree
        // with GIT_CHECKOUT_NONE — a no-op in C (checkout.c:297, 500, 572,
        // 609).
        await CommitFileAsync(repo, "f.txt", "two\n");
        GitReference? head = await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken);
        Commit? headCommit = await repo.ObjectLookupAsync<Commit>(((GitDirectReference)head!).Target, TestContext.Current.CancellationToken);
        Commit? parent = await repo.ObjectLookupAsync<Commit>(headCommit!.ParentId(0), TestContext.Current.CancellationToken);
        GitTree? firstTree = await repo.ObjectLookupAsync<GitTree>(parent!.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(firstTree);

        await repo.CheckoutTreeAsync(firstTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.None }, TestContext.Current.CancellationToken);

        // The workdir file must be untouched (still "two\n").
        string content = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("two\n", content);
    }

    // ── Missing Index File Checkout Index Does Not Remove Workdir ────

    [Fact]
    public async Task MissingIndexFile_CheckoutIndex_DoesNotRemoveWorkdir()
    {
        await using GitRepository repo = await InitRepoAsync("h08");
        await CommitFileAsync(repo, "f.txt", "one\n");

        // Delete the on-disk index and REOPEN the repo so the index is
        // freshly loaded as empty: C treats this as an initial checkout
        // (RECREATE_MISSING, empty baseline — checkout.c:2451-2454, 2476-
        // 2483), not as a baseline where every file is DELETED.
        string indexPath = Path.Combine(repo.Path, "index");
        File.Delete(indexPath);
        string repoPath = repo.Path;
        string workdir = repo.Workdir!;
        await repo.DisposeAsync();

        await using GitRepository reopened = await GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken);
        await reopened.CheckoutIndexAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(workdir, "f.txt")), "workdir file must survive a missing-index checkout");
    }
}
