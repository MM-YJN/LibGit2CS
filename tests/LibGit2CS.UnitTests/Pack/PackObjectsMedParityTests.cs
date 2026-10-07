using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Pack;

/// <summary>
/// Regression tests for the pack-objects parity behaviors in
/// libgit2 1.9.4. Expectations are
/// C-verified against libgit2 1.9.4 (pack-objects.c, odb_mempack.c, push.c,
/// transports/local.c).
/// </summary>
public sealed class PackObjectsMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public PackObjectsMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackObjects_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig() => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> CreateRepoAsync(string name, bool bare = true)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, bare, new GitContext());
    }

    /// <summary>Creates a commit with one "README.md" blob; returns (commit, tree, blob).</summary>
    private static async ValueTask<(GitOid Commit, GitOid Tree, GitOid Blob)> CreateCommitAsync(GitRepository repo, string message = "initial\n", string blobContent = "hello\n")
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.ASCII.GetBytes(blobContent), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = null,
        });

        return (commitOid, treeOid, blobOid);
    }

    // ---------------------------------------------------------------
    // insert API semantics — InsertTreeAsync must always walk (C's
    // git_packbuilder_insert_tree walks even when the tree is already
    // inserted), and insert_commit/insert_recur equivalents must exist.
    // ---------------------------------------------------------------

    [Fact]
    public async Task InsertTree_AlreadyInsertedBare_StillWalksSubtree()
    {
        await using GitRepository repo = await CreateRepoAsync("insert-bare");
        (_, GitOid treeOid, _) = await CreateCommitAsync(repo);

        using GitPackWriter pb = repo.NewPackWriter();

        // Insert the tree BARE (as git_packbuilder_insert does for trees in
        // insert_recur and push)…
        await pb.InsertAsync(treeOid, TestContext.Current.CancellationToken);
        Assert.Equal(1, pb.ObjectCount);

        // …then insert_tree: C still walks the whole subtree (the walk runs
        // even when the tree was already inserted — pack-objects.c:1526-1539).
        await pb.InsertTreeAsync(treeOid, TestContext.Current.CancellationToken);
        Assert.Equal(2, pb.ObjectCount); // tree + README blob
    }

    [Fact]
    public async Task InsertCommit_InsertsCommitAndTree()
    {
        await using GitRepository repo = await CreateRepoAsync("insert-commit");
        (GitOid commitOid, _, _) = await CreateCommitAsync(repo);

        using GitPackWriter pb = repo.NewPackWriter();

        // C (git_packbuilder_insert_commit): commit + its tree (not parents).
        await pb.InsertCommitAsync(commitOid, TestContext.Current.CancellationToken);
        Assert.Equal(3, pb.ObjectCount); // commit + tree + blob
    }

    [Fact]
    public async Task InsertRecur_TagChainAndTargetClosure()
    {
        await using GitRepository repo = await CreateRepoAsync("insert-tag");
        (GitOid commitOid, _, _) = await CreateCommitAsync(repo);

        // tag1 -> commit (annotated)
        await repo.TagCreateAsync("t1", (await repo.ObjectLookupAsync(commitOid, TestContext.Current.CancellationToken))!, TestSig(), "t1\n", cancellationToken: TestContext.Current.CancellationToken);
        GitReference? tagRef = await repo.ReferenceLookupAsync("refs/tags/t1", TestContext.Current.CancellationToken);
        GitOid tagOid = ((GitDirectReference)tagRef!).Target;

        using GitPackWriter pb = repo.NewPackWriter();

        // C (git_packbuilder_insert_recur, pack-objects.c:1541-1576): TAG →
        // insert tag, recurse target; COMMIT → commit + tree.
        await pb.InsertRecurAsync(tagOid, null, TestContext.Current.CancellationToken);
        Assert.Equal(4, pb.ObjectCount); // tag + commit + tree + blob
    }

    // ---------------------------------------------------------------
    // mempack dump must insert commit + full tree (C's
    // git_mempack__dump calls git_packbuilder_insert_commit), not just the
    // commit objects.
    // ---------------------------------------------------------------

    [Fact]
    public async Task MempackDump_InsertsCommitAndTree()
    {
        await using GitRepository repo = await CreateRepoAsync("mempack");
        (GitOid commitOid, _, _) = await CreateCommitAsync(repo);

        // Write the commit's objects into a mempack backend.
        await using var mempack = new MemPackBackend();
        using var odbContext = new GitContext();
        await using var odb = new GitObjectDb(odbContext);
        odb.AddBackend(mempack, priority: 1);

        GitObject? commit = await repo.ObjectLookupAsync(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        await odb.WriteAsync(GitObjectType.Commit, commit!.Raw.ToArray(), TestContext.Current.CancellationToken);
        commit.Dispose();

        using GitPackWriter pb = repo.NewPackWriter();

        // C: git_mempack__dump → git_packbuilder_insert_commit per commit —
        // pulls in the commit's tree and every tree/blob beneath it.
        await mempack.DumpAsync(pb, TestContext.Current.CancellationToken);
        Assert.Equal(3, pb.ObjectCount); // commit + tree + blob
    }

    // ---------------------------------------------------------------
    // push tag chains — C peels the FULL tag chain (enqueue_tag,
    // push.c:257-282) and pushes the final peeled commit onto the walk.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Push_TagChain_PackContainsFullClosure()
    {
        await using GitRepository sourceRepo = await CreateRepoAsync("push-tag-src");
        (GitOid commitOid, _, _) = await CreateCommitAsync(sourceRepo);

        // refs/tags/t1 -> tag1 -> tag2 -> commit (annotated tag chain).
        GitObject? commit = await sourceRepo.ObjectLookupAsync(commitOid, TestContext.Current.CancellationToken);
        GitOid tag2 = await sourceRepo.TagCreateAnnotationAsync("inner", commit!, TestSig(), "inner\n", TestContext.Current.CancellationToken);
        GitObject? tag2Obj = await sourceRepo.ObjectLookupAsync(tag2, TestContext.Current.CancellationToken);
        GitOid tag1 = await sourceRepo.TagCreateAnnotationAsync("outer", tag2Obj!, TestSig(), "outer\n", TestContext.Current.CancellationToken);
        GitObject? tag1Obj = await sourceRepo.ObjectLookupAsync(tag1, TestContext.Current.CancellationToken);
        await sourceRepo.TagCreateAsync("t1", tag1Obj!, TestSig(), "t1\n", cancellationToken: TestContext.Current.CancellationToken);
        commit?.Dispose();
        tag2Obj?.Dispose();
        tag1Obj?.Dispose();

        await using GitRepository targetRepo = await CreateRepoAsync("push-tag-tgt");
        await using GitRemote remote = await sourceRepo.RemoteCreateAsync("origin", targetRepo.Path, TestContext.Current.CancellationToken);

        await remote.PushAsync(["refs/tags/t1:refs/tags/t1"], cancellationToken: TestContext.Current.CancellationToken);

        // The pushed pack must contain the tag chain AND the peeled commit's
        // full tree closure (C: enqueue_tag inserts every tag, then the
        // peeled commit is revwalk-pushed).
        Assert.True(await targetRepo.Objects.ExistsAsync(tag1, TestContext.Current.CancellationToken));
        Assert.True(await targetRepo.Objects.ExistsAsync(tag2, TestContext.Current.CancellationToken));
        Assert.True(await targetRepo.Objects.ExistsAsync(commitOid, TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------
    // a late insert after prepare must re-run the delta search (C
    // resets pb->done=false; pack-objects.c:255, 1336-1343).
    // ---------------------------------------------------------------

    [Fact]
    public async Task LateInsert_AfterPrepare_RedoesDeltaSearch()
    {
        await using GitRepository repo = await CreateRepoAsync("late-insert");

        // 500-byte bodies differing in one char: the delta (~24 bytes) is well
        // under max_size = size/2 - 20 = 230, so the delta search accepts it.
        string baseText = new('x', 500);
        byte[] bodyA = System.Text.Encoding.ASCII.GetBytes(baseText + "A");
        byte[] bodyC = System.Text.Encoding.ASCII.GetBytes(baseText + "C");
        GitOid oidA = await repo.ObjectWriteAsync(GitObjectType.Blob, bodyA, TestContext.Current.CancellationToken);
        GitOid oidC = await repo.ObjectWriteAsync(GitObjectType.Blob, bodyC, TestContext.Current.CancellationToken);
        // A second, distinct blob so the delta list has > 1 entry.
        GitOid oidB = await repo.ObjectWriteAsync(GitObjectType.Blob, "something entirely different here"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitPackWriter pb = repo.NewPackWriter();

        await pb.InsertAsync(oidA, TestContext.Current.CancellationToken);
        await pb.InsertAsync(oidB, TestContext.Current.CancellationToken);
        await pb.PrepareAsync(TestContext.Current.CancellationToken);

        // Late insert after prepare.
        await pb.InsertAsync(oidC, TestContext.Current.CancellationToken);
        await pb.PrepareAsync(TestContext.Current.CancellationToken);

        string packDir = Path.Combine(_tempDir, "late-insert-pack");
        Directory.CreateDirectory(packDir);
        string packPath = await pb.WriteToDirectoryAsync(packDir, progress: null, TestContext.Current.CancellationToken);

        // The re-prepared delta search processes the delta list in
        // type_size_sort order: with no name hashes (po->hash == 0) and equal
        // sizes, the tiebreak is C's pointer order = INSERTION order
        // (pack-objects.c type_size_sort: ascending git_pobject addresses,
        // contiguous in insertion order). A (inserted first) is processed
        // first with an empty window and stays whole; the late object C is
        // deltafied against A. Skipping the re-prepare would deltafy NO
        // object.
        using GitPackIndex idx = await GitPackIndex.OpenAsync(Path.ChangeExtension(packPath, ".idx"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitPackIndexLookupResult result = idx.FindIndex(oidC);
        Assert.True(result.Found);
        long offset = idx.GetObjectOffset(result.Index);

        byte[] entryBytes = ReadPackBytes(packPath, offset, 1);
        int type = (entryBytes[0] >> 4) & 7;
        Assert.True(type is 6 or 7, $"re-prepare must deltafy the late same-size object, entry type was {type}");
    }

    private static byte[] ReadPackBytes(string packPath, long offset, int count)
    {
        using var fs = new FileStream(packPath, FileMode.Open, FileAccess.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        byte[] buf = new byte[count];
        int read = fs.Read(buf, 0, count);
        return buf[..read];
    }

    // ---------------------------------------------------------------
    // pack.* config — pack.deltaCacheSize also sets the big-file
    // threshold (C quirk, pack-objects.c:114-120).
    // ---------------------------------------------------------------

    [Fact]
    public async Task PackConfig_DeltaCacheSize_SetsBigFileThreshold()
    {
        await using GitRepository repo = await CreateRepoAsync("delta-cache");

        // pack.deltaCacheSize < 512MB → big_file_threshold drops below the
        // blob sizes, so the two similar blobs are NOT delta-eligible.
        await repo.Config.SetStringAsync("pack.deltaCacheSize", "1000", TestContext.Current.CancellationToken);

        byte[] bodyA = System.Text.Encoding.ASCII.GetBytes(new string('a', 2000));
        byte[] bodyB = System.Text.Encoding.ASCII.GetBytes(new string('a', 1999) + "b");
        GitOid oidA = await repo.ObjectWriteAsync(GitObjectType.Blob, bodyA, TestContext.Current.CancellationToken);
        GitOid oidB = await repo.ObjectWriteAsync(GitObjectType.Blob, bodyB, TestContext.Current.CancellationToken);

        using GitPackWriter pb = repo.NewPackWriter();
        await pb.InsertAsync(oidA, TestContext.Current.CancellationToken);
        await pb.InsertAsync(oidB, TestContext.Current.CancellationToken);
        await pb.PrepareAsync(TestContext.Current.CancellationToken);

        string packDir = Path.Combine(_tempDir, "delta-cache-pack");
        Directory.CreateDirectory(packDir);
        string packPath = await pb.WriteToDirectoryAsync(packDir, progress: null, TestContext.Current.CancellationToken);

        using GitPackIndex idx = await GitPackIndex.OpenAsync(Path.ChangeExtension(packPath, ".idx"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitPackIndexLookupResult result = idx.FindIndex(oidB);
        Assert.True(result.Found);
        long offset = idx.GetObjectOffset(result.Index);

        byte[] entryBytes = ReadPackBytes(packPath, offset, 1);
        int type = (entryBytes[0] >> 4) & 7;
        Assert.Equal(3, type); // full blob — no delta (over big_file_threshold)
    }

    // ---------------------------------------------------------------
    // progress reporting — stage 0 (ADDING_OBJECTS) on insert and
    // stage 1 (DELTAFICATION) on prepare.
    // ---------------------------------------------------------------

    [Fact]
    public async Task InsertAndPrepare_ReportProgress()
    {
        await using GitRepository repo = await CreateRepoAsync("progress");
        (GitOid commitOid, _, _) = await CreateCommitAsync(repo);

        var progress = new SyncProgress<GitPackProgress>();

        using GitPackWriter pb = repo.NewPackWriter();

        await pb.InsertAsync(commitOid, progress, TestContext.Current.CancellationToken);
        await pb.PrepareAsync(progress, TestContext.Current.CancellationToken);

        // C (pack-objects.c:257-271): ADDING_OBJECTS with total 0 on insert.
        Assert.Contains(progress.Reports, r => r.Stage == 0 && r.Current >= 1 && r.Total == 0);

        // C (pack-objects.c:1349-1352, 1377): DELTAFICATION with (0, N) at
        // start and (N, N) at the end.
        Assert.Contains(progress.Reports, r => r.Stage == 1 && r.Current == 0 && r.Total == pb.ObjectCount);
        Assert.Contains(progress.Reports, r => r.Stage == 1 && r.Current == pb.ObjectCount && r.Total == pb.ObjectCount);
    }

    /// <summary>Synchronous progress capture (Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }

    // ---------------------------------------------------------------
    // local fetch of a tag — C's insert_recur inserts the peeled
    // target's FULL TREE (transports/local.c:614-629), not just the tag
    // chain.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Fetch_TagToCommit_GetsTreeClosure()
    {
        await using GitRepository sourceRepo = await CreateRepoAsync("fetch-tag-src");

        // An orphan commit: its tree/blob are NOT reachable from any branch.
        (GitOid orphanCommit, GitOid treeOid, GitOid blobOid) = await CreateCommitAsync(sourceRepo, "orphan\n", "orphan content\n");
        GitObject? commit = await sourceRepo.ObjectLookupAsync(orphanCommit, TestContext.Current.CancellationToken);
        await sourceRepo.TagCreateAsync("t", commit!, TestSig(), "t\n", cancellationToken: TestContext.Current.CancellationToken);
        commit?.Dispose();

        await using GitRepository clientRepo = await CreateRepoAsync("fetch-tag-client", bare: false);
        await using GitRemote remote = await clientRepo.RemoteCreateAsync("origin", sourceRepo.Path, TestContext.Current.CancellationToken);

        await remote.FetchAsync(["+refs/heads/*:refs/remotes/origin/*", "+refs/tags/*:refs/tags/*"], null, cancellationToken: TestContext.Current.CancellationToken);

        // The fetched pack must contain the tag's peeled commit + tree + blob.
        Assert.True(await clientRepo.Objects.ExistsAsync(orphanCommit, TestContext.Current.CancellationToken));
        Assert.True(await clientRepo.Objects.ExistsAsync(treeOid, TestContext.Current.CancellationToken));
        Assert.True(await clientRepo.Objects.ExistsAsync(blobOid, TestContext.Current.CancellationToken));
    }
}
