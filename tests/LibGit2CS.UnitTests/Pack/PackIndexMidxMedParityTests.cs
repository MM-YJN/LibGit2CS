using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

/// <summary>
/// Regression tests for the pack index and indexer parity behaviors in
/// libgit2 1.9.4. Expectations
/// are C-verified against libgit2 1.9.4 (indexer.c, pack.c, midx.c).
/// </summary>
public sealed class PackIndexMidxMedParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public PackIndexMidxMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackIndexMidx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------
    // the indexer must reject duplicate OIDs ("duplicate object %s
    // found in pack", indexer.c:524-534, GIT_ERROR_INDEXER).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Indexer_DuplicateOid_Throws()
    {
        string packDir = Path.Combine(_tempDir, "dup");
        Directory.CreateDirectory(packDir);

        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, "hello"u8.ToArray());
        builder.AddFullObject(GitObjectType.Blob, "hello"u8.ToArray()); // same OID again

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.AppendAsync(builder.Build(), stats, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Indexer, ex.Category);
        Assert.Contains("duplicate object", ex.Message);
    }

    // ---------------------------------------------------------------
    // pack open must validate the pack trailer against the idx's
    // embedded pack checksum (pack.c:1135-1144).
    // ---------------------------------------------------------------

    [Fact]
    public async Task PackFile_Open_TrailerMismatch_Throws()
    {
        string packDir = Path.Combine(_tempDir, "trailer");
        Directory.CreateDirectory(packDir);

        byte[] body = "hello"u8.ToArray();
        byte[] packBytes = BuildPackBytes([0x30, .. ZlibTestHelpers.CompressLooseObject(body)]); // blob size 5
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);
        string packPath = WritePack(packDir, "pack-mismatch", packBytes, oid, 12, GitHashAlgorithmKind.Sha1);

        // Corrupt the pack's trailer (last byte) — the idx still embeds the
        // original checksum, so the open must fail.
#pragma warning disable CA1849
        File.WriteAllBytes(packPath, [.. packBytes[..^1], (byte)(packBytes[^1] ^ 0xFF)]);
