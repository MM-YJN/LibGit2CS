// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Utils;

/// <summary> Byte-wise ordinal comparer for <see cref="ReadOnlyMemory{T}"/> (byte) keys — the managed equivalent of libc <c>strcmp</c> over byte buffers (C's
/// <c>git__strcmp</c>, <c>src/util/util.h:146</c>). The shorter buffer sorts before a longer buffer of which it is a prefix (matches the <c>NUL &lt; any
/// byte</c> rule of <c>strcmp</c>). </summary> <remarks> used for the byte-keyed sorted collections (packed-refs write/enumerate ordering), where C's
/// sortedcache walks entries in <c>strcmp</c> order over the raw refname bytes. </remarks>
internal sealed class ByteOrdinalComparer : IComparer<ReadOnlyMemory<byte>>
{
    /// <summary>The shared instance.</summary>
    public static readonly ByteOrdinalComparer Instance = new();

    private ByteOrdinalComparer()
    {
    }

    /// <inheritdoc/>
    public int Compare(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
    {
        ReadOnlySpan<byte> sx = x.Span;
        ReadOnlySpan<byte> sy = y.Span;
        int min = Math.Min(sx.Length, sy.Length);
        for (int i = 0; i < min; i++)
        {
            int diff = sx[i] - sy[i];
            if (diff != 0)
            {
                return diff;
            }
        }

        return sx.Length - sy.Length;
    }
}
