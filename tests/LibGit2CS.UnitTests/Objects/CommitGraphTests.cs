using System.Buffers.Binary;
using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class CommitGraphTests : IDisposable
{
    private readonly string _tempDir;

    public CommitGraphTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CommitGraphTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task Open_MissingFile_ReturnsNull()
    {
        CommitGraph? cg = await CommitGraph.OpenAsync(_tempDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Null(cg);
    }

    [Fact]
    public async Task OpenAndFindEntry_SyntheticSingleCommit_ReturnsEntry()
    {
        // Build a minimal commit-graph for SHA-1 with one commit.
        int oidSize = 20;
        var commit = GitOid.Parse("0123456789abcdef0123456789abcdef01234567".AsSpan(), GitHashAlgorithmKind.Sha1);
        var tree = GitOid.Parse("fedcba9876543210fedcba9876543210fedcba98".AsSpan(), GitHashAlgorithmKind.Sha1);

        byte[] data = BuildMinimalCommitGraph(oidSize, commit, tree, generation: 1, commitTime: 1234567890L);

        string objectsDir = Path.Combine(_tempDir, "objects", "info");
        Directory.CreateDirectory(objectsDir);
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "objects", "info", "commit-graph"), data, cancellationToken: TestContext.Current.CancellationToken);

        CommitGraph? cg = await CommitGraph.OpenAsync(Path.Combine(_tempDir, "objects"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(cg);
        Assert.Equal(1, cg!.NumCommits);

        CommitGraphEntry? entry = cg.FindEntry(commit);
        Assert.NotNull(entry);
        CommitGraphEntry e = entry!.Value;
        Assert.Equal(tree, e.TreeOid);
        Assert.Equal(1u, e.Generation);
        Assert.Equal(1234567890L, e.CommitTime);
        Assert.Equal(0, e.ParentCount);
    }

    [Fact]
    public async Task FindEntry_NotPresent_ReturnsNull()
    {
        int oidSize = 20;
        var commit = GitOid.Parse("0123456789abcdef0123456789abcdef01234567".AsSpan(), GitHashAlgorithmKind.Sha1);
        var tree = GitOid.Parse("fedcba9876543210fedcba9876543210fedcba98".AsSpan(), GitHashAlgorithmKind.Sha1);

        byte[] data = BuildMinimalCommitGraph(oidSize, commit, tree, generation: 1, commitTime: 100L);
        Directory.CreateDirectory(Path.Combine(_tempDir, "objects", "info"));
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "objects", "info", "commit-graph"), data, cancellationToken: TestContext.Current.CancellationToken);

        CommitGraph cg = (await CommitGraph.OpenAsync(Path.Combine(_tempDir, "objects"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken))!;
        var other = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);
        Assert.Null(cg.FindEntry(other));
    }

    /// <summary>Builds a minimal single-commit commit-graph buffer (v1, SHA-1).</summary>
    private static byte[] BuildMinimalCommitGraph(int oidSize, GitOid commit, GitOid tree, uint generation, long commitTime)
    {
        int checksumSize = oidSize;
        // Chunks: OIDF (1024), OIDL (oidSize), CDAT (oidSize+16). No EDGE.
        int numChunks = 3;
        int headerSize = 8 + (1 + numChunks) * 12;
        int oidfSize = 256 * 4;
        int oidlSize = oidSize;
        int cdatSize = oidSize + 16;

        int oidfOff = headerSize;
        int oidlOff = oidfOff + oidfSize;
        int cdatOff = oidlOff + oidlSize;
        int trailerOff = cdatOff + cdatSize;
        int totalSize = trailerOff + checksumSize;

        byte[] buf = new byte[totalSize];
        var ms = new MemoryStream(buf, 0, totalSize, writable: true);

        // Header: "CGPH", v1, oid_v1, numChunks=3, baseGraphs=0.
        ms.WriteByte((byte)'C');
        ms.WriteByte((byte)'G');
        ms.WriteByte((byte)'P');
        ms.WriteByte((byte)'H');
        ms.WriteByte(1);
        ms.WriteByte(1);
        ms.WriteByte((byte)numChunks);
        ms.WriteByte(0);

        // Chunk table (3 chunks + sentinel). Each: id(4 BE) + offset(8 BE).
        WriteChunkEntry(ms, 0x4F494446, oidfOff); // "OIDF"
        WriteChunkEntry(ms, 0x4F49444C, oidlOff); // "OIDL"
        WriteChunkEntry(ms, 0x43444154, cdatOff); // "CDAT"
        WriteChunkEntry(ms, 0, trailerOff);        // sentinel

        // OIDF: 256 cumulative entries (BE). Commit's first byte is 0x01, so
        // fanout[0]=0 and fanout[1..255]=1.
        byte firstByte = commit.RawBytes[0];
        for (int i = 0; i < 256; i++)
        {
            WriteUInt32BE(ms, i < firstByte ? 0u : 1u);
        }

        // OIDL: the commit's raw OID.
        ms.Write(commit.RawBytes.ToArray(), 0, oidSize);

        // CDAT: tree_oid + parent1(MISSING) + parent2(MISSING) + gen+timehi + timelo.
        ms.Write(tree.RawBytes.ToArray(), 0, oidSize);
        WriteUInt32BE(ms, 0x70000000u); // parent1 missing
        WriteUInt32BE(ms, 0x70000000u); // parent2 missing

        // generation occupies high 30 bits; time gets low 2 bits of this word.
        uint timeHi = (uint)((commitTime >> 32) & 0x3);
        uint genWord = ((uint)generation << 2) | timeHi;
        WriteUInt32BE(ms, genWord);
        WriteUInt32BE(ms, (uint)(commitTime & 0xFFFFFFFFu));

        // Trailer checksum: SHA-1 of all preceding bytes (the parser now
        // validates it, matching git_commit_graph_validate).
        ms.Write(new byte[checksumSize], 0, checksumSize);
        byte[] hash = SHA1.HashData(buf.AsSpan(0, buf.Length - checksumSize));
        hash.CopyTo(buf, buf.Length - checksumSize);

        return buf;
    }

    private static void WriteChunkEntry(Stream ms, uint id, long offset)
    {
        WriteUInt32BE(ms, id);
        // offset as 8 bytes big-endian.
        for (int shift = 56; shift >= 0; shift -= 8)
        {
            ms.WriteByte((byte)((offset >> shift) & 0xFF));
        }
    }

    private static void WriteUInt32BE(Stream ms, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        ms.Write(buf);
    }
}