#pragma warning restore CA1849

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
    }

    // ---------------------------------------------------------------
    // indexer commit requires the pack to be exactly
    // <objects><oid_size trailer> (indexer.c:1254-1261): trailing bytes
    // → "unexpected data at the end of the pack"; missing trailer →
    // "missing trailer at the end of the pack".
    // ---------------------------------------------------------------

    [Fact]
    public async Task Indexer_Commit_TrailingBytes_Throws()
    {
        string packDir = Path.Combine(_tempDir, "trail-extra");
        Directory.CreateDirectory(packDir);

        byte[] body = "hello"u8.ToArray();
        byte[] packBytes = BuildPackBytes([0x30, .. ZlibTestHelpers.CompressLooseObject(body)]);

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        byte[] withExtra = [.. packBytes, .. (byte[])"EXTRA"u8.ToArray()];
        Assert.True(await indexer.AppendAsync(withExtra, stats, TestContext.Current.CancellationToken));

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.CommitAsync(stats, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Indexer, ex.Category);
        Assert.Contains("unexpected data at the end of the pack", ex.Message);
    }

    [Fact]
    public async Task Indexer_Commit_MissingTrailer_Throws()
    {
        string packDir = Path.Combine(_tempDir, "trail-missing");
        Directory.CreateDirectory(packDir);

        byte[] body = "hello"u8.ToArray();
        byte[] packBytes = [.. BuildPackHeader(1), 0x30, .. ZlibTestHelpers.CompressLooseObject(body)]; // no trailer

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        Assert.True(await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken));

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.CommitAsync(stats, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Indexer, ex.Category);
        Assert.Contains("missing trailer at the end of the pack", ex.Message);
    }

    // ---------------------------------------------------------------
    // exact error codes — C returns generic -1 (GIT_ERROR) for
    // indexer failures.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Indexer_BadMagic_ThrowsError()
    {
        string packDir = Path.Combine(_tempDir, "magic");
        Directory.CreateDirectory(packDir);

        byte[] badHeader = [0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 1];
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.AppendAsync(badHeader, stats, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task Indexer_Commit_TrailerMismatch_ThrowsError()
    {
        string packDir = Path.Combine(_tempDir, "trailer-mismatch");
        Directory.CreateDirectory(packDir);

        byte[] body = "hello"u8.ToArray();
        byte[] packBytes = BuildPackBytes([0x30, .. ZlibTestHelpers.CompressLooseObject(body)]);
        // Overwrite the trailer with a wrong hash (zeros).
        Array.Clear(packBytes, packBytes.Length - 20, 20);

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        Assert.True(await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken));

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.CommitAsync(stats, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("trailer", ex.Message);
    }

    [Fact]
    public async Task Indexer_UnresolvableDeltas_ThrowsError()
    {
        string packDir = Path.Combine(_tempDir, "unresolvable");
        Directory.CreateDirectory(packDir);

        // Thin pack whose REF_DELTA base is nowhere — not in the pack and not
        // in the (empty) ODB.
        var baseOid = GitOid.Parse("1111111111111111111111111111111111111111".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] delta = ThinPackBuilder.MakeCopyAppendDelta("base"u8.ToArray(), " appended"u8.ToArray());
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddRefDelta(baseOid, delta);
        byte[] packBytes = builder.Build();

        using var odbContext = new GitContext();
        await using var odb = new GitObjectDb(odbContext);
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1, odb);
        var stats = new GitIndexerProgress();
        Assert.True(await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken));

        // C: "missing delta bases" (indexer.c:1010) / fix_thin_pack failures are GIT_ERROR_INDEXER.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.CommitAsync(stats, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Indexer, ex.Category);
    }

    // ---------------------------------------------------------------
    // MIDX OIDF fanout monotonicity (midx.c:101-107, "index is
    // non-monotonic").
    // ---------------------------------------------------------------

    [Fact]
    public async Task Midx_NonMonotonicFanout_Throws()
    {
        byte[] midx = await BuildValidMidxAsync();

        // Set fanout[0] to a huge value — fanout[1] (0) is then smaller.
        int oidfOff = FindChunk(midx, 0x4F494446); // "OIDF"
        BinaryPrimitives.WriteUInt32BigEndian(midx.AsSpan(oidfOff), 0xFFFFFFFF);

        await AssertThrowsMidxErrorAsync(midx, "non-monotonic");
    }

    // ---------------------------------------------------------------
    // MIDX PNAM chunk validation (midx.c:52-85).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Midx_EmptyPackfileName_Throws()
    {
        byte[] midx = await BuildValidMidxAsync();

        // NUL out the first byte of the first name.
        int pnamOff = FindChunk(midx, 0x504E414D); // "PNAM"
        midx[pnamOff] = 0;

        await AssertThrowsMidxErrorAsync(midx, "empty packfile name");
    }

    [Fact]
    public async Task Midx_UnsortedPackfileNames_Throws()
    {
        byte[] midx = await BuildValidMidxAsync();

        // Make the second name sort BEFORE the first ('a' < 'p').
        int pnamOff = FindChunk(midx, 0x504E414D); // "PNAM"
        int firstLen = 0;
        while (midx[pnamOff + firstLen] != 0)
        {
            firstLen++;
        }

        midx[pnamOff + firstLen + 1] = (byte)'a';

        await AssertThrowsMidxErrorAsync(midx, "not sorted");
    }

    // ---------------------------------------------------------------
    // MIDX entry lookup must bounds-check the pack index against the
    // packfile-names table (midx.c:447-449).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Midx_FindEntry_PackIndexOutOfRange_FallsBackToPerPack()
    {
        byte[] midx = await BuildValidMidxAsync();
        string packDir = Path.Combine(_tempDir, "midx-oob");
        Directory.CreateDirectory(packDir);
        string midxPath = Path.Combine(packDir, "multi-pack-index");
#pragma warning disable CA1849
        File.WriteAllBytes(midxPath, midx);
#pragma warning restore CA1849

        // Read the first OIDL entry to have a real OID to look up.
        int oidlOff = FindChunk(midx, 0x4F49444C); // "OIDL"
        byte[] firstOid = midx[oidlOff..(oidlOff + 20)];
        var oid = GitOid.FromRaw(firstOid, GitHashAlgorithmKind.Sha1);

        // Corrupt the first OOFF record's pack index to be out of range.
        int ooffOff = FindChunk(midx, 0x4F4F4646); // "OOFF"
        BinaryPrimitives.WriteUInt32BigEndian(midx.AsSpan(ooffOff), 0xFFFFFFFF);
        await File.WriteAllBytesAsync(midxPath, midx, TestContext.Current.CancellationToken);

        MultiPackIndex? idx = await MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(idx);

        // C: midx.c:447-449 errors fall through to the per-pack.idx search (odb_pack.c:278-296) — FindEntry must report not-found, not throw hard.
        Assert.Null(idx!.FindEntry(oid));
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>Builds a valid MIDX over the testrepo fixture packs.</summary>
    private async Task<byte[]> BuildValidMidxAsync()
    {
        string extractedPath = FixtureLoader.ExtractTreeToTemp("Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(extractedPath);
        string packDir = Path.Combine(extractedPath, "testrepo.git", "objects", "pack");

        // Remove the pre-existing MIDX so the writer creates a fresh one.
        string existing = Path.Combine(packDir, "multi-pack-index");
        if (File.Exists(existing))
        {
            File.Delete(existing);
        }

        using var writer = new MultiPackIndexWriter(packDir, GitHashAlgorithmKind.Sha1);
        foreach (string idx in Directory.EnumerateFiles(packDir, "*.idx"))
        {
            await writer.AddAsync(idx, TestContext.Current.CancellationToken);
        }

        return writer.Dump();
    }

    private static int FindChunk(byte[] midx, uint chunkId)
    {
        byte numChunks = midx[6];
        int off = 12;
        for (int i = 0; i < numChunks; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32BigEndian(midx.AsSpan(off));
            if (id == chunkId)
            {
                uint high = BinaryPrimitives.ReadUInt32BigEndian(midx.AsSpan(off + 4));
                uint low = BinaryPrimitives.ReadUInt32BigEndian(midx.AsSpan(off + 8));
                return (int)(((long)high << 32) | low);
            }

            off += 12;
        }

        throw new InvalidOperationException($"chunk {chunkId} not found");
    }

    private static async Task AssertThrowsMidxErrorAsync(byte[] midx, string messagePart)
    {
        string packDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MidxCorrupt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        try
        {
            string midxPath = Path.Combine(packDir, "multi-pack-index");
#pragma warning disable CA1849
            File.WriteAllBytes(midxPath, midx);
#pragma warning restore CA1849

            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
            // C (midx.c:46-50): midx_error returns -1 (GIT_ERROR) —.
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Contains(messagePart, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(packDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] BuildPackHeader(int objectCount)
    {
        byte[] header = new byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)objectCount);
        return header;
    }

    /// <summary>PACK header + entries + SHA trailer.</summary>
    private static byte[] BuildPackBytes(byte[] entries)
    {
        byte[] body = [.. BuildPackHeader(1), .. entries];
        using var hash = GitIncrementalHash.Create(GitHashAlgorithmKind.Sha1);
        hash.AppendData(body);
        byte[] trailer = hash.Finalize().RawBytes.ToArray();
        return [.. body, .. trailer];
    }

    /// <summary>
    /// Writes a .pack/.idx pair whose single entry claims <paramref name="oid"/>
    /// at <paramref name="offset"/> (crc over the entry).
    /// </summary>
    private static string WritePack(
        string packDir,
        string namePrefix,
        byte[] packBytes,
        GitOid oid,
        long offset,
        GitHashAlgorithmKind algorithm)
    {
        int oidSize = GitOid.SizeFor(algorithm);
        int entryLen = (int)(packBytes.Length - 12 - oidSize);
        uint crc = entryLen > 0 ? Crc32.HashToUInt32(packBytes.AsSpan(12, entryLen)) : 0;

        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(algorithm);

        Span<byte> magic = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(magic[0..4], 0xff744f63);
        BinaryPrimitives.WriteUInt32BigEndian(magic[4..8], 2);
        ms.Write(magic);
        hash.AppendData(magic);

        byte firstByte = oid.RawBytes[0];
        Span<byte> fanout = stackalloc byte[4];
        for (int i = 0; i < 256; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(fanout, (uint)(i >= firstByte ? 1 : 0));
            ms.Write(fanout);
            hash.AppendData(fanout);
        }

        ms.Write(oid.RawBytes);
        hash.AppendData(oid.RawBytes);

        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        ms.Write(crcBuf);
        hash.AppendData(crcBuf);

        Span<byte> offBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(offBuf, (uint)offset);
        ms.Write(offBuf);
        hash.AppendData(offBuf);

        byte[] packChecksum = packBytes[^oidSize..];
        ms.Write(packChecksum);
        hash.AppendData(packChecksum);

        GitOid idxChecksum = hash.Finalize();
        ms.Write(idxChecksum.RawBytes);

        string packPath = Path.Combine(packDir, $"{namePrefix}-{Guid.NewGuid().ToString("N")[..8]}.pack");
        string idxPath = Path.ChangeExtension(packPath, ".idx");
#pragma warning disable CA1849
        File.WriteAllBytes(packPath, packBytes);
        File.WriteAllBytes(idxPath, ms.ToArray());
#pragma warning restore CA1849
        return packPath;
    }
}
