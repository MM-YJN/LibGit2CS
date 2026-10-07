// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Remote;

namespace LibGit2CS.Transports;

/// <summary>
/// Writes git pkt-line protocol packets to a buffer.
/// Managed port of the write functions in <c>src/libgit2/transports/smart_pkt.c</c>.
/// </summary>
internal static class GitPacketWriter
{
    /// <summary>
    /// Write a flush packet ("0000") to a byte buffer.
    /// </summary>
    public static void WriteFlush(IBufferWriter<byte> buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        buf.Write(GitPacketReader.FlushStrBytes);
    }

    /// <summary>
    /// Write a "done" packet ("0009done\n") to the buffer.
    /// Ported from <c>git_pkt_buffer_done()</c>.
    /// </summary>
    public static void WriteDone(IBufferWriter<byte> buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        buf.Write(GitPacketReader.DoneStrBytes);
    }

    /// <summary>
    /// Write a "have &lt;oid&gt;\n" packet to a byte buffer.
    /// </summary>
    public static void WriteHave(IBufferWriter<byte> buf, GitOid oid)
    {
        ArgumentNullException.ThrowIfNull(buf);

        int oidHexSize = oid.HexSize;
        int haveLen = GitPacketReader.PktLenSize + 5 + oidHexSize + 1;

        WriteHexLength(buf, haveLen);
        WriteAscii(buf, "have ");
        WriteOidHex(buf, oid);
        buf.Write("\n"u8);
    }

    /// <summary>
    /// Write all "want" packets for a fetch negotiation, including capabilities
    /// on the first non-local ref, shallow roots, and depth.
    /// Ported from <c>git_pkt_buffer_wants()</c>.
    /// </summary>
    /// <param name="buf">The output buffer.</param>
    /// <param name="refs">The remote heads we want to fetch.</param>
    /// <param name="caps">The server's capabilities (determines which client caps to advertise).</param>
    /// <param name="oidType">The OID type (SHA-1 or SHA-256).</param>
    /// <param name="shallowRoots">Shallow root OIDs (empty if not a shallow clone).</param>
    /// <param name="depth">Clone depth (0 = full history).</param>
    public static void WriteWants(
        IBufferWriter<byte> buf,
        IReadOnlyList<GitRemoteHead> refs,
        GitSmartCapabilities caps,
        GitHashAlgorithmKind oidType,
        IReadOnlyList<GitOid>? shallowRoots = null,
        int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentNullException.ThrowIfNull(refs);

        int oidHexSize = GitOid.HexSizeFor(oidType);
        int wantLen = GitPacketReader.PktLenSize + 5 + oidHexSize + 1; // "want " + oid + "\n"

        int i = 0;

        // First non-local ref gets capabilities
        if (caps != GitSmartCapabilities.None)
        {
            // Find first non-local ref
            for (; i < refs.Count; i++)
            {
                if (!refs[i].Local)
                {
                    break;
                }
            }

            if (i < refs.Count)
            {
                WriteWantWithCaps(buf, refs[i], caps, oidType);
                i++;
            }
        }

        // Remaining non-local refs get plain want lines
        for (; i < refs.Count; i++)
        {
            if (refs[i].Local)
            {
                continue;
            }

            WriteHexLength(buf, wantLen);
            buf.Write("want "u8);
            WriteOidHex(buf, refs[i].Oid);
            buf.Write("\n"u8);
        }

        Span<byte> stackBuffer = stackalloc byte[128]; // 128 is enough for both "shallow <oid>\n" and "deepen <depth>\n" packets

        // Shallow roots
        if (shallowRoots != null)
        {
            foreach (GitOid root in shallowRoots)
            {
                // "shallow <oid>\n"
                int offset = 0;
                Write(stackBuffer, "shallow "u8, ref offset);
                offset += root.FormatHex(stackBuffer.Slice(offset));
                Write(stackBuffer, "\n"u8, ref offset);

                int shallowLen = offset + GitPacketReader.PktLenSize;
                WriteHexLength(buf, shallowLen);
                buf.Write(stackBuffer.Slice(0, offset));
            }
        }

        // Depth
        if (depth > 0)
        {
            // "deepen <depth>\n"
            int offset = 0;
            Write(stackBuffer, "deepen "u8, ref offset);
            bool result = depth.TryFormat(stackBuffer.Slice(offset), out int depthBytesWritten, provider: CultureInfo.InvariantCulture);
            Debug.Assert(result);
            offset += depthBytesWritten;
            Write(stackBuffer, "\n"u8, ref offset);

            int deepenLen = offset + GitPacketReader.PktLenSize;
            WriteHexLength(buf, deepenLen);
            buf.Write(stackBuffer.Slice(0, offset));
        }

        // Flush
        WriteFlush(buf);
    }

