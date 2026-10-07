// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers.Binary;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Pack;

/// <summary>
/// Read-side parser for the <c>.git/objects/pack/multi-pack-index</c> binary
/// format. Managed port of libgit2's <c>src/libgit2/midx.c</c> (read side; the
/// writer is in <c>Pack/MultiPackIndexWriter.cs</c>).
/// </summary>
/// <remarks>
/// <para>
/// The multi-pack index (midx) accelerates object lookup across multiple pack
/// files: instead of scanning each pack's <c>.idx</c> in turn, a single midx
/// lookup returns the pack index + offset. The pack backend falls back to
/// per-pack <c>.idx</c> lookup when the midx is absent.
/// </para>
/// <para>
/// <b>v1 format only.</b> Handles Packfile Names (PNAM), OID Fanout (OIDF),
/// OID Lookup (OIDL), Object Offsets (OOFF), and Object Large Offsets (LOFF).
/// Unknown chunk IDs are tolerated.
/// </para>
/// </remarks>
internal sealed class MultiPackIndex
{
    private const uint Signature = 0x4D494458; // "MIDX"
    private const byte Version1 = 1;
    private const byte ObjectIdVersion1 = 1;

    private const uint ChunkPackfileNames = 0x504E414D;   // "PNAM"
    private const uint ChunkOidFanout = 0x4F494446;        // "OIDF"
    private const uint ChunkOidLookup = 0x4F49444C;        // "OIDL"
    private const uint ChunkObjectOffsets = 0x4F4F4646;    // "OOFF"
    private const uint ChunkObjectLargeOffsets = 0x4C4F4646; // "LOFF"

    private readonly byte[] _data;
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly int _oidSize;
    private readonly int _numObjects;
    private readonly int _numPacks;

    private readonly int _packfileNamesOffset;
    private readonly int _oidFanoutOffset;
    private readonly int _oidLookupOffset;
    private readonly int _objectOffsetsOffset;
    private readonly int _objectLargeOffsetsOffset;
    private readonly int _numObjectLargeOffsets;

    private MultiPackIndex(byte[] data, GitHashAlgorithmKind algorithm)
    {
        _data = data;
        _algorithm = algorithm;
        _oidSize = GitOid.SizeFor(algorithm);

        _numObjects = Parse(out _packfileNamesOffset, out _oidFanoutOffset,
            out _oidLookupOffset, out _objectOffsetsOffset,
            out _objectLargeOffsetsOffset, out _numObjectLargeOffsets, out _numPacks);
    }

    /// <summary>The number of objects indexed.</summary>
    public int NumObjects => _numObjects;

    /// <summary>The number of packs referenced.</summary>
    public int NumPacks => _numPacks;

    /// <summary>
    /// Opens and parses the multi-pack-index at <paramref name="packDir"/>/multi-pack-index.
    /// Returns null if absent. Throws on corrupt/unrecognized format.
    /// </summary>
    public static async Task<MultiPackIndex?> OpenAsync(string packDir, GitHashAlgorithmKind algorithm, CancellationToken cancellationToken)
    {
        string path = Path.Join(packDir, "multi-pack-index");
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new MultiPackIndex(bytes, algorithm);
    }

    /// <summary>
    /// Gets the packfile name at <paramref name="packIndex"/> (NUL-terminated
    /// strings in the PNAM chunk, e.g. <c>"pack-&lt;hash&gt;.idx"</c>).
    /// </summary>
    public string GetPackName(int packIndex)
    {
        int off = _packfileNamesOffset;
        for (int i = 0; i < packIndex; i++)
        {
            while (_data[off] != 0)
            {
                off++;
            }

            off++;
        }

        int end = off;
        while (_data[end] != 0)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(_data, off, end - off);
    }

    /// <summary>
    /// Finds the (packIndex, offset) for <paramref name="oid"/>. Returns null
    /// if not present. Matches <c>git_midx_entry_find</c> (midx.c:380-453).
    /// </summary>
    public MultiPackIndexEntry? FindEntry(GitOid oid)
    {
        byte firstByte = oid.RawBytes[0];
        int hi = (int)ReadUInt32BE(_oidFanoutOffset + firstByte * 4);
        int lo = firstByte == 0 ? 0 : (int)ReadUInt32BE(_oidFanoutOffset + (firstByte - 1) * 4);

        int pos = BinarySearchOid(oid.RawBytes, lo, hi);
        if (pos < 0)
        {
            return null;
        }

        // Decode the 8-byte object-offset record: pack_index(4) + offset/large-ref(4).
        int objOff = _objectOffsetsOffset + pos * 8;
        int packIndex = (int)ReadUInt32BE(objOff);

        // C (midx.c:432-449): git_midx_entry_find resolves the LARGE OFFSET first ("invalid index into the object large offsets table" → notfound → per-pack
        // fallback) and only then validates the pack index.
        uint offsetLo = ReadUInt32BE(objOff + 4);

        long offset;
        if ((offsetLo & 0x80000000u) != 0 && _objectLargeOffsetsOffset != 0)
        {
            int largePos = (int)(offsetLo & 0x7fffffffu);
            if ((uint)largePos >= (uint)_numObjectLargeOffsets)
            {
                // C (midx.c:439-440): git_odb__error_notfound — the caller falls back to per-pack lookup.
                return null;
            }

            int lOff = _objectLargeOffsetsOffset + largePos * 8;
            offset = ((long)ReadUInt32BE(lOff) << 32) | ReadUInt32BE(lOff + 4);
        }
        else
        {
            offset = offsetLo;
        }

        // C (midx.c:447-449): the decoded pack index must be within the packfile-names table ("invalid index into the packfile names table"). C returns an
        // error here and pack_entry_find falls through to the per-pack.idx search (odb_pack.c:278-296) — the port must return null (not-found) instead of
        // throwing hard.
        if ((uint)packIndex >= (uint)_numPacks)
        {
            return null;
        }

        return new MultiPackIndexEntry(packIndex, offset);
    }

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

