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
/// Regression tests for the pack core-format parity behaviors in
/// libgit2 1.9.4. Expectations are C-verified against libgit2 1.9.4
/// (delta.c, pack.c, indexer.c).
/// </summary>
public sealed class PackCoreFormatsMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public PackCoreFormatsMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackCore_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ---------------------------------------------------------------
    // copy-op bounds check misses 32-bit offset wraparound — the C#
    // int accumulation turns 0xFF000000 into a negative offset, the range
    // check passes, and Slice throws ArgumentOutOfRangeException. C
    // accumulates into a size_t and fails with "failed to apply delta"
    // (GIT_ERROR_INVALID).
    // ---------------------------------------------------------------

    [Fact]
    public void DeltaApplier_Apply_CopyOffsetHighBit_ThrowsGitException()
    {
        // Delta: base_size 5, result_size 0x10000 (65536), then a COPY op
        // (0x89 = copy + offset bytes 0 and 3) whose top offset byte has
        // bit 7 set → offset 0xFF000000.
        byte[] delta =
        [
            0x05,
            0x80, 0x80, 0x04, // 65536
            0x89, 0x00, 0xFF, // copy: off = 0xFF000000, len = 0x10000
        ];

        byte[] baseData = "hello"u8.ToArray();

        // C (delta.c:582-596): GIT_ADD_SIZET_OVERFLOW / base_len < end → fail
        // → "failed to apply delta". The C# must throw GitException, not
        // ArgumentOutOfRangeException from baseData.Slice.
        GitException ex = Assert.Throws<GitException>(() => GitDeltaApplier.Apply(baseData, delta));
        Assert.Contains("failed to apply delta", ex.Message);
    }

    // ---------------------------------------------------------------
    // the duplicate git_delta_apply port (DeltaEncoder.Apply) has no
    // varint overflow guard and no bounds checks on copy operands.
    // ---------------------------------------------------------------

    [Fact]
    public void DeltaEncoder_Apply_CopyOffsetHighBit_ThrowsGitException()
    {
        byte[] delta =
        [
            0x05,
            0x80, 0x80, 0x04, // 65536
            0x89, 0x00, 0xFF, // copy: off = 0xFF000000, len = 0x10000
        ];

        byte[] baseData = "hello"u8.ToArray();

        GitException ex = Assert.Throws<GitException>(() => DeltaEncoder.Apply(baseData, delta));
        Assert.Contains("failed to apply delta", ex.Message);
    }

    [Fact]
    public void DeltaEncoder_Apply_HeaderVarintOverflow_Throws()
    {
        // C (hdr_sz, delta.c:478-481): a continuation chain reaching
        // shift >= 64 fails with "delta header overflow". 11 continuation
        // bytes (shifts 0..70) trigger it.
        byte[] delta = new byte[13];
        Array.Fill(delta, (byte)0x80, 0, 11); // 11 continuation bytes
        delta[11] = 0x05;
        delta[12] = 0x00;

        GitException ex = Assert.Throws<GitException>(() => DeltaEncoder.Apply([1, 2, 3, 4, 5], delta));
        Assert.Contains("delta header overflow", ex.Message);
    }

    [Fact]
    public void DeltaEncoder_Apply_TruncatedCopyOperand_ThrowsGitException()
    {
        // COPY op claims offset byte 3 but the delta ends after byte 0.
        byte[] delta =
        [
            0x05,
            0x01,
            0x89, 0x00, // truncated: byte 3 missing
        ];

        byte[] baseData = "hello"u8.ToArray();

        // C (ADD_DELTA, delta.c:582): delta == delta_end → goto fail
        // → "failed to apply delta". The C# operand reads must be
        // bounds-checked (not IndexOutOfRangeException).
        GitException ex = Assert.Throws<GitException>(() => DeltaEncoder.Apply(baseData, delta));
        Assert.Contains("failed to apply delta", ex.Message);
    }

    // ---------------------------------------------------------------
    // pack object header decode missing the shift > 63 corruption
    // guard — C errors ("header length is zero" / "packfile corrupted",
    // GIT_ERROR_ODB), C# silently wraps the size.
    // ---------------------------------------------------------------

    [Fact]
    public async Task PackFile_Read_HeaderShiftOverflow_ThrowsOdbError()
    {
        string packDir = Path.Combine(_tempDir, "shift");
        Directory.CreateDirectory(packDir);

        // Object header: b0 = 0x90 (type 1, size 0, continuation) followed by
        // 9 continuation bytes; the 10th continuation check has shift 67 >= 64.
        byte[] header = [0x90, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01];
        byte[] body = ZlibTestHelpers.CompressLooseObject("hello"u8.ToArray());
        byte[] packBytes = BuildPackBytes([.. header, .. body]);

        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        string packPath = WritePack(packDir, "pack-shift", packBytes, oid, 12, GitHashAlgorithmKind.Sha1);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // C: packfile_unpack_header1 → "packfile corrupted" → surfaced as
        // "header length is zero" (pack.c:434-438, 484-486), GIT_ERROR_ODB.
        GitException ex = await Assert.ThrowsAsync<GitException>(() => pack.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Contains("header length is zero", ex.Message);
    }

    [Fact]
    public async Task Indexer_HeaderShiftOverflow_ThrowsOdbError()
    {
        string packDir = Path.Combine(_tempDir, "indexer-shift");
        Directory.CreateDirectory(packDir);

        byte[] header = [0x90, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01];
        byte[] body = ZlibTestHelpers.CompressLooseObject("hello"u8.ToArray());
        byte[] packBytes = BuildPackBytes([.. header, .. body]);

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
    }

    // ---------------------------------------------------------------
    // header decode at end of the pack window throws
    // ArgumentOutOfRangeException instead of GIT_EBUFS ("buffer too
    // small", GIT_ERROR_ODB).
    // ---------------------------------------------------------------

    [Fact]
    public async Task PackFile_Read_TruncatedHeader_ThrowsBufferTooShort()
    {
        string packDir = Path.Combine(_tempDir, "trunc");
        Directory.CreateDirectory(packDir);

        // Truncated pack: PACK header + b0 + 8 continuation bytes, then EOF
        // (no trailer). The 9th continuation read runs past the file end →
        // C: GIT_EBUFS "buffer too small". The idx's pack checksum is set to
        // the pack's tail bytes so the trailer↔idx validation passes and
        // the header decode is what hits EOF.
        byte[] packBytes = [.. BuildPackHeader(1), 0x90, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80];

        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        string packPath = WritePack(packDir, "pack-trunc", packBytes, oid, 12, GitHashAlgorithmKind.Sha1);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => pack.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.BufferTooShort, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Contains("buffer too small", ex.Message);
    }

    // ---------------------------------------------------------------
    // pack body decompress must validate the inflated length against
    // the header-declared size (pack.c:938-942, "error inflating zlib
    // stream", GIT_ERROR_ZLIB).
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(100)]
    [InlineData(4)]
    [InlineData(0)]
    public async Task PackFile_Read_BodyLengthMismatch_ThrowsZlib(int declaredSize)
    {
        string packDir = Path.Combine(_tempDir, "len");
        Directory.CreateDirectory(packDir);

        // Exercise underflow and overflow, including an empty output buffer.
        byte[] header = [(byte)(0xB0 | (declaredSize & 15)), (byte)(declaredSize >> 4)];
        byte[] body = ZlibTestHelpers.CompressLooseObject("short"u8.ToArray());
        byte[] packBytes = BuildPackBytes([.. header, .. body]);

        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "short"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        string packPath = WritePack(packDir, "pack-len", packBytes, oid, 12, GitHashAlgorithmKind.Sha1);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => pack.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Zlib, ex.Category);
        Assert.Contains("error inflating zlib stream", ex.Message);
    }

    // ---------------------------------------------------------------
    // the pack indexer must index objects whose compressed body
    // exceeds the 256 KB single-buffer window (C inflates incrementally).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Indexer_LargeObject_IndexesSuccessfully()
    {
        string packDir = Path.Combine(_tempDir, "large");
        Directory.CreateDirectory(packDir);

        // Incompressible 300 KB body → compressed size > 256 KB.
        byte[] bigBody = new byte[300_000];
        var rng = new Random(12345);
        rng.NextBytes(bigBody);

        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, bigBody);
        byte[] packBytes = builder.Build();
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, bigBody, GitHashAlgorithmKind.Sha1);

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        Assert.True(await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken));
        Assert.True(await indexer.CommitAsync(stats, TestContext.Current.CancellationToken));
        Assert.Equal(1, stats.IndexedObjects);

        using PackFile pack = await PackFile.OpenAsync(indexer.PackPath!, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        RawObjectData? raw = await pack.ReadAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(raw);
        Assert.Equal(bigBody, raw!.Value.Data);
    }

    // ---------------------------------------------------------------
    // Regression: the indexer's grow-and-retry window loop must reset the
    // pooled decompression writer between attempts. A failed (truncated)
    // attempt leaves partial output in the writer; without a reset the
    // retry appends the full stream after the partial prefix and the
    // non-delta branch hashes corrupted data → wrong OID in the .idx.
    // (The test above reads back through PackFile; this pins the
    // .idx OID itself, which is what the corruption poisoned.)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Indexer_LargeObject_IdxContainsCorrectOid()
    {
        string packDir = Path.Combine(_tempDir, "large-oid");
        Directory.CreateDirectory(packDir);

        // Incompressible 300 KB body → compressed size > 256 KB, forcing the
        // indexer's first 256 KB window to fail and retry with a grown window.
        byte[] bigBody = new byte[300_000];
        new Random(12345).NextBytes(bigBody);

        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, bigBody);
        byte[] packBytes = builder.Build();
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, bigBody, GitHashAlgorithmKind.Sha1);

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        Assert.True(await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken));
        Assert.True(await indexer.CommitAsync(stats, TestContext.Current.CancellationToken));

        string idxPath = Path.ChangeExtension(indexer.PackPath!, ".idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(1, idx.ObjectCount);
        Assert.True(idx.FindIndex(oid).Found, "large object indexed under the wrong OID (retry loop corrupted the decompressed body)");
    }

    // ---------------------------------------------------------------
    // Regression: the same grow-and-retry path for a REF_DELTA whose
    // compressed delta body exceeds the 256 KB window. The delta branch
    // discards the decompressed body (only the compressed length is used),
    // but the retry must still resolve the delta correctly.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Indexer_LargeCompressedDelta_ResolvesCorrectly()
    {
        string packDir = Path.Combine(_tempDir, "large-delta");
        Directory.CreateDirectory(packDir);

        // Insert-only delta with an incompressible 300 KB result body →
        // compressed delta size > 256 KB, forcing the retry path.
        byte[] baseBody = "base content\n"u8.ToArray();
        byte[] resultBody = new byte[300_000];
        new Random(12345).NextBytes(resultBody);
        byte[] deltaData = ThinPackBuilder.MakeInsertOnlyDelta(baseBody.Length, resultBody);

        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, baseBody);
        builder.AddRefDelta(baseOid, deltaData);
        byte[] packBytes = builder.Build();

        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        Assert.True(await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken));
        Assert.True(await indexer.CommitAsync(stats, TestContext.Current.CancellationToken));
        Assert.Equal(2, stats.IndexedObjects);

        string idxPath = Path.ChangeExtension(indexer.PackPath!, ".idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(2, idx.ObjectCount);
        Assert.True(idx.FindIndex(baseOid).Found, "base blob missing from idx");
        Assert.True(idx.FindIndex(resultOid).Found, "resolved delta missing from idx (retry path corrupted the delta body)");
    }

    // ---------------------------------------------------------------
    // git_packfile_resolve_header returns the RAW type nibble for
    // non-delta headers (no validation) — type 0 (EXT1) succeeds in the
    // header path; the full-unpack path still errors.
    // ---------------------------------------------------------------

    [Fact]
    public async Task PackFile_ReadHeader_InvalidType_ReturnsRawType()
    {
        string packDir = Path.Combine(_tempDir, "type");
        Directory.CreateDirectory(packDir);

        // Type nibble 0 (GIT_OBJECT__EXT1), size 5, no continuation.
        byte[] header = [0x05];
        byte[] body = ZlibTestHelpers.CompressLooseObject("hello"u8.ToArray());
        byte[] packBytes = BuildPackBytes([.. header, .. body]);

        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        string packPath = WritePack(packDir, "pack-type", packBytes, oid, 12, GitHashAlgorithmKind.Sha1);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // C (pack.c:548-554): the else branch returns *size_p = size with
        // success — no type validation in resolve_header.
        GitObjectHeader? headerResult = await pack.ReadHeaderAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(headerResult);
        Assert.Equal(GitObjectType.Ext1, headerResult!.Value.Type);
        Assert.Equal(5, headerResult.Value.Size);

        // The full-unpack path DOES validate (pack.c:743-745, "invalid
        // packfile type in header", GIT_ERROR_ODB).
        GitException ex = await Assert.ThrowsAsync<GitException>(() => pack.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
    }

    // ── Helpers ───────────────────────────────────────────────────────

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
        byte[] head = BuildPackHeader(1);
        byte[] body = [.. head, .. entries];
        using var hash = GitIncrementalHash.Create(GitHashAlgorithmKind.Sha1);
        hash.AppendData(body);
        byte[] trailer = hash.Finalize().RawBytes.ToArray();
        return [.. body, .. trailer];
    }

    /// <summary>
    /// Writes a .pack/.idx pair whose single entry claims <paramref name="oid"/>
    /// at <paramref name="offset"/> (crc over the entry). With
    /// <paramref name="fakeChecksum"/> the idx's pack checksum is zeroed — the
    /// pack itself is truncated and has no trailer.
    /// </summary>
    private static string WritePack(
        string packDir,
        string namePrefix,
        byte[] packBytes,
        GitOid oid,
        long offset,
        GitHashAlgorithmKind algorithm,
        bool fakeChecksum = false)
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

        byte[] packChecksum = fakeChecksum ? new byte[oidSize] : packBytes[^oidSize..];
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
