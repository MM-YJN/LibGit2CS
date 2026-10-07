// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace LibGit2CS.Utils;

internal readonly record struct DecimalPadLeftFormatter(long Value, int TotalWidth, char PaddingChar = '0')
    : ISpanFormattable, IUtf8SpanFormattable
{
    public override string ToString() => ToString(null, null);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(TotalWidth, "totalWidth");

        ulong value = unchecked((ulong)Value);
        int digitCount = GetDigitCount(value);
        int charsRequired = Math.Max(TotalWidth, digitCount);

        return string.Create(charsRequired, (value, digitCount, PaddingChar), static (destination, state) =>
        {
            int paddingCount = destination.Length - state.digitCount;
            destination[..paddingCount].Fill(state.PaddingChar);

            int index = destination.Length;
            ulong remaining = state.value;
            do
            {
                destination[--index] = (char)('0' + (remaining % 10));
                remaining /= 10;
            }
            while (remaining != 0);
        });
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ulong value = unchecked((ulong)Value);
        int digitCount = GetDigitCount(value);

        int paddingCount = TotalWidth > digitCount ? TotalWidth - digitCount : 0;
        int charsRequired = digitCount + paddingCount;
        if (destination.Length < charsRequired)
        {
            charsWritten = 0;
            return false;
        }

        destination[..paddingCount].Fill(PaddingChar);

        int index = charsRequired;
        do
        {
            destination[--index] = (char)('0' + (value % 10));
            value /= 10;
        }
        while (value != 0);

        charsWritten = charsRequired;
        return true;
    }

    private static int GetDigitCount(ulong value)
    {
        int digitCount = 1;
        while (value >= 10)
        {
            value /= 10;
            digitCount++;
        }

        return digitCount;
    }

    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ulong value = unchecked((ulong)Value);
        int digitCount = GetDigitCount(value);

        int paddingCount = TotalWidth > digitCount ? TotalWidth - digitCount : 0;
        char paddingChar = PaddingChar;
        Span<byte> encodedPadding = stackalloc byte[Encoding.UTF8.GetMaxByteCount(1)];
        int paddingByteCount = Encoding.UTF8.GetBytes(new ReadOnlySpan<char>(ref paddingChar), encodedPadding);

        long bytesRequiredLong = (long)paddingCount * paddingByteCount + digitCount;
        if (bytesRequiredLong > utf8Destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        int bytesRequired = (int)bytesRequiredLong;
        int paddingBytes = bytesRequired - digitCount;
        for (int index = 0; index < paddingBytes; index += paddingByteCount)
        {
            encodedPadding[..paddingByteCount].CopyTo(utf8Destination.Slice(index, paddingByteCount));
        }

        int digitIndex = bytesRequired;
        do
        {
            utf8Destination[--digitIndex] = (byte)('0' + (value % 10));
            value /= 10;
        }
        while (value != 0);

        bytesWritten = bytesRequired;
        return true;
    }
}
