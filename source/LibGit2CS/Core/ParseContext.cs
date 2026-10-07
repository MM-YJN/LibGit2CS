// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary> Line-level cursor over an input buffer. Managed port of libgit2's <c>git_parse_ctx</c> (<c>src/libgit2/parse.h:12-24</c> +
/// <c>src/libgit2/parse.c:10-138</c>). </summary> <remarks> <para> The cursor tracks the original content, the remaining unparsed bytes, and the current line
/// (start pointer + length + 1-based number). Advancing moves the line pointer forward and recomputes the line length by scanning for the next <c>\n</c>. The
/// <c>remain</c> pointer is implicit in <c>_offset</c> (an index into the buffer) since C# does not support pointer arithmetic over managed buffers without
/// <c>unsafe</c> context. </para> <para> <b>Byte domain</b>: the buffer is a <see cref="ReadOnlyMemory{T}"/> of <see cref="byte"/> exactly as in C
/// (<c>git_parse_ctx</c> walks a <c>char *</c> buffer of raw patch bytes); there is no string intermediate and no decode/re-encode boundary. All offsets are
/// byte offsets. </para> <para> <b>Primary consumer:</b> <c>PatchParser</c> (<c>patch_parse.c</c>), which calls these helpers on nearly every line of the
/// header/hunk/body parsers. The cursor is a mutable struct passed by reference in C; in C# it is a mutable class to keep the call-site ergonomics close to C
/// (advance mutates in place) while avoiding <c>ref</c>-struct restrictions. </para> </remarks>
internal sealed class ParseContext
{
    private ReadOnlyMemory<byte> _content = ReadOnlyMemory<byte>.Empty;
    private int _contentLength;
    private int _offset;       // index into _content of the start of the current line
    private int _lineLength;   // length of current line (including trailing '\n' if present)
    private int _lineNumber = 1;

    /// <summary>The original content buffer (never modified).</summary>
    public ReadOnlyMemory<byte> Content => _content;

    /// <summary>Length of the original content buffer.</summary>
    public int ContentLength => _contentLength;

    /// <summary>Remaining unparsed bytes (from current line start to end).</summary>
    public int RemainLength => _contentLength - _offset;

    /// <summary>The 1-based current line number.</summary>
    public int LineNumber => _lineNumber;

    /// <summary>Length of the current line (including trailing <c>\n</c> if present).</summary>
    public int LineLength => _lineLength;

    /// <summary>
    /// Initializes the parse context from a content buffer. Matches
    /// <c>git_parse_ctx_init</c> (parse.c:10-27). Empty content is treated as
    /// an empty buffer.
    /// </summary>
    public void Init(ReadOnlyMemory<byte> content)
    {
        if (content.Length > 0)
        {
            _content = content;
            _contentLength = content.Length;
        }
        else
        {
            _content = ReadOnlyMemory<byte>.Empty;
            _contentLength = 0;
        }

        _offset = 0;
        _lineLength = LineLengthOf(0, _contentLength);
        _lineNumber = 1;
    }

    /// <summary>
    /// Resets the context to an empty state. Matches
    /// <c>git_parse_ctx_clear</c> (parse.c:29-33).
    /// </summary>
    public void Clear()
    {
        _content = ReadOnlyMemory<byte>.Empty;
        _contentLength = 0;
        _offset = 0;
        _lineLength = 0;
        _lineNumber = 1;
    }

    /// <summary>
    /// Whether there is any content remaining to parse.
    /// </summary>
    public bool HasRemaining => RemainLength > 0;

    /// <summary>
    /// A readonly span over the current line (NOT including the trailing
    /// <c>\n</c>).
    /// </summary>
    public ReadOnlySpan<byte> Line => _content.Span.Slice(_offset, _lineLength);

    /// <summary>
    /// Checks whether the current line starts with the given bytes. Matches
    /// <c>git_parse_ctx_contains</c> (parse.h:34-38).
    /// </summary>
    public bool Contains(ReadOnlySpan<byte> str)
        => _lineLength >= str.Length && Line.StartsWith(str);

    /// <summary>
    /// Advances past the current line. Matches <c>git_parse_advance_line</c>
    /// (parse.c:35-41).
    /// </summary>
    public void AdvanceLine()
    {
        _offset += _lineLength;
        int remain = _contentLength - _offset;
        _lineLength = LineLengthOf(_offset, remain);
        _lineNumber++;
    }

    /// <summary>
    /// Advances <paramref name="byteCount"/> bytes within the current line.
    /// Matches <c>git_parse_advance_chars</c> (parse.c:43-48).
    /// </summary>
    public void AdvanceChars(int byteCount)
    {
        _offset += byteCount;
        _lineLength -= byteCount;
    }

