// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Utils;

/// <summary> Byte-span hashing helpers shared by the byte-keyed dictionary key types. FNV-1a 32-bit over the raw bytes — the same algorithm as <see
/// cref="LibGit2CS.IO.GitPath.GetHashCode()"/> (IO/GitPath.cs), kept in one place so every byte-keyed type hashes identically. </summary>
internal static class ByteHash
{
    /// <summary>
    /// FNV-1a 32-bit hash over the raw bytes. Consistent with byte-wise
    /// equality (<see cref="MemoryExtensions.SequenceEqual{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>), good
    /// distribution for short keys.
    /// </summary>
    public static int Fnv1a(ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            int hash = (int)2166136261u;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash = (hash ^ bytes[i]) * 16777619;
            }

            return hash;
        }
    }
}
