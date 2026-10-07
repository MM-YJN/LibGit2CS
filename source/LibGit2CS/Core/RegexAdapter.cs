// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace LibGit2CS.Core;

/// <summary> Regular expression adapter wrapping <see cref="Regex"/> behind libgit2's <c>git_regexp</c> API surface. Managed port of <c>src/util/regexp.c</c> /
/// <c>regexp.h</c>. </summary> <remarks> <para> libgit2 supports multiple regex backends (PCRE, PCRE2, POSIX regcomp). This adapter replaces all of them
/// with .NET's <c>Regex</c> engine, exposing the same <c>compile</c> / <c>match</c> / <c>search</c> / <c>dispose</c> API. </para> <para> First consumer:
/// <c>diff_driver.c</c> funcname patterns. Also used by <c>revparse.c</c>
/// <c>^{/regex}</c>. </para> <para> <b>Byte domain.</b> libgit2's
/// <c>git_regexp</c> runs over raw bytes (<c>char*</c>): <c>.</c>, character classes, anchors and match offsets all count bytes. This adapter models that
/// domain by mapping input bytes to chars through the Latin1 bijection — .NET's only lossless byte↔char mapping (0x00–0xFF ↔ U+0000–U+00FF), one char per byte —
/// so <c>.</c> matches exactly one byte, classes count bytes, and the offsets returned by <see cref="Search(ReadOnlySpan{byte}, Span{RegexMatch})"/> are
/// <b>byte offsets by identity</b> (no char↔byte back-mapping). This is the one sanctioned <see cref="Encoding.Latin1"/> use in the codebase;
/// the source-scanning convention test allowlists exactly this file.
/// </para> <para> <b>Pattern domain.</b> <see cref="Compile(string, RegexFlags)"/> UTF-8-encodes the pattern string into the
/// byte domain, so a literal <c>é</c> becomes bytes <c>C3 A9</c> and matches UTF-8 content bytes <c>C3 A9</c> — exactly as C's pattern bytes would. Regex
/// metacharacters are all ≤ U+007F, so ASCII patterns are byte-identical through the encode. Two documented divergences: (1) a <c>\xHH</c>-style
/// raw-byte-escape reading of the pattern does not hold — pattern chars are UTF-8-encoded, never "raw byte HH"; (2) <c>RegexOptions.IgnoreCase</c> folds a
/// different domain than C's locale-ASCII-only fold (pre-existing engine divergence). </para> <para> User-precompiled <see cref="Regex"/> instances (the <see
/// cref="RegexAdapter(Regex)"/> constructor) are <b>not</b> pattern-transformed — they match against the bijection-decoded input as-is, which is exactly the
/// config-multivar behavior. </para> </remarks>
internal sealed class RegexAdapter : IDisposable
{
    /// <summary>
    /// Per-match time budget.
    /// .NET's default is <see cref="Regex.InfiniteMatchTimeout"/> — a
    /// pathological pattern (e.g. <c>(a+)+$</c>) against attacker-influenced
    /// input causes catastrophic backtracking. Upstream PCRE/PCRE2 enforce a
    /// default match-step limit (src/util/regexp.c), so each match here is
    /// bounded by this timeout. Immutable — safe as a static.
    /// </summary>
    private static readonly TimeSpan s_matchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Byte inputs at or below this length decode on the stack; longer inputs
    /// rent from <see cref="ArrayPool{T}"/>. Funcname lines, config values and
    /// commit messages are short, so the common path is allocation-free.
    /// </summary>
    private const int StackallocThreshold = 256;

    private Regex? _regex;

    internal RegexAdapter(Regex regex)
    {
        _regex = regex;
    }

    /// <summary> Compiles a regular expression pattern. Matches <c>git_regexp_compile</c>. </summary> <param name="pattern">The regex pattern.</param> <param
    /// name="flags">Compile flags (currently only <see cref="RegexFlags.IgnoreCase"/>).</param> <returns>A compiled <see cref="RegexAdapter"/>.</returns>
    /// <exception cref="ArgumentException">The pattern is invalid.</exception> <remarks> The pattern string is UTF-8-encoded into the byte domain: a literal
    /// non-ASCII char becomes its UTF-8 bytes in the bijection domain, matching UTF-8 content bytes exactly as C's pattern bytes would. ASCII patterns (all
    /// metacharacters, all builtin userdiff patterns) are unchanged by the encode. </remarks>
    public static RegexAdapter Compile(string pattern, RegexFlags flags = RegexFlags.None)
    {
        RegexOptions options = RegexOptions.Compiled;
        if ((flags & RegexFlags.IgnoreCase) != 0)
        {
            options |= RegexOptions.IgnoreCase;
        }

        // the byte-domain contract: UTF-8-encode the pattern into the byte domain, then map the bytes to chars via the Latin1 bijection so the Regex sees one
        // char per pattern byte (C's pattern bytes).
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(pattern));
        int bytesWritten = Encoding.UTF8.GetBytes(pattern, buffer);
        string byteDomainPattern = string.Create(bytesWritten, buffer.AsSpan(0, bytesWritten), (span, state) => Encoding.Latin1.GetChars(state, span));
        ArrayPool<byte>.Shared.Return(buffer);

