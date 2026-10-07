// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Text;

using LibGit2CS.Core;

using LibGit2CS.Utils;

namespace LibGit2CS.Refs;

/// <summary> Byte-keyed dictionary key for reference names. Wraps the raw refname bytes with hand-written byte-wise equality and FNV-1a hashing — the managed
/// equivalent of libgit2's packed-refs store, whose entries carry the refname as inline <c>char name[]</c> bytes compared with <c>strcmp</c>
/// (<c>refdb_fs.c:46-51, 107-110</c>). </summary> <remarks> <para> This is a <see langword="struct"/>, not a <c>record struct</c>: auto-generated record
/// equality would compare the <see cref="ReadOnlyMemory{T}"/> field by reference+length, not byte-for-byte (the same rationale as <see cref="LibGit2CS.IO.GitPath"/>,
/// IO/GitPath.cs). Equality is <c>SequenceEqual</c> over the bytes; the hash is FNV-1a 32-bit (<see cref="ByteHash.Fnv1a"/>), consistent with <see
/// cref="LibGit2CS.IO.GitPath.GetHashCode()"/>. </para> <para> <c>FileRefBackend._packedRefs</c> keys on this type so non-UTF-8 refnames round-trip byte-exact (C's
/// <c>strcmp</c> over raw refname bytes), instead of degrading to the U+FFFD display decode. </para> </remarks>
internal readonly struct RefNameKey : IEquatable<RefNameKey>
{
    private readonly ReadOnlyMemory<byte> _bytes;

    private RefNameKey(ReadOnlyMemory<byte> bytes) => _bytes = bytes;

    /// <summary>Wraps an existing byte buffer (zero-copy).</summary>
    public static RefNameKey From(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>The raw refname bytes.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    public ReadOnlySpan<byte> Span => _bytes.Span;
    public int Length => _bytes.Length;
    public bool IsEmpty => _bytes.IsEmpty;
    public static implicit operator RefNameKey(string value)
        => From(Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value))));
    public override string ToString() => Encoding.UTF8.GetString(Span);
    public bool StartsWith(ReadOnlySpan<byte> prefix) => Span.StartsWith(prefix);

    public bool TryGetFileSystemString([NotNullWhen(true)] out string? value)
    {
        try
        {
            value = new UTF8Encoding(false, true).GetString(Span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            value = null;
            return false;
        }
    }
    public string ToFileSystemString()
        => TryGetFileSystemString(out string? value) ? value
            : throw new GitException(GitErrorCode.InvalidSpec,
                "Reference name cannot be represented losslessly by the managed filesystem API.", GitErrorCategory.Reference);
    // Existing text-only consumers must fail explicitly, never turn replacement characters into identifiers.
    public string ToUtf8StringStrict()
        => TryGetFileSystemString(out string? value) ? value
            : throw new GitException(GitErrorCode.InvalidSpec,
                "Reference name cannot be represented losslessly by this text-only operation.", GitErrorCategory.Reference);
    public byte[] SymbolicContent() => [.. "ref: "u8, .. Span, (byte)'\n'];

    /// <inheritdoc/>
    public bool Equals(RefNameKey other) => _bytes.Span.SequenceEqual(other._bytes.Span);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is RefNameKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ByteHash.Fnv1a(_bytes.Span);

    public static bool operator ==(RefNameKey left, RefNameKey right) => left.Equals(right);

    public static bool operator !=(RefNameKey left, RefNameKey right) => !left.Equals(right);
}

/// <summary> Byte-ordinal comparer for <see cref="RefNameKey"/> — the managed equivalent of C's <c>packref_cmp</c> (<c>strcmp</c> over the raw refname bytes,
/// refdb_fs.c:107-110). Used by the packed-refs write path's sorted dictionary. </summary>
internal sealed class RefNameKeyComparer : IComparer<RefNameKey>
{
    /// <summary>The shared instance.</summary>
    public static readonly RefNameKeyComparer Ordinal = new();

    private RefNameKeyComparer()
    {
    }

    /// <inheritdoc/>
    public int Compare(RefNameKey x, RefNameKey y)
    {
        ReadOnlySpan<byte> sx = x.Bytes.Span;
        ReadOnlySpan<byte> sy = y.Bytes.Span;
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
