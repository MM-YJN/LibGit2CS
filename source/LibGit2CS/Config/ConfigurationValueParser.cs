// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Globalization;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.Config;

/// <summary>
/// Value-parsing helpers for config values. Managed port of libgit2's
/// <c>git_config_parse_bool</c>, <c>git_config_parse_int32</c>,
/// <c>git_config_parse_int64</c>, and <c>git_config_parse_path</c>.
/// </summary>
internal static class ConfigurationValueParser
{
    /// <summary>
    /// Parses a git config boolean. Matches libgit2's <c>git_config_parse_bool</c>.
    /// Accepts: <c>true/yes/on/1</c> → true, <c>false/no/off/0</c> → false.
    /// Any non-zero integer is also true.
    /// </summary>
    public static bool TryParseBool(string? value, out bool result)
    {
        result = false;

        // C (util.c:647-666, git__parse_bool): a MISSING value means TRUE;
        // an empty string is FALSE.
        if (value is null)
        {
            result = true;
            return true;
        }

        if (value.Length == 0)
        {
            result = false;
            return true;
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        if (TryParseInt32(value, out int intVal))
        {
            result = intVal != 0;
            return true;
        }

        return false;
    }

    /// <summary> Byte-domain <see cref="TryParseBool(string?, out bool)"/> over the raw config value bytes. </summary>
    public static bool TryParseBool(ReadOnlySpan<byte> value, out bool result)
    {
        result = false;

        if (value.IsEmpty)
        {
            result = false;
            return true;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "true"u8)
            || ConfigKeyName.AsciiEqualsIgnoreCase(value, "yes"u8)
            || ConfigKeyName.AsciiEqualsIgnoreCase(value, "on"u8))
        {
            result = true;
            return true;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "false"u8)
            || ConfigKeyName.AsciiEqualsIgnoreCase(value, "no"u8)
            || ConfigKeyName.AsciiEqualsIgnoreCase(value, "off"u8))
        {
            result = false;
            return true;
        }

