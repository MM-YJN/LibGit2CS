// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Objects;

/// <summary>
/// Low-level line-oriented byte parser. Managed port of libgit2's
/// <c>src/libgit2/parse.c</c> + <c>parse.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// Git object formats (commit, tag, tree, grafts) are line-oriented byte streams.
/// The C <c>git_parse_ctx</c> tracks <c>content</c>/<c>remain</c>/<c>line</c> as
/// pointer + length, where <c>line</c> advances as chars are consumed (so
/// <c>line_len</c> shrinks). In managed code we hold the full
/// <see cref="ReadOnlySpan{T}"/> of bytes and track three positions:
/// <see cref="_lineStart"/> (first byte after the previous <c>\n</c>),
/// <see cref="_lineEnd"/> (position of the terminating <c>\n</c>, or end of
/// content), and <see cref="_offset"/> (current read cursor within the line).
/// </para>
/// <para>
/// <b>Ref struct</b> — stack-only, cannot be boxed or stored on the heap. Matches
/// the C struct-by-value usage pattern (parsers hold it as a local).
/// </para>
/// </remarks>
public ref struct GitObjectParser(ReadOnlySpan<byte> content)
{
    private readonly ReadOnlySpan<byte> _content = content;
    private int _lineStart = 0;
    private int _lineEnd = FindLineEnd(content, 0);
    private int _offset = 0;
    private int _lineNumber = 1;

    /// <summary>The full content being parsed.</summary>
    public readonly ReadOnlySpan<byte> Content => _content;

    /// <summary>Remaining unparsed bytes from the current cursor position.</summary>
    public readonly ReadOnlySpan<byte> Remain => _content[_offset..];

    /// <summary>
    /// The remaining unconsumed bytes of the current line (excluding the trailing
    /// <c>\n</c>). As <see cref="AdvanceChars"/> consumes bytes, this shrinks.
    /// </summary>
    public readonly ReadOnlySpan<byte> Line => _content[_offset.._lineEnd];

    /// <summary>1-based current line number.</summary>
    public readonly int LineNumber => _lineNumber;

    /// <summary>
    /// True if the current line is terminated by a <c>\n</c> (i.e. the line
    /// does not run to the end of the content). Matches C's
    /// <c>git_parse_ctx</c> semantics where a line at end-of-buffer has no
    /// terminator — parsers that require one (e.g.
    /// <c>git_signature__parse</c>'s ender, signature.c:330-332) must reject
    /// such lines.
    /// </summary>
    public readonly bool IsLineTerminated => _lineEnd < _content.Length;

    /// <summary>True if no bytes remain to parse.</summary>
    public readonly bool IsAtEnd => _offset >= _content.Length;

    /// <summary>
    /// True if the remaining line content starts with <paramref name="expected"/>.
    /// Matches <c>git_parse_ctx_contains</c>.
    /// </summary>
    public readonly bool LineStartsWith(ReadOnlySpan<byte> expected)
    {
        ReadOnlySpan<byte> line = Line;
        return line.Length >= expected.Length && line[..expected.Length].SequenceEqual(expected);
    }

    /// <summary>
    /// Advances past the current line (including its trailing <c>\n</c>) and
    /// positions the cursor at the start of the next line. Matches
    /// <c>git_parse_advance_line</c>.
    /// </summary>
    public void AdvanceLine()
    {
        // _lineEnd points at the '\n' (or end of content).
        int next = _lineEnd;
        if (next < _content.Length && _content[next] == (byte)'\n')
        {
            next++;
        }

        _lineStart = next;
        _offset = next;
        _lineEnd = FindLineEnd(_content, next);
        _lineNumber++;
    }

    /// <summary>
    /// Advances <paramref name="count"/> bytes within the current line.
    /// Matches <c>git_parse_advance_chars</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="count"/> exceeds the remaining line length.
    /// </exception>
    public void AdvanceChars(int count)
    {
        int remaining = _lineEnd - _offset;
        if (count < 0 || count > remaining)
        {
            throw new ArgumentException(
                $"Cannot advance {count} bytes; line has {remaining} remaining.", nameof(count));
        }

        _offset += count;
    }

    /// <summary>
    /// If the remaining line content starts with <paramref name="expected"/>,
    /// advances past it and returns true. Otherwise returns false. Matches
    /// <c>git_parse_advance_expected</c>.
    /// </summary>
    public bool AdvanceExpected(ReadOnlySpan<byte> expected)
    {
        if (!LineStartsWith(expected))
        {
            return false;
        }

        _offset += expected.Length;
        return true;
    }

    /// <summary>
    /// If the remaining line content starts with the ASCII string
    /// <paramref name="expected"/>, advances past it and returns true.
    /// </summary>
    public bool AdvanceExpected(string expected) => AdvanceExpected(Encoding.ASCII.GetBytes(expected));

    /// <summary>
    /// Advances past leading whitespace (spaces and tabs, not newlines) on the
    /// current line. Returns true if any whitespace was skipped. Matches
    /// <c>git_parse_advance_ws</c>.
    /// </summary>
    public bool AdvanceWhitespace()
    {
        ReadOnlySpan<byte> line = Line;
        int i = 0;
        while (i < line.Length && IsSpace(line[i]))
        {
            i++;
        }

        if (i <= 0)
        {
            return false;
        }

        _offset += i;
        return true;
    }

    /// <summary>
    /// If the cursor is at the end of the current line and the next byte is
    /// <c>\n</c>, advances to the next line and returns true. Otherwise returns
    /// false. Matches <c>git_parse_advance_nl</c> — which requires the
    /// remaining line to be exactly <c>\n</c>, so a line whose content ends
    /// with <c>\r</c> (CRLF) is rejected.
    /// </summary>
    public bool AdvanceNewline()
    {
        if (_offset != _lineEnd || _offset >= _content.Length)
        {
            return false;
        }

        AdvanceLine();
        return true;
    }

    /// <summary>
    /// Parses a base-<paramref name="radix"/> integer from the start of the
    /// remaining line content and advances past it. Matches
    /// <c>git_parse_advance_digit</c> (parse.c:90-103).
    /// </summary>
    /// <remarks>
    /// The C <c>git_parse_advance_digit</c> uses <c>git__isdigit</c> (0-9 only) as
    /// its initial guard, so hex values starting with <c>a</c>-<c>f</c> are
    /// rejected (parse.c:91). The value is then parsed with
    /// <c>git__strntol64</c> (util.c:33-148), which consumes the optional
    /// <c>0x</c> prefix for base 16 and fails the whole parse — without
    /// advancing — on integer overflow.
    /// </remarks>
    /// <returns>The parsed value, or null if no valid digit is present or the value overflows.</returns>
    public long? AdvanceDigit(int radix = 10)
    {
        ReadOnlySpan<byte> line = Line;
        if (line.Length == 0 || line[0] is < (byte)'0' or > (byte)'9')
        {
            return null;
        }

        int end = 0;
        if (radix == 16 && line.Length > 2 && line[0] == (byte)'0' && line[1] is (byte)'x' or (byte)'X')
        {
            end = 2; // git__strntol64 skips the "0x" prefix (util.c:69-71)
        }

        while (end < line.Length && IsValidDigit(line[end], radix))
        {
            end++;
        }

        if (end == 0 || (radix == 16 && end == 2))
        {
            return null; // no digits after the prefix — strntol64 "not a number"
        }

        // strntol64 semantics: overflow fails the parse WITHOUT advancing
        // (util.c:145-148, "overflow error") — matches the null-on-failure
        // convention instead of leaking an OverflowException.
        string text = Encoding.ASCII.GetString(line[..end]);
        long value;
        try
        {
            value = Convert.ToInt64(text, radix);
        }
        catch (OverflowException)
        {
            return null;
        }

        _offset += end;
        return value;
    }

    /// <summary>
    /// Parses an OID of the given algorithm from the start of the remaining line
    /// content and advances past it. Matches <c>git_parse_advance_oid</c>.
    /// </summary>
    /// <returns>The parsed OID, or null if the line is too short or has invalid hex.</returns>
    public GitOid? AdvanceOid(GitHashAlgorithmKind kind)
    {
        int hexSize = GitOid.HexSizeFor(kind);
        ReadOnlySpan<byte> line = Line;
        if (line.Length < hexSize)
        {
            return null;
        }

        // OID hex is ASCII; max 64 hex chars for SHA-256 — stackalloc avoids allocation.
        Span<char> chars = stackalloc char[hexSize];
        for (int i = 0; i < hexSize; i++)
        {
            chars[i] = (char)line[i];
        }

        if (!GitOid.TryParse(chars, kind, out GitOid oid))
        {
            return null;
        }

        _offset += hexSize;
        return oid;
    }

    /// <summary>
    /// Peeks at the next non-whitespace byte on the current line without
    /// advancing. Returns -1 if the line is empty or all whitespace. Matches
    /// <c>git_parse_peek</c> with <c>GIT_PARSE_PEEK_SKIP_WHITESPACE</c>.
    /// </summary>
    public readonly int PeekSkipWhitespace()
    {
        ReadOnlySpan<byte> line = Line;
        for (int i = 0; i < line.Length; i++)
        {
            if (!IsSpace(line[i]))
            {
                return line[i];
            }
        }

        return -1;
    }

    /// <summary>
    /// Peeks at the first unconsumed byte of the current line without advancing.
    /// Returns -1 if the cursor is at the end of the line.
    /// </summary>
    public readonly int Peek() => _offset < _lineEnd ? _content[_offset] : -1;

    /// <summary>
    /// Finds the position of the <c>\n</c> ending the line that starts at
    /// <paramref name="start"/>. Returns the content length if no <c>\n</c> is found.
    /// </summary>
    /// <remarks>
    /// C's <c>git__linenlen</c> (util.c:337-342) counts everything up to and
    /// including the <c>\n</c> as the line — a <c>\r</c> before the newline is
    /// part of the line CONTENT, not a line terminator. So a CRLF line's
    /// content ends with <c>\r</c>, and <c>git_parse_advance_nl</c> (which
    /// requires the remaining line to be exactly <c>\n</c>) fails — commit/tag
    /// objects with CRLF headers are rejected, exactly like C.
    /// </remarks>
    private static int FindLineEnd(ReadOnlySpan<byte> content, int start)
    {
        if (start >= content.Length)
        {
            return content.Length;
        }

        int nl = content[start..].IndexOf((byte)'\n');
        return nl < 0 ? content.Length : start + nl;
    }

    private static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\r';

    private static bool IsValidDigit(byte c, int radix)
    {
        if (c is >= (byte)'0' and <= (byte)'9')
        {
            return c - (byte)'0' < radix;
        }

        if (radix > 10)
        {
            if (c >= (byte)'a' && c <= (byte)'a' + radix - 11)
            {
                return true;
            }

            if (c >= (byte)'A' && c <= (byte)'A' + radix - 11)
            {
                return true;
            }
        }

        return false;
    }
}
