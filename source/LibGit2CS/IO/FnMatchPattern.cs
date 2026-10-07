// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

namespace LibGit2CS.IO;

/// <summary> Parsed glob pattern for gitignore/gitattributes/pathspec rules. Managed port of <c>git_attr_fnmatch</c> + <c>git_attr_fnmatch__parse</c> +
/// <c>git_attr_fnmatch__match</c> in <c>src/libgit2/attr_file.c</c>. </summary> <remarks> <para> Pattern syntax (from <c>gitattributes(5)</c> /
/// <c>gitignore(5)</c>): <list type="bullet"> <item><c>!</c> prefix negates the pattern.</item> <item>Leading <c>/</c> anchors to the containing directory
/// (full path match).</item> <item>Trailing <c>/</c> matches directories only.</item> <item><c>*</c> matches any chars except <c>/</c>; <c>**</c> matches
/// across <c>/</c>.</item> <item><c>#</c> starts a comment line (skipped).</item> <item>Spaces delimit patterns unless escaped with <c>\</c>.</item> </list>
/// </para> <para> <b>Byte-faithful.</b> <see cref="Pattern"/> and <see cref="ContainingDir"/> are <see cref="GitPath"/> (raw UTF-8 bytes), matching
/// <c>git_attr_fnmatch.pattern</c>/<c>containing_dir</c> which are <c>char *</c> into a <c>git_pool</c> byte arena (<c>attr_file.h:73-79</c>). <see
/// cref="Match(AttrPath)"/> uses <see cref="LibGit2CS.IO.GitPath.ComparePrefix(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/>/ <see cref="LibGit2CS.IO.GitPath.ComparePrefixIgnoreCase(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/> (ports <c>git__prefixcmp</c>/
/// <c>git__prefixcmp_icase</c> at <c>attr_file.c:490-500</c>) and the byte <see cref="WildMatch.IsMatch(ReadOnlySpan{byte}, ReadOnlySpan{byte},
/// WildMatchFlags)"/> overload. Non-UTF-8 patterns/paths round-trip byte-exact; the <c>OrdinalIgnoreCase</c> → ASCII-fold parity fix is applied
/// to the containing-dir prefix check and the wildmatch. </para> <para> The <see cref="Parse(ReadOnlySpan{char}, string?, Flag)"/> overload (char-based)
/// remains as a convenience overload: it encodes the pattern to UTF-8 bytes and constructs the byte-faithful fields. Callers use <see
/// cref="Parse(ReadOnlySpan{byte}, GitPath, Flag)"/> directly. </para> </remarks>
internal sealed class FnMatchPattern
{
    /// <summary>Pattern flags. Matches <c>GIT_ATTR_FNMATCH_*</c> in <c>attr_file.h</c>.</summary>
    [Flags]
    public enum Flag : uint
    {
        /// <summary>Pattern is negated (<c>!</c> prefix).</summary>
        Negative = 1u << 0,

        /// <summary>Pattern matches directories only (trailing <c>/</c>).</summary>
        Directory = 1u << 1,

        /// <summary>Pattern is anchored to the full path (leading <c>/</c>).</summary>
        FullPath = 1u << 2,

        /// <summary>Pattern is a macro definition (<c>[attr]</c>).</summary>
        Macro = 1u << 3,

        /// <summary>Pattern is used in an ignore context.</summary>
        Ignore = 1u << 4,

        /// <summary>Pattern contains wildcard chars.</summary>
        HasWild = 1u << 5,

        /// <summary>Allow unescaped spaces in pattern.</summary>
        AllowSpace = 1u << 6,

        /// <summary>Case-insensitive matching.</summary>
        ICase = 1u << 7,

        /// <summary>Pattern is <c>*</c> or <c>.</c> — matches everything.</summary>
        MatchAll = 1u << 8,

        /// <summary>Allow negation (<c>!</c> prefix).</summary>
        AllowNeg = 1u << 9,

        /// <summary>Allow macro definition (<c>[attr]</c>).</summary>
        AllowMacro = 1u << 10,
    }

