using System.Buffers.Binary;
using System.IO.Hashing;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

/// <summary> Regression tests for <see cref="PackFile.ReadHeaderAsync"/> must return the delta's RESULT size (as C's <c>git_packfile_resolve_header</c> does
/// via <c>git_delta_read_header_fromstream</c>), not the base object's size. </summary> <remarks> Verified end-to-end first-party: a depth-1 delta
/// blob reported the 8049-byte base size where C reports the 7992-byte result size. The reported size feeds <c>git_odb_read_header</c> equivalents for every
/// delta-compressed pack object. </remarks>
public sealed class PackHeaderDeltaSizeParityTests : IDisposable
{
    private readonly string _tempDir;

    public PackHeaderDeltaSizeParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_HeaderDelta_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task ReadHeader_OfsDelta_ReturnsDeltaResultSize()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        byte[] baseBody = "the quick brown fox\n"u8.ToArray();
        byte[] suffix = "jumps over the lazy dog\n"u8.ToArray();
        byte[] resultBody = [.. baseBody, .. suffix];
        Assert.NotEqual(baseBody.Length, resultBody.Length);

        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        byte[] delta = ThinPackBuilder.MakeCopyAppendDelta(baseBody, suffix);
        string packDir = Path.Combine(_tempDir, "ofs");
        Directory.CreateDirectory(packDir);
        WritePackWithBaseAndOfsDelta(packDir, "pack-ofs", baseBody, baseOid, delta, resultOid);

        using PackFile pack = await PackFile.OpenAsync(
            Directory.GetFiles(packDir, "*.pack")[0], GitHashAlgorithmKind.Sha1, ct);

