// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Utils;

namespace LibGit2CS.Config;

/// <summary>
/// Streaming tokenizer for the git config file format. Managed port of
/// libgit2's <c>config_parse.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// The git config grammar (see <c>git-config(1)</c> and the grammar comment in
/// <c>config_parse.c</c>) supports three section-header forms, multiline values
/// via trailing backslash, escape sequences, comment stripping that respects
/// quote parity, and UTF-8 BOM. There is no BCL parser that handles these rules
/// correctly; this is a faithful line-oriented port of libgit2's tokenizer.
/// </para>
/// <para>
/// <b>Callbacks are delegate-based</b> matching the C <c>git_config_parser_*_cb</c>
/// signatures. The parser is synchronous; callbacks must not store the passed
/// spans beyond their invocation (values are materialized to strings before the
/// callback returns).
/// </para>
/// </remarks>
internal sealed class ConfigParser
{
    private readonly string _path;
    private readonly ReadOnlyMemory<byte> _content;
    private int _offset;
    private int _lineNum;

    private ConfigParser(string path, ReadOnlyMemory<byte> content)
    {
        _path = path;
        _content = content;
        _offset = 0;
        _lineNum = 1;
    }

    /// <summary>
    /// Callback invoked when a section header is parsed.
    /// </summary>
    /// <param name="currentSection">The fully-qualified normalized section name (e.g. <c>core</c> or <c>branch.master</c>). Section name is lowercased; quoted subsection preserves case.</param>
    /// <param name="line">The raw line text INCLUDING its trailing <c>\n</c> (absent only for the final line at EOF), matching C's <c>line_start</c>/<c>line_len</c> at <c>write_on_section</c> time. For a header with trailing content (<c>[sec] var = 1</c>) this is the FULL line.</param>
    /// <param name="lineNum">1-based line number for diagnostics.</param>
    public delegate Task SectionCallback(ReadOnlyMemory<byte> currentSection, ReadOnlyMemory<byte> line, int lineNum);

    /// <summary>
    /// Callback invoked for each variable declaration (<c>name = value</c> or lone <c>name</c>).
    /// </summary>
    /// <param name="currentSection">The active section, or <c>null</c> if the variable appears before any section header.</param>
    /// <param name="varName">The variable name with ORIGINAL case. The callback is responsible for lowercasing when building the fully-qualified key.</param>
    /// <param name="varValue">The unescaped value, or <c>null</c> for a lone variable (no <c>=</c>).</param>
    /// <param name="line">The raw line text INCLUDING its trailing <c>\n</c> (absent only at EOF); for multiline values this spans ALL continuation lines, matching C's <c>line_start</c>/<c>line_len</c> at <c>write_on_variable</c> time. For a variable on a section-header line this is only the remainder after the header.</param>
    /// <param name="lineNum">1-based line number for diagnostics.</param>
    public delegate Task VariableCallback(ReadOnlyMemory<byte>? currentSection, ReadOnlyMemory<byte> varName, ReadOnlyMemory<byte>? varValue, ReadOnlyMemory<byte> line, int lineNum);

    /// <summary>
    /// Callback invoked for comment-only or blank lines.
    /// </summary>
    /// <param name="line">The raw line text INCLUDING its trailing <c>\n</c>; blank lines yield <c>"\n"</c>.</param>
    /// <param name="lineNum">1-based line number for diagnostics.</param>
    public delegate Task CommentCallback(ReadOnlyMemory<byte> line, int lineNum);

    /// <summary>
    /// Callback invoked at end-of-file.
    /// </summary>
    /// <param name="currentSection">The active section at EOF, or <c>null</c>.</param>
    public delegate Task EofCallback(ReadOnlyMemory<byte>? currentSection);