    /// <summary>Incoming flags mask (the parse-time options preserved from the caller).</summary>
    /// <remarks>
    /// Ports the caller-set bits of <c>git_attr_fnmatch.flags</c> that
    /// <c>git_attr_fnmatch__parse</c> preserves: <c>ALLOWSPACE</c>,
    /// <c>ALLOWNEG</c>, <c>ALLOWMACRO</c>, <c>ICASE</c>, and <c>IGNORE</c>.
    /// The mask must include <c>ICASE</c>/<c>IGNORE</c> so the
    /// case-insensitive and ignore-context bits the caller set are preserved.
    /// </remarks>
    private const Flag Incoming = Flag.AllowSpace | Flag.AllowNeg | Flag.AllowMacro | Flag.ICase | Flag.Ignore;

    /// <summary>The glob pattern as raw UTF-8 bytes (unescaped).</summary>
    public GitPath Pattern { get; private set; }

    /// <summary>The containing directory prefix (for patterns from subdirectories), or empty.</summary>
    public GitPath ContainingDir { get; private set; }

    /// <summary>The pattern flags.</summary>
    public Flag Flags { get; private set; }

    /// <summary>
    /// Sets the <see cref="Flag.Ignore"/> context bit on this pattern.
    /// Called by the ignore engine (<c>ignore.c</c> sets
    /// <c>GIT_ATTR_FNMATCH_IGNORE</c> on every parsed rule at
    /// <c>ignore.c:208</c>) so that <see cref="Match"/> applies the
    /// ignore-specific directory-vs-file semantics.
    /// </summary>
    internal void SetIgnoreFlag() => Flags |= Flag.Ignore;

