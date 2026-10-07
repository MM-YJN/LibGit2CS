// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers.Binary;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

using static System.Runtime.InteropServices.JavaScript.JSType;

namespace LibGit2CS.Pack;

/// <summary>
/// Pack index file (`.idx`). Managed port of the read-side of libgit2's
/// <c>pack.c:pack_index_check_locked</c> + <c>nth_packed_object_offset_locked</c>
/// + <c>git_pack__lookup_id</c>.
/// </summary>
/// <remarks>
/// <para>
/// Supports both index v1 (no header, 256-entry fanout + interleaved OID/offset
/// entries) and v2 (signature <c>\377tOc</c>, version 2, separate OID/CRC/offset
/// tables). v2 is the modern format; v1 is preserved for legacy repos.
/// </para>
/// <para>
/// <b>Large offsets</b>: offsets ≥ 2³¹ are stored in a separate 8-byte
/// table at the end of the `.idx`. The main 4-byte offset table holds
/// <c>0x80000000 | idx_into_large_table</c>. Use <see cref="long"/> throughout.
/// </para>
/// </remarks>
public sealed class GitPackIndex : IDisposable
{
    /// <summary>Index v2 signature: <c>\377tOc</c> (<c>0xff744f63</c>).</summary>
    internal const uint IdxSignature = 0xff744f63;

    private readonly byte[] _data;
    private readonly int _version;
    private readonly int _numObjects;
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly int _oidSize;

    // Offset into _data where the 256-entry fanout table starts.
    private readonly int _fanoutOffset;

    // Offset into _data where the OID lookup table starts.
    private readonly int _oidTableOffset;

    // Offset into _data where the CRC32 table starts (v2 only; 0 for v1).
    private readonly int _crcTableOffset;

    // Offset into _data where the 4-byte offset table starts.
    private readonly int _offsetTableOffset;

    // For v1, each entry is (oid_size + 4) bytes: OID followed by 4-byte offset.
    // For v2, entries are separated: OID table, CRC table, offset table.

    private bool _disposed;

    private GitPackIndex(
        byte[] data,
        int version,
        int numObjects,
        GitHashAlgorithmKind algorithm,
        int fanoutOffset,
        int oidTableOffset,
        int crcTableOffset,
        int offsetTableOffset)
    {
        _data = data;
        _version = version;
        _numObjects = numObjects;
        _algorithm = algorithm;
        _oidSize = GitOid.SizeFor(algorithm);
        _fanoutOffset = fanoutOffset;
        _oidTableOffset = oidTableOffset;
        _crcTableOffset = crcTableOffset;
        _offsetTableOffset = offsetTableOffset;
    }

    /// <summary>The index format version (1 or 2).</summary>
    public int Version => _version;

    /// <summary>Number of objects in the pack.</summary>
    public int ObjectCount => _numObjects;

    /// <summary>The hash algorithm used for OIDs in this pack.</summary>
    public GitHashAlgorithmKind Algorithm => _algorithm;

    /// <summary>
    /// Loads and validates a pack index from <paramref name="path"/>.
    /// Matches <c>pack_index_check_locked</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if the file is too small, has an unsupported
    /// version, non-monotonic fanout, or incorrect size.
    /// </exception>
    public static async Task<GitPackIndex> OpenAsync(string path, GitHashAlgorithmKind algorithm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(bytes, algorithm);
    }

    /// <summary>
    /// Parses a pack index from in-memory bytes. Matches
    /// <c>pack_index_check_locked</c> validation logic.
    /// </summary>
    internal static GitPackIndex Parse(byte[] data, GitHashAlgorithmKind algorithm)
    {
        ArgumentNullException.ThrowIfNull(data);
        int oidSize = GitOid.SizeFor(algorithm);

        // Minimum size: fanout (4*256) + 2*oidSize (pack + index checksums).
        int minSize = 4 * 256 + oidSize * 2;
        if (data.Length < minSize)
        {
            throw new GitException(GitErrorCode.Error, "invalid pack index: too small", GitErrorCategory.Odb);
        }

        int version;
        int fanoutOffset;
        int oidTableOffset;
        int crcTableOffset;
        int offsetTableOffset;

        // Detect v2 by signature at byte 0.
        uint sig = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (sig == IdxSignature)
        {
            version = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
            if (version is not 2)
            {
                throw new GitException(GitErrorCode.Error, "unsupported index version", GitErrorCategory.Odb);
            }

            // v2 layout: 8-byte header, then fanout, then OID table, CRC table, offset table.
            fanoutOffset = 8;
            oidTableOffset = fanoutOffset + 4 * 256;
            // CRC and offset tables come after the OID table; their positions depend
            // on numObjects which we compute from the fanout below.
            crcTableOffset = 0; // computed below
        }
        else
        {
            version = 1;
            fanoutOffset = 0;
            oidTableOffset = fanoutOffset + 4 * 256;
            crcTableOffset = 0; // v1 has no CRC table
        }

        // Read the fanout table and verify monotonicity.
        int numObjects = 0;
        for (int i = 0; i < 256; i++)
        {
            int n = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(fanoutOffset + i * 4));
            if (n < numObjects)
            {
                throw new GitException(GitErrorCode.Error, "index is non-monotonic", GitErrorCategory.Odb);
            }

            numObjects = n;
        }