    /// <summary>
    /// Write a single "want" line with capabilities.
    /// Ported from <c>buffer_want_with_caps()</c>.
    /// </summary>
    private static void WriteWantWithCaps(IBufferWriter<byte> buf, GitRemoteHead head, GitSmartCapabilities caps, GitHashAlgorithmKind oidType)
    {
        const int MaxCapsStrLength = 128; // 128 is enough space if all capabilities are present, including spaces
        Span<byte> capsStrBuffer = stackalloc byte[MaxCapsStrLength];
        int offset = 0;

        // Prefer multi_ack_detailed over multi_ack
        if ((caps & GitSmartCapabilities.MultiAckDetailed) != 0)
        {
            Write(capsStrBuffer, "multi_ack_detailed "u8, ref offset);
        }
        else if ((caps & GitSmartCapabilities.MultiAck) != 0)
        {
            Write(capsStrBuffer, "multi_ack "u8, ref offset);
        }

        // Prefer side-band-64k over side-band
        if ((caps & GitSmartCapabilities.SideBand64k) != 0)
        {
            Write(capsStrBuffer, "side-band-64k "u8, ref offset);
        }
        else if ((caps & GitSmartCapabilities.SideBand) != 0)
        {
            Write(capsStrBuffer, "side-band "u8, ref offset);
        }

        if ((caps & GitSmartCapabilities.IncludeTag) != 0)
        {
            Write(capsStrBuffer, "include-tag "u8, ref offset);
        }

        if ((caps & GitSmartCapabilities.ThinPack) != 0)
        {
            Write(capsStrBuffer, "thin-pack "u8, ref offset);
        }

        if ((caps & GitSmartCapabilities.OfsDelta) != 0)
        {
            Write(capsStrBuffer, "ofs-delta "u8, ref offset);
        }

        if ((caps & GitSmartCapabilities.Shallow) != 0)
        {
            Write(capsStrBuffer, "shallow "u8, ref offset);
        }

        int oidHexSize = GitOid.HexSizeFor(oidType);
        // len = 4 + "want " + oid + " " + caps + "\n"
        int len = GitPacketReader.PktLenSize + 5 + oidHexSize + 1 + offset + 1;

        WriteHexLength(buf, len);
        buf.Write("want "u8);
        WriteOidHex(buf, head.Oid);
        buf.Write(" "u8);
        buf.Write(capsStrBuffer.Slice(0, offset));
        buf.Write("\n"u8);
    }

    public static void WriteHexLength(IBufferWriter<byte> buf, int len)
    {
        Span<byte> buffer = buf.GetSpan(16);
        bool result = len.TryFormat(buffer, out int bytesWritten, "x4", CultureInfo.InvariantCulture);
        Debug.Assert(result);
        buf.Advance(bytesWritten);
    }

    private static void WriteAscii(IBufferWriter<byte> buf, string s)
    {
        Span<byte> buffer = buf.GetSpan(Encoding.ASCII.GetMaxByteCount(s.Length));
        int bytesWritten = Encoding.ASCII.GetBytes(s, buffer);
        buf.Advance(bytesWritten);
    }

    public static int WriteOidHex(IBufferWriter<byte> buf, GitOid oid)
    {
        int size = oid.HexSize;
        Span<byte> buffer = buf.GetSpan(size);
        oid.FormatHex(buffer);
        buf.Advance(size);
        return size;
    }

    private static void Write(Span<byte> destination, ReadOnlySpan<byte> bytes, ref int offset)
    {
        bytes.CopyTo(destination.Slice(offset));
        offset += bytes.Length;
    }

    public static int WriteUtf8(IBufferWriter<byte> buf, ReadOnlySpan<char> value)
    {
        Span<byte> buffer = buf.GetSpan(Encoding.UTF8.GetByteCount(value));
        int bytesWritten = Encoding.UTF8.GetBytes(value, buffer);
        buf.Advance(bytesWritten);
        return bytesWritten;
    }

    public static int WriteZeros(IBufferWriter<byte> buf, int length)
    {
        Span<byte> buffer = buf.GetSpan(length);
        buffer.Slice(0, length).Fill((byte)'0');
        buf.Advance(length);
        return length;
    }
}