    /// <summary>
    /// Parses a pattern from a line of byte text. Matches <c>git_attr_fnmatch__parse</c>.
    /// Byte-faithful entry point used by byte-faithful callers.
    /// </summary>
    /// <param name="text">The pattern text (may contain trailing content after the pattern).</param>
    /// <param name="context">The context directory (for relative patterns), or empty.</param>
    /// <param name="incomingFlags">Incoming flags (AllowSpace, AllowNeg, AllowMacro, ICase, Ignore).</param>
    /// <returns>The parsed pattern, or <c>null</c> if the line is blank/comment.</returns>
    public static FnMatchPattern? Parse(ReadOnlySpan<byte> text, GitPath context, Flag incomingFlags)
    {
        // Check optimized patterns first (* or .).
        if (text.Length == 1 && (text[0] == (byte)'*' || text[0] == (byte)'.'))
        {
            return new FnMatchPattern
            {
                Pattern = GitPath.FromUtf8Bytes(text.ToArray()),
                Flags = Flag.MatchAll,
            };
        }

        Flag flags = incomingFlags & Incoming;
        bool allowSpace = (flags & Flag.AllowSpace) != 0;

        ReadOnlySpan<byte> pattern = text;

        // Skip leading whitespace (unless allowSpace).
        while (!allowSpace && pattern.Length > 0 && IsSpaceByte(pattern[0]))
        {
            pattern = pattern[1..];
        }

        // Blank line or comment — not a pattern.
        if (pattern.Length == 0 || pattern[0] == (byte)'#' || pattern[0] == (byte)'\n' ||
            (pattern[0] == (byte)'\r' && pattern.Length > 1 && pattern[1] == (byte)'\n'))
        {
            return null;
        }

        // [attr] macro definition.
        if (pattern.Length > 0 && pattern[0] == (byte)'[' && (flags & Flag.AllowMacro) != 0)
        {
            if (pattern.Length >= 6 && pattern[..6].SequenceEqual("[attr]"u8))
            {
                flags |= Flag.Macro;
                pattern = pattern[6..];
            }
            // else: a character range like [a-e]* — accepted as-is.
        }

        // ! negation prefix.
        if (pattern.Length > 0 && pattern[0] == (byte)'!' && (flags & Flag.AllowNeg) != 0)
        {
            flags |= Flag.Negative;
            pattern = pattern[1..];
        }

        // Scan until non-escaped whitespace.
        int slashCount = 0;
        bool escaped = false;
        int scanEnd = 0;

        for (int i = 0; i < pattern.Length; i++)
        {
            byte c = pattern[i];

            if (c == (byte)'\\' && !escaped)
            {
                escaped = true;
                continue;
            }

            if (IsSpaceByte(c) && !escaped)
            {
                if (!allowSpace || (c != (byte)' ' && c != (byte)'\t' && c != (byte)'\r'))
                {
                    break;
                }
            }
            else if (c == (byte)'/')
            {
                flags |= Flag.FullPath;
                slashCount++;
            }
            else if (IsWildcardByte(c) && !escaped)
            {
                flags |= Flag.HasWild;
            }

            escaped = false;
            scanEnd = i + 1;
        }

        // Handle the leading slash: C (attr_file.c:772-777) advances the pattern past the FIRST leading '/' only (the `slash_count == 1 && pattern == scan`
        // guard fires once) — "//foo" keeps "/foo" and "///foo" keeps "//foo".
        int patternStart = pattern.Length > 0 && pattern[0] == (byte)'/' ? 1 : 0;

        int length = scanEnd - patternStart;
        if (scanEnd == 0 || length <= 0)
        {
            return null;
        }

        byte[] patternBytes = pattern[patternStart..scanEnd].ToArray();

        // Remove trailing \r (CRLF files).
        if (patternBytes.Length > 0 && patternBytes[^1] == (byte)'\r')
        {
            patternBytes = patternBytes[..^1];
            if (patternBytes.Length == 0)
            {
                return null;
            }
        }

        // Remove trailing spaces (respecting escapes).
        patternBytes = RemoveTrailingSpacesBytes(patternBytes);
        if (patternBytes.Length == 0)
        {
            return null;
        }

        // Trailing / means directory-only.
        if (patternBytes[^1] == (byte)'/')
        {
            patternBytes = patternBytes[..^1];
            flags |= Flag.Directory;
            slashCount--;

            if (slashCount <= 0)
            {
                flags &= ~Flag.FullPath;
            }
        }

        // Set containing_dir from context.
        GitPath containingDir = default;
        if (!context.IsEmpty)
        {
            ReadOnlySpan<byte> ctxSpan = context.Span;
            int slash = ctxSpan.LastIndexOf((byte)'/');
            if (slash >= 0)
            {
                containingDir = GitPath.FromUtf8Bytes(ctxSpan[..(slash + 1)].ToArray());
            }
        }

        // Unescape internal whitespace.
        patternBytes = UnescapeSpacesBytes(patternBytes);

        return new FnMatchPattern
        {
            Pattern = GitPath.FromUtf8Bytes(patternBytes),
            ContainingDir = containingDir,
            Flags = flags,
        };
    }

