// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Utils;

namespace LibGit2CS.Core;

/// <summary>
/// Commit message prettification. Managed port of libgit2's
/// <c>src/libgit2/message.c</c>.
/// </summary>
/// <remarks>
/// <see cref="Prettify"/> cleans up excess whitespace and ensures a trailing
/// newline. Optionally strips comment lines. Greatly inspired by git's
/// <c>stripspace</c>.
/// </remarks>
public static class GitMessage
{
    /// <summary>
    /// Cleans up excess whitespace and ensures a trailing newline in the
    /// message. Optionally removes lines starting with
    /// <paramref name="commentChar"/>. Matches <c>git_message_prettify</c>
    /// (message.c:68-75 + <c>git_message__prettify</c> message.c:26-66).
    /// </summary>
    /// <param name="message">The message to prettify.</param>
    /// <param name="stripComments">If non-zero, remove comment lines.</param>
    /// <param name="commentChar">Lines starting with this character are
    /// considered comments and removed when <paramref name="stripComments"/>
    /// is non-zero.</param>
    /// <returns>The cleaned-up message.</returns>
    public static string Prettify(ReadOnlySpan<char> message, char commentChar, bool stripComments = true)
    {
        const int InitialCapacity = 256;
        using var result = new ValueStringBuilder(InitialCapacity);
        int consecutiveEmptyLines = 0;
        int i = 0;

        while (i < message.Length)
        {
            int nextNewline = message.Slice(i).IndexOf('\n');
            int lineLength = nextNewline >= 0
                ? nextNewline + 1
                : message.Length - i;

            if (stripComments && lineLength > 0 && message[i] == commentChar)
            {
                i += lineLength;
                continue;
            }

            int rtrimmedLength = LineLengthWithoutTrailingSpaces(message, i, lineLength);

            if (rtrimmedLength == 0)
            {
                consecutiveEmptyLines++;
                i += lineLength;
                continue;
            }

            if (consecutiveEmptyLines > 0 && result.Length > 0)
            {
                result.Append('\n');
            }

            consecutiveEmptyLines = 0;
            result.Append(message.Slice(i, rtrimmedLength));
            result.Append('\n');
            i += lineLength;
        }

        return result.ToString();
    }

    /// <summary>
    /// Returns the length of <c>line[0..len]</c> with trailing whitespace
    /// removed. Matches <c>line_length_without_trailing_spaces</c>
    /// (message.c:12-22).
    /// </summary>
    private static int LineLengthWithoutTrailingSpaces(ReadOnlySpan<char> line, int offset, int len)
    {
        while (len > 0)
        {
            char c = line[offset + len - 1];
            if (!IsSpace(c))
            {
                break;
            }

            len--;
        }

        return len;
    }

    /// <summary>Matches <c>git__isspace</c> — the C isspace set.</summary>
    internal static bool IsSpace(char c)
        => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';
}
