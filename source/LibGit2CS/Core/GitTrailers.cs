// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Collections.Immutable;

namespace LibGit2CS.Core;

/// <summary>
/// Commit message trailer parsing. Managed port of libgit2's
/// <c>src/libgit2/trailer.c</c>.
/// </summary>
/// <remarks>
/// <see cref="Parse"/> extracts the trailer block (the last paragraph of a
/// commit message, excluding patches/conflicts) and parses individual
/// <c>key: value</c> trailers from it. Matches the heuristic in
/// <c>trailer.c</c> for determining where the trailer block starts and ends.
/// </remarks>
public static class GitTrailers
{
    private const char CommentLineChar = '#';
    private const char TrailerSeparator = ':';

    private static readonly ImmutableArray<string> s_gitGeneratedPrefixes =
    [
        "Signed-off-by: ",
        "(cherry picked from commit ",
    ];

    /// <summary>
    /// Parses trailers from a commit message. Matches
    /// <c>git_message_trailers</c> (trailer.c:297-424).
    /// </summary>
    /// <param name="message">The message to parse.</param>
    /// <returns>The parsed trailers (empty if none found).</returns>
    public static IReadOnlyList<GitMessageTrailer> Parse(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        char[]? trailerBlock = ExtractTrailerBlock(message, out int trailerLen);
        if (trailerBlock is null || trailerLen == 0)
        {
            return [];
        }

        return ParseTrailerBlock(trailerBlock, trailerLen);
    }

    /// <summary>
    /// Extracts the trailer block from the message. Matches
    /// <c>extract_trailer_block</c> (trailer.c:261-279).
    /// </summary>
    private static char[]? ExtractTrailerBlock(string message, out int len)
    {
        int patchStart = FindPatchStart(message);
        int trailerEnd = FindTrailerEnd(message, patchStart);
        int trailerStart = FindTrailerStart(message, trailerEnd);

        int trailerLen = trailerEnd - trailerStart;
        if (trailerLen <= 0)
        {
            len = 0;
            return null;
        }

        char[] buffer = new char[trailerLen];
        message.CopyTo(trailerStart, buffer, 0, trailerLen);
        len = trailerLen;
        return buffer;
    }

    /// <summary>
    /// Finds the start of a patch (line beginning with "---"). Matches
    /// <c>find_patch_start</c> (trailer.c:156-166).
    /// </summary>
    private static int FindPatchStart(string str)
    {
        int pos = 0;
        while (pos < str.Length)
        {
            if (pos + 3 < str.Length && str[pos] == '-' && str[pos + 1] == '-' && str[pos + 2] == '-' && IsSpace(str[pos + 3]))
            {
                return pos;
            }

            pos = NextLine(str, pos);
        }

        return pos;
    }

    /// <summary>
    /// Finds the end of the trailer block, stripping trailing comments and
    /// conflicts. Matches <c>find_trailer_end</c> (trailer.c:256-259) +
    /// <c>ignore_non_trailer</c> (trailer.c:117-150).
    /// </summary>
    private static int FindTrailerEnd(string buf, int len)
    {
        return len - IgnoreNonTrailer(buf, len);
    }

    /// <summary>
    /// Strips trailing comment lines, blank lines, and Conflicts: blocks from
    /// the end of the message. Matches <c>ignore_non_trailer</c>
    /// (trailer.c:117-150).
    /// </summary>
    private static int IgnoreNonTrailer(string buf, int len)
    {
        int boc = 0;
        int bol = 0;
        bool inOldConflictsBlock = false;
        int cutoff = len;

        while (bol < cutoff)
        {
            int next = NextLineFrom(buf, bol, len);

            if (buf[bol] is CommentLineChar or '\n')
            {
                if (boc == 0)
                {
                    boc = bol;
                }
            }
            else if (StartsWith(buf, bol, len, "Conflicts:\n"))
            {
                inOldConflictsBlock = true;
                if (boc == 0)
                {
                    boc = bol;
                }
            }
            else if (inOldConflictsBlock && buf[bol] == '\t')
            {
                // A pathname in the conflicts block.
            }
            else if (boc != 0)
            {
                boc = 0;
                inOldConflictsBlock = false;
            }

            bol = next;
        }

        return boc != 0 ? len - boc : len - cutoff;
    }

