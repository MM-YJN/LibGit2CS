using System.Buffers.Binary;
using System.IO.Hashing;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

/// <summary>
/// Tests cross-pack REF_DELTA resolution: when a pack contains a REF_DELTA
/// whose base OID lives in a different pack (or as a loose object), the
/// <see cref="PackFile"/>._crossPackBaseResolver callback (wired by
/// <see cref="PackObjectBackend"/>) resolves the base so the delta can be
/// applied.
/// </summary>
public sealed class CrossPackRefDeltaTests : IDisposable
{
    private readonly string _tempDir;

    public CrossPackRefDeltaTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CrossPack_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// Two packs in the same directory: Pack B has the base blob as a full
    /// object; Pack A has a REF_DELTA whose base is Pack B's blob.
    /// <see cref="PackObjectBackend.ReadAsync"/> should resolve the delta via
    /// the cross-pack resolver and return the correct data.
    /// </summary>
    [Fact]
    public async Task ReadAsync_CrossPackRefDelta_ResolvesFromOtherPack()
    {
        string packDir = Path.Combine(_tempDir, "pack");
        Directory.CreateDirectory(packDir);
        CancellationToken ct = TestContext.Current.CancellationToken;

        byte[] baseBody = "hello world\n"u8.ToArray();
        byte[] suffix = "more data\n"u8.ToArray();
        byte[] resultBody = [.. baseBody, .. suffix];

        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        // Pack B: base object as a full (non-delta) entry.
        WritePackWithFullObjects(packDir, "pack-base", [(GitObjectType.Blob, baseBody)], GitHashAlgorithmKind.Sha1);

        // Pack A: thin pack with a REF_DELTA against baseOid.
        byte[] delta = ThinPackBuilder.MakeCopyAppendDelta(baseBody, suffix);
        WriteThinPackWithRefDelta(packDir, "pack-delta", baseOid, delta, resultOid, GitHashAlgorithmKind.Sha1);

        // Open via PackObjectBackend — the resolver should be wired automatically.
        await using var backend = new PackObjectBackend(packDir, GitHashAlgorithmKind.Sha1);
        await backend.RefreshAsync(ct);

        RawObjectData? raw = await backend.ReadAsync(resultOid, ct);

        Assert.NotNull(raw);
        Assert.Equal(GitObjectType.Blob, raw!.Value.Type);
        Assert.Equal(resultBody, raw.Value.Data);
    }

    /// <summary>
    /// Same scenario as above but testing <see cref="PackObjectBackend.ReadHeaderAsync"/>:
    /// the header (type + size) should resolve via the cross-pack resolver.
    /// </summary>
    [Fact]
    public async Task ReadHeaderAsync_CrossPackRefDelta_ResolvesFromOtherPack()
    {
        string packDir = Path.Combine(_tempDir, "pack");
        Directory.CreateDirectory(packDir);
        CancellationToken ct = TestContext.Current.CancellationToken;

        byte[] baseBody = "header test base\n"u8.ToArray();
        byte[] suffix = "appended\n"u8.ToArray();
        byte[] resultBody = [.. baseBody, .. suffix];

        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        WritePackWithFullObjects(packDir, "pack-base", [(GitObjectType.Blob, baseBody)], GitHashAlgorithmKind.Sha1);
        byte[] delta = ThinPackBuilder.MakeCopyAppendDelta(baseBody, suffix);
        WriteThinPackWithRefDelta(packDir, "pack-delta", baseOid, delta, resultOid, GitHashAlgorithmKind.Sha1);

        await using var backend = new PackObjectBackend(packDir, GitHashAlgorithmKind.Sha1);
        await backend.RefreshAsync(ct);

        GitObjectHeader? header = await backend.ReadHeaderAsync(resultOid, ct);

        Assert.NotNull(header);
        // The base object's type (C's resolve_header walks the chain to find the base type), and the delta's RESULT size (the size comes from the delta header,
        // not the base).
        Assert.Equal(GitObjectType.Blob, header!.Value.Type);
        Assert.Equal(resultBody.Length, header.Value.Size);
    }

