// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Git-style offset varint codec. Managed port of libgit2's
/// <c>src/util/varint.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> standard unsigned LEB128. Git's varint uses an "offset
/// encoding": after each 7-bit shift, the value is decremented before encoding
/// the next byte with the continuation bit set. Decoding reverses this by
/// adding 1 after each continuation byte.
/// </para>
/// <para>
/// Used by pack OFS_DELTA offsets and index v4 path-prefix compression.
/// </para>
/// <para>
/// <b>Encoding</b> (matches <c>git_encode_varint</c>): the value is written
/// low-7-bits first. The first byte has no continuation bit. Each subsequent
/// byte (working backward) has the continuation bit (0x80) set and encodes
/// <c>(value - 1) &amp; 0x7F</c> after shifting <c>value &gt;&gt;= 7</c>.
/// </para>
/// <para>
/// <b>Decoding</b> (matches <c>git_decode_varint</c>): reads 7 bits per byte.
/// For each byte with the continuation bit set, <c>val += 1</c> before shifting
/// in the next 7 bits. Overflow is detected when bit 7 of <c>val</c> is set
/// before a shift.
/// </para>
/// </remarks>
public static class GitVarint
{
    private const int MaxVarintSize = 16;

    /// <summary>
    /// Decodes a git-style varint from <paramref name="bufp"/>. Matches
    /// <c>git_decode_varint</c>.
    /// </summary>
    /// <param name="bufp">The buffer to decode from.</param>
    /// <param name="varintLen">The number of bytes consumed (0 on overflow).</param>
    /// <returns>The decoded value, or 0 on overflow (with <paramref name="varintLen"/> = 0).</returns>
    public static ulong Decode(ReadOnlySpan<byte> bufp, out int varintLen)
    {
        if (bufp.IsEmpty)
        {
            varintLen = 0;
            return 0;
        }

        int pos = 0;
        byte c = bufp[pos++];
        ulong val = (ulong)(c & 0x7F);

        while ((c & 0x80) != 0)
        {
            val += 1;

            // Overflow check: if bit 7 is set, shifting left by 7 would overflow.
            if (val == 0 || (val >> 57) != 0)
            {
                varintLen = 0;
                return 0;
            }

            if (pos >= bufp.Length)
            {
                varintLen = 0;
                return 0;
            }

            c = bufp[pos++];
            val = (val << 7) + (ulong)(c & 0x7F);
        }

        varintLen = pos;
        return val;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a git-style varint into
    /// <paramref name="buf"/>. Matches <c>git_encode_varint</c>.
    /// </summary>
    /// <param name="buf">The destination buffer (at least 16 bytes recommended).</param>
    /// <param name="value">The value to encode.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException"><paramref name="buf"/> is too small
    /// to hold the encoded varint.</exception>
    public static int Encode(Span<byte> buf, ulong value)
    {
        Span<byte> varint = stackalloc byte[MaxVarintSize];
        int pos = MaxVarintSize - 1;
        varint[pos] = (byte)(value & 0x7F);

        while ((value >>= 7) != 0)
        {
            value -= 1;
            pos--;
            varint[pos] = (byte)(0x80 | (value & 0x7F));
        }

        int length = MaxVarintSize - pos;
        if (buf.Length < length)
        {
            throw new ArgumentException("Buffer too small for varint encoding", nameof(buf));
        }

        varint.Slice(pos, length).CopyTo(buf);
        return length;
    }
}
