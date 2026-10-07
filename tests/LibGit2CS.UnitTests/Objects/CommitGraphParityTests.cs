using System.Buffers.Binary;
using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

/// <summary> Parity tests for the commit-graph reader: out-of-range parent/edge indices must behave like C — the EDGE-list position check fails
/// <c>git_commit_graph_entry_find</c> (caller falls back to the ODB, <c>FindEntry</c> returns null), while an out-of-range parent index fails with
/// <c>GIT_ENOTFOUND</c> "commit index %zu does not exist" (commit_graph.c:499-502, entry_parent) — never an unmanaged <see
/// cref="ArgumentOutOfRangeException"/>. </summary>
public sealed class CommitGraphParityTests : IDisposable
{
    private const uint MissingParent = 0x70000000u;

    private readonly string _tempDir;

    public CommitGraphParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CommitGraphParity_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── parent index >= num_commits → GIT_ENOTFOUND, not a crash ─

    [Fact]
    public async Task FindEntry_ParentIndexOutOfRange_ThrowsNotFound()
    {
        // c0's CDAT p0 = 5 with num_commits = 2: C's
        // git_commit_graph_entry_parent → git_commit_graph_entry_get_byindex
        // returns GIT_ENOTFOUND "commit index 5 does not exist"
        // (commit_graph.c:499-502); the revwalk fails with that error.
        GitOid c0 = Oid(0x01);
        GitOid c1 = Oid(0x02);

        byte[] data = BuildGraph(
            [c0, c1],
            [(5, MissingParent), (MissingParent, MissingParent)],
            null);

        CommitGraph cg = await OpenAsync(data);

        GitException ex = Assert.Throws<GitException>(() => cg.FindEntry(c0));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("commit index 5 does not exist", ex.Message);
    }

    [Fact]
    public async Task FindEntry_SecondParentIndexOutOfRange_ThrowsNotFound()
    {
        // c0's CDAT p1 = 7 with num_commits = 2.
        GitOid c0 = Oid(0x01);
        GitOid c1 = Oid(0x02);

        byte[] data = BuildGraph(
            [c0, c1],
            [(1, 7), (MissingParent, MissingParent)],
            null);

        CommitGraph cg = await OpenAsync(data);

        GitException ex = Assert.Throws<GitException>(() => cg.FindEntry(c0));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("commit index 7 does not exist", ex.Message);
    }

    [Fact]
    public async Task FindEntry_MissingParentWithOneParent_ThrowsNotFound()
    {
        // Crafted: p0 = MISSING but p1 != MISSING → parent_count == 1; C
        // decodes parent 0 from parent_indices[0] (the raw MISSING value)
        // → "commit index 1879048192 does not exist".
        GitOid c0 = Oid(0x01);
        GitOid c1 = Oid(0x02);

        byte[] data = BuildGraph(
            [c0, c1],
            [(MissingParent, 1), (MissingParent, MissingParent)],
            null);

        CommitGraph cg = await OpenAsync(data);

        GitException ex = Assert.Throws<GitException>(() => cg.FindEntry(c0));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("commit index 1879048192 does not exist", ex.Message);
    }

