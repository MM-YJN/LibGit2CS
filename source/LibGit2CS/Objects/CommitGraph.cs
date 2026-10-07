// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers.Binary;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Objects;

/// <summary>
/// Read-side parser for the <c>.git/objects/info/commit-graph</c> binary format.
/// Managed port of libgit2's <c>src/libgit2/commit_graph.c</c> (read side; the
/// writer is in <c>Objects/CommitGraphWriter.cs</c>).
/// Public surface mirrors <c>git_commit_graph_open</c>
/// (<c>include/git2/sys/commit_graph.h:70</c>); the entry-lookup members below
/// mirror the internal <c>src/libgit2/commit_graph.h</c> API and stay internal.
/// </summary>
/// <remarks>
/// <para>
/// The commit-graph accelerates revwalk by providing O(1) access to a commit's
/// generation number, committer time, and parent OIDs — avoiding an ODB read +
/// zlib inflation per commit. Revwalk falls back to the ODB path when the file
/// is absent or corrupt.
/// </para>
/// <para>
/// <b>v1 format only.</b> Handles OID Fanout (OIDF), OID Lookup (OIDL), Commit
/// Data (CDAT), and Extra Edge List (EDGE) chunks. Bloom filter (BIDX/BDAT) and
/// generation-data-v2 (GDA2/GDO2) chunks are recognized but skipped — for
/// GDA2/GDO2 this diverges from the v1.9.4 baseline (which rejects them),
/// matching upstream libgit2 main commit 2e3ec8d (PR #7271); see the
/// divergence note in <see cref="ParseHeaderAndChunks"/>.
/// </para>
/// <para>
/// No <see cref="GitContext"/> parameter — like <see cref="LibGit2CS.Diff.GitDiff.Buffers"/>,
/// this is a standalone object decoupled from any repository/context. Holds
/// only a managed <c>byte[]</c>, so there is nothing to dispose.
/// </para>
/// </remarks>
public sealed class CommitGraph
{
    private const uint Signature = 0x43475048; // "CGPH"
    private const byte Version1 = 1;
    private const byte ObjectIdVersion1 = 1;

    // Chunk IDs (big-endian ASCII).
    private const uint ChunkOidFanout = 0x4F494446;     // "OIDF"
    private const uint ChunkOidLookup = 0x4F49444C;     // "OIDL"
    private const uint ChunkCommitData = 0x43444154;    // "CDAT"
    private const uint ChunkExtraEdgeList = 0x45444745; // "EDGE"
    private const uint ChunkBloomIndex = 0x42494458;    // "BIDX"
    private const uint ChunkBloomData = 0x42444154;     // "BDAT"
    private const uint ChunkGenerationData = 0x47444132;         // "GDA2"
    private const uint ChunkGenerationDataOverflow = 0x47444F32; // "GDO2"

    private const uint MissingParent = 0x70000000;

    private readonly byte[] _data;
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly int _oidSize;
    private readonly int _numCommits;

    // Chunk offsets into _data.
    private readonly int _oidFanoutOffset;
    private readonly int _oidLookupOffset;
    private readonly int _commitDataOffset;
    private readonly int _extraEdgeListOffset;
    private readonly int _numExtraEdgeList;

    private CommitGraph(byte[] data, GitHashAlgorithmKind algorithm)
    {
        _data = data;
        _algorithm = algorithm;
        _oidSize = GitOid.SizeFor(algorithm);

        // Parse header + chunk table.
        _numCommits = ParseHeaderAndChunks(
            out _oidFanoutOffset, out _oidLookupOffset,
            out _commitDataOffset, out _extraEdgeListOffset, out _numExtraEdgeList);
    }

    /// <summary>The number of commits stored in this commit-graph.</summary>
    public int NumCommits => _numCommits;

