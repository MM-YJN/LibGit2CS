// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Diagnostics;
using System.Text;

namespace LibGit2CS.Utils;

internal ref partial struct ValueStringBuilder
{
    internal void Append(StringBuilder value)
    {
        EnsureCapacity(value.Length);
        foreach (ReadOnlyMemory<char> chunk in value.GetChunks())
        {
            Append(chunk.Span);
        }
    }

    internal void AppendUtf8(ReadOnlySpan<byte> utf8Bytes)
    {
        int charCount = Encoding.UTF8.GetCharCount(utf8Bytes);
        Span<char> span = AppendSpan(charCount);
        int charsWriten = Encoding.UTF8.GetChars(utf8Bytes, span);
        Debug.Assert(charsWriten == span.Length);
    }

    internal void Clear()
    {
        _pos = 0;
    }
}
