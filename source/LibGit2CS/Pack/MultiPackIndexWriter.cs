// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Utils;

namespace LibGit2CS.Pack;

/// <summary>
/// Write-side builder for the <c>.git/objects/pack/multi-pack-index</c> binary
/// format. Managed port of libgit2's <c>src/libgit2/midx.c</c> write side
/// (lines 502–940).
/// </summary>
/// <remarks>
/// <para>
/// Collects pack files via <see cref="AddAsync"/>, then serializes the chunk-based
/// binary format: header (12 bytes) + chunk table + PNAM (packfile names) +
/// OIDF (fanout) + OIDL (OID lookup) + OOFF (object offsets) + LOFF (large
/// offsets for ≥ 2 GiB) + trailer (SHA-1/SHA-256 checksum).
/// </para>
/// <para>
/// <b>Dedup + large offsets:</b> all object entries are sorted by
/// OID, then deduplicated (keeps first occurrence = smallest pack offset).
/// Offsets ≥ 2³¹ use the LOFF chunk (8-byte offsets) with <c>0x80000000</c>
/// flag in the 4-byte OOFF table. Pack names are stored as <c>.idx</c> filenames
/// (NUL-terminated, padded to 4-byte boundary).
/// </para>
/// <para>
/// <b>Chunk format:</b> 12-byte header (<c>"MIDX"</c> + version +
/// oid_version + num_chunks + base_midx + num_packfiles) + 12-byte chunk table
/// entries + sentinel chunk + chunks + trailer checksum. All integers are
/// big-endian. Checksum computed incrementally via
/// <see cref="GitIncrementalHash"/> (AOT-clean).
/// </para>
/// </remarks>
internal sealed class MultiPackIndexWriter : IDisposable
{
    private const uint Signature = 0x4D494458; // "MIDX"
    private const byte Version = 1;
    private const byte ObjectIdVersion = 1;

    private const uint ChunkPackfileNamesId = 0x504E414D;   // "PNAM"
    private const uint ChunkOidFanoutId = 0x4F494446;        // "OIDF"
    private const uint ChunkOidLookupId = 0x4F49444C;        // "OIDL"
    private const uint ChunkObjectOffsetsId = 0x4F4F4646;    // "OOFF"
    private const uint ChunkObjectLargeOffsetsId = 0x4C4F4646; // "LOFF"

    private const long LargeOffsetThreshold = 0x80000000L;

    private readonly string _packDir;
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly int _oidSize;
    private readonly List<PackEntry> _packs = [];
    private bool _disposed;

    /// <summary>
    /// Creates a new MIDX writer targeting <paramref name="packDir"/>
    /// (typically <c>&lt;repo&gt;/objects/pack</c>).
    /// </summary>
    public MultiPackIndexWriter(string packDir, GitHashAlgorithmKind algorithm)
    {
        ArgumentNullException.ThrowIfNull(packDir);
        _packDir = packDir;
        _algorithm = algorithm;
        _oidSize = GitOid.SizeFor(algorithm);
    }

