// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Index;

namespace LibGit2CS.IO;

/// <summary> Compiled pathspec for path-limited operations. Managed port of <c>src/libgit2/pathspec.c</c> + <c>pathspec.h</c>. </summary> <remarks> <para> A
/// pathspec is a set of glob patterns (using gitignore/gitattributes syntax) used to limit which paths an operation touches. Patterns are compiled into <see
/// cref="FnMatchPattern"/> objects at construction time. </para> <para> Match semantics: a path matches the pathspec if it matches any positive pattern AND no
/// negative (<c>!</c>) pattern. The <c>NO_GLOB</c> flag disables wildcard expansion (exact string compare only). The <c>IGNORE_CASE</c>/<c>USE_CASE</c> flags
/// control case sensitivity. </para> <para> <b>Byte-faithful.</b> Patterns and paths are <see cref="GitPath"/> (raw UTF-8 bytes). The match context (<see
/// cref="MatchContext"/>) ports <c>pathspec_match_context</c> (<c>pathspec.c:111-115</c>): the <see cref="MatchContext.Strcomp"/>/ <see
/// cref="MatchContext.Strncomp"/> delegate slots and <see cref="MatchContext.WildmatchFlags"/> are set ONCE per match (by <see cref="MatchContext.Create"/>
/// porting <c>pathspec_match_context_init</c>), not re-derived per pattern. This replaces the per-call <c>StringComparison</c> ternary in
/// <c>MatchOne</c> — the same structural fix applied to the diff pipeline's <c>_strcomp</c>/<c>_strncomp</c>/<c>_pfxcomp</c>/<c>_entrycomp</c> slots.
/// The ASCII-only fold parity fix applies to the <see cref="MatchContext.Strcomp"/>/<see cref="MatchContext.Strncomp"/> slots via <see
/// cref="LibGit2CS.IO.GitPath.CompareIgnoreCase(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/>/<see cref="GitPath.CompareIgnoreCase(GitPath, GitPath, int)"/>. </para> <para> The <see cref="string"/>-taking overloads
/// (<see cref="New(string[])"/>, <see cref="MatchesPath(MatchFlags, string)"/>, etc.) remain as convenience overloads that encode to UTF-8 and delegate to the
/// byte-faithful overloads. </para> </remarks>
public sealed class GitPathSpec
{
    /// <summary>Pathspec match flags. Matches <c>git_pathspec_flag_t</c>.</summary>
    [Flags]
    public enum MatchFlags
    {
        /// <summary>Default behavior.</summary>
        Default = 0,

        /// <summary>Case-insensitive match.</summary>
        IgnoreCase = 1 << 0,

        /// <summary>Force case-sensitive match.</summary>
        UseCase = 1 << 1,

        /// <summary>Disable wildcard glob; use simple string compare.</summary>
        NoGlob = 1 << 2,

        /// <summary>Return <c>NotFound</c> if zero matches.</summary>
        NoMatchError = 1 << 3,

        /// <summary>Track unmatched patterns in the result.</summary>
        FindFailures = 1 << 4,

        /// <summary>Only track whether patterns matched; skip building file list.</summary>
        FailuresOnly = 1 << 5,
    }

    /// <summary>Sentinel for "no pattern matched this path".</summary>
    public const int NoMatch = -1;

    private readonly List<FnMatchPattern> _patterns = [];
    private GitPath _prefix;

    /// <summary>The common non-wildcard prefix of all patterns, or empty.</summary>
    public GitPath Prefix => _prefix;

    /// <summary>Number of compiled patterns.</summary>
    public int PatternCount => _patterns.Count;

    /// <summary>True if the pathspec is empty (matches everything).</summary>
    public bool IsEmpty => _patterns.Count == 0;

    private GitPathSpec()
    {
    }

