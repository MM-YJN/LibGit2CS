// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Diagnostics.CodeAnalysis;

namespace LibGit2CS.Utils;

/// <summary> Byte-wise equality comparer for <see cref="ReadOnlyMemory{T}"/> (byte) keys — the managed equivalent of libc <c>strcmp</c>-keyed hashmaps (C's
/// <c>git_hashmap_str</c>, <c>src/util/hashmap_str.h:24-27</c>): equality is <c>SequenceEqual</c> over the bytes and the hash is FNV-1a 32-bit (<see
/// cref="ByteHash.Fnv1a"/>), consistent with <see cref="LibGit2CS.IO.GitPath"/> and the <see cref="LibGit2CS.Config.ConfigNameKey"/>/<see cref="LibGit2CS.Refs.RefNameKey"/> key types. </summary> <remarks>
/// Used for byte-keyed <see cref="HashSet{T}"/>s over raw name bytes (e.g. the submodule cache's .gitmodules name set) where the key type is a plain <see
/// cref="ReadOnlyMemory{T}"/> rather than a dedicated struct. </remarks>
internal sealed class ReadOnlyMemoryByteComparer : IEqualityComparer<ReadOnlyMemory<byte>>
{
    /// <summary>The shared instance.</summary>
    public static readonly ReadOnlyMemoryByteComparer Ordinal = new();

    private ReadOnlyMemoryByteComparer()
    {
    }

    /// <inheritdoc/>
    public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
        => x.Span.SequenceEqual(y.Span);

    /// <inheritdoc/>
    public int GetHashCode(ReadOnlyMemory<byte> obj) => ByteHash.Fnv1a(obj.Span);
}
