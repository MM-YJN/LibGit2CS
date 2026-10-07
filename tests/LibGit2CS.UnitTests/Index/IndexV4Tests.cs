using System.Buffers.Binary;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Index;

/// <summary>
/// Tests for index format v4 path-prefix compression. Verifies
/// that <see cref="GitIndex.SetVersion(4)"/> produces a byte-exact v4 index
/// file matching <c>git -c index.version=4 update-index --add --cacheinfo</c>
/// output, and that v4 files round-trip through read/write with no data loss.
/// </summary>
public sealed class IndexV4Tests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public IndexV4Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexV4_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ===== SetVersion =====

    [Fact]
    public async Task SetVersion_V4_Succeeds()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        index.SetVersion(4);
        Assert.Equal(4, index.Version);
    }

    [Fact]
    public async Task SetVersion_V4_MarksDirty()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(index.IsDirty);
        index.SetVersion(4);
        Assert.True(index.IsDirty);
    }

    // ===== Round-trip =====

    [Fact]
    public async Task WriteV4_ReadV4_RoundTrips_Entries()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oidA = await _repo.ObjectWriteAsync(GitObjectType.Blob, "alpha\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oidB = await _repo.ObjectWriteAsync(GitObjectType.Blob, "beta\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oidC = await _repo.ObjectWriteAsync(GitObjectType.Blob, "gamma\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oidR = await _repo.ObjectWriteAsync(GitObjectType.Blob, "root\n"u8.ToArray(), TestContext.Current.CancellationToken);

        index.Add(new GitIndexEntry("dir/a.txt", oidA, GitFileMode.Regular));
        index.Add(new GitIndexEntry("dir/b.txt", oidB, GitFileMode.Regular));
        index.Add(new GitIndexEntry("dir/sub/c.txt", oidC, GitFileMode.Regular));
        index.Add(new GitIndexEntry("root.txt", oidR, GitFileMode.Regular));

        index.SetVersion(4);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(4, reloaded.Version);
        Assert.Equal(4, reloaded.EntryCount);
        Assert.Equal("dir/a.txt", reloaded.EntryByIndex(0).Path.ToUtf8String());
        Assert.Equal("dir/b.txt", reloaded.EntryByIndex(1).Path.ToUtf8String());
        Assert.Equal("dir/sub/c.txt", reloaded.EntryByIndex(2).Path.ToUtf8String());
        Assert.Equal("root.txt", reloaded.EntryByIndex(3).Path.ToUtf8String());
        Assert.Equal(oidA, reloaded.EntryByIndex(0).Id);
        Assert.Equal(oidB, reloaded.EntryByIndex(1).Id);
        Assert.Equal(oidC, reloaded.EntryByIndex(2).Id);
        Assert.Equal(oidR, reloaded.EntryByIndex(3).Id);
    }

    [Fact]
    public async Task WriteV4_FirstEntry_HasStripLengthZero()
    {
        // The first entry in a v4 index must write the full path (varint strip=0).
        // A single-entry v4 index is byte-identical to a v2 index except for the
        // version field and the absence of 8-byte alignment padding.
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry("README.md", blobOid, GitFileMode.Regular));
        index.SetVersion(4);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        byte[] bytes = await File.ReadAllBytesAsync(indexPath, cancellationToken: TestContext.Current.CancellationToken);

        // Header: DIRC + version 4 + count 1.
        Assert.Equal(0x44495243u, ReadUInt32BE(bytes, 0));
        Assert.Equal(4u, ReadUInt32BE(bytes, 4));
        Assert.Equal(1u, ReadUInt32BE(bytes, 8));

        GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal("README.md", reloaded.EntryByIndex(0).Path.ToUtf8String());
    }

    [Fact]
    public async Task WriteV4_ProducesSmallerFile_ThanV2()
    {
        // v4 prefix compression should produce a smaller file than v2 for paths
        // that share a common prefix. The savings come from (1) the stripped
        // prefix bytes and (2) the absence of 8-byte alignment padding.
        GitOid[] oids =
        [
            await _repo.ObjectWriteAsync(GitObjectType.Blob, "a\n"u8.ToArray(), TestContext.Current.CancellationToken),
            await _repo.ObjectWriteAsync(GitObjectType.Blob, "b\n"u8.ToArray(), TestContext.Current.CancellationToken),
            await _repo.ObjectWriteAsync(GitObjectType.Blob, "c\n"u8.ToArray(), TestContext.Current.CancellationToken),
            await _repo.ObjectWriteAsync(GitObjectType.Blob, "d\n"u8.ToArray(), TestContext.Current.CancellationToken),
        ];
        string[] paths = new[] { "src/foo/a.txt", "src/foo/b.txt", "src/foo/c.txt", "src/foo/d.txt" };

        // Write v2.
        GitIndex index2 = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        for (int i = 0; i < paths.Length; i++)
        {
            index2.Add(new GitIndexEntry(paths[i], oids[i], GitFileMode.Regular));
        }
        index2.SetVersion(2);
        await index2.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        long v2Size = new FileInfo(Path.Combine(_repo.Path, "index")).Length;

        // Write v4 (fresh in-memory index).
        var index4 = GitIndex.New(GitHashAlgorithmKind.Sha1);
        for (int i = 0; i < paths.Length; i++)
        {
            index4.Add(new GitIndexEntry(paths[i], oids[i], GitFileMode.Regular));
        }
        index4.SetVersion(4);
        // SerializeForWrite is internal; serialize into a pooled writer.
        using var v4Writer = new PooledByteBufferWriter();
        index4.SerializeForWrite(v4Writer);
        long v4Size = v4Writer.WrittenCount;

        Assert.True(v4Size < v2Size,
            $"v4 ({v4Size}) should be smaller than v2 ({v2Size}) for shared-prefix paths");
    }

    // ===== Cross-version equality =====

    [Fact]
    public async Task WriteV2_AndV4_ProduceSameEntries_AfterRead()
    {
        GitOid[] oids =
        [
            await _repo.ObjectWriteAsync(GitObjectType.Blob, "alpha\n"u8.ToArray(), TestContext.Current.CancellationToken),
            await _repo.ObjectWriteAsync(GitObjectType.Blob, "beta\n"u8.ToArray(), TestContext.Current.CancellationToken),
            await _repo.ObjectWriteAsync(GitObjectType.Blob, "gamma\n"u8.ToArray(), TestContext.Current.CancellationToken),
            await _repo.ObjectWriteAsync(GitObjectType.Blob, "root\n"u8.ToArray(), TestContext.Current.CancellationToken),
        ];
        string[] paths = new[] { "dir/a.txt", "dir/b.txt", "dir/sub/c.txt", "root.txt" };

        // Write v2.
        GitIndex index2 = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        for (int i = 0; i < paths.Length; i++)
        {
            index2.Add(new GitIndexEntry(paths[i], oids[i], GitFileMode.Regular));
        }
        index2.SetVersion(2);
        await index2.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        string v2Path = Path.Combine(_repo.Path, "index");
        GitIndex v2Read = await GitIndex.OpenAsync(v2Path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // Write v4 to a separate file.
        var index4 = GitIndex.New(GitHashAlgorithmKind.Sha1);
        for (int i = 0; i < paths.Length; i++)
        {
            index4.Add(new GitIndexEntry(paths[i], oids[i], GitFileMode.Regular));
        }
        index4.SetVersion(4);
        string v4Path = Path.Combine(_tempDir, "v4.index");
        using (var writer = new PooledByteBufferWriter())
        {
            index4.SerializeForWrite(writer);
            await File.WriteAllBytesAsync(v4Path, writer.WrittenMemory, cancellationToken: TestContext.Current.CancellationToken);
        }

        GitIndex v4Read = await GitIndex.OpenAsync(v4Path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(v2Read.EntryCount, v4Read.EntryCount);
        for (int i = 0; i < v2Read.EntryCount; i++)
        {
            Assert.Equal(v2Read.EntryByIndex(i).Path, v4Read.EntryByIndex(i).Path);
            Assert.Equal(v2Read.EntryByIndex(i).Id, v4Read.EntryByIndex(i).Id);
            Assert.Equal(v2Read.EntryByIndex(i).Mode, v4Read.EntryByIndex(i).Mode);
        }
    }

    // ===== Byte-exact vs git-generated fixture =====

    [Fact]
    public void WriteV4_ByteExact_MatchesGitGeneratedFixture()
    {
        // The fixture was generated by:
        //   git init && git config index.version 4
        //   oid_a=$(printf 'alpha\n' | git hash-object -w --stdin)
        //   git update-index --add --cacheinfo 100644,$oid_a,dir/a.txt
        //   ... (dir/b.txt, dir/sub/c.txt, root.txt)
        // All stat fields are zero (--cacheinfo skips workdir stat). No TREE
        // extension (no commit). The trailing 20 bytes are the SHA-1 checksum.
        byte[] fixtureBytes = FixtureLoader.LoadBytes("Fixtures.index.v4_deterministic");
        Assert.Equal(319, fixtureBytes.Length);

        // Reconstruct the same index in C# with zero stat fields and the
        // exact OIDs from the fixture.
        var oidA = GitOid.Parse("4a58007052a65fbc2fc3f910f2855f45a4058e74", GitHashAlgorithmKind.Sha1);
        var oidB = GitOid.Parse("65b2df87f7df3aeedef04be96703e55ac19c2cfb", GitHashAlgorithmKind.Sha1);
        var oidC = GitOid.Parse("af17f6cc87e4d5e4adec0018cbb73d3e2bd008c8", GitHashAlgorithmKind.Sha1);
        var oidR = GitOid.Parse("d8649da39ddf7910d29982e2f19cd9c0ff5ffe96", GitHashAlgorithmKind.Sha1);

        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        index.Add(new GitIndexEntry
        {
            Path = GitPath.FromUtf8String("dir/a.txt"),
            Id = oidA,
            Mode = GitFileMode.Regular,
            Ctime = default,
            Mtime = default,
            Dev = 0,
            Ino = 0,
            Uid = 0,
            Gid = 0,
            FileSize = 0,
            Flags = 0,
            FlagsExtended = 0,
        });
        index.Add(new GitIndexEntry
        {
            Path = GitPath.FromUtf8String("dir/b.txt"),
            Id = oidB,
            Mode = GitFileMode.Regular,
            Ctime = default,
            Mtime = default,
            Dev = 0,
            Ino = 0,
            Uid = 0,
            Gid = 0,
            FileSize = 0,
            Flags = 0,
            FlagsExtended = 0,
        });
        index.Add(new GitIndexEntry
        {
            Path = GitPath.FromUtf8String("dir/sub/c.txt"),
            Id = oidC,
            Mode = GitFileMode.Regular,
            Ctime = default,
            Mtime = default,
            Dev = 0,
            Ino = 0,
            Uid = 0,
            Gid = 0,
            FileSize = 0,
            Flags = 0,
            FlagsExtended = 0,
        });
        index.Add(new GitIndexEntry
        {
            Path = GitPath.FromUtf8String("root.txt"),
            Id = oidR,
            Mode = GitFileMode.Regular,
            Ctime = default,
            Mtime = default,
            Dev = 0,
            Ino = 0,
            Uid = 0,
            Gid = 0,
            FileSize = 0,
            Flags = 0,
            FlagsExtended = 0,
        });
        index.SetVersion(4);

        using var produced = new PooledByteBufferWriter();
        index.SerializeForWrite(produced);
        Assert.Equal(fixtureBytes.Length, produced.WrittenCount);
        Assert.Equal(fixtureBytes, produced.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task ReadV4_Fixture_ParsesCorrectly()
    {
        byte[] fixtureBytes = FixtureLoader.LoadBytes("Fixtures.index.v4_deterministic");
        string tmpPath = Path.Combine(_tempDir, "fixture.index");
        await File.WriteAllBytesAsync(tmpPath, fixtureBytes, cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await GitIndex.OpenAsync(tmpPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(4, index.Version);
        Assert.Equal(4, index.EntryCount);

        Assert.Equal("dir/a.txt", index.EntryByIndex(0).Path.ToUtf8String());
        Assert.Equal("dir/b.txt", index.EntryByIndex(1).Path.ToUtf8String());
        Assert.Equal("dir/sub/c.txt", index.EntryByIndex(2).Path.ToUtf8String());
        Assert.Equal("root.txt", index.EntryByIndex(3).Path.ToUtf8String());

        Assert.Equal(GitOid.Parse("4a58007052a65fbc2fc3f910f2855f45a4058e74", GitHashAlgorithmKind.Sha1),
            index.EntryByIndex(0).Id);
        Assert.Equal(GitOid.Parse("65b2df87f7df3aeedef04be96703e55ac19c2cfb", GitHashAlgorithmKind.Sha1),
            index.EntryByIndex(1).Id);
        Assert.Equal(GitOid.Parse("af17f6cc87e4d5e4adec0018cbb73d3e2bd008c8", GitHashAlgorithmKind.Sha1),
            index.EntryByIndex(2).Id);
        Assert.Equal(GitOid.Parse("d8649da39ddf7910d29982e2f19cd9c0ff5ffe96", GitHashAlgorithmKind.Sha1),
            index.EntryByIndex(3).Id);
    }

    // ===== v4 with extended flags =====

    [Fact]
    public async Task WriteV4_WithExtendedFlags_RoundTrips()
    {
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "content\n"u8.ToArray(), TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry
        {
            Path = GitPath.FromUtf8String("dir/file.txt"),
            Id = blobOid,
            Mode = GitFileMode.Regular,
            Flags = GitIndexEntry.Extended,
            FlagsExtended = GitIndexEntry.SkipWorktree,
        });
        index.SetVersion(4);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        string indexPath = Path.Combine(_repo.Path, "index");
        GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(4, reloaded.Version);
        Assert.Equal(1, reloaded.EntryCount);
        GitIndexEntry entry = reloaded.EntryByIndex(0);
        Assert.Equal("dir/file.txt", entry.Path.ToUtf8String());
        Assert.Equal(blobOid, entry.Id);
        Assert.True(entry.HasExtended);
        Assert.True(entry.SkipWorktreeFlag);
    }

    // ===== Error cases =====

    [Fact]
    public async Task ReadV4_BadStripLength_Throws()
    {
        // Hand-craft a v4 index with a strip length that exceeds the previous
        // entry's path length.
        // Entry 1: path "a" (valid, strip=0).
        // Entry 2: varint strip=5 (but last path is only 1 byte).
        byte[] bytes = BuildHandCraftedV4WithBadStrip();
        string tmpPath = Path.Combine(_tempDir, "bad.index");
        await File.WriteAllBytesAsync(tmpPath, bytes, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await GitIndex.OpenAsync(tmpPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
    }

    // ===== Helpers =====

    private static uint ReadUInt32BE(byte[] b, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset, 4));

    /// <summary>
    /// Builds a minimal v4 index with two entries where the second entry's
    /// varint strip length (5) exceeds the first entry's path length (1).
    /// The trailing checksum is all-zero (which ParseIndex accepts as skipHash).
    /// </summary>
    private static byte[] BuildHandCraftedV4WithBadStrip()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // Header: DIRC, version 4, count 2.
        bw.Write([0x44, 0x49, 0x52, 0x43]);
        bw.Write([0x00, 0x00, 0x00, 0x04]);
        bw.Write([0x00, 0x00, 0x00, 0x02]);

        // Entry 1: all-zero common fields (40B) + zero OID (20B) + flags=0x0001 (pathlen 1) + varint 0x00 (strip 0) + "a" + NUL.
        bw.Write(new byte[40]); // common fields all zero
        bw.Write(new byte[20]); // OID all zero
        bw.Write([0x00, 0x01]); // flags = 1 (namelen=1)
        bw.Write([0x00]); // varint strip = 0
        bw.Write([(byte)'a', 0x00]); // "a" + NUL

        // Entry 2: all-zero common + zero OID + flags=0x0001 + varint 0x05 (strip 5) + "b" + NUL.
        bw.Write(new byte[40]);
        bw.Write(new byte[20]);
        bw.Write([0x00, 0x01]);
        bw.Write([0x05]); // varint strip = 5 (> last.Length=1)
        bw.Write([(byte)'b', 0x00]);

        // Trailing 20-byte all-zero checksum (skipHash — accepted by VerifyChecksum).
        bw.Write(new byte[20]);

        return ms.ToArray();
    }
}