    private static GitException MidxError(string message) => new(
        GitErrorCode.Error,
        $"invalid multi-pack-index file - {message}",
        GitErrorCategory.Odb);

    /// <summary>
    /// Validates the PNAM chunk contents. Matches <c>midx_parse_packfile_names</c>
    /// (midx.c:52-85): empty chunk, empty names, unterminated names, unsorted
    /// names, non-.idx names, and non-local names are all rejected.
    /// </summary>
    private void ValidatePackfileNames(int offset, int length, int packfiles)
    {
        if (length == 0)
        {
            throw MidxError("empty Packfile Names chunk");
        }

        int pos = offset;
        int remaining = length;
        string? previous = null;
        for (int i = 0; i < packfiles; i++)
        {
            int len = 0;
            while (pos + len < offset + length && _data[pos + len] != 0)
            {
                len++;
            }

            if (len == 0)
            {
                throw MidxError("empty packfile name");
            }

            if (len + 1 > remaining)
            {
                throw MidxError("unterminated packfile name");
            }

            string name = System.Text.Encoding.ASCII.GetString(_data, pos, len);
            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
            {
                throw MidxError("packfile names are not sorted");
            }

            if (name.Length <= ".idx".Length || !name.EndsWith(".idx", StringComparison.Ordinal))
            {
                throw MidxError("non-.idx packfile name");
            }

            if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
            {
                throw MidxError("non-local packfile");
            }

            previous = name;
            pos += len + 1;
            remaining -= len + 1;
        }
    }

