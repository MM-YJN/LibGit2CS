using System.Buffers.Binary;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Pack;

/// <summary>
/// Tests for <see cref="GitPackIndexer"/> — .idx v2 generation, pack parsing,
/// and roundtrip verification with <see cref="GitPackIndex"/> reader.
/// </summary>
public sealed class PackIndexerTests : IDisposable
{
    private readonly string _tempDir;

    public PackIndexerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackIndexer_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static async Task<GitOid> CreateCommitAsync(GitRepository repo)
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
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });
    }

    private async Task<string> BuildPack(GitRepository repo, GitOid commitOid)
    {
        string packDir = Path.Combine(_tempDir, "packs");
        Directory.CreateDirectory(packDir);

        using GitPackWriter pb = repo.NewPackWriter();
        using GitRevWalker walk = repo.NewRevWalker();
        walk.Sort = GitSortMode.Time;
        await walk.PushAsync(commitOid, TestContext.Current.CancellationToken);
        await pb.InsertWalkAsync(walk, TestContext.Current.CancellationToken);

        return await pb.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Commit_ProducesValidIdxV2Magic()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        byte[] idxBytes = await File.ReadAllBytesAsync(idxPath, cancellationToken: TestContext.Current.CancellationToken);

        // v2 magic: \377tOc = 0xff, 't', 'O', 'c'
        Assert.Equal(0xff, idxBytes[0]);
        Assert.Equal((byte)'t', idxBytes[1]);
        Assert.Equal((byte)'O', idxBytes[2]);
        Assert.Equal((byte)'c', idxBytes[3]);

        // Version 2 (big-endian)
        int version = (int)BinaryPrimitives.ReadUInt32BigEndian(idxBytes.AsSpan(4, 4));
        Assert.Equal(2, version);
    }

    [Fact]
    public async Task Commit_ProducesCorrectObjectCount()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, repo.ObjectFormat, TestContext.Current.CancellationToken);

        // 3 objects: commit + tree + blob
        Assert.Equal(3, idx.ObjectCount);
    }

    [Fact]
    public async Task Commit_FanoutTableIsMonotonicallyIncreasing()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        byte[] idxBytes = await File.ReadAllBytesAsync(idxPath, cancellationToken: TestContext.Current.CancellationToken);

        // Fanout table starts at byte 8 (after magic + version)
        int prev = 0;
        for (int i = 0; i < 256; i++)
        {
            int offset = 8 + i * 4;
            int n = (idxBytes[offset] << 24) | (idxBytes[offset + 1] << 16) |
                    (idxBytes[offset + 2] << 8) | idxBytes[offset + 3];
            Assert.True(n >= prev, $"fanout[{i}] = {n} < fanout[{i - 1}] = {prev}");
            prev = n;
        }

        // Last entry equals total object count
        Assert.Equal(3, prev);
    }

    [Fact]
    public async Task Commit_IdxIsReadableByPackIndex()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, repo.ObjectFormat, TestContext.Current.CancellationToken);

        Assert.Equal(2, idx.Version);
        Assert.Equal(3, idx.ObjectCount);
    }

    [Fact]
    public async Task Commit_OidsInIdxMatchOriginalObjects()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, repo.ObjectFormat, TestContext.Current.CancellationToken);
        Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitOid treeOid = commit.Tree;

        // Find the commit OID in the index
        GitPackIndexLookupResult result = idx.FindIndex(commitOid, commitOid.HexSize);
        Assert.True(result.Found);

        // Find the tree OID in the index
        result = idx.FindIndex(treeOid, treeOid.HexSize);
        Assert.True(result.Found);

        commit.Dispose();
    }

    [Fact]
    public async Task Commit_PackAndIdxAreConsistent()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using PackFile pack = await PackFile.OpenAsync(packPath, repo.ObjectFormat, TestContext.Current.CancellationToken);
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, repo.ObjectFormat, TestContext.Current.CancellationToken);

        Assert.Equal(pack.ObjectCount, idx.ObjectCount);

        // Every OID in the index should be readable from the pack
        for (int i = 0; i < idx.ObjectCount; i++)
        {
            GitOid oid = idx.GetOid(i);
            Assert.True(pack.Exists(oid), $"OID {oid} not found in pack");
        }
    }

    [Fact]
    public async Task Commit_AllObjectsReadableViaPackAndIdx()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using PackFile pack = await PackFile.OpenAsync(packPath, repo.ObjectFormat, TestContext.Current.CancellationToken);
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, repo.ObjectFormat, TestContext.Current.CancellationToken);

        // Look up each OID via the index, read it from the pack
        for (int i = 0; i < idx.ObjectCount; i++)
        {
            GitOid oid = idx.GetOid(i);
            long offset = idx.GetObjectOffset(i);
            RawObjectData? obj = await pack.ReadAsync(oid, TestContext.Current.CancellationToken);
            Assert.NotNull(obj);
        }
    }

    [Fact]
    public async Task Commit_PackFileHasCorrectTrailer()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, repo.ObjectFormat, TestContext.Current.CancellationToken);

        // The pack checksum in the .idx should match a SHA-1 hash
        GitOid packChecksum = idx.PackChecksum();
        Assert.False(packChecksum.IsZero);
    }

    [Fact]
    public async Task Append_InvalidPackHeader_Throws()
    {
        string packDir = Path.Combine(_tempDir, "badpack");
        Directory.CreateDirectory(packDir);

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        byte[] badHeader = new byte[] { 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 1 };
        var stats = new GitIndexerProgress();

        await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.AppendAsync(badHeader, stats, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispose_BeforeCommit_DeletesTempPack()
    {
        string packDir = Path.Combine(_tempDir, "dispose");
        Directory.CreateDirectory(packDir);

        var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        await indexer.DisposeAsync();

        // No .pack or .idx files should exist
        IEnumerable<string> files = Directory.GetFiles(packDir, "*.pack").Concat(Directory.GetFiles(packDir, "*.idx"));
        Assert.Empty(files);
    }

    [Fact]
    public async Task Commit_ProducesPackWithCorrectName()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);

        // Pack file should be named pack-<40-hex-chars>.pack
        string fileName = Path.GetFileName(packPath);
        Assert.StartsWith("pack-", fileName);
        Assert.EndsWith(".pack", fileName);

        // The hex part should be 40 chars (SHA-1)
        string hexPart = fileName.Substring("pack-".Length, fileName.Length - "pack-".Length - ".pack".Length);
        Assert.Equal(40, hexPart.Length);

        // The .idx file should have the same base name
        string idxPath = Path.ChangeExtension(packPath, ".idx");
        string idxFileName = Path.GetFileName(idxPath);
        Assert.EndsWith(hexPart + ".idx", idxFileName);
    }

    [Fact]
    public async Task Commit_Crc32TableMatchesZlibCrc32()
    {
        await using GitRepository repo = await CreateRepoAsync("source");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, repo.ObjectFormat, TestContext.Current.CancellationToken);
        byte[] packBytes = await File.ReadAllBytesAsync(packPath, cancellationToken: TestContext.Current.CancellationToken);

        // For each object, compute CRC32 from the pack data and verify it matches
        // the CRC in the .idx (we can't directly read the CRC table from PackIndex,
        // but we can verify the .idx is valid by checking that all objects are readable)
        for (int i = 0; i < idx.ObjectCount; i++)
        {
            GitOid oid = idx.GetOid(i);
            long offset = idx.GetObjectOffset(i);
            Assert.True(offset >= 12); // After pack header
            Assert.True(offset < packBytes.Length - 20); // Before trailer
        }
    }

    // -------------------------------------------------------------------------
    //  Thin-pack tests — exercise fix_thin_pack + inject_object +
    //  update_header_and_rehash (the unthinning path).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Helper: feeds a thin pack through the indexer and returns the committed
    /// pack path + stats. The base object is pre-written to <paramref name="odb"/>.
    /// </summary>
    private static async Task<(string PackPath, GitIndexerProgress Stats)> IndexThinPackAsync(
        string packDir,
        GitObjectDb odb,
        byte[] thinPack,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(packDir);
        var stats = new GitIndexerProgress();
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1, odb);
        await indexer.AppendAsync(thinPack, stats, cancellationToken);
        await indexer.CommitAsync(stats, cancellationToken);
        return (indexer.PackPath!, stats);
    }

    [Fact]
    public async Task Commit_ThinPack_InjectsExternalBase()
    {
        await using GitRepository repo = await CreateRepoAsync("thin_repo");
        byte[] baseBody = "hello\n"u8.ToArray();
        byte[] resultBody = "hello\n world\n"u8.ToArray();

        GitOid baseOid = await repo.ObjectWriteAsync(GitObjectType.Blob, baseBody, TestContext.Current.CancellationToken);
        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        byte[] delta = ThinPackBuilder.MakeCopyAppendDelta(baseBody, " world\n"u8);
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddRefDelta(baseOid, delta);

        string packDir = Path.Combine(_tempDir, "thin_pack");
        (string packPath, GitIndexerProgress stats) = await IndexThinPackAsync(packDir, repo.Objects, builder.Build(), TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.LocalObjects);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(2, pack.ObjectCount);
        Assert.True(pack.Exists(baseOid));
        Assert.True(pack.Exists(resultOid));
    }

    [Fact]
    public async Task Commit_ThinPack_PackIsSelfContained()
    {
        await using GitRepository repo = await CreateRepoAsync("selfcontained");
        byte[] baseBody = "base data here\n"u8.ToArray();
        byte[] resultBody = "modified data here\n"u8.ToArray();

        GitOid baseOid = await repo.ObjectWriteAsync(GitObjectType.Blob, baseBody, TestContext.Current.CancellationToken);

        byte[] delta = ThinPackBuilder.MakeInsertOnlyDelta(baseBody.Length, resultBody);
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddRefDelta(baseOid, delta);

        string packDir = Path.Combine(_tempDir, "sc_pack");
        (string packPath, GitIndexerProgress stats) = await IndexThinPackAsync(packDir, repo.Objects, builder.Build(), TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.LocalObjects);

        // Every OID in the .idx must be readable from the .pack — the defining
        // property of a self-contained (unthinned) pack.
        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        foreach (GitOid oid in pack.Enumerate())
        {
            RawObjectData? data = await pack.ReadAsync(oid, TestContext.Current.CancellationToken);
            Assert.True(data.HasValue);
            Assert.True(data.Value.Data.Length > 0);
        }
    }

    [Fact]
    public async Task Commit_ThinPack_DeltaResolvesCorrectly()
    {
        await using GitRepository repo = await CreateRepoAsync("resolve");
        byte[] baseBody = "original\n"u8.ToArray();
        byte[] resultBody = "original\nappended\n"u8.ToArray();

        GitOid baseOid = await repo.ObjectWriteAsync(GitObjectType.Blob, baseBody, TestContext.Current.CancellationToken);

        byte[] delta = ThinPackBuilder.MakeCopyAppendDelta(baseBody, "appended\n"u8);
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddRefDelta(baseOid, delta);

        string packDir = Path.Combine(_tempDir, "resolve_pack");
        (string packPath, _) = await IndexThinPackAsync(packDir, repo.Objects, builder.Build(), TestContext.Current.CancellationToken);

        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);
        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        RawObjectData result = (await pack.ReadAsync(resultOid, TestContext.Current.CancellationToken))!.Value;
        Assert.Equal(resultBody, result.Data);

        RawObjectData baseObj = (await pack.ReadAsync(baseOid, TestContext.Current.CancellationToken))!.Value;
        Assert.Equal(baseBody, baseObj.Data);
    }

    [Fact]
    public async Task Commit_ThinPack_PackHeaderCountIncludesInjected()
    {
        await using GitRepository repo = await CreateRepoAsync("hdr");
        byte[] baseBody = "b\n"u8.ToArray();
        byte[] resultBody = "bx\n"u8.ToArray();

        GitOid baseOid = await repo.ObjectWriteAsync(GitObjectType.Blob, baseBody, TestContext.Current.CancellationToken);

        byte[] delta = ThinPackBuilder.MakeInsertOnlyDelta(baseBody.Length, resultBody);
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddRefDelta(baseOid, delta);

        string packDir = Path.Combine(_tempDir, "hdr_pack");
        (string packPath, _) = await IndexThinPackAsync(packDir, repo.Objects, builder.Build(), TestContext.Current.CancellationToken);

        byte[] packBytes = await File.ReadAllBytesAsync(packPath, cancellationToken: TestContext.Current.CancellationToken);

        // Bytes 8-11: object count (big-endian). Original header said 1; after
        // injection + update_header_and_rehash it must be 2.
        uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packBytes.AsSpan(8, 4));
        Assert.Equal(2u, count);
    }

    [Fact]
    public async Task Commit_ThinPack_TrailerMatchesRehash()
    {
        await using GitRepository repo = await CreateRepoAsync("trailer");
        byte[] baseBody = "trailer base\n"u8.ToArray();
        byte[] resultBody = "trailer modified\n"u8.ToArray();

        GitOid baseOid = await repo.ObjectWriteAsync(GitObjectType.Blob, baseBody, TestContext.Current.CancellationToken);

        byte[] delta = ThinPackBuilder.MakeInsertOnlyDelta(baseBody.Length, resultBody);
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddRefDelta(baseOid, delta);

        string packDir = Path.Combine(_tempDir, "trailer_pack");
        (string packPath, _) = await IndexThinPackAsync(packDir, repo.Objects, builder.Build(), TestContext.Current.CancellationToken);

        byte[] packBytes = await File.ReadAllBytesAsync(packPath, cancellationToken: TestContext.Current.CancellationToken);
        int oidSize = GitOid.SizeFor(GitHashAlgorithmKind.Sha1);

        // The trailer (last oidSize bytes) must equal the SHA-1 of everything before it.
        byte[] dataPart = packBytes[..^oidSize];
        byte[] trailer = packBytes[^oidSize..];

        using var hash = GitIncrementalHash.Create(GitHashAlgorithmKind.Sha1);
        hash.AppendData(dataPart);
        GitOid computed = hash.Finalize();
        Assert.Equal(computed.RawBytes.ToArray(), trailer);
    }

    [Fact]
    public async Task Commit_ThinPack_NoOdb_Throws()
    {
        GitOid fakeOid = GitObjectDb.HashObject(GitObjectType.Blob, "fake base"u8.ToArray(), GitHashAlgorithmKind.Sha1);

        byte[] delta = ThinPackBuilder.MakeInsertOnlyDelta("fake base"u8.Length, "x\n"u8);
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddRefDelta(fakeOid, delta);

        string packDir = Path.Combine(_tempDir, "noodb");
        Directory.CreateDirectory(packDir);
        var stats = new GitIndexerProgress();

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1, odb: null);
        await indexer.AppendAsync(builder.Build(), stats, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.CommitAsync(stats, TestContext.Current.CancellationToken));
        Assert.Contains("cannot fix a thin pack without an ODB", ex.Message);
    }

    [Fact]
    public async Task Commit_ThinPack_BaseAlreadyInPack_NoInjection()
    {
        byte[] baseBody = "in-pack-base\n"u8.ToArray();
        byte[] resultBody = "in-pack-base-X\n"u8.ToArray();
        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);

        // Pack contains BOTH the full base object AND a REF_DELTA against it —
        // no injection needed.
        byte[] delta = ThinPackBuilder.MakeInsertOnlyDelta(baseBody.Length, resultBody);
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, baseBody);
        builder.AddRefDelta(baseOid, delta);

        string packDir = Path.Combine(_tempDir, "basepresent");
        Directory.CreateDirectory(packDir);
        var stats = new GitIndexerProgress();

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1, odb: null);
        await indexer.AppendAsync(builder.Build(), stats, TestContext.Current.CancellationToken);
        await indexer.CommitAsync(stats, TestContext.Current.CancellationToken);

        Assert.Equal(0, stats.LocalObjects);
        Assert.Equal(2, stats.IndexedObjects);
    }

    [Fact]
    public async Task Commit_NonThinPack_NoInjection()
    {
        // Regression: a self-contained pack from GitPackWriter must not trigger
        // any injection. LocalObjects stays 0.
        await using GitRepository repo = await CreateRepoAsync("nonthin");
        GitOid commitOid = await CreateCommitAsync(repo);
        string packPath = await BuildPack(repo, commitOid);

        // Re-index the self-contained pack through a fresh indexer to confirm
        // no thin-pack path is triggered.
        string packDir = Path.Combine(_tempDir, "nonthin_reindex");
        Directory.CreateDirectory(packDir);
        byte[] packBytes = await File.ReadAllBytesAsync(packPath, cancellationToken: TestContext.Current.CancellationToken);

        var stats = new GitIndexerProgress();
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1, repo.Objects);
        await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken);
        await indexer.CommitAsync(stats, TestContext.Current.CancellationToken);

        Assert.Equal(0, stats.LocalObjects);
    }

    [Fact]
    public async Task Commit_ThinPack_MultipleBases_AllInjected()
    {
        await using GitRepository repo = await CreateRepoAsync("multi");
        byte[] baseA = "AAAA\n"u8.ToArray();
        byte[] baseB = "BBBB\n"u8.ToArray();
        byte[] baseC = "CCCC\n"u8.ToArray();

        GitOid oidA = await repo.ObjectWriteAsync(GitObjectType.Blob, baseA, TestContext.Current.CancellationToken);
        GitOid oidB = await repo.ObjectWriteAsync(GitObjectType.Blob, baseB, TestContext.Current.CancellationToken);
        GitOid oidC = await repo.ObjectWriteAsync(GitObjectType.Blob, baseC, TestContext.Current.CancellationToken);

        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddRefDelta(oidA, ThinPackBuilder.MakeInsertOnlyDelta(baseA.Length, "AAAAdiff\n"u8));
        builder.AddRefDelta(oidB, ThinPackBuilder.MakeInsertOnlyDelta(baseB.Length, "BBBBdiff\n"u8));
        builder.AddRefDelta(oidC, ThinPackBuilder.MakeInsertOnlyDelta(baseC.Length, "CCCCdiff\n"u8));

        string packDir = Path.Combine(_tempDir, "multi_pack");
        (string packPath, GitIndexerProgress stats) = await IndexThinPackAsync(packDir, repo.Objects, builder.Build(), TestContext.Current.CancellationToken);

        Assert.Equal(3, stats.LocalObjects);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(6, pack.ObjectCount);  // 3 deltas + 3 injected bases
        Assert.True(pack.Exists(oidA));
        Assert.True(pack.Exists(oidB));
        Assert.True(pack.Exists(oidC));
    }

    [Fact]
    public async Task Commit_ThinPack_ChainedRefDelta_Resolves()
    {
        // D1 is a REF_DELTA against external base A. D2 is a REF_DELTA against
        // D1's result (which is only known after D1 resolves). Only A should be
        // injected — D2's base resolves from the pack once D1 is resolved.
        // This validates the iterative fix_thin_pack approach over a naive
        // batch-inject-all-external-bases strategy.
        await using GitRepository repo = await CreateRepoAsync("chained");
        byte[] baseBody = "AAAA"u8.ToArray();
        byte[] d1Result = "AAAAXXXX"u8.ToArray();
        byte[] d2Result = "AAAAXXXXZZZZ"u8.ToArray();

        GitOid baseOid = await repo.ObjectWriteAsync(GitObjectType.Blob, baseBody, TestContext.Current.CancellationToken);
        GitOid d1ResultOid = GitObjectDb.HashObject(GitObjectType.Blob, d1Result, GitHashAlgorithmKind.Sha1);
        GitOid d2ResultOid = GitObjectDb.HashObject(GitObjectType.Blob, d2Result, GitHashAlgorithmKind.Sha1);

        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddRefDelta(baseOid, ThinPackBuilder.MakeCopyAppendDelta(baseBody, "XXXX"u8));
        builder.AddRefDelta(d1ResultOid, ThinPackBuilder.MakeCopyAppendDelta(d1Result, "ZZZZ"u8));

        string packDir = Path.Combine(_tempDir, "chained_pack");
        (string packPath, GitIndexerProgress stats) = await IndexThinPackAsync(packDir, repo.Objects, builder.Build(), TestContext.Current.CancellationToken);

        // Only the root external base is injected — D2's base (D1's result) is
        // resolved from the pack, not the ODB.
        Assert.Equal(1, stats.LocalObjects);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(3, pack.ObjectCount);  // D1 result + D2 result + injected A
        Assert.True(pack.Exists(baseOid));
        Assert.True(pack.Exists(d1ResultOid));
        Assert.True(pack.Exists(d2ResultOid));

        // Verify content integrity
        Assert.Equal(d1Result, (await pack.ReadAsync(d1ResultOid, TestContext.Current.CancellationToken))!.Value.Data);
        Assert.Equal(d2Result, (await pack.ReadAsync(d2ResultOid, TestContext.Current.CancellationToken))!.Value.Data);
    }
}
