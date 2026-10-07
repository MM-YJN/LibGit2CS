using System.Buffers.Binary;
using System.IO.Hashing;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Integration tests for cross-pack REF_DELTA resolution through the full
/// <see cref="GitRepository"/> → <see cref="GitObjectDb"/> →
/// <see cref="PackObjectBackend"/> → <see cref="PackFile"/> pipeline.
/// </summary>
/// <remarks>
/// These tests simulate the scenario discovered in the VS Code repo: a repo
/// containing corrupt packs (left by failed fetches) whose REF_DELTA entries
/// reference bases in other packs. The cross-pack resolver callback (wired by
/// <see cref="PackObjectBackend.RefreshAsync"/>) allows these objects to be
/// read without throwing, so fetches succeed.
/// </remarks>
public sealed class CrossPackIntegrationTests
{
    /// <summary>Converts a filesystem path to a <c>file:///</c> URL.</summary>
    private static string FileUrl(string path)
    {
        string posix = path.Replace('\\', '/');
        return posix.StartsWith('/') ? $"file://{posix}" : $"file:///{posix}";
    }

    /// <summary>
    /// Opens a repo whose pack directory contains two packs: one with a base
    /// blob, and a thin pack with a REF_DELTA against that blob. Looking up the
    /// delta OID through <see cref="GitObjectDb.LookupAsync"/> should resolve
    /// via the cross-pack resolver.
    /// </summary>
    [Fact]
    public async Task Lookup_ObjectInCorruptPack_ResolvesViaCrossPackResolver()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-xpack-lookup-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] baseBody = "cross-pack integration base\n"u8.ToArray();
            byte[] suffix = "extra line\n"u8.ToArray();
            byte[] resultBody = [.. baseBody, .. suffix];

            GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
            GitOid resultOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

            await using (GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct))
            {
                string packDir = System.IO.Path.Combine(repo.Path, "objects", "pack");

                // Pack B: base object as a full entry.
                CrossPackTestHelper.WritePackWithFullObjects(packDir, "pack-base",
                    [(GitObjectType.Blob, baseBody)], GitHashAlgorithmKind.Sha1);

                // Pack A: thin pack with a REF_DELTA against baseOid.
                byte[] delta = BuildCopyAppendDelta(baseBody, suffix);
                CrossPackTestHelper.WriteThinPackWithRefDelta(packDir, "pack-thin",
                    baseOid, delta, resultOid, GitHashAlgorithmKind.Sha1);
            }

            // Re-open the repo — the PackObjectBackend loads both packs and
            // wires the cross-pack resolver.
            await using GitRepository repo2 = await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: ct);

            GitObject? obj = await repo2.ObjectLookupAsync(resultOid, ct);
            Assert.NotNull(obj);
            Assert.Equal(GitObjectType.Blob, obj!.Type);
            Assert.Equal(resultBody, obj.Raw.ToArray());
            obj.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { }
        }
    }

    /// <summary>
    /// A repo with a corrupt pack (cross-pack REF_DELTA) can still fetch from
    /// a source. The fetch's thin-pack fixup calls <c>InjectObjectAsync</c>
    /// which reads bases from the ODB; the cross-pack resolver ensures the
    /// corrupt pack doesn't cause lookup failures.
    /// </summary>
    [Fact]
    public async Task Fetch_RepoWithCorruptPack_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sourcePath = Path.Combine(Path.GetTempPath(), "libgit2cs-xpack-src-" + Guid.NewGuid().ToString("N"));
        string clientPath = Path.Combine(Path.GetTempPath(), "libgit2cs-xpack-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sig = new GitSignature("Test", "test@test.com", new GitTime(1700000000, 0));

            // Build source with one commit.
            GitOid commitA;
            byte[] blobABody = "initial content\n"u8.ToArray();
            GitOid blobAOid = GitObjectDb.HashObject(GitObjectType.Blob, blobABody, GitHashAlgorithmKind.Sha1);
            await using (GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: true, new GitContext(), cancellationToken: ct))
            {
                _ = await source.ObjectWriteAsync(GitObjectType.Blob, blobABody, ct);
                using GitTreeBuilder treeBld = source.NewTreeBuilder();
                await treeBld.InsertAsync("file.txt", blobAOid, GitFileMode.Regular, ct);
                GitOid treeOid = await treeBld.WriteAsync(ct);
                commitA = await source.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = sig,
                    Committer = sig,
                    Message = "initial\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            // Clone the source to the client (local clone, no fast-path).
            GitCloneOptions cloneOpts = new() { CloneLocal = GitCloneLocal.NoLocal };
            await using (GitRepository client = await GitClone.RunAsync(
                FileUrl(sourcePath), clientPath, cloneOpts, new GitContext(), ct))
            {
                // Write a corrupt pack into the client: a thin pack with a
                // REF_DELTA whose base is in another pack. This simulates
                // leftover corrupt packs from failed fetches. The base is
                // written as a separate pack (not a loose object) because the
                // cross-pack resolver searches packs only.
                string packDir = System.IO.Path.Combine(client.Path, "objects", "pack");

                byte[] corruptBaseBody = "corrupt pack base\n"u8.ToArray();
                byte[] corruptSuffix = "appended\n"u8.ToArray();
                byte[] corruptResultBody = [.. corruptBaseBody, .. corruptSuffix];

                GitOid corruptBaseOid = GitObjectDb.HashObject(GitObjectType.Blob, corruptBaseBody, GitHashAlgorithmKind.Sha1);
                GitOid corruptResultOid = GitObjectDb.HashObject(GitObjectType.Blob, corruptResultBody, GitHashAlgorithmKind.Sha1);

                // Write the base as a separate pack.
                CrossPackTestHelper.WritePackWithFullObjects(packDir, "pack-xpack-base",
                    [(GitObjectType.Blob, corruptBaseBody)], GitHashAlgorithmKind.Sha1);

                // Write the corrupt thin pack alongside the valid packs.
                byte[] deltaData = BuildCopyAppendDelta(corruptBaseBody, corruptSuffix);
                CrossPackTestHelper.WriteThinPackWithRefDelta(packDir, "pack-corrupt",
                    corruptBaseOid, deltaData, corruptResultOid, GitHashAlgorithmKind.Sha1);
            }

            // Add a second commit to the source.
            byte[] blobBBody = "second file\n"u8.ToArray();
            GitOid blobBOid = GitObjectDb.HashObject(GitObjectType.Blob, blobBBody, GitHashAlgorithmKind.Sha1);
            await using (GitRepository source2 = await GitRepository.OpenAsync(sourcePath, new GitContext(), cancellationToken: ct))
            {
                _ = await source2.ObjectWriteAsync(GitObjectType.Blob, blobBBody, ct);
                using GitTreeBuilder treeBld2 = source2.NewTreeBuilder();
                await treeBld2.InsertAsync("file.txt", blobAOid, GitFileMode.Regular, ct);
                await treeBld2.InsertAsync("file2.txt", blobBOid, GitFileMode.Regular, ct);
                GitOid tree2Oid = await treeBld2.WriteAsync(ct);
                _ = await source2.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = tree2Oid,
                    Parents = [commitA],
                    Author = sig,
                    Committer = sig,
                    Message = "second\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            // Re-open the client and fetch — the corrupt pack must not prevent
            // the fetch from completing.
            await using (GitRepository client2 = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct))
            {
                GitRemote remote = await client2.RemoteLookupAsync("origin", ct);
                await remote.FetchAsync(cancellationToken: ct);
            }

            // Verify the second commit's blob is accessible in the client.
            await using GitRepository client3 = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct);
            Assert.True(await client3.Objects.ExistsAsync(blobBOid, ct));
        }
        finally
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch { }

            try
            {
                Directory.Delete(clientPath, recursive: true);
            }
            catch { }
        }
    }

    // ── Delta helpers ─────────────────────────────────────────────────

    private static byte[] BuildCopyAppendDelta(byte[] baseBody, byte[] suffix)
    {
        var delta = new List<byte>();
        int resultLen = baseBody.Length + suffix.Length;
        WriteVarint(delta, baseBody.Length);
        WriteVarint(delta, resultLen);

        if (baseBody.Length > 0)
        {
            WriteCopyAll(delta, baseBody.Length);
        }

        WriteInsert(delta, suffix);
        return delta.ToArray();
    }

    private static void WriteVarint(List<byte> output, long value)
    {
        while (value > 0x7F)
        {
            output.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        output.Add((byte)(value & 0x7F));
    }

    private static void WriteCopyAll(List<byte> output, int size)
    {
        if (size < 256)
        {
            output.Add(0x90);
            output.Add((byte)size);
        }
        else if (size < 65536)
        {
            output.Add(0xB0);
            output.Add((byte)(size & 0xFF));
            output.Add((byte)((size >> 8) & 0xFF));
        }
        else
        {
            throw new NotSupportedException($"test COPY size {size} too large");
        }
    }

    private static void WriteInsert(List<byte> output, byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int chunk = Math.Min(data.Length - offset, 127);
            output.Add((byte)chunk);
            for (int i = 0; i < chunk; i++)
            {
                output.Add(data[offset + i]);
            }

            offset += chunk;
        }
    }
}