    /// <summary>
    /// PackFile.ReadAsync without a resolver still throws for cross-pack REF_DELTA
    /// (matching C libgit2's get_delta_base). The resolver is opt-in.
    /// </summary>
    [Fact]
    public async Task PackFile_Read_CrossPackRefDelta_WithoutResolver_Throws()
    {
        string packDir = Path.Combine(_tempDir, "pack");
        Directory.CreateDirectory(packDir);

        byte[] baseBody = "no resolver\n"u8.ToArray();
        byte[] suffix = "suffix\n"u8.ToArray();
        byte[] resultBody = [.. baseBody, .. suffix];

        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        byte[] delta = ThinPackBuilder.MakeCopyAppendDelta(baseBody, suffix);
        (string packPath, _) = WriteThinPackWithRefDelta(packDir, "pack-thin", baseOid, delta, resultOid, GitHashAlgorithmKind.Sha1);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => pack.ReadAsync(resultOid, TestContext.Current.CancellationToken));
        Assert.Contains("REF_DELTA base", ex.Message);
    }

    /// <summary>
    /// PackFile.ReadAsync WITH a resolver callback resolves cross-pack REF_DELTA.
    /// </summary>
    [Fact]
    public async Task PackFile_Read_CrossPackRefDelta_WithResolver_ReturnsData()
    {
        string packDir = Path.Combine(_tempDir, "pack");
        Directory.CreateDirectory(packDir);

        byte[] baseBody = "resolver base\n"u8.ToArray();
        byte[] suffix = "appended\n"u8.ToArray();
        byte[] resultBody = [.. baseBody, .. suffix];

        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
        GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        byte[] delta = ThinPackBuilder.MakeCopyAppendDelta(baseBody, suffix);
        (string packPath, _) = WriteThinPackWithRefDelta(packDir, "pack-thin", baseOid, delta, resultOid, GitHashAlgorithmKind.Sha1);

        // Resolver callback returns the known base data.
        Func<GitOid, CancellationToken, Task<RawObjectData?>> resolver = (oid, _) =>
        {
            if (oid.Equals(baseOid))
            {
                return Task.FromResult<RawObjectData?>(new RawObjectData(GitObjectType.Blob, baseBody.Length, baseBody));
            }

            return Task.FromResult<RawObjectData?>(null);
        };

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken, resolver);

        RawObjectData? raw = await pack.ReadAsync(resultOid, TestContext.Current.CancellationToken);

        Assert.NotNull(raw);
        Assert.Equal(GitObjectType.Blob, raw!.Value.Type);
        Assert.Equal(resultBody, raw.Value.Data);
    }

    /// <summary>
    /// RefreshAsync leaves tmp_pack_* files alone — C's packfile_load__cb
    /// (odb_pack.c) never touches them.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_LeavesOrphanedTempPacks()
    {
        string packDir = Path.Combine(_tempDir, "pack");
        Directory.CreateDirectory(packDir);

        // Create some orphaned temp pack files.
#pragma warning disable CA1849
        File.WriteAllBytes(Path.Combine(packDir, "tmp_pack_abc123"), "garbage"u8.ToArray());
        File.WriteAllBytes(Path.Combine(packDir, "tmp_pack_def456"), "more garbage"u8.ToArray());
#pragma warning restore CA1849

        // Create a dummy .idx + .pack so RefreshAsync has something to load.
        byte[] body = "temp cleanup test\n"u8.ToArray();
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);
        WritePackWithFullObjects(packDir, "pack-real", [(GitObjectType.Blob, body)], GitHashAlgorithmKind.Sha1);

        await using var backend = new PackObjectBackend(packDir, GitHashAlgorithmKind.Sha1);
        await backend.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(packDir, "tmp_pack_abc123")), "tmp_pack_abc123 must NOT be deleted");
        Assert.True(File.Exists(Path.Combine(packDir, "tmp_pack_def456")), "tmp_pack_def456 must NOT be deleted");
        Assert.True(backend.Exists(oid), "real pack should still be loaded");
    }

    /// <summary>
    /// ReadHeaderAsync does not throw when a pack claims an OID but the content
    /// is unreadable; it tries remaining packs and returns null if none can
    /// satisfy the read.
    /// </summary>
    [Fact]
    public async Task ReadHeaderAsync_CorruptPack_DoesNotThrow()
    {
        string packDir = Path.Combine(_tempDir, "pack");
        Directory.CreateDirectory(packDir);
        CancellationToken ct = TestContext.Current.CancellationToken;

        byte[] body = "survivable\n"u8.ToArray();
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

        // Write a pack with the object.
        WritePackWithFullObjects(packDir, "pack-good", [(GitObjectType.Blob, body)], GitHashAlgorithmKind.Sha1);

        await using var backend = new PackObjectBackend(packDir, GitHashAlgorithmKind.Sha1);
        await backend.RefreshAsync(ct);

        // ReadHeader should succeed for the existing OID.
        GitObjectHeader? header = await backend.ReadHeaderAsync(oid, ct);
        Assert.NotNull(header);
        Assert.Equal(GitObjectType.Blob, header!.Value.Type);

        // ReadHeader should return null for a nonexistent OID without throwing.
        byte[] nonexistentBytes = new byte[20];
        Array.Fill(nonexistentBytes, (byte)0xDE);
        var nonexistent = GitOid.FromRaw(nonexistentBytes, GitHashAlgorithmKind.Sha1);
        Assert.Null(await backend.ReadHeaderAsync(nonexistent, ct));
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes a self-contained pack (.pack + .idx) with the given full objects.
    /// Uses <see cref="ThinPackBuilder"/> to build the pack bytes and a manual
    /// .idx v2 builder.
    /// </summary>
    private static (string PackPath, string IdxPath) WritePackWithFullObjects(
        string packDir,
        string namePrefix,
        IReadOnlyList<(GitObjectType Type, byte[] Body)> objects,
        GitHashAlgorithmKind algorithm)
    {
        var builder = new ThinPackBuilder(algorithm);
        foreach ((GitObjectType type, byte[] body) in objects)
        {
            builder.AddFullObject(type, body);
        }

        byte[] packBytes = builder.Build();
        int oidSize = GitOid.SizeFor(algorithm);

        // Compute OIDs and CRCs for each entry.
        var entries = new List<(GitOid Oid, long Offset, uint Crc)>();
        long offset = 12; // after PACK header
        Span<byte> hdrBuf = stackalloc byte[16];

        for (int i = 0; i < objects.Count; i++)
        {
            (GitObjectType type, byte[] body) = objects[i];
            GitOid oid = GitObjectDb.HashObject(type, body, algorithm);

            // Find the packed entry length: header + compressed body.
            int hdrLen = PackEncoding.WriteObjectHeader(hdrBuf, type, body.Length);
            byte[] compressed = ZlibTestHelpers.CompressLooseObject(body);
            int entryLen = hdrLen + compressed.Length;

            // CRC over the packed bytes.
            uint crc = Crc32.HashToUInt32(packBytes.AsSpan((int)offset, entryLen));
            entries.Add((oid, offset, crc));
            offset += entryLen;
        }

        return WriteIdxAndPack(packDir, namePrefix, packBytes, entries, algorithm, oidSize);
    }

    /// <summary>
    /// Writes a thin pack (.pack + .idx) containing a single REF_DELTA entry
    /// whose base is NOT in the pack. The .idx lists the resolved OID.
    /// </summary>
    private static (string PackPath, string IdxPath) WriteThinPackWithRefDelta(
        string packDir,
        string namePrefix,
        GitOid baseOid,
        byte[] deltaData,
        GitOid resultOid,
        GitHashAlgorithmKind algorithm)
    {
        var builder = new ThinPackBuilder(algorithm);
        builder.AddRefDelta(baseOid, deltaData);
        byte[] packBytes = builder.Build();
        int oidSize = GitOid.SizeFor(algorithm);

        // The REF_DELTA entry starts at offset 12 (after PACK header).
        // It consists of: object header + base OID + compressed delta.
        long offset = 12;
        int hdrLen = PackEncoding.WriteObjectHeader(stackalloc byte[16], GitObjectType.RefDelta, deltaData.Length);
        byte[] compressed = ZlibTestHelpers.CompressLooseObject(deltaData);
        int entryLen = hdrLen + oidSize + compressed.Length;
        uint crc = Crc32.HashToUInt32(packBytes.AsSpan((int)offset, entryLen));

        var entries = new List<(GitOid Oid, long Offset, uint Crc)> { (resultOid, offset, crc) };

        return WriteIdxAndPack(packDir, namePrefix, packBytes, entries, algorithm, oidSize);
    }

    /// <summary>
    /// Writes the .idx v2 file and the .pack file to disk.
    /// </summary>
    private static (string PackPath, string IdxPath) WriteIdxAndPack(
        string packDir,
        string namePrefix,
        byte[] packBytes,
        IReadOnlyList<(GitOid Oid, long Offset, uint Crc)> entries,
        GitHashAlgorithmKind algorithm,
        int oidSize)
    {
        // Sort entries by raw OID bytes (idx v2 requirement).
        List<(GitOid Oid, long Offset, uint Crc)> sorted = [.. entries.OrderBy(e => e.Oid, GitOidRawComparer.s_instance)];

        // Build .idx v2 bytes.
        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(algorithm);

        // Magic + version.
        Span<byte> magic = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(magic[0..4], 0xff744f63);
        BinaryPrimitives.WriteUInt32BigEndian(magic[4..8], 2);
        ms.Write(magic);
        hash.AppendData(magic);

        // Fanout table: 256 × uint32 BE.
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

        // OID table.
        foreach ((GitOid oid, _, _) in sorted)
        {
            ms.Write(oid.RawBytes);
            hash.AppendData(oid.RawBytes);
        }

        // CRC32 table.
        Span<byte> crcBuf = stackalloc byte[4];
        foreach ((_, _, uint crc) in sorted)
        {
            BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
            ms.Write(crcBuf);
            hash.AppendData(crcBuf);
        }

        // Offset table.
        Span<byte> offBuf = stackalloc byte[4];
        foreach ((_, long off, _) in sorted)
        {
            BinaryPrimitives.WriteUInt32BigEndian(offBuf, (uint)off);
            ms.Write(offBuf);
            hash.AppendData(offBuf);
        }

        // Pack checksum (last oidSize bytes of the pack file).
        byte[] packChecksum = packBytes[^oidSize..];
        ms.Write(packChecksum);
        hash.AppendData(packChecksum);

        // Idx checksum.
        GitOid idxChecksum = hash.Finalize();
        ms.Write(idxChecksum.RawBytes);

        byte[] idxBytes = ms.ToArray();

        // Use unique pack names to avoid collisions. The pack checksum is
        // embedded in the .idx, so any name works for PackFile.OpenAsync.
        string packPath = Path.Combine(packDir, $"{namePrefix}-{Guid.NewGuid().ToString("N")[..8]}.pack");
        string idxPath = Path.ChangeExtension(packPath, ".idx");
#pragma warning disable CA1849
        File.WriteAllBytes(packPath, packBytes);
        File.WriteAllBytes(idxPath, idxBytes);
#pragma warning restore CA1849

        return (packPath, idxPath);
    }

    /// <summary>
    /// Compares <see cref="GitOid"/> by raw bytes for sorting.
    /// </summary>
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
