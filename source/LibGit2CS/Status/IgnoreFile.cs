// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Status;

/// <summary>
/// A parsed <c>.gitignore</c> file. Managed port of the ignore-specific
/// path through <c>git_attr_file</c> + <c>parse_ignore_file</c>
/// (<c>src/libgit2/ignore.c:170-243</c>).
/// </summary>
/// <remarks>
/// <para>
/// The C <c>parse_ignore_file</c> differs from the attribute parse
/// (<c>git_attr_file__parse_buffer</c>) in three ways:
/// <list type="bullet">
/// <item>No <c>[attr]</c> macros — <c>allow_macros = false</c>.</item>
/// <item>Sets <c>GIT_ATTR_FNMATCH_IGNORE</c> on every parsed pattern.</item>
/// <item>Drops useless negative rules via <c>does_negate_rule</c>
/// (a negative pattern with no matching positive rule is discarded;
/// wildcard negatives are always kept).</item>
/// </list>
/// </para>
/// <para>
/// The <c>context</c> (subdirectory scope) is set from the file's relative
/// path when the file lives below the repo root (e.g.
/// <c>src/.gitignore</c> → context <c>src/</c>), matching
/// <c>ignore.c:183-187</c>.
/// </para>
/// </remarks>
internal sealed class IgnoreFile
{
    private readonly List<IgnoreRule> _rules = [];

    /// <summary>The parsed rules in file order. Lookups iterate in reverse.</summary>
    public IReadOnlyList<IgnoreRule> Rules => _rules;

    /// <summary>The relative path the file was loaded from (e.g. <c>src/.gitignore</c>), or null for in-memory.</summary>
    public string? RelativePath { get; }

    public IgnoreFile(string? relativePath = null) => RelativePath = relativePath;

