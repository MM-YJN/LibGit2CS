// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Buffers;

namespace LibGit2CS.Utils;

internal static class BufferWriterExtensions
{
    internal static int WriteSpanFormattable<T>(this IBufferWriter<byte> writer, T value, string? format = null, IFormatProvider? provider = null) where T : IUtf8SpanFormattable
    {
        Span<byte> span = writer.GetSpan();
        if (value.TryFormat(span, out int bytesWritten, format.AsSpan(), provider))
        {
            writer.Advance(bytesWritten);
            return bytesWritten;
        }
        else
        {
            // If the initial span was too small, we need to allocate a larger buffer.
            int requiredSize = Math.Max(256, span.Length);
            while (true)
            {
                requiredSize *= 2;
                span = writer.GetSpan(requiredSize);
                if (value.TryFormat(span, out bytesWritten, format.AsSpan(), provider))
                {
                    writer.Advance(bytesWritten);
                    return bytesWritten;
                }
            }
        }
    }

    /// <summary> Writes a single byte to the writer. <see cref="IBufferWriter{T}"/> has no single-item write API; this mirrors <see
    /// cref="PooledByteBufferWriter.Write(byte)"/> for the interface type. </summary>
    internal static void WriteByte(this IBufferWriter<byte> writer, byte value)
    {
        Span<byte> span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }
}