        GitObjectHeader? header = await pack.ReadHeaderAsync(resultOid, ct);
        Assert.NotNull(header);
        // C's resolve_header: delta result size, not the base's size.
        Assert.Equal(resultBody.Length, header!.Value.Size);
        Assert.Equal(GitObjectType.Blob, header.Value.Type);
    }

    [Fact]
    public async Task ReadHeader_RefDelta_SamePack_ReturnsDeltaResultSize()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        byte[] baseBody = "base content line one\n"u8.ToArray();
        byte[] suffix = "extra tail\n"u8.ToArray();
        byte[] resultBody = [.. baseBody, .. suffix];
        Assert.NotEqual(baseBody.Length, resultBody.Length);

        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        byte[] delta = ThinPackBuilder.MakeCopyAppendDelta(baseBody, suffix);
        string packDir = Path.Combine(_tempDir, "ref");
        Directory.CreateDirectory(packDir);
        WritePackWithBaseAndRefDelta(packDir, "pack-ref", baseBody, baseOid, delta, resultOid);

        using PackFile pack = await PackFile.OpenAsync(
            Directory.GetFiles(packDir, "*.pack")[0], GitHashAlgorithmKind.Sha1, ct);

        GitObjectHeader? header = await pack.ReadHeaderAsync(resultOid, ct);
        Assert.NotNull(header);
        Assert.Equal(resultBody.Length, header!.Value.Size);
        Assert.Equal(GitObjectType.Blob, header.Value.Type);

        // The base's own header is unaffected.
        GitObjectHeader? baseHeader = await pack.ReadHeaderAsync(baseOid, ct);
        Assert.NotNull(baseHeader);
        Assert.Equal(baseBody.Length, baseHeader!.Value.Size);
    }

    [Fact]
    public async Task ReadHeader_OfsDelta_DeltaChain_ReturnsTopLevelResultSize()
    {
        // Base -> delta1 (base+suffix1) -> delta2 (delta1+suffix2): the header
        // of the top-level entry must report delta2's result size (C walks the
        // chain only to resolve the type; the size comes from the top delta).
        CancellationToken ct = TestContext.Current.CancellationToken;

        byte[] baseBody = "root\n"u8.ToArray();
        byte[] midBody = [.. baseBody, .. "middle\n"u8.ToArray()];
        byte[] resultBody = [.. midBody, .. "final\n"u8.ToArray()];
        Assert.NotEqual(baseBody.Length, resultBody.Length);

        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
        GitOid midOid = GitObjectDb.HashObject(GitObjectType.Blob, midBody, GitHashAlgorithmKind.Sha1);
        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        byte[] delta1 = ThinPackBuilder.MakeCopyAppendDelta(baseBody, "middle\n"u8);
        byte[] delta2 = ThinPackBuilder.MakeCopyAppendDelta(midBody, "final\n"u8);

        string packDir = Path.Combine(_tempDir, "chain");
        Directory.CreateDirectory(packDir);
        WritePackWithOfsDeltaChain(packDir, "pack-chain", baseBody, baseOid, delta1, midOid, delta2, resultOid);

        using PackFile pack = await PackFile.OpenAsync(
            Directory.GetFiles(packDir, "*.pack")[0], GitHashAlgorithmKind.Sha1, ct);

        GitObjectHeader? header = await pack.ReadHeaderAsync(resultOid, ct);
        Assert.NotNull(header);
        Assert.Equal(resultBody.Length, header!.Value.Size);
        Assert.Equal(GitObjectType.Blob, header.Value.Type);

        GitObjectHeader? midHeader = await pack.ReadHeaderAsync(midOid, ct);
        Assert.NotNull(midHeader);
        Assert.Equal(midBody.Length, midHeader!.Value.Size);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes a pack with a full base blob followed by an OFS_DELTA entry whose
    /// delta produces <paramref name="resultOid"/>.
    /// </summary>
    private static void WritePackWithBaseAndOfsDelta(
        string packDir,
        string namePrefix,
        byte[] baseBody,
        GitOid baseOid,
        byte[] deltaData,
        GitOid resultOid)
    {
        int oidSize = GitOid.SizeFor(GitHashAlgorithmKind.Sha1);
        byte[] compressedDelta = ZlibTestHelpers.CompressLooseObject(deltaData);

        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(GitHashAlgorithmKind.Sha1);

        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(buf[0..4], 0x5041434b); // "PACK"
        BinaryPrimitives.WriteUInt32BigEndian(buf[4..8], 2);
        BinaryPrimitives.WriteUInt32BigEndian(buf[8..12], 2);          // 2 objects
        ms.Write(buf[..12]);
        hash.AppendData(buf[..12]);

        long baseOffset = 12;

        // Entry 1: full base blob.
        int hdrLen = PackEncoding.WriteObjectHeader(buf, GitObjectType.Blob, baseBody.Length);
        ms.Write(buf[..hdrLen]);
        hash.AppendData(buf[..hdrLen]);
        ms.Write(baseBody);
        hash.AppendData(baseBody);

        // Entry 2: OFS_DELTA. Base offset distance (0-based byte distance).
        long deltaOffset = ms.Length;
        long distance = deltaOffset - baseOffset;
        Assert.InRange(distance, 1, 126); // single-byte offset varint

        hdrLen = PackEncoding.WriteObjectHeader(buf, GitObjectType.OfsDelta, deltaData.Length);
        ms.Write(buf[..hdrLen]);
        hash.AppendData(buf[..hdrLen]);

        // OFS_DELTA base offset varint: distance (single byte, no continuation).
        byte[] distanceBytes = [(byte)distance];
        ms.WriteByte(distanceBytes[0]);
        hash.AppendData(distanceBytes);

        ms.Write(compressedDelta);
        hash.AppendData(compressedDelta);

        GitOid trailer = hash.Finalize();
        ms.Write(trailer.RawBytes.ToArray());

        byte[] packBytes = ms.ToArray();

        // CRCs.
        uint baseCrc = Crc32.HashToUInt32(packBytes.AsSpan((int)baseOffset, (int)(deltaOffset - baseOffset)));
        int deltaEntryLen = hdrLen + 1 + compressedDelta.Length;
        uint deltaCrc = Crc32.HashToUInt32(packBytes.AsSpan((int)deltaOffset, deltaEntryLen));
        var entries = new List<(GitOid Oid, long Offset, uint Crc)>
        {
            (baseOid, baseOffset, baseCrc),
            (resultOid, deltaOffset, deltaCrc),
        };

        WriteIdxAndPack(packDir, namePrefix, packBytes, entries, oidSize);
    }

    /// <summary>
    /// Writes a pack with a full base blob followed by an OFS_DELTA chain:
    /// entry 2 is OFS_DELTA(base → mid), entry 3 is OFS_DELTA(mid → result).
    /// </summary>
    private static void WritePackWithOfsDeltaChain(
        string packDir,
        string namePrefix,
        byte[] baseBody,
        GitOid baseOid,
        byte[] delta1,
        GitOid midOid,
        byte[] delta2,
        GitOid resultOid)
    {
        int oidSize = GitOid.SizeFor(GitHashAlgorithmKind.Sha1);
        byte[] compressedDelta1 = ZlibTestHelpers.CompressLooseObject(delta1);
        byte[] compressedDelta2 = ZlibTestHelpers.CompressLooseObject(delta2);

        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(GitHashAlgorithmKind.Sha1);

        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(buf[0..4], 0x5041434b);
        BinaryPrimitives.WriteUInt32BigEndian(buf[4..8], 2);
        BinaryPrimitives.WriteUInt32BigEndian(buf[8..12], 3);          // 3 objects
        ms.Write(buf[..12]);
        hash.AppendData(buf[..12]);

        long baseOffset = 12;

        int hdrLen = PackEncoding.WriteObjectHeader(buf, GitObjectType.Blob, baseBody.Length);
        ms.Write(buf[..hdrLen]);
        hash.AppendData(buf[..hdrLen]);
        ms.Write(baseBody);
        hash.AppendData(baseBody);

        long midOffset = ms.Length;
        long distance1 = midOffset - baseOffset;
        Assert.InRange(distance1, 1, 126);
        hdrLen = PackEncoding.WriteObjectHeader(buf, GitObjectType.OfsDelta, delta1.Length);
        ms.Write(buf[..hdrLen]);
        hash.AppendData(buf[..hdrLen]);
        byte[] distance1Bytes = [(byte)distance1];
        ms.WriteByte(distance1Bytes[0]);
        hash.AppendData(distance1Bytes);
        ms.Write(compressedDelta1);
        hash.AppendData(compressedDelta1);

        long resultOffset = ms.Length;
        long distance2 = resultOffset - midOffset;
        Assert.InRange(distance2, 1, 126);
        hdrLen = PackEncoding.WriteObjectHeader(buf, GitObjectType.OfsDelta, delta2.Length);
        ms.Write(buf[..hdrLen]);
        hash.AppendData(buf[..hdrLen]);
        byte[] distance2Bytes = [(byte)distance2];
        ms.WriteByte(distance2Bytes[0]);
        hash.AppendData(distance2Bytes);
        ms.Write(compressedDelta2);
        hash.AppendData(compressedDelta2);

        GitOid trailer = hash.Finalize();
        ms.Write(trailer.RawBytes.ToArray());

        byte[] packBytes = ms.ToArray();

        uint baseCrc = Crc32.HashToUInt32(packBytes.AsSpan((int)baseOffset, (int)(midOffset - baseOffset)));
        int delta1EntryLen = hdrLen + 1 + compressedDelta1.Length;
        uint midCrc = Crc32.HashToUInt32(packBytes.AsSpan((int)midOffset, delta1EntryLen));
        int delta2EntryLen = hdrLen + 1 + compressedDelta2.Length;
        uint resultCrc = Crc32.HashToUInt32(packBytes.AsSpan((int)resultOffset, delta2EntryLen));
        var entries = new List<(GitOid Oid, long Offset, uint Crc)>
        {
            (baseOid, baseOffset, baseCrc),
            (midOid, midOffset, midCrc),
            (resultOid, resultOffset, resultCrc),
        };

        WriteIdxAndPack(packDir, namePrefix, packBytes, entries, oidSize);
    }

    /// <summary>
    /// Writes a pack with a full base blob followed by a REF_DELTA entry.
    /// </summary>
    private static void WritePackWithBaseAndRefDelta(
        string packDir,
        string namePrefix,
        byte[] baseBody,
        GitOid baseOid,
        byte[] deltaData,
        GitOid resultOid)
    {
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, baseBody);
        builder.AddRefDelta(baseOid, deltaData);
        byte[] packBytes = builder.Build();
        int oidSize = GitOid.SizeFor(GitHashAlgorithmKind.Sha1);

        long offset = 12;
        Span<byte> hdrBuf = stackalloc byte[16];
        byte[] compressedBase = ZlibTestHelpers.CompressLooseObject(baseBody);
        int baseHdrLen = PackEncoding.WriteObjectHeader(hdrBuf, GitObjectType.Blob, baseBody.Length);
        uint baseCrc = Crc32.HashToUInt32(packBytes.AsSpan((int)offset, baseHdrLen + compressedBase.Length));
        offset += baseHdrLen + compressedBase.Length;

        byte[] compressedDelta = ZlibTestHelpers.CompressLooseObject(deltaData);
        int deltaHdrLen = PackEncoding.WriteObjectHeader(hdrBuf, GitObjectType.RefDelta, deltaData.Length);
        uint deltaCrc = Crc32.HashToUInt32(packBytes.AsSpan((int)offset, deltaHdrLen + oidSize + compressedDelta.Length));

        var entries = new List<(GitOid Oid, long Offset, uint Crc)>
        {
            (baseOid, 12, baseCrc),
            (resultOid, offset, deltaCrc),
        };

        WriteIdxAndPack(packDir, namePrefix, packBytes, entries, oidSize);
    }

    /// <summary>
    /// Writes the .idx v2 file and the .pack file to disk.
    /// </summary>
    private static void WriteIdxAndPack(
        string packDir,
        string namePrefix,
        byte[] packBytes,
        IReadOnlyList<(GitOid Oid, long Offset, uint Crc)> entries,
        int oidSize)
    {
        List<(GitOid Oid, long Offset, uint Crc)> sorted = [.. entries.OrderBy(e => e.Oid, GitOidRawComparer.s_instance)];

        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(GitHashAlgorithmKind.Sha1);

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

        byte[] idxBytes = ms.ToArray();

        string packPath = Path.Combine(packDir, $"{namePrefix}-{Guid.NewGuid().ToString("N")[..8]}.pack");
        string idxPath = Path.ChangeExtension(packPath, ".idx");
#pragma warning disable CA1849
        File.WriteAllBytes(packPath, packBytes);
        File.WriteAllBytes(idxPath, idxBytes);
#pragma warning restore CA1849
    }

    private sealed class GitOidRawComparer : IComparer<GitOid>
    {
        internal static readonly GitOidRawComparer s_instance = new();

        public int Compare(GitOid x, GitOid y)
        {
            ReadOnlySpan<byte> xb = x.RawBytes;
            ReadOnlySpan<byte> yb = y.RawBytes;
            return xb.SequenceCompareTo(yb);
        }
    }
}
