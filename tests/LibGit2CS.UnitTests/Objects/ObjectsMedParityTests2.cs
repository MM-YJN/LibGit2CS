using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.UnitTests.Pack;

namespace LibGit2CS.UnitTests.Objects;

/// <summary> Parity tests for the object database and pack: (indexer categories + count), (unbounded reader depth), (64-bit delta
/// varints), (name hash), (prefix refresh-on-miss), (refresh-failure swallow), (alternates dedup), (commit-graph chunk offset range),
/// (midx FindEntry fallback), (tree bypath/create_updated), (commit reflog + signature validation), (blob binary + symlink).
/// </summary>
public sealed class ObjectsMedParityTests2 : IDisposable
{
    private readonly string _tempDir;

    public ObjectsMedParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectsMed2_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── indexer error categories + object count ───────────

    [Fact]
    public async Task Indexer_BadMagic_IndexerCategory()
    {
        // C (indexer.c:108): "wrong pack signature", GIT_ERROR_INDEXER.
        await using var indexer = new GitPackIndexer(NewDir(), GitHashAlgorithmKind.Sha1);
        byte[] header = new byte[12];
        "XXXX"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.AppendAsync(header, new GitIndexerProgress(), TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Indexer, ex.Category);
    }

    [Fact]
    public async Task Indexer_BadVersion_IndexerCategory()
    {
        // C (indexer.c:113): "wrong pack version", GIT_ERROR_INDEXER.
        await using var indexer = new GitPackIndexer(NewDir(), GitHashAlgorithmKind.Sha1);
        byte[] header = new byte[12];
        "PACK"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.AppendAsync(header, new GitIndexerProgress(), TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Indexer, ex.Category);
    }

