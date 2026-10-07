// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Collections.Immutable;
using System.Text;

namespace LibGit2CS.Core;

/// <summary>
/// Base85 encoder/decoder for binary patch bodies. Managed port of
/// <c>git_str_encode_base85</c> (<c>src/util/str.c:348-388</c>) and
/// <c>git_str_decode_base85</c> (<c>src/util/str.c:410-468</c>).
/// </summary>
/// <remarks>
/// <para>
/// git's base85 alphabet is
/// <c>0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&amp;()*+-;&lt;=&gt;?@^_`{|}~</c>
/// (distinct from Ascii85 / z85). Each group of up to 4 bytes is packed into a
/// <c>uint32</c> big-endian, then emitted as 5 base85 digits (least-significant
/// digit last).
/// </para>
/// <para>
/// Consumers: <c>format_binary</c> in <c>diff_print.c:482</c> (encode) +
/// <c>patch_parse.c</c> (decode) + <c>apply.c</c> (decode via
/// <c>patch_parse</c>).
/// </para>
/// </remarks>
internal static class Base85
{
    private static ReadOnlySpan<byte> Alphabet
        => "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&()*+-;<=>?@^_`{|}~"u8;

    /// <summary>
    /// Inverse decode table: maps ASCII char code → digit value (0-84) or -1 for
    /// invalid. Matches <c>base85_decode[]</c> (str.c:391-407) but with the
    /// off-by-one pre-applied (C does <c>de = table[ch]; if (--de &lt; 0)</c>, so
    /// the stored table values are 1-based; here we store 0-based directly and
    /// use -1 for invalid).
    /// </summary>
    private static readonly ImmutableArray<sbyte> s_decodeTable = BuildDecodeTable();

    private static ImmutableArray<sbyte> BuildDecodeTable()
    {
        sbyte[] table = new sbyte[256];
        Array.Fill(table, (sbyte)-1);
        for (int i = 0; i < Alphabet.Length; i++)
        {
            table[Alphabet[i]] = (sbyte)i;
        }

        return table.ToImmutableArray();
    }

    /// <summary>
    /// Returns the number of bytes that will be produced by encoding <paramref name="inputLength"/> bytes of input.
    /// </summary>
    /// <param name="inputLength">The number of bytes of input to be encoded.</param>
    /// <returns>The number of bytes that will be produced by encoding the input.</returns>
    public static int GetEncodedLength(int inputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);

        // Each 4 bytes of input produces 5 bytes of output, rounded up.
        return (inputLength / 4) * 5 + (inputLength % 4 == 0 ? 0 : 5);
    }

    /// <summary>
    /// Encodes <paramref name="data"/> as base85. Matches <c>git_str_encode_base85</c>.
    /// </summary>
    /// <param name="data">The input data to encode.</param>
    /// <param name="output">The span to receive the encoded characters.</param>
    /// <returns>The number of characters written to <paramref name="output"/>.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="output"/> is too small to hold the encoded data.</exception>
    public static int Encode(ReadOnlySpan<byte> data, Span<byte> output)
    {
        int blocks = (data.Length / 4) + (data.Length % 4 == 0 ? 0 : 1);

        if (output.Length < blocks * 5)
        {
            throw new ArgumentException("Output span is too small for the encoded data.", nameof(output));
        }

        Span<byte> b85 = stackalloc byte[5];

        int i = 0;
        int remaining = data.Length;
        while (remaining > 0)
        {
            uint acc = 0;
            for (int shift = 24; shift >= 0 && remaining > 0; shift -= 8)
            {
                acc |= (uint)data[i++] << shift;
                remaining--;
            }

            for (int j = 4; j >= 0; j--)
            {
                int val = (int)(acc % 85);
                acc /= 85;
                b85[j] = Alphabet[val];
            }

            b85.CopyTo(output);
            output = output.Slice(5);
        }

        return blocks * 5;
    }

    /// <summary>
    /// Encodes <paramref name="data"/> as base85. Matches
    /// <c>git_str_encode_base85</c>.
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(GetEncodedLength(data.Length));
        int length = Encode(data, buffer);
        string result = Encoding.ASCII.GetString(buffer, 0, length);
        ArrayPool<byte>.Shared.Return(buffer);
        return result;
    }

    /// <summary>
    /// Decodes a base85-encoded byte span. Matches <c>git_str_decode_base85</c>
    /// (str.c:410-468). Append the decoded bytes to the buffer writer. Return false on invalid input.
    /// </summary>
    /// <param name="base85">The base85-encoded input (length must be a multiple of 5).</param>
    /// <param name="outputLen">The expected number of decoded bytes.</param>
    /// <param name="writer">The buffer writer to receive the decoded bytes.</param>
    public static bool TryDecode(ReadOnlySpan<byte> base85, int outputLen, IBufferWriter<byte> writer)
    {
        if (base85.Length % 5 != 0 || outputLen > base85.Length * 4 / 5)
        {
            return false;
        }

        Span<byte> buf = writer.GetSpan(outputLen);
        int bi = 0;
        int si = 0;
        int remaining = outputLen;

        while (remaining > 0)
        {
            uint acc = 0;
            // Read 4 base85 digits → acc = d0*85^3 + d1*85^2 + d2*85 + d3.
            for (int cnt = 4; cnt > 0; cnt--)
            {
                sbyte de = s_decodeTable[base85[si++]];
                if (de < 0)
                {
                    return false;
                }
                acc = acc * 85 + (uint)de;
            }

            // Read the 5th digit and add.
            sbyte de5 = s_decodeTable[base85[si++]];
            if (de5 < 0)
            {
                return false;
            }

            // Overflow check: acc * 85 + de5 must not overflow uint.
            if (acc > (0xFFFFFFFFu / 85u) ||
                (acc * 85u) > (0xFFFFFFFFu - (uint)de5))
            {
                return false;
            }

            acc = acc * 85u + (uint)de5;

            // Emit cnt bytes (big-endian, matching encode's shift direction).
            int emit = remaining < 4 ? remaining : 4;
            remaining -= emit;
            for (int j = 0; j < emit; j++)
            {
                acc = (acc << 8) | (acc >> 24);
                buf[bi++] = (byte)acc;
            }
        }

        writer.Advance(outputLen);
        return true;
    }
}
