// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Pack;

/// <summary>
/// Applies a git delta stream to a base object, producing the result object.
/// Managed port of libgit2's <c>src/libgit2/delta.c:git_delta_apply</c> (read side).
/// </summary>
/// <remarks>
/// <para>
/// Delta stream format:
/// <code>
/// [base_size_varint]   — size of the base object (for validation)
/// [result_size_varint] — size of the result object
/// &lt;instructions&gt;
/// </code>
/// </para>
/// <para>
/// Each instruction byte:
/// <list type="bullet">
///   <item><b>COPY</b> (cmd &amp; 0x80): Copy from base.
///     <c>cmd &amp; 0x01</c>→offset byte 0, <c>cmd &amp; 0x02</c>→offset byte 1,
///     <c>cmd &amp; 0x04</c>→offset byte 2, <c>cmd &amp; 0x08</c>→offset byte 3,
///     <c>cmd &amp; 0x10</c>→size byte 0, <c>cmd &amp; 0x20</c>→size byte 1,
///     <c>cmd &amp; 0x40</c>→size byte 2. If no size bits set, len = 0x10000 (64 KB).</item>
///   <item><b>INSERT</b> (cmd &amp; 0x80 == 0, cmd != 0): The byte value itself is
///     the count of literal bytes to copy from the delta stream.</item>
///   <item><b>RESERVED</b> (cmd == 0): Invalid — reserved for future encodings.</item>
/// </list>
/// </para>
/// <para>
/// The varint size encoding (<c>hdr_sz</c>) is the standard 7-bit-per-byte
/// continuation scheme: each byte contributes 7 bits (low bits), MSB=1 means
/// continuation, MSB=0 means last byte.
/// </para>
/// </remarks>
public static class GitDeltaApplier
{
    /// <summary>
    /// Applies <paramref name="delta"/> to <paramref name="baseData"/>, producing
    /// the reconstructed object bytes. Matches <c>git_delta_apply</c>.
    /// </summary>
    /// <param name="baseData">The base object's raw bytes.</param>
    /// <param name="delta">The delta stream (starting with base-size and result-size varints).</param>
    /// <returns>The reconstructed object bytes.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if the delta is malformed, truncated, or the
    /// base size doesn't match.
    /// </exception>
    public static byte[] Apply(ReadOnlySpan<byte> baseData, ReadOnlySpan<byte> delta)
    {
        // Parse the base-size varint and validate against the actual base data length.
        (long baseSize, int offset) = ReadVarint(delta);
        if (baseSize != baseData.Length)
        {
            throw new GitException(
                GitErrorCode.Error,
                "failed to apply delta: base size does not match given data",
                GitErrorCategory.Invalid);
        }

        // Parse the result-size varint.
        (long resultSize, int deltaOffset) = ReadVarint(delta[offset..]);
        deltaOffset += offset;

        // the result
        // size is an attacker-controlled varint; values ≥ 2^31 (or negative
        // from bit-63 varints) made new byte[resultSize] throw an unhandled
        // OverflowException. C's GIT_ERROR_CHECK_ALLOC_ADD (delta.c:568-570)
        // returns a clean allocation error; the sibling DeltaEncoder.Apply
        // guards the same way (Core/DeltaEncoder.cs:562-569).
        if (resultSize is > int.MaxValue or < 0)
        {
            throw new GitException(GitErrorCode.Error, "failed to apply delta", GitErrorCategory.Invalid);
        }

        byte[] result = new byte[resultSize];
        int resultPos = 0;
        int deltaPos = deltaOffset;

        while (deltaPos < delta.Length)
        {
            byte cmd = delta[deltaPos++];

            if ((cmd & 0x80) != 0)
            {
                // COPY instruction: read offset and length from the delta stream.
                // C (delta.c:582-596) accumulates the offset into a size_t from
                // unsigned bytes — there is no sign issue. Accumulate into a
                // long here: an int would go negative when the top offset byte
                // has bit 7 set (offset >= 2^31), which would pass the range
                // check below and make Slice throw ArgumentOutOfRangeException
                // instead of the delta error.
                long copyOff = 0;
                int copyLen = 0;

                if ((cmd & 0x01) != 0)
                {
                    copyOff |= (long)ReadDeltaByte(delta, ref deltaPos);
                }

                if ((cmd & 0x02) != 0)
                {
                    copyOff |= (long)ReadDeltaByte(delta, ref deltaPos) << 8;
                }

                if ((cmd & 0x04) != 0)
                {
                    copyOff |= (long)ReadDeltaByte(delta, ref deltaPos) << 16;
                }

                if ((cmd & 0x08) != 0)
                {
                    copyOff |= (long)ReadDeltaByte(delta, ref deltaPos) << 24;
                }

                if ((cmd & 0x10) != 0)
                {
                    copyLen |= ReadDeltaByte(delta, ref deltaPos);
                }

                if ((cmd & 0x20) != 0)
                {
                    copyLen |= ReadDeltaByte(delta, ref deltaPos) << 8;
                }

                if ((cmd & 0x40) != 0)
                {
                    copyLen |= ReadDeltaByte(delta, ref deltaPos) << 16;
                }

                if (copyLen == 0)
                {
                    copyLen = 0x10000; // 64 KB default
                }

                // Validate: offset + length must fit within base, and within result.
                // Matches GIT_ADD_SIZET_OVERFLOW + base_len < end + res_sz < len.
                if ((ulong)copyOff + (uint)copyLen > (ulong)baseData.Length || resultPos + copyLen > resultSize)
                {
                    throw new GitException(GitErrorCode.Error, "failed to apply delta", GitErrorCategory.Invalid);
                }

                baseData.Slice((int)copyOff, copyLen).CopyTo(result.AsSpan(resultPos));
                resultPos += copyLen;
            }
            else if (cmd != 0)
            {
                // INSERT instruction: copy `cmd` literal bytes from the delta stream.
                if (delta.Length - deltaPos < cmd || resultPos + cmd > resultSize)
                {
                    throw new GitException(GitErrorCode.Error, "failed to apply delta", GitErrorCategory.Invalid);
                }

                delta[deltaPos..(deltaPos + cmd)].CopyTo(result.AsSpan(resultPos));
                deltaPos += cmd;
                resultPos += cmd;
            }
            else
            {
                // cmd == 0 is reserved.
                throw new GitException(GitErrorCode.Error, "failed to apply delta", GitErrorCategory.Invalid);
            }
        }

        // The delta must be fully consumed and the result must be exactly filled.
        if (deltaPos != delta.Length || resultPos != resultSize)
        {
            throw new GitException(GitErrorCode.Error, "failed to apply delta", GitErrorCategory.Invalid);
        }

        return result;

        // Reads one byte from the delta stream at the current position and advances.
        // Throws if the stream is exhausted. Static local — no capture of ref struct.
        static int ReadDeltaByte(ReadOnlySpan<byte> span, ref int pos)
        {
            if (pos >= span.Length)
            {
                throw new GitException(GitErrorCode.Error, "failed to apply delta: truncated", GitErrorCategory.Invalid);
            }

            return span[pos++];
        }
    }