    /// <summary>
    /// Opens and parses the commit-graph file at
    /// <paramref name="objectsDir"/>/info/commit-graph. Matches
    /// <c>git_commit_graph_open</c> (<c>include/git2/sys/commit_graph.h:70</c>).
    /// Returns null if the file does not exist (the documented
    /// <c>GIT_ENOTFOUND</c> → null shape); throws on corrupt/unrecognized
    /// format. The trailing checksum is copied but never verified — matching C,
    /// which verifies only in the separate (unported) validate API.
    /// </summary>
    public static ValueTask<CommitGraph?> OpenAsync(string objectsDir, GitHashAlgorithmKind algorithm, CancellationToken cancellationToken = default)
    {
        string path = Path.Join(objectsDir, "info", "commit-graph");
        if (!File.Exists(path))
        {
            // Absent commit-graph is the majority case for repos without one.
            return ValueTask.FromResult<CommitGraph?>(null);
        }

        return new ValueTask<CommitGraph?>(OpenSlowAsync(path, algorithm, cancellationToken));
    }

    /// <summary>Slow path of <see cref="OpenAsync"/>: reads and parses the commit-graph file (disk IO).</summary>
    private static async Task<CommitGraph?> OpenSlowAsync(string path, GitHashAlgorithmKind algorithm, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var graph = new CommitGraph(bytes, algorithm);

        // C (commit_graph.c:200-250, 344-362): the open path COPIES the
        // trailing checksum but NEVER verifies it — neither the explicit
        // git_commit_graph_open nor the lazy git_commit_graph_new used by
        // the ODB/revwalk path. Trailer verification exists only in the
        // separate git_commit_graph_validate API (not surfaced by the
        // port).
        return graph;
    }

    /// <summary>
    /// Finds the entry for <paramref name="oid"/>. Returns null if not present.
    /// Matches <c>git_commit_graph_entry_find</c> (commit_graph.c:575-631).
    /// Internal — C keeps the entry API in <c>src/libgit2/commit_graph.h</c>,
    /// outside the public <c>git2/sys/commit_graph.h</c> surface.
    /// </summary>
    internal CommitGraphEntry? FindEntry(GitOid oid)
    {
        byte firstByte = oid.RawBytes[0];
        int hi = (int)ReadUInt32BE(_oidFanoutOffset + firstByte * 4);
        int lo = firstByte == 0 ? 0 : (int)ReadUInt32BE(_oidFanoutOffset + (firstByte - 1) * 4);

        int pos = BinarySearchOid(oid.RawBytes, lo, hi);
        if (pos < 0)
        {
            return null;
        }

        return GetEntryByIndex(pos);
    }