    /// <summary>
    /// Parses a pattern from a line of char text. Matches <c>git_attr_fnmatch__parse</c>.
    /// Convenience overload: encodes the pattern to UTF-8 bytes and delegates to
    /// <see cref="Parse(ReadOnlySpan{byte}, GitPath, Flag)"/>.
    /// </summary>
    public static FnMatchPattern? Parse(ReadOnlySpan<char> text, string? context, Flag incomingFlags)
    {
        GitPath ctxPath = context is null ? default : GitPath.FromUtf8String(context);
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text)];
        Encoding.UTF8.GetBytes(text, bytes);
        return Parse(bytes, ctxPath, incomingFlags);
    }

    /// <summary>
    /// Tests whether this pattern matches the given path. Matches
    /// <c>git_attr_fnmatch__match</c> (<c>attr_file.c:477-536</c>).
    /// </summary>
    public bool Match(AttrPath path)
    {
        // If the rule was generated in a subdirectory, only match paths
        // inside that directory. git__prefixcmp / git__prefixcmp_icase
        // (attr_file.c:490-500) → GitPath.ComparePrefix / ComparePrefixIgnoreCase.
        GitPath relPath = path.Path;

        if (!ContainingDir.IsEmpty)
        {
            bool icase = (Flags & Flag.ICase) != 0;
            int cmp = icase
                ? GitPath.ComparePrefixIgnoreCase(relPath, ContainingDir)
                : GitPath.ComparePrefix(relPath, ContainingDir);
            if (cmp != 0)
            {
                return false;
            }

            relPath = relPath.Slice(ContainingDir.Length);
        }

        WildMatchFlags wildmatchFlags = WildMatchFlags.None;

        if ((Flags & Flag.ICase) != 0)
        {
            wildmatchFlags |= WildMatchFlags.CaseInsensitive;
        }

        GitPath filename;
        if ((Flags & Flag.FullPath) != 0)
        {
            filename = relPath;
            wildmatchFlags |= WildMatchFlags.Pathname;
        }
        else
        {
            filename = path.Basename;
        }

        // Directory-only constraint (attr_file.c:512-533).
        if ((Flags & Flag.Directory) != 0 && !path.IsDir)
        {
            // For attribute checks or checks at the root of this match's
            // containing_dir (or root of the repository if no containing_dir),
            // do not match. The C `path->basename == relpath` pointer-equality
            // test (attr_file.c:521) is true when there's no containing_dir AND
            // no slash in the path — i.e. the basename IS the whole relative
            // path. Equivalent byte-domain check: lengths equal (basename
            // spans the whole relPath).
            if ((Flags & Flag.Ignore) == 0 || path.Basename.Length == relPath.Length)
            {
                return false;
            }

            // Fail match if this is a file with same name as ignored folder.
            bool sameName = (Flags & Flag.ICase) != 0
                ? GitPath.CompareIgnoreCase(Pattern, relPath) == 0
                : GitPath.Compare(Pattern, relPath) == 0;

            if (sameName)
            {
                return false;
            }

            return WildMatch.IsMatch(Pattern.Span, relPath.Span, wildmatchFlags);
        }

        return WildMatch.IsMatch(Pattern.Span, filename.Span, wildmatchFlags);
    }

    private static bool IsWildcardByte(byte c) => c is (byte)'*' or (byte)'?' or (byte)'[';

    private static bool IsSpaceByte(byte c)
        => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\v' or (byte)'\f';

    /// <summary>
    /// Removes trailing unescaped spaces. Matches <c>trailing_space_length</c>.
    /// Byte-port of the char-based <c>RemoveTrailingSpaces</c>.
    /// </summary>
    private static byte[] RemoveTrailingSpacesBytes(byte[] str)
    {
        int n = str.Length;
        while (n > 0)
        {
            if (str[n - 1] is not (byte)' ' and not (byte)'\t')
            {
                break;
            }

            // Count escape chars before this space.
            int i = n;
            while (i > 1 && str[i - 2] == (byte)'\\')
            {
                i--;
            }

            if ((n - i) % 2 != 0)
            {
                break; // odd number of backslashes — space is escaped
            }

            n--;
        }

        return n < str.Length ? str[..n] : str;
    }

    /// <summary>
    /// Unescapes escaped whitespace, removing the backslash. Matches
    /// <c>unescape_spaces</c>. Byte-port of the char-based <c>UnescapeSpaces</c>.
    /// </summary>
    private static byte[] UnescapeSpacesBytes(byte[] str)
    {
        bool hasBackslash = false;
        foreach (byte b in str)
        {
            if (b == (byte)'\\')
            {
                hasBackslash = true;
                break;
            }
        }

        if (!hasBackslash)
        {
            return str;
        }

        byte[] result = new byte[str.Length];
        int pos = 0;
        bool escaped = false;

        for (int scan = 0; scan < str.Length; scan++)
        {
            if (!escaped && str[scan] == (byte)'\\')
            {
                escaped = true;
                continue;
            }

            // Only re-insert backslash for escaped non-spaces.
            if (escaped && !IsSpaceByte(str[scan]))
            {
                result[pos++] = (byte)'\\';
            }

            result[pos++] = str[scan];
            escaped = false;
        }

        return result[..pos];
    }
}
