using System.Buffers.Binary;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

// GetObjectOffset must use size_t-style arithmetic for the large-offset
// position, so a crafted .idx whose 4-byte offset entry decodes to
// largeIdx = 0x20000000 fails cleanly instead of wrapping `largeIdx * 8`
// to 0 and returning attacker-chosen trailer bytes
// (C uses size_t arithmetic, pack.c:1279-1283).
//
// The midx packfile count must stay unsigned, so values >= 2^31 do not
// wrap negative and skip the PNAM validation; C keeps the uint32 count and
// fails the parse (midx.c:52-85), falling back to per-pack .idx lookup.
//
// The indexer must compare the inflated delta length to the pack
// header's declared size, so a pack whose delta header size mismatches the
// stream is rejected (C rejects `total != size`, pack.c:938-942).
//
// DecompressBody must not wrap `expectedSize * 3 + 1024` negative for
// crafted header sizes (C's GIT_ERROR_CHECK_ALLOC_ADD errors cleanly,
// pack.c:899), and the read must not scale
// off the attacker-controlled declared size (whole-tail amplification).
//
// UnpackAtOffset must not eagerly decompress every chain level and pin the
// sum of all delta bodies; C stores metadata and inflates each delta during
// the unwind (pack.c:583-663, 766-817).
public sealed class PackMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public PackMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── Large Offset Table Int Overflow Throws Clean Error ───────────

    [Fact]
    public void LargeOffsetTable_IntOverflow_ThrowsCleanError()
    {
        // 36-object v2 .idx: entry 0's offset decodes to largeIdx = 0x20000000.
        // C: size_t arithmetic → past the end → clean error; the index must not
        // wrap the multiplication and land inside the (attacker-controlled)
        // trailer.
        const int n = 36;
        byte[] data = new byte[8 + 256 * 4 + n * 20 + n * 4 + n * 4 + 40];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0xFF744F63);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 2);

        // Fanout: all 36 objects share every first byte (monotonic).
        for (int i = 0; i < 256; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8 + i * 4, 4), n);
        }

        int oidTableOffset = 8 + 256 * 4;
        int crcTableOffset = oidTableOffset + n * 20;
        int offsetTableOffset = crcTableOffset + n * 4;

        for (int i = 0; i < n; i++)
        {
            // OID table: distinct OIDs.
            data[oidTableOffset + i * 20] = (byte)i;
        }

        // Entry 0: large-offset marker with largeIdx = 0x20000000.
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offsetTableOffset, 4), 0x80000000u | 0x20000000u);
        for (int i = 1; i < n; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offsetTableOffset + i * 4, 4), (uint)(12 + i));
        }

        // Trailer (checksums): recognizable attacker-chosen bytes that a
        // wrapped lookup must never return as the object offset.
        for (int i = 0; i < 40; i++)
        {
            data[data.Length - 40 + i] = (byte)(0x11 + i);
        }

        using var idx = GitPackIndex.Parse(data, GitHashAlgorithmKind.Sha1);
        GitException ex = Assert.Throws<GitException>(() => idx.GetObjectOffset(0));
        Assert.Contains("large offset out of bounds", ex.Message);
    }

    // ── Midx Packfile Count Over Int Max Throws Clean Error ──────────

    [Fact]
    public async Task Midx_PackfileCountOverIntMax_ThrowsCleanError()
    {
        // Header (12) + one chunk header (12) + filler: packfile count 0xFFFFFFFF.
        byte[] midx = new byte[32];
        midx[0] = (byte)'M';
        midx[1] = (byte)'I';
        midx[2] = (byte)'D';
        midx[3] = (byte)'X';
        midx[4] = 1; // version
        midx[5] = 1; // oid version
        midx[6] = 1; // one chunk
        BinaryPrimitives.WriteUInt32BigEndian(midx.AsSpan(8, 4), 0xFFFFFFFF);

        string packDir = Path.Combine(_tempDir, "midx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await File.WriteAllBytesAsync(
            Path.Combine(packDir, "multi-pack-index"), midx,
            cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("too many packfiles", ex.Message);
    }

    // ── Delta Header Size Mismatch Fails Commit ──────────────────────

    [Fact]
    public async Task Delta_HeaderSizeMismatch_FailsCommit()
    {
        byte[] baseBody = "base content\n"u8.ToArray();
        byte[] suffix = " - suffix"u8.ToArray();
        byte[] deltaData = ThinPackBuilder.MakeCopyAppendDelta(baseBody, suffix);

        // REF_DELTA whose pack header declares a size 7 bytes larger than the
        // actual delta stream — C rejects `total != size` (pack.c:938-942), so
        // the pack commit fails rather than accepting it and surfacing a read
        // error later.
        byte[] packBytes = BuildPackWithRefDelta(baseBody, deltaData, declaredDeltaSize: deltaData.Length + 7);

        string packDir = Path.Combine(_tempDir, "idx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await indexer.CommitAsync(stats, TestContext.Current.CancellationToken));
        Assert.Contains("error inflating zlib stream", ex.Message);
    }

    [Fact]
    public async Task Delta_HeaderSizeMatch_Commits()
    {
        // Control: the same pack with the CORRECT declared size commits fine.
        byte[] baseBody = "base content\n"u8.ToArray();
        byte[] suffix = " - suffix"u8.ToArray();
        byte[] deltaData = ThinPackBuilder.MakeCopyAppendDelta(baseBody, suffix);
        byte[] packBytes = BuildPackWithRefDelta(baseBody, deltaData, declaredDeltaSize: deltaData.Length);

        string packDir = Path.Combine(_tempDir, "idx_ok_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken);
        await indexer.CommitAsync(stats, TestContext.Current.CancellationToken);
        Assert.NotNull(indexer.PackPath);
    }

    /// <summary>
    /// Builds a 2-object pack: a full base blob + a REF_DELTA against it whose
    /// pack header declares <paramref name="declaredDeltaSize"/>.
    /// </summary>
    private static byte[] BuildPackWithRefDelta(byte[] baseBody, byte[] deltaData, long declaredDeltaSize)
    {
        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);

        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(GitHashAlgorithmKind.Sha1);

        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], 2);
        ms.Write(header);
        hash.AppendData(header);

        Span<byte> objHdr = stackalloc byte[16];

        // Full base object.
        int hdrLen = PackEncoding.WriteObjectHeader(objHdr, GitObjectType.Blob, baseBody.Length);
        ms.Write(objHdr[..hdrLen].ToArray());
        hash.AppendData(objHdr[..hdrLen]);
        byte[] compressedBase = ZlibTestHelpers.CompressLooseObject(baseBody);
        ms.Write(compressedBase);
        hash.AppendData(compressedBase);

        // REF_DELTA with a (possibly wrong) declared size.
        int deltaHdrLen = PackEncoding.WriteObjectHeader(objHdr, GitObjectType.RefDelta, declaredDeltaSize);
        ms.Write(objHdr[..deltaHdrLen].ToArray());
        hash.AppendData(objHdr[..deltaHdrLen]);
        ms.Write(baseOid.RawBytes.ToArray());
        hash.AppendData(baseOid.RawBytes.ToArray());
        byte[] compressedDelta = ZlibTestHelpers.CompressLooseObject(deltaData);
        ms.Write(compressedDelta);
        hash.AppendData(compressedDelta);

        GitOid trailer = hash.Finalize();
        ms.Write(trailer.RawBytes.ToArray());
        return ms.ToArray();
    }

    // ── Huge Declared Size Throws Clean Git Exception ────────────────

    [Fact]
    public async Task HugeDeclaredSize_ThrowsCleanGitException()
    {
        // A full blob whose pack header declares 2^62 bytes with a tiny body
        // fails with a clean GitException like C's GIT_ERROR_CHECK_ALLOC_ADD
        // (pack.c:899), not with a raw OverflowException from the window
        // allocation.
        byte[] body = "hello"u8.ToArray();
        byte[] packBytes = BuildPackWithSingleObject(body, declaredSize: 1L << 62);

        string packDir = Path.Combine(_tempDir, "big_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken);
        await indexer.CommitAsync(stats, TestContext.Current.CancellationToken);

        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);
        using PackFile pack = await PackFile.OpenAsync(indexer.PackPath!, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => pack.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Zlib, ex.Category);
        Assert.Contains("error inflating zlib stream", ex.Message);
    }

    private static byte[] BuildPackWithSingleObject(byte[] body, long declaredSize)
    {
        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(GitHashAlgorithmKind.Sha1);

        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], 1);
        ms.Write(header);
        hash.AppendData(header);

        Span<byte> objHdr = stackalloc byte[16];
        int hdrLen = PackEncoding.WriteObjectHeader(objHdr, GitObjectType.Blob, declaredSize);
        ms.Write(objHdr[..hdrLen].ToArray());
        hash.AppendData(objHdr[..hdrLen]);

        byte[] compressed = ZlibTestHelpers.CompressLooseObject(body);
        ms.Write(compressed);
        hash.AppendData(compressed);

        GitOid trailer = hash.Finalize();
        ms.Write(trailer.RawBytes.ToArray());
        return ms.ToArray();
    }

    // ── Deep Delta Chain Resolves Correctly ──────────────────────────

    [Fact]
    public async Task DeepDeltaChain_ResolvesCorrectly()
    {
        // functional guard: a 60-deep REF_DELTA chain must resolve under the
        // metadata-only chain walk (each delta body is re-inflated from the
        // pack during the unwind).
        const int depth = 60;
        byte[] baseBody = "v0"u8.ToArray();
        byte[] result = baseBody;
        byte[] prev = baseBody;
        var deltaOids = new GitOid[depth];

        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, baseBody);

        for (int i = 0; i < depth; i++)
        {
            byte[] suffix = Encoding.ASCII.GetBytes(((char)('a' + (i % 26))).ToString());
            byte[] deltaData = ThinPackBuilder.MakeCopyAppendDelta(prev, suffix);
            result = [.. prev, .. suffix];
            deltaOids[i] = GitObjectDb.HashObject(GitObjectType.Blob, result, GitHashAlgorithmKind.Sha1);
            builder.AddRefDelta(GitObjectDb.HashObject(GitObjectType.Blob, prev, GitHashAlgorithmKind.Sha1), deltaData);
            prev = result;
        }

        byte[] packBytes = builder.Build();
        string packDir = Path.Combine(_tempDir, "chain_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken);
        await indexer.CommitAsync(stats, TestContext.Current.CancellationToken);

        using PackFile pack = await PackFile.OpenAsync(indexer.PackPath!, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        RawObjectData? raw = await pack.ReadAsync(deltaOids[^1], TestContext.Current.CancellationToken);
        Assert.NotNull(raw);
        Assert.Equal("v0" + new string(Enumerable.Range(0, depth).Select(i => (char)('a' + (i % 26))).ToArray()), Encoding.ASCII.GetString(raw!.Value.Data));
    }
}