        // Validate file size against the expected layout.
        if (version == 1)
        {
            // v1: fanout (4*256) + nr*(oid_size+4) + 2*oid_size
            long expectedSize = 4 * 256 + (long)numObjects * (oidSize + 4) + oidSize * 2;
            if (data.Length != expectedSize)
            {
                throw new GitException(GitErrorCode.Error, "index is corrupted (wrong size)", GitErrorCategory.Odb);
            }

            // v1: offsets are interleaved — each entry is (oid_size + 4) bytes.
            // The OID table starts at oidTableOffset; each entry's offset is the
            // 4-byte big-endian value at position oidTableOffset + i*(oid_size+4) + oid_size.
            offsetTableOffset = oidTableOffset; // used with stride
        }
        else
        {
            // v2: 8 + fanout(4*256) + nr*oid_size + nr*4(crc) + nr*4(offset) + 2*oid_size
            // plus optional 8-byte large offset entries.
            long minSizeV2 = 8 + 4 * 256 + (long)numObjects * (oidSize + 4 + 4) + oidSize * 2;
            long maxSizeV2 = minSizeV2;
            if (numObjects > 0)
            {
                maxSizeV2 += (long)(numObjects - 1) * 8;
            }

            if (data.Length < minSizeV2 || data.Length > maxSizeV2)
            {
                throw new GitException(GitErrorCode.Error, "wrong index size", GitErrorCategory.Odb);
            }

            crcTableOffset = oidTableOffset + numObjects * oidSize;
            offsetTableOffset = crcTableOffset + numObjects * 4;
        }

