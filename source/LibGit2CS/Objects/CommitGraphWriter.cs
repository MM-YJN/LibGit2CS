// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Pack;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;
using LibGit2CS.Utils;

namespace LibGit2CS.Objects;

/// <summary>
/// Write-side builder for the <c>.git/objects/info/commit-graph</c> binary format.
/// Managed port of libgit2's <c>src/libgit2/commit_graph.c</c> write side
/// (lines 60–100, 689–1319). Public surface mirrors
/// <c>git_commit_graph_writer_new</c> / <c>_add_index_file</c> /
/// <c>_add_revwalk</c> / <c>_dump</c> / <c>_commit</c>
/// (<c>include/git2/sys/commit_graph.h</c>).
/// </summary>
/// <remarks>
/// <para>
/// Collects commit metadata via <see cref="AddRevwalkAsync"/> or
/// <see cref="AddIndexFileAsync"/>, computes generation numbers via a Post-Order DFS,
/// then serializes the chunk-based binary format: header (8 bytes) + chunk table +
/// OIDF (fanout) + OIDL (OID lookup) + CDAT (commit data) + EDGE (extra edges for
/// octopus merges) + trailer (SHA-1/SHA-256 checksum of all preceding bytes).
/// </para>
/// <para>
/// No <see cref="GitContext"/> parameter — like <see cref="LibGit2CS.Diff.GitDiff.Buffers"/>,
/// this is a standalone object decoupled from any repository/context (the repo
/// it operates on is passed per call to <see cref="AddIndexFileAsync"/>).
/// </para>
/// <para>
/// <b>Generation computation:</b> uses a 4-state Post-Order DFS with
/// an explicit stack (no recursion) to avoid C stack overflow on deep histories.
/// States: <c>Unvisited → Added → Expanded → Visited</c>. Generation = max(parent
/// generations) + 1. Root commits = 1. Octopus merges cap at
/// <c>0x3FFFFFFF</c>. Missing parents = 0.
/// </para>
/// <para>
/// <b>Chunk format:</b> 8-byte header (<c>"CGPH"</c> + version +
/// oid_version + num_chunks + base_graphs) + 12-byte chunk table entries (4-byte
/// ID + 8-byte offset) + sentinel chunk + chunks + trailer checksum. All integers
/// are big-endian. Checksum computed incrementally via
/// <see cref="GitIncrementalHash"/> (AOT-clean).
/// </para>
/// </remarks>
public sealed class CommitGraphWriter : IDisposable
{
    private const uint Signature = 0x43475048; // "CGPH"
    private const byte Version = 1;
    private const byte ObjectIdVersion = 1;

    private const uint ChunkOidFanoutId = 0x4F494446;     // "OIDF"
    private const uint ChunkOidLookupId = 0x4F49444C;     // "OIDL"
    private const uint ChunkCommitDataId = 0x43444154;    // "CDAT"
    private const uint ChunkExtraEdgeListId = 0x45444745; // "EDGE"

    private const uint MissingParent = 0x70000000;
    private const uint GenerationNumberMax = 0x3FFFFFFF;

    private readonly string _objectsInfoDir;
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly int _oidSize;
    private readonly List<PackedCommit> _commits = [];
    private bool _disposed;

    /// <summary>
    /// Creates a new commit-graph writer targeting
    /// <paramref name="objectsInfoDir"/> (typically
    /// <c>&lt;repo&gt;/objects/info</c>).
    /// </summary>
    public CommitGraphWriter(string objectsInfoDir, CommitGraphWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(objectsInfoDir);
        _objectsInfoDir = objectsInfoDir;
        _algorithm = options?.ObjectFormat ?? GitHashAlgorithmKind.Sha1;
        _oidSize = GitOid.SizeFor(_algorithm);
    }