    /// <summary>
    /// Reads the base-size and result-size varints from the delta header without
    /// applying the delta. Matches <c>git_delta_read_header</c>.
    /// </summary>
    /// <returns>The (baseSize, resultSize) pair.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if the delta header is truncated.
    /// </exception>
    public static (long BaseSize, long ResultSize) ReadHeader(ReadOnlySpan<byte> delta)
    {
        (long baseSize, int offset1) = ReadVarint(delta);
        (long resultSize, int _) = ReadVarint(delta[offset1..]);
        return (baseSize, resultSize);
    }

    /// <summary>
    /// Reads a 7-bit-per-byte continuation varint (the size encoding used in delta
    /// headers). Matches <c>hdr_sz</c> in <c>delta.c</c>.
    /// </summary>
    /// <returns>The parsed value and the number of bytes consumed.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if the stream is truncated or overflows.
    /// </exception>
    private static (long Value, int BytesConsumed) ReadVarint(ReadOnlySpan<byte> span)
    {
        long result = 0;
        int shift = 0;
        int pos = 0;

        while (true)
        {
            if (pos >= span.Length)
            {
                throw new GitException(GitErrorCode.Error, "truncated delta", GitErrorCategory.Invalid);
            }

            if (shift >= 64)
            {
                throw new GitException(GitErrorCode.Error, "delta header overflow", GitErrorCategory.Invalid);
            }

            byte c = span[pos++];
            result |= (long)(c & 0x7f) << shift;
            shift += 7;

            if ((c & 0x80) == 0)
            {
                break;
            }
        }

        return (result, pos);
    }
}
