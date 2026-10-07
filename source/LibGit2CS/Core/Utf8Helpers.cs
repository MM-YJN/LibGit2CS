// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

/*
 * git_utf8_iterate is taken from the utf8proc project,
 * http://www.public-software-group.org/utf8proc
 *
 * Copyright (c) 2009 Public Software Group e. V., Berlin, Germany
 *
 * Permission is hereby granted, free of charge, to any person obtaining a
 * copy of this software and associated documentation files (the ""Software""),
 * to deal in the Software without restriction, including without limitation
 * the rights to use, copy, modify, merge, publish, distribute, sublicense,
 * and/or sell copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
 * DEALINGS IN THE SOFTWARE.
 */

namespace LibGit2CS.Core;

/// <summary>
/// UTF-8 boundary helpers. Managed port of libgit2's <c>src/util/utf8.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Provides byte-level UTF-8 iteration and validation. Most of the codebase uses
/// <c>System.Text.Rune</c> or <c>Encoding.UTF8</c> for higher-level operations,
/// but a few hot paths (e.g., <c>path.c</c>, Windows path-length
/// checks) need direct byte-level control: decode one codepoint from a
/// <c>ReadOnlySpan&lt;byte&gt;</c>, count codepoints in a byte buffer, and find
/// the length of the valid UTF-8 prefix.
/// </para>
/// <para>
/// The algorithm is from the <c>utf8proc</c> project (MIT-licensed), matching
/// libgit2's <c>git_utf8_iterate</c> / <c>git_utf8_char_length</c> /
/// <c>git_utf8_valid_buf_length</c>.
/// </para>
/// </remarks>
internal static class Utf8Helpers
{
    /// <summary>
    /// UTF-8 leading-byte classification: 0 = invalid/continuation,
    /// 1 = single-byte (ASCII), 2 = 2-byte start, 3 = 3-byte start,
    /// 4 = 4-byte start.
    /// </summary>
    private static ReadOnlySpan<byte> Utf8Class
    => [
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        4, 4, 4, 4, 4, 4, 4, 4, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>
    /// Decodes one UTF-8 codepoint from a byte span. Matches
    /// <c>git_utf8_iterate</c>.
    /// </summary>
    /// <param name="bytes">The byte buffer to read from.</param>
    /// <param name="codepoint">The decoded Unicode codepoint (0 on error).</param>
    /// <returns>The byte length of the decoded codepoint, or -1 on invalid
    /// UTF-8.</returns>
    public static int Iterate(ReadOnlySpan<byte> bytes, out uint codepoint)
    {
        codepoint = 0;
        if (bytes.IsEmpty)
        {
            return -1;
        }

        byte length = Utf8Class[bytes[0]];
        if (length == 0 || length > bytes.Length)
        {
            return -1;
        }

        // Validate continuation bytes.
        for (int i = 1; i < length; i++)
        {
            if ((bytes[i] & 0xC0) != 0x80)
            {
                return -1;
            }
        }

        uint uc;
        switch (length)
        {
            case 1:
                uc = bytes[0];
                break;
            case 2:
                uc = (uint)((bytes[0] & 0x1F) << 6) + (uint)(bytes[1] & 0x3F);
                if (uc < 0x80)
                {
                    return -1;
                }

                break;
            case 3:
                uc = (uint)((bytes[0] & 0x0F) << 12)
                    + (uint)((bytes[1] & 0x3F) << 6)
                    + (uint)(bytes[2] & 0x3F);
                if (uc is < 0x800 or >= 0xD800 and < 0xE000 or
                    >= 0xFDD0 and < 0xFDF0)
                {
                    return -1;
                }

                break;
            case 4:
                uc = (uint)((bytes[0] & 0x07) << 18)
                    + (uint)((bytes[1] & 0x3F) << 12)
                    + (uint)((bytes[2] & 0x3F) << 6)
                    + (uint)(bytes[3] & 0x3F);
                if (uc is < 0x10000 or >= 0x110000)
                {
                    return -1;
                }

                break;
            default:
                return -1;
        }

        // Reject non-characters (U+FFFE, U+FFFF, etc.).
        if ((uc & 0xFFFF) >= 0xFFFE)
        {
            return -1;
        }

        codepoint = uc;
        return length;
    }

    /// <summary>
    /// Counts the number of UTF-8 codepoints in a byte buffer. Matches
    /// <c>git_utf8_char_length</c>. Invalid bytes are counted as 1 codepoint
    /// each (matching libgit2's fallback behavior).
    /// </summary>
    public static int CharLength(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        int count = 0;
        while (offset < bytes.Length)
        {
            int length = CharLen(bytes[offset..]);
            if (length < 0)
            {
                length = 1;
            }

            offset += length;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Finds the length of the longest valid UTF-8 prefix. Matches
    /// <c>git_utf8_valid_buf_length</c>. Stops at the first invalid byte
    /// sequence.
    /// </summary>
    public static int ValidBufLength(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            int length = CharLen(bytes[offset..]);
            if (length < 0)
            {
                break;
            }

            offset += length;
        }

        return offset;
    }

    /// <summary>
    /// Determines the byte length of the first UTF-8 character, without
    /// decoding it. Matches <c>utf8_charlen</c> (static in C).
    /// </summary>
    private static int CharLen(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return -1;
        }

        byte length = Utf8Class[bytes[0]];
        if (length == 0 || length > bytes.Length)
        {
            return -1;
        }

        for (int i = 1; i < length; i++)
        {
            if ((bytes[i] & 0xC0) != 0x80)
            {
                return -1;
            }
        }

        return length;
    }
}