    /// <summary>Decodes the entry at <paramref name="pos"/>. Matches <c>git_commit_graph_entry_get_byindex</c>.</summary>
    /// <returns>The decoded entry, or <c>null</c> when the entry's extra-edge
    /// list position is out of range — C's <c>get_byindex</c> fails with
    /// GIT_ENOTFOUND there, so <c>git_commit_graph_entry_find</c> reports
    /// "not found" and the revwalk falls back to the ODB.</returns>
    private CommitGraphEntry? GetEntryByIndex(int pos)
    {
        if ((uint)pos >= (uint)_numCommits)
        {
            throw new GitException(GitErrorCode.NotFound, $"commit-graph index {pos} out of range", GitErrorCategory.Odb);
        }

        int recordSize = _oidSize + 16; // tree_oid + 4 uint32s
        int baseOff = _commitDataOffset + pos * recordSize;

        // Tree OID.
        var treeOid = GitOid.FromRaw(new ReadOnlySpan<byte>(_data, baseOff, _oidSize), _algorithm);

        // Parent indices.
        uint p0 = ReadUInt32BE(baseOff + _oidSize);
        uint p1 = ReadUInt32BE(baseOff + _oidSize + 4);

        int parentCount = (p0 != MissingParent ? 1 : 0) + (p1 != MissingParent ? 1 : 0);

        // Generation + commit_time: generation occupies high 30 bits, time gets 2 bits.
        uint genAndTimeHi = ReadUInt32BE(baseOff + _oidSize + 8);
        uint timeLo = ReadUInt32BE(baseOff + _oidSize + 12);

        uint generation = genAndTimeHi >> 2;
        long commitTime = timeLo | ((long)(genAndTimeHi & 0x3) << 32);

        // Extra parents for octopus merges (parent2 high bit set).
        int extraEdgeIndex = -1;
        if ((p1 & 0x80000000u) != 0)
        {
            int edgePos = (int)(p1 & 0x7fffffff);
            if ((uint)edgePos >= (uint)_numExtraEdgeList)
            {
                // commit_graph.c:517-525 — "commit %u does not exist",
                // GIT_ENOTFOUND from get_byindex; entry_find then reports
                // not-found and git_commit_list_parse falls back to the ODB.
                return null;
            }

            extraEdgeIndex = edgePos;
            while (edgePos < _numExtraEdgeList && (ReadUInt32BE(_extraEdgeListOffset + edgePos * 4) & 0x80000000u) == 0)
            {
                edgePos++;
                parentCount++;
            }
        }

        // C (commit_list.c:187): an entry with more than 65535 parents is discarded BEFORE any parent OID is resolved — git_commit_graph_entry only records
        // parent_count/indices and git_commit_list_parse checks git__is_uint16(e.parent_count) first — so a corrupt huge-parent-count entry with
        // out-of-range indices falls back to the ODB instead of throwing NotFound.
        if (parentCount > ushort.MaxValue)
        {
            return null;
        }

        // Build the parent OID list by resolving indices into the OID lookup
        // table. Mirrors git_commit_graph_entry_parent (commit_graph.c:633-658):
        // parent 0 always comes from parent_indices[0]; parent 1 comes from
        // parent_indices[1] only when parent_count == 2; the remaining parents
        // come from the extra-edge list (flag bit masked). Any out-of-range
        // index fails with GIT_ENOTFOUND "commit index %zu does not exist"
        // (get_byindex, commit_graph.c:499-502) — never an unmanaged
        // ArgumentOutOfRangeException.
        var parents = new List<GitOid>(parentCount);
        if (parentCount > 0)
        {
            parents.Add(OidAtIndex((int)p0));
        }

        if (parentCount == 2)
        {
            parents.Add(OidAtIndex((int)p1));
        }
        else if (parentCount > 2)
        {
            for (int i = extraEdgeIndex; i < _numExtraEdgeList; i++)
            {
                uint edge = ReadUInt32BE(_extraEdgeListOffset + i * 4);
                parents.Add(OidAtIndex((int)(edge & 0x7fffffff)));
                if ((edge & 0x80000000u) != 0)
                {
                    break;
                }
            }
        }

        // The commit's own OID (from the lookup table).
        GitOid sha = OidAtIndex(pos);

        return new CommitGraphEntry(sha, treeOid, generation, commitTime, parentCount, [.. parents]);
    }

