// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Collections.Immutable;
using System.Text.RegularExpressions;

using LibGit2CS.Core;

namespace LibGit2CS.Diff;

/// <summary>
/// A diff driver: per-language funcname patterns + binary/text flags.
/// Managed port of <c>struct git_diff_driver</c> in
/// <c>src/libgit2/diff_driver.c:37-44</c> + the built-in table in
/// <c>src/libgit2/userdiff.h</c>.
/// </summary>
internal sealed class DiffDriver(string name)
{
    private const int BytesToCheckNul = 8000;

    private const GitDiffOptionsFlags ForceDiffable =
        GitDiffOptionsFlags.ForceText | GitDiffOptionsFlags.ForceBinary;

    public DiffDriverType Type { get; init; } = DiffDriverType.Auto;
    public GitDiffOptionsFlags BinaryFlags { get; init; }
    public GitDiffOptionsFlags OtherFlags { get; init; }
    public ImmutableList<DiffDriverPattern> FnPatterns { get; init; } = [];
    public RegexAdapter? WordRegex { get; init; }
    public string Name => name;

    /// <summary>
    /// Applies the driver's binary/other flags to the option flags. Matches
    /// <c>git_diff_driver_update_options</c> (diff_driver.c:402-409). If the
    /// user has not explicitly forced text or binary, the driver's
    /// <see cref="BinaryFlags"/> are applied; <see cref="OtherFlags"/> are
    /// always applied.
    /// </summary>
    public void UpdateOptions(ref GitDiffOptionsFlags flags)
    {
        if ((flags & ForceDiffable) == 0)
        {
            flags |= BinaryFlags;
        }

        flags |= OtherFlags;
    }

    /// <summary>
    /// Scans the first 8000 bytes for a NUL byte. Matches
    /// <c>git_diff_driver_content_is_binary</c> (diff_driver.c:411-431).
    /// </summary>
    public static bool ContentIsBinary(ReadOnlySpan<byte> content)
    {
        ReadOnlySpan<byte> search = content.Length > BytesToCheckNul
            ? content[..BytesToCheckNul]
            : content;

        return search.Contains((byte)0);
    }

    /// <summary>
    /// Builds the function-name extractor for the xdiff bridge. Returns
    /// <c>null</c> when the driver has no funcname detection (never, since
    /// even the <c>Auto</c> archetype uses the simple alpha/underscore/dollar
    /// check).
    /// </summary>
    /// <remarks>
    /// For <see cref="DiffDriverType.PatternList"/>: iterates patterns, regex
    /// searches for capture group 1 (or full match), matching
    /// <c>diff_context_line__pattern_match</c> (diff_driver.c:441-465). For
    /// other types: checks if the first char is alpha/underscore/dollar,
    /// matching <c>diff_context_line__simple</c> (diff_driver.c:433-439).
    /// </remarks>
    public Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)> GetFunctionNameExtractor()
    {
        if (Type == DiffDriverType.PatternList && FnPatterns.Count > 0)
        {
            return PatternExtractor;
        }

        return SimpleExtractor;
    }

    /// <summary>
    /// Simple funcname: first char is alpha, '_', or '$'. The name is the
    /// whole (rtrimmed) line. Matches <c>diff_context_line__simple</c>.
    /// </summary>
    private static (bool, Range) SimpleExtractor(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> trimmed = TrimTrailingWhitespace(line);
        if (trimmed.IsEmpty)
        {
            return (false, default);
        }

        char first = (char)trimmed[0];
        if (char.IsAsciiLetter(first) || first == '_' || first == '$')
        {
            return (true, 0..trimmed.Length);
        }

        return (false, default);
    }

    /// <summary> Pattern-based funcname: iterates patterns, regex searches, extracts capture group 1 (or full match). Matches
    /// <c>diff_context_line__pattern_match</c> (diff_driver.c:441-465). </summary> <remarks> The regex runs over the raw line bytes: <see
    /// cref="RegexAdapter.Search(ReadOnlySpan{byte}, Span{RegexMatch})"/> returns byte offsets by identity, so the returned <see cref="Range"/> slices
    /// <paramref name="line"/> directly — no UTF-8 decode and no char→byte back-mapping. C consumes to the match start, truncates to the match end, then
    /// <c>git_str_rtrim</c>s the region (trailing trim only — there is no leading trim). </remarks>
    private (bool, Range) PatternExtractor(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> trimmed = TrimTrailingWhitespace(line);
        if (trimmed.IsEmpty)
        {
            return (false, default);
        }

        Span<RegexMatch> matches = stackalloc RegexMatch[2];

        foreach (DiffDriverPattern pat in FnPatterns)
        {
            bool matched;
            try
            {
                matched = pat.Regex.Search(trimmed, matches);
            }
            catch (RegexMatchTimeoutException)
            {
                // matches are bounded (RegexAdapter's 1 s budget).
                // C treats a git_regexp_search failure (e.g. PCRE's match
                // limit) as no-match for this pattern and tries the next
                // one (diff_driver.c:451-458).
                continue;
            }

            if (matched)
            {
                if (pat.Negate)
                {
                    return (false, default);
                }

                // Use capture group 1 if it participated; else full match (group 0).
                int idx = !matches[1].IsUnset ? 1 : 0;
                int start = matches[idx].Start;
                int end = matches[idx].End;

                // C's git_str_rtrim on the match region (diff_driver.c:461).
                while (end > start && IsAsciiWhitespace(trimmed[end - 1]))
                {
                    end--;
                }

                // matches[] are byte offsets into `trimmed` by identity (the
                // bijection is 1 char = 1 byte), so the Range slices the raw
                // bytes directly (the Xdiff package's emitter applies the
                // range to this same line span when building hunk headers).
                return (true, new Range(start, end));
            }
        }

        return (false, default);
    }

    private static bool IsAsciiWhitespace(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f' or (byte)'\v';

    private static ReadOnlySpan<byte> TrimTrailingWhitespace(ReadOnlySpan<byte> span)
    {
        int end = span.Length;
        while (end > 0)
        {
            byte c = span[end - 1];
            if (c is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f' or (byte)'\v'))
            {
                break;
            }

            end--;
        }

        return span[..end];
    }
}