    /// <summary>
    /// Adds all commits reachable from <paramref name="walk"/>. The walker is
    /// walked to completion. Matches <c>git_commit_graph_writer_add_revwalk</c>
    /// (commit_graph.c:835–860).
    /// </summary>
    public async Task AddRevwalkAsync(GitRevWalker walk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(walk);
        ObjectDisposedException.ThrowIf(_disposed, this);

        GitRepository repo = walk.Repository;
        // C (commit_graph.c:843-859): while ((git_revwalk_next(&id, walk)) == 0)
        // — any non-zero result (GIT_ITEROVER or a negative error) exits the
        // loop and the function returns 0 (success). So walker iteration errors
        // are swallowed; only commit-lookup errors inside the loop propagate.
        IAsyncEnumerator<GitOid> enumerator = walk.WalkAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (GitException)
                {
                    // C: git_revwalk_next returned an error → loop exits silently.
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                GitOid oid = enumerator.Current;
                if (await repo.Objects.LookupAsync<Commit>(oid, cancellationToken).ConfigureAwait(false) is not { } commit)
                {
                    throw new GitException(
                        GitErrorCode.NotFound,
                        $"commit {oid} not found in object database",
                        GitErrorCategory.Odb);
                }

                var packed = PackedCommit.Create(commit);
                _commits.Add(packed);
                commit.Dispose();
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds all commit OIDs found in the pack index file at
    /// <paramref name="idxPath"/> by looking up each object's header type via
    /// <paramref name="repo"/>. Non-commit objects are skipped. Matches
    /// <c>git_commit_graph_writer_add_index_file</c> (commit_graph.c:805–833).
    /// </summary>
    public async Task AddIndexFileAsync(GitRepository repo, string idxPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(idxPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? packDir = Path.GetDirectoryName(idxPath);
        string idxName = Path.GetFileName(idxPath);
        string fullIdxPath = packDir is not null ? Path.Join(packDir, idxName) : idxName;

        using GitPackIndex index = await GitPackIndex.OpenAsync(fullIdxPath, repo.ObjectFormat, cancellationToken).ConfigureAwait(false);
        foreach (GitOid oid in index.EnumerateOids())
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitObjectHeader? header = await repo.Objects.ReadHeaderAsync(oid, cancellationToken).ConfigureAwait(false);
            if (header is not { } h)
            {
                // C (commit_graph.c:781-783): git_odb_read_header error
                // (GIT_ENOTFOUND) → propagate; the writer fails.
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"object not found - no match for id ({oid})",
                    GitErrorCategory.Odb);
            }

            if (h.Type != GitObjectType.Commit)
            {
                continue;
            }

            if (await repo.Objects.LookupAsync<Commit>(oid, cancellationToken).ConfigureAwait(false) is not { } commit)
            {
                // C (commit_graph.c:787-789): git_commit_lookup error → propagate.
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"object not found - no match for id ({oid})",
                    GitErrorCategory.Odb);
            }

            var packed = PackedCommit.Create(commit);
            _commits.Add(packed);
            commit.Dispose();
        }
    }

