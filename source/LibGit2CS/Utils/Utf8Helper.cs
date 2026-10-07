// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Diagnostics;
using System.Text;

namespace LibGit2CS.Utils;

internal static class Utf8Helper
{
    internal static byte[] EncodeToByteArray(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        int byteCount = Encoding.UTF8.GetByteCount(chars);
        byte[] bytes = new byte[byteCount];
        int bytesWritten = Encoding.UTF8.GetBytes(chars, bytes);
        Debug.Assert(bytesWritten == bytes.Length);
        return bytes;
    }
}