    [Fact]
    public async Task FindEntry_EdgeFlaggedSingleParentCount_ThrowsNotFound()
    {
        // Crafted: p1 edge-flagged but the EDGE list's first entry has its
        // high bit set → parent_count == 2; C's entry_parent(n==1) decodes
        // parent_indices[1] raw (0x80000000|pos) → out of range → ENOTFOUND.
        GitOid c0 = Oid(0x01);
        GitOid c1 = Oid(0x02);

        byte[] data = BuildGraph(
            [c0, c1],
            [(1, 0x80000000u | 0), (MissingParent, MissingParent)],
            [0x80000000u | 1]); // single edge entry, high bit set

        CommitGraph cg = await OpenAsync(data);

        GitException ex = Assert.Throws<GitException>(() => cg.FindEntry(c0));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── EDGE-list position out of range → FindEntry null (ODB fallback) ──

    [Fact]
    public async Task FindEntry_EdgeListPositionOutOfRange_ReturnsNull()
    {
        // c0's p1 edge-flagged with edgePos 3 but num_extra_edge_list == 1:
        // C's git_commit_graph_entry_find fails ("commit %u does not exist",
        // GIT_ENOTFOUND) and git_commit_list_parse falls back to the ODB.
        GitOid c0 = Oid(0x01);
        GitOid c1 = Oid(0x02);

        byte[] data = BuildGraph(
            [c0, c1],
            [(1, 0x80000000u | 3), (MissingParent, MissingParent)],
            [0x80000000u | 1]);

        CommitGraph cg = await OpenAsync(data);

        Assert.Null(cg.FindEntry(c0));
    }

    // ── Regression: valid graphs still decode ─────────────────────────────

    [Fact]
    public async Task FindEntry_NormalParentChain_Decodes()
    {
        GitOid c0 = Oid(0x01);
        GitOid c1 = Oid(0x02);

        byte[] data = BuildGraph(
            [c0, c1],
            [(1, MissingParent), (MissingParent, MissingParent)],
            null);

        CommitGraph cg = await OpenAsync(data);

        CommitGraphEntry? e0 = cg.FindEntry(c0);
        Assert.NotNull(e0);
        Assert.Equal(1, e0!.Value.ParentCount);
        Assert.Equal(c1, e0.Value.Parents[0]);

        CommitGraphEntry? e1 = cg.FindEntry(c1);
        Assert.NotNull(e1);
        Assert.Equal(0, e1!.Value.ParentCount);
    }

    [Fact]
    public async Task FindEntry_OctopusEdgeList_Decodes()
    {
        // 3 commits; c0 has parents c1, c2, c1 via the EDGE list
        // [2 (clear), 1 (set)] → parent_count == 3.
        GitOid c0 = Oid(0x01);
        GitOid c1 = Oid(0x02);
        GitOid c2 = Oid(0x03);

        byte[] data = BuildGraph(
            [c0, c1, c2],
            [(1, 0x80000000u | 0), (MissingParent, MissingParent), (MissingParent, MissingParent)],
            [2, 0x80000000u | 1]);

        CommitGraph cg = await OpenAsync(data);

        CommitGraphEntry? e0 = cg.FindEntry(c0);
        Assert.NotNull(e0);
        Assert.Equal(3, e0!.Value.ParentCount);
        Assert.Equal(c1, e0.Value.Parents[0]);
        Assert.Equal(c2, e0.Value.Parents[1]);
        Assert.Equal(c1, e0.Value.Parents[2]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static GitOid Oid(byte firstByte)
    {
        Span<byte> raw = stackalloc byte[20];
        raw[0] = firstByte;
        for (int i = 1; i < 20; i++)
        {
            raw[i] = (byte)(firstByte * 17 + i);
        }

        return GitOid.FromRaw(raw, GitHashAlgorithmKind.Sha1);
    }

    private async ValueTask<CommitGraph> OpenAsync(byte[] data)
    {
        string dir = Path.Combine(_tempDir, "objects", "info");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "commit-graph"), data, cancellationToken: TestContext.Current.CancellationToken);
        return (await CommitGraph.OpenAsync(Path.Combine(_tempDir, "objects"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Builds a v1 SHA-1 commit-graph with <paramref name="commits"/> and the
    /// per-commit (p0, p1) CDAT words, plus an optional EDGE chunk.
    /// </summary>
    private static byte[] BuildGraph(GitOid[] commits, (uint P0, uint P1)[] cdat, uint[]? edges)
    {
        int oidSize = 20;
        int numCommits = commits.Length;
        int checksumSize = oidSize;
        int numChunks = 3 + (edges is { Length: > 0 } ? 1 : 0);
        int headerSize = 8 + (1 + numChunks) * 12;
        int oidfSize = 256 * 4;
        int oidlSize = oidSize * numCommits;
        int cdatSize = (oidSize + 16) * numCommits;
        int edgeSize = edges is { Length: > 0 } ? edges.Length * 4 : 0;

        int oidfOff = headerSize;
        int oidlOff = oidfOff + oidfSize;
        int cdatOff = oidlOff + oidlSize;
        int edgeOff = cdatOff + cdatSize;
        int trailerOff = edgeOff + edgeSize;
        int totalSize = trailerOff + checksumSize;

        byte[] buf = new byte[totalSize];
        var ms = new MemoryStream(buf, 0, totalSize, writable: true);

        // Header: "CGPH", v1, oid_v1, numChunks, baseGraphs=0.
        ms.WriteByte((byte)'C');
        ms.WriteByte((byte)'G');
        ms.WriteByte((byte)'P');
        ms.WriteByte((byte)'H');
        ms.WriteByte(1);
        ms.WriteByte(1);
        ms.WriteByte((byte)numChunks);
        ms.WriteByte(0);

        WriteChunkEntry(ms, 0x4F494446, oidfOff); // "OIDF"
        WriteChunkEntry(ms, 0x4F49444C, oidlOff); // "OIDL"
        WriteChunkEntry(ms, 0x43444154, cdatOff); // "CDAT"
        if (edges is { Length: > 0 })
        {
            WriteChunkEntry(ms, 0x45444745, edgeOff); // "EDGE"
        }

        WriteChunkEntry(ms, 0, trailerOff); // sentinel

        // OIDF: cumulative counts by first byte.
        for (int i = 0; i < 256; i++)
        {
            uint count = 0;
            foreach (GitOid c in commits)
            {
                if (c.RawBytes[0] <= i)
                {
                    count++;
                }
            }

            WriteUInt32BE(ms, count);
        }

        // OIDL.
        foreach (GitOid c in commits)
        {
            ms.Write(c.RawBytes.ToArray(), 0, oidSize);
        }

        // CDAT: tree (zero) + p0 + p1 + gen/time words (0) per commit.
        for (int i = 0; i < numCommits; i++)
        {
            ms.Write(new byte[oidSize], 0, oidSize);
            WriteUInt32BE(ms, cdat[i].P0);
            WriteUInt32BE(ms, cdat[i].P1);
            WriteUInt32BE(ms, 0);
            WriteUInt32BE(ms, 0);
        }

        // EDGE.
        if (edges is { Length: > 0 })
        {
            foreach (uint e in edges)
            {
                WriteUInt32BE(ms, e);
            }
        }

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
