// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.Core;

/// <summary>
/// Unique identity of a git object (commit, tree, blob, tag).
/// Managed equivalent of libgit2's <c>git_oid</c>.
/// </summary>
/// <remarks>
/// Dual-mode: carries its <see cref="Algorithm"/> so both SHA-1 (20 bytes) and
/// SHA-256 (32 bytes) OIDs coexist. Backing storage is a heap-allocated
/// <c>byte[]</c>; equality and hashing are byte-wise, not reference-based.
/// </remarks>
public readonly record struct GitOid : IEquatable<GitOid>, IComparable<GitOid>, ISpanFormattable, IUtf8SpanFormattable
{
    // Hex lookup tables (index by nibble / by byte).
    private static ReadOnlySpan<byte> HexUpper => "0123456789abcdef"u8;

    private readonly byte[]? _bytes;
    private readonly int _hexLength;

    private GitOid(GitHashAlgorithmKind algorithm, byte[] bytes)
        : this(algorithm, bytes, bytes.Length * 2)
    {
    }

    private GitOid(GitHashAlgorithmKind algorithm, byte[] bytes, int hexLength)
    {
        Algorithm = algorithm;
        _bytes = bytes;
        _hexLength = hexLength;
    }

    /// <summary>
    /// The hash algorithm used to produce this OID.
    /// </summary>
    public GitHashAlgorithmKind Algorithm { get; }

    /// <summary>
    /// Raw bytes of the OID (20 for SHA-1, 32 for SHA-256).
    /// Returns an empty span for an uninitialized (<c>default</c>) OID.
    /// </summary>
    public ReadOnlySpan<byte> RawBytes => _bytes is null ? ReadOnlySpan<byte>.Empty : _bytes;

    /// <summary>
    /// Raw byte size for this OID's algorithm.
    /// A <c>default</c> OID (Algorithm = 0) reports SHA-1 size for libgit2 parity.
    /// </summary>
    public int Size => Algorithm switch
    {
        GitHashAlgorithmKind.Sha256 => SHA256.HashSizeInBytes,
        _ => SHA1.HashSizeInBytes,
    };

    /// <summary>
    /// Hex character count for this OID's algorithm (twice <see cref="Size"/>).
    /// </summary>
    public int HexSize => Size * 2;

    /// <summary>
    /// The number of hex characters that were specified when this OID was parsed.
    /// For a full OID this equals <see cref="HexSize"/>. For an abbreviated OID
    /// (e.g. parsed from <c>"a65fedf39"</c>) this is the actual prefix length
    /// (9). Used by prefix-lookup (<c>ExistsPrefix</c>) to distinguish a partial
    /// prefix from a full OID. Defaults to <see cref="HexSize"/> when unset.
    /// </summary>
    public int HexLength => _hexLength == 0 ? HexSize : _hexLength;

    /// <summary>
    /// True if every byte is zero. A <c>default</c> OID is considered zero.
    /// </summary>
    public bool IsZero
    {
        get
        {
            if (_bytes is null)
            {
                return true;
            }

            foreach (byte b in _bytes)
            {
                if (b != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// A zero (null) SHA-1 OID. Matches libgit2's <c>GIT_OID_SHA1_ZERO</c>.
    /// </summary>
    public static GitOid Empty { get; } = new(GitHashAlgorithmKind.Sha1, new byte[SHA1.HashSizeInBytes]);

    /// <summary>
    /// SHA-1 of the empty blob: <c>e69de29bb2d1d6434b8b29ae775ad8c2e48c5391</c>.
    /// Matches libgit2's <c>git_oid__empty_blob_sha1</c>.
    /// </summary>
    public static GitOid EmptyBlobSha1 { get; } = FromRaw(
        [0xe6, 0x9d, 0xe2, 0x9b, 0xb2, 0xd1, 0xd6, 0x43, 0x4b, 0x8b,
         0x29, 0xae, 0x77, 0x5a, 0xd8, 0xc2, 0xe4, 0x8c, 0x53, 0x91],
        GitHashAlgorithmKind.Sha1);

    /// <summary>
    /// SHA-1 of the empty tree: <c>4b825dc642cb6eb9a060e54bf8d69288fbee4904</c>.
    /// Matches libgit2's <c>git_oid__empty_tree_sha1</c>.
    /// </summary>
    public static GitOid EmptyTreeSha1 { get; } = FromRaw(
        [0x4b, 0x82, 0x5d, 0xc6, 0x42, 0xcb, 0x6e, 0xb9, 0xa0, 0x60,
         0xe5, 0x4b, 0xf8, 0xd6, 0x92, 0x88, 0xfb, 0xee, 0x49, 0x04],
        GitHashAlgorithmKind.Sha1);

    /// <summary>
    /// SHA-256 of the empty blob. Computed at init from <c>SHA256("blob 0\0")</c>.
    /// </summary>
    public static GitOid EmptyBlobSha256 { get; } = ComputeEmptyObjectHash("blob"u8, GitHashAlgorithmKind.Sha256);

    /// <summary>
    /// SHA-256 of the empty tree. Computed at init from <c>SHA256("tree 0\0")</c>.
    /// </summary>
    public static GitOid EmptyTreeSha256 { get; } = ComputeEmptyObjectHash("tree"u8, GitHashAlgorithmKind.Sha256);

    /// <summary>
    /// Computes the OID of an empty git object of the given type by hashing its
    /// header (<c>"&lt;type&gt; 0\0"</c>). Used for the well-known empty blob/tree OIDs.
    /// </summary>
    [SuppressMessage("Security", "CA5350:Do not use insecure cryptographic algorithms", Justification = "SHA1 is required by git's object format; not used for security")]
    private static GitOid ComputeEmptyObjectHash(ReadOnlySpan<byte> type, GitHashAlgorithmKind kind)
    {
        // git object format: "<type> <size>\0<content>". Empty object has size 0 and no content.
        Debug.Assert(type.Length <= 4); // "blob" or "tree" fits in 4 bytes; no other types are used here.

        Span<byte> header = stackalloc byte[type.Length + 3]; // type + space + size + null
        type.CopyTo(header);
        " 0\0"u8.CopyTo(header.Slice(type.Length));

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        int size;
        if (kind == GitHashAlgorithmKind.Sha256)
        {
            SHA256.HashData(header, hash);
            size = SHA256.HashSizeInBytes;
        }
        else
        {
            SHA1.HashData(header, hash);
            size = SHA1.HashSizeInBytes;
        }

        return FromRaw(hash.Slice(0, size), kind);
    }

    /// <summary>
    /// Parses a hex-formatted OID of the given algorithm.
    /// </summary>
    /// <exception cref="FormatException">Input has invalid length or characters.</exception>
    public static GitOid Parse(ReadOnlySpan<char> hex, GitHashAlgorithmKind kind)
    {
        if (!TryParse(hex, kind, out GitOid oid))
        {
            throw new FormatException(
                $"Unable to parse OID: invalid hex string (length {hex.Length}, expected {SizeFor(kind) * 2} for {kind}).");
        }

        return oid;
    }

    /// <summary>
    /// Parses a hex-formatted OID. Accepts strings shorter than the full hex size
    /// (for abbreviated OIDs); the last byte's low nibble is zeroed if the length is odd.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> hex, GitHashAlgorithmKind kind, out GitOid value)
    {
        int size = SizeFor(kind);
        int hexSize = size * 2;

        if (hex.Length == 0 || hex.Length > hexSize)
        {
            value = default;
            return false;
        }

        byte[] bytes = new byte[size];
        for (int i = 0; i < hex.Length; i++)
        {
            int nibble = FromHex(hex[i]);
            if (nibble < 0)
            {
                value = default;
                return false;
            }

            bytes[i / 2] |= (byte)(nibble << (i % 2 == 0 ? 4 : 0));
        }

        value = new GitOid(kind, bytes, hex.Length);
        return true;
    }

    /// <summary>
    /// Parses a hex-formatted OID from raw bytes (the byte domain used by the
    /// patch parser, which walks the patch buffer directly). Same semantics as
    /// the <see cref="TryParse(ReadOnlySpan{char}, GitHashAlgorithmKind, out GitOid)"/>
    /// overload.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> hex, GitHashAlgorithmKind kind, out GitOid value)
    {
        int size = SizeFor(kind);
        int hexSize = size * 2;

        if (hex.Length == 0 || hex.Length > hexSize)
        {
            value = default;
            return false;
        }

        byte[] bytes = new byte[size];
        for (int i = 0; i < hex.Length; i++)
        {
            int nibble = FromHex((char)hex[i]);
            if (nibble < 0)
            {
                value = default;
                return false;
            }

            bytes[i / 2] |= (byte)(nibble << (i % 2 == 0 ? 4 : 0));
        }

        value = new GitOid(kind, bytes, hex.Length);
        return true;
    }

    internal static GitOid ComputeOid(GitObjectType type, ReadOnlySpan<byte> content, GitHashAlgorithmKind kind)
    {
        Span<byte> headerBuffer = stackalloc byte[32];
        int typeLength = WriteType(headerBuffer, type);
        bool formatResult = content.Length.TryFormat(headerBuffer.Slice(typeLength), out int bytesWritten, provider: CultureInfo.InvariantCulture);
        Debug.Assert(formatResult);

        bytesWritten += typeLength;
        Debug.Assert(bytesWritten < headerBuffer.Length - 1);

        headerBuffer[bytesWritten++] = 0;

        using var hasher = GitIncrementalHash.Create(kind);
        hasher.AppendData(headerBuffer.Slice(0, bytesWritten));
        hasher.AppendData(content);
        return hasher.Finalize();
    }

    /// <summary>
    /// Formats a git object header: <c>"&lt;type&gt; &lt;size&gt;\0"</c>.
    /// Matches <c>git_odb__format_object_header</c>.
    /// </summary>
    internal static void WriteHeader(IBufferWriter<byte> writer, GitObjectType type, int size)
    {
        Span<byte> buffer = writer.GetSpan(32);
        int bytesWritten = WriteType(buffer, type);
        bool formatResult = size.TryFormat(buffer.Slice(bytesWritten), out int sizeBytesWritten, provider: CultureInfo.InvariantCulture);
        Debug.Assert(formatResult);
        bytesWritten += sizeBytesWritten;
        buffer[bytesWritten++] = 0;
        writer.Advance(bytesWritten);
    }

    /// <summary>
    /// Formats a git object header: <c>"&lt;type&gt; &lt;size&gt;\0"</c>.
    /// Matches <c>git_odb__format_object_header</c>.
    /// </summary>
    internal static int WriteHeader(Span<byte> destination, GitObjectType type, long size)
    {
        // We assume the caller has allocated enough space for the header (type + space + size + null).
        int bytesWritten = WriteType(destination, type);
        bool formatResult = size.TryFormat(destination.Slice(bytesWritten), out int sizeBytesWritten, provider: CultureInfo.InvariantCulture);
        Debug.Assert(formatResult);
        bytesWritten += sizeBytesWritten;
        destination[bytesWritten++] = 0;
        return bytesWritten;
    }

    private static int WriteType(Span<byte> destination, GitObjectType type)
    {
        // Writes "<type> " (with the trailing separator space) — matches the
        // "%s %ld" format of git_odb__format_object_header (odb.c:97).
        switch (type)
        {
            case GitObjectType.Commit:
                "commit "u8.CopyTo(destination);
                return 7;
            case GitObjectType.Tree:
                "tree "u8.CopyTo(destination);
                return 5;
            case GitObjectType.Blob:
                "blob "u8.CopyTo(destination);
                return 5;
            case GitObjectType.Tag:
                "tag "u8.CopyTo(destination);
                return 4;
            case GitObjectType.OfsDelta:
                "OFS_DELTA "u8.CopyTo(destination);
                return 10;
            case GitObjectType.RefDelta:
                "REF_DELTA "u8.CopyTo(destination);
                return 10;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Creates an OID from raw bytes. The span length must match the algorithm's size.
    /// </summary>
    /// <exception cref="ArgumentException">Raw length doesn't match the algorithm.</exception>
    public static GitOid FromRaw(ReadOnlySpan<byte> raw, GitHashAlgorithmKind kind)
    {
        int size = SizeFor(kind);
        if (raw.Length != size)
        {
            throw new ArgumentException(
                $"Raw OID length {raw.Length} doesn't match {kind} size {size}.", nameof(raw));
        }

        byte[] bytes = new byte[size];
        raw.CopyTo(bytes);
        return new GitOid(kind, bytes);
    }

    /// <summary>
    /// Returns a copy of this OID with <see cref="HexLength"/> set to
    /// <paramref name="hexLength"/>. Used by <see cref="Objects.GitObjectDb.ExpandIdsAsync"/>
    /// to normalize an <see cref="Objects.GitOdbExpandId.Id"/> so that
    /// <see cref="Objects.GitObjectDb.ExistsPrefixAsync"/> uses the right number of nibbles
    /// (the C <c>git_odb_expand_ids</c> passes <c>query-&gt;length</c> separately to
    /// <c>odb_exists_prefix_1</c>; the C# <see cref="LibGit2CS.Objects.GitObjectDb.ExistsPrefixAsync"/> infers
    /// the length from <see cref="HexLength"/>, so the OID must carry the right value).
    /// </summary>
    /// <param name="hexLength">
    /// The hex prefix length in nibbles. Clamped to [0, <see cref="HexSize"/>].
    /// </param>
    internal GitOid WithHexLength(int hexLength)
    {
        int clamped = Math.Clamp(hexLength, 0, HexSize);
        if (clamped == HexLength)
        {
            return this;
        }

        return new GitOid(Algorithm, _bytes ?? [], clamped);
    }

    /// <summary>
    /// Formats the OID as a lowercase hex string.
    /// </summary>
    public override string ToString()
        => ToString(null, null);

    /// <summary>Returns the Git textual representation of this value.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (_bytes is null)
        {
            return new string('0', HexSize);
        }

        return string.Create(HexSize, this, static (span, oid) => oid.FormatHex(span));
    }

    /// <summary>Formats this value into the destination; returns false if the buffer is too small.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (destination.Length < HexSize)
        {
            charsWritten = 0;
            return false;
        }

        FormatHex(destination);
        charsWritten = HexSize;
        return true;
    }

    /// <summary>Formats this value into the destination; returns false if the buffer is too small.</summary>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (utf8Destination.Length < HexSize)
        {
            bytesWritten = 0;
            return false;
        }

        FormatHex(utf8Destination);
        bytesWritten = HexSize;
        return true;
    }

    /// <summary>
    /// Formats into <paramref name="destination"/> as hex.
    /// <paramref name="destination"/> must be at least <see cref="HexSize"/> chars.
    /// </summary>
    public int FormatHex(Span<char> destination)
    {
        if (destination.Length < HexSize)
        {
            throw new ArgumentException($"Destination span must be at least {HexSize} chars.", nameof(destination));
        }

        if (_bytes is null)
        {
            destination[..HexSize].Fill('0');
            return HexSize;
        }

        ReadOnlySpan<byte> hexUpper = HexUpper;

        int size = Size;
        for (int i = 0; i < size; i++)
        {
            destination[i * 2] = (char)hexUpper[_bytes[i] >> 4];
            destination[i * 2 + 1] = (char)hexUpper[_bytes[i] & 0x0f];
        }

        return HexSize;
    }

    /// <summary>
    /// Formats into <paramref name="destination"/> as hex encoded in ASCII.
    /// <paramref name="destination"/> must be at least <see cref="HexSize"/> bytes.
    /// </summary>
    public int FormatHex(Span<byte> destination)
    {
        if (destination.Length < HexSize)
        {
            throw new ArgumentException($"Destination span must be at least {HexSize} bytes.", nameof(destination));
        }

        if (_bytes is null)
        {
            destination[..HexSize].Fill((byte)'0');
            return HexSize;
        }

        ReadOnlySpan<byte> hexUpper = HexUpper;

        int size = Size;
        for (int i = 0; i < size; i++)
        {
            destination[i * 2] = hexUpper[_bytes[i] >> 4];
            destination[i * 2 + 1] = hexUpper[_bytes[i] & 0x0f];
        }

        return HexSize;
    }

    /// <summary>
    /// Formats as a loose-object path: <c>aa/bbcc...</c> (first 2 hex chars, slash, rest).
    /// Matches <c>git_oid_pathfmt</c> (<c>oid.c:145-156</c>) exactly: 2-char prefix
    /// + <c>/</c> + remaining hex chars.
    /// </summary>
    public string ToPathString()
    {
        if (_bytes is null)
        {
            return "00/" + new string('0', HexSize - 2);
        }

        return string.Create(HexSize + 1, this, static (span, oid) =>
        {
            byte[]? bytes = oid._bytes;
            Debug.Assert(bytes is not null, "_bytes is non-null (checked before lambda)");

            ReadOnlySpan<byte> hexUpper = HexUpper;

            // First 2 hex chars (1 byte)
            span[0] = (char)hexUpper[bytes[0] >> 4];
            span[1] = (char)hexUpper[bytes[0] & 0x0f];
            span[2] = '/';
            // Remaining hex chars starting at position 3
            for (int i = 1; i < oid.Size; i++)
            {
                span[i * 2 + 1] = (char)hexUpper[bytes[i] >> 4];
                span[i * 2 + 2] = (char)hexUpper[bytes[i] & 0x0f];
            }
        });
    }

    /// <summary>
    /// Formats the first <paramref name="hexChars"/> hex characters of this
    /// OID as a lowercase hex string. Matches <c>git_oid_nfmt</c>
    /// (<c>oid.c:119-138</c>): writes <c>min(hexChars, HexSize)</c> chars and
    /// zero-fills the rest of a caller-supplied buffer in C. The C# version
    /// returns exactly the clamped length (no zero-fill tail).
    /// </summary>
    /// <param name="hexChars">
    /// The number of hex characters to emit. Clamped to <see cref="HexSize"/>.
    /// Must be non-negative.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="hexChars"/> is negative.
    /// </exception>
    public string ToHexPrefix(int hexChars)
    {
        if (hexChars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hexChars), "must be non-negative");
        }

        int n = Math.Min(hexChars, HexSize);
        if (n == 0)
        {
            return string.Empty;
        }

        if (_bytes is null)
        {
            return new string('0', n);
        }

        return string.Create(n, (this, n), static (span, state) =>
        {
            (GitOid oid, int len) = state;

            byte[]? bytes = oid._bytes;
            Debug.Assert(bytes is not null, "_bytes is non-null (checked before lambda)");

            ReadOnlySpan<byte> hexUpper = HexUpper;

            for (int i = 0; i < len; i++)
            {
                span[i] = (i & 1) == 0
                    ? (char)hexUpper[bytes[i / 2] >> 4]
                    : (char)hexUpper[bytes[i / 2] & 0x0f];
            }
        });
    }

    /// <summary>
    /// Returns true if this OID's first <paramref name="hexLength"/> hex characters
    /// match <paramref name="prefix"/>. Use for abbreviated OID lookup.
    /// </summary>
    public bool StartsWith(GitOid prefix, int hexLength)
    {
        int nibbleCount = Math.Min(hexLength, Math.Min(HexSize, prefix.HexSize));
        int fullBytes = nibbleCount / 2;

        for (int i = 0; i < fullBytes; i++)
        {
            if (RawBytes[i] != prefix.RawBytes[i])
            {
                return false;
            }
        }

        if ((nibbleCount & 1) == 1)
        {
            // Compare only the high nibble of the partial byte.
            return (RawBytes[fullBytes] & 0xf0) == (prefix.RawBytes[fullBytes] & 0xf0);
        }

        return true;
    }

    /// <summary>
    /// Returns true if this OID starts with <paramref name="prefix"/>'s full byte content.
    /// </summary>
    public bool StartsWith(GitOid prefix) => StartsWith(prefix, prefix.HexSize);

    /// <summary>
    /// Compares two OIDs byte-by-byte. Matches libgit2's <c>git_oid_cmp</c>.
    /// </summary>
    public int CompareTo(GitOid other)
    {
        if (EffectiveAlgorithm != other.EffectiveAlgorithm)
        {
            return ((int)EffectiveAlgorithm).CompareTo((int)other.EffectiveAlgorithm);
        }

        if (_bytes is null && other._bytes is null)
        {
            return 0;
        }

        if (_bytes is null)
        {
            // A default OID is a zero-filled buffer of Size bytes: it sorts
            // before any non-zero OID and equal to an all-zero one.
            return other.IsZero ? 0 : -1;
        }

        if (other._bytes is null)
        {
            return IsZero ? 0 : 1;
        }

        return RawBytes.SequenceCompareTo(other.RawBytes);
    }

    /// <summary>True if <paramref name="left"/> sorts before <paramref name="right"/>.</summary>
    public static bool operator <(GitOid left, GitOid right) => left.CompareTo(right) < 0;

    /// <summary>True if <paramref name="left"/> sorts at or before <paramref name="right"/>.</summary>
    public static bool operator <=(GitOid left, GitOid right) => left.CompareTo(right) <= 0;

    /// <summary>True if <paramref name="left"/> sorts after <paramref name="right"/>.</summary>
    public static bool operator >(GitOid left, GitOid right) => left.CompareTo(right) > 0;

    /// <summary>True if <paramref name="left"/> sorts at or after <paramref name="right"/>.</summary>
    public static bool operator >=(GitOid left, GitOid right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// The algorithm used for comparison. A <c>default</c> OID (Algorithm 0)
    /// is an unspecified zero OID and compares as SHA-1, matching
    /// <see cref="Size"/>'s existing SHA-1 fallback.
    /// </summary>
    private GitHashAlgorithmKind EffectiveAlgorithm => Algorithm == 0 ? GitHashAlgorithmKind.Sha1 : Algorithm;

    /// <summary>
    /// Byte-wise equality. Overrides record struct's default reference equality on <c>byte[]</c>.
    /// </summary>
    public bool Equals(GitOid other)
    {
        if (EffectiveAlgorithm != other.EffectiveAlgorithm)
        {
            return false;
        }

        if (_bytes is null)
        {
            // A default OID is a zero-filled buffer of Size bytes — C's
            // git_oid_equal is a fixed-size memcmp, so it equals any all-zero
            // OID of the same algorithm.
            return other._bytes is null || other.IsZero;
        }

        if (other._bytes is null)
        {
            return IsZero;
        }

        return RawBytes.SequenceEqual(other.RawBytes);
    }

    /// <summary>
    /// Byte-wise hash code. Combines algorithm with a folded hash of the raw bytes
    /// (xxHash-style folding for distribution).
    /// </summary>
    public override int GetHashCode()
    {
        int hash = (int)EffectiveAlgorithm;
        if (_bytes is null)
        {
            // A default OID is a zero-filled buffer of Size bytes (see
            // Equals): fold Size zero bytes so the hash matches a parsed
            // all-zero OID.
            for (int i = 0; i < Size; i++)
            {
                hash = (hash * 31) ^ 0;
            }

            return hash;
        }

        foreach (byte b in _bytes)
        {
            hash = (hash * 31) ^ b;
        }

        return hash;
    }

    /// <summary>
    /// Size in bytes for the given algorithm.
    /// </summary>
    public static int SizeFor(GitHashAlgorithmKind kind) => kind switch
    {
        GitHashAlgorithmKind.Sha256 => SHA256.HashSizeInBytes,
        _ => SHA1.HashSizeInBytes,
    };

    /// <summary>
    /// Hex size for the given algorithm.
    /// </summary>
    public static int HexSizeFor(GitHashAlgorithmKind kind) => SizeFor(kind) * 2;

    private static int FromHex(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c - '0';
        }

        if (c is >= 'a' and <= 'f')
        {
            return c - 'a' + 10;
        }

        if (c is >= 'A' and <= 'F')
        {
            return c - 'A' + 10;
        }

        return -1;
    }
}