    /// <summary>
    /// Adds a pack file by its <c>.idx</c> path. Matches <c>git_midx_writer_add</c>
    /// (midx.c:570–594). The path may be relative to <see cref="_packDir"/> or
    /// absolute.
    /// </summary>
    public async Task AddAsync(string idxPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idxPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string fullIdxPath = Path.IsPathRooted(idxPath)
            ? idxPath
            : Path.Join(_packDir, idxPath);

        // C (midx.c:733-750): the PNAM name is the pack path made RELATIVE to
        // pack_dir (git_fs_path_make_relative) — an out-of-directory pack
        // yields "../.." names — validated to end with ".pack" and converted
        // to ".idx". The port takes idx paths, so the relative name is
        // validated against ".idx" directly.
        string relName = PathHelpers.MakeRelative(fullIdxPath, _packDir);
        if (relName.Length <= ".idx".Length || !relName.EndsWith(".idx", StringComparison.Ordinal))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"invalid packfile name: '{fullIdxPath}'",
                GitErrorCategory.Odb);
        }

        using GitPackIndex index = await GitPackIndex.OpenAsync(fullIdxPath, _algorithm, cancellationToken).ConfigureAwait(false);

        var entries = new List<MidxEntry>(index.ObjectCount);
        for (int i = 0; i < index.ObjectCount; i++)
        {
            GitOid oid = index.GetOid(i);
            long offset = index.GetObjectOffset(i);
            entries.Add(new MidxEntry(oid, offset, 0)); // packIndex assigned later
        }

        _packs.Add(new PackEntry(relName, entries));
    }

    /// <summary>
    /// Serializes the MIDX to a byte array. Matches <c>git_midx_writer_dump</c>
    /// / <c>midx_write</c> (midx.c:679–891, 927–940).
    /// </summary>
    public byte[] Dump()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return BuildAndSerialize();
    }

    /// <summary>
    /// Serializes the MIDX and writes it atomically to
    /// <c>&lt;packDir&gt;/multi-pack-index</c>. Matches
    /// <c>git_midx_writer_commit</c> (midx.c:899–925).
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] data = Dump();
        string path = Path.Join(_packDir, "multi-pack-index");
        // C (midx.c): git_futils_writebuffer - NO leading-dir creation.
        await AsyncFileIO.WriteAtomicAsync(path, data, createLeadingDirs: false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _packs.Clear();
    }

    // ── Core serialization ───────────────────────────────────────────

    /// <summary>
    /// Builds the complete MIDX binary. Matches <c>midx_write</c>
    /// (midx.c:679–891).
    /// </summary>
    private byte[] BuildAndSerialize()
    {
        // Sort packs alphabetically by name and assign pack indices.
        _packs.Sort((a, b) => string.CompareOrdinal(a.IdxName, b.IdxName));
        for (int i = 0; i < _packs.Count; i++)
        {
            _packs[i].PackIndex = (uint)i;
        }

        // Collect all object entries with their pack indices.
        var allEntries = new List<MidxEntry>();
        foreach (PackEntry pack in _packs)
        {
            foreach (MidxEntry entry in pack.Entries)
            {
                allEntries.Add(new MidxEntry(entry.Oid, entry.Offset, pack.PackIndex));
            }
        }

        // Sort by OID and deduplicate (keep first = smallest pack offset).
        allEntries.Sort((a, b) => a.Oid.CompareTo(b.Oid));
        DedupEntries(allEntries);

        // Build PNAM chunk (NUL-terminated names, padded to 4-byte boundary).
        // Rents from ArrayPool via PooledByteBufferWriter.
        using var packfileNames = new PooledByteBufferWriter();
        foreach (PackEntry pack in _packs)
        {
            packfileNames.Write(Encoding.ASCII.GetBytes(pack.IdxName));
            packfileNames.Write("\0"u8);
        }

        // Pad to 4-byte boundary.
        while ((packfileNames.WrittenCount & 3) != 0)
        {
            packfileNames.Write("\0"u8);
        }

        // Build OIDF (256 × 4-byte cumulative fanout).
        byte[] oidFanout = new byte[256 * 4];
        uint fanoutCount = 0u;
        for (int i = 0; i < 256; i++)
        {
            while (fanoutCount < (uint)allEntries.Count
                && allEntries[(int)fanoutCount].Oid.RawBytes[0] <= i)
            {
                fanoutCount++;
            }

            BinaryPrimitives.WriteUInt32BigEndian(oidFanout.AsSpan(i * 4), fanoutCount);
        }

        // Build OIDL (sorted OIDs).
        using var oidLookup = new PooledByteBufferWriter(allEntries.Count * _oidSize);
        foreach (MidxEntry entry in allEntries)
        {
            oidLookup.Write(entry.Oid.RawBytes[.._oidSize]);
        }

        // Build OOFF + LOFF (pack_index + offset per entry, large offsets → LOFF).
        using var objectOffsets = new PooledByteBufferWriter(allEntries.Count * 8);
        using var objectLargeOffsets = new PooledByteBufferWriter();
        uint largeOffsetCount = 0;

        foreach (MidxEntry entry in allEntries)
        {
            WriteUInt32BE(objectOffsets, entry.PackIndex);

            if (entry.Offset >= LargeOffsetThreshold)
            {
                // High bit set → index into LOFF chunk.
                WriteUInt32BE(objectOffsets, 0x80000000u | largeOffsetCount);
                WriteUInt64BE(objectLargeOffsets, (ulong)entry.Offset);
                largeOffsetCount++;
            }
            else
            {
                WriteUInt32BE(objectOffsets, (uint)entry.Offset & 0x7FFFFFFFu);
            }
        }

        bool hasLargeOffsets = objectLargeOffsets.WrittenCount > 0;
        byte numChunks = (byte)(hasLargeOffsets ? 5 : 4);

        // Compute chunk offsets.
        // Header = 12 bytes, chunk table = (1 + numChunks) * 12 bytes.
        int headerSize = 12 + (1 + numChunks) * 12;
        long offset = headerSize;
        long pnamOff = offset;
        offset += packfileNames.WrittenCount;
        long oidfOff = offset;
        offset += oidFanout.Length;
        long oidlOff = offset;
        offset += oidLookup.WrittenCount;
        long ooffOff = offset;
        offset += objectOffsets.WrittenCount;
        long loffOff = 0;
        if (hasLargeOffsets)
        {
            loffOff = offset;
            offset += objectLargeOffsets.WrittenCount;
        }

        // Serialize to output with incremental checksum.
        using var hash = GitIncrementalHash.Create(_algorithm);
        using var output = new PooledByteBufferWriter();

        // Header: 4-byte magic + 1-byte version + 1-byte oid_version +
        // 1-byte num_chunks + 1-byte base_midx + 4-byte num_packfiles.
        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), Signature);
        header[4] = Version;
        header[5] = ObjectIdVersion;
        header[6] = numChunks;
        header[7] = 0; // base_midx_files
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), _packs.Count);
        Append(output, hash, header);

        // Chunk table.
        AppendChunkHeader(output, hash, ChunkPackfileNamesId, pnamOff);
        AppendChunkHeader(output, hash, ChunkOidFanoutId, oidfOff);
        AppendChunkHeader(output, hash, ChunkOidLookupId, oidlOff);
        AppendChunkHeader(output, hash, ChunkObjectOffsetsId, ooffOff);
        if (hasLargeOffsets)
        {
            AppendChunkHeader(output, hash, ChunkObjectLargeOffsetsId, loffOff);
        }

        // Sentinel chunk (id=0, offset=trailer position).
        AppendChunkHeader(output, hash, 0, offset);

        // Chunk data.
        Append(output, hash, packfileNames.WrittenSpan);
        Append(output, hash, oidFanout);
        Append(output, hash, oidLookup.WrittenSpan);
        Append(output, hash, objectOffsets.WrittenSpan);
        if (hasLargeOffsets)
        {
            Append(output, hash, objectLargeOffsets.WrittenSpan);
        }

        // Finalize checksum and append as trailer.
        GitOid checksum = hash.Finalize();
        output.Write(checksum.RawBytes[.._oidSize]);

        // Dump() returns byte[] — one final alloc is unavoidable.
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Removes duplicate entries (by OID) from the sorted list, keeping the first
    /// occurrence (smallest pack offset). Matches <c>git_vector_uniq</c>
    /// (midx.c:771).
    /// </summary>
    private static void DedupEntries(List<MidxEntry> entries)
    {
        if (entries.Count < 2)
        {
            return;
        }

        int writeIdx = 0;
        for (int readIdx = 1; readIdx < entries.Count; readIdx++)
        {
            if (!entries[writeIdx].Oid.Equals(entries[readIdx].Oid))
            {
                writeIdx++;
                if (writeIdx != readIdx)
                {
                    entries[writeIdx] = entries[readIdx];
                }
            }
        }

        writeIdx++;
        if (writeIdx < entries.Count)
        {
            entries.RemoveRange(writeIdx, entries.Count - writeIdx);
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
        BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(0), chunkId);
        BinaryPrimitives.WriteInt64BigEndian(buf.Slice(4), offset);
        Append(output, hash, buf);
    }

    private static void WriteUInt32BE(PooledByteBufferWriter ms, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(ms.GetSpan(4), value);
        ms.Advance(4);
    }

    private static void WriteUInt64BE(PooledByteBufferWriter ms, ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(ms.GetSpan(8), value);
        ms.Advance(8);
    }

    /// <summary>
    /// A pack file added to the MIDX writer, with its pre-collected object
    /// entries.
    /// </summary>
    private sealed class PackEntry(string idxName, List<MidxEntry> entries)
    {
        public string IdxName { get; } = idxName;
        public List<MidxEntry> Entries { get; } = entries;
        public uint PackIndex { get; set; }
    }
}