        if (TryParseInt32(value, out int intVal))
        {
            result = intVal != 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a git config 32-bit integer with optional <c>k</c>/<c>m</c>/<c>g</c>
    /// suffix (base-1024). Matches libgit2's <c>git_config_parse_int32</c>.
    /// </summary>
    public static bool TryParseInt32(string? value, out int result)
    {
        if (!TryParseInt64(value, out long tmp))
        {
            result = 0;
            return false;
        }

        result = (int)tmp;
        return result == tmp;
    }

    /// <summary>
    /// Byte-domain <see cref="TryParseInt32(string?, out int)"/>.
    /// </summary>
    public static bool TryParseInt32(ReadOnlySpan<byte> value, out int result)
    {
        if (!TryParseInt64(value, out long tmp))
        {
            result = 0;
            return false;
        }

        result = (int)tmp;
        return result == tmp;
    }

    /// <summary>
    /// Parses a git config 64-bit integer with optional <c>k</c>/<c>m</c>/<c>g</c>
    /// suffix (base-1024). Matches libgit2's <c>git_config_parse_int64</c>.
    /// </summary>
    public static bool TryParseInt64(string? value, out long result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (!TryParseRawInt64(value.AsSpan(), out long num, out int suffixIndex))
        {
            return false;
        }

        if (suffixIndex >= value.Length)
        {
            result = num;
            return true;
        }

        char suffix = value[suffixIndex];
        switch (suffix)
        {
            case 'g':
            case 'G':
                num *= 1024;
                goto case 'm';
            case 'm':
            case 'M':
                num *= 1024;
                goto case 'k';
            case 'k':
            case 'K':
                num *= 1024;
                if (suffixIndex + 1 != value.Length)
                {
                    return false;
                }

                result = num;
                return true;
            default:
                return false;
        }
    }

    /// <summary> Byte-domain <see cref="TryParseInt64(string?, out long)"/> over the raw config value bytes. </summary>
    public static bool TryParseInt64(ReadOnlySpan<byte> value, out long result)
    {
        result = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        if (!TryParseRawInt64(value, out long num, out int suffixIndex))
        {
            return false;
        }

        if (suffixIndex >= value.Length)
        {
            result = num;
            return true;
        }

        byte suffix = value[suffixIndex];
        switch (suffix)
        {
            case (byte)'g':
            case (byte)'G':
                num *= 1024;
                goto case (byte)'m';
            case (byte)'m':
            case (byte)'M':
                num *= 1024;
                goto case (byte)'k';
            case (byte)'k':
            case (byte)'K':
                num *= 1024;
                if (suffixIndex + 1 != value.Length)
                {
                    return false;
                }

                result = num;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parses a path value, expanding a leading <c>~</c> to the home directory.
    /// Matches libgit2's <c>git_config__parse_path</c>.
    /// </summary>
    /// <param name="value">The raw config value.</param>
    /// <param name="dirs">The directory resolver used for <c>~</c>-expansion.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if the value starts with <c>~</c> followed by
    /// a non-separator character (<c>~user</c> is not supported).
    /// </exception>
    public static string ParsePath(string? value, GitSystemDirs dirs)
    {
        ArgumentNullException.ThrowIfNull(dirs);

        if (value is null || value.Length == 0)
        {
            return string.Empty;
        }

        if (value[0] == '~')
        {
            // C (config.c:926-930): only '\0' or '/' may follow the tilde —
            // a backslash is rejected on every platform.
            if (value.Length > 1 && value[1] != '/')
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "retrieving a homedir by name is not supported",
                    GitErrorCategory.Config);
            }

            // C (sysdir.c:622-626 + 548-590): git_sysdir_find_homedir
            // requires the home directory to EXIST — otherwise
            // GIT_ENOTFOUND "the home directory doesn't exist".
            string? home = dirs.FindHomeDir();
            if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
            {
                throw new GitException(
                    GitErrorCode.NotFound,
                    "the home directory doesn't exist",
                    GitErrorCategory.Os);
            }

            // C (config.c:926-933 + sysdir.c:640-650): "~" alone or "~/" both
            // pass the filename AFTER the tilde-slash to
            // git_sysdir_expand_homedir_file; an empty filename returns the
            // home directory AS-IS (no trailing separator). So "~/" → home,
            // not home + "/".
            string rest = value.Length > 2 ? value[2..] : string.Empty;
            if (rest.Length == 0)
            {
                return home;
            }

            return dirs.ExpandHomedirFile(rest);
        }

        return value;
    }

    /// <summary> Byte-domain path parse producing a <see cref="GitPath"/>: a non-<c>~</c> value round-trips its raw bytes verbatim (<see
    /// cref="GitPath.FromUtf8Bytes"/>), so non-UTF-8 config paths stay byte-exact. The <c>~</c> expansion crosses the filesystem/string boundary
    /// (<c>GitSystemDirs</c> is string-centric); the home-dir portion is re-encoded to bytes at that sanctioned edge. </summary> <param name="value">The raw
    /// config value bytes.</param> <param name="dirs">The directory resolver used for <c>~</c>-expansion.</param> <exception cref="GitException"> <see
    /// cref="GitErrorCode.Error"/> if the value starts with <c>~</c> followed by a non-separator character (<c>~user</c> is not supported). </exception>
    public static GitPath ParsePathBytes(ReadOnlyMemory<byte> value, GitSystemDirs dirs)
    {
        ArgumentNullException.ThrowIfNull(dirs);

        ReadOnlySpan<byte> span = value.Span;
        if (span.IsEmpty)
        {
            return GitPath.FromUtf8Bytes(ReadOnlyMemory<byte>.Empty);
        }

        if (span[0] == (byte)'~')
        {
            // C (config.c:926-930): only '\0' or '/' may follow the tilde —
            // a backslash is rejected on every platform.
            if (span.Length > 1 && span[1] != (byte)'/')
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "retrieving a homedir by name is not supported",
                    GitErrorCategory.Config);
            }

            // C (sysdir.c:622-626 + 548-590): git_sysdir_find_homedir
            // requires the home directory to EXIST — otherwise
            // GIT_ENOTFOUND "the home directory doesn't exist".
            string? home = dirs.FindHomeDir();
            if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
            {
                throw new GitException(
                    GitErrorCode.NotFound,
                    "the home directory doesn't exist",
                    GitErrorCategory.Os);
            }

            // C (config.c:926-933 + sysdir.c:640-650): "~" alone or "~/" both
            // pass the filename AFTER the tilde-slash to
            // git_sysdir_expand_homedir_file; an empty filename returns the
            // home directory AS-IS (no trailing separator). So "~/" → home,
            // not home + "/".
            if (span.Length <= 2)
            {
                return GitPath.FromFileSystemString(home);
            }

            // The rest of the value (after "~/") is a filesystem path — the
            // UTF-8 display decode is the sanctioned conversion edge; the
            // joined result is re-encoded for the byte-faithful GitPath.
            string rest = Encoding.UTF8.GetString(span[2..]);
            return GitPath.FromFileSystemString(dirs.ExpandHomedirFile(rest));
        }

