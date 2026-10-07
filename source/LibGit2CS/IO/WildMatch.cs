// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

/*
 * Copyright (C) the libgit2 contributors. All rights reserved.
 * This file is part of libgit2, distributed under the GNU GPL v2 with
 * a Linking Exception. For full terms see the included COPYING file.
 * Do shell-style pattern matching for ?, \, [], and * characters.
 * It is 8bit clean.
 * Written by Rich $alz, mirror!rs, Wed Nov 26 19:03:17 EST 1986.
 * Rich $alz is now <rsalz@bbn.com>.
 * Modified by Wayne Davison to special-case '/' matching, to make '**'
 * work differently than '*', and to fix the character-class code.
 * Imported from git.git.
 */

using System.Collections.Immutable;

namespace LibGit2CS.IO;

/// <summary>
/// Shell-style pattern matching for <c>?</c>, <c>\</c>, <c>[]</c>, and <c>*</c>.
/// Managed port of the read-side subset of libgit2's <c>util/wildmatch.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is a faithful port of the full <c>util/wildmatch.c</c>
/// (<c>dowild()</c> function). libgit2's wildmatch only defines
/// <see cref="WildMatchFlags.Pathname"/> (<c>WM_PATHNAME</c>) and
/// <see cref="WildMatchFlags.CaseInsensitive"/> (<c>WM_CASEFOLD</c>); there is
/// no <c>WM_PERIOD</c> or <c>WM_LEADING_DIR</c> in libgit2 (those exist in
/// git.git's wildmatch but were not imported). The <c>.gitignore</c> dot-file
/// and directory semantics are handled by <c>ignore.c</c> at the
/// call site, not by the wildmatch function itself.
/// </para>
/// <para>
/// Character classes (<c>[abc]</c>, <c>[a-z]</c>, <c>[[:alpha:]]</c>) are
/// included because real-world <c>gitdir:</c> patterns may use them.
/// </para>
/// </remarks>
internal static class WildMatch
{
    private const char NegateClass = '!';
    private const char NegateClass2 = '^';