        return new GitPackIndex(
            data,
            version,
            numObjects,
            algorithm,
            fanoutOffset,
            oidTableOffset,
            crcTableOffset,
            offsetTableOffset);
    }

    /// <summary>
    /// Returns the OID at index <paramref name="index"/> (0-based).
    /// </summary>
    public GitOid GetOid(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)_numObjects)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int offset;
        if (_version == 1)
        {
            offset = _oidTableOffset + index * (_oidSize + 4);
        }
        else
        {
            offset = _oidTableOffset + index * _oidSize;
        }

        return GitOid.FromRaw(_data.AsSpan(offset, _oidSize), _algorithm);
    }

    /// <summary>
    /// Returns the file offset of the object at index <paramref name="index"/>.
    /// Matches <c>nth_packed_object_offset_locked</c>. Handles the large-offset
    /// table for objects ≥ 2GB into the pack.
    /// </summary>
    public long GetObjectOffset(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)_numObjects)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (_version == 1)
        {
            // v1: offset is the 4-byte BE value after the OID in each interleaved entry.
            int pos = _oidTableOffset + index * (_oidSize + 4) + _oidSize;
            return BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(pos));
        }

        // v2: read the 4-byte offset from the offset table.
        uint off32 = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_offsetTableOffset + index * 4));
        if ((off32 & 0x80000000) == 0)
        {
            return off32;
        }

        // Large offset: index into the 8-byte table.
        int largeIdx = (int)(off32 & 0x7fffffff);

        // Compute in long: an int expression
        // (`largeIdx * 8 ≥ 2^32`) wraps on crafted .idx files, landing on an
        // attacker-chosen earlier entry and returning a wrong offset. C uses
        // size_t pointer arithmetic (pack.c:1279-1283) and errors cleanly on
        // the same input — compute in long like C.
        long largeOffset = (long)_offsetTableOffset + (long)_numObjects * 4 + (long)largeIdx * 8;

        // C (pack.c:1282-1283): `if (index >= end - 8) return -1;` — an entry
        // starting at exactly len-8 (the trailer checksum) is out of bounds.
        if (largeOffset >= _data.Length - 8)
        {
            throw new GitException(GitErrorCode.Error, "packfile index is corrupt (large offset out of bounds)", GitErrorCategory.Odb);
        }

        return BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan((int)largeOffset));
    }

    /// <summary>
    /// Looks up an object by full or abbreviated OID. Matches
    /// <c>pack_entry_find_offset</c> + <c>git_pack__lookup_id</c>.
    /// </summary>
    /// <param name="oid">The full or abbreviated OID to find.</param>
    /// <param name="hexLength">
    /// Number of hex characters to match. Pass <c>0</c> or the full hex size for
    /// an exact (full OID) lookup.
    /// </param>
    /// <returns>
    /// A <see cref="GitPackIndexLookupResult"/>. If <see cref="GitPackIndexLookupResult.Found"/>
    /// is true, <see cref="GitPackIndexLookupResult.Index"/> is the object's index in
    /// the pack. If the OID prefix matches multiple objects,
    /// <see cref="GitPackIndexLookupResult.Ambiguous"/> is true. If no object matches,
    /// <see cref="GitPackIndexLookupResult.Index"/> is -1.
    /// </returns>
    public GitPackIndexLookupResult FindIndex(GitOid oid, int hexLength = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (hexLength == 0)
        {
            hexLength = oid.HexSize;
        }

        ReadOnlySpan<byte> oidBytes = oid.RawBytes;
        byte firstByte = oidBytes[0];

        // Use the fanout table to narrow the binary search range.
        int hi = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_fanoutOffset + firstByte * 4));
        int lo = firstByte == 0 ? 0 : (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_fanoutOffset + (firstByte - 1) * 4));

        // Binary search in the OID table.
        int pos = BinarySearchOid(oidBytes, lo, hi);

        int foundIndex;
        if (pos >= 0)
        {
            // Exact match.
            foundIndex = pos;
        }
        else
        {
            // No exact match; pos = -(insertion point) - 1.
            int insertionPoint = -pos - 1;
            if (insertionPoint >= _numObjects)
            {
                return new GitPackIndexLookupResult(-1, false);
            }

            // Check if the object at the insertion point matches the prefix.
            GitOid candidateOid = GetOid(insertionPoint);
            if (!candidateOid.StartsWith(oid, hexLength))
            {
                return new GitPackIndexLookupResult(-1, false);
            }

            foundIndex = insertionPoint;
        }

        // For abbreviated lookups, check ambiguity: does the NEXT object also match?
        if (hexLength != oid.HexSize && foundIndex + 1 < _numObjects)
        {
            GitOid nextOid = GetOid(foundIndex + 1);
            if (nextOid.StartsWith(oid, hexLength))
            {
                return new GitPackIndexLookupResult(foundIndex, true);
            }
        }

        return new GitPackIndexLookupResult(foundIndex, false);
    }

    /// <summary>
    /// Binary search for <paramref name="oidBytes"/> in the OID table between
    /// <paramref name="lo"/> and <paramref name="hi"/> (exclusive). Matches
    /// <c>git_pack__lookup_id</c>.
    /// </summary>
    /// <returns>
    /// The index of the match, or <c>-(insertion point) - 1</c> if not found.
    /// </returns>
    private int BinarySearchOid(ReadOnlySpan<byte> oidBytes, int lo, int hi)
    {
        int stride = _version == 1 ? _oidSize + 4 : _oidSize;

        while (lo < hi)
        {
            int mi = (lo + hi) / 2;
            int cmp = CompareOidAt(mi, oidBytes, stride);

            if (cmp == 0)
            {
                return mi;
            }

            if (cmp > 0)
            {
                hi = mi;
            }
            else
            {
                lo = mi + 1;
            }
        }

        return -lo - 1;
    }

    /// <summary>
    /// Compares the OID at index <paramref name="index"/> with
    /// <paramref name="oidBytes"/>.
    /// </summary>
    private int CompareOidAt(int index, ReadOnlySpan<byte> oidBytes, int stride)
    {
        int offset = _oidTableOffset + index * stride;
        Span<byte> span = _data.AsSpan(offset, _oidSize);
        return span.SequenceCompareTo(oidBytes[.._oidSize]);
    }

    /// <summary>
    /// Returns the pack checksum (the OID of the `.pack` file) embedded in the
    /// trailing checksum of the `.idx`.
    /// </summary>
    public GitOid PackChecksum()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The pack checksum is the second-to-last OID in the file
        // (the last OID is the .idx file's own checksum).
        int offset = _data.Length - _oidSize * 2;
        return GitOid.FromRaw(_data.AsSpan(offset, _oidSize), _algorithm);
    }

    /// <summary>
    /// Enumerates all OIDs in this pack, in sorted order.
    /// </summary>
    public IEnumerable<GitOid> EnumerateOids()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _numObjects; i++)
        {
            yield return GetOid(i);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