    /// <summary>
    /// Finds the start of the trailer block. Matches
    /// <c>find_trailer_start</c> (trailer.c:172-253).
    /// </summary>
    private static int FindTrailerStart(string buf, int len)
    {
        int endOfTitle;
        bool onlySpaces = true;
        bool recognizedPrefix = false;
        int trailerLines = 0;
        int nonTrailerLines = 0;
        int possibleContinuationLines = 0;

        // The first paragraph is the title and cannot be trailers.
        int s = 0;
        while (s < len)
        {
            if (buf[s] == CommentLineChar)
            {
                s = NextLine(buf, s);
                continue;
            }

            if (IsBlankLine(buf, s))
            {
                break;
            }

            s = NextLine(buf, s);
        }

        endOfTitle = s;

        int l = len;
        while (true)
        {
            int from = l;
            if (!LastLine(out l, buf, from) || l < endOfTitle)
            {
                break;
            }
            int bol = l;

            if (buf[bol] == CommentLineChar)
            {
                nonTrailerLines += possibleContinuationLines;
                possibleContinuationLines = 0;
                continue;
            }

            if (IsBlankLine(buf, bol))
            {
                if (onlySpaces)
                {
                    continue;
                }

                nonTrailerLines += possibleContinuationLines;
                if (recognizedPrefix && trailerLines * 3 >= nonTrailerLines)
                {
                    return NextLine(buf, bol);
                }

                if (trailerLines > 0 && nonTrailerLines == 0)
                {
                    return NextLine(buf, bol);
                }

                return len;
            }

            onlySpaces = false;

            bool matchedPrefix = false;
            foreach (string prefix in s_gitGeneratedPrefixes)
            {
                if (StartsWith(buf, bol, len, prefix))
                {
                    trailerLines++;
                    possibleContinuationLines = 0;
                    recognizedPrefix = true;
                    matchedPrefix = true;
                    break;
                }
            }

            if (matchedPrefix)
            {
                continue;
            }

            int separatorPos = FindSeparator(buf, bol, len);
            if (separatorPos >= 1 && !IsSpace(buf[bol]))
            {
                trailerLines++;
                possibleContinuationLines = 0;
            }
            else if (IsSpace(buf[bol]))
            {
                possibleContinuationLines++;
            }
            else
            {
                nonTrailerLines++;
                nonTrailerLines += possibleContinuationLines;
                possibleContinuationLines = 0;
            }
        }

        return len;
    }

    /// <summary>
    /// Parses the extracted trailer block into individual trailers. Matches
    /// the state machine in <c>git_message_trailers</c>
    /// (trailer.c:297-424).
    /// </summary>
    private static List<GitMessageTrailer> ParseTrailerBlock(char[] trailer, int trailerLen)
    {
        var result = new List<GitMessageTrailer>();
        TrailerState state = TrailerState.Start;
        int ptr = 0;
        int keyStart = -1;
        int valueStart = -1;

        while (true)
        {
            switch (state)
            {
                case TrailerState.Start:
                    if (ptr >= trailerLen || trailer[ptr] == '\0')
                    {
                        return result;
                    }

                    keyStart = ptr;
                    state = TrailerState.Key;
                    continue;

                case TrailerState.Key:
                    if (ptr >= trailerLen || trailer[ptr] == '\0')
                    {
                        return result;
                    }

                    if (IsAlnum(trailer[ptr]) || trailer[ptr] == '-')
                    {
                        ptr++;
                        continue;
                    }

                    if (trailer[ptr] is ' ' or '\t')
                    {
                        trailer[ptr] = '\0';
                        ptr++;
                        state = TrailerState.KeyWs;
                        continue;
                    }

                    if (IsSeparator(trailer[ptr]))
                    {
                        trailer[ptr] = '\0';
                        ptr++;
                        state = TrailerState.SepWs;
                        continue;
                    }

                    state = TrailerState.Ignore;
                    continue;

                case TrailerState.KeyWs:
                    if (ptr >= trailerLen || trailer[ptr] == '\0')
                    {
                        return result;
                    }

                    if (trailer[ptr] is ' ' or '\t')
                    {
                        ptr++;
                        continue;
                    }

                    if (IsSeparator(trailer[ptr]))
                    {
                        ptr++;
                        state = TrailerState.SepWs;
                        continue;
                    }

                    state = TrailerState.Ignore;
                    continue;

                case TrailerState.SepWs:
                    if (ptr >= trailerLen || trailer[ptr] == '\0')
                    {
                        return result;
                    }

                    if (trailer[ptr] is ' ' or '\t')
                    {
                        ptr++;
                        continue;
                    }

                    valueStart = ptr;
                    ptr++;
                    state = TrailerState.Value;
                    continue;

                case TrailerState.Value:
                    if (ptr >= trailerLen || trailer[ptr] == '\0')
                    {
                        state = TrailerState.ValueEnd;
                        continue;
                    }

                    if (trailer[ptr] == '\n')
                    {
                        ptr++;
                        state = TrailerState.ValueNl;
                        continue;
                    }

                    ptr++;
                    continue;

                case TrailerState.ValueNl:
                    if (ptr < trailerLen && trailer[ptr] == ' ')
                    {
                        // Continuation line.
                        ptr++;
                        state = TrailerState.Value;
                        continue;
                    }

                    // Terminate the previous value at the newline.
                    if (ptr - 1 < trailerLen)
                    {
                        trailer[ptr - 1] = '\0';
                    }

                    state = TrailerState.ValueEnd;
                    continue;

                case TrailerState.ValueEnd:
                    result.Add(new GitMessageTrailer(
                        new string(trailer, keyStart, (keyStart >= 0 ? FindNullOrEnd(trailer, keyStart, trailerLen) : keyStart) - keyStart),
                        new string(trailer, valueStart, (valueStart >= 0 ? FindNullOrEnd(trailer, valueStart, trailerLen) : valueStart) - valueStart)));

                    keyStart = -1;
                    valueStart = -1;
                    state = TrailerState.Start;
                    continue;

                case TrailerState.Ignore:
                    if (ptr >= trailerLen || trailer[ptr] == '\0')
                    {
                        return result;
                    }

                    if (trailer[ptr] == '\n')
                    {
                        ptr++;
                        state = TrailerState.Start;
                        continue;
                    }

                    ptr++;
                    continue;
            }
        }
    }

