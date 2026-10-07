using System.Security.Cryptography;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Index;

/// <summary> Parity tests for merge, checkout, and index: (gitlink workdir-modified guard), (merge result null/zero → Conflict),
/// (UPDATE_ONLY submodule gate), (DISABLE_PATHSPEC_MATCH propagation), (DONT_UPDATE_INDEX conflict removal), (merge rename similarity without
/// AllowSmallFiles), (v4 extended-flag re-sync), (unsigned extension size), (tree-cache missing subtree). </summary>
public sealed class MergeCheckoutMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public MergeCheckoutMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeCheckoutMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string NewDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async ValueTask<GitRepository> InitRepoAsync(string dir)
        => await GitRepository.InitAsync(dir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

    // ── merge similarity has no AllowSmallFiles ────────────────

    [Fact]
    public void MergeSimilarity_TinyFile_InvalidMarker()
    {
        // C (hashsig.c:219-223): < 4 buckets + no ALLOW_SMALL_FILES → GIT_EBUFS → the merge path scores 0.
        Assert.Null(LibGit2CS.Core.SimilarityHash.Create("tiny\n"u8, LibGit2CS.Core.SimilarityHashOptions.SmartWhitespace));

        // Sanity: the diff path's flag combination still produces a signature.
        Assert.NotNull(LibGit2CS.Core.SimilarityHash.Create("tiny\n"u8, LibGit2CS.Core.SimilarityHashOptions.SmartWhitespace | LibGit2CS.Core.SimilarityHashOptions.AllowSmallFiles));
    }

    // ── v4 index extended-flag re-sync is v2/v3-only ───────────

    [Fact]
    public async Task Index_V4_ExtendedFlagNotResynced()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.SetVersion(4);

        // An entry with EXTENDED set but no extended flags at all.
        idx.Add(new GitIndexEntry("a.txt", GitOid.Parse("1111111111111111111111111111111111111111", GitHashAlgorithmKind.Sha1), GitFileMode.Regular)
        {
            Flags = GitIndexEntry.Extended,
            FlagsExtended = 0,
        });

        await idx.WriteAsync(TestContext.Current.CancellationToken);

        // Re-read: C keeps the EXTENDED bit + the 2-byte flags_extended for v4 (index.c:3238-3243).
        GitIndex reRead = await GitIndex.OpenAsync(System.IO.Path.Combine(repo.Path, "index"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitIndexEntry entry = reRead.EntryByPath("a.txt", stage: 0)!.Value;
        Assert.True(entry.HasExtended);
    }

    // ── high-bit extension size → GitException, not a crash ───

    [Fact]
    public async Task Index_HighBitExtensionSize_TruncatedGitException()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("a.txt", GitOid.Parse("1111111111111111111111111111111111111111", GitHashAlgorithmKind.Sha1), GitFileMode.Regular));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        string indexPath = System.IO.Path.Combine(repo.Path, "index");
        byte[] good = await File.ReadAllBytesAsync(indexPath, TestContext.Current.CancellationToken);

        // Append a TREE extension whose size has the high bit set — C reads it unsigned and fails with "extension is truncated";
        // and throw ArgumentOutOfRangeException.
        var ms = new MemoryStream();
        ms.Write(good);
        ms.Write(System.Text.Encoding.ASCII.GetBytes("TREE"));
        Span<byte> sizeBuf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sizeBuf, 0x8000_0000u);
        ms.Write(sizeBuf);
        byte[] corrupt = ms.ToArray();
        byte[] hash = SHA1.HashData(corrupt.AsSpan(0, corrupt.Length - 20));
        hash.CopyTo(corrupt, corrupt.Length - 20);
        await File.WriteAllBytesAsync(indexPath, corrupt, TestContext.Current.CancellationToken);

        GitException ex = Assert.ThrowsAny<GitException>(() =>
            GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        Assert.Contains("extension is truncated", ex.Message);
    }

    // ── tree-cache read aborts on a missing subtree ───────────

    [Fact]
    public async Task TreeCache_ReadTree_MissingSubtree_Throws()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);

        // A raw tree referencing a subtree OID that does not exist in the
        // ODB (the tree BUILDER validates, so craft the bytes).
        var missingSub = GitOid.Parse("2222222222222222222222222222222222222222", GitHashAlgorithmKind.Sha1);
        byte[] rawTree = [.. "40000 sub\0"u8.ToArray(), .. missingSub.RawBytes.ToArray()];
        GitOid treeOid = await repo.ObjectWriteAsync(GitObjectType.Tree, rawTree, TestContext.Current.CancellationToken);
        using GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        // C (tree-cache.c:216-218): a missing subtree aborts the cache build.
        await Assert.ThrowsAsync<GitException>(async () =>
            await TreeCache.ReadTreeAsync(tree, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
    }

    // ── dirty submodule workdir does not conflict a gitlink ────

    [Fact]
    public async Task Checkout_DirtySubmodule_MovesGitlink()
    {
        string subDir = NewDir();
        GitOid subOid1, subOid2;
        await using (GitRepository sub = await InitRepoAsync(subDir))
        {
            await File.WriteAllTextAsync(Path.Combine(sub.Workdir!, "f.txt"), "v1\n", TestContext.Current.CancellationToken);
            GitIndex sidx = await sub.GetIndexAsync(TestContext.Current.CancellationToken);
            await sidx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await sidx.WriteAsync(TestContext.Current.CancellationToken);
            GitOid t1 = await sidx.WriteTreeAsync(TestContext.Current.CancellationToken);
            subOid1 = await sub.CommitCreateAsync(new CommitCreateOptions { Tree = t1, Parents = [], Author = TestSig(), Committer = TestSig(), Message = "s1\n", UpdateRef = "refs/heads/master" }, cancellationToken: TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(sub.Workdir!, "f.txt"), "v2\n", TestContext.Current.CancellationToken);
            sidx = await sub.GetIndexAsync(TestContext.Current.CancellationToken);
            await sidx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await sidx.WriteAsync(TestContext.Current.CancellationToken);
            GitOid t2 = await sidx.WriteTreeAsync(TestContext.Current.CancellationToken);
            subOid2 = await sub.CommitCreateAsync(new CommitCreateOptions { Tree = t2, Parents = [subOid1], Author = TestSig(), Committer = TestSig(), Message = "s2\n", UpdateRef = "refs/heads/master" }, cancellationToken: TestContext.Current.CancellationToken);
        }

        string parentDir = NewDir();
        await using GitRepository repo = await InitRepoAsync(parentDir);
        string workdir = repo.Workdir!;

        // Commit A: gitlink @ subOid1.
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("sub", subOid1, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeA = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitOid commitA = await repo.CommitCreateAsync(new CommitCreateOptions { Tree = treeA, Parents = [], Author = TestSig(), Committer = TestSig(), Message = "a\n", UpdateRef = "refs/heads/master" }, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir submodule checked out and DIRTY (tracked file modified).
        Directory.CreateDirectory(Path.Combine(workdir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "sub", ".git"), "gitdir: " + subDir + "/.git\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, "f.txt"), "DIRTY LOCAL EDIT\n", TestContext.Current.CancellationToken);

        // Commit B: gitlink moves to subOid2.
        idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("sub", subOid2, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeB = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        _ = await repo.CommitCreateAsync(new CommitCreateOptions { Tree = treeB, Parents = [commitA], Author = TestSig(), Committer = TestSig(), Message = "b\n", UpdateRef = "refs/heads/master" }, cancellationToken: TestContext.Current.CancellationToken);

        using GitTree treeBObj = (await repo.ObjectLookupAsync<GitTree>(treeB, TestContext.Current.CancellationToken))!;

        // C (checkout.c:523-529): a gitlink workdir skips the workdir-modified test → SAFE checkout proceeds;
        // over the dirty submodule.
        await repo.CheckoutTreeAsync(treeBObj, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(workdir, "sub", ".git")));
    }

    // ── UPDATE_ONLY does nothing for submodules ────────────────

    [Fact]
    public async Task Checkout_UpdateOnly_SubmoduleIndexUnchanged()
    {
        string subDir = NewDir();
        GitOid subOid1, subOid2;
        await using (GitRepository sub = await InitRepoAsync(subDir))
        {
            await File.WriteAllTextAsync(Path.Combine(sub.Workdir!, "f.txt"), "v1\n", TestContext.Current.CancellationToken);
            GitIndex sidx = await sub.GetIndexAsync(TestContext.Current.CancellationToken);
            await sidx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await sidx.WriteAsync(TestContext.Current.CancellationToken);
            GitOid t1 = await sidx.WriteTreeAsync(TestContext.Current.CancellationToken);
            subOid1 = await sub.CommitCreateAsync(new CommitCreateOptions { Tree = t1, Parents = [], Author = TestSig(), Committer = TestSig(), Message = "s1\n", UpdateRef = "refs/heads/master" }, cancellationToken: TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(sub.Workdir!, "f.txt"), "v2\n", TestContext.Current.CancellationToken);
            sidx = await sub.GetIndexAsync(TestContext.Current.CancellationToken);
            await sidx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await sidx.WriteAsync(TestContext.Current.CancellationToken);
            GitOid t2 = await sidx.WriteTreeAsync(TestContext.Current.CancellationToken);
            subOid2 = await sub.CommitCreateAsync(new CommitCreateOptions { Tree = t2, Parents = [subOid1], Author = TestSig(), Committer = TestSig(), Message = "s2\n", UpdateRef = "refs/heads/master" }, cancellationToken: TestContext.Current.CancellationToken);
        }

        string parentDir = NewDir();
        await using GitRepository repo = await InitRepoAsync(parentDir);
        string workdir = repo.Workdir!;

        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("sub", subOid1, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeA = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitOid commitA = await repo.CommitCreateAsync(new CommitCreateOptions { Tree = treeA, Parents = [], Author = TestSig(), Committer = TestSig(), Message = "a\n", UpdateRef = "refs/heads/master" }, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir submodule present.
        Directory.CreateDirectory(Path.Combine(workdir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "sub", ".git"), "gitdir: " + subDir + "/.git\n", TestContext.Current.CancellationToken);

        idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("sub", subOid2, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeB = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        _ = await repo.CommitCreateAsync(new CommitCreateOptions { Tree = treeB, Parents = [commitA], Author = TestSig(), Committer = TestSig(), Message = "b\n", UpdateRef = "refs/heads/master" }, cancellationToken: TestContext.Current.CancellationToken);

        // Reset the index to subOid1 (the state the checkout starts from).
        idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("sub", subOid1, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        using GitTree treeBObj = (await repo.ObjectLookupAsync<GitTree>(treeB, TestContext.Current.CancellationToken))!;

        // C (checkout.c:1683-1685): UPDATE_ONLY means do NOTHING for submodules — the gitlink index entry keeps subOid1.
        await repo.CheckoutTreeAsync(treeBObj, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.UpdateOnly }, TestContext.Current.CancellationToken);

        GitIndex after = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        GitIndexEntry subEntry = after.EntryByPath("sub", stage: 0)!.Value;
        Assert.Equal(subOid1, subEntry.Id);
    }

    // ── DISABLE_PATHSPEC_MATCH makes the pathspec literal ─────

    [Fact]
    public async Task Checkout_DisablePathspecMatch_LiteralMatch()
    {
        // A literal "*.txt" file cannot exist on Windows (wildcards are
        // invalid in filenames), so the scenario is POSIX-only.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);
        string workdir = repo.Workdir!;

        // Target tree with "a.txt", "b.txt" and a literal "*.txt" file.
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("a.txt", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        await bld.InsertAsync("b.txt", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        await bld.InsertAsync("*.txt", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        using GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        // With DISABLE_PATHSPEC_MATCH the glob "*.txt" matches only the literal file.
        var literalPath = LibGit2CS.IO.GitPath.FromUtf8String("*.txt");
        await repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.DisablePathSpecMatch,
            Paths = [literalPath],
        }, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(workdir, "a.txt")));
        Assert.False(File.Exists(Path.Combine(workdir, "b.txt")));
        Assert.True(File.Exists(Path.Combine(workdir, "*.txt")));

        // Without the flag the glob matches all three.
        File.Delete(Path.Combine(workdir, "*.txt"));
        await repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
            Paths = [literalPath],
        }, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(workdir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(workdir, "b.txt")));
        Assert.True(File.Exists(Path.Combine(workdir, "*.txt")));
    }

    // ── DONT_UPDATE_INDEX keeps conflict entries ──────────────

    [Fact]
    public async Task Checkout_DontUpdateIndex_KeepsConflictEntries()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);

        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("a.txt", blob, GitFileMode.Regular).WithStage(1));
        idx.Add(new GitIndexEntry("a.txt", blob, GitFileMode.Regular).WithStage(2));
        idx.Add(new GitIndexEntry("a.txt", blob, GitFileMode.Regular).WithStage(3));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("a.txt", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        using GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        // C (checkout.c:1277-1286): DONT_UPDATE_INDEX suppresses the conflict-entry removal too.
        await repo.CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.DontUpdateIndex,
        }, TestContext.Current.CancellationToken);

        GitIndex after = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.True(after.HasConflicts);
    }

    // ── null/zero merge result → GIT_ECONFLICT ─────────────────

    [Fact]
    public void MergeFileResult_NullPathZeroMode_ForRenameRename()
    {
        // The checkout merge call site throws GIT_ECONFLICT "could not merge contents of file" when the merge result path/mode are null/0 (the shapes C aborts
        // on — checkout.c:2117-2121). These inputs produce exactly that contract: rename/rename with differing targets → BestPath null; a one-sided-deleted
        // merge → BestMode 0.
        var ancestor = LibGit2CS.IO.GitPath.FromUtf8String("A.txt");
        var ours = LibGit2CS.IO.GitPath.FromUtf8String("C.txt");
        var theirs = LibGit2CS.IO.GitPath.FromUtf8String("D.txt");

        Assert.Null(GitMergeFile.BestPath(ancestor, ours, theirs));
        Assert.Equal(0u, GitMergeFile.BestMode(1, 1, 0));
    }
}
