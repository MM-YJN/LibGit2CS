// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Unicode;

using LibGit2CS.Core;

namespace LibGit2CS.Config;

/// <summary> Builds the fully-qualified normalized config key string from the parser's byte-domain section and variable name spans. Managed equivalent of the
/// name assembly in libgit2's <c>config_file.c</c> / <c>config_mem.c</c> (<c>git_config__normalize_name</c> over the parsed section+name bytes). </summary>
/// <remarks> <para> The parser restricts section and variable names to ASCII (<c>[A-Za-z0-9-]</c>, key chars — <c>IsKeyChar</c>/<c>IsNameChar</c> in
/// <c>ConfigParser</c>), and subsection bytes may be arbitrary. The fully qualified key is <c>section(.subsection)*.variable</c> with the section and variable
/// parts lowercased in place; the subsection is preserved verbatim. </para> <para> The byte-domain key is produced directly (ASCII-lowercase per byte — no
/// char-encoding round-trip) and the string key is the UTF-8 display decode of those bytes, the same display contract as <see cref="GitConfigEntry.Value"/>.
/// Non-UTF-8 subsection bytes surface as U+FFFD in the string key; byte-faithful key matching must compare the byte-domain key. </para> </remarks>
internal static class ConfigKeyName
{
    /// <summary>
    /// The maximum key byte length that avoids a pooled buffer.
    /// </summary>
    private const int StackallocThreshold = 128;