    /// <summary>
    /// Finds the NUL terminator or end of buffer from <paramref name="start"/>.
    /// </summary>
    private static int FindNullOrEnd(char[] buffer, int start, int len)
    {
        int i = start;
        while (i < len && buffer[i] != '\0')
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Returns true if the line at <paramref name="bol"/> is blank. Matches
    /// <c>is_blank_line</c> (trailer.c:24-30).
    /// </summary>
    private static bool IsBlankLine(string str, int bol)
    {
        int s = bol;
        while (s < str.Length && str[s] != '\n' && IsSpace(str[s]))
        {
            s++;
        }

        return s >= str.Length || str[s] == '\n';
    }

    /// <summary>
    /// Returns the position of the next line (after the next newline). Matches
    /// <c>next_line</c> (trailer.c:32-42).
    /// </summary>
    private static int NextLine(string str, int pos)
    {
        int nl = str.IndexOf('\n', pos, StringComparison.Ordinal);
        return nl >= 0 ? nl + 1 : str.Length;
    }

    /// <summary>
    /// Returns the byte offset of the start of the next line from
    /// <paramref name="bol"/>.
    /// </summary>
    private static int NextLineFrom(string buf, int bol, int len)
    {
        int nl = buf.IndexOf('\n', bol, StringComparison.Ordinal);
        if (nl >= 0 && nl < len)
        {
            return nl + 1;
        }

        return len;
    }

    /// <summary>
    /// Returns the position of the start of the last line. Matches
    /// <c>last_line</c> (trailer.c:47-72).
    /// </summary>
    private static bool LastLine(out int pos, string buf, int len)
    {
        pos = 0;

        if (len == 0)
        {
            return false;
        }

        if (len == 1)
        {
            return true;
        }

        int i = len - 2;
        for (; i > 0; i--)
        {
            if (buf[i] == '\n')
            {
                pos = i + 1;
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the separator position in a line. Matches <c>find_separator</c>
    /// (trailer.c:86-105).
    /// </summary>
    private static int FindSeparator(ReadOnlySpan<char> line, int offset, int len)
    {
        bool whitespaceFound = false;
        for (int i = offset; i < len; i++)
        {
            char c = line[i];
            if (c == '\n')
            {
                break;
            }

            if (IsSeparator(c))
            {
                return i - offset;
            }

            if (!whitespaceFound && (IsAlnum(c) || c == '-'))
            {
                continue;
            }

            if (i > offset && (c == ' ' || c == '\t'))
            {
                whitespaceFound = true;
                continue;
            }

            break;
        }

        return -1;
    }

    /// <summary>Checks if <paramref name="buf"/> at <paramref name="offset"/> starts with <paramref name="prefix"/>.</summary>
    private static bool StartsWith(string buf, int offset, int len, string prefix)
    {
        if (offset + prefix.Length > len)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (buf[offset + i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSpace(char c)
        => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

    private static bool IsAlnum(char c)
        => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    private static bool IsSeparator(char c)
        => c == TrailerSeparator;

    private enum TrailerState
    {
        Start = 0,
        Key = 1,
        KeyWs = 2,
        SepWs = 3,
        Value = 4,
        ValueNl = 5,
        ValueEnd = 6,
        Ignore = 7,
    }
}