    private GitOid OidAtIndex(int index)
    {
        if ((uint)index >= (uint)_numCommits)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"commit index {index} does not exist",
                GitErrorCategory.Invalid);
        }

        int off = _oidLookupOffset + index * _oidSize;
        return GitOid.FromRaw(new ReadOnlySpan<byte>(_data, off, _oidSize), _algorithm);
    }

    /// <summary>Binary search for <paramref name="oid"/> in the OID lookup table [lo, hi).</summary>
    private int BinarySearchOid(ReadOnlySpan<byte> oid, int lo, int hi)
    {
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int off = _oidLookupOffset + mid * _oidSize;
            int cmp = CompareBytes(_data, off, oid, _oidSize);
            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else if (cmp > 0)
            {
                hi = mid;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    private static int CompareBytes(byte[] data, int dataOff, ReadOnlySpan<byte> oid, int len)
    {
        for (int i = 0; i < len; i++)
        {
            byte a = data[dataOff + i];
            byte b = oid[i];
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return 0;
    }

    private uint ReadUInt32BE(int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));

    /// <summary>
    /// Parses the 8-byte header + chunk table. Returns num_commits (from the OID fanout).
    /// Matches <c>git_commit_graph_file_parse</c> (commit_graph.c:199-302).
    /// </summary>
    private int ParseHeaderAndChunks(
        out int oidFanoutOffset, out int oidLookupOffset,
        out int commitDataOffset, out int extraEdgeOffset, out int numExtraEdge)
    {
        oidFanoutOffset = 0;
        oidLookupOffset = 0;
        commitDataOffset = 0;
        extraEdgeOffset = 0;
        numExtraEdge = 0;

        int checksumSize = _oidSize;
        if (_data.Length < 8 + checksumSize)
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - too short", GitErrorCategory.Odb);
        }

        uint sig = ReadUInt32BE(0);
        byte version = _data[4];
        byte oidVersion = _data[5];
        byte numChunks = _data[6];

        if (sig != Signature || version != Version1 || oidVersion != ObjectIdVersion1)
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - unsupported version", GitErrorCategory.Odb);
        }

        if (numChunks == 0)
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - no chunks", GitErrorCategory.Odb);
        }

        // First chunk data starts after header + (1 + numChunks) chunk-table entries (12 bytes each).
        int lastChunkOffset = 8 + (1 + numChunks) * 12;
        int trailerOffset = _data.Length - checksumSize;
        if (trailerOffset < lastChunkOffset)
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - wrong size", GitErrorCategory.Odb);
        }

        // Chunk table entries: [id(4), offset(8)] each, starting at offset 8.
        int chunkHdr = 8;
        var chunkOffsets = new Dictionary<uint, (int offset, int length)>();
        uint lastId = 0;
        int lastOff = lastChunkOffset;

        for (int i = 0; i < numChunks; i++, chunkHdr += 12)
        {
            uint id = ReadUInt32BE(chunkHdr);

            // C (commit_graph.c:245-249): the offset is kept in uint64 and validated against the trailer in 64 bits. The int-indexed window cannot
            // represent offsets >= 2^31, so those are rejected outright instead of wrapping into a small positive value that could pass the trailer check.
            ulong wideOffset = ((ulong)ReadUInt32BE(chunkHdr + 4) << 32) | ReadUInt32BE(chunkHdr + 8);
            if (wideOffset >= (ulong)int.MaxValue)
            {
                throw new GitException(GitErrorCode.Error, "invalid commit-graph file - chunk offset out of range", GitErrorCategory.Odb);
            }

            int chunkOffset = (int)wideOffset;

            // C (commit_graph.c:282-284): any chunk ID outside the known set
            // is rejected ("unrecognized chunk ID").
            //
            // DIVERGENCE from the v1.9.4 port baseline: GDA2/GDO2
            // (generation data v2, written by git >= 2.38 — GDA2 by default,
            // GDO2 when an offset exceeds 31 bits) are recognized and skipped,
            // a forward-port of upstream libgit2 main commit 2e3ec8d
            // (PR #7271, "Add support for reading newer commit graphs by skip
            // reading GDA2/GDO2"), which v1.9.4 itself rejects. Without it,
            // every stock `git commit-graph write` output since git 2.38 is
            // treated as corrupt and the revwalk silently loses the graph
            // fast path. Like BIDX/BDAT, the chunks are never consumed — the
            // generation field in CDAT still carries the topological level.
            // Legacy GDAT (git 2.35-2.37) stays rejected, matching upstream
            // main.
            if (id is not (ChunkOidFanout or ChunkOidLookup or ChunkCommitData or ChunkExtraEdgeList
                or ChunkBloomIndex or ChunkBloomData or ChunkGenerationData or ChunkGenerationDataOverflow))
            {
                throw new GitException(GitErrorCode.Error, "unrecognized chunk ID", GitErrorCategory.Odb);
            }

            if (chunkOffset < lastOff)
            {
                throw new GitException(GitErrorCode.Error, "invalid commit-graph file - non-monotonic chunks", GitErrorCategory.Odb);
            }

            if (chunkOffset >= trailerOffset)
            {
                throw new GitException(GitErrorCode.Error, "invalid commit-graph file - chunks beyond trailer", GitErrorCategory.Odb);
            }

            if (i > 0)
            {
                chunkOffsets[lastId] = (lastOff, chunkOffset - lastOff);
            }

            lastId = id;
            lastOff = chunkOffset;
        }

        // Final chunk length up to the trailer.
        chunkOffsets[lastId] = (lastOff, trailerOffset - lastOff);

        // OID Fanout.
        if (!chunkOffsets.TryGetValue(ChunkOidFanout, out (int offset, int length) fanout))
        {
            // C (commit_graph.c:145-146): a chunk whose offset is 0 is
            // reported as "missing"; the port collapses it into this branch.
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - missing OID Fanout chunk", GitErrorCategory.Odb);
        }

        // C (commit_graph.c:147-148): a zero-length fanout is "empty", not "wrong length" — the empty case is a distinct error.
        if (fanout.length == 0)
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - empty OID Fanout chunk", GitErrorCategory.Odb);
        }

        if (fanout.length != 256 * 4)
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - OID Fanout chunk has wrong length", GitErrorCategory.Odb);
        }

        oidFanoutOffset = fanout.offset;
        int numCommits = (int)ReadUInt32BE(oidFanoutOffset + 255 * 4);

        // Validate monotonicity.
        for (int i = 1; i < 256; i++)
        {
            if (ReadUInt32BE(oidFanoutOffset + i * 4) < ReadUInt32BE(oidFanoutOffset + (i - 1) * 4))
            {
                throw new GitException(GitErrorCode.Error, "invalid commit-graph file - non-monotonic fanout", GitErrorCategory.Odb);
            }
        }

        // OID Lookup.
        if (!chunkOffsets.TryGetValue(ChunkOidLookup, out (int offset, int length) lookup))
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - missing OID Lookup chunk", GitErrorCategory.Odb);
        }

        // C (commit_graph.c:152-153): an all-zero fanout with an empty OIDL is rejected as "0 commits".
        if (lookup.length == 0)
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - empty OID Lookup chunk", GitErrorCategory.Odb);
        }

        if (lookup.length != numCommits * _oidSize)
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - OID Lookup chunk has wrong length", GitErrorCategory.Odb);
        }

        // C (commit_graph.c:155-159): the OIDL entries must be strictly
        // increasing — "OID Lookup index is non-monotonic".
        for (int i = 1; i < numCommits; i++)
        {
            int prev = lookup.offset + (i - 1) * _oidSize;
            int cur = lookup.offset + i * _oidSize;
            if (CompareBytes(_data, prev, _data.AsSpan(cur, _oidSize), _oidSize) >= 0)
            {
                throw new GitException(GitErrorCode.Error, "OID Lookup index is non-monotonic", GitErrorCategory.Odb);
            }
        }

        oidLookupOffset = lookup.offset;

        // Commit Data.
        if (!chunkOffsets.TryGetValue(ChunkCommitData, out (int offset, int length) cdat))
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - missing Commit Data chunk", GitErrorCategory.Odb);
        }

        if (cdat.length == 0)
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - empty Commit Data chunk", GitErrorCategory.Odb);
        }

        if (cdat.length != numCommits * (_oidSize + 16))
        {
            throw new GitException(GitErrorCode.Error, "invalid commit-graph file - Commit Data chunk has wrong length", GitErrorCategory.Odb);
        }

        commitDataOffset = cdat.offset;

        // Extra Edge List (optional).
        if (chunkOffsets.TryGetValue(ChunkExtraEdgeList, out (int offset, int length) edge))
        {
            if (edge.length % 4 != 0)
            {
                throw new GitException(GitErrorCode.Error, "invalid commit-graph file - malformed Extra Edge List", GitErrorCategory.Odb);
            }

            extraEdgeOffset = edge.offset;
            numExtraEdge = edge.length / 4;
        }

        return numCommits;
    }
}

/// <summary>
/// A decoded commit-graph entry. Matches <c>git_commit_graph_entry</c>
/// (<c>sys/commit_graph.h</c>).
/// </summary>
internal readonly record struct CommitGraphEntry(
    GitOid Oid,
    GitOid TreeOid,
    uint Generation,
    long CommitTime,
    int ParentCount,
    GitOid[] Parents);
