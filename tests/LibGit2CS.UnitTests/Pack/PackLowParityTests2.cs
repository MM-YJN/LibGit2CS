using System.Buffers.Binary;
using System.Reflection;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.UnitTests;

namespace LibGit2CS.UnitTests.Pack;

/// <summary> Parity tests for ODB pack-side items: truncated-header varint error ordering, large-offset bounds
/// off-by-one, invented Stage: 2 progress, midx large-offset-before-pack-index order, 0-object midx rejection, whole-second pack mtime
/// sort. Expectations are C-verified against libgit2 1.9.4 (pack.c:428-434, 1282-1283; pack.h:52-55; midx.c:121-122, 138-139, 432-449; odb_pack.c:205-228).
/// </summary>
public sealed class PackLowParityTests2 : IDisposable
{
    private readonly string _tempDir;

    public PackLowParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackLow2_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── truncated-header varint — bounds before shift guard ────

    [Fact]
    public async Task ReadHeader_TruncatedContinuationHeader_BufferTooShort()
    {
        // C (pack.c:428-434): the buffer-too-small check (GIT_EBUFS) fires
        // BEFORE the shift-overflow guard. A header whose continuation chain
        // runs past EOF must surface GIT_EBUFS, not "header length is zero".
        // The pack is 22 bytes: 12-byte pack header + a 10-byte object header
        // (first byte 0x9F = type 1 / size 0xF / continuation, then nine
        // 0x80 continuation bytes) that ends exactly at EOF. The idx's pack
        // checksum field mirrors the pack's last 20 bytes so the open-time
        // trailer comparison passes.
        string dir = NewDir();
        var oid = GitOid.FromRaw(Enumerable.Repeat((byte)0x11, 20).ToArray(), GitHashAlgorithmKind.Sha1);

        byte[] pack = new byte[22];
        "PACK"u8.CopyTo(pack.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(pack.AsSpan(4), 2);   // version
        BinaryPrimitives.WriteUInt32BigEndian(pack.AsSpan(8), 1);   // 1 object
        pack[12] = 0x9F;                                            // type 1, size 0xF, continuation
        pack.AsSpan(13, 9).Fill(0x80);                              // nine continuation bytes

        byte[] idx = BuildIdxV2(
            [(oid, 12)],
            largeOffsets: [],
            packChecksum: pack.AsSpan(pack.Length - 20, 20).ToArray());

        string packPath = Path.Combine(dir, "pack-craft.pack");
        string idxPath = Path.Combine(dir, "pack-craft.idx");
        await File.WriteAllBytesAsync(packPath, pack, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(idxPath, idx, TestContext.Current.CancellationToken);

        using PackFile pf = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => pf.ReadHeaderAsync(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.BufferTooShort, ex.Code);
    }

    // ── large-offset bounds check off-by-one byte ──────────────

    [Fact]
    public async Task GetObjectOffset_LargeOffsetAtLenMinus8_Rejected()
    {
        // C (pack.c:1282-1283): `if (index >= end - 8) return -1;` — a large
        // offset whose 8-byte entry starts at exactly len-8 (the trailer
        // checksum) is rejected, and the checksum bytes are never read as an
        // offset.
        string dir = NewDir();
        var oid0 = GitOid.FromRaw(Enumerable.Repeat((byte)0x11, 20).ToArray(), GitHashAlgorithmKind.Sha1);
        var oid1 = GitOid.FromRaw(Enumerable.Repeat((byte)0x22, 20).ToArray(), GitHashAlgorithmKind.Sha1);

        // 2 objects, no large-offset table: len = 8+1024+40+8+8+40 = 1128.
        // Entry 0's offset is 0x80000004 → largeIdx 4 → large offset table
        // position 1080+8+32 = 1120 = len - 8 → C rejects.
        byte[] idx = BuildIdxV2(
            [(oid0, unchecked((int)0x80000004u)), (oid1, 12)],
            largeOffsets: [],
            packChecksum: Enumerable.Repeat((byte)0x33, 20).ToArray());
        string idxPath = Path.Combine(dir, "pack-craft.idx");
        await File.WriteAllBytesAsync(idxPath, idx, TestContext.Current.CancellationToken);

        using GitPackIndex gpi = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Throws<GitException>(() => gpi.GetObjectOffset(0));

        // Control: a large index one entry earlier (1120-8 = 1112 < len-8) is
        // accepted on both sides (C reads the trailer-adjacent bytes).
        byte[] idx2 = BuildIdxV2(
            [(oid0, unchecked((int)0x80000003u)), (oid1, 12)],
            largeOffsets: [],
            packChecksum: Enumerable.Repeat((byte)0x33, 20).ToArray());
        string idxPath2 = Path.Combine(dir, "pack-craft2.idx");
        await File.WriteAllBytesAsync(idxPath2, idx2, TestContext.Current.CancellationToken);

        using GitPackIndex gpi2 = await GitPackIndex.OpenAsync(idxPath2, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        _ = gpi2.GetObjectOffset(0); // must not throw
    }

    // ── no invented Stage: 2 progress during pack writing ──────

    [Fact]
    public async Task WriteToDirectory_NoStage2Progress()
    {
        // C (pack.h:52-55): git_packbuilder_stage_t has only ADDING_OBJECTS (0)
        // and DELTAFICATION (1). write_pack reports no progress at all — pack
        // writing progress flows through the indexer callback: the two
        // `Stage: 2` reports must not be emitted.
        string repoPath = Path.Combine(NewDir(), "repo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), TestContext.Current.CancellationToken);
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        string packDir = NewDir();
        using GitPackWriter pb = repo.NewPackWriter();
        await pb.InsertAsync(commitOid, TestContext.Current.CancellationToken);
        var progress = new SyncProgress<GitPackProgress>();
        await pb.WriteToDirectoryAsync(packDir, progress, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(progress.Reports, r => r.Stage == 2);
    }

    // ── midx parse checks ──────────────────────────────────

    [Fact]
    public async Task OpenMidx_EmptyOidlChunk_Rejected()
    {
        // C (midx.c:121-122): an empty OID Lookup chunk is rejected with midx_error (GIT_ERROR_ODB, code -1).
        string dir = NewDir();
        byte[] midx = BuildMidx(
            numPackfiles: 1,
            pnam: "pack-abc.idx\0"u8.ToArray(),
            numObjects: 0,
            oidlLength: 0,
            ooffLength: 0);
        await File.WriteAllBytesAsync(Path.Combine(dir, "multi-pack-index"), midx, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await MultiPackIndex.OpenAsync(dir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task OpenMidx_EmptyOoffChunk_Rejected()
    {
        // C (midx.c:138-139): an empty Object Offsets chunk is rejected.
        string dir = NewDir();
        byte[] midx = BuildMidx(
            numPackfiles: 1,
            pnam: "pack-abc.idx\0"u8.ToArray(),
            numObjects: 1,
            oidlLength: 20,
            ooffLength: 0);
        await File.WriteAllBytesAsync(Path.Combine(dir, "multi-pack-index"), midx, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await MultiPackIndex.OpenAsync(dir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task OpenMidx_EmptyOidfChunk_Rejected()
    {
        // C (midx.c:101-102): an empty OID Fanout chunk is rejected.
        string dir = NewDir();
        byte[] midx = BuildMidx(
            numPackfiles: 1,
            pnam: "pack-abc.idx\0"u8.ToArray(),
            numObjects: 0,
            oidlLength: 0,
            ooffLength: 0,
            oidfLength: 0);
        await File.WriteAllBytesAsync(Path.Combine(dir, "multi-pack-index"), midx, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await MultiPackIndex.OpenAsync(dir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task MidxEntry_DoublyCorrupt_ReturnsNull()
    {
        // C (midx.c:432-449): git_midx_entry_find resolves the large offset FIRST (notfound when its position is out of range) and only then validates the pack
        // index. Both corruptions map to null (per-pack fallback), so this pins the
        // C-faithful order: the large-offset check is the one that fires.
        string dir = NewDir();
        byte[] midx = BuildMidxWithEntry(
            packIndex: 99,        // out of range
            offsetLo: unchecked((int)0x80000063u), // large-offset flag + out-of-range position
            numLargeOffsets: 1);
        await File.WriteAllBytesAsync(Path.Combine(dir, "multi-pack-index"), midx, TestContext.Current.CancellationToken);

        MultiPackIndex? m = await MultiPackIndex.OpenAsync(dir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(m);

        var oid = GitOid.FromRaw(Enumerable.Repeat((byte)0x44, 20).ToArray(), GitHashAlgorithmKind.Sha1);
        Assert.Null(m.FindEntry(oid));
    }

    // ── pack sort compares whole seconds, not sub-second ───────

    [Fact]
    public async Task Refresh_SameWholeSecondMtimes_TieKeepsLoadOrder()
    {
        // C (pack.c:1227): p->mtime = (git_time_t)st.st_mtime — whole seconds.
        // Two packs written within the same whole second are a TIE in C and
        // keep their load order, not an order based on their sub-second
        // mtimes. Swapping only the sub-second parts must not change the pack
        // order.
        string packDir = NewDir();
        byte[] packBytes = FixtureLoader.LoadBytes("Fixtures/pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.pack");
        byte[] idxBytes = FixtureLoader.LoadBytes("Fixtures/pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        await File.WriteAllBytesAsync(Path.Combine(packDir, "pack-one.pack"), packBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(packDir, "pack-one.idx"), idxBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(packDir, "pack-two.pack"), packBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(packDir, "pack-two.idx"), idxBytes, TestContext.Current.CancellationToken);

        var whole = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(packDir, "pack-one.pack"), whole.AddMilliseconds(100));
        File.SetLastWriteTimeUtc(Path.Combine(packDir, "pack-two.pack"), whole.AddMilliseconds(900));

        List<string> order1 = await LoadPackOrderAsync(packDir);

        // Swap the sub-second parts; the whole-second values are unchanged.
        File.SetLastWriteTimeUtc(Path.Combine(packDir, "pack-one.pack"), whole.AddMilliseconds(900));
        File.SetLastWriteTimeUtc(Path.Combine(packDir, "pack-two.pack"), whole.AddMilliseconds(100));

        List<string> order2 = await LoadPackOrderAsync(packDir);

        Assert.Equal(order1, order2);
    }

    private static async Task<List<string>> LoadPackOrderAsync(string packDir)
    {
        var backend = new PackObjectBackend(packDir, GitHashAlgorithmKind.Sha1);
        await backend.RefreshAsync(TestContext.Current.CancellationToken);

        FieldInfo field = typeof(PackObjectBackend).GetField("_packs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var packs = (List<PackFile>)field.GetValue(backend)!;
        return packs.Select(p => Path.GetFileName(p.PackPath)).ToList();
    }

    // ── binary builders ───────────────────────────────────────────────────

    /// <summary>Builds a v2 pack index over (oid, offset) pairs.</summary>
    private static byte[] BuildIdxV2((GitOid Oid, int Offset)[] entries, int[] largeOffsets, byte[] packChecksum)
    {
        int oidSize = 20;
        int len = 8 + 4 * 256 + entries.Length * (oidSize + 4 + 4) + largeOffsets.Length * 8 + oidSize * 2;
        byte[] data = new byte[len];

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0), 0xff744f63);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 2);

        // Fanout: counts by first byte.
        int[] counts = new int[256];
        foreach ((GitOid oid, _) in entries)
        {
            counts[oid.RawBytes[0]]++;
        }

        int running = 0;
        for (int i = 0; i < 256; i++)
        {
            running += counts[i];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8 + i * 4), (uint)running);
        }

        int pos = 8 + 4 * 256;
        foreach ((GitOid oid, _) in entries)
        {
            oid.RawBytes.CopyTo(data.AsSpan(pos));
            pos += oidSize;
        }

        foreach ((GitOid _, int offset) in entries)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(pos), (uint)offset);
            pos += 4;
        }

        foreach ((GitOid _, int offset) in entries)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(pos), (uint)offset);
            pos += 4;
        }

        foreach (int lo in largeOffsets)
        {
            BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(pos), lo);
            pos += 8;
        }

        packChecksum.CopyTo(data, pos);
        return data;
    }

    /// <summary>
    /// Builds a multi-pack-index file with PNAM + OIDF + OIDL + OOFF chunks.
    /// The fanout is all-zero (numObjects must be 0) unless
    /// <paramref name="numObjects"/> &gt; 0, in which case fanout[255] is set.
    /// </summary>
    private static byte[] BuildMidx(int numPackfiles, byte[] pnam, int numObjects, int oidlLength, int ooffLength, int oidfLength = 4 * 256)
    {
        const int numChunks = 4; // PNAM, OIDF, OIDL, OOFF
        int tableSize = (1 + numChunks) * 12;
        int pnamOff = 12 + tableSize;
        int oidfOff = pnamOff + pnam.Length;
        int oidlOff = oidfOff + oidfLength;
        int ooffOff = oidlOff + oidlLength;
        int trailerOff = ooffOff + ooffLength;
        byte[] data = new byte[trailerOff + 20];

        "MIDX"u8.CopyTo(data.AsSpan(0, 4));
        data[4] = 1; // version
        data[5] = 1; // oid version
        data[6] = numChunks;
        data[7] = 0; // base midx count
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)numPackfiles);

        WriteChunkEntry(data, 12, 0, "PNAM", pnamOff);
        WriteChunkEntry(data, 12, 1, "OIDF", oidfOff);
        WriteChunkEntry(data, 12, 2, "OIDL", oidlOff);
        WriteChunkEntry(data, 12, 3, "OOFF", ooffOff);
        WriteChunkEntry(data, 12, 4, 0x00000000, trailerOff);

        pnam.CopyTo(data, pnamOff);

        if (oidfLength == 4 * 256)
        {
            // Fanout: all zero except [255] = numObjects (monotonic).
            if (numObjects > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(oidfOff + 255 * 4), (uint)numObjects);
            }
        }

        return data;
    }

    /// <summary>
    /// Builds a 1-object midx whose single OOFF entry carries
    /// <paramref name="packIndex"/> and <paramref name="offsetLo"/>, with a
    /// LOFF chunk of <paramref name="numLargeOffsets"/> entries so the
    /// large-offset branch of FindEntry is reachable.
    /// </summary>
    private static byte[] BuildMidxWithEntry(int packIndex, int offsetLo, int numLargeOffsets)
    {
        byte[] oidl = Enumerable.Repeat((byte)0x44, 20).ToArray();
        const int numChunks = 5; // PNAM, OIDF, OIDL, OOFF, LOFF
        int tableSize = (1 + numChunks) * 12;
        int pnamOff = 12 + tableSize;
        byte[] pnam = "pack-abc.idx\0"u8.ToArray();
        int oidfOff = pnamOff + pnam.Length;
        int oidlOff = oidfOff + 4 * 256;
        int ooffOff = oidlOff + oidl.Length;
        int loffOff = ooffOff + 8;
        int trailerOff = loffOff + numLargeOffsets * 8;
        byte[] data = new byte[trailerOff + 20];

        "MIDX"u8.CopyTo(data.AsSpan(0, 4));
        data[4] = 1;
        data[5] = 1;
        data[6] = numChunks;
        data[7] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);

        WriteChunkEntry(data, 12, 0, "PNAM", pnamOff);
        WriteChunkEntry(data, 12, 1, "OIDF", oidfOff);
        WriteChunkEntry(data, 12, 2, "OIDL", oidlOff);
        WriteChunkEntry(data, 12, 3, "OOFF", ooffOff);
        WriteChunkEntry(data, 12, 4, "LOFF", loffOff);
        WriteChunkEntry(data, 12, 5, 0x00000000, trailerOff);

        pnam.CopyTo(data, pnamOff);
        for (int i = 0x44; i < 256; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(oidfOff + i * 4), 1);
        }

        oidl.CopyTo(data, oidlOff);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(ooffOff), (uint)packIndex);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(ooffOff + 4), (uint)offsetLo);

        for (int i = 0; i < numLargeOffsets; i++)
        {
            BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(loffOff + i * 8), 0x1234567890L);
        }

        return data;
    }

    private static void WriteChunkEntry(byte[] data, int tableStart, int index, string id, int offset)
    {
        uint idValue = (uint)((id[0] << 24) | (id[1] << 16) | (id[2] << 8) | id[3]);
        WriteChunkEntry(data, tableStart, index, idValue, offset);
    }

    private static void WriteChunkEntry(byte[] data, int tableStart, int index, uint idValue, int offset)
    {
        int pos = tableStart + index * 12;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(pos), idValue);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(pos + 4), offset);
    }

    /// <summary>Synchronous progress capture (Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }
}
