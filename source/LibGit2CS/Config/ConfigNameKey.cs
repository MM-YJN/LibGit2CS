// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Diagnostics.CodeAnalysis;

using LibGit2CS.Utils;

namespace LibGit2CS.Config;

/// <summary> Byte-keyed dictionary key for config entry names. Wraps the raw fully-qualified name bytes (<see cref="GitConfigEntry.NameBytes"/>) with
/// hand-written byte-wise equality and FNV-1a hashing — the managed equivalent of libgit2's <c>git_config_list_headmap</c> keys, which are <c>char *</c> name
/// bytes compared with <c>strcmp</c> and hashed byte-wise (<c>src/util/hashmap_str.h</c>, <c>config_list.c:29</c>). </summary> <remarks> <para> This is a <see
/// langword="struct"/>, not a <c>record struct</c>: auto-generated record equality would compare the <see cref="ReadOnlyMemory{T}"/> field by reference+length,
/// not byte-for-byte (the same rationale as <see cref="LibGit2CS.IO.GitPath"/>, IO/GitPath.cs). Equality is <c>SequenceEqual</c> over the bytes; the hash is FNV-1a 32-bit
/// (<see cref="ByteHash.Fnv1a"/>), consistent with <see cref="LibGit2CS.IO.GitPath.GetHashCode()"/>. </para> <para> <see cref="ConfigList"/> keys its head map on this type so
/// non-UTF-8 subsection bytes round-trip byte-exact (C's <c>strcmp</c> over raw name bytes), instead of degrading to the U+FFFD display decode. </para>
/// </remarks>
internal readonly struct ConfigNameKey : IEquatable<ConfigNameKey>
{
    private readonly ReadOnlyMemory<byte> _bytes;

    private ConfigNameKey(ReadOnlyMemory<byte> bytes) => _bytes = bytes;

    /// <summary>Wraps an existing byte buffer (zero-copy).</summary>
    public static ConfigNameKey From(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>The raw name bytes.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <inheritdoc/>
    public bool Equals(ConfigNameKey other) => _bytes.Span.SequenceEqual(other._bytes.Span);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ConfigNameKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ByteHash.Fnv1a(_bytes.Span);

    public static bool operator ==(ConfigNameKey left, ConfigNameKey right) => left.Equals(right);

    public static bool operator !=(ConfigNameKey left, ConfigNameKey right) => !left.Equals(right);
}
