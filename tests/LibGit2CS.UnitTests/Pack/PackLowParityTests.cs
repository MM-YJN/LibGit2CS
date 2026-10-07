using System.Buffers.Binary;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

// Parity tests for hsize, 10-byte varint, delta-error mapping, the MIDX chunk-offset guard, empty-LOFF, and indexer OFS base guards.
// Expectations are C-verified against libgit2 1.9.4 (delta.c:171-174, 480-483, 630-631; midx.c:222-223, 148-162; pack.c:992-999).
public sealed class PackLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public PackLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── 10-byte delta-header varint is accepted ────────────────────────

    [Fact]
    public void DeltaHeader_10ByteVarint_IsAccepted()
    {
        // C (delta.c:480-483): the guard is shift >= sizeof(size_t)*8 (64),
        // so the 10th byte (shift == 63) is processed; only an 11th byte
        // errors. C-verified: base = 0x8000000000000000 on
        // 80 80 80 80 80 80 80 80 80 01.
        byte[] delta = [
            0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01, // base size
            0x00,                                                       // result size
        ];

        (long baseSize, long resultSize) = GitDeltaApplier.ReadHeader(delta);

        Assert.Equal(unchecked((long)0x8000000000000000), baseSize);
        Assert.Equal(0, resultSize);
    }

    [Fact]
    public void DeltaHeader_11ByteVarint_ThrowsOverflow()
    {
        // An 11th byte (shift >= 70) errors.
        byte[] delta = [
            0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01,
        ];

        GitException ex = Assert.Throws<GitException>(() => GitDeltaApplier.ReadHeader(delta));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("delta header overflow", ex.Message);
    }

    // ── delta failures map to (-1, GIT_ERROR_INVALID) ─────────────────

    [Fact]
    public void Apply_MalformedDelta_ThrowsErrorInvalid()
    {
        // C (delta.c:630-631): every git_delta_apply failure returns -1
        // (GIT_ERROR) with the GIT_ERROR_INVALID class.
        GitException ex = Assert.Throws<GitException>(() => GitDeltaApplier.Apply([1], [1, 0, 0]));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Contains("failed to apply delta", ex.Message);
    }

    // ── small sources use a 16-bucket table ────────────────────────────

    [Fact]
    public void BuildIndex_SmallSource_Uses16Buckets()
    {
        // C (delta.c:171-174): hsize = entries/4, then the smallest i >= 4
        // with (1u << i) >= hsize — for a 100-byte source (entries = 6,
        // hsize = 1) the table has 16 buckets (HashMask 15), not 32
        // (HashMask 31).
        byte[] source = new byte[100];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)i;
        }

        DeltaEncoder.DeltaIndex? index = DeltaEncoder.BuildIndex(source);

        Assert.NotNull(index);
        Assert.Equal(15u, index!.HashMask);
    }

    // ── MIDX chunk-offset high word >= INT32_MAX is rejected ───────────

    [Fact]
    public async Task Midx_HighChunkOffsetWord_ThrowsOutOfRange()
    {
        // C (midx.c:222-223): high_offset >= INT32_MAX → "chunk offset out
        // of range".
        using var ms = new MemoryStream();
        WriteBE32(ms, 0x4d494458u); // "MIDX"
        ms.WriteByte(1);            // version
        ms.WriteByte(1);            // oid version
        ms.WriteByte(2);            // 2 chunks
        ms.WriteByte(0);            // base midx
        WriteBE32(ms, 0);           // packfile count
        WriteChunk(ms, 0x504e414du, 12 + (1 + 2) * 12); // PNAM at chunk-table end
        WriteBE32(ms, 0x4f494446u); // chunk id "OIDF"
        WriteBE32(ms, 0x80000000u); // high word >= INT32_MAX
        WriteBE32(ms, 0);           // low word
        ms.Write(new byte[100]);    // PNAM data (padding)
        ms.Write(new byte[20]);     // trailer

        await AssertMidxThrowsAsync(ms.ToArray(), "chunk offset out of range");
    }

    // ── an empty LOFF chunk means "no large-offset table" ──────────────

    [Fact]
    public async Task Midx_EmptyLoff_ReturnsRawOffset()
    {
        // C (midx.c:148-162, 434-446): object_large_offsets stays NULL for an
        // empty LOFF chunk, so a flagged entry returns the raw 32-bit value
        // with no error.
        var oid = GitOid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa".AsSpan(), GitHashAlgorithmKind.Sha1);
        byte[] midx = BuildMidxWithEmptyLoff(oid);
        string packDir = Path.Combine(_tempDir, "midx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await File.WriteAllBytesAsync(Path.Combine(packDir, "multi-pack-index"), midx, cancellationToken: TestContext.Current.CancellationToken);

        MultiPackIndex? index = await MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        MultiPackIndexEntry? entry = index!.FindEntry(oid);

        Assert.NotNull(entry);
        // Raw 32-bit value with the high bit still set (a C quirk).
        Assert.Equal(0x80000000L, entry!.Value.Offset);
        Assert.Equal(0, entry!.Value.PackIndex);
    }

    // ── indexer OFS_DELTA base overflow guard ─────────────────────────

    [Fact]
    public async Task Indexer_OfsDeltaBaseOverflow_Throws()
    {
        // C (pack.c:992-994): a base-offset varint reaching MSB(x, 7) fails
        // "invalid pack file - overflow";
        // fail later as an unresolvable delta.
        using var ms = new MemoryStream();
        ms.Write("PACK"u8);
        WriteBE32(ms, 2);  // version
        WriteBE32(ms, 1);  // 1 object
        ms.WriteByte(0x60); // OFS_DELTA (type 6), size 0, no continuation
        for (int i = 0; i < 12; i++)
        {
            ms.WriteByte(0x80); // base-offset varint continuation bytes → overflow
        }

        string packDir = Path.Combine(_tempDir, "ofs_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.AppendAsync(ms.ToArray(), stats, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Contains("invalid pack file - overflow", ex.Message);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task AssertMidxThrowsAsync(byte[] midx, string messagePart)
    {
        string packDir = Path.Combine(_tempDir, "midx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await File.WriteAllBytesAsync(Path.Combine(packDir, "multi-pack-index"), midx, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains(messagePart, ex.Message);
    }

    /// <summary>Builds a minimal MIDX with PNAM/OIDF/OIDL/OOFF + an EMPTY LOFF chunk.</summary>
    private static byte[] BuildMidxWithEmptyLoff(GitOid oid)
    {
        byte[] pnam = "a.idx\0"u8.ToArray();           // 6 bytes
        byte[] oidf = new byte[256 * 4];               // cumulative fanout: 1 object at 'a'
        for (int i = 0; i < 256; i++)
        {
            WriteBE32Into(oidf, i * 4, i >= 0xAA ? 1u : 0u);
        }

        byte[] oidl = oid.RawBytes.ToArray();          // 20 bytes
        byte[] ooff = new byte[8];                     // packIndex 0 + offset 0x80000000
        WriteBE32Into(ooff, 0, 0);
        WriteBE32Into(ooff, 4, 0x80000000u);
        byte[] loff = [];                              // EMPTY LOFF chunk

        int chunkHdrEnd = 12 + (1 + 6) * 12;           // header + 6 chunk headers
        int pnamOff = chunkHdrEnd;
        int oidfOff = pnamOff + pnam.Length;
        int oidlOff = oidfOff + oidf.Length;
        int ooffOff = oidlOff + oidl.Length;
        int loffOff = ooffOff + ooff.Length;
        int dummyLen = 20;

        using var ms = new MemoryStream();
        WriteBE32(ms, 0x4d494458u); // "MIDX"
        ms.WriteByte(1);
        ms.WriteByte(1);
        ms.WriteByte(6);            // 6 chunks
        ms.WriteByte(0);
        WriteBE32(ms, 1);           // 1 packfile

        WriteChunk(ms, 0x504e414du, pnamOff); // PNAM
        WriteChunk(ms, 0x4f494446u, oidfOff); // OIDF
        WriteChunk(ms, 0x4f49444cu, oidlOff); // OIDL
        WriteChunk(ms, 0x4f4f4646u, ooffOff); // OOFF
        WriteChunk(ms, 0x4c4f4646u, loffOff); // LOFF (empty: same offset as the next chunk)
        WriteChunk(ms, 0x58585858u, loffOff); // unknown trailing chunk at the same offset
        ms.Write(new byte[12]);     // the sentinel zero-chunk slot

        ms.Write(pnam);
        ms.Write(oidf);
        ms.Write(oidl);
        ms.Write(ooff);
        ms.Write(loff);
        ms.Write(new byte[dummyLen]); // unknown-chunk data
        ms.Write(new byte[20]);     // trailer
        return ms.ToArray();
    }

    private static void WriteChunk(MemoryStream ms, uint id, int offset)
    {
        WriteBE32(ms, id);
        WriteBE32(ms, (uint)(offset >> 32));
        WriteBE32(ms, (uint)offset);
    }

    private static void WriteBE32(MemoryStream ms, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteBE32Into(byte[] target, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset, 4), value);
    }
}
