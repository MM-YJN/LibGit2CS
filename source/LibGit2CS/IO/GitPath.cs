// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace LibGit2CS.IO;

/// <summary> Byte-faithful git path representation: a UTF-8 byte sequence compared byte-wise (<c>memcmp</c>-equivalent) end-to-end. This mirrors libgit2's
/// "bag-of-bytes" path model (<c>src/libgit2/tree.c</c>, <c>src/libgit2/index.c</c>, <c>src/libgit2/diff.c</c> all compare raw path bytes with
/// <c>strcmp</c>/<c>memcmp</c>), which makes the Latin-1/UTF-8 decode-mismatch class of defect structurally impossible for any byte sequence, not merely valid
/// UTF-8. </summary> <remarks> <para> UTF-8 decoding happens only at two well-defined egress edges: <see cref="ToUtf8String"/> (display / logging / API egress)
/// and <see cref="ToFileSystemString"/> (the filesystem boundary). The ingress counterpart <see cref="FromFileSystemString(string, bool)"/> applies the macOS
/// <c>core.precomposeunicode</c> NFD-&gt;NFC transcode when requested. All internal plumbing compares <see cref="GitPath"/> values directly. </para> <para>
/// Backing storage is a single <see cref="ReadOnlyMemory{T}"/> (<c>byte</c>) field. Tree entries slice the retained object buffer (zero-copy, matching
/// <c>entry->filename = buffer</c> in <c>tree.c:435</c>); index entries hold owned <c>byte[]</c> (matching <c>memcpy(entry->path,...)</c> in
/// <c>index.c:968</c>). Equality and hashing are <b>hand-written</b> byte-wise (this is a <see langword="struct"/>, not a <c>record struct</c>: auto-generated
/// record equality would compare the memory field by reference+length, not byte-for-byte). </para> <para> The static comparator
/// surface is a 1:1 port of the libgit2 string primitive family (<c>src/util/util.h</c>, <c>src/util/util.c</c>, <c>src/util/fs_path.c</c>,
/// <c>src/util/ctype_compat.h</c>). </para> </remarks>
public readonly struct GitPath : IEquatable<GitPath>, IComparable<GitPath>, ISpanFormattable, IUtf8SpanFormattable
{
    private const byte DirectorySeparator = (byte)'/';

    private readonly ReadOnlyMemory<byte> _bytes;

    private GitPath(ReadOnlyMemory<byte> bytes) => _bytes = bytes;

    /// <summary>Wraps an existing UTF-8 byte buffer (zero-copy).</summary>
    /// <param name="bytes">A slice of a retained buffer (tree entry) or owned bytes.</param>
    public static GitPath FromUtf8Bytes(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>
    /// Encodes a .NET string to UTF-8 bytes and owns the copy. Inverse of
    /// <see cref="ToUtf8String"/>. Matches the index-entry <c>memcpy</c> model.
    /// </summary>
    public static GitPath FromUtf8String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GitPath(Encoding.UTF8.GetBytes(value));
    }

    /// <summary> Encodes a filesystem-native path to UTF-8 bytes. Equivalent to <see cref="FromUtf8String(string)"/> — no precompose — and kept as a
    /// backward-compatible alias for callers that do not need the macOS NFD-&gt;NFC transcode. Use <see cref="FromFileSystemString(string, bool)"/> to opt in
    /// to <c>core.precomposeunicode</c> handling. </summary>
    public static GitPath FromFileSystemString(string value)
        => FromUtf8String(value);

    /// <summary>
    /// Encodes a filesystem-native path to UTF-8 bytes, optionally applying the
    /// macOS <c>core.precomposeunicode</c> NFD-&gt;NFC transcode first. Ports
    /// libgit2's per-entry <c>git_fs_path_iconv</c> call inside
    /// <c>git_fs_path_diriter_next</c> (<c>fs_path.c:1466-1469</c>): when
    /// <paramref name="precompose"/> is <see langword="true"/>, the input is
    /// routed through <see cref="PathPrecompose.PrecomposeCore"/> before UTF-8
    /// encoding, so an NFD name read from an HFS+/APFS filesystem produces the
    /// same <see cref="GitPath"/> bytes as the corresponding NFC tree/index
    /// entry. When <paramref name="precompose"/> is <see langword="false"/>
    /// (the default on non-macOS and for callers that have not opted in), the
    /// input bytes are preserved as-is.
    /// </summary>
    /// <param name="value">The filesystem-native path.</param>
    /// <param name="precompose">
    /// <see langword="true"/> to apply NFD-&gt;NFC precompose (the caller has
    /// resolved <c>core.precomposeunicode=true</c> and is on a platform whose
    /// filesystem may decompose Unicode); <see langword="false"/> to preserve
    /// the input bytes.
    /// </param>
    /// <returns>A byte-faithful <see cref="GitPath"/> over the (possibly precomposed) UTF-8 bytes.</returns>
    public static GitPath FromFileSystemString(string value, bool precompose)
        => precompose ? FromUtf8String(PathPrecompose.PrecomposeCore(value)) : FromUtf8String(value);

    /// <summary>The raw byte view. Capture once on hot paths.</summary>
    public ReadOnlySpan<byte> Span => _bytes.Span;

    /// <summary>Byte length of the path.</summary>
    public int Length => _bytes.Length;

    /// <summary>True if the path is empty.</summary>
    public bool IsEmpty => _bytes.Length == 0;

    /// <summary>
    /// Returns a zero-copy slice of this path (the slice retains the underlying
    /// buffer). Used for splitting a path on <c>/</c> into components.
    /// </summary>
    public GitPath Slice(int start, int length) => new(_bytes.Slice(start, length));

    /// <summary>
    /// Returns a zero-copy slice from <paramref name="start"/> to the end.
    /// </summary>
    public GitPath Slice(int start) => new(_bytes.Slice(start));

    /// <summary> Whether this path and <paramref name="other"/> reference the SAME underlying byte buffer at the same position — the managed equivalent of C's
    /// path-pointer identity (<c>diff_print.c:261</c> compares <c>old_file.path != new_file.path</c> as pointers). Generated diff deltas share one pooled path
    /// (C: <c>git_pool_strdup</c> once) so equal paths are pointer-identical; parsed diffs allocate a fresh buffer per side. </summary>
    internal bool SharesBufferWith(GitPath other)
    {
        if (_bytes.IsEmpty || other._bytes.IsEmpty)
        {
            return _bytes.IsEmpty == other._bytes.IsEmpty;
        }

        return MemoryMarshal.TryGetArray(_bytes, out ArraySegment<byte> a)
            && MemoryMarshal.TryGetArray(other._bytes, out ArraySegment<byte> b)
            && ReferenceEquals(a.Array, b.Array)
            && a.Offset == b.Offset
            && a.Count == b.Count;
    }

    /// <summary>
    /// UTF-8 decode with replacement fallback (U+FFFD for invalid bytes) —
    /// correct for valid UTF-8, lossy otherwise. This matches git's own display
    /// limitation. Use only for human-facing output / logging / API egress,
    /// never for comparisons.
    /// </summary>
    public string ToUtf8String() => Encoding.UTF8.GetString(_bytes.Span);

    /// <summary> The raw bytes as a <see cref="ReadOnlyMemory{T}"/> (owned copy). The byte-parity surface for byte-keyed lookups (e.g. the submodule cache's
    /// path→name map) — the inverse of <see cref="FromUtf8Bytes"/>. </summary>
    public ReadOnlyMemory<byte> ToUtf8Bytes() => _bytes.ToArray();

    /// <summary> Egress for the filesystem boundary (<c>Path.Join</c> / <c>File.*</c> / <c>Directory.*</c>). Every
    /// <c>Path.Join(workdir, path)</c> site routes through this. The decode is plain UTF-8 with replacement fallback — <b>no</b> precompose transcode is applied here,
    /// matching libgit2 (which has no egress iconv: on macOS the OS auto-converts NFC-&gt;NFD on write). The ingress counterpart is <see
    /// cref="FromFileSystemString(string, bool)"/>. </summary>
    public string ToFileSystemString() => ToUtf8String();

    /// <inheritdoc/>
    public override string ToString() => ToUtf8String();

    // ----- IEquatable<GitPath> / IComparable<GitPath> (byte-wise) -----

    /// <inheritdoc/>
    public bool Equals(GitPath other) => EqualsBytes(this, other);

    /// <inheritdoc/>
    public int CompareTo(GitPath other) => Compare(this, other);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj)
        => obj is GitPath other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        ReadOnlySpan<byte> b = _bytes.Span;
        unchecked
        {
            // FNV-1a 32-bit; consistent with byte equality, good distribution.
            int hash = (int)2166136261u;
            for (int i = 0; i < b.Length; i++)
            {
                hash = (hash ^ b[i]) * 16777619;
            }

            return hash;
        }
    }

    /// <summary>Tests whether the paths contain identical bytes.</summary>
    public static bool operator ==(GitPath left, GitPath right) => left.Equals(right);

    /// <summary>Tests whether the paths contain different bytes.</summary>
    public static bool operator !=(GitPath left, GitPath right) => !left.Equals(right);

    /// <summary>Tests whether the left value sorts before the right value.</summary>
    public static bool operator <(GitPath left, GitPath right) => left.CompareTo(right) < 0;

    /// <summary>Tests whether the left value sorts before or equal to the right value.</summary>
    public static bool operator <=(GitPath left, GitPath right) => left.CompareTo(right) <= 0;

    /// <summary>Tests whether the left value sorts after the right value.</summary>
    public static bool operator >(GitPath left, GitPath right) => left.CompareTo(right) > 0;

    /// <summary>Tests whether the left value sorts after or equal to the right value.</summary>
    public static bool operator >=(GitPath left, GitPath right) => left.CompareTo(right) >= 0;

    private static bool EqualsBytes(GitPath a, GitPath b)
    {
        ReadOnlySpan<byte> sa = a._bytes.Span;
        ReadOnlySpan<byte> sb = b._bytes.Span;
        return sa.SequenceEqual(sb);
    }

    // ----- Comparator family (1:1 port of libgit2 string primitives) -----

    /// <summary>
    /// Byte-wise compare (sign-equivalent to libc <c>strcmp</c>). Ports
    /// <c>git__strcmp</c> (<c>src/util/util.h:146</c>). The shorter path sorts
    /// before a longer path of which it is a prefix (matches the <c>NUL &lt;
    /// any byte</c> rule of <c>strcmp</c>).
    /// </summary>
    public static int Compare(GitPath a, GitPath b)
    {
        ReadOnlySpan<byte> sa = a._bytes.Span;
        ReadOnlySpan<byte> sb = b._bytes.Span;
        int min = Math.Min(sa.Length, sb.Length);
        for (int i = 0; i < min; i++)
        {
            int diff = sa[i] - sb[i];
            if (diff != 0)
            {
                return diff;
            }
        }

        return sa.Length - sb.Length;
    }

    /// <summary>
    /// Byte-wise compare bounded by <paramref name="length"/> (sign-equivalent
    /// to libc <c>strncmp</c>). Ports <c>git__strncmp</c>
    /// (<c>src/util/util.h:147</c>).
    /// </summary>
    public static int Compare(GitPath a, GitPath b, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> sa = a._bytes.Span;
        ReadOnlySpan<byte> sb = b._bytes.Span;
        int n = Math.Min(length, Math.Min(sa.Length, sb.Length));
        for (int i = 0; i < n; i++)
        {
            int diff = sa[i] - sb[i];
            if (diff != 0)
            {
                return diff;
            }
        }

        if (n >= length)
        {
            return 0;
        }

        // One or both paths ended before `length`; the ended side is the
        // virtual NUL terminator (0), the other side is a real byte at [n].
        int ca = (sa.Length == n) ? 0 : sa[n];
        int cb = (sb.Length == n) ? 0 : sb[n];
        return ca - cb;
    }

    /// <summary>
    /// ASCII case-fold compare. Ports <c>git__strcasecmp</c>
    /// (<c>src/util/util.c:162</c>). Only <c>A-Z</c> fold to <c>a-z</c>;
    /// non-ASCII bytes pass through unchanged (diverges from .NET's
    /// <c>StringComparison.OrdinalIgnoreCase</c>, which is Unicode-aware).
    /// </summary>
    public static int CompareIgnoreCase(GitPath a, GitPath b)
    {
        ReadOnlySpan<byte> sa = a._bytes.Span;
        ReadOnlySpan<byte> sb = b._bytes.Span;
        int min = Math.Min(sa.Length, sb.Length);
        for (int i = 0; i < min; i++)
        {
            int al = AsciiToLower(sa[i]);
            int bl = AsciiToLower(sb[i]);
            int diff = al - bl;
            if (diff != 0)
            {
                return diff;
            }
        }

        return sa.Length - sb.Length;
    }

    /// <summary>
    /// ASCII-only <c>git__tolower</c> (ctype_compat.h:18-21): only
    /// <c>A-Z</c> fold to <c>a-z</c>; bytes &gt;= 0x80 pass through unchanged.
    /// </summary>
    public static int AsciiToLower(byte c)
        => c is >= (byte)'A' and <= (byte)'Z' ? c + 32 : c;

    /// <summary>
    /// ASCII case-fold compare bounded by <paramref name="length"/>. Ports
    /// <c>git__strncasecmp</c> (<c>src/util/util.c:191</c>) faithfully,
    /// including the <c>do/while</c> semantics (at least one byte pair is
    /// processed; the loop stops on <c>NUL</c> or fold mismatch or cap). The
    /// fold is ASCII-only via <see cref="AsciiToLower"/> (like the unbounded
    /// variant) — bytes &gt;= 0x80 pass through unchanged, matching
    /// <c>git__tolower</c> (ctype_compat.h).
    /// </summary>
    public static int CompareIgnoreCase(GitPath a, GitPath b, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> sa = a._bytes.Span;
        ReadOnlySpan<byte> sb = b._bytes.Span;
        int i = 0;
        int al;
        int bl;
        int remaining = length;
        do
        {
            al = AsciiToLower(i < sa.Length ? sa[i] : (byte)0);
            bl = AsciiToLower(i < sb.Length ? sb[i] : (byte)0);
            i++;
        }
        while (--remaining > 0 && al != 0 && al == bl);

        return al - bl;
    }

    /// <summary>
    /// Case-insensitive sort comparator: folds for the primary comparison but,
    /// when two paths fold equal, preserves the raw case as a stable tiebreaker.
    /// Ports <c>git__strcasesort_cmp</c> (<c>src/util/util.c:169</c>). The fold
    /// is ASCII-only via <see cref="AsciiToLower"/> (bytes &gt;= 0x80 pass
    /// through unchanged, matching <c>git__tolower</c>).
    /// </summary>
    public static int CompareCaseSort(GitPath a, GitPath b)
    {
        ReadOnlySpan<byte> sa = a._bytes.Span;
        ReadOnlySpan<byte> sb = b._bytes.Span;
        int min = Math.Min(sa.Length, sb.Length);
        int caseTiebreak = 0;
        int i;
        for (i = 0; i < min; i++)
        {
            byte ca = sa[i];
            byte cb = sb[i];
            if (ca != cb)
            {
                int al = AsciiToLower(ca);
                int bl = AsciiToLower(cb);
                if (al != bl)
                {
                    break;
                }

                // Fold-equal but raw-case different: record the FIRST such diff.
                if (caseTiebreak == 0)
                {
                    caseTiebreak = ca - cb;
                }
            }
        }

        if (i < sa.Length || i < sb.Length)
        {
            int al = AsciiToLower(i < sa.Length ? sa[i] : (byte)0);
            int bl = AsciiToLower(i < sb.Length ? sb[i] : (byte)0);
            return al - bl;
        }

        return caseTiebreak;
    }

    /// <summary>
    /// Byte-wise prefix compare (unbounded). Returns 0 iff <paramref name="str"/>
    /// starts with <paramref name="prefix"/>; otherwise the signed byte
    /// difference at the first divergence (negative if <paramref name="str"/> is
    /// shorter than <paramref name="prefix"/>). Ports <c>git__prefixcmp</c>
    /// (<c>src/util/util.c:241</c>).
    /// </summary>
    public static int ComparePrefix(GitPath str, GitPath prefix)
    {
        ReadOnlySpan<byte> ss = str._bytes.Span;
        ReadOnlySpan<byte> sp = prefix._bytes.Span;
        int max = Math.Max(ss.Length, sp.Length);
        for (int i = 0; i < max; i++)
        {
            byte p = i < sp.Length ? sp[i] : (byte)0;
            byte s = i < ss.Length ? ss[i] : (byte)0;
            if (p == 0)
            {
                return 0;
            }

            if (s != p)
            {
                return s - p;
            }
        }

        return 0;
    }

    /// <summary>
    /// Byte-wise prefix compare bounded by <paramref name="strLen"/>. Ports
    /// <c>git__prefixncmp</c> (<c>src/util/util.c:257</c>): iterates at most
    /// <paramref name="strLen"/> bytes of <paramref name="str"/>, returning 0
    /// when <paramref name="prefix"/> is exhausted, the byte difference on
    /// mismatch, or <c>-(next prefix byte)</c> when <paramref name="strLen"/>
    /// is exhausted with <paramref name="prefix"/> remaining.
    /// </summary>
    public static int ComparePrefix(GitPath str, int strLen, GitPath prefix)
    {
        ReadOnlySpan<byte> ss = str._bytes.Span;
        ReadOnlySpan<byte> sp = prefix._bytes.Span;
        for (int i = 0; i < strLen; i++)
        {
            byte s = i < ss.Length ? ss[i] : (byte)0;
            byte p = i < sp.Length ? sp[i] : (byte)0;
            if (p == 0)
            {
                return 0;
            }

            if (s != p)
            {
                return s - p;
            }
        }

        int nextPrefix = strLen < sp.Length ? sp[strLen] : 0;
        return -nextPrefix;
    }

    /// <summary>
    /// ASCII case-fold prefix compare (unbounded). Ports
    /// <c>git__prefixcmp_icase</c> (<c>src/util/util.c:262</c>).
    /// </summary>
    public static int ComparePrefixIgnoreCase(GitPath str, GitPath prefix)
    {
        ReadOnlySpan<byte> ss = str._bytes.Span;
        ReadOnlySpan<byte> sp = prefix._bytes.Span;
        int max = Math.Max(ss.Length, sp.Length);
        for (int i = 0; i < max; i++)
        {
            int p = AsciiToLower(i < sp.Length ? sp[i] : (byte)0);
            int s = AsciiToLower(i < ss.Length ? ss[i] : (byte)0);
            if (p == 0)
            {
                return 0;
            }

            if (s != p)
            {
                return s - p;
            }
        }

        return 0;
    }

    /// <summary>
    /// ASCII case-fold prefix compare bounded by <paramref name="strLen"/>.
    /// Ports <c>git__prefixncmp_icase</c> (<c>src/util/util.c:267</c>). The
    /// fold is ASCII-only via <see cref="AsciiToLower"/>.
    /// </summary>
    public static int ComparePrefixIgnoreCase(GitPath str, int strLen, GitPath prefix)
    {
        ReadOnlySpan<byte> ss = str._bytes.Span;
        ReadOnlySpan<byte> sp = prefix._bytes.Span;
        for (int i = 0; i < strLen; i++)
        {
            int s = AsciiToLower(i < ss.Length ? ss[i] : (byte)0);
            int p = AsciiToLower(i < sp.Length ? sp[i] : (byte)0);
            if (p == 0)
            {
                return 0;
            }

            if (s != p)
            {
                return s - p;
            }
        }

        int nextPrefix = strLen < sp.Length ? AsciiToLower(sp[strLen]) : 0;
        return -nextPrefix;
    }

    /// <summary>
    /// Compares <paramref name="a"/> against <paramref name="bPrefix"/> over
    /// <paramref name="bPrefix"/>'s full length, then (if those bytes are equal)
    /// returns the byte of <paramref name="a"/> immediately after the prefix —
    /// <c>0</c> when <paramref name="a"/> is exactly the prefix length, positive
    /// when <paramref name="a"/> is longer. Ports <c>git__strlcmp</c>
    /// (<c>src/util/util.h:162</c>).
    /// </summary>
    public static int CompareLengthAware(GitPath a, GitPath bPrefix)
    {
        int bLen = bPrefix._bytes.Length;
        int cmp = Compare(a, bPrefix, bLen);
        if (cmp != 0)
        {
            return cmp;
        }

        int ca = bLen < a._bytes.Length ? a._bytes.Span[bLen] : 0;
        return ca;
    }

    /// <summary>
    /// Tree-entry ordering: byte compare over the common length, then the
    /// directory-as-trailing-<c>/</c> rule. A path that is a directory is
    /// compared as if its name ended in <c>'/'</c> (0x2F); a non-directory's
    /// virtual terminator is <c>NUL</c> (0x00). This makes a directory sort
    /// <em>after</em> a sibling blob of the same name but before any sibling
    /// whose name continues with a byte &gt; <c>'/'</c>. Ports
    /// <c>git_fs_path_cmp</c> (<c>src/util/fs_path.c:907</c>).
    /// </summary>
    /// <param name="isDirA">True if <paramref name="a"/> names a directory (tree entry).</param>
    /// <param name="isDirB">True if <paramref name="b"/> names a directory (tree entry).</param>
    /// <param name="a">The first value to compare.</param>
    /// <param name="b">The second value to compare.</param>
    public static int CompareTreeOrder(GitPath a, bool isDirA, GitPath b, bool isDirB)
    {
        ReadOnlySpan<byte> sa = a._bytes.Span;
        ReadOnlySpan<byte> sb = b._bytes.Span;
        int len = Math.Min(sa.Length, sb.Length);
        int cmp = Compare(a, b, len);
        if (cmp != 0)
        {
            return cmp;
        }

        int c1 = ByteAtOrVirtual(sa, len, isDirA);
        int c2 = ByteAtOrVirtual(sb, len, isDirB);
        return c1 < c2 ? -1 : c1 > c2 ? 1 : 0;
    }

    /// <summary>
    /// Two-phase homing-search comparator: <c>memcmp</c> over the shorter
    /// length, returning 0 whenever one name is a byte-prefix of the other (no
    /// length tiebreak). Used by the tree-entry lookup's homing step. Ports
    /// <c>homing_search_cmp</c> (<c>src/libgit2/tree.c:122</c>).
    /// </summary>
    public static int CompareHoming(GitPath key, GitPath entry)
    {
        ReadOnlySpan<byte> sk = key._bytes.Span;
        ReadOnlySpan<byte> se = entry._bytes.Span;
        int min = Math.Min(sk.Length, se.Length);
        for (int i = 0; i < min; i++)
        {
            int diff = sk[i] - se[i];
            if (diff != 0)
            {
                return diff;
            }
        }

        return 0;
    }

    /// <summary>
    /// Case-select dispatcher. Ports the <c>STRCMP_CASESELECT</c> macro
    /// (<c>src/util/util.h:49</c>).
    /// </summary>
    public static int Compare(GitPath a, GitPath b, bool ignoreCase)
        => ignoreCase ? CompareIgnoreCase(a, b) : Compare(a, b);

    private static int ByteAtOrVirtual(ReadOnlySpan<byte> span, int index, bool isDir)
    {
        if (index < span.Length)
        {
            return span[index];
        }

        return isDir ? DirectorySeparator : 0;
    }

    // ----- Byte concatenation -----

    /// <summary>
    /// Byte-concatenates two <see cref="GitPath"/> values (no separator).
    /// Matches C <c>git_str_puts</c>/<c>memcpy</c> byte concat (e.g. the
    /// <c>path~suffix</c> suffixing in <c>checkout_path_suffixed</c>,
    /// checkout.c:1957-1985). Returns a new owned copy (the inputs may be
    /// zero-copy slices that must not be mutated).
    /// </summary>
    public static GitPath operator +(GitPath left, GitPath right)
    {
        ReadOnlySpan<byte> a = left.Span;
        ReadOnlySpan<byte> b = right.Span;
        byte[] result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return FromUtf8Bytes(result);
    }

    /// <summary>
    /// Byte-concatenates a <see cref="GitPath"/> with a UTF-8-encoded
    /// <see cref="string"/> (no separator). The string is encoded as UTF-8;
    /// for ASCII strings (the common case for suffixes like <c>~ours</c>) this
    /// is byte-identical to the C <c>git_str_printf(&amp;buf, "%s%s", …)</c>.
    /// </summary>
    public static GitPath operator +(GitPath left, string right)
        => left + FromUtf8String(right);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => Encoding.UTF8.TryGetChars(_bytes.Span, destination, out charsWritten);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToUtf8String();

    /// <inheritdoc />
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ReadOnlySpan<byte> span = _bytes.Span;

        if (!span.TryCopyTo(utf8Destination))
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = span.Length;
        return true;
    }

    /// <summary>Tests byte equality against the UTF-8 encoding of the supplied text.</summary>
    public bool EqualsUtf8(ReadOnlySpan<char> other)
    {
        ReadOnlySpan<byte> span = _bytes.Span;
        int byteCount = Encoding.UTF8.GetByteCount(other);
        if (byteCount != span.Length)
        {
            return false;
        }

        byte[]? rented = null;
        Span<byte> otherBytes = byteCount > 256 ? (rented = ArrayPool<byte>.Shared.Rent(byteCount)) : stackalloc byte[256];
        try
        {
            Encoding.UTF8.GetBytes(other, otherBytes);
            return span.SequenceEqual(otherBytes.Slice(0, byteCount));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Tests whether this path ends with the UTF-8 encoding of the supplied suffix.</summary>
    public bool EndsWithUtf8(ReadOnlySpan<char> suffix)
    {
        ReadOnlySpan<byte> span = _bytes.Span;
        int byteCount = Encoding.UTF8.GetByteCount(suffix);
        if (byteCount > span.Length)
        {
            return false;
        }
        byte[]? rented = null;
        Span<byte> suffixBytes = byteCount > 256 ? (rented = ArrayPool<byte>.Shared.Rent(byteCount)) : stackalloc byte[256];
        try
        {
            Encoding.UTF8.GetBytes(suffix, suffixBytes);
            return span.Slice(span.Length - byteCount).SequenceEqual(suffixBytes.Slice(0, byteCount));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
