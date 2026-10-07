using System.Buffers.Binary;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Pack;

/// <summary>
/// Tests for <see cref="GitPackWriter"/> — object enumeration, pack assembly,
/// and hash verification. Uses in-memory repos with known objects.
/// </summary>
public sealed class PackWriterTests : IDisposable
{
    private readonly string _tempDir;

    public PackWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackWriter_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig()
        => new("Test", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> CreateRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
    }

    private static async Task<GitOid> CreateCommitAsync(GitRepository repo, string message = "initial\n")
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = "refs/heads/master",
        });
    }

    /// <summary>
    /// Inserts a commit and all reachable objects (tree, blobs) into the pack writer.
    /// </summary>
    private static async Task InsertCommitWithTree(GitPackWriter pb, GitRepository repo, GitOid commitOid)
    {
        using GitRevWalker walk = repo.NewRevWalker();
        walk.Sort = GitSortMode.Time;
        await walk.PushAsync(commitOid, TestContext.Current.CancellationToken);
        await pb.InsertWalkAsync(walk, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RetainedObjectBuffer_SurvivesObjectDisposalAndBackendReset()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await CreateRepoAsync("retained");
        var backend = new MemPackBackend();
        repo.Objects.AddBackend(backend, priority: 10);
        byte[] source = new byte[65536];
        new Random(42).NextBytes(source);
        GitOid id = await repo.ObjectWriteAsync(GitObjectType.Blob, source, ct);
        GitObject obj = (await repo.ObjectLookupAsync(id, ct))!;
        ReadOnlyMemory<byte> retained = obj.Raw;
        obj.Dispose();
        backend.Reset();

        DeltaEncoder.DeltaIndex index = Assert.IsType<DeltaEncoder.DeltaIndex>(DeltaEncoder.BuildIndexFromRetainedBuffer(retained));
        byte[] target = (byte[])source.Clone();
        target[^1] ^= 0x7f;
        byte[] delta = Assert.IsType<byte[]>(DeltaEncoder.CreateFromIndex(index, target, 0));
        Assert.Equal(source, retained.ToArray());
        Assert.Equal(target, DeltaEncoder.Apply(retained.Span, delta));
    }

    [Theory]
    [InlineData("loose", 0)]
    [InlineData("loose", 4096)]
    [InlineData("packed", 0)]
    [InlineData("packed", 4096)]
    [InlineData("memory", 0)]
    [InlineData("memory", 4096)]
    public async Task Write_SimilarBlobsAcrossWindowRotations_PreservesBodies(string backend, int windowMemory)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateRepoAsync("source");
        await using GitRepository packed = await CreateRepoAsync("packed");
        if (backend == "memory")
        {
            source.Objects.AddBackend(new MemPackBackend(), priority: 10);
        }

        var expected = new Dictionary<GitOid, byte[]>();
        byte[] payload = new byte[128 * 1024];
        new Random(42).NextBytes(payload);
        for (int i = 0; i < 24; i++)
        {
            payload[payload.Length / 2] = (byte)i;
            GitOid id = await source.ObjectWriteAsync(GitObjectType.Blob, payload, ct);
            expected.Add(id, (byte[])payload.Clone());
        }

        GitRepository input = source;
        if (backend == "packed")
        {
            using GitPackWriter seed = source.NewPackWriter();
            foreach (GitOid id in expected.Keys)
            {
                await seed.InsertAsync(id, ct);
            }

            string inputPackDirectory = Path.Combine(packed.Path, "objects", "pack");
            Directory.CreateDirectory(inputPackDirectory);
            await seed.WriteToDirectoryAsync(inputPackDirectory, null, ct);
            input = packed; // This repository has no loose copies of the objects.
        }

        await input.Config.SetIntAsync("pack.window", 3, ct);
        await input.Config.SetIntAsync("pack.windowMemory", windowMemory, ct);
        using GitPackWriter writer = input.NewPackWriter();
        foreach (GitOid id in expected.Keys)
        {
            await writer.InsertAsync(id, ct);
        }

        string outputDirectory = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = await writer.WriteToDirectoryAsync(outputDirectory, null, ct);
        using PackFile output = await PackFile.OpenAsync(outputPath, input.ObjectFormat, ct);
        Assert.Equal(expected.Count, output.ObjectCount);
        foreach ((GitOid id, byte[] body) in expected)
        {
            RawObjectData actual = (await output.ReadAsync(id, ct))!.Value;
            Assert.Equal(body, actual.Data);
            using GitObject? original = await input.ObjectLookupAsync(id, ct);
            Assert.NotNull(original);
            Assert.Equal(body, original.Raw.ToArray());
        }
    }

    [Fact]
    public async Task Insert_SingleObject_ObjectCountIsOne()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);

        using GitPackWriter pb = repo.NewPackWriter();
        await pb.InsertAsync(commitOid, TestContext.Current.CancellationToken);

        Assert.Equal(1, pb.ObjectCount);
    }

    [Fact]
    public async Task Insert_DuplicateOid_IsIdempotent()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);

        using GitPackWriter pb = repo.NewPackWriter();
        await pb.InsertAsync(commitOid, TestContext.Current.CancellationToken);
        await pb.InsertAsync(commitOid, TestContext.Current.CancellationToken);

        Assert.Equal(1, pb.ObjectCount);
    }

    [Fact]
    public async Task InsertTree_IncludesAllTreeObjects()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;

        using GitPackWriter pb = repo.NewPackWriter();
        await pb.InsertTreeAsync(commit.Tree, TestContext.Current.CancellationToken);
        commit.Dispose();

        // Should include the tree and the blob (not the commit)
        Assert.Equal(2, pb.ObjectCount);
    }

    [Fact]
    public async Task Write_ProducesValidPackFile()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);

        using GitPackWriter pb = repo.NewPackWriter();
        await pb.InsertAsync(commitOid, TestContext.Current.CancellationToken);

        using var ms = new MemoryStream();
        await pb.WriteAsync(ms, null, TestContext.Current.CancellationToken);

        byte[] packData = ms.ToArray();

        // Verify pack header: "PACK" + version 2 + object count
        Assert.Equal((byte)'P', packData[0]);
        Assert.Equal((byte)'A', packData[1]);
        Assert.Equal((byte)'C', packData[2]);
        Assert.Equal((byte)'K', packData[3]);

        int version = (int)BinaryPrimitives.ReadUInt32BigEndian(packData.AsSpan(4, 4));
        Assert.Equal(2, version);

        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(packData.AsSpan(8, 4));
        Assert.Equal(pb.ObjectCount, count);

        // Verify trailer (20 bytes SHA-1 at the end)
        Assert.True(packData.Length >= 12 + 20);
    }

    [Fact]
    public async Task Write_IncludesCommitAndTreeAndBlob()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);

        using GitPackWriter pb = repo.NewPackWriter();
        await InsertCommitWithTree(pb, repo, commitOid);

        using var ms = new MemoryStream();
        await pb.WriteAsync(ms, null, TestContext.Current.CancellationToken);

        // The pack should contain 3 objects: commit + tree + blob
        byte[] packData = ms.ToArray();
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(packData.AsSpan(8, 4));
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Prepare_DoesNotThrow()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);

        using GitPackWriter pb = repo.NewPackWriter();
        await pb.InsertAsync(commitOid, TestContext.Current.CancellationToken);
        await pb.PrepareAsync(TestContext.Current.CancellationToken);

        // Prepare should complete without throwing
        Assert.True(pb.ObjectCount > 0);
    }

    [Fact]
    public async Task WriteToDirectory_CreatesPackAndIdxFiles()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);

        string packDir = Path.Combine(_tempDir, "packs");
        Directory.CreateDirectory(packDir);

        using GitPackWriter pb = repo.NewPackWriter();
        await InsertCommitWithTree(pb, repo, commitOid);
        string packPath = await pb.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);

        // Verify .pack file exists
        Assert.True(File.Exists(packPath));
        Assert.EndsWith(".pack", packPath);

        // Verify .idx file exists
        string idxPath = Path.ChangeExtension(packPath, ".idx");
        Assert.True(File.Exists(idxPath));

        // Verify the .idx starts with the v2 magic
        byte[] idxBytes = await File.ReadAllBytesAsync(idxPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0xff, idxBytes[0]);
        Assert.Equal((byte)'t', idxBytes[1]);
        Assert.Equal((byte)'O', idxBytes[2]);
        Assert.Equal((byte)'c', idxBytes[3]);
    }

    [Fact]
    public async Task WriteToDirectory_PackIsReadableByPackFile()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);

        string packDir = Path.Combine(_tempDir, "packs");
        Directory.CreateDirectory(packDir);

        using GitPackWriter pb = repo.NewPackWriter();
        await InsertCommitWithTree(pb, repo, commitOid);
        string packPath = await pb.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);

        // Read back with PackFile
        using PackFile pack = await PackFile.OpenAsync(packPath, repo.ObjectFormat, TestContext.Current.CancellationToken);

        // Verify all objects are readable
        Assert.Equal(pb.ObjectCount, pack.ObjectCount);
    }

    [Fact]
    public async Task WriteToDirectory_IdxIsReadableByPackIndex()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);

        string packDir = Path.Combine(_tempDir, "packs");
        Directory.CreateDirectory(packDir);

        using GitPackWriter pb = repo.NewPackWriter();
        await InsertCommitWithTree(pb, repo, commitOid);
        string packPath = await pb.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        // Read back with PackIndex
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, repo.ObjectFormat, TestContext.Current.CancellationToken);

        Assert.Equal(pb.ObjectCount, idx.ObjectCount);
        Assert.Equal(2, idx.Version);
    }

    [Fact]
    public async Task Write_ObjectsAreReadableAfterRoundtrip()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitOid treeOid = commit.Tree;

        string packDir = Path.Combine(_tempDir, "packs");
        Directory.CreateDirectory(packDir);

        using GitPackWriter pb = repo.NewPackWriter();
        await InsertCommitWithTree(pb, repo, commitOid);
        string packPath = await pb.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);

        // Read back with PackFile
        using PackFile pack = await PackFile.OpenAsync(packPath, repo.ObjectFormat, TestContext.Current.CancellationToken);

        // Verify the commit is readable and has the right OID
        Assert.True(pack.Exists(commitOid));
        Assert.True(pack.Exists(treeOid));

        commit.Dispose();
    }

    [Fact]
    public async Task Write_MultipleCommits_AllIncludedInPack()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commit1 = await CreateCommitAsync(repo, "first\n");

        // Create a second commit with a different tree
        GitOid blobOid2 = await repo.ObjectWriteAsync(GitObjectType.Blob, "world\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("file2.txt", blobOid2, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid2 = await treeBld.WriteAsync(CancellationToken.None);

        GitOid commit2 = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid2,
            Parents = [commit1],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "second\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        using GitPackWriter pb = repo.NewPackWriter();
        using GitRevWalker walk = repo.NewRevWalker();
        walk.Sort = GitSortMode.Time;
        await walk.PushAsync(commit2, TestContext.Current.CancellationToken);
        await pb.InsertWalkAsync(walk, TestContext.Current.CancellationToken);

        string packDir = Path.Combine(_tempDir, "packs");
        Directory.CreateDirectory(packDir);
        string packPath = await pb.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);

        using PackFile pack = await PackFile.OpenAsync(packPath, repo.ObjectFormat, TestContext.Current.CancellationToken);

        // Should have: 2 commits + 2 trees + 2 blobs = 6 objects
        Assert.Equal(6, pack.ObjectCount);
        Assert.True(pack.Exists(commit1));
        Assert.True(pack.Exists(commit2));
    }

    [Fact]
    public async Task Write_EmptyPack_ProducesValidFile()
    {
        await using GitRepository repo = await CreateRepoAsync("source");

        using GitPackWriter pb = repo.NewPackWriter();

        using var ms = new MemoryStream();
        await pb.WriteAsync(ms, null, TestContext.Current.CancellationToken);

        byte[] packData = ms.ToArray();
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(packData.AsSpan(8, 4));
        Assert.Equal(0, count);

        // Pack header (12) + trailer (20) = 32 bytes minimum
        Assert.Equal(32, packData.Length);
    }

    [Fact]
    public async Task Dispose_Twice_DoesNotThrow()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitPackWriter pb = repo.NewPackWriter();
        pb.Dispose();
        pb.Dispose();
    }

    // ─── Delta/base desync regression tests ────────────────────────────
    //
    // GitPackWriter.FindDeltasAsync selects the best delta base per target
    // under a strict gate (deltaSize < bestDeltaSize && < target.Size/2).
    // Each successful probe must write the base OID and the encoded delta
    // bytes together, so the selection and the bytes can never disagree:
    // otherwise the pack emits a REF_DELTA whose base OID and encoded bytes
    // disagree, and the receiver's GitDeltaApplier.Apply throws "base size
    // does not match given data" during GitPackIndexer.CommitAsync.
    //
    // The carry-forward history below (each commit's tree is a superset of
    // the previous one) deterministically exercises this: trees
    // T1 < T2 < T3 by size, and the smallest (T1) has two viable bases (T2,
    // T3) whose deltas are exactly equal in size.

    /// <summary>
    /// Creates a commit whose tree is the cumulative carry-forward of all
    /// <paramref name="files"/> (matching the spike's BuildCommitsAsync shape).
    /// Each call with a grown dictionary produces a tree that is a superset
    /// of the previous commit's tree, exercising the delta window.
    /// </summary>
    private static async Task<GitOid> CreateCarryForwardCommitAsync(
        GitRepository repo,
        GitOid? parent,
        IReadOnlyDictionary<string, byte[]> files,
        string message,
        CancellationToken ct)
    {
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        foreach (KeyValuePair<string, byte[]> entry in files)
        {
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, entry.Value, ct);
            await treeBld.InsertAsync(entry.Key, blobOid, GitFileMode.Regular, ct);
        }

        GitOid treeOid = await treeBld.WriteAsync(ct);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is null ? [] : [parent.Value],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = "refs/heads/master",
        }, ct);
    }

    /// <summary>
    /// Builds <paramref name="commitCount"/> linear carry-forward commits
    /// (commit 1 = hello.txt; commit i &gt; 1 adds file{i}.txt while keeping
    /// all prior files). Returns the commit OIDs in creation order and the
    /// final blob contents for round-trip verification.
    /// </summary>
    private static async Task<(List<GitOid> Commits, Dictionary<string, byte[]> Files)> BuildCarryForwardHistoryAsync(
        GitRepository repo,
        int commitCount,
        CancellationToken ct)
    {
        var commits = new List<GitOid>(commitCount);
        var files = new Dictionary<string, byte[]>();

        for (int i = 1; i <= commitCount; i++)
        {
            string name = i == 1 ? "hello.txt" : $"file{i}.txt";
            byte[] content = Encoding.UTF8.GetBytes(i == 1 ? "hello\n" : $"file{i}\n");
            files[name] = content;

            GitOid? parent = commits.Count > 0 ? commits[^1] : null;
            GitOid commit = await CreateCarryForwardCommitAsync(repo, parent, files, $"commit {i}\n", ct);
            commits.Add(commit);
        }

        return (commits, files);
    }

    /// <summary>
    /// Regression for the delta/base desync: a 3-commit carry-forward push
    /// opens the desync window (two equal-size delta candidates for the
    /// smallest tree). <see cref="GitPackWriter.WriteToDirectoryAsync"/> must
    /// write cleanly with all 9 objects (3C/3T/3B) round-tripping with
    /// byte-exact blob content, not throw "base size does not match given
    /// data" from <see cref="GitDeltaApplier.Apply"/> during
    /// <see cref="GitPackIndexer.CommitAsync"/>.
    /// </summary>
    [Fact]
    public async Task WriteToDirectory_ThreeCommitsWithCarryForward_DoesNotThrow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await CreateRepoAsync("source");
        (List<GitOid> commits, Dictionary<string, byte[]> files) = await BuildCarryForwardHistoryAsync(repo, commitCount: 3, ct);

        string packDir = Path.Combine(_tempDir, "packs-3commit");
        Directory.CreateDirectory(packDir);

        using GitPackWriter pb = repo.NewPackWriter();
        using GitRevWalker walk = repo.NewRevWalker();
        walk.Sort = GitSortMode.Time;
        await walk.PushAsync(commits[^1], ct);
        await pb.InsertWalkAsync(walk, ct);

        string packPath = await pb.WriteToDirectoryAsync(packDir, null, ct);

        // 3 commits + 3 trees + 3 blobs = 9 objects.
        Assert.Equal(9, pb.ObjectCount);

        using PackFile pack = await PackFile.OpenAsync(packPath, repo.ObjectFormat, ct);
        Assert.Equal(9, pack.ObjectCount);

        foreach (GitOid commit in commits)
        {
            Assert.True(pack.Exists(commit), $"commit {commit} missing from pack");
        }

        // Verify byte-exact blob content survives the delta → apply round-trip.
        foreach (KeyValuePair<string, byte[]> entry in files)
        {
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, entry.Value, ct);
            Assert.True(pack.Exists(blobOid), $"blob {entry.Key} ({blobOid}) missing from pack");
            RawObjectData read = (await pack.ReadAsync(blobOid, ct))!.Value;
            Assert.Equal(entry.Value, read.Data);
        }
    }

    /// <summary>
    /// Wider window variant: 5 carry-forward commits produce 5 trees in the
    /// delta window with deeper candidate overlap, exercising the atomic
    /// base+bytes selection under more probes per target.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    public async Task WriteToDirectory_FiveCommitsWithCarryForward_AllObjectsRoundTrip(int windowMemory)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await CreateRepoAsync("source");
        await repo.Config.SetIntAsync("pack.windowMemory", windowMemory, ct);
        (List<GitOid> commits, Dictionary<string, byte[]> files) = await BuildCarryForwardHistoryAsync(repo, commitCount: 5, ct);

        string packDir = Path.Combine(_tempDir, "packs-5commit");
        Directory.CreateDirectory(packDir);

        using GitPackWriter pb = repo.NewPackWriter();
        using GitRevWalker walk = repo.NewRevWalker();
        walk.Sort = GitSortMode.Time;
        await walk.PushAsync(commits[^1], ct);
        await pb.InsertWalkAsync(walk, ct);

        string packPath = await pb.WriteToDirectoryAsync(packDir, null, ct);

        // 5 commits + 5 trees + 5 blobs = 15 objects.
        Assert.Equal(15, pb.ObjectCount);

        using PackFile pack = await PackFile.OpenAsync(packPath, repo.ObjectFormat, ct);
        Assert.Equal(15, pack.ObjectCount);

        foreach (GitOid commit in commits)
        {
            using Commit? expectedCommit = await repo.ObjectLookupAsync<Commit>(commit, ct);
            Assert.NotNull(expectedCommit);
            RawObjectData readCommit = (await pack.ReadAsync(commit, ct))!.Value;
            Assert.Equal(expectedCommit.Raw.ToArray(), readCommit.Data);

            using GitTree? expectedTree = await repo.ObjectLookupAsync<GitTree>(expectedCommit.Tree, ct);
            Assert.NotNull(expectedTree);
            RawObjectData readTree = (await pack.ReadAsync(expectedCommit.Tree, ct))!.Value;
            Assert.Equal(expectedTree.Raw.ToArray(), readTree.Data);
        }

        foreach (KeyValuePair<string, byte[]> entry in files)
        {
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, entry.Value, ct);
            RawObjectData read = (await pack.ReadAsync(blobOid, ct))!.Value;
            Assert.Equal(entry.Value, read.Data);
        }
    }

    /// <summary>
    /// The <see cref="GitPackWriter.WriteAsync"/> stream path (used by
    /// HTTP/SSH transports) serializes objects via the same
    /// <see cref="GitPackWriter.WriteObjectToBytesAsync"/> as
    /// <see cref="GitPackWriter.WriteToDirectoryAsync"/> but does not itself
    /// run the indexer. Feed the streamed bytes through a fresh
    /// <see cref="GitPackIndexer"/> to verify the receiver-side delta
    /// resolution succeeds — the exact path a remote LibGit2CS push target
    /// exercises.
    /// </summary>
    [Fact]
    public async Task Write_ThreeCommitsWithCarryForward_StreamFeedsIndexerCleanly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await CreateRepoAsync("source");
        (List<GitOid> commits, _) = await BuildCarryForwardHistoryAsync(repo, commitCount: 3, ct);

        using GitPackWriter pb = repo.NewPackWriter();
        using GitRevWalker walk = repo.NewRevWalker();
        walk.Sort = GitSortMode.Time;
        await walk.PushAsync(commits[^1], ct);
        await pb.InsertWalkAsync(walk, ct);

        using var ms = new MemoryStream();
        await pb.WriteAsync(ms, null, ct);
        byte[] packBytes = ms.ToArray();

        // Feed the streamed pack to a fresh indexer — this resolves deltas
        // and throws "base size does not match given data" if any REF_DELTA
        // has a mismatched base/bytes pair.
        string indexerDir = Path.Combine(_tempDir, "stream-indexer");
        Directory.CreateDirectory(indexerDir);
        await using var indexer = new GitPackIndexer(indexerDir, repo.ObjectFormat, repo.Objects);
        var stats = new GitIndexerProgress();
        await indexer.AppendAsync(packBytes, stats, ct);
        await indexer.CommitAsync(stats, ct);
    }
}