    /// <summary>
    /// Matches <paramref name="pattern"/> against <paramref name="text"/>.
    /// </summary>
    /// <param name="pattern">The glob pattern.</param>
    /// <param name="text">The text to test.</param>
    /// <param name="flags">Combination of <see cref="WildMatchFlags"/> values.</param>
    /// <returns><c>true</c> if <paramref name="text"/> matches <paramref name="pattern"/>.</returns>
    public static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, WildMatchFlags flags = WildMatchFlags.None)
        => DoWild(pattern, 0, text, 0, flags) == WildMatchResult.Match;

    private static WildMatchResult DoWild(ReadOnlySpan<char> pattern, int pStart, ReadOnlySpan<char> text, int tStart, WildMatchFlags flags)
    {
        bool caseFold = (flags & WildMatchFlags.CaseInsensitive) != 0;
        bool pathname = (flags & WildMatchFlags.Pathname) != 0;

        int p = pStart;
        int t = tStart;

        while (p < pattern.Length)
        {
            char pCh = pattern[p];

            if (t >= text.Length && pCh != '*')
            {
                return WildMatchResult.AbortAll;
            }

            char tCh = t < text.Length ? text[t] : '\0';
            if (caseFold)
            {
                // C folds ASCII-only (git__tolower via sane_ctype, wildmatch.c:111-114).
                tCh = AsciiFold(tCh);
                pCh = AsciiFold(pCh);
            }

            switch (pCh)
            {
                case '\\':
                    p++;
                    if (p >= pattern.Length)
                    {
                        return WildMatchResult.NoMatch;
                    }

                    // C does NOT fold the escaped character (wildmatch.c:112-115
                    // falls through to the default case with the raw p_ch).
                    pCh = pattern[p];

                    if (tCh != pCh)
                    {
                        return WildMatchResult.NoMatch;
                    }

                    t++;
                    p++;
                    continue;

                case '?':
                    if (pathname && tCh == '/')
                    {
                        return WildMatchResult.NoMatch;
                    }

                    t++;
                    p++;
                    continue;

                case '*':
                    {
                        p++;
                        bool matchSlash = !pathname;
                        if (p < pattern.Length && pattern[p] == '*')
                        {
                            int prevP = p - 2;
                            while (p < pattern.Length && pattern[p] == '*')
                            {
                                p++;
                            }

                            if (!pathname)
                            {
                                matchSlash = true;
                            }
                            else if (prevP < pStart || pattern[prevP] == '/')
                            {
                                bool atEnd = p >= pattern.Length;
                                bool nextIsSlash = !atEnd && pattern[p] == '/';
                                bool nextIsEscapedSlash = p + 1 < pattern.Length
                                    && pattern[p] == '\\' && pattern[p + 1] == '/';

                                if (nextIsSlash)
                                {
                                    WildMatchResult sub = DoWild(pattern, p + 1, text, t, flags);
                                    if (sub == WildMatchResult.Match)
                                    {
                                        return WildMatchResult.Match;
                                    }
                                }

                                if (atEnd || nextIsSlash || nextIsEscapedSlash)
                                {
                                    matchSlash = true;
                                }
                                else
                                {
                                    matchSlash = false;
                                }
                            }
                            else
                            {
                                matchSlash = false;
                            }
                        }

                        if (p >= pattern.Length)
                        {
                            if (!matchSlash)
                            {
                                for (int i = t; i < text.Length; i++)
                                {
                                    if (text[i] == '/')
                                    {
                                        return WildMatchResult.NoMatch;
                                    }
                                }
                            }

                            return WildMatchResult.Match;
                        }

                        if (!matchSlash && pattern[p] == '/')
                        {
                            int slash = -1;
                            for (int i = t; i < text.Length; i++)
                            {
                                if (text[i] == '/')
                                {
                                    slash = i;
                                    break;
                                }
                            }

                            if (slash < 0)
                            {
                                return WildMatchResult.NoMatch;
                            }

                            // The slash is consumed by the loop's t++/p++ below.
                            t = slash;
                            p++;
                            t++;
                            continue;
                        }

                        // Greedy match with backtracking via recursion.
                        while (true)
                        {
                            if (t >= text.Length)
                            {
                                break;
                            }

                            // Fast-forward: when '*' is followed by a literal, skip
                            // ahead in text to the next occurrence of that literal.
                            if (!IsGlobSpecial(pattern[p]))
                            {
                                pCh = caseFold ? AsciiFold(pattern[p]) : pattern[p];

                                while (t < text.Length && (matchSlash || text[t] != '/'))
                                {
                                    if ((caseFold ? AsciiFold(text[t]) : text[t]) == pCh)
                                    {
                                        break;
                                    }

                                    t++;
                                }

                                if (t >= text.Length || (!matchSlash && text[t] == '/'))
                                {
                                    if (!(t < text.Length && text[t] == pCh))
                                    {
                                        return WildMatchResult.NoMatch;
                                    }
                                }
                            }

                            WildMatchResult matched = DoWild(pattern, p, text, t, flags);
                            if (matched != WildMatchResult.NoMatch)
                            {
                                if (!matchSlash || matched != WildMatchResult.AbortToStarStar)
                                {
                                    return matched;
                                }
                            }
                            else if (!matchSlash && text[t] == '/')
                            {
                                return WildMatchResult.AbortToStarStar;
                            }

                            t++;
                        }

                        return WildMatchResult.AbortAll;
                    }

                case '[':
                    {
                        p++;
                        if (p >= pattern.Length)
                        {
                            return WildMatchResult.AbortAll;
                        }

                        pCh = pattern[p];
                        if (pCh == NegateClass2)
                        {
                            pCh = NegateClass;
                        }

                        bool negated = pCh == NegateClass;
                        if (negated)
                        {
                            p++;
                            if (p >= pattern.Length)
                            {
                                return WildMatchResult.AbortAll;
                            }

                            pCh = pattern[p];
                        }

                        char prevCh = default;
                        bool matched = false;
                        do
                        {
                            if (pCh == '\\')
                            {
                                p++;
                                if (p >= pattern.Length)
                                {
                                    return WildMatchResult.AbortAll;
                                }

                                pCh = pattern[p];
                                if (tCh == pCh)
                                {
                                    matched = true;
                                }
                            }
                            else if (pCh == '-' && prevCh != default && p + 1 < pattern.Length && pattern[p + 1] != ']')
                            {
                                p++;
                                pCh = pattern[p];
                                if (pCh == '\\')
                                {
                                    p++;
                                    if (p >= pattern.Length)
                                    {
                                        return WildMatchResult.AbortAll;
                                    }

                                    pCh = pattern[p];
                                }

                                if (tCh <= pCh && tCh >= prevCh)
                                {
                                    matched = true;
                                }
                                else if (caseFold && tCh is >= 'a' and <= 'z')
                                {
                                    // C: ISLOWER(t_ch) → toupper(t_ch) (ASCII-only).
                                    char tChUpper = (char)(tCh - 32);
                                    if (tChUpper <= pCh && tChUpper >= prevCh)
                                    {
                                        matched = true;
                                    }
                                }

                                pCh = default;
                            }
                            else if (pCh == '[' && p + 1 < pattern.Length && pattern[p + 1] == ':')
                            {
                                int s = p + 2;
                                int end = s;
                                while (end < pattern.Length && pattern[end] != ']')
                                {
                                    end++;
                                }

                                if (end >= pattern.Length)
                                {
                                    return WildMatchResult.AbortAll;
                                }

                                if (end - 1 < s || pattern[end - 1] != ':')
                                {
                                    // Not a real [:class:] — treat '[' literally.
                                    // C's `continue` lands on the do-while condition
                                    // `(prev_ch = p_ch, (p_ch = *++p) != ']')` which
                                    // ADVANCES p (wildmatch.c:281-289), so the
                                    // literal-'[' handling continues scanning ':'
                                    // and every inner char as class members and the
                                    // first ']' only terminates the class. The old
                                    // `p = end` skipped straight past them (and a
                                    // bare `continue` would jump to the C# while
                                    // condition without moving p and loop forever —
                                    // the same trap as in the byte matcher).
                                    // Mirror the condition-side advance explicitly.
                                    p = s - 2;
                                    pCh = '[';
                                    if (tCh == pCh)
                                    {
                                        matched = true;
                                    }

                                    prevCh = pCh;
                                    p++;
                                    if (p >= pattern.Length)
                                    {
                                        return WildMatchResult.AbortAll;
                                    }

                                    pCh = pattern[p];
                                    continue;
                                }
                                else
                                {
                                    int classLen = end - 1 - s;
                                    if (PosixClassMatches(tCh, classLen, pattern, s, caseFold))
                                    {
                                        matched = true;
                                    }

                                    pCh = default;
                                    p = end;
                                }
                            }
                            else if (tCh == pCh)
                            {
                                matched = true;
                            }

                            prevCh = pCh;
                            p++;
                            if (p >= pattern.Length)
                            {
                                return WildMatchResult.AbortAll;
                            }

                            pCh = pattern[p];
                        }
                        while (pCh != ']');

                        if (matched == negated || (pathname && tCh == '/'))
                        {
                            return WildMatchResult.NoMatch;
                        }

                        t++;
                        p++;
                        continue;
                    }

                default:
                    if (tCh != pCh)
                    {
                        return WildMatchResult.NoMatch;
                    }

                    t++;
                    p++;
                    continue;
            }
        }

        return t < text.Length ? WildMatchResult.NoMatch : WildMatchResult.Match;
    }

    // POSIX [:class:] test on the (already ASCII-folded) char. Ports the
    // CC_EQ ladder in dowild (wildmatch.c:262-299) with the sane_ctype
    // classes: non-ASCII chars (bytes >= 0x80) are in NO class. The "upper"
    // class also accepts ISLOWER(t_ch) under WM_CASEFOLD (wildmatch.c:295-296).
    private static bool PosixClassMatches(char tCh, int classLen, ReadOnlySpan<char> pattern, int classStart, bool caseFold)
    {
        if (tCh > 0x7F)
        {
            return false;
        }

        byte c = (byte)tCh;
        return classLen switch
        {
            5 when IsClass(pattern, classStart, "alnum") => IsAlphaByte(c) || IsDigitByte(c),
            5 when IsClass(pattern, classStart, "alpha") => IsAlphaByte(c),
            5 when IsClass(pattern, classStart, "blank") => c is (byte)' ' or (byte)'\t',
            5 when IsClass(pattern, classStart, "cntrl") => IsCntrlByte(c),
            5 when IsClass(pattern, classStart, "digit") => IsDigitByte(c),
            5 when IsClass(pattern, classStart, "graph") => c is > 32 and < 127,
            5 when IsClass(pattern, classStart, "lower") => IsLowerByte(c),
            5 when IsClass(pattern, classStart, "print") => c is >= 32 and < 127,
            // libc ispunct in the C locale: printable, non-alnum, non-space — INCLUDING the glob specials * ? [ \ (which sane_ctype marks G, not U|R|P).
            // wildmatch.c:91-92 uses ISPUNCT (ctype.h), not sane_ctype.
            5 when IsClass(pattern, classStart, "punct") => c is > 32 and < 127 && !IsAlphaByte(c) && !IsDigitByte(c),
            // libc isspace: \t \n \v \f \r and space — includes VT (0x0B) and FF (0x0C), which sane_ctype marks CNTRL-only.
            5 when IsClass(pattern, classStart, "space") => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r',
            5 when IsClass(pattern, classStart, "upper") => IsUpperByte(c) || (caseFold && IsLowerByte(c)),
            6 when IsClass(pattern, classStart, "xdigit") => IsXDigitByte(c),
            _ => false,
        };
    }

    private static char AsciiFold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    private static bool IsClass(ReadOnlySpan<char> pattern, int start, ReadOnlySpan<char> name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (pattern[start + i] != name[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsGlobSpecial(char c) => c is '*' or '?' or '[' or '\\';

    // ----- Byte-based matcher -----

    // sane_ctype table ported 1:1 from src/util/wildmatch.c:42-52.
    // Entries 128..255 are 0 (the C table is 128 entries with implicit 0
    // for the high range — see the comment at wildmatch.c:51). Non-ASCII
    // bytes are NOT classified as alpha/digit/space/etc., matching libgit2.
    // ImmutableArray<byte> (not byte[]) so the static-state convention test
    // (which flags mutable T[] statics) passes — see StaticStateConventionTests.
    private const byte X = 0x40; // GIT_CNTRL
    private const byte S = 0x01; // GIT_SPACE
    private const byte Z = 0x41; // GIT_CNTRL | GIT_SPACE
    private const byte P = 0x20; // GIT_PATHSPEC_MAGIC
    private const byte R = 0x10; // GIT_REGEX_SPECIAL
    private const byte D = 0x02; // GIT_DIGIT
    private const byte A = 0x04; // GIT_ALPHA
    private const byte G = 0x08; // GIT_GLOB_SPECIAL
    private const byte U = 0x80; // GIT_PUNCT

    private static readonly ImmutableArray<byte> s_saneCtype =
    [
        X, X, X, X, X, X, X, X, X, Z, Z, X, X, Z, X, X, //   0.. 15
        X, X, X, X, X, X, X, X, X, X, X, X, X, X, X, X, //  16.. 31
        S, P, P, P, R, P, P, P, R, R, G, R, P, P, R, P, //  32.. 47
        D, D, D, D, D, D, D, D, D, D, P, P, P, P, P, G, //  48.. 63
        P, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, //  64.. 79
        A, A, A, A, A, A, A, A, A, A, A, G, G, U, R, P, //  80.. 95
        P, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, //  96..111
        A, A, A, A, A, A, A, A, A, A, A, R, R, U, P, X, // 112..127
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 128..143
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 144..159
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 160..175
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 176..191
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 192..207
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 208..223
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 224..239
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 240..255
    ];

    private const byte MaskAlpha = A;
    private const byte MaskDigit = D;
    private const byte MaskCntrl = X;
    private const byte MaskGlob = G;

    private static bool IsAlphaByte(byte c) => (s_saneCtype[c] & MaskAlpha) != 0;
    private static bool IsDigitByte(byte c) => (s_saneCtype[c] & MaskDigit) != 0;
    private static bool IsCntrlByte(byte c) => (s_saneCtype[c] & MaskCntrl) != 0;
    private static bool IsUpperByte(byte c) => c is >= (byte)'A' and <= (byte)'Z';
    private static bool IsLowerByte(byte c) => c is >= (byte)'a' and <= (byte)'z';
    private static bool IsGlobSpecialByte(byte c) => (s_saneCtype[c] & MaskGlob) != 0;

    /// <summary> Byte-based glob match. Ports <c>dowild</c> (<c>src/util/wildmatch.c:97-314</c>) operating on <see cref="ReadOnlySpan{T}"/> (<c>byte</c>) with
    /// ASCII-only case fold (non-ASCII bytes pass through unchanged — the <c>OrdinalIgnoreCase</c> -> ASCII-fold parity fix applied to
    /// wildmatch). Used by the pathspec/attr/ignore subsystems so non-UTF-8 paths round-trip byte-exact. </summary>
    public static bool IsMatch(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> text, WildMatchFlags flags = WildMatchFlags.None)
        => DoWildBytes(pattern, 0, text, 0, flags) == WildMatchResult.Match;

    private static WildMatchResult DoWildBytes(ReadOnlySpan<byte> pattern, int pStart, ReadOnlySpan<byte> text, int tStart, WildMatchFlags flags)
    {
        bool caseFold = (flags & WildMatchFlags.CaseInsensitive) != 0;
        bool pathname = (flags & WildMatchFlags.Pathname) != 0;

        int p = pStart;
        int t = tStart;

        while (p < pattern.Length)
        {
            byte pCh = pattern[p];

            if (t >= text.Length && pCh != (byte)'*')
            {
                return WildMatchResult.AbortAll;
            }

            byte tCh = t < text.Length ? text[t] : (byte)0;
            if (caseFold)
            {
                if (IsUpperByte(tCh))
                {
                    tCh = (byte)(tCh + 32);
                }

                if (IsUpperByte(pCh))
                {
                    pCh = (byte)(pCh + 32);
                }
            }

            switch (pCh)
            {
                case (byte)'\\':
                    p++;
                    if (p >= pattern.Length)
                    {
                        return WildMatchResult.NoMatch;
                    }

                    // C does NOT fold the escaped character (wildmatch.c:112-115
                    // falls through to the default case with the raw p_ch).
                    pCh = pattern[p];

                    if (tCh != pCh)
                    {
                        return WildMatchResult.NoMatch;
                    }

                    t++;
                    p++;
                    continue;

                case (byte)'?':
                    if (pathname && tCh == (byte)'/')
                    {
                        return WildMatchResult.NoMatch;
                    }

                    t++;
                    p++;
                    continue;

                case (byte)'*':
                    {
                        p++;
                        bool matchSlash = !pathname;
                        if (p < pattern.Length && pattern[p] == (byte)'*')
                        {
                            int prevP = p - 2;
                            while (p < pattern.Length && pattern[p] == (byte)'*')
                            {
                                p++;
                            }

                            if (!pathname)
                            {
                                matchSlash = true;
                            }
                            else if (prevP < pStart || pattern[prevP] == (byte)'/')
                            {
                                bool atEnd = p >= pattern.Length;
                                bool nextIsSlash = !atEnd && pattern[p] == (byte)'/';
                                bool nextIsEscapedSlash = p + 1 < pattern.Length
                                    && pattern[p] == (byte)'\\' && pattern[p + 1] == (byte)'/';

                                if (nextIsSlash)
                                {
                                    WildMatchResult sub = DoWildBytes(pattern, p + 1, text, t, flags);
                                    if (sub == WildMatchResult.Match)
                                    {
                                        return WildMatchResult.Match;
                                    }
                                }

                                if (atEnd || nextIsSlash || nextIsEscapedSlash)
                                {
                                    matchSlash = true;
                                }
                                else
                                {
                                    matchSlash = false;
                                }
                            }
                            else
                            {
                                matchSlash = false;
                            }
                        }

                        if (p >= pattern.Length)
                        {
                            if (!matchSlash)
                            {
                                for (int i = t; i < text.Length; i++)
                                {
                                    if (text[i] == (byte)'/')
                                    {
                                        return WildMatchResult.NoMatch;
                                    }
                                }
                            }

                            return WildMatchResult.Match;
                        }

                        if (!matchSlash && pattern[p] == (byte)'/')
                        {
                            int slash = -1;
                            for (int i = t; i < text.Length; i++)
                            {
                                if (text[i] == (byte)'/')
                                {
                                    slash = i;
                                    break;
                                }
                            }

                            if (slash < 0)
                            {
                                return WildMatchResult.NoMatch;
                            }

                            // The slash is consumed by the loop's t++/p++ below.
                            t = slash;
                            p++;
                            t++;
                            continue;
                        }

                        // Greedy match with backtracking via recursion.
                        while (true)
                        {
                            if (t >= text.Length)
                            {
                                break;
                            }

                            // Fast-forward: when '*' is followed by a literal, skip
                            // ahead in text to the next occurrence of that literal.
                            if (!IsGlobSpecialByte(pattern[p]))
                            {
                                pCh = pattern[p];
                                if (caseFold && IsUpperByte(pCh))
                                {
                                    pCh = (byte)(pCh + 32);
                                }

                                while (t < text.Length && (matchSlash || text[t] != (byte)'/'))
                                {
                                    byte c = text[t];
                                    if (caseFold && IsUpperByte(c))
                                    {
                                        c = (byte)(c + 32);
                                    }

                                    if (c == pCh)
                                    {
                                        break;
                                    }

                                    t++;
                                }

                                if (t >= text.Length || (!matchSlash && text[t] == (byte)'/'))
                                {
                                    if (!(t < text.Length && text[t] == pCh))
                                    {
                                        return WildMatchResult.NoMatch;
                                    }
                                }
                            }

                            WildMatchResult matched = DoWildBytes(pattern, p, text, t, flags);
                            if (matched != WildMatchResult.NoMatch)
                            {
                                if (!matchSlash || matched != WildMatchResult.AbortToStarStar)
                                {
                                    return matched;
                                }
                            }
                            else if (!matchSlash && text[t] == (byte)'/')
                            {
                                return WildMatchResult.AbortToStarStar;
                            }

                            t++;
                        }

                        return WildMatchResult.AbortAll;
                    }

                case (byte)'[':
                    {
                        p++;
                        if (p >= pattern.Length)
                        {
                            return WildMatchResult.AbortAll;
                        }

                        pCh = pattern[p];
                        if (pCh == (byte)'^')
                        {
                            pCh = (byte)'!';
                        }

                        bool negated = pCh == (byte)'!';
                        if (negated)
                        {
                            p++;
                            if (p >= pattern.Length)
                            {
                                return WildMatchResult.AbortAll;
                            }

                            pCh = pattern[p];
                        }

                        byte prevCh = 0;
                        bool matched = false;
                        do
                        {
                            if (pCh == 0)
                            {
                                return WildMatchResult.AbortAll;
                            }

                            if (pCh == (byte)'\\')
                            {
                                p++;
                                if (p >= pattern.Length)
                                {
                                    return WildMatchResult.AbortAll;
                                }

                                pCh = pattern[p];
                                if (tCh == pCh)
                                {
                                    matched = true;
                                }
                            }
                            else if (pCh == (byte)'-' && prevCh != 0 && p + 1 < pattern.Length && pattern[p + 1] != (byte)']')
                            {
                                p++;
                                pCh = pattern[p];
                                if (pCh == (byte)'\\')
                                {
                                    p++;
                                    if (p >= pattern.Length)
                                    {
                                        return WildMatchResult.AbortAll;
                                    }

                                    pCh = pattern[p];
                                }

                                if (tCh <= pCh && tCh >= prevCh)
                                {
                                    matched = true;
                                }
                                else if (caseFold && IsLowerByte(tCh))
                                {
                                    byte tChUpper = (byte)(tCh - 32);
                                    if (tChUpper <= pCh && tChUpper >= prevCh)
                                    {
                                        matched = true;
                                    }
                                }

                                pCh = 0; // makes prev_ch get set to 0
                            }
                            else if (pCh == (byte)'[' && p + 1 < pattern.Length && pattern[p + 1] == (byte)':')
                            {
                                int s = p + 2;
                                int end = s;
                                while (end < pattern.Length && pattern[end] != (byte)']')
                                {
                                    end++;
                                }

                                if (end >= pattern.Length)
                                {
                                    return WildMatchResult.AbortAll;
                                }

                                int classLen = end - 1 - s;
                                if (classLen < 0 || pattern[end - 1] != (byte)':')
                                {
                                    // Not a real [:class:] — treat '[' literally.
                                    // C's `continue` lands on the do-while condition
                                    // `(prev_ch = p_ch, (p_ch = *++p) != ']')` which
                                    // ADVANCES p (wildmatch.c:281-289), so the
                                    // literal-'[' handling cannot re-enter this
                                    // branch. Mirror that advance explicitly —
                                    // a bare `continue` would jump to the C# while
                                    // condition without moving p and loop forever.
                                    p = s - 2;
                                    pCh = (byte)'[';
                                    if (tCh == pCh)
                                    {
                                        matched = true;
                                    }

                                    prevCh = pCh;
                                    p++;
                                    if (p >= pattern.Length)
                                    {
                                        return WildMatchResult.AbortAll;
                                    }

                                    pCh = pattern[p];
                                    continue;
                                }

                                if (PosixClassMatchesByte(tCh, classLen, pattern, s, caseFold))
                                {
                                    matched = true;
                                }

                                pCh = 0; // makes prev_ch get set to 0
                                p = end;
                            }
                            else if (tCh == pCh)
                            {
                                matched = true;
                            }

                            prevCh = pCh;
                            p++;
                            if (p >= pattern.Length)
                            {
                                return WildMatchResult.AbortAll;
                            }

                            pCh = pattern[p];
                        }
                        while (pCh != (byte)']');

                        if (matched == negated || (pathname && tCh == (byte)'/'))
                        {
                            return WildMatchResult.NoMatch;
                        }

                        t++;
                        p++;
                        continue;
                    }

                default:
                    if (tCh != pCh)
                    {
                        return WildMatchResult.NoMatch;
                    }

                    t++;
                    p++;
                    continue;
            }
        }

        return t < text.Length ? WildMatchResult.NoMatch : WildMatchResult.Match;
    }

    // POSIX [:class:] test on the (already ASCII-folded) byte. Ports the
    // CC_EQ ladder in dowild (wildmatch.c:262-299). ASCII-only: non-ASCII
    // bytes are NOT in any class (the sane_ctype table maps 128..255 to 0).
    // The "upper" class also accepts ISLOWER(t_ch) under WM_CASEFOLD
    // (wildmatch.c:295-296).
    private static bool PosixClassMatchesByte(byte tCh, int classLen, ReadOnlySpan<byte> pattern, int classStart, bool caseFold)
    {
        byte c = tCh;
        return classLen switch
        {
            5 when IsClassBytes(pattern, classStart, "alnum"u8) => IsAlphaByte(c) || IsDigitByte(c),
            5 when IsClassBytes(pattern, classStart, "alpha"u8) => IsAlphaByte(c),
            5 when IsClassBytes(pattern, classStart, "blank"u8) => c is (byte)' ' or (byte)'\t',
            5 when IsClassBytes(pattern, classStart, "cntrl"u8) => IsCntrlByte(c),
            5 when IsClassBytes(pattern, classStart, "digit"u8) => IsDigitByte(c),
            5 when IsClassBytes(pattern, classStart, "graph"u8) => c is > 32 and < 127,
            5 when IsClassBytes(pattern, classStart, "lower"u8) => IsLowerByte(c),
            5 when IsClassBytes(pattern, classStart, "print"u8) => c is < 127 and >= 32,
            // libc ispunct / isspace (wildmatch.c:91-92), NOT sane_ctype — see the char-matcher counterpart..
            5 when IsClassBytes(pattern, classStart, "punct"u8) => c is > 32 and < 127 && !IsAlphaByte(c) && !IsDigitByte(c),
            5 when IsClassBytes(pattern, classStart, "space"u8) => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r',
            5 when IsClassBytes(pattern, classStart, "upper"u8) => IsUpperByte(c) || (caseFold && IsLowerByte(c)),
            6 when IsClassBytes(pattern, classStart, "xdigit"u8) => IsXDigitByte(c),
            _ => false,
        };
    }

    private static bool IsXDigitByte(byte c)
        => c is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static bool IsClassBytes(ReadOnlySpan<byte> pattern, int start, ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (pattern[start + i] != name[i])
            {
                return false;
            }
        }

        return true;
    }

    private enum WildMatchResult
    {
        Match = 0,
        NoMatch = 1,
        AbortAll = 2,
        AbortToStarStar = 3,
    }
}