    /// <summary>Matches <c>git_midx_parse</c> (midx.c:164-295).</summary>
    private int Parse(
        out int pnamOff, out int oidfOff, out int oidlOff,
        out int ooffOff, out int loffOff, out int numLargeOff, out int numPacks)
    {
        pnamOff = 0;
        oidfOff = 0;
        oidlOff = 0;
        ooffOff = 0;
        loffOff = 0;
        numLargeOff = 0;
        numPacks = 0;

        if (_data.Length < 12 + _oidSize)
        {
            throw new GitException(GitErrorCode.Error, "invalid multi-pack-index file - too short", GitErrorCategory.Odb);
        }

        uint sig = ReadUInt32BE(0);
        byte version = _data[4];
        byte oidVersion = _data[5];
        byte numChunks = _data[6];
        // _data[7] = base_midx_files (always 0 in v1).

        if (sig != Signature || version != Version1 || oidVersion != ObjectIdVersion1)
        {
            throw new GitException(GitErrorCode.Error, "invalid multi-pack-index file - unsupported version", GitErrorCategory.Odb);
        }

        if (numChunks == 0)
        {
            throw new GitException(GitErrorCode.Error, "invalid multi-pack-index file - no chunks", GitErrorCategory.Odb);
        }

        uint numPackfiles = ReadUInt32BE(8); // 4-byte packfile count at offset 8.

        // An `(int)` cast would wrap counts ≥ 2^31 negative, skipping the PNAM
        // validation loop and leaving _numPacks negative — FindEntry's
        // `(uint)packIndex >= (uint)_numPacks` guard then passed for nearly
        // any attacker-supplied OOFF pack index and GetPackName scanned the
        // buffer unboundedly (IndexOutOfRangeException). C validates the
        // count inside midx_parse_packfile_names and fails the parse
        // (midx.c:52-85), falling back to per-pack .idx lookup — reject
        // unrepresentable counts up front like the indexer's object-count
        // guard.
        if (numPackfiles > int.MaxValue)
        {
            throw MidxError("too many packfiles");
        }

        int checksumSize = _oidSize;
        int trailerOffset = _data.Length - checksumSize;
        int lastChunkOffset = 12 + (1 + numChunks) * 12;
        if (trailerOffset < lastChunkOffset)
        {
            throw new GitException(GitErrorCode.Error, "invalid multi-pack-index file - wrong size", GitErrorCategory.Odb);
        }

        var chunkOffsets = new Dictionary<uint, (int offset, int length)>();
        int chunkHdr = 12;
        uint lastId = 0;
        int lastOff = lastChunkOffset;

        for (int i = 0; i < numChunks; i++, chunkHdr += 12)
        {
            uint id = ReadUInt32BE(chunkHdr);
            uint highOffset = ReadUInt32BE(chunkHdr + 4);
            uint lowOffset = ReadUInt32BE(chunkHdr + 8);

            // C (midx.c:222-223): a chunk offset whose high word is >=
            // INT32_MAX is rejected outright ("chunk offset out of range") -
            // it cannot wrap silently.
            if (highOffset >= int.MaxValue)
            {
                throw MidxError("chunk offset out of range");
            }

            int chunkOffset = (int)(((long)highOffset << 32) | lowOffset);

            if (chunkOffset < lastOff)
            {
                throw new GitException(GitErrorCode.Error, "invalid multi-pack-index file - non-monotonic chunks", GitErrorCategory.Odb);
            }

            if (chunkOffset >= trailerOffset)
            {
                throw new GitException(GitErrorCode.Error, "invalid multi-pack-index file - chunks beyond trailer", GitErrorCategory.Odb);
            }

            if (i > 0)
            {
                chunkOffsets[lastId] = (lastOff, chunkOffset - lastOff);
            }

            lastId = id;
            lastOff = chunkOffset;
        }

        chunkOffsets[lastId] = (lastOff, trailerOffset - lastOff);

        // PNAM
        if (!chunkOffsets.TryGetValue(ChunkPackfileNames, out (int offset, int length) pnam))
        {
            throw new GitException(GitErrorCode.Error, "invalid multi-pack-index file - missing PNAM chunk", GitErrorCategory.Odb);
        }

        pnamOff = pnam.offset;

        // C (midx.c:52-85, midx_parse_packfile_names): the PNAM chunk must be
        // non-empty and contain exactly numPackfiles sorted, NUL-terminated,
        // local .idx names.
        ValidatePackfileNames(pnam.offset, pnam.length, (int)numPackfiles);

        // OIDF
        if (!chunkOffsets.TryGetValue(ChunkOidFanout, out (int offset, int length) oidf))
        {
            throw MidxError("missing OID Fanout chunk");
        }

        // C (midx.c:98-100): a zero-length fanout is "empty", not "wrong length" — the empty case is a distinct error.
        if (oidf.length == 0)
        {
            throw MidxError("empty OID Fanout chunk");
        }

        if (oidf.length != 256 * 4)
        {
            throw MidxError("OID Fanout chunk has wrong length");
        }

        oidfOff = oidf.offset;

        // C (midx.c:101-107, midx_parse_oid_fanout): every fanout entry must
        // be >= the previous one ("index is non-monotonic") — unsigned
        // comparisons like C's uint32_t.
        uint previous = 0;
        for (int i = 0; i < 256; i++)
        {
            uint n = ReadUInt32BE(oidfOff + i * 4);
            if (n < previous)
            {
                throw MidxError("index is non-monotonic");
            }

            previous = n;
        }

        int numObjects = (int)previous;

        // OIDL
        if (!chunkOffsets.TryGetValue(ChunkOidLookup, out (int offset, int length) oidl))
        {
            throw MidxError("missing OID Lookup chunk");
        }

        // C (midx.c:121-122): an all-zero fanout with an empty OIDL is rejected as "0 objects".
        if (oidl.length == 0)
        {
            throw MidxError("empty OID Lookup chunk");
        }

        if (oidl.length != numObjects * _oidSize)
        {
            throw MidxError("OID Lookup chunk has wrong length");
        }

        oidlOff = oidl.offset;

        // OOFF
        if (!chunkOffsets.TryGetValue(ChunkObjectOffsets, out (int offset, int length) ooff))
        {
            throw MidxError("missing Object Offsets chunk");
        }

        // C (midx.c:138-139): an empty Object Offsets chunk is rejected.
        if (ooff.length == 0)
        {
            throw MidxError("empty Object Offsets chunk");
        }

        if (ooff.length != numObjects * 8)
        {
            throw MidxError("Object Offsets chunk has wrong length");
        }

        ooffOff = ooff.offset;

        // LOFF (optional). C (midx.c:148-162): an EMPTY LOFF chunk (length 0)
        // means "no large-offset table" — object_large_offsets stays NULL, so
        // FindEntry returns the raw 32-bit value instead of erroring.
        if (chunkOffsets.TryGetValue(ChunkObjectLargeOffsets, out (int offset, int length) loff))
        {
            if (loff.length % 8 != 0)
            {
                throw new GitException(GitErrorCode.Error, "invalid multi-pack-index file - malformed LOFF chunk", GitErrorCategory.Odb);
            }

            if (loff.length > 0)
            {
                loffOff = loff.offset;
                numLargeOff = loff.length / 8;
            }
        }

        numPacks = (int)numPackfiles;
        return numObjects;
    }
}