    /// <summary>
    /// If the current line starts with <paramref name="expected"/>, advances
    /// past it and returns true; otherwise returns false. Matches
    /// <c>git_parse_advance_expected</c> (parse.c:50-63).
    /// </summary>
    public bool AdvanceExpected(ReadOnlySpan<byte> expected)
    {
        if (_lineLength < expected.Length || !Line.StartsWith(expected))
        {
            return false;
        }

        AdvanceChars(expected.Length);
        return true;
    }

    /// <summary>
    /// Skips whitespace on the current line (not past <c>\n</c>). Returns true if
    /// any whitespace was skipped. Matches <c>git_parse_advance_ws</c>
    /// (parse.c:65-79).
    /// </summary>
    public bool AdvanceWs()
    {
        ReadOnlySpan<byte> content = _content.Span;
        bool skipped = false;
        while (_lineLength > 0 && content[_offset] != (byte)'\n' && IsSpace(content[_offset]))
        {
            _offset++;
            _lineLength--;
            skipped = true;
        }

        return skipped;
    }

    /// <summary>
    /// If the current line is exactly <c>\n</c>, advances past it and returns
    /// true; otherwise returns false. Matches <c>git_parse_advance_nl</c>
    /// (parse.c:81-88).
    /// </summary>
    public bool AdvanceNl()
    {
        if (_lineLength != 1 || _content.Span[_offset] != (byte)'\n')
        {
            return false;
        }

        AdvanceLine();
        return true;
    }

    /// <summary>
    /// Parses a signed integer from the start of the current line and advances
    /// past it. Matches <c>git_parse_advance_digit</c> (parse.c:90-103) but
    /// returns the value via <paramref name="value"/> and a bool success.
    /// </summary>
    public bool AdvanceDigit(out long value, int baseValue)
    {
        value = 0;
        if (_lineLength < 1 || !IsDigit(_content.Span[_offset]))
        {
            return false;
        }

        ReadOnlySpan<byte> span = Line;
        if (!TryParseInt64(span, baseValue, out value, out int consumed))
        {
            return false;
        }

        AdvanceChars(consumed);
        return true;
    }

    /// <summary>
    /// Peeks at the next (optionally non-whitespace) byte on the current line
    /// without advancing. Matches <c>git_parse_peek</c> (parse.c:118-138).
    /// </summary>
    public bool Peek(out byte b, bool skipWhitespace = false)
    {
        b = 0;
        int remain = _lineLength;
        int i = _offset;
        ReadOnlySpan<byte> content = _content.Span;
        while (remain > 0)
        {
            byte ch = content[i];
            if (skipWhitespace && IsSpace(ch))
            {
                remain--;
                i++;
                continue;
            }

            b = ch;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Computes the length of the first line in the buffer starting at
    /// <paramref name="offset"/> (including the trailing <c>\n</c> if present).
    /// Matches <c>git__linenlen</c> (util.c:337-341).
    /// </summary>
    private int LineLengthOf(int offset, int remain)
    {
        if (remain <= 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> span = _content.Span.Slice(offset, remain);
        int nl = span.IndexOf((byte)'\n');
        return nl < 0 ? remain : nl + 1;
    }

    private static bool IsSpace(int c)
        => c is ' ' or '\t' or '\n' or '\f' or '\r' or '\v';

    private static bool IsDigit(int c)
        => c is >= '0' and <= '9';

    /// <summary>
    /// Parses a signed 64-bit integer from the start of the span. Matches the
    /// subset of <c>git__strntol64</c> (util.c:34-136) used by
    /// <c>git_parse_advance_digit</c>: the first char is already verified as a
    /// digit (no sign, no whitespace skip, no base auto-detect). Supports base 8
    /// / 10 / 16. Sets <paramref name="consumed"/> to the number of bytes
    /// parsed. Returns false on overflow.
    /// </summary>
    private static bool TryParseInt64(ReadOnlySpan<byte> span, int baseValue, out long result, out int consumed)
    {
        result = 0;
        consumed = 0;

        if (baseValue == 0)
        {
            baseValue = span[0] != (byte)'0' ? 10
                : (span.Length > 2 && (span[1] == 'x' || span[1] == 'X')) ? 16 : 8;
        }

        if (baseValue is < 0 or > 36)
        {
            return false;
        }

        int p = 0;
        // Skip 0x prefix for base 16.
        if (baseValue == 16 && span.Length > 2 && span[0] == (byte)'0' && (span[1] == 'x' || span[1] == 'X'))
        {
            p += 2;
        }

        long n = 0L;
        int ndig = 0;
        bool overflow = false;
        for (; p < span.Length; p++, ndig++)
        {
            int c = span[p];
            long v;
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
            else
            {
                break;
            }

            if (v >= baseValue)
            {
                break;
            }

            if (overflow)
            {
                continue;
            }

            // n = n * baseValue + v, with overflow check.
            try
            {
                n = checked(n * baseValue + v);
            }
            catch (OverflowException)
            {
                overflow = true;
                n = 0;
            }
        }

        if (ndig == 0)
        {
            return false;
        }

        consumed = p;
        result = overflow ? 0 : n;
        return !overflow;
    }
}