        return GitPath.FromUtf8Bytes(value);
    }

    private static bool TryParseRawInt64(ReadOnlySpan<char> span, out long num, out int suffixIndex)
    {
        num = 0;
        suffixIndex = 0;

        // Exact port of git__strntol64 (util.c:34-118) with base 0: ASCII
        // whitespace skip, sign, base auto-detect (0x → hex, leading 0 → octal),
        // then a digit loop in the detected base.
        int i = 0;
        while (i < span.Length && IsAsciiWhitespace(span[i]))
        {
            i++;
        }

        bool negative = false;
        if (i < span.Length && (span[i] == '-' || span[i] == '+'))
        {
            negative = span[i] == '-';
            i++;
        }

        if (i >= span.Length)
        {
            return false; // ndig == 0 → "not a number"
        }

        int numberBase;
        if (span[i] != '0')
        {
            numberBase = 10;
        }
        else if (span.Length - i > 2 && (span[i + 1] == 'x' || span[i + 1] == 'X'))
        {
            numberBase = 16;
        }
        else
        {
            numberBase = 8;
        }

        // Skip the 0x prefix of hex numbers (no equivalent needed for octal:
        // a leading '0' has no impact on the value).
        if (numberBase == 16 && span.Length - i > 2 && span[i] == '0'
            && (span[i + 1] == 'x' || span[i + 1] == 'X'))
        {
            i += 2;
        }

        long n = 0;
        int ndig = 0;
        while (i < span.Length)
        {
            char c = span[i];
            int v = numberBase;
            if (c is >= '0' and <= '9')
            {
                v = c - '0';
            }
            else if (c is >= 'a' and <= 'z')
            {
                v = c - 'a' + 10;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                v = c - 'A' + 10;
            }

            if (v >= numberBase)
            {
                break;
            }

            try
            {
                n = checked((n * numberBase) + (negative ? -v : v));
            }
            catch (OverflowException)
            {
                return false; // ovfl → strntol64 error
            }

            ndig++;
            i++;
        }

        if (ndig == 0)
        {
            return false;
        }

        num = n;
        suffixIndex = i;
        return true;
    }

    /// <summary> Byte-domain <see cref="LibGit2CS.Config.ConfigurationValueParser.TryParseRawInt64(System.ReadOnlySpan{char}, out long, out int)"/> — the digit/letter classes are ASCII by construction, so char tests become byte tests verbatim.
    /// </summary>
    private static bool TryParseRawInt64(ReadOnlySpan<byte> span, out long num, out int suffixIndex)
    {
        num = 0;
        suffixIndex = 0;

        // Exact port of git__strntol64 (util.c:34-118) with base 0: ASCII
        // whitespace skip, sign, base auto-detect (0x → hex, leading 0 → octal),
        // then a digit loop in the detected base.
        int i = 0;
        while (i < span.Length && IsAsciiWhitespace((char)span[i]))
        {
            i++;
        }

        bool negative = false;
        if (i < span.Length && (span[i] == (byte)'-' || span[i] == (byte)'+'))
        {
            negative = span[i] == (byte)'-';
            i++;
        }

        if (i >= span.Length)
        {
            return false; // ndig == 0 → "not a number"
        }

        int numberBase;
        if (span[i] != (byte)'0')
        {
            numberBase = 10;
        }
        else if (span.Length - i > 2 && (span[i + 1] == (byte)'x' || span[i + 1] == (byte)'X'))
        {
            numberBase = 16;
        }
        else
        {
            numberBase = 8;
        }

        // Skip the 0x prefix of hex numbers (no equivalent needed for octal:
        // a leading '0' has no impact on the value).
        if (numberBase == 16 && span.Length - i > 2 && span[i] == (byte)'0'
            && (span[i + 1] == (byte)'x' || span[i + 1] == (byte)'X'))
        {
            i += 2;
        }

        long n = 0;
        int ndig = 0;
        while (i < span.Length)
        {
            byte c = span[i];
            int v = numberBase;
            if (c is >= (byte)'0' and <= (byte)'9')
            {
                v = c - '0';
            }
            else if (c is >= (byte)'a' and <= (byte)'z')
            {
                v = c - 'a' + 10;
            }
            else if (c is >= (byte)'A' and <= (byte)'Z')
            {
                v = c - 'A' + 10;
            }

            if (v >= numberBase)
            {
                break;
            }

            try
            {
                n = checked((n * numberBase) + (negative ? -v : v));
            }
            catch (OverflowException)
            {
                return false; // ovfl → strntol64 error
            }

            ndig++;
            i++;
        }

        if (ndig == 0)
        {
            return false;
        }

        num = n;
        suffixIndex = i;
        return true;
    }

    /// <summary>
    /// ASCII whitespace test matching <c>git__isspace</c> (space plus
    /// <c>\t</c>-<c>\r</c>) — NOT <see cref="char.IsWhiteSpace(char)"/>,
    /// which also accepts non-ASCII characters.
    /// </summary>
    private static bool IsAsciiWhitespace(char c) => c is ' ' or (>= '\t' and <= '\r');
}