        try
        {
            var regex = new Regex(byteDomainPattern, options, s_matchTimeout);
            return new RegexAdapter(regex);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid regex pattern '{pattern}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Tests whether the entire string matches. Returns <c>true</c> on match,
    /// <c>false</c> if no match. Matches <c>git_regexp_match</c> (which returns
    /// 0 on match, GIT_ENOTFOUND on no match).
    /// </summary>
    /// <remarks>
    /// String convenience overload: the input is matched as-is (no byte
    /// domain mapping). For ASCII input this is identical to
    /// <see cref="IsMatch(ReadOnlySpan{byte})"/>; chars &gt; U+00FF have no
    /// byte-domain equivalent. The byte overload is the parity surface.
    /// </remarks>
    public bool IsMatch(string input)
    {
        ObjectDisposedException.ThrowIf(_regex is null, this);
        return _regex.IsMatch(input);
    }

    /// <summary>
    /// Tests whether the entire byte input matches. Returns <c>true</c> on
    /// match, <c>false</c> if no match. Matches <c>git_regexp_match</c>.
    /// </summary>
    /// <param name="input">The raw bytes to test (libgit2's <c>char*</c>).</param>
    /// <remarks>
    /// The input is mapped to chars via the Latin1 bijection (one char per
    /// byte), so <c>.</c> and character classes count bytes exactly as C's
    /// regex backends do over the raw bytes.
    /// </remarks>
    public bool IsMatch(ReadOnlySpan<byte> input)
    {
        ObjectDisposedException.ThrowIf(_regex is null, this);

        if (input.Length <= StackallocThreshold)
        {
            Span<char> buffer = stackalloc char[input.Length];
            Encoding.Latin1.GetChars(input, buffer);
            return _regex.IsMatch(buffer);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(input.Length);
        try
        {
            int written = Encoding.Latin1.GetChars(input, rented);
            return _regex.IsMatch(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Searches for the first match in the input string. Populates
    /// <paramref name="matches"/> with capture group offsets. Matches
    /// <c>git_regexp_search</c>.
    /// </summary>
    /// <param name="input">The string to search.</param>
    /// <param name="matches">Buffer to receive match results. Index 0 is the
    /// full match; indices 1..N are capture groups. Unmatched groups have
    /// Start = End = -1.</param>
    /// <returns><c>true</c> if a match was found.</returns>
    /// <remarks>
    /// String convenience overload: offsets are character indices into
    /// <paramref name="input"/>. For ASCII input they equal the byte offsets
    /// of <see cref="Search(ReadOnlySpan{byte}, Span{RegexMatch})"/>.
    /// </remarks>
    public bool Search(string input, Span<RegexMatch> matches)
    {
        ObjectDisposedException.ThrowIf(_regex is null, this);
        return SearchCore(input, matches);
    }

    /// <summary>
    /// Searches for the first match in the raw byte input. Populates
    /// <paramref name="matches"/> with capture group offsets. Matches
    /// <c>git_regexp_search</c>.
    /// </summary>
    /// <param name="input">The raw bytes to search (libgit2's <c>char*</c>).</param>
    /// <param name="matches">Buffer to receive match results. Index 0 is the
    /// full match; indices 1..N are capture groups. Unmatched groups have
    /// Start = End = -1.</param>
    /// <returns><c>true</c> if a match was found.</returns>
    /// <remarks>
    /// The input is mapped to chars via the Latin1 bijection (one char per
    /// byte), so the returned offsets are <b>byte offsets by identity</b> —
    /// they index <paramref name="input"/> directly, with no char↔byte
    /// back-mapping.
    /// </remarks>
    public bool Search(ReadOnlySpan<byte> input, Span<RegexMatch> matches)
    {
        ObjectDisposedException.ThrowIf(_regex is null, this);
        return SearchCore(DecodeBijectionToString(input), matches);
    }

    /// <summary>Releases the compiled regex. Matches <c>git_regexp_dispose</c>.</summary>
    public void Dispose()
    {
        _regex = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Builds a whole-value literal matcher for the byte-domain config APIs.
    /// Escaping happens after the lossless byte-to-character mapping, so neither
    /// invalid UTF-8 nor regex metacharacters change the value being matched.
    /// </summary>
    internal static Regex CompileLiteralBytes(ReadOnlySpan<byte> value)
        => new("\\A" + Regex.Escape(DecodeBijectionToString(value)) + "\\z", RegexOptions.None, s_matchTimeout);

    /// <summary> Tests a user-precompiled <see cref="Regex"/> against raw bytes via the Latin1 bijection (one char per byte). Used by the config multivar
    /// machinery (<c>config_file.c</c>'s <c>git_regexp_match</c> over the parsed value bytes). The <see cref="Regex"/> is <b>not</b> pattern-transformed — it
    /// matches the bijection-decoded input as-is, which is exactly the config-multivar behavior. </summary> <param name="regexp">A user-compiled regex
    /// (e.g. from <see cref="LibGit2CS.Config.GitConfiguration.SetMultiAsync"/>).</param> <param name="bytes">The raw value bytes to
    /// test.</param> <returns><c>true</c> if the regex matches the decoded bytes.</returns>
    internal static bool IsMatchBijection(Regex regexp, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= StackallocThreshold)
        {
            Span<char> buffer = stackalloc char[bytes.Length];
            Encoding.Latin1.GetChars(bytes, buffer);
            return regexp.IsMatch(buffer);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(bytes.Length);
        try
        {
            int written = Encoding.Latin1.GetChars(bytes, rented);
            return regexp.IsMatch(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Maps raw bytes to a string via the Latin1 bijection (one char per
    /// byte). <see cref="Regex.Match(string)"/> has no span overload, so
    /// searches materialize the decoded input; the stack path avoids the
    /// array pool.
    /// </summary>
    private static string DecodeBijectionToString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= StackallocThreshold)
        {
            Span<char> buffer = stackalloc char[bytes.Length];
            Encoding.Latin1.GetChars(bytes, buffer);
            return new string(buffer);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(bytes.Length);
        try
        {
            int written = Encoding.Latin1.GetChars(bytes, rented);
            return new string(rented, 0, written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Shared search core over the bijection-decoded char domain. Offsets in
    /// <paramref name="matches"/> are indices into <paramref name="text"/>.
    /// </summary>
    private bool SearchCore(string text, Span<RegexMatch> matches)
    {
        ObjectDisposedException.ThrowIf(_regex is null, this);
        Match match = _regex.Match(text);
        if (!match.Success)
        {
            for (int i = 0; i < matches.Length; i++)
            {
                matches[i] = new RegexMatch(-1, -1);
            }

            return false;
        }

        // Group 0 = full match, groups 1..N = capture groups.
        for (int i = 0; i < matches.Length; i++)
        {
            Group? group = i <= match.Groups.Count - 1 ? match.Groups[i] : null;
            if (group is { Success: true })
            {
                matches[i] = new RegexMatch(group.Index, group.Index + group.Length);
            }
            else
            {
                // C's
                // git_regexp_search leaves start = end = -1 for groups that
                // did not participate (regexp.c:62-66, 140-144). A default
                // (0,0) would make RegexMatch.IsUnset always false and break
                // consumers like the diff-driver funcname extractor
                // (diff_driver.c:455).
                matches[i] = new RegexMatch(-1, -1);
            }
        }

        return true;
    }
}

/// <summary>
/// A regex match result with start and end offsets. Managed equivalent of
/// libgit2's <c>git_regmatch</c>.
/// </summary>
/// <remarks>
/// <c>Start</c> and <c>End</c> are -1 when the capture group did not participate
/// in the match, matching libgit2's convention. The offsets are byte offsets
/// into the input when produced by <see cref="RegexAdapter.Search(ReadOnlySpan{byte}, Span{RegexMatch})"/>
/// (the bijection is 1 char = 1 byte), and character indices when produced by
/// the string overload.
/// </remarks>
internal readonly record struct RegexMatch(int Start, int End)
{
    /// <summary>True if this group did not participate in the match.</summary>
    public bool IsUnset => Start < 0 || End < 0;
}

/// <summary>
/// Flags for <see cref="RegexAdapter.Compile"/>. Matches libgit2's
/// <c>git_regexp_flags_t</c>.
/// </summary>
[Flags]
internal enum RegexFlags
{
    /// <summary>Default behavior: case-sensitive.</summary>
    None = 0,

    /// <summary>Case-insensitive matching. Matches <c>GIT_REGEXP_ICASE</c>.</summary>
    IgnoreCase = 1,
}