    [Fact]
    public async Task Indexer_TrailerMismatch_IndexerCategory()
    {
        // C (indexer.c:1275): "packfile trailer mismatch", GIT_ERROR_INDEXER.
        string dir = NewDir();
        await using var indexer = new GitPackIndexer(dir, GitHashAlgorithmKind.Sha1);
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, "hello\n"u8.ToArray());
        byte[] pack = builder.Build();
        pack[^1] ^= 0xFF; // corrupt the trailer

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
        {
            await indexer.AppendAsync(pack, new GitIndexerProgress(), TestContext.Current.CancellationToken);
            await indexer.CommitAsync(new GitIndexerProgress(), TestContext.Current.CancellationToken);
        });
        Assert.Equal(GitErrorCategory.Indexer, ex.Category);
    }

    [Fact]
    public async Task Indexer_ObjectCountOverIntMax_RejectedAsTooManyObjects()
    {
        // C (indexer.c:907-915): the count is unsigned; values that cannot be represented (>= 2^31) are rejected up front instead of wrapping negative.
        await using var indexer = new GitPackIndexer(NewDir(), GitHashAlgorithmKind.Sha1);
        byte[] header = new byte[12];
        "PACK"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x8000_0000u);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.AppendAsync(header, new GitIndexerProgress(), TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Indexer, ex.Category);
        Assert.Equal("too many objects", ex.Message);
    }

    // ── the reader has NO delta-depth cap ──────────────────────

    [Fact]
    public async Task PackFile_DeepDeltaChain_Beyond50_ReadsFine()
    {
        // C walks the dependency chain while(true) (pack.c:596); the 50-limit
        // is writer-side GIT_PACK_DEPTH. A 60-deep chain must read.
        const int depth = 60;
        string packDir = NewDir();
        (byte[] packBytes, List<(GitOid Oid, long Offset, uint Crc)> entries) = BuildDeepOfsPack(depth);
        WriteIdxAndPack(packDir, packBytes, entries, GitHashAlgorithmKind.Sha1, out string packPath);

        using PackFile packFile = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitOid last = entries[^1].Oid;

        GitObjectHeader? header = await packFile.ReadHeaderAsync(last, TestContext.Current.CancellationToken);
        Assert.NotNull(header);
        Assert.Equal(GitObjectType.Blob, header.Value.Type);
        Assert.Equal(1, header.Value.Size);

        RawObjectData data = (await packFile.ReadAsync(last, TestContext.Current.CancellationToken))!.Value;
        Assert.Equal(GitObjectType.Blob, data.Type);
        byte expectedLast = (byte)depth;
        Assert.Single(data.Data);
        Assert.Equal(expectedLast, data.Data[0]);
    }

    /// <summary>
    /// Builds a pack whose objects form a single OFS_DELTA chain of the given
    /// depth: obj0 = blob "a", objN = OFS_DELTA(base = objN-1) with a 1-byte
    /// insert-only delta body.
    /// </summary>
    private static (byte[] Pack, List<(GitOid Oid, long Offset, uint Crc)> Entries) BuildDeepOfsPack(int depth)
    {
        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(GitHashAlgorithmKind.Sha1);
        var entries = new List<(GitOid Oid, long Offset, uint Crc)>();

        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)(depth + 1));
        ms.Write(header);
        hash.AppendData(header);

        Span<byte> hdrBuf = stackalloc byte[16];

        // obj0: full blob "a".
        {
            byte[] body = [(byte)'a'];
            long entryStart = ms.Position;
            int hdrLen = PackEncoding.WriteObjectHeader(hdrBuf, GitObjectType.Blob, body.Length);
            ms.Write(hdrBuf[..hdrLen]);
            hash.AppendData(hdrBuf[..hdrLen]);
            byte[] compressed = ZlibTestHelpers.CompressLooseObject(body);
            ms.Write(compressed);
            hash.AppendData(compressed);
            long entryEnd = ms.Position;
            uint crc = Crc32.HashToUInt32(packBytes(ms, entryStart, entryEnd));
            GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);
            entries.Add((oid, entryStart, crc));
        }

        // objN: OFS_DELTA whose base is the previous object. Each delta
        // inserts a DISTINCT byte so every object has a distinct OID (a
        // shared OID would make the .idx resolve every lookup to the same
        // entry and hide the chain).
        for (int i = 1; i <= depth; i++)
        {
            byte resultByte = (byte)i; // distinct per index (1..depth)

            byte[] delta = [0x01, 0x01, 0x01, resultByte]; // base 1, res 1, insert 1 byte
            long entryStart = ms.Position;
            int hdrLen = PackEncoding.WriteObjectHeader(hdrBuf, GitObjectType.OfsDelta, delta.Length);
            ms.Write(hdrBuf[..hdrLen]);
            hash.AppendData(hdrBuf[..hdrLen]);

            long dist = entryStart - entries[i - 1].Offset;
            Assert.True(dist is >= 1 and < 128, "single-byte OFS offset encoding requires dist < 128");
            ms.WriteByte((byte)dist);
            hash.AppendData([(byte)dist]);

            byte[] compressed = ZlibTestHelpers.CompressLooseObject(delta);
            ms.Write(compressed);
            hash.AppendData(compressed);
            long entryEnd = ms.Position;
            uint crc = Crc32.HashToUInt32(packBytes(ms, entryStart, entryEnd));
            byte[] resultBody = [resultByte];
            GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);
            entries.Add((oid, entryStart, crc));
        }

        // Trailer.
        GitOid trailer = hash.Finalize();
        ms.Write(trailer.RawBytes.ToArray());
        return (ms.ToArray(), entries);

        static byte[] packBytes(MemoryStream stream, long start, long end)
        {
            byte[] buf = stream.GetBuffer();
            return buf[(int)start..(int)end];
        }
    }

    // ── delta header varints decode in 64 bits ─────────────────

    [Fact]
    public void DeltaApply_LargeBaseSizeVarint_DoesNotWrapInt()
    {
        // base size = 2^32 + 5: a 32-bit accumulator wraps to 5, which would
        // match a 5-byte base and let a bogus delta apply.
        byte[] baseBuf = new byte[5];
        byte[] delta = [.. Varint(0x1_0000_0005L), .. Varint(1), 0x01, 0x78];

        Assert.Throws<GitException>(() => DeltaEncoder.Apply(baseBuf, delta));
    }

    [Fact]
    public void DeltaApply_HugeResultSize_ThrowsGitException()
    {
        byte[] baseBuf = [0x01];
        byte[] delta = [.. Varint(1), .. Varint(0x8000_0007L), 0x01, 0x78];

        Assert.Throws<GitException>(() => DeltaEncoder.Apply(baseBuf, delta));
    }

    private static byte[] Varint(long value)
    {
        var bytes = new List<byte>();
        ulong v = (ulong)value;
        do
        {
            byte b = (byte)(v & 0x7f);
            v >>= 7;
            if (v != 0)
            {
                b |= 0x80;
            }

            bytes.Add(b);
        }
        while (v != 0);

        return bytes.ToArray();
    }

    // ── name hash feeds the writer's object records ────────────

    [Fact]
    public void NameHash_Null_IsZero()
    {
        Assert.Equal(0u, GitPackWriter.NameHash((string?)null));
    }

    [Fact]
    public void NameHash_WhitespaceSkipped_LaterCharsWeighMore()
    {
        Assert.Equal(GitPackWriter.NameHash("a.txt"), GitPackWriter.NameHash("a.txt"));
        Assert.Equal(GitPackWriter.NameHash("a b.txt"), GitPackWriter.NameHash("ab.txt"));
        Assert.NotEqual(GitPackWriter.NameHash("a.txt"), GitPackWriter.NameHash("b.txt"));

        // Later characters count "most" — "x.c" vs "x.o" differ in the last char.
        Assert.NotEqual(GitPackWriter.NameHash("file.c"), GitPackWriter.NameHash("file.o"));
    }

    // ── ODB refresh semantics ───────────────────────────────

    [Fact]
    public async Task Odb_AbbreviatedLookup_RefreshesOnMiss()
    {
        string repoDir = NewDir();
        await using GitRepository repo1 = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);
        GitOid blob = await repo1.ObjectWriteAsync(GitObjectType.Blob, "refresh me\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // Second handle, opened BEFORE the pack appears.
        await using GitRepository repo2 = await GitRepository.OpenAsync(repoDir, new GitContext(), TestContext.Current.CancellationToken);

        // Move the object into a pack (written after repo2's ODB was built).
        string packDir = Path.Combine(repoDir, "objects", "pack");
        Directory.CreateDirectory(packDir);
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, "refresh me\n"u8.ToArray());
        byte[] packBytes = builder.Build();
        int hdrLen = PackEncoding.WriteObjectHeader(stackalloc byte[16], GitObjectType.Blob, "refresh me\n".Length);
        uint crc = Crc32.HashToUInt32(packBytes.AsSpan(12, hdrLen + ZlibTestHelpers.CompressLooseObject("refresh me\n"u8).Length));
        WriteIdxAndPack(packDir, packBytes, [(blob, 12L, crc)], GitHashAlgorithmKind.Sha1, out _);

        // Delete the loose copy so the pack is the only source.
        string loosePath = Path.Combine(repoDir, "objects", blob.ToString()[..2], blob.ToString()[2..]);
        File.Delete(loosePath);

        // Abbreviated lookups must refresh-on-miss.
        string prefix = blob.ToString()[..7];
        (bool found, GitOid oid) = await repo2.Objects.ExistsPrefixAsync(GitOid.Parse(prefix, GitHashAlgorithmKind.Sha1), TestContext.Current.CancellationToken);
        Assert.True(found);
        Assert.Equal(blob, oid);

        GitBlob? lookedUp = await repo2.ObjectLookupPrefixAsync<GitBlob>(GitOid.Parse(prefix, GitHashAlgorithmKind.Sha1), TestContext.Current.CancellationToken);
        Assert.NotNull(lookedUp);
        Assert.Equal(blob, lookedUp!.Id);
    }

    [Fact]
    public async Task Odb_CorruptPackInDir_RefreshFailureYieldsNotFound()
    {
        string repoDir = NewDir();
        await using GitRepository repo1 = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);
        _ = await repo1.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);

        await using GitRepository repo2 = await GitRepository.OpenAsync(repoDir, new GitContext(), TestContext.Current.CancellationToken);

        // Drop a corrupt pack + idx into the pack dir AFTER the ODB was built.
        string packDir = Path.Combine(repoDir, "objects", "pack");
        Directory.CreateDirectory(packDir);
        File.WriteAllBytes(Path.Combine(packDir, "pack-aaaa.pack"), [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3]);
        File.WriteAllBytes(Path.Combine(packDir, "pack-aaaa.idx"), [0xBA, 0xAD, 0xF0, 0x0D, 9, 9, 9]);

        // C (odb.c:1049-1053): a failed refresh short-circuits to
        // not-found instead of propagating the pack-open error.
        var missing = GitOid.Parse("1111111111111111111111111111111111111111", GitHashAlgorithmKind.Sha1);
        bool exists = await repo2.Objects.ExistsAsync(missing, TestContext.Current.CancellationToken);
        Assert.False(exists);

        GitObject? obj = await repo2.ObjectLookupAsync(missing, TestContext.Current.CancellationToken);
        Assert.Null(obj);
    }

    // ── alternates inode dedup ─────────────────────────────────

    [Fact]
    public async Task Odb_AlternatesListingSameDirTwice_LoadsOnce()
    {
        string repoDir = NewDir();
        GitOid blob;
        await using (GitRepository repo1 = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken))
        {
            blob = await repo1.ObjectWriteAsync(GitObjectType.Blob, "dedup\n"u8.ToArray(), TestContext.Current.CancellationToken);

            // The objects dir listed twice (duplicate alternate) + a self-cycle.
            string objectsDir = Path.Combine(repoDir, "objects");
            string altFile = Path.Combine(objectsDir, "info", "alternates");
            Directory.CreateDirectory(Path.GetDirectoryName(altFile)!);
            await File.WriteAllTextAsync(altFile, objectsDir + "\n" + objectsDir + "\n", TestContext.Current.CancellationToken);
        }

        await using GitRepository repo2 = await GitRepository.OpenAsync(repoDir, new GitContext(), TestContext.Current.CancellationToken);

        // Each OID appears exactly once despite the duplicate/cyclic alternates.
        var seen = new HashSet<GitOid>();
        int count = 0;
        await foreach (GitOid oid in repo2.Objects.EnumerateAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(seen.Add(oid), $"OID {oid} enumerated more than once");
            count++;
        }

        Assert.True(count >= 1);
        Assert.Contains(blob, seen);
    }

    // ── commit-graph chunk offset range ────────────────────────

    [Fact]
    public async Task CommitGraph_ChunkOffsetOverIntMax_Rejected()
    {
        string objectsDir = Path.Combine(NewDir(), "objects");
        Directory.CreateDirectory(Path.Combine(objectsDir, "info"));

        // Minimal graph: header + OIDF/OIDL/CDAT chunk table + trailer, with OIDL's chunk offset high word set to 0x80000000 (>= 2^31) — C rejects via the
        // 64-bit trailer comparison.
        byte[] buf = BuildMinimalGraphWithHugeOffset();

        await File.WriteAllBytesAsync(Path.Combine(objectsDir, "info", "commit-graph"), buf, TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await CommitGraph.OpenAsync(objectsDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        // C (commit_graph.c:104-108): commit_graph_error returns -1 (GIT_ERROR) —.
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("out of range", ex.Message);
    }

    private static byte[] BuildMinimalGraphWithHugeOffset()
    {
        const int numChunks = 3;
        const int headerSize = 8 + (1 + numChunks) * 12;
        const int oidfSize = 256 * 4;
        const int oidlSize = 20;
        const int cdatSize = 20 + 16;
        int trailerOff = headerSize + oidfSize + oidlSize + cdatSize;
        byte[] buf = new byte[trailerOff + 20];
        using var ms = new MemoryStream(buf, 0, buf.Length, writable: true);
        ms.Write("CGPH"u8);
        ms.WriteByte(1);
        ms.WriteByte(1);
        ms.WriteByte((byte)numChunks);
        ms.WriteByte(0);
        WriteChunkEntry(ms, 0x4F494446, headerSize);                              // OIDF
        WriteChunkEntry(ms, 0x4F49444C, unchecked((long)0x8000_0000_0000_0100UL));                  // OIDL — huge offset
        WriteChunkEntry(ms, 0x43444154, headerSize + oidfSize + oidlSize);        // CDAT
        // Fill OIDF with a fanout that would make numCommits=1.
        Span<byte> fanout = stackalloc byte[4];
        for (int i = 0; i < 256; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(fanout, i < (byte)'x' ? 0u : 1u);
            ms.Write(fanout);
        }

        // Checksum.
        byte[] checksum = SHA1.HashData(buf.AsSpan(0, trailerOff));
        ms.Write(checksum);
        return buf;
    }

    private static void WriteChunkEntry(MemoryStream ms, uint id, long offset)
    {
        Span<byte> entry = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(entry[..4], id);
        BinaryPrimitives.WriteUInt32BigEndian(entry[4..8], (uint)(offset >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)offset);
        ms.Write(entry);
    }

    // ── bypath trailing slash requires a tree ──────────────────

    [Fact]
    public async Task Tree_EntryByPath_TrailingSlashOnBlob_NotFound()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("foo", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        using GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        // "foo/" where foo is a blob → C returns ENOTFOUND ("exists but is
        // not a tree").
        Assert.Null(await tree.EntryByPathAsync("foo/", TestContext.Current.CancellationToken));
        Assert.NotNull(await tree.EntryByPathAsync("foo", TestContext.Current.CancellationToken));

        // "foo/" where foo is a tree → the tree entry is returned.
        GitOid subBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, "y\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder sub = repo.NewTreeBuilder();
        await sub.InsertAsync("inner", subBlob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid subOid = await sub.WriteAsync(CancellationToken.None);
        using GitTreeBuilder bld2 = repo.NewTreeBuilder();
        await bld2.InsertAsync("dir", subOid, GitFileMode.Tree, TestContext.Current.CancellationToken);
        GitOid tree2 = await bld2.WriteAsync(CancellationToken.None);
        using GitTree tree2Obj = (await repo.ObjectLookupAsync<GitTree>(tree2, TestContext.Current.CancellationToken))!;
        Assert.NotNull(await tree2Obj.EntryByPathAsync("dir/", TestContext.Current.CancellationToken));
        Assert.NotNull(await tree2Obj.EntryByPathAsync("dir/inner", TestContext.Current.CancellationToken));
    }

    // ── create_updated rejects duplicate paths ─────────────────

    [Fact]
    public async Task TreeCreateUpdated_DuplicatePaths_Rejected()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blob2 = await repo.ObjectWriteAsync(GitObjectType.Blob, "y\n"u8.ToArray(), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.TreeCreateUpdatedAsync(baseline: null,
            [
                new GitTreeUpdate(GitTreeUpdateAction.Upsert, "a.txt", blob),
                new GitTreeUpdate(GitTreeUpdateAction.Upsert, "a.txt", blob2),
            ], TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("duplicate entries given for update", ex.Message);
    }

    // ── create_updated D/F conflict vs the ORIGINAL tree ───────

    [Fact]
    public async Task TreeCreateUpdated_RemovedFileThenSubdir_DFConflict()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("a", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid baseline = await bld.WriteAsync(CancellationToken.None);
        using GitTree baselineTree = (await repo.ObjectLookupAsync<GitTree>(baseline, TestContext.Current.CancellationToken))!;

        // C (tree.c:1220-1228): the walk-down consults the ORIGINAL tree
        // FIRST — removing "a" then creating "a/b" is still a D/F conflict.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.TreeCreateUpdatedAsync(baselineTree,
            [
                new GitTreeUpdate(GitTreeUpdateAction.Remove, "a"),
                new GitTreeUpdate(GitTreeUpdateAction.Upsert, "a/b", blob),
            ], TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("D/F conflict", ex.Message);
    }

    // ── merge commit reflog message ────────────────────────────

    [Fact]
    public async Task CommitCreate_MergeParents_ReflogSaysMerge()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("f", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid tree = await bld.WriteAsync(CancellationToken.None);

        GitOid c1 = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "one\n",
            UpdateRef = "refs/heads/main",
        }, cancellationToken: TestContext.Current.CancellationToken);
        GitOid c2 = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "two\n",
            UpdateRef = "refs/heads/other",
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitOid merge = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [c1, c2],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "merged\n",
            UpdateRef = "refs/heads/main",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // C (refs.c:1162-1191): commit_type() = " (merge)" for >= 2 parents.
        GitRefLog reflog = (await repo.ReferenceReadLogAsync("refs/heads/main", TestContext.Current.CancellationToken))!;
        Assert.Contains(reflog, e => e.Message.Contains("commit (merge): merged", StringComparison.Ordinal));
    }

    // ── create_with_signature validates tree/parents ───────────

    [Fact]
    public async Task CommitCreateWithSignature_MissingTree_Rejected()
    {
        await using GitRepository repo = await InitRepoAsync();

        string missing = new('1', 40);
        string content = $"tree {missing}\n" +
                         "author A U Thor <a@b.c> 1700000000 +0000\n" +
                         "committer A U Thor <a@b.c> 1700000000 +0000\n" +
                         "\nmessage\n";

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await Commit.CreateWithSignatureAsync(repo, content, null, null, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("tree", ex.Message);
    }

    [Fact]
    public async Task CommitCreateWithSignature_ValidTree_Succeeds()
    {
        await using GitRepository repo = await InitRepoAsync();
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("f", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid tree = await bld.WriteAsync(CancellationToken.None);

        string content = $"tree {tree}\n" +
                         "author A U Thor <a@b.c> 1700000000 +0000\n" +
                         "committer A U Thor <a@b.c> 1700000000 +0000\n" +
                         "\nmessage\n";

        GitOid oid = await Commit.CreateWithSignatureAsync(repo, content, "-----BEGIN PGP-----\nxyz\n-----END PGP-----", null, TestContext.Current.CancellationToken);
        Commit? parsed = await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(parsed);
        Assert.Equal("message\n", parsed!.Message);
    }

    // ── data-is-binary scans the full buffer ───────────────────

    [Fact]
    public void BlobDataIsBinary_ScansFullBuffer()
    {
        // Binary signal appears past the 8000-byte cap of git_blob_is_binary.
        byte[] data = new byte[9000];
        Array.Fill(data, (byte)'a');
        data[8500] = 0; // NUL past 8000

        Assert.True(GitBlob.IsBinaryBytes(data));

        // The blob-level check (git_blob_is_binary) caps at 8000 → not binary.
        var blob = GitBlob.Parse(owner: null, default, data);
        Assert.False(blob.IsBinary);
    }

    // ── create_from_disk stores the link target, not content ──

    [Fact]
    public async Task BlobCreateFromDisk_SymlinkOutsideWorkdir_StoresTargetPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // symlinks need privileges on Windows
        }

        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        // A symlink OUTSIDE the workdir pointing at a real file.
        string outside = Path.Combine(_tempDir, "target-file.txt");
        await File.WriteAllTextAsync(outside, "SECRET CONTENT\n", TestContext.Current.CancellationToken);
        string linkPath = Path.Combine(_tempDir, "link.txt");
        File.Delete(linkPath);
        File.CreateSymbolicLink(linkPath, outside);

        GitOid oid = await repo.BlobCreateFromDiskAsync(linkPath, TestContext.Current.CancellationToken);

        // C (blob.c:210-228): the blob content is the link-target path bytes.
        GitOid expected = await repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes(outside), TestContext.Current.CancellationToken);
        Assert.Equal(expected, oid);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async ValueTask<GitRepository> InitRepoAsync()
    {
        string repoDir = NewDir();
        return await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Writes a .idx v2 + .pack pair (mirrors CrossPackRefDeltaTests).
    /// </summary>
    private static void WriteIdxAndPack(
        string packDir,
        byte[] packBytes,
        List<(GitOid Oid, long Offset, uint Crc)> entries,
        GitHashAlgorithmKind algorithm,
        out string packPath)
    {
        int oidSize = GitOid.SizeFor(algorithm);
        List<(GitOid Oid, long Offset, uint Crc)> sorted = [.. entries.OrderBy(e => e.Oid, OidRawComparer.s_instance)];

        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(algorithm);
        Span<byte> magic = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(magic[0..4], 0xff744f63);
        BinaryPrimitives.WriteUInt32BigEndian(magic[4..8], 2);
        ms.Write(magic);
        hash.AppendData(magic);

        int[] byteCounts = new int[256];
        foreach ((GitOid oid, _, _) in sorted)
        {
            byteCounts[oid.RawBytes[0]]++;
        }

        int cumulative = 0;
        Span<byte> fanoutBuf = stackalloc byte[4];
        for (int i = 0; i < 256; i++)
        {
            cumulative += byteCounts[i];
            BinaryPrimitives.WriteUInt32BigEndian(fanoutBuf, (uint)cumulative);
            ms.Write(fanoutBuf);
            hash.AppendData(fanoutBuf);
        }

        foreach ((GitOid oid, _, _) in sorted)
        {
            ms.Write(oid.RawBytes);
            hash.AppendData(oid.RawBytes);
        }

        Span<byte> crcBuf = stackalloc byte[4];
        foreach ((_, _, uint crc) in sorted)
        {
            BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
            ms.Write(crcBuf);
            hash.AppendData(crcBuf);
        }

        Span<byte> offBuf = stackalloc byte[4];
        foreach ((_, long off, _) in sorted)
        {
            BinaryPrimitives.WriteUInt32BigEndian(offBuf, (uint)off);
            ms.Write(offBuf);
            hash.AppendData(offBuf);
        }

        byte[] packChecksum = packBytes[^oidSize..];
        ms.Write(packChecksum);
        hash.AppendData(packChecksum);
        GitOid idxChecksum = hash.Finalize();
        ms.Write(idxChecksum.RawBytes);

        packPath = Path.Combine(packDir, "pack-" + Guid.NewGuid().ToString("N")[..8] + ".pack");
        string idxPath = Path.ChangeExtension(packPath, ".idx");
#pragma warning disable CA1849
        File.WriteAllBytes(packPath, packBytes);
        File.WriteAllBytes(idxPath, ms.ToArray());
#pragma warning restore CA1849
    }

    private sealed class OidRawComparer : IComparer<GitOid>
    {
        internal static readonly OidRawComparer s_instance = new();

        public int Compare(GitOid x, GitOid y)
            => x.RawBytes.SequenceCompareTo(y.RawBytes);
    }
}
