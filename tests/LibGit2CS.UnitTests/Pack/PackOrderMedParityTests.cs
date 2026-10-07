using System.Buffers.Binary;
using System.IO.Hashing;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

/// <summary>
/// Regression tests for the ODB pack-read behavior (pack read: C fails on
/// the first pack that claims the OID if unpacking fails — it does not try a
/// "good copy" in another pack; pack search order is local-first then
/// mtime-youngest-first, odb_pack.c packfile_sort__cb).
/// </summary>
public sealed class PackOrderMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public PackOrderMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackOrder_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// Two packs claim the same OID with different content; C reads from the
    /// younger pack (mtime-sorted first — packfile_sort__cb, odb_pack.c:205-225).
    /// </summary>
    [Fact]
    public async Task Read_SameOidInTwoPacks_YoungerPackWins()
    {
        string packDir = Path.Combine(_tempDir, "packs");
        Directory.CreateDirectory(packDir);
        CancellationToken ct = TestContext.Current.CancellationToken;

        byte[] oldContent = "old content"u8.ToArray();
        byte[] newContent = "new content"u8.ToArray();
        // The idx may claim any OID; both packs claim the SAME one.
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, oldContent, GitHashAlgorithmKind.Sha1);

        string oldPack = WritePackClaimingOid(packDir, "pack-old", oid, oldContent, GitHashAlgorithmKind.Sha1);
        string newPack = WritePackClaimingOid(packDir, "pack-new", oid, newContent, GitHashAlgorithmKind.Sha1);

        // Older pack first, younger pack second (younger mtime sorts first).
        File.SetLastWriteTimeUtc(oldPack, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(Path.ChangeExtension(oldPack, ".idx"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newPack, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.ChangeExtension(newPack, ".idx"), DateTime.UtcNow);

        await using var backend = new PackObjectBackend(packDir, GitHashAlgorithmKind.Sha1);
        await backend.RefreshAsync(ct);

        RawObjectData? raw = await backend.ReadAsync(oid, ct);

        Assert.NotNull(raw);
        Assert.Equal(newContent, raw!.Value.Data);
    }

    /// <summary>
    /// C fails on the FIRST pack whose idx claims the OID when unpacking
    /// fails — a good copy in a later pack is never consulted. The claiming
    /// thin pack (REF_DELTA with a missing base) sorts first (newer mtime).
    /// </summary>
    [Fact]
    public async Task Read_FirstClaimingPackFails_ThrowsInsteadOfGoodCopy()
    {
        string packDir = Path.Combine(_tempDir, "packs");
        Directory.CreateDirectory(packDir);
        CancellationToken ct = TestContext.Current.CancellationToken;

        byte[] body = "the object"u8.ToArray();
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

        // Pack 1: a thin REF_DELTA pack that CLAIMS oid in its idx but cannot
        // unpack (base missing, no resolver for the base) — newer mtime.
        byte[] delta = [0x01, 0x02, 0x03];
        string thinPack = WriteThinPackClaimingOid(packDir, "pack-thin", oid, delta, GitHashAlgorithmKind.Sha1);

        // Pack 2: a full good copy of the object — older mtime.
        string goodPack = WritePackClaimingOid(packDir, "pack-good", oid, body, GitHashAlgorithmKind.Sha1);

        File.SetLastWriteTimeUtc(goodPack, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(Path.ChangeExtension(goodPack, ".idx"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(thinPack, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.ChangeExtension(thinPack, ".idx"), DateTime.UtcNow);

        await using var backend = new PackObjectBackend(packDir, GitHashAlgorithmKind.Sha1);
        await backend.RefreshAsync(ct);

        // C (pack_backend__read → pack_entry_find + git_packfile_unpack):
        // the first claiming pack's unpack failure propagates immediately —
        // the good copy in the other pack is never tried.
        await Assert.ThrowsAsync<GitException>(async () => await backend.ReadAsync(oid, ct));
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes a self-contained pack whose .idx claims <paramref name="oid"/>
    /// for a single entry whose packed bytes are <paramref name="content"/>
    /// (header + zlib body). The claimed OID need not match the content hash —
    /// mirroring a pack that holds a stale/duplicate entry.
    /// </summary>
    private static string WritePackClaimingOid(
        string packDir,
        string namePrefix,
        GitOid oid,
        byte[] content,
        GitHashAlgorithmKind algorithm)
    {
        Span<byte> hdrBuf = stackalloc byte[16];
        int hdrLen = PackEncoding.WriteObjectHeader(hdrBuf, GitObjectType.Blob, content.Length);
        byte[] compressed = ZlibTestHelpers.CompressLooseObject(content);
        using var packMs = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], 1);
        packMs.Write(header);
        packMs.Write(hdrBuf[..hdrLen]);
        packMs.Write(compressed);
        byte[] packBody = packMs.ToArray();

        using var hash = GitIncrementalHash.Create(algorithm);
        hash.AppendData(packBody);
        byte[] trailer = hash.Finalize().RawBytes.ToArray();
        byte[] packBytes = [.. packBody, .. trailer];

        uint crc = Crc32.HashToUInt32(packBytes.AsSpan(12, hdrLen + compressed.Length));
        return WriteIdxAndPack(packDir, namePrefix, packBytes, oid, 12, crc, algorithm);
    }

    /// <summary>
    /// Writes a thin pack (REF_DELTA entry, base NOT in the pack) whose .idx
    /// claims <paramref name="oid"/>. Unpacking fails (missing base) unless a
    /// cross-pack resolver supplies it.
    /// </summary>
    private static string WriteThinPackClaimingOid(
        string packDir,
        string namePrefix,
        GitOid oid,
        byte[] deltaData,
        GitHashAlgorithmKind algorithm)
    {
        int oidSize = GitOid.SizeFor(algorithm);
        Span<byte> hdrBuf = stackalloc byte[16];
        int hdrLen = PackEncoding.WriteObjectHeader(hdrBuf, GitObjectType.RefDelta, deltaData.Length);
        byte[] compressed = ZlibTestHelpers.CompressLooseObject(deltaData);
        using var packMs = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], 1);
        packMs.Write(header);
        packMs.Write(hdrBuf[..hdrLen]);
        packMs.Write(oid.RawBytes);
        packMs.Write(compressed);
        byte[] packBody = packMs.ToArray();

        using var hash = GitIncrementalHash.Create(algorithm);
        hash.AppendData(packBody);
        byte[] trailer = hash.Finalize().RawBytes.ToArray();
        byte[] packBytes = [.. packBody, .. trailer];

        uint crc = Crc32.HashToUInt32(packBytes.AsSpan(12, hdrLen + oidSize + compressed.Length));
        return WriteIdxAndPack(packDir, namePrefix, packBytes, oid, 12, crc, algorithm);
    }

    /// <summary>
    /// Writes a .idx v2 + .pack pair with a single entry claiming
    /// <paramref name="oid"/> at <paramref name="offset"/> with
    /// <paramref name="crc"/>. Returns the .pack path.
    /// </summary>
    private static string WriteIdxAndPack(
        string packDir,
        string namePrefix,
        byte[] packBytes,
        GitOid oid,
        long offset,
        uint crc,
        GitHashAlgorithmKind algorithm)
    {
        int oidSize = GitOid.SizeFor(algorithm);
        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(algorithm);

        Span<byte> magic = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(magic[0..4], 0xff744f63);
        BinaryPrimitives.WriteUInt32BigEndian(magic[4..8], 2);
        ms.Write(magic);
        hash.AppendData(magic);

        // Fanout: only the first byte of oid is populated.
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