    /// <summary>
    /// Parses the config text, dispatching to the given callbacks. Matches
    /// libgit2's <c>git_config_parse</c>.
    /// </summary>
    /// <param name="path">File path (used in error messages).</param>
    /// <param name="content">The config file text.</param>
    /// <param name="onSection">Section header callback, or <c>null</c> to ignore.</param>
    /// <param name="onVariable">Variable callback, or <c>null</c> to ignore.</param>
    /// <param name="onComment">Comment callback, or <c>null</c> to ignore.</param>
    /// <param name="onEof">EOF callback, or <c>null</c> to ignore.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task ParseAsync(
        string path,
        ReadOnlyMemory<byte> content,
        SectionCallback? onSection,
        VariableCallback? onVariable,
        CommentCallback? onComment,
        EofCallback? onEof,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parser = new ConfigParser(path, content);
        await parser.ParseImplAsync(onSection, onVariable, onComment, onEof, cancellationToken).ConfigureAwait(false);
    }

    private static readonly ReadOnlyMemory<byte> s_newLine = "\n"u8.ToArray();

    [SuppressMessage("Performance", "IDE0060:Remove unused parameters", Justification = "CT reserved for future cancellation forwarding; body uses `out _` discards so the param cannot be named `_`.")]
    private async Task ParseImplAsync(
        SectionCallback? onSection,
        VariableCallback? onVariable,
        CommentCallback? onComment,
        EofCallback? onEof,
        CancellationToken cancellationToken)
    {
        SkipBom();

        PooledByteBufferWriter? currentSection = null;
        bool eof = false;

        using var valueWriter = new PooledByteBufferWriter();

        try
        {
            while (!eof)
            {
                if (_offset >= _content.Length && AtLineEnd())
                {
                    break;
                }

                int lineNum = _lineNum;
                int lineStart = _offset;

                if (!TryPeek(skipWhitespace: true, out byte c) && !TryPeek(skipWhitespace: false, out c))
                {
                    // Blank line — C delivers it to on_comment (config_parse.c:553-559).
                    if (onComment is not null)
                    {
                        await onComment(s_newLine, lineNum).ConfigureAwait(false);
                    }

                    if (!AdvanceLine())
                    {
                        eof = true;
                    }

                    continue;
                }

                switch ((char)c)
                {
                    case '[':
                        currentSection ??= new();
                        currentSection.ResetWrittenCount();
                        ParseSectionHeader(currentSection);
                        if (onSection is not null)
                        {
                            await onSection(currentSection.WrittenMemory, RawLineFrom(lineStart), lineNum).ConfigureAwait(false);
                        }

                        // After parsing the section header, there may be more content
                        // on the same line (e.g. `[section] var = val`). Re-peek to
                        // process the remainder without advancing to the next line.
                        if (TryPeek(skipWhitespace: true, out _))
                        {
                            continue;
                        }

                        if (!AdvanceLine())
                        {
                            eof = true;
                        }

                        break;

                    case '\n':
                    case '\r':
                    case ' ':
                    case '\t':
                    case ';':
                    case '#':
                        if (onComment is not null)
                        {
                            await onComment(RawLineFrom(lineStart), lineNum).ConfigureAwait(false);
                        }

                        if (!AdvanceLine())
                        {
                            eof = true;
                        }

                        break;

                    default:
                        valueWriter.ResetWrittenCount();
                        ParseVariable(out ReadOnlyMemory<byte> varName, valueWriter, out bool valueWritten);
                        if (onVariable is not null)
                        {
                            // The ternary's common type must be ReadOnlyMemory<byte>?;
                            // without the explicit cast, `null` would coerce to
                            // default(ReadOnlyMemory<byte>) = Empty (non-null),
                            // collapsing the lone-variable/null-value distinction.
                            ReadOnlyMemory<byte>? varValue = valueWritten ? valueWriter.WrittenMemory : (ReadOnlyMemory<byte>?)null;
                            await onVariable(currentSection?.WrittenMemory, varName, varValue, _content[lineStart.._offset], lineNum).ConfigureAwait(false);
                        }

                        break;
                }
            }

            if (onEof is not null)
            {
                await onEof(currentSection?.WrittenMemory).ConfigureAwait(false);
            }
        }
        finally
        {
            currentSection?.Dispose();
        }
    }

    private void SkipBom()
    {
        // UTF-8 BOM: EF BB BF, encoded in the string as the U+FEFF character.
        if (_content.Length >= 3 && _content.Span[0] == 0xEF && _content.Span[1] == 0xBB && _content.Span[2] == 0xBF)
        {
            _offset = 3;
            return;
        }
    }

    private bool TryPeek(bool skipWhitespace, out byte c)
    {
        ReadOnlySpan<byte> span = CurrentLine.Span;
        int i = 0;

        if (skipWhitespace)
        {
            while (i < span.Length && IsAsciiWhitespace((char)span[i]))
            {
                i++;
            }
        }

        if (i >= span.Length)
        {
            c = 0;
            return false;
        }

        c = span[i];
        return true;
    }

    private ReadOnlyMemory<byte> CurrentLine
    {
        get
        {
            int start = _offset;
            // Span.IndexOf returns an index RELATIVE to the slice start; the
            // pre-refactor code used string.IndexOf('\n', _offset) which
            // returned an ABSOLUTE index. Treat the result as a relative
            // offset (line length), not an absolute position.
            int end = _content.Span.Slice(start).IndexOf((byte)'\n');
            if (end < 0)
            {
                return _content[start..];
            }

            return _content.Slice(start, end);
        }
    }

    private bool AtLineEnd() => _offset >= _content.Length;

    /// <summary>
    /// The raw text of the line starting at <paramref name="start"/> INCLUDING
    /// its trailing newline (absent only for the final line at EOF). Matches
    /// C's <c>line_start</c>/<c>line_len</c>.
    /// </summary>
    private ReadOnlyMemory<byte> RawLineFrom(int start)
    {
        int end = _content.Span.Slice(start).IndexOf((byte)'\n');
        if (end < 0)
        {
            return _content[start..];
        }

        // end is RELATIVE to start; convert to absolute and include the '\n'.
        return _content[start..(start + end + 1)];
    }

    private bool AdvanceLine()
    {
        // newlineIdx is RELATIVE to _offset (Span.IndexOf on a slice).
        int newlineIdx = _content.Span.Slice(_offset).IndexOf((byte)'\n');
        if (newlineIdx < 0)
        {
            _offset = _content.Length;
            return false;
        }

        _offset += newlineIdx + 1;
        _lineNum++;
        return true; // always true — we moved forward
    }

    private void AdvanceChars(int n) => _offset += n;

    private void ParseSectionHeader(PooledByteBufferWriter writer)
    {
        ReadOnlySpan<byte> line = CurrentLine.Span;

        // Find the closing ']'.
        int closeIdx = line.LastIndexOf((byte)']');
        if (closeIdx < 0)
        {
            throw ParseError(0, "missing ']' in section header");
        }

        int pos = 0;

        // Skip leading whitespace.
        while (pos < line.Length && IsAsciiWhitespace((char)line[pos]))
        {
            pos++;
        }

        if (pos >= line.Length || line[pos] != '[')
        {
            throw ParseError(pos, "expected '[' to open section header");
        }

        pos++;

        int nameLength = 0;

        // Read section name: alphanumeric, '-', or '.'. Stop at whitespace (subsection
        // follows) or ']' (end of section).
        while (pos < closeIdx)
        {
            char c = (char)line[pos];
            if (IsAsciiWhitespace(c))
            {
                // Whitespace → parse a quoted subsection.
                Debug.Assert(writer.WrittenCount == nameLength);
                int endOffset = ParseSubsectionHeader(writer, line, pos);
                // C's
                // parse_subsection_header returns the offset right after the
                // FIRST ']' following the closing quote (config_parse.c:150);
                // advancing past the LAST ']' swallowed an extra trailing
                // ']' that C re-processes as a variable and rejects.
                AdvanceChars(endOffset);
                return;
            }

            if (!IsKeyChar(c) && c != '.')
            {
                throw ParseError(pos, "unexpected character in section header");
            }

            writer.Write((byte)char.ToLowerInvariant(c));
            nameLength++;
            pos++;
        }

        // C's
        // parse_section_header reads the char after '[' first and rejects a
        // ']' there ("unexpected character in header", config_parse.c:191-205)
        // — an empty section name like `[]` must fail the whole file load.
        if (nameLength == 0)
        {
            throw ParseError(pos, "unexpected character in header");
        }

        if (pos >= line.Length || line[pos] != ']')
        {
            throw ParseError(pos, "unexpected end of file in section header");
        }

        AdvanceChars(closeIdx + 1);
    }

    private int ParseSubsectionHeader(PooledByteBufferWriter writer, ReadOnlySpan<byte> line, int pos)
    {
        // Skip whitespace before the quote.
        while (pos < line.Length && IsAsciiWhitespace((char)line[pos]))
        {
            pos++;
        }

        if (pos >= line.Length || line[pos] != '"')
        {
            throw ParseError(pos, "missing quotation marks in section header");
        }

        int firstQuote = pos;
        if (!line.Slice(firstQuote + 1).Contains((byte)'"'))
        {
            throw ParseError(pos, "missing closing quotation mark in section header");
        }

        writer.Write((byte)'.');

        pos = firstQuote + 1;
        while (pos < line.Length)
        {
            char c = (char)line[pos];
            if (c == '"')
            {
                // Closing quote reached — stop and validate the char after it.
                break;
            }

            switch (c)
            {
                case '\0':
                    throw ParseError(pos, "unexpected end-of-line in section header");
                case '\\':
                    pos++;
                    if (pos >= line.Length)
                    {
                        throw ParseError(pos, "unexpected end-of-line in section header");
                    }

                    writer.Write((byte)line[pos]);
                    break;
                default:
                    writer.Write((byte)c);
                    break;
            }

            pos++;
        }

        // C (config_parse.c:143-147): after the closing quote, the next char
        // must be ']'. For `[sec "a"b"]` the first closing quote is followed
        // by 'b', so this errors ("unexpected text after closing quotes")
        // instead of jumping to the last quote and accepting.
        if (pos >= line.Length || line[pos] != '"' || pos + 1 >= line.Length || line[pos + 1] != ']')
        {
            throw ParseError(pos, "unexpected text after closing quotes");
        }

        // The offset just past the FIRST ']' after the closing quote — C's
        // parse_subsection_header return value (config_parse.c:150). Any
        // further ']' on the line is re-processed by the main loop and
        // rejected as a variable.
        return pos + 2;
    }

    private void ParseVariable(out ReadOnlyMemory<byte> name, PooledByteBufferWriter valueWriter, out bool valueWritten)
    {
        ReadOnlyMemory<byte> currentLine = CurrentLine;
        ReadOnlySpan<byte> rawLine = currentLine.Span;

        // Skip leading whitespace (matches git_parse_advance_ws in parse_variable).
        int wsStart = 0;
        while (wsStart < rawLine.Length && IsAsciiWhitespace((char)rawLine[wsStart]))
        {
            wsStart++;
        }

        ReadOnlyMemory<byte> line = wsStart > 0 ? currentLine[wsStart..] : currentLine;

        // Strip comments (quote-aware).
        ReadOnlyMemory<byte> stripped = StripComments(line, inQuotes: 0, out int quoteCount);

        // Parse the name.
        name = ParseName(stripped, out int valueStartIndex);
        if (valueStartIndex < 0)
        {
            // Lone variable (no '=').
            AdvanceLine();
            valueWritten = false;
            return;
        }

        // valueStartIndex is the index just after '='. Skip whitespace.
        ReadOnlySpan<byte> strippedSpan = stripped.Span;
        int vs = valueStartIndex;
        while (vs < strippedSpan.Length && IsAsciiWhitespace((char)strippedSpan[vs]))
        {
            vs++;
        }

        ReadOnlySpan<byte> remaining = vs < strippedSpan.Length ? strippedSpan[vs..] : default;

        // Unescape, detecting multiline continuation.
        bool isMulti = UnescapeLine(remaining, valueWriter);
        if (isMulti)
        {
            ParseMultilineVariable(valueWriter, quoteCount % 2);
        }

        AdvanceLine();
        valueWritten = true;
    }

    private static ReadOnlyMemory<byte> StripComments(ReadOnlyMemory<byte> line, int inQuotes, out int quoteCount)
    {
        ReadOnlySpan<byte> lineSpan = line.Span;

        quoteCount = inQuotes;
        int backslashCount = 0;
        int end = line.Length;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = (char)lineSpan[i];

            if (ch == '"' && ((i > 0 && lineSpan[i - 1] != '\\') || i == 0))
            {
                quoteCount++;
            }

            if ((ch == ';' || ch == '#') && (quoteCount % 2) == 0 && (backslashCount % 2) == 0)
            {
                end = i;
                break;
            }

            if (ch == '\\')
            {
                backslashCount++;
            }
            else
            {
                backslashCount = 0;
            }
        }

        // Trim trailing whitespace.
        while (end > 0 && IsAsciiWhitespace((char)lineSpan[end - 1]))
        {
            end--;
        }

        return line[..end];
    }

    private ReadOnlyMemory<byte> ParseName(ReadOnlyMemory<byte> line, out int valueStartIndex)
    {
        ReadOnlySpan<byte> lineSpan = line.Span;

        int nameEnd = 0;
        while (nameEnd < line.Length && IsNameChar((char)lineSpan[nameEnd]))
        {
            nameEnd++;
        }

        if (nameEnd == 0)
        {
            // C (config_parse.c:312-315): set_parse_error(reader, 0, ...) —
            // wrapped, no column.
            throw ParseError(0, "invalid configuration key");
        }

        int valueStart = nameEnd;
        while (valueStart < lineSpan.Length && IsAsciiWhitespace((char)lineSpan[valueStart]))
        {
            valueStart++;
        }

        if (valueStart < lineSpan.Length)
        {
            if (lineSpan[valueStart] == '=')
            {
                valueStartIndex = valueStart + 1;
                return line[..nameEnd];
            }

            throw ParseError(0, "invalid configuration key");
        }

        valueStartIndex = -1;
        return line[..nameEnd];
    }

    private static bool UnescapeLine(ReadOnlySpan<byte> input, PooledByteBufferWriter valueWriter)
    {
        int i = 0;

        while (i < input.Length)
        {
            byte c = input[i];
            if (c == '"')
            {
                // Quote toggling is tracked by StripComments; here we just drop them.
                i++;
                continue;
            }

            if (c != '\\')
            {
                valueWriter.Write(c);
                i++;
                continue;
            }

            // Backslash — check the next char.
            i++;
            if (i >= input.Length)
            {
                // Trailing backslash at end-of-line → multiline continuation.
                return true;
            }

            char escaped = (char)input[i];
            char unescaped = escaped switch
            {
                'n' => '\n',
                't' => '\t',
                'b' => '\b',
                '"' => '"',
                '\\' => '\\',
                // C (config_parse.c:402-406): "invalid escape at %s" — the
                // message is NOT wrapped and prints the remainder of the line
                // starting at the offending character.
                _ => throw new GitException(
                    GitErrorCode.Error,
                    $"invalid escape at {Encoding.UTF8.GetString(input[i..])}",
                    GitErrorCategory.Config),
            };

            valueWriter.Write((byte)unescaped);
            i++;
        }

        return false;
    }

    private void ParseMultilineVariable(PooledByteBufferWriter writer, int inQuotes)
    {
        bool multiline = true;
        int currentQuotes = inQuotes;

        while (multiline)
        {
            if (!AdvanceLine())
            {
                // EOF — end the value (C config_parse.c:350-353: line[0] == '\0').
                break;
            }

            ReadOnlyMemory<byte> raw = CurrentLine;
            ReadOnlyMemory<byte> stripped = StripComments(raw, currentQuotes, out int quoteCount);
            if (stripped.IsEmpty)
            {
                // C (config_parse.c:355-361): a blank or comment-only continuation line is stripped to "" and SKIPPED ("pretend it didn't exist") — it does NOT
                // terminate the value; only EOF does.
                currentQuotes = quoteCount;
                continue;
            }

            bool isMulti = UnescapeLine(stripped.Span, writer);
            multiline = isMulti;
            currentQuotes = quoteCount;
        }
    }

    private GitException ParseError(int col, string message)
    {
        // C (config_parse.c:15-25): the column is appended only when col != 0.
        string suffix = col != 0 ? $", column {col}" : string.Empty;
        return new GitException(
            GitErrorCode.Error,
            $"failed to parse config file: {message} (in {_path}:{_lineNum}{suffix})",
            GitErrorCategory.Config);
    }

    private static bool IsKeyChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-';

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-';

    /// <summary>
    /// Matches C's <c>git__isspace</c> (ctype_compat.h:43-47): ASCII
    /// whitespace only — space, <c>\t</c>, <c>\n</c>, <c>\f</c>, <c>\r</c>,
    /// <c>\v</c>. Unlike <see cref="char.IsWhiteSpace(char)"/>, this does NOT
    /// treat Unicode whitespace (U+00A0, U+2003, U+0085, …) as a separator.
    /// </summary>
    private static bool IsAsciiWhitespace(char c) => c is ' ' or (>= '\t' and <= '\r');
}