    /// <summary>
    /// Parses <c>.gitignore</c> text into rules. Matches
    /// <c>parse_ignore_file</c> (ignore.c:170-243). Drops useless negative
    /// rules per <c>does_negate_rule</c>.
    /// </summary>
    /// <param name="text">The file content (UTF-8 BOM tolerated).</param>
    /// <param name="ignoreCase">Whether to set <c>ICASE</c> on patterns (from <c>core.ignorecase</c>).</param>
    /// <param name="context">The subdirectory scope, or null for root.</param>
    public void ParseBuffer(string text, bool ignoreCase, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Skip UTF-8 BOM if present — matches git_str_detect_bom.
        if (text.Length >= 3 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        FnMatchPattern.Flag incomingFlags = FnMatchPattern.Flag.AllowSpace | FnMatchPattern.Flag.AllowNeg;
        if (ignoreCase)
        {
            incomingFlags |= FnMatchPattern.Flag.ICase;
        }

        int pos = 0;
        while (pos < text.Length)
        {
            int lineEnd = text.IndexOf('\n', pos, StringComparison.Ordinal);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            ReadOnlySpan<char> line = text.AsSpan(pos, lineEnd - pos);
            pos = lineEnd + 1;

            var pattern = FnMatchPattern.Parse(line, context, incomingFlags);
            if (pattern is null)
            {
                continue; // blank or comment
            }

            // ignore.c:208 — every parsed rule carries the IGNORE flag so
            // that FnMatchPattern.Match applies the ignore-specific
            // directory-vs-file semantics.
            pattern.SetIgnoreFlag();

            var rule = new IgnoreRule(pattern);

            // does_negate_rule: drop useless non-wildcard negative rules
            // (ignore.c:221-223). Wildcard negatives are always kept
            // (we cannot always verify whether a wildcard negates another rule).
            if (rule.IsNegative && !rule.HasWild)
            {
                if (!DoesNegateRule(_rules, pattern))
                {
                    continue; // useless negative — drop
                }
            }

            _rules.Add(rule);
        }
    }

    /// <summary> Byte-faithful overload. Parses <c>.gitignore</c> raw bytes into rules without a lossy UTF-8 decode, so non-UTF-8 patterns round-trip
    /// byte-exact. Matches <c>parse_ignore_file</c> (ignore.c:170-243) on the raw byte buffer (the C original reads bytes via <c>git_futils_readbuffer</c>).
    /// </summary>
    public void ParseBuffer(ReadOnlyMemory<byte> content, bool ignoreCase, GitPath context = default)
    {
        ReadOnlySpan<byte> span = content.Span;

        // Skip UTF-8 BOM if present (EF BB BF) — matches git_str_detect_bom.
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        FnMatchPattern.Flag incomingFlags = FnMatchPattern.Flag.AllowSpace | FnMatchPattern.Flag.AllowNeg;
        if (ignoreCase)
        {
            incomingFlags |= FnMatchPattern.Flag.ICase;
        }

        int pos = 0;
        while (pos < span.Length)
        {
            int lineEnd = span[pos..].IndexOf((byte)'\n');
            if (lineEnd < 0)
            {
                lineEnd = span.Length;
            }
            else
            {
                lineEnd += pos;
            }

            ReadOnlySpan<byte> line = span[pos..lineEnd];
            pos = lineEnd + 1;

            var pattern = FnMatchPattern.Parse(line, context, incomingFlags);
            if (pattern is null)
            {
                continue; // blank or comment
            }

            pattern.SetIgnoreFlag();

            var rule = new IgnoreRule(pattern);

            if (rule.IsNegative && !rule.HasWild)
            {
                if (!DoesNegateRule(_rules, pattern))
                {
                    continue; // useless negative — drop
                }
            }

            _rules.Add(rule);
        }
    }

    /// <summary> Loads a <c>.gitignore</c> file from the workdir (filesystem). Matches the <c>GIT_ATTR_FILE_SOURCE_FILE</c> path in <c>git_attr_file__load</c>.
    /// Returns null if the file does not exist. Byte-faithful: the FS-boundary <c>Path.Join</c> routes through <see cref="GitPath.ToFileSystemString"/>.
    /// </summary>
    public static ValueTask<IgnoreFile?> LoadFromWorkdirAsync(string workdir, bool ignoreCase, GitPath relativePath, CancellationToken cancellationToken)
    {
        // FS boundary: single transcode point.
        string fullPath = Path.Join(workdir, relativePath.ToFileSystemString());
        if (!File.Exists(fullPath))
        {
            // Absence is the majority case when probing for ignore files.
            return ValueTask.FromResult<IgnoreFile?>(null);
        }

        return new ValueTask<IgnoreFile?>(LoadFromWorkdirSlowAsync(fullPath, ignoreCase, relativePath, cancellationToken));
    }

    /// <summary>Slow path of <see cref="LoadFromWorkdirAsync(string, bool, GitPath, CancellationToken)"/>: reads the ignore file (disk IO).</summary>
    private static async Task<IgnoreFile?> LoadFromWorkdirSlowAsync(string fullPath, bool ignoreCase, GitPath relativePath, CancellationToken cancellationToken)
    {
        // Read raw bytes so non-UTF-8 patterns round-trip byte-exact (a UTF-8 read with replacement
        // fallback would corrupt invalid byte sequences).
        byte[] content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        // IgnoreFile.RelativePath is string-typed (public API); the relative path is an attribute path, so the
        // ToUtf8String decode is the correct one.
        var file = new IgnoreFile(relativePath.ToUtf8String());
        GitPath context = GetContextBytes(relativePath);
        file.ParseBuffer(content, ignoreCase, context);
        return file;
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. NB: the <c>string relativePath</c> parameter keeps
    /// its default to preserve the existing public signature; the <see cref="GitPath"/> overload above takes CT last per convention. </summary>
    public static ValueTask<IgnoreFile?> LoadFromWorkdirAsync(string workdir, bool ignoreCase, CancellationToken cancellationToken, string relativePath = ".gitignore")
        => LoadFromWorkdirAsync(workdir, ignoreCase, GitPath.FromUtf8String(relativePath), cancellationToken);

    /// <summary>
    /// Loads a <c>.gitignore</c> from an explicit absolute path (for
    /// <c>.git/info/exclude</c> and <c>core.excludesfile</c>).
    /// </summary>
    public static ValueTask<IgnoreFile?> LoadFromPathAsync(string fullPath, bool ignoreCase, CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            return ValueTask.FromResult<IgnoreFile?>(null);
        }

        return new ValueTask<IgnoreFile?>(LoadFromPathSlowAsync(fullPath, ignoreCase, cancellationToken));
    }

    /// <summary>Slow path of <see cref="LoadFromPathAsync"/>: reads the ignore file (disk IO).</summary>
    private static async Task<IgnoreFile?> LoadFromPathSlowAsync(string fullPath, bool ignoreCase, CancellationToken cancellationToken)
    {
        byte[] content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var file = new IgnoreFile(fullPath);
        file.ParseBuffer(content, ignoreCase, default);
        return file;
    }

    /// <summary>
    /// Extracts the subdirectory context from a relative path (e.g.
    /// <c>src/.gitignore</c> → <c>src/</c>). Matches the context extraction
    /// in <c>parse_ignore_file</c> (ignore.c:183-187) — only files below
    /// the repo root that end with <c>/.gitignore</c> get a context.
    /// </summary>
    private static string? GetContext(string relativePath)
    {
        const string Suffix = "/" + ".gitignore";
        if (relativePath.EndsWith(Suffix, StringComparison.Ordinal) &&
            relativePath.Length > Suffix.Length)
        {
            return relativePath[..(relativePath.Length - ".gitignore".Length)];
        }

        return null;
    }

    /// <summary>
    /// Byte-faithful overload of <see cref="GetContext"/>. Returns a
    /// <see cref="GitPath"/> (empty if no context).
    /// </summary>
    private static GitPath GetContextBytes(string relativePath)
    {
        string? ctx = GetContext(relativePath);
        return ctx is null ? default : GitPath.FromUtf8String(ctx);
    }

    /// <summary>Byte-faithful overload taking a <see cref="GitPath"/>.</summary>
    private static GitPath GetContextBytes(GitPath relativePath)
        => GetContextBytes(relativePath.ToUtf8String());

    /// <summary>
    /// Checks whether a negative (non-wildcard) rule is "useful" — i.e.
    /// whether an existing positive rule covers the same path. Matches
    /// <c>does_negate_rule</c> (ignore.c:103-168). Returns true if the
    /// negative should be kept (some existing rule covers it), false if it
    /// should be dropped.
    /// </summary>
    /// <remarks>
    /// Non-wildcard rules are checked via <c>does_negate_pattern</c> (exact
    /// basename-tail match). Wildcard rules in the existing list are checked
    /// by wildmatching the existing rule's full path against the negative's
    /// full path. If any existing rule covers the negative, the negative is
    /// kept.
    /// </remarks>
    private static bool DoesNegateRule(List<IgnoreRule> rules, FnMatchPattern neg)
    {
        // negPath = containingDir + pattern (ignore.c:137). Byte-concat via the
        // GitPath operator + (ports the git_str_puts/memcpy byte concat used
        // by the C path assembly).
        GitPath negPath = neg.ContainingDir + neg.Pattern;

        foreach (IgnoreRule rule in rules)
        {
            FnMatchPattern pat = rule.Pattern;

            if (!rule.HasWild)
            {
                if (DoesNegatePattern(pat, neg))
                {
                    return true;
                }

                continue;
            }

            // Wildcard rule — wildmatch the existing rule's full path
            // against the negative's full path (ignore.c:137-159).
            GitPath rulePath = pat.ContainingDir + pat.Pattern;

            WildMatchFlags wildmatchFlags = WildMatchFlags.Pathname;
            if ((neg.Flags & FnMatchPattern.Flag.ICase) != 0)
            {
                wildmatchFlags |= WildMatchFlags.CaseInsensitive;
            }

            // If the existing rule is not FULLPATH, drop WM_PATHNAME so
            // that *.txt-style patterns match across directory boundaries.
            WildMatchFlags effectiveFlags = wildmatchFlags;
            if ((pat.Flags & FnMatchPattern.Flag.FullPath) == 0)
            {
                effectiveFlags &= ~WildMatchFlags.Pathname;
            }

            if (WildMatch.IsMatch(rulePath.Span, negPath.Span, effectiveFlags))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a positive non-wildcard rule and a negative non-wildcard
    /// pattern negate each other. Matches <c>does_negate_pattern</c>
    /// (ignore.c:50-88). The rules:
    /// <list type="bullet">
    /// <item>If lengths match: exact byte match (case-sensitive unless
    /// the negative is ICASE — ASCII-only fold, not OrdinalIgnoreCase).</item>
    /// <item>If lengths differ: the shorter pattern must contain no
    /// <c>/</c> (basename-only) and the longer must end with
    /// <c>/</c>+shorter.</item>
    /// </list>
    /// </summary>
    private static bool DoesNegatePattern(FnMatchPattern rule, FnMatchPattern neg)
    {
        // rule must be positive, neg must be negative.
        if ((rule.Flags & FnMatchPattern.Flag.Negative) != 0 ||
            (neg.Flags & FnMatchPattern.Flag.Negative) == 0)
        {
            return false;
        }

        bool caseInsensitive = (neg.Flags & FnMatchPattern.Flag.ICase) != 0;
        GitPath rulePat = rule.Pattern;
        GitPath negPat = neg.Pattern;

        if (rulePat.Length == negPat.Length)
        {
            // ASCII-fold parity fix: GitPath.Compare/CompareIgnoreCase ports
            // git__strcmp/strcasecmp (ASCII-only), NOT string.Equals(Ordinal*).
            return caseInsensitive
                ? GitPath.CompareIgnoreCase(rulePat, negPat) == 0
                : GitPath.Compare(rulePat, negPat) == 0;
        }

        GitPath shorter, longer;
        if (rulePat.Length < negPat.Length)
        {
            shorter = rulePat;
            longer = negPat;
        }
        else
        {
            shorter = negPat;
            longer = rulePat;
        }

        // The shorter pattern must be basename-only (no '/').
        if (shorter.Span.IndexOf((byte)'/') >= 0)
        {
            return false;
        }

        // The longer pattern must have a '/' immediately before the tail (C: p = longer + length - shorter.length; p[-1] != '/' → false, ignore.c:80-84). For
        // "/bar" vs "bar", p[-1] is the leading '/' — a valid basename re-includeshorter.Length <
        // longer.Length here, so the index is never negative.
        if (longer.Span[longer.Length - shorter.Length - 1] != (byte)'/')
        {
            return false;
        }

        ReadOnlySpan<byte> tail = longer.Span[(longer.Length - shorter.Length)..];
        return caseInsensitive
            ? CompareIgnoreCaseBytes(tail, shorter.Span)
            : tail.SequenceEqual(shorter.Span);
    }

    private static bool CompareIgnoreCaseBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (GitPath.AsciiToLower(a[i]) != GitPath.AsciiToLower(b[i]))
            {
                return false;
            }
        }

        return true;
    }
}