    /// <summary>
    /// The fully-qualified normalized key as bytes (ASCII-lowercased section
    /// and variable parts, verbatim subsection bytes). Byte-parity surface —
    /// the string form is its UTF-8 display decode
    /// (<see cref="GitConfigEntry.Name"/>).
    /// </summary>
    public static ReadOnlyMemory<byte> BuildFullyQualifiedNameBytes(ReadOnlyMemory<byte>? currentSection, ReadOnlyMemory<byte> varName)
    {
        int totalLength = varName.Length + (currentSection is { } s ? s.Length + 1 : 0);

        // Fast path: the whole key fits in a stack buffer (keys are short in
        // practice — section + name; only subsection bytes can grow it).
        if (totalLength <= StackallocThreshold)
        {
            Span<byte> buffer = stackalloc byte[totalLength];
            int written = WriteKey(buffer, currentSection, varName.Span);
            Debug.Assert(written == totalLength);
            return buffer.ToArray();
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            int written = WriteKey(rented, currentSection, varName.Span);
            Debug.Assert(written == totalLength);
            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int WriteKey(Span<byte> destination, ReadOnlyMemory<byte>? currentSection, ReadOnlySpan<byte> varName)
    {
        int written = 0;

        // The parser already normalizes the section bytes (section name
        // lowercased, quoted subsection case preserved — ParseSectionHeader/
        // ParseSubsectionHeader), so the section is copied verbatim.
        if (currentSection is { } section)
        {
            section.Span.CopyTo(destination);
            written = section.Length;
            destination[written++] = (byte)'.';
        }

        // The variable name arrives with ORIGINAL case; lowercased here.
        written += WriteLowercased(destination[written..], varName);
        return written;
    }

    private static int WriteLowercased(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        Debug.Assert(destination.Length >= source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            byte b = source[i];
            destination[i] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 0x20) : b;
        }

        return source.Length;
    }

    /// <summary>
    /// Whether a byte span equals an ASCII string, byte for byte. Used by the
    /// in-place edit machinery to compare parser-emitted section/variable names
    /// against the (ASCII-validated) caller keys — the byte-domain replacement
    /// for the char-encoding compares. The char side must be ASCII
    /// (chars &gt; U+007F truncate to their low byte, so non-ASCII char
    /// compares must use the byte-vs-byte overload with a UTF-8-encoded
    /// operand).
    /// </summary>
    public static bool AsciiEquals(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> chars)
    {
        if (bytes.Length != chars.Length)
        {
            return false;
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)chars[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Byte-vs-byte equality (C's <c>strcmp</c> over byte buffers). Used for
    /// config value compares against caller strings (e.g. remote names) —
    /// the caller string is UTF-8-encoded once and compared byte-exact.
    /// </summary>
    public static bool AsciiEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        return a.SequenceEqual(b);
    }

    /// <summary>
    /// Case-insensitive variant of <see cref="LibGit2CS.Config.ConfigKeyName.AsciiEquals(System.ReadOnlySpan{byte}, System.ReadOnlySpan{char})"/>: ASCII-lowercases
    /// both sides per byte (matches C's <c>strcasecmp</c> over ASCII names).
    /// </summary>
    public static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> chars)
    {
        if (bytes.Length != chars.Length)
        {
            return false;
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            if (ToLowerAscii(bytes[i]) != ToLowerAscii((byte)chars[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// ASCII case-insensitive byte-vs-byte equality (C's <c>strcasecmp</c>
    /// over byte buffers). Used by the byte-domain value parsers for the
    /// ASCII literal tests (<c>true/yes/on/...</c>).
    /// </summary>
    public static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (ToLowerAscii(a[i]) != ToLowerAscii(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToLowerAscii(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 0x20) : b;

    /// <summary>
    /// Builds a fully-qualified config key from byte-domain section,
    /// subsection and variable parts: <c>section.subsection.variable</c> with
    /// the variable ASCII-lowercased and the section/subsection copied
    /// verbatim. The managed equivalent of C's key assembly
    /// (<c>git_str_printf("branch.%.*s.remote", …, branch)</c>, remote.c:2783)
    /// — the subsection bytes are preserved verbatim, so non-UTF-8 subsection
    /// bytes (e.g. a branch name with raw <c>0xFF</c>) build the same key
    /// bytes C would.
    /// </summary>
    /// <param name="section">The section name bytes (already lowercased by the parser).</param>
    /// <param name="subsection">The subsection bytes (verbatim).</param>
    /// <param name="variable">The variable name bytes (lowercased here).</param>
    public static ReadOnlyMemory<byte> BuildNameBytes(ReadOnlySpan<byte> section, ReadOnlySpan<byte> subsection, ReadOnlySpan<byte> variable)
    {
        int totalLength = section.Length + 1 + subsection.Length + 1 + variable.Length;

        // Fast path: the whole key fits in a stack buffer (keys are short in
        // practice — section + subsection + variable).
        if (totalLength <= StackallocThreshold)
        {
            Span<byte> buffer = stackalloc byte[totalLength];
            int written = WriteNameKey(buffer, section, subsection, variable);
            Debug.Assert(written == totalLength);
            return buffer.ToArray();
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            int written = WriteNameKey(rented, section, subsection, variable);
            Debug.Assert(written == totalLength);
            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int WriteNameKey(Span<byte> destination, ReadOnlySpan<byte> section, ReadOnlySpan<byte> subsection, ReadOnlySpan<byte> variable)
    {
        int written = 0;
        section.CopyTo(destination);
        written += section.Length;
        destination[written++] = (byte)'.';
        subsection.CopyTo(destination[written..]);
        written += subsection.Length;
        destination[written++] = (byte)'.';
        written += WriteLowercased(destination[written..], variable);
        return written;
    }

    /// <summary>
    /// Normalizes a config key over raw bytes: lowercases the section (before
    /// first dot) and variable name (after last dot), preserves the subsection
    /// verbatim. Byte-domain port of <c>git_config__normalize_name</c>
    /// (config.c:1542-1575) — C normalizes <c>char *</c> bytes with
    /// <c>strchr</c>/<c>strrchr</c> and in-place ASCII lowercasing, so
    /// non-UTF-8 subsection bytes pass through verbatim.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.InvalidSpec"/> if the name is missing a dot, has
    /// empty components, contains invalid characters, or has newlines in the
    /// subsection.
    /// </exception>
    public static void NormalizeNameBytes(IBufferWriter<byte> writer, ReadOnlySpan<byte> name)
    {
        int fdot = name.IndexOf((byte)'.');
        int ldot = name.LastIndexOf((byte)'.');

        if (fdot < 0 || fdot == 0 || ldot < 0 || ldot == name.Length - 1)
        {
            throw InvalidName(name);
        }

        // Subsection (middle) preserves case but must not contain newlines.
        ReadOnlySpan<byte> middle = name.Slice(fdot, ldot - fdot + 1);
        if (middle.Contains((byte)'\n'))
        {
            throw InvalidName(name);
        }

        ReadOnlySpan<byte> firstPart = name.Slice(0, fdot);
        ReadOnlySpan<byte> lastPart = name.Slice(ldot + 1);

        int length = firstPart.Length + middle.Length + lastPart.Length;
        Span<byte> result = writer.GetSpan(length);

        // Validate and downcase up to first dot.
        LowercaseSectionBytes(firstPart, name, result);

        middle.CopyTo(result.Slice(firstPart.Length));

        // Validate and downcase after last dot.
        LowercaseSectionBytes(lastPart, name, result.Slice(firstPart.Length + middle.Length));

        writer.Advance(length);
    }

    private static void LowercaseSectionBytes(ReadOnlySpan<byte> span, ReadOnlySpan<byte> original, Span<byte> target)
    {
        if (span.IsEmpty)
        {
            throw InvalidName(original);
        }

        Debug.Assert(target.Length >= span.Length);

        for (int i = 0; i < span.Length; i++)
        {
            byte c = span[i];
            if (IsAsciiLetterOrDigit(c))
            {
                target[i] = ToLowerAscii(c);
            }
            else if (c == (byte)'-' && i > 0)
            {
                target[i] = c;
            }
            else
            {
                throw InvalidName(original);
            }
        }
    }

    private static bool IsAsciiLetterOrDigit(byte c)
        => c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9';

    private static GitException InvalidName(ReadOnlySpan<byte> name)
        => new(
            GitErrorCode.InvalidSpec,
            $"invalid config item name '{Encoding.UTF8.GetString(name)}'",
            GitErrorCategory.Config);
}
