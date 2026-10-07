// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Buffers;
using System.Text;

namespace LibGit2CS.Utils;

/// <summary>
/// ASCII-only text trimming helpers matching C's <c>git__isspace</c>-based
/// trimming (str.c <c>git_str_rtrim</c>, util.c <c>git__isspace</c>): C
/// trims ONLY the ASCII whitespace set (<c> \t\n\v\f\r</c>) and never
/// Unicode whitespace, unlike <see cref="string.TrimEnd()"/>.
/// </summary>
internal static class AsciiText
{
    public static int BytewiseCompare(string a, string b) => BytewiseCompare(a.AsSpan(), b.AsSpan());

    /// <summary>
    /// Byte-wise string comparison over the UTF-8 encodings, matching C's
    /// <c>git__strcmp</c> order:
    /// StringComparer.Ordinal orders UTF-16 code units, which diverges for
    /// non-BMP (surrogate-pair) ref names.
    /// </summary>
    public static int BytewiseCompare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(a) + Encoding.UTF8.GetByteCount(b));
        int aLength = Encoding.UTF8.GetBytes(a, buffer);
        int bLength = Encoding.UTF8.GetBytes(b, buffer.AsSpan(aLength));
        ReadOnlySpan<byte> ab = buffer.AsSpan(0, aLength);
        ReadOnlySpan<byte> bb = buffer.AsSpan(aLength, bLength);
        int result = ab.SequenceCompareTo(bb);
        ArrayPool<byte>.Shared.Return(buffer);
        return result;
    }

    /// <summary>
    /// Trims trailing ASCII whitespace. Matches <c>git_str_rtrim</c>
    /// (str.c:869-879).
    /// </summary>
    public static ReadOnlySpan<char> Rtrim(ReadOnlySpan<char> value)
    {
        int end = value.Length;
        while (end > 0 && IsAsciiSpace(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }

    /// <summary>
    /// Trims trailing ASCII whitespace (byte domain). Matches
    /// <c>git_str_rtrim</c> (str.c:869-879) over a raw byte buffer.
    /// </summary>
    public static ReadOnlySpan<byte> Rtrim(ReadOnlySpan<byte> value)
    {
        int end = value.Length;
        while (end > 0 && IsAsciiSpace((char)value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }

    /// <summary>Matches C's <c>git__isspace</c> (util.c:729-735).</summary>
    internal static bool IsAsciiSpace(char c)
        => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    /// <summary>Matches C's <c>git__isspace</c> over a byte (util.c:729-735).</summary>
    internal static bool IsAsciiSpace(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r';
}