    /// <summary>
    /// Compiles a pathspec from an array of byte-faithful patterns. Matches
    /// <c>git_pathspec_new</c> + <c>git_pathspec__vinit</c>.
    /// </summary>
    public static GitPathSpec New(params GitPath[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var ps = new GitPathSpec();

        if (IsEmptyPathspec(patterns))
        {
            return ps;
        }

        foreach (GitPath pattern in patterns)
        {
            if (pattern.IsEmpty)
            {
                continue;
            }

            FnMatchPattern.Flag flags = FnMatchPattern.Flag.AllowSpace | FnMatchPattern.Flag.AllowNeg;
            var match = FnMatchPattern.Parse(pattern.Span, default, flags);
            if (match is not null)
            {
                ps._patterns.Add(match);
            }
        }

        ps._prefix = ComputePrefix(patterns);
        return ps;
    }

    /// <summary> <c>string[]</c> convenience overload of <see cref="New(GitPath[])"/>. Now <c>params</c>. Encodes each pattern via <see
    /// cref="GitPath.FromUtf8String"/>. </summary>
    public static GitPathSpec New(params string[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (IsEmptyPathspec(patterns))
        {
            return new GitPathSpec();
        }

        var bytePatterns = new GitPath[patterns.Length];
        for (int i = 0; i < patterns.Length; i++)
        {
            bytePatterns[i] = patterns[i] is null ? default : GitPath.FromUtf8String(patterns[i]);
        }

        return New(bytePatterns);
    }

    /// <summary>
    /// Tests whether a single byte-faithful path matches the pathspec. Matches
    /// <c>git_pathspec_matches_path</c>.
    /// </summary>
    public bool MatchesPath(MatchFlags flags, GitPath path)
        => MatchInternal(path, flags) > 0;

    /// <summary>
    /// Tests whether a single string path matches the pathspec. Encodes to
    /// UTF-8 and delegates to the byte overload.
    /// </summary>
    public bool MatchesPath(MatchFlags flags, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return MatchesPath(flags, GitPath.FromUtf8String(path));
    }

    /// <summary>
    /// Tests a byte-faithful path against the compiled patterns and returns
    /// the match result. Returns 1 = positive match, 0 = negative match
    /// (excluded), -1 = no match.
    /// </summary>
    internal int MatchInternal(GitPath path, MatchFlags flags)
    {
        if (_patterns.Count == 0)
        {
            return 1; // empty pathspec matches everything
        }

        var ctxt = MatchContext.Create(
            disableFnmatch: (flags & MatchFlags.NoGlob) != 0,
            casefold: (flags & MatchFlags.IgnoreCase) != 0);

        for (int i = 0; i < _patterns.Count; i++)
        {
            int result = MatchOne(_patterns[i], ctxt, path);
            if (result >= 0)
            {
                return result;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns the index of the first pattern that matches the byte-faithful
    /// path, or <see cref="NoMatch"/> if none. Matches
    /// <c>git_pathspec__match_at</c>.
    /// </summary>
    internal int MatchAt(GitPath path, MatchFlags flags)
    {
        // C (pathspec.c:173-193): an empty pathspec matches nothing
        // (git_pathspec__match_at leaves result at GIT_ENOTFOUND).
        if (_patterns.Count == 0)
        {
            return NoMatch;
        }

        var ctxt = MatchContext.Create(
            disableFnmatch: (flags & MatchFlags.NoGlob) != 0,
            casefold: (flags & MatchFlags.IgnoreCase) != 0);

        for (int i = 0; i < _patterns.Count; i++)
        {
            if (MatchOne(_patterns[i], ctxt, path) >= 0)
            {
                return i;
            }
        }

        return NoMatch;
    }

    /// <summary>
    /// Returns the matching pathspec pattern for the given byte-faithful path,
    /// or empty if the pathspec is empty or no pattern matches. Ports
    /// <c>git_pathspec__match</c> (pathspec.c:196-230) with its
    /// <c>matched_pathspec</c> out-parameter: the FIRST pattern with
    /// <c>result &gt;= 0</c> wins (<c>git_pathspec__match_at</c> breaks on the
    /// first match, positive or negative — so pattern ORDER matters); a
    /// negative match (<c>result == 0</c>) is NOT a match, so the pattern is
    /// returned only for a positive match (<c>result &gt; 0</c>). (C sets the
    /// out-parameter for any <c>result &gt;= 0</c>, but no caller consumes it
    /// for a negative match — the diff/checkout filters drop the entry — so
    /// reporting the pattern only for a positive match is behaviorally
    /// identical.)
    /// </summary>
    internal GitPath MatchPathspec(GitPath path, MatchFlags flags)
    {
        if (_patterns.Count == 0)
        {
            return default;
        }

        var ctxt = MatchContext.Create(
            disableFnmatch: (flags & MatchFlags.NoGlob) != 0,
            casefold: (flags & MatchFlags.IgnoreCase) != 0);

        for (int i = 0; i < _patterns.Count; i++)
        {
            int result = MatchOne(_patterns[i], ctxt, path);
            if (result >= 0)
            {
                return result > 0 ? _patterns[i].Pattern : default;
            }
        }

        return default;
    }

    /// <summary>
    /// Returns the matching pathspec pattern for the given string path.
    /// Encodes to UTF-8 and delegates to the byte overload.
    /// </summary>
    internal string? MatchPathspec(string path, MatchFlags flags)
    {
        ArgumentNullException.ThrowIfNull(path);
        GitPath result = MatchPathspec(GitPath.FromUtf8String(path), flags);
        return result.IsEmpty ? null : result.ToUtf8String();
    }

    /// <summary>
    /// Matches the pathspec against a sequence of index entries (from any
    /// iterator source: tree, index, workdir). Matches
    /// <c>pathspec_match_from_iterator</c>.
    /// </summary>
    public GitPathSpecMatchList Match(IEnumerable<GitIndexEntry> entries, MatchFlags flags)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return MatchPathsCore(entries.Select(e => e.Path), flags);
    }

    /// <summary>
    /// Matches the pathspec against a sequence of byte-faithful paths.
    /// Convenience overload.
    /// </summary>
    public GitPathSpecMatchList MatchPaths(IEnumerable<GitPath> paths, MatchFlags flags)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return MatchPathsCore(paths, flags);
    }

    /// <summary>
    /// Ports <c>pathspec_match_from_iterator</c> (pathspec.c:397-505) over a
    /// path sequence: ENOTFOUND paths are skipped; a NEGATIVE pattern match
    /// (result == 0) marks the pattern used but does NOT list the file; a
    /// positive match with FIND_FAILURES also marks every LATER pattern that
    /// matches the path (<c>pathspec_mark_remaining</c>, pathspec.c:343-369);
    /// and NO_MATCH_ERROR throws ENOTFOUND "no matching files were found"
    /// when nothing matched. (The C workdir-only "ignored and untracked"
    /// skip has no counterpart for index-entry/path sequences.)
    /// </summary>
    private GitPathSpecMatchList MatchPathsCore(IEnumerable<GitPath> paths, MatchFlags flags)
    {
        bool findFailures = (flags & MatchFlags.FindFailures) != 0;
        bool failuresOnly = (flags & MatchFlags.FailuresOnly) != 0;

        var matches = new List<GitPath>();
        bool[]? used = findFailures ? new bool[_patterns.Count] : null;
        int usedCount = 0;
        int foundFiles = 0;

        foreach (GitPath path in paths)
        {
            int idx = MatchAt(path, flags);
            if (idx < 0)
            {
                continue; // no pattern matched (GIT_ENOTFOUND)
            }

            // C (pathspec.c:449-453): a negative pattern match (result == 0)
            // marks the pattern used and does not list the file.
            bool positive = MatchOne(_patterns[idx], MatchContext.Create(
                disableFnmatch: (flags & MatchFlags.NoGlob) != 0,
                casefold: (flags & MatchFlags.IgnoreCase) != 0), path) > 0;
            if (!positive)
            {
                if (findFailures && used is not null && !used[idx])
                {
                    used[idx] = true;
                    usedCount++;
                }

                continue;
            }

            if (findFailures && used is not null)
            {
                if (!used[idx])
                {
                    used[idx] = true;
                    usedCount++;
                }

                // C (pathspec.c:460-464): also mark later patterns that match.
                if (usedCount < _patterns.Count)
                {
                    usedCount += MarkRemaining(used, idx + 1, path, flags);
                }
            }

            foundFiles++;
            if (!failuresOnly)
            {
                matches.Add(path);
            }
        }

        var failures = new List<GitPath>();
        if (findFailures)
        {
            Debug.Assert(used is not null, "used is allocated when findFailures is true");
            for (int i = 0; i < _patterns.Count; i++)
            {
                if (!used[i])
                {
                    failures.Add(_patterns[i].Pattern);
                }
            }
        }

        // C (pathspec.c:479-483): NO_MATCH_ERROR — no files matched.
        if ((flags & MatchFlags.NoMatchError) != 0 && foundFiles == 0)
        {
            throw new GitException(GitErrorCode.NotFound, "no matching files were found", GitErrorCategory.Invalid);
        }

        return new GitPathSpecMatchList(matches, failures);
    }

    /// <summary>
    /// Marks every pattern at index &gt;= <paramref name="start"/> that also
    /// positively matches <paramref name="path"/>. Ports
    /// <c>pathspec_mark_remaining</c> (pathspec.c:343-369).
    /// </summary>
    private int MarkRemaining(bool[] used, int start, GitPath path, MatchFlags flags)
    {
        int count = 0;
        var ctxt = MatchContext.Create(
            disableFnmatch: (flags & MatchFlags.NoGlob) != 0,
            casefold: (flags & MatchFlags.IgnoreCase) != 0);

        for (int i = start; i < _patterns.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            if (MatchOne(_patterns[i], ctxt, path) > 0)
            {
                used[i] = true;
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Matches the pathspec against a sequence of string paths. Encodes each
    /// to UTF-8 and delegates to
    /// <see cref="MatchPaths(IEnumerable{GitPath}, MatchFlags)"/>.
    /// </summary>
    public GitPathSpecMatchList MatchPaths(IEnumerable<string> paths, MatchFlags flags)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return MatchPaths(paths.Select(p => GitPath.FromUtf8String(p)), flags);
    }

    /// <summary>
    /// Match context holding the comparator slots and wildmatch flags. Ports
    /// <c>pathspec_match_context</c> (<c>pathspec.c:111-115</c>) + the
    /// <c>pathspec_match_context_init</c> factory (<c>pathspec.c:117-136</c>).
    /// The slots are set ONCE per match, not per pattern — the structural fix
    /// that replaces the per-call <c>StringComparison</c> ternary.
    /// </summary>
    internal readonly struct MatchContext
    {
        /// <summary>
        /// Wildmatch flags: <c>-1</c> (fnmatch disabled), <c>0</c>
        /// (case-sensitive), or <c>WM_CASEFOLD</c> (case-insensitive). Ports
        /// <c>wildmatch_flags</c>.
        /// </summary>
        public readonly int WildmatchFlags;

        /// <summary>
        /// String compare slot: <see cref="LibGit2CS.IO.GitPath.Compare(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/> or
        /// <see cref="LibGit2CS.IO.GitPath.CompareIgnoreCase(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/>. Ports
        /// <c>int (*strcomp)(const char *, const char *)</c>.
        /// </summary>
        public readonly Func<GitPath, GitPath, int> Strcomp;

        /// <summary>
        /// Length-bounded string compare slot: <see cref="GitPath.Compare(GitPath, GitPath, int)"/>
        /// or <see cref="GitPath.CompareIgnoreCase(GitPath, GitPath, int)"/>. Ports
        /// <c>int (*strncomp)(const char *, const char *, size_t)</c>.
        /// </summary>
        public readonly Func<GitPath, GitPath, int, int> Strncomp;

        private MatchContext(int wildmatchFlags, Func<GitPath, GitPath, int> strcomp, Func<GitPath, GitPath, int, int> strncomp)
        {
            WildmatchFlags = wildmatchFlags;
            Strcomp = strcomp;
            Strncomp = strncomp;
        }

        /// <summary>
        /// Creates a match context. Ports <c>pathspec_match_context_init</c>
        /// (<c>pathspec.c:117-136</c>).
        /// </summary>
        public static MatchContext Create(bool disableFnmatch, bool casefold)
        {
            int wildmatchFlags = disableFnmatch ? -1 : casefold ? (int)WildMatchFlags.CaseInsensitive : 0;
            Func<GitPath, GitPath, int> strcomp = casefold ? GitPath.CompareIgnoreCase : GitPath.Compare;
            Func<GitPath, GitPath, int, int> strncomp = casefold ? GitPath.CompareIgnoreCase : GitPath.Compare;
            return new MatchContext(wildmatchFlags, strcomp, strncomp);
        }
    }

    /// <summary>
    /// Matches one byte-faithful path against one pattern using the
    /// context's slots. Matches <c>pathspec_match_one</c>
    /// (<c>pathspec.c:138-171</c>). Returns 1 = match, 0 = negative match,
    /// -1 = no match.
    /// </summary>
    private static int MatchOne(FnMatchPattern match, MatchContext ctxt, GitPath path)
    {
        // MATCH_ALL patterns always match.
        if ((match.Flags & FnMatchPattern.Flag.MatchAll) != 0)
        {
            return (match.Flags & FnMatchPattern.Flag.Negative) != 0 ? 0 : 1;
        }

        // 1. Try exact string compare via the strcomp slot.
        bool result = ctxt.Strcomp(match.Pattern, path) == 0;

        // 2. If no match and glob is enabled, try wildmatch.
        if (!result && ctxt.WildmatchFlags >= 0)
        {
            var wmFlags = (WildMatchFlags)ctxt.WildmatchFlags;
            result = WildMatch.IsMatch(match.Pattern.Span, path.Span, wmFlags);
        }

        // 3. If still no match and pattern has no wildcards, try exact dirname
        //    prefix match (path starts with "pattern/"). Ports
        //    pathspec.c:152-156 (strncomp + path[length] == '/').
        if (!result && (match.Flags & FnMatchPattern.Flag.HasWild) == 0)
        {
            if (path.Length > match.Pattern.Length &&
                ctxt.Strncomp(path, match.Pattern, match.Pattern.Length) == 0 &&
                path.Span[match.Pattern.Length] == (byte)'/')
            {
                result = true;
            }
        }

        // 4. Negative pattern: check for exact match of filename with leading '!'.
        //    Ports pathspec.c:161-166 — C requires only that
        //    path[match->length + 1] is NUL (exact name) or '/'; a strict `>`
        //    guard would make the exact-name branch dead.
        if (!result && (match.Flags & FnMatchPattern.Flag.Negative) != 0)
        {
            ReadOnlySpan<byte> pathSpan = path.Span;
            if (pathSpan.Length > 0 && pathSpan[0] == (byte)'!' &&
                pathSpan.Length >= match.Pattern.Length + 1 &&
                ctxt.Strncomp(path.Slice(1), match.Pattern, match.Pattern.Length) == 0 &&
                (pathSpan.Length == match.Pattern.Length + 1 ||
                 pathSpan[match.Pattern.Length + 1] == (byte)'/'))
            {
                return 1;
            }
        }

        if (result)
        {
            return (match.Flags & FnMatchPattern.Flag.Negative) != 0 ? 0 : 1;
        }

        return -1;
    }

    /// <summary>
    /// Computes the common non-wildcard prefix across all patterns. Matches
    /// <c>git_pathspec_prefix</c> (<c>pathspec.c:21-46</c>).
    /// </summary>
    private static GitPath ComputePrefix(GitPath[] patterns)
    {
        if (patterns.Length == 0)
        {
            return default;
        }

        // Find common prefix across all patterns (byte-wise).
        ReadOnlySpan<byte> prefix = patterns[0].Span;
        for (int i = 1; i < patterns.Length; i++)
        {
            ReadOnlySpan<byte> p = patterns[i].Span;
            int len = 0;
            int minLen = Math.Min(prefix.Length, p.Length);
            while (len < minLen && prefix[len] == p[len])
            {
                len++;
            }

            prefix = prefix[..len];
            if (prefix.IsEmpty)
            {
                return default;
            }
        }

        // Truncate at first unescaped wildcard.
        int end = 0;
        for (; end < prefix.Length; end++)
        {
            if (IsWildcard(prefix[end]) && (end == 0 || prefix[end - 1] != (byte)'\\'))
            {
                break;
            }
        }

        prefix = prefix[..end];
        if (prefix.IsEmpty)
        {
            return default;
        }

        // Unescape backslashes (git_str_unescape, pathspec.c:43).
        byte[] unescaped = UnescapeBackslashes(prefix);
        return GitPath.FromUtf8Bytes(unescaped);
    }

    private static byte[] UnescapeBackslashes(ReadOnlySpan<byte> span)
    {
        bool hasBackslash = false;
        foreach (byte b in span)
        {
            if (b == (byte)'\\')
            {
                hasBackslash = true;
                break;
            }
        }

        if (!hasBackslash)
        {
            return span.ToArray();
        }

        byte[] result = new byte[span.Length];
        int pos = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\\' && i + 1 < span.Length)
            {
                result[pos++] = span[i + 1];
                i++;
            }
            else
            {
                result[pos++] = span[i];
            }
        }

        return result[..pos];
    }

    private static bool IsWildcard(byte c) => c is (byte)'*' or (byte)'?' or (byte)'[';

    private static bool IsEmptyPathspec(GitPath[] patterns)
    {
        if (patterns is null || patterns.Length == 0)
        {
            return true;
        }

        foreach (GitPath p in patterns)
        {
            if (!p.IsEmpty)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEmptyPathspec(string[] patterns)
    {
        if (patterns is null || patterns.Length == 0)
        {
            return true;
        }

        foreach (string s in patterns)
        {
            if (!string.IsNullOrEmpty(s))
            {
                return false;
            }
        }

        return true;
    }
}