/// <summary>
/// Test helper that writes pack files (.pack + .idx v2) with cross-pack
/// REF_DELTA entries. Used by <see cref="CrossPackIntegrationTests"/>.
/// </summary>
internal static class CrossPackTestHelper
{
    public static void WritePackWithFullObjects(
        string packDir,
        string namePrefix,
        IReadOnlyList<(GitObjectType Type, byte[] Body)> objects,
        GitHashAlgorithmKind algorithm)
    {
        Directory.CreateDirectory(packDir);
        int oidSize = GitOid.SizeFor(algorithm);

        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(algorithm);

        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)objects.Count);
        ms.Write(header);
        hash.AppendData(header);

        var entries = new List<(GitOid Oid, long Offset, uint Crc)>();
        Span<byte> hdrBuf = stackalloc byte[16];
        long offset = 12;

        foreach ((GitObjectType type, byte[] body) in objects)
        {
            GitOid oid = GitObjectDb.HashObject(type, body, algorithm);
            int hdrLen = PackEncoding.WriteObjectHeader(hdrBuf, type, body.Length);
            ms.Write(hdrBuf[..hdrLen].ToArray());
            hash.AppendData(hdrBuf[..hdrLen]);

            byte[] compressed = ZlibTestHelpers.CompressLooseObject(body);
            ms.Write(compressed);
            hash.AppendData(compressed);

            entries.Add((oid, offset, 0));
            offset += hdrLen + compressed.Length;
        }

        GitOid trailer = hash.Finalize();
        ms.Write(trailer.RawBytes);
        byte[] packBytes = ms.ToArray();

        for (int i = 0; i < entries.Count; i++)
        {
            (GitOid oid, long off, _) = entries[i];
            long nextOff = i + 1 < entries.Count ? entries[i + 1].Offset : packBytes.Length - oidSize;
            uint crc = Crc32.HashToUInt32(packBytes.AsSpan((int)off, (int)(nextOff - off)));
            entries[i] = (oid, off, crc);
        }

        WriteIdxAndPack(packDir, namePrefix, packBytes, entries, algorithm, oidSize);
    }

    public static void WriteThinPackWithRefDelta(
        string packDir,
        string namePrefix,
        GitOid baseOid,
        byte[] deltaData,
        GitOid resultOid,
        GitHashAlgorithmKind algorithm)
    {
        Directory.CreateDirectory(packDir);
        int oidSize = GitOid.SizeFor(algorithm);

        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(algorithm);

        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], 1);
        ms.Write(header);
        hash.AppendData(header);

        long offset = 12;
        Span<byte> hdrBuf = stackalloc byte[16];
        int hdrLen = PackEncoding.WriteObjectHeader(hdrBuf, GitObjectType.RefDelta, deltaData.Length);
        ms.Write(hdrBuf[..hdrLen].ToArray());
        hash.AppendData(hdrBuf[..hdrLen]);

        ms.Write(baseOid.RawBytes);
        hash.AppendData(baseOid.RawBytes);

        byte[] compressed = ZlibTestHelpers.CompressLooseObject(deltaData);
        ms.Write(compressed);
        hash.AppendData(compressed);

        GitOid trailer = hash.Finalize();
        ms.Write(trailer.RawBytes);
        byte[] packBytes = ms.ToArray();

        int entryLen = hdrLen + oidSize + compressed.Length;
        uint crc = Crc32.HashToUInt32(packBytes.AsSpan((int)offset, entryLen));
        var entries = new List<(GitOid Oid, long Offset, uint Crc)> { (resultOid, offset, crc) };

        WriteIdxAndPack(packDir, namePrefix, packBytes, entries, algorithm, oidSize);
    }

    private static void WriteIdxAndPack(
        string packDir, string namePrefix, byte[] packBytes,
        IReadOnlyList<(GitOid Oid, long Offset, uint Crc)> entries,
        GitHashAlgorithmKind algorithm, int oidSize)
    {
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
        Span<byte> buf4 = stackalloc byte[4];
        for (int i = 0; i < 256; i++)
        {
            cumulative += byteCounts[i];
            BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)cumulative);
            ms.Write(buf4);
            hash.AppendData(buf4);
        }

        foreach ((GitOid oid, _, _) in sorted)
        {
            ms.Write(oid.RawBytes);
            hash.AppendData(oid.RawBytes);
        }

        foreach ((_, _, uint crc) in sorted)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf4, crc);
            ms.Write(buf4);
            hash.AppendData(buf4);
        }

        foreach ((_, long off, _) in sorted)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)off);
            ms.Write(buf4);
            hash.AppendData(buf4);
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

    private sealed class OidRawComparer : IComparer<GitOid>
    {
        internal static readonly OidRawComparer s_instance = new();

        public int Compare(GitOid x, GitOid y) => x.RawBytes.SequenceCompareTo(y.RawBytes);
    }
}
