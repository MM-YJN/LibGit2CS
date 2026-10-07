using System.Buffers.Binary;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

/// <summary>
/// Test-only builder that constructs thin pack files at the byte level. Used by
/// <see cref="PackIndexerTests"/> to exercise the indexer's thin-pack fixup path
/// (<c>fix_thin_pack</c> + <c>inject_object</c> + <c>update_header_and_rehash</c>).
/// </summary>
internal sealed class ThinPackBuilder
{
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly List<Entry> _entries = [];

    internal ThinPackBuilder(GitHashAlgorithmKind algorithm)
    {
        _algorithm = algorithm;
    }

    /// <summary>
    /// Adds a full (non-delta) object entry — identical to what a normal
    /// self-contained pack contains.
    /// </summary>
    internal void AddFullObject(GitObjectType type, ReadOnlyMemory<byte> body)
    {
        _entries.Add(new Entry(type, null, body));
    }

    /// <summary>
    /// Adds a REF_DELTA entry whose base (<paramref name="baseOid"/>) is NOT in
    /// the pack — a thin-pack entry. The base must be resolvable from the ODB at
    /// commit time for the indexer to unthin it.
    /// </summary>
    internal void AddRefDelta(GitOid baseOid, ReadOnlyMemory<byte> deltaData)
    {
        _entries.Add(new Entry(GitObjectType.RefDelta, baseOid, deltaData));
    }

    /// <summary>
    /// Builds the complete pack bytes: PACK header + entries + SHA trailer.
    /// </summary>
    internal byte[] Build()
    {
        using var ms = new MemoryStream();
        using var hash = GitIncrementalHash.Create(_algorithm);

        // PACK header: "PACK" + version 2 + object count (big-endian)
        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)_entries.Count);
        ms.Write(header);
        hash.AppendData(header);

        // Entries
        Span<byte> objHdr = stackalloc byte[16];
        foreach (Entry entry in _entries)
        {
            if (entry.Type == GitObjectType.RefDelta)
            {
                // REF_DELTA: type header + base OID (raw bytes) + compressed delta
                int hdrLen = PackEncoding.WriteObjectHeader(objHdr, GitObjectType.RefDelta, entry.Body.Length);
                ms.Write(objHdr[..hdrLen].ToArray());
                hash.AppendData(objHdr[..hdrLen]);

                byte[] baseOidBytes = entry.BaseOid!.Value.RawBytes.ToArray();
                ms.Write(baseOidBytes);
                hash.AppendData(baseOidBytes);
            }
            else
            {
                // Full object: type header + compressed body
                int hdrLen = PackEncoding.WriteObjectHeader(objHdr, entry.Type, entry.Body.Length);
                ms.Write(objHdr[..hdrLen].ToArray());
                hash.AppendData(objHdr[..hdrLen]);
            }

            byte[] compressed = ZlibTestHelpers.CompressLooseObject(entry.Body.Span);
            ms.Write(compressed);
            hash.AppendData(compressed);
        }

        // Trailer: hash of everything above
        GitOid trailer = hash.Finalize();
        ms.Write(trailer.RawBytes.ToArray());

        return ms.ToArray();
    }

    /// <summary>
    /// Creates a delta that copies the entire base, producing target == base.
    /// </summary>
    internal static byte[] MakeCopyAllDelta(ReadOnlySpan<byte> baseBody)
    {
        var delta = new List<byte>();
        WriteVarint(delta, baseBody.Length);  // base size
        WriteVarint(delta, baseBody.Length);  // result size (same)

        if (baseBody.Length > 0)
        {
            WriteCopyAll(delta, baseBody.Length);
        }

        return delta.ToArray();
    }

    /// <summary>
    /// Creates a delta that produces <paramref name="resultBody"/> from any base
    /// of size <paramref name="baseSize"/> using only INSERT instructions (the
    /// base content is irrelevant — only its size is validated).
    /// </summary>
    internal static byte[] MakeInsertOnlyDelta(int baseSize, ReadOnlySpan<byte> resultBody)
    {
        var delta = new List<byte>();
        WriteVarint(delta, baseSize);           // base size (validated only)
        WriteVarint(delta, resultBody.Length);  // result size

        WriteInsert(delta, resultBody);

        return delta.ToArray();
    }

    /// <summary>
    /// Creates a delta that copies the entire base and then appends
    /// <paramref name="suffix"/>. The result is <c>baseBody + suffix</c>.
    /// </summary>
    internal static byte[] MakeCopyAppendDelta(ReadOnlySpan<byte> baseBody, ReadOnlySpan<byte> suffix)
    {
        var delta = new List<byte>();
        int resultLen = baseBody.Length + suffix.Length;
        WriteVarint(delta, baseBody.Length);  // base size
        WriteVarint(delta, resultLen);        // result size

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
        // COPY instruction: cmd = 0x80 | size_bits. No offset bytes (offset=0).
        // Size byte 0 (bit 4): for size < 256. Size bytes 0+1 (bits 4+5): < 65536.
        if (size < 256)
        {
            output.Add(0x90);       // copy + size byte 0
            output.Add((byte)size);
        }
        else if (size < 65536)
        {
            output.Add(0xB0);       // copy + size byte 0 + size byte 1
            output.Add((byte)(size & 0xFF));
            output.Add((byte)((size >> 8) & 0xFF));
        }
        else
        {
            throw new NotSupportedException($"test COPY size {size} too large");
        }
    }

    private static void WriteInsert(List<byte> output, ReadOnlySpan<byte> data)
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

    private sealed record Entry(GitObjectType Type, GitOid? BaseOid, ReadOnlyMemory<byte> Body);
}
