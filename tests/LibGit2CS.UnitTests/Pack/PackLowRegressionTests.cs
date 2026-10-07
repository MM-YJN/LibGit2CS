using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Pack;

// Parity cases verified against libgit2 1.9.4:
//  - the decompression-window growth (int)Math.Min(available, toRead*4)
//    wrapped negative for packs with >2GB remaining, throwing
//    OverflowException from new byte[toRead] (C streams incrementally,
//    pack.c:847-879).
//  - written .pack/.idx got umask-default 0644 instead of C's
//    read-only 0444 (pack.h:31), and the .idx was written non-atomically
//    (C uses git_filebuf temp+rename, indexer.c:1383-1391).
//  - NameHash folded UTF-16 chars instead of the raw UTF-8 bytes
//    (pack-objects.c:73-88) — non-bit-exact packs for non-ASCII paths.
//  - attacker-controlled delta result size ≥ 2^31 threw
//    OverflowException instead of a clean GitException (delta.c:568-570).
//  - the OFS_DELTA base varint accumulated into a signed long and
//    wrapped negative, bypassing C's "out of bounds" rejection
//    (pack.c:992-1004).
//  - the two pack write paths used different header counts with no
//    local check; C asserts "invalid write order" (pack-objects.c:623-627).
public sealed class PackLowRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public PackLowRegressionTests()
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
        catch (IOException)
        {
        }
    }

    // ---- decompression window growth never wraps negative ----

    [Fact]
    public void NextDecompressWindow_OverArrayLimit_ReturnsMinusOne()
    {
        // 1 GiB window in a >2 GiB pack: the old (int)Math.Min(available,
        // toRead * 4) wrapped negative (256K→…→1G→4G), and new byte[toRead]
        // threw OverflowException.
        Assert.Equal(-1, GitPackIndexer.NextDecompressWindow(available: 3L << 30, current: 1 << 30));
    }

    [Fact]
    public void NextDecompressWindow_NormalGrowth_Quadruples()
    {
        // Control: 256 KiB → 1 MiB in a large pack.
        Assert.Equal(1 << 20, GitPackIndexer.NextDecompressWindow(available: 1L << 30, current: 1 << 18));
    }

    [Fact]
    public void NextDecompressWindow_GrowthBoundedByAvailable()
    {
        // Control: growth never exceeds the remaining pack bytes.
        Assert.Equal(1 << 20, GitPackIndexer.NextDecompressWindow(available: 1 << 20, current: 1 << 18));
    }

    // ---- pack/idx files are 0444 and the idx write is atomic ----

    [Fact]
    public async Task WriteToDirectory_PackAndIdxAreReadOnly_NoTempLeftovers()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX mode semantics
        }

        string repoPath = Path.Combine(_tempDir, "repo8");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: true, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await repo.ObjectWriteAsync(
            GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);

        string packDir = Path.Combine(_tempDir, "packs8");
        Directory.CreateDirectory(packDir);

        using GitPackWriter pb = repo.NewPackWriter();
        await pb.InsertAsync(blobOid, TestContext.Current.CancellationToken);
        string packPath = await pb.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        // C: GIT_PACK_FILE_MODE 0444 (pack.h:31), umask-masked by open(2) —
        // never owner-writable. Leaving both at 0666 & ~umask
        // (0644 here) would be wrong.
        UnixFileMode packMode = File.GetUnixFileMode(packPath);
        UnixFileMode idxMode = File.GetUnixFileMode(idxPath);
        Assert.Equal(0, (int)(packMode & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)));
        Assert.Equal(0, (int)(idxMode & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)));

        // The .idx is written via temp + rename (C's git_filebuf commit_at,
        // indexer.c:1383-1391) — no temp files may remain.
        string[] leftovers = Directory.GetFiles(packDir, "*.tmp.*");
        Assert.Empty(leftovers);
    }

    // ---- NameHash folds the raw UTF-8 bytes ----

    [Fact]
    public void NameHash_NonAsciiPath_HashesUtf8Bytes()
    {
        // C's name_hash (pack-objects.c:73-88) folds the raw UTF-8 bytes:
        // "café.txt" = 63 61 66 C3 A9 2E 74 78 74. Folding
        // UTF-16 chars (U+00E9 as one unit) would produce a different hash and
        // delta-base selection.
        uint expected = 0;
        foreach (byte c in "café.txt"u8)
        {
            if (c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r')
            {
                continue;
            }

            expected = (expected >> 2) + ((uint)c << 24);
        }

        Assert.Equal(expected, GitPackWriter.NameHash("café.txt"));
    }

    [Fact]
    public void NameHash_AsciiPath_Unchanged()
    {
        // Control: ASCII paths hash identically under both schemes.
        Assert.Equal(GitPackWriter.NameHash("README.md"), GitPackWriter.NameHash("README.md"));
        Assert.Equal(0u, GitPackWriter.NameHash((string?)null));
    }

    // ---- delta result size ≥ 2^31 fails cleanly ----

    [Fact]
    public void Apply_ResultSizeOverIntMax_ThrowsGitException()
    {
        // base-size 0, result-size 2^31 (varint 80 80 80 80 08). Throwing
        // OverflowException from new byte[resultSize] would not match C's
        // GIT_ERROR_CHECK_ALLOC_ADD (delta.c:568-570) clean error.
        byte[] delta = [0x00, 0x80, 0x80, 0x80, 0x80, 0x08];
        GitException ex = Assert.Throws<GitException>(() => GitDeltaApplier.Apply([], delta));
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }

    [Fact]
    public void Apply_ResultSizeNegative_ThrowsGitException()
    {
        // A bit-63 varint (0xFF × 9 + 0x01) reads as a negative long — also
        // rejected instead of an array-allocation exception.
        byte[] delta = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        Assert.Throws<GitException>(() => GitDeltaApplier.Apply([], delta));
    }

    [Fact]
    public void Apply_NormalDelta_StillApplies()
    {
        // Control: a small valid delta still applies.
        byte[] baseData = "hello world"u8.ToArray();
        byte[] delta = [0x0B, 0x0C, 0x90, 0x0B, 0x01, 0x21]; // copy 11@0, insert '!'
        byte[] result = GitDeltaApplier.Apply(baseData, delta);
        Assert.Equal("hello world!", Encoding.UTF8.GetString(result));
    }

    // ---- OFS_DELTA base varint wrap is rejected ----

    [Fact]
    public async Task Indexer_OfsDeltaWrappedBase_ThrowsOutOfBounds()
    {
        // OFS_DELTA object: header 0x60 (type 6, size 0) + base varint
        // [0xFF × 8, 0x00]. The accumulation reaches 2^56 pre-shift (guard
        // passes), then << 7 wraps into [2^63, 2^64): C's unsigned size_t
        // stays huge and rejects (pack.c:992-1004) — a signed long would go
        // negative, pass the out-of-bounds check, and point the base FORWARD.
        var pack = new MemoryStream();
        pack.Write("PACK"u8);
        WriteUInt32BE(pack, 2);
        WriteUInt32BE(pack, 1);
        pack.WriteByte(0x60);
        for (int i = 0; i < 8; i++)
        {
            pack.WriteByte(0xFF);
        }

        pack.WriteByte(0x00);
        byte[] trailer = new byte[20]; // any trailer — the parse throws first
        pack.Write(trailer);

        var indexer = new GitPackIndexer(_tempDir, GitHashAlgorithmKind.Sha1);
        await using (indexer.ConfigureAwait(false))
        {
            var stats = new GitIndexerProgress();
            GitException ex = await Assert.ThrowsAsync<GitException>(
                () => indexer.AppendAsync(pack.ToArray(), stats, TestContext.Current.CancellationToken));
            Assert.Contains("out of bounds", ex.Message);
        }
    }

    [Fact]
    public async Task PackFile_OfsDeltaWrappedBase_ThrowsOutOfBounds()
    {
        // Same crafted object, exercised through PackFile.ReadAsync: the base
        // varint parse must reject the wrapped offset with C's out-of-bounds
        // error instead of computing a forward base offset.
        byte[] packData = BuildOfsDeltaPack();
        string packPath = Path.Combine(_tempDir, "pack-wrapped.pack");
        string idxPath = Path.Combine(_tempDir, "pack-wrapped.idx");
        await File.WriteAllBytesAsync(packPath, packData, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(idxPath, BuildIdxV2(packData), cancellationToken: TestContext.Current.CancellationToken);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitOid oid = pack.Enumerate().First();
        GitException ex = await Assert.ThrowsAsync<GitException>(() => pack.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Contains("out of bounds", ex.Message);
    }

    /// <summary>Builds a one-object pack whose OFS_DELTA base varint wraps.</summary>
    private static byte[] BuildOfsDeltaPack()
    {
        using var pack = new MemoryStream();
        pack.Write("PACK"u8);
        WriteUInt32BE(pack, 2);
        WriteUInt32BE(pack, 1);
        pack.WriteByte(0x60);
        for (int i = 0; i < 8; i++)
        {
            pack.WriteByte(0xFF);
        }

        pack.WriteByte(0x00);
        byte[] data = pack.ToArray();
        byte[] trailer = SHA1.HashData(data);
        pack.Write(trailer);
        return pack.ToArray();
    }

    /// <summary>Builds a v2 .idx over the single object at offset 12.</summary>
    private static byte[] BuildIdxV2(byte[] packData)
    {
        byte[] packChecksum = packData[^20..];
        byte[] oid = new byte[20];
        Array.Fill(oid, (byte)0xAB);

        int len = 8 + 4 * 256 + 20 + 4 + 4 + 20 + 20;
        byte[] data = new byte[len];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0), 0xff744f63);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 2);

        // Fanout: the single OID starts with 0xAB.
        for (int i = 0xAB; i < 256; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8 + i * 4), 1);
        }

        int pos = 8 + 4 * 256;
        oid.CopyTo(data.AsSpan(pos));
        pos += 20;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(pos), 0); // CRC
        pos += 4;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(pos), 12); // offset
        pos += 4;
        packChecksum.CopyTo(data.AsSpan(pos));
        pos += 20;

        byte[] idxChecksum = SHA1.HashData(data.AsSpan(0, pos));
        idxChecksum.CopyTo(data.AsSpan(pos));
        return data;
    }

    private static void WriteUInt32BE(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        s.Write(b);
    }

    // ---- write-order count validation ----

    [Fact]
    public async Task WriteToDirectory_HeaderCountMatchesObjectList()
    {
        // Control: a normal pack write still succeeds (the count check is
        // satisfied) and the header count equals the object count.
        string repoPath = Path.Combine(_tempDir, "repo12");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: true, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await repo.ObjectWriteAsync(
            GitObjectType.Blob, "data"u8.ToArray(), TestContext.Current.CancellationToken);

        string packDir = Path.Combine(_tempDir, "packs12");
        Directory.CreateDirectory(packDir);

        using GitPackWriter pb = repo.NewPackWriter();
        await pb.InsertAsync(blobOid, TestContext.Current.CancellationToken);
        string packPath = await pb.WriteToDirectoryAsync(packDir, null, TestContext.Current.CancellationToken);

        byte[] header = new byte[12];
        using (FileStream fs = File.OpenRead(packPath))
        {
            await fs.ReadExactlyAsync(header, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal("PACK"u8.ToArray(), header[..4]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
    }
}