    /// <summary>
    /// Serializes the commit-graph to a byte array. Matches
    /// <c>git_commit_graph_writer_dump</c> / <c>commit_graph_write</c>
    /// (commit_graph.c:1072–1271, 1307–1319).
    /// </summary>
    public byte[] Dump()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return BuildAndSerialize();
    }

    /// <summary>
    /// Serializes the commit-graph and writes it atomically to
    /// <c>&lt;objectsInfoDir&gt;/commit-graph</c>. Matches
    /// <c>git_commit_graph_writer_commit</c> (commit_graph.c:1279–1305).
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] data = Dump();
        string path = Path.Join(_objectsInfoDir, "commit-graph");
        string lockPath = path + ".lock";

        // C (commit_graph.c:1279-1305 + filebuf.c:44-66): git_filebuf_open
        // creates <path>.lock with O_CREAT|O_EXCL and mode 0644; a
        // pre-existing lock is GIT_ELOCKED ("failed to create locked file").
        FileStream fs;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                fs = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            else
            {
                // C creates the temp file with mode 0644 (commit_graph.c:1293);
                // open(2) applies mode & ~umask — a restrictive umask (e.g.
                // 027) yields 0640. UnixCreateMode applies the umask
                // atomically at creation, like C's open(2); the previous
                // post-hoc SetUnixFileMode forced exactly 0644 regardless of
                // umask, diverging from C.
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = CommitGraphFileMode,
                };
                fs = new FileStream(lockPath, options);
            }
        }
        catch (IOException)
        {
            throw new GitException(
                GitErrorCode.Locked,
                $"failed to create locked file '{lockPath}'",
                GitErrorCategory.Odb);
        }

        try
        {
            using (fs)
            {
                await fs.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }

            // C: git_filebuf_commit renames the lock over the target.
            File.Move(lockPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteLock(lockPath);
            throw;
        }
    }

    private static void TryDeleteLock(string lockPath)
    {
        try
        {
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a stray lock file after a failed commit.
        }
    }

    /// <summary>
    /// C's commit-graph file mode: 0644 (commit_graph.c:1293), masked by the
    /// process umask at creation.
    /// </summary>
    private const UnixFileMode CommitGraphFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commits.Clear();
    }

    // ── Core serialization ───────────────────────────────────────────

    /// <summary>
    /// Builds the complete commit-graph binary. Matches
    /// <c>commit_graph_write</c> (commit_graph.c:1072–1271).
    /// </summary>
    private byte[] BuildAndSerialize()
    {
        // Sort commits by OID and deduplicate.
        TimSort.Sort(_commits, PackedCommit.CompareByOid);
        DedupCommits();

        // Compute generation numbers (4-state Post-Order DFS).
        ComputeGenerationNumbers();

        // Build chunk data: OIDF, OIDL, CDAT, EDGE. The chunk builders rent
        // from ArrayPool via PooledByteBufferWriter; the fanout table is a
        // fixed-size array (small, no pool benefit).
        byte[] oidFanout = new byte[256 * 4];
        using var oidLookup = new PooledByteBufferWriter();
        using var commitData = new PooledByteBufferWriter();
        using var extraEdgeList = new PooledByteBufferWriter();

        PopulateChunkData(oidFanout, oidLookup, commitData, extraEdgeList);

        bool hasEdge = extraEdgeList.WrittenCount > 0;
        byte numChunks = (byte)(hasEdge ? 4 : 3);

        // Compute chunk offsets.
        int headerSize = 8 + (1 + numChunks) * 12;
        long offset = headerSize;
        long oidFanoutOff = offset;
        offset += oidFanout.Length;
        long oidLookupOff = offset;
        offset += oidLookup.WrittenCount;
        long commitDataOff = offset;
        offset += commitData.WrittenCount;
        long extraEdgeOff = 0;
        if (hasEdge)
        {
            extraEdgeOff = offset;
            offset += extraEdgeList.WrittenCount;
        }

        // Serialize to output with incremental checksum.
        using var hash = GitIncrementalHash.Create(_algorithm);
        using var output = new PooledByteBufferWriter();
        WriteTo(output, hash, numChunks, oidFanout, oidLookup, commitData, extraEdgeList,
            oidFanoutOff, oidLookupOff, commitDataOff, extraEdgeOff, hasEdge);

        // Finalize checksum and append as trailer.
        GitOid checksum = hash.Finalize();
        output.Write(checksum.RawBytes[.._oidSize]);

        // Dump() returns byte[] — one final alloc is unavoidable.
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Writes the header, chunk table, and all chunk data to
    /// <paramref name="output"/>, feeding every byte into
    /// <paramref name="hash"/> for the trailing checksum.
    /// </summary>
    private static void WriteTo(
        PooledByteBufferWriter output,
        GitIncrementalHash hash,
        byte numChunks,
        byte[] oidFanout,
        PooledByteBufferWriter oidLookup,
        PooledByteBufferWriter commitData,
        PooledByteBufferWriter extraEdgeList,
        long oidFanoutOff,
        long oidLookupOff,
        long commitDataOff,
        long extraEdgeOff,
        bool hasEdge)
    {
        // Header: 4-byte magic + 1-byte version + 1-byte oid_version + 1-byte
        // num_chunks + 1-byte base_graphs.
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, Signature);
        header[4] = Version;
        header[5] = ObjectIdVersion;
        header[6] = numChunks;
        header[7] = 0; // base_graph_files
        Append(output, hash, header);

        // Chunk table (numChunks + 1 sentinel, each 12 bytes: id(4) + offset(8)).
        AppendChunkHeader(output, hash, ChunkOidFanoutId, oidFanoutOff);
        AppendChunkHeader(output, hash, ChunkOidLookupId, oidLookupOff);
        AppendChunkHeader(output, hash, ChunkCommitDataId, commitDataOff);
        if (hasEdge)
        {
            AppendChunkHeader(output, hash, ChunkExtraEdgeListId, extraEdgeOff);
        }

        // Sentinel chunk (id=0, offset=trailer position).
        long trailerOffset = oidFanoutOff + oidFanout.Length
            + oidLookup.WrittenCount + commitData.WrittenCount
            + (hasEdge ? extraEdgeList.WrittenCount : 0);
        AppendChunkHeader(output, hash, 0, trailerOffset);

        // Chunk data.
        Append(output, hash, oidFanout);
        Append(output, hash, oidLookup.WrittenSpan);
        Append(output, hash, commitData.WrittenSpan);
        if (hasEdge)
        {
            Append(output, hash, extraEdgeList.WrittenSpan);
        }
    }

    /// <summary>
    /// Populates the OIDF, OIDL, CDAT, and EDGE chunk data from the sorted,
    /// deduplicated commit list. Matches the chunk-building loops in
    /// <c>commit_graph_write</c> (commit_graph.c:1119–1205).
    /// </summary>
    private void PopulateChunkData(
        byte[] oidFanout,
        PooledByteBufferWriter oidLookup,
        PooledByteBufferWriter commitData,
        PooledByteBufferWriter extraEdgeList)
    {
        // OID Fanout: 256 cumulative BE uint32 counts.
        uint fanoutCount = 0u;
        for (int i = 0; i < 256; i++)
        {
            while (fanoutCount < (uint)_commits.Count
                && _commits[(int)fanoutCount].Oid.RawBytes[0] <= i)
            {
                fanoutCount++;
            }

            BinaryPrimitives.WriteUInt32BigEndian(oidFanout.AsSpan(i * 4, 4), fanoutCount);
        }

        // OID Lookup + Commit Data + Extra Edge List.
        uint extraEdgeCount = 0;
        foreach (PackedCommit pc in _commits)
        {
            // OIDL: raw OID bytes.
            oidLookup.Write(pc.Oid.RawBytes[.._oidSize]);

            // CDAT: tree_oid + parent1(4) + parent2(4) + gen_timehi(4) + timelo(4).
            commitData.Write(pc.TreeOid.RawBytes[.._oidSize]);

            uint parentCount = (uint)pc.Parents.Count;

            // Parent 1.
            if (parentCount == 0)
            {
                WriteUInt32BE(commitData, MissingParent);
            }
            else
            {
                WriteUInt32BE(commitData, pc.ParentIndices[0]);
            }

            // Parent 2.
            if (parentCount < 2)
            {
                WriteUInt32BE(commitData, MissingParent);
            }
            else if (parentCount == 2)
            {
                WriteUInt32BE(commitData, pc.ParentIndices[1]);
            }
            else
            {
                // Octopus: parent2 field becomes 0x80000000 | edge_index.
                WriteUInt32BE(commitData, 0x80000000u | extraEdgeCount);
            }

            // Extra edge list for >2 parents: write parent indices 1..N-1.
            if (parentCount > 2)
            {
                for (uint parentI = 1; parentI < parentCount; parentI++)
                {
                    uint edgeWord = pc.ParentIndices[(int)parentI];
                    // Last parent in the chain gets the high bit set.
                    if (parentI + 1 == parentCount)
                    {
                        edgeWord |= 0x80000000u;
                    }

                    WriteUInt32BE(extraEdgeList, edgeWord);
                }

                extraEdgeCount += parentCount - 1;
            }

            // Generation + commit time (high 30 bits = generation, low 2 bits = time_hi).
            uint generation = pc.Generation;
            if (generation > GenerationNumberMax)
            {
                generation = GenerationNumberMax;
            }

            ulong commitTime = (ulong)pc.CommitTime;
            uint genTimeHi = (generation << 2) | ((uint)(commitTime >> 32) & 0x3);
            WriteUInt32BE(commitData, genTimeHi);
            WriteUInt32BE(commitData, (uint)(commitTime & 0xFFFFFFFFu));
        }
    }

    /// <summary>
    /// Removes duplicate commits (by OID) from the sorted list. Matches
    /// <c>git_vector_uniq</c> with <c>packed_commit_free_dup</c>.
    /// </summary>
    private void DedupCommits()
    {
        if (_commits.Count < 2)
        {
            return;
        }

        int writeIdx = 0;
        for (int readIdx = 1; readIdx < _commits.Count; readIdx++)
        {
            if (!_commits[writeIdx].Oid.Equals(_commits[readIdx].Oid))
            {
                writeIdx++;
                if (writeIdx != readIdx)
                {
                    _commits[writeIdx] = _commits[readIdx];
                }
            }
        }

        writeIdx++;
        if (writeIdx < _commits.Count)
        {
            _commits.RemoveRange(writeIdx, _commits.Count - writeIdx);
        }
    }

    // ── Generation number computation ─────────────────────────────────

    /// <summary>
    /// States for the Post-Order DFS generation computation. Matches
    /// <c>generation_number_commit_state</c> (commit_graph.c:862–867).
    /// </summary>
    private enum GenState : byte
    {
        Unvisited = 0,
        Added = 1,
        Expanded = 2,
        Visited = 3,
    }

    /// <summary>
    /// Computes generation numbers via a 4-state Post-Order DFS with an explicit
    /// stack. Matches <c>compute_generation_numbers</c>
    /// (commit_graph.c:871–1008).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generation = max(parent generations) + 1. Root commits (no parents) get
    /// generation 1. Octopus merges cap at <c>0x3FFFFFFF</c>. Parents not in the
    /// writer's set get generation 0.
    /// </para>
    /// <para>
    /// The stack-based approach avoids C stack overflow on deep histories. Each
    /// node may be pushed twice: once for initial visit (→ Expanded when parents
    /// are pushed), once for finalization (→ Visited when generation is computed).
    /// </para>
    /// </remarks>
    private void ComputeGenerationNumbers()
    {
        if (_commits.Count == 0)
        {
            return;
        }

        // Assign indices and build OID → index map.
        var oidMap = new Dictionary<GitOid, int>(_commits.Count);
        for (int i = 0; i < _commits.Count; i++)
        {
            _commits[i].Index = i;
            oidMap[_commits[i].Oid] = i;
        }

        // Resolve parent OIDs to parent indices.
        foreach (PackedCommit pc in _commits)
        {
            foreach (GitOid parentOid in pc.Parents)
            {
                if (!oidMap.TryGetValue(parentOid, out int parentIdx))
                {
                    throw new GitException(
                        GitErrorCode.NotFound,
                        $"parent commit {parentOid} not found in commit graph",
                        GitErrorCategory.Odb);
                }

                pc.ParentIndices.Add((uint)parentIdx);
            }
        }

        // Initialize the stack with all commit indices.
        var stack = new Stack<int>(_commits.Count);
        for (int i = _commits.Count - 1; i >= 0; i--)
        {
            stack.Push(i);
        }

        var states = new GenState[_commits.Count];

        while (stack.Count > 0)
        {
            int i = stack.Pop();
            PackedCommit pc = _commits[i];

            if (states[i] == GenState.Visited)
            {
                continue;
            }

            if (states[i] == GenState.Expanded)
            {
                // All parents have been visited; compute generation.
                pc.Generation = 0;
                foreach (uint parentIdx in pc.ParentIndices)
                {
                    uint parentGen = _commits[(int)parentIdx].Generation;
                    if (pc.Generation < parentGen)
                    {
                        pc.Generation = parentGen;
                    }
                }

                if (pc.Generation < GenerationNumberMax)
                {
                    pc.Generation++;
                }

                states[i] = GenState.Visited;
                continue;
            }

            // First visit: check for root commit (no parents).
            if (pc.ParentIndices.Count == 0)
            {
                states[i] = GenState.Visited;
                pc.Generation = 1;
                continue;
            }

            // Push this commit back for finalization after all parents are done.
            stack.Push(i);

            // Push unvisited parents.
            foreach (uint parentIdx in pc.ParentIndices)
            {
                if (states[(int)parentIdx] == GenState.Unvisited)
                {
                    states[(int)parentIdx] = GenState.Added;
                    stack.Push((int)parentIdx);
                }
            }

            states[i] = GenState.Expanded;
        }
    }

    // ── Binary helpers ───────────────────────────────────────────────

    private static void Append(PooledByteBufferWriter output, GitIncrementalHash hash, ReadOnlySpan<byte> data)
    {
        output.Write(data);
        hash.AppendData(data);
    }

    private static void AppendChunkHeader(PooledByteBufferWriter output, GitIncrementalHash hash, uint chunkId, long offset)
    {
        Span<byte> buf = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(0, 4), chunkId);
        BinaryPrimitives.WriteInt64BigEndian(buf.Slice(4, 8), offset);
        Append(output, hash, buf);
    }

    private static void WriteUInt32BE(PooledByteBufferWriter writer, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(writer.GetSpan(4), value);
        writer.Advance(4);
    }
}
