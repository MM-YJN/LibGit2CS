// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Buffers.Text;
using System.Text;

using LibGit2CS.Core;

using Xdiff;
using Xdiff.Emit;

namespace LibGit2CS.Diff;

/// <summary>
/// Bridges libgit2 diff options/types to the <see cref="Xdiff"/> library and
/// converts its structured <see cref="DiffResult"/> back into libgit2
/// <see cref="GitDiffHunk"/>/<see cref="GitDiffLine"/>. Managed port of
/// <c>src/libgit2/diff_xdiff.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// The C original (<c>git_xdiff_cb</c>, diff_xdiff.c:112-195) is callback-driven:
/// xdiff invokes <c>out_line</c> with 1, 2, or 3 buffers and the C code parses
/// hunk headers, tracks line numbers, and emits libgit2 lines. The C# xdiff
/// library instead returns a fully materialized <see cref="DiffResult"/>, so
/// this bridge iterates that result and re-derives the no-newline-at-EOF
/// markers (the C 3-buffer convention) by inspecting whether each line's
/// content ends with <c>\n</c>.
/// </para>
/// <para>
/// <b>EOF-newline handling</b>: xdiff's <c>xdl_emit_diffrec</c>
/// (deps/xdiff/xutils.c:39-59) appends a third buffer
/// <c>"\n\\ No newline at end of file\n"</c> whenever a record lacks a
/// trailing newline. libgit2 maps the third buffer to a follow-up
/// <see cref="GitDiffLine"/> with origin <see cref="GitDiffLineOrigin.ContextEofnl"/>
/// / <see cref="GitDiffLineOrigin.DelEofnl"/> / <see cref="GitDiffLineOrigin.AddEofnl"/>
/// (derived from the preceding line's sigil). The bridge reproduces this: for
/// any <see cref="GitDiffLine.Content"/> that does not end in <c>\n</c>, it emits
/// the regular line followed by an EOFNL marker line.
/// </para>
/// </remarks>
internal static class XdiffBridge
{
    /// <summary>
    /// The exact EOFNL marker bytes emitted by xdiff's
    /// <c>xdl_emit_diffrec</c> (deps/xdiff/xutils.c:49). Includes the leading
    /// <c>\n</c> (the preceding record had none) and the trailing <c>\n</c>.
    /// </summary>
    private static ReadOnlyMemory<byte> EofnlContent { get; } =
        "\n\\ No newline at end of file\n"u8.ToArray();

    /// <summary>
    /// Builds the xdiff-library options that mirror the libgit2
    /// <paramref name="options"/> flags. Maps
    /// <see cref="GitDiffOptionsFlags"/> whitespace/algorithm bits to
    /// <see cref="DiffOptions"/> — matches <c>git_xdiff_init</c>
    /// (diff_xdiff.c:234-261).
    /// </summary>
    /// <param name="options">libgit2 diff options.</param>
    /// <param name="funcnameExtractor">
    /// Optional function-name extractor (from <see cref="DiffDriver"/>); when
    /// non-null, enables funcname capture with regex group extraction. Matches
    /// <c>git_diff_find_context_init</c> + <c>XDL_EMIT_FUNCNAMES</c> in
    /// <c>git_xdiff</c> (diff_xdiff.c:209-216).
    /// </param>
    public static DiffOptions BuildXdiffOptions(
        GitDiffOptions options,
        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)>? funcnameExtractor = null)
    {
        GitDiffOptionsFlags flags = options.Flags;
        WhitespaceMode ws = WhitespaceMode.None;
        if ((flags & GitDiffOptionsFlags.IgnoreWhitespace) != 0)
        {
            ws |= WhitespaceMode.IgnoreAll | WhitespaceMode.IgnoreChanges | WhitespaceMode.IgnoreAtEol;
        }

        if ((flags & GitDiffOptionsFlags.IgnoreWhitespaceChange) != 0)
        {
            ws |= WhitespaceMode.IgnoreChanges;
        }

        if ((flags & GitDiffOptionsFlags.IgnoreWhitespaceEol) != 0)
        {
            ws |= WhitespaceMode.IgnoreAtEol;
        }

        DiffAlgorithm algorithm = DiffAlgorithm.Myers;
        if ((flags & GitDiffOptionsFlags.Patience) != 0)
        {
            algorithm = DiffAlgorithm.Patience;
        }
        else if ((flags & GitDiffOptionsFlags.Minimal) != 0)
        {
            algorithm = DiffAlgorithm.Minimal;
        }

        return new DiffOptions
        {
            Algorithm = algorithm,
            Whitespace = ws,
            IgnoreBlankLines = (flags & GitDiffOptionsFlags.IgnoreBlankLines) != 0,
            IndentHeuristic = (flags & GitDiffOptionsFlags.IndentHeuristic) != 0,
            ContextLines = options.ContextLines,
            InterHunkLines = options.InterHunkLines,
            IncludeFunctionNames = funcnameExtractor is not null,
            FunctionNameExtractor = funcnameExtractor,
        };
    }

    /// <summary>
    /// Computes the per-file diff and invokes the callbacks for each hunk/line.
    /// Matches <c>git_xdiff</c> (diff_xdiff.c:197-232) + <c>git_xdiff_cb</c>
    /// (diff_xdiff.c:112-195).
    /// </summary>
    /// <param name="targetHunkList">Target list to receive the computed hunks.</param>
    /// <param name="oldContent">Old-side file content (raw bytes).</param>
    /// <param name="newContent">New-side file content (raw bytes).</param>
    /// <param name="options">libgit2 diff options.</param>
    /// <param name="funcnameExtractor">Optional funcname extractor.</param>
    public static void Compute(
        List<GitDiffHunk> targetHunkList,
        ReadOnlyMemory<byte> oldContent,
        ReadOnlyMemory<byte> newContent,
        GitDiffOptions options,
        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)>? funcnameExtractor)
    {
        var sink = new GitDiffHunkListSink(targetHunkList, funcnameExtractor is not null);
        DiffOptions xdOptions = BuildXdiffOptions(options, funcnameExtractor);
        Xdiff.Diff.Compute(sink, oldContent, newContent, xdOptions);
    }

    private sealed class GitDiffHunkListSink(List<GitDiffHunk> targetHunkList, bool hasFuncNameExtractor) : HunkSinkBase
    {
        private List<GitDiffLine>? _currentLines;

        // Running line counters reset to the hunk's start values — matches
        // git_xdiff_cb (diff_xdiff.c:147-148).
        private int _oldLineno;
        private int _newLineno;

        public override void BeginHunk()
        {
            _currentLines = new();
            _oldLineno = OldStart;
            _newLineno = NewStart;

            ReadOnlyMemory<byte> header = BuildHunkHeader(this, hasFuncNameExtractor);
            var hunk = new GitDiffHunk(OldStart, OldCount, NewStart, NewCount, header, _currentLines);
            targetHunkList.Add(hunk);
        }

        public override void Line(DiffLineKind kind, ReadOnlyMemory<byte> content, int oldLine, int newLine)
        {
            GitDiffLine line = EmitLine(kind, content, ref _oldLineno, ref _newLineno);
            _currentLines?.Add(line);

            // No-newline-at-EOF: when a record lacks a trailing newline,
            // emit the EOFNL follow-up. Matches the C 3-buffer path
            // (diff_xdiff.c:173-192 + xutils.c:48-52).
            ReadOnlySpan<byte> span = content.Span;
            if (span.Length == 0 || span[^1] != (byte)'\n')
            {
                GitDiffLineOrigin eofnlOrigin = kind switch
                {
                    DiffLineKind.Context => GitDiffLineOrigin.ContextEofnl,
                    DiffLineKind.Addition => GitDiffLineOrigin.DelEofnl,
                    DiffLineKind.Deletion => GitDiffLineOrigin.AddEofnl,
                    _ => GitDiffLineOrigin.ContextEofnl,
                };

                line = EmitEofnlLine(eofnlOrigin, ref _oldLineno, ref _newLineno);
                _currentLines?.Add(line);
            }
        }
    }

    /// <summary>
    /// Emits one regular line, advancing the running line counters per
    /// <c>diff_update_lines</c> (diff_xdiff.c:67-110).
    /// </summary>
    private static GitDiffLine EmitLine(
        DiffLineKind kind,
        ReadOnlyMemory<byte> content,
        ref int oldLineno,
        ref int newLineno)
    {
        int lineCount = CountNewlines(content.Span);
        GitDiffLineOrigin origin = kind switch
        {
            DiffLineKind.Context => GitDiffLineOrigin.Context,
            DiffLineKind.Addition => GitDiffLineOrigin.Addition,
            DiffLineKind.Deletion => GitDiffLineOrigin.Deletion,
            _ => GitDiffLineOrigin.Context,
        };

        int oldLine;
        int newLine;
        switch (kind)
        {
            case DiffLineKind.Addition:
                oldLine = -1;
                newLine = newLineno;
                newLineno += lineCount;
                break;
            case DiffLineKind.Deletion:
                oldLine = oldLineno;
                newLine = -1;
                oldLineno += lineCount;
                break;
            default: // Context
                oldLine = oldLineno;
                newLine = newLineno;
                oldLineno += lineCount;
                newLineno += lineCount;
                break;
        }

        return new GitDiffLine(origin, oldLine, newLine, lineCount, content);
    }

    /// <summary>
    /// Emits the EOFNL marker line. Line-number advancement mirrors
    /// <c>diff_update_lines</c> for the EOFNL origins
    /// (diff_xdiff.c:83-102): DEL_EOFNL acts like ADDITION,
    /// ADD_EOFNL acts like DELETION, CONTEXT_EOFNL acts like CONTEXT.
    /// </summary>
    private static GitDiffLine EmitEofnlLine(
        GitDiffLineOrigin eofnlOrigin,
        ref int oldLineno,
        ref int newLineno)
    {
        ReadOnlyMemory<byte> content = EofnlContent;
        int lineCount = CountNewlines(content.Span);

        int oldLine;
        int newLine;
        switch (eofnlOrigin)
        {
            case GitDiffLineOrigin.DelEofnl:
                oldLine = -1;
                newLine = newLineno;
                newLineno += lineCount;
                break;
            case GitDiffLineOrigin.AddEofnl:
                oldLine = oldLineno;
                newLine = -1;
                oldLineno += lineCount;
                break;
            default: // ContextEofnl
                oldLine = oldLineno;
                newLine = newLineno;
                oldLineno += lineCount;
                newLineno += lineCount;
                break;
        }

        return new GitDiffLine(eofnlOrigin, oldLine, newLine, lineCount, content);
    }

    private static int CountNewlines(ReadOnlySpan<byte> span)
    {
        int count = 0;
        foreach (byte b in span)
        {
            if (b == (byte)'\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary> Builds the raw hunk header bytes (<c>@@ -old,cnt +new,cnt @@[ funcname]\n</c>). Mirrors xdiff's <c>xdl_format_hunk_hdr</c> assembly
    /// (deps/xdiff/xutils.c:343-390): the funcname is capped so the trailing <c>\n</c> always fits the 128-byte buffer. The assembled header then goes through
    /// the same post-processing <c>git_xdiff_cb</c> applies to <c>git_diff_hunk::header</c> (diff_xdiff.c:126-140): capped at <see cref="HunkHeaderSize"/> - 1
    /// bytes, truncated at the last valid UTF-8 boundary (<see cref="Utf8Helpers.ValidBufLength"/>, the port of <c>git_utf8_valid_buf_length</c>), and a
    /// <c>\n</c> spliced back into the vacated position when the truncation removed one. </summary> <remarks> Internal (not private) so the 128-byte cap —
    /// reachable end-to-end only with ~10-digit line numbers — is unit-testable directly. </remarks>
    internal static ReadOnlyMemory<byte> BuildHunkHeader(HunkSinkBase sink, bool hasFuncname)
    {
        Span<byte> buf = stackalloc byte[HunkHeaderSize];
        int pos = 0;

        AppendAscii(buf, ref pos, "@@ -");
        AppendRange(buf, ref pos, sink.OldStart, sink.OldCount);
        AppendAscii(buf, ref pos, " +");
        AppendRange(buf, ref pos, sink.NewStart, sink.NewCount);
        AppendAscii(buf, ref pos, " @@");

        if (hasFuncname && sink.Func is { Length: > 0 } funcName)
        {
            buf[pos++] = (byte)' ';
            int n = Math.Min(funcName.Length, buf.Length - pos - 1);
            funcName.Span[..n].CopyTo(buf[pos..]);
            pos += n;
        }

        buf[pos++] = (byte)'\n';

        // Cap at GIT_DIFF_HUNK_HEADER_SIZE - 1 (diff_xdiff.c:129-131), then
        // sanitize invalid UTF-8: truncate at the last valid boundary and
        // splice '\n' back if the truncation removed it (diff_xdiff.c:133-138).
        int headerLen = Math.Min(pos, HunkHeaderSize - 1);
        int validLen = Utf8Helpers.ValidBufLength(buf[..headerLen]);
        if (validLen < headerLen)
        {
            buf[validLen] = (byte)'\n';
            headerLen = validLen + 1;
        }

        return buf[..headerLen].ToArray();
    }

    private const int HunkHeaderSize = 128; // GIT_DIFF_HUNK_HEADER_SIZE

    private static void AppendAscii(Span<byte> buf, ref int pos, ReadOnlySpan<char> text)
    {
        int n = Encoding.ASCII.GetBytes(text, buf[pos..]);
        pos += n;
    }

    private static void AppendRange(Span<byte> buf, ref int pos, int start, int count)
    {
        Utf8Formatter.TryFormat(start, buf[pos..], out int written);
        pos += written;
        if (count != 1)
        {
            buf[pos++] = (byte)',';
            Utf8Formatter.TryFormat(count, buf[pos..], out written);
            pos += written;
        }
    }

    /// <summary> Renders the bytes of a <see cref="GitDiffLine"/> as git does: for context/addition/deletion lines the origin sigil precedes the content; for
    /// EOFNL/binary/header lines only the content is emitted. Used by the printer path. Matches <c>git_diff_print_callback__to_buf</c> (diff_print.c:794-812).
    /// </summary> <remarks> Byte sink: writes the origin sigil as a single byte then the raw content bytes — no decode/re-encode round-trip. </remarks>
    public static void RenderLine(IBufferWriter<byte> writer, in GitDiffLine line)
    {
        if (line.Origin is GitDiffLineOrigin.Addition or GitDiffLineOrigin.Deletion or GitDiffLineOrigin.Context)
        {
            Span<byte> sigil = writer.GetSpan(1);
            sigil[0] = (byte)line.Origin;
            writer.Advance(1);
        }

        writer.Write(line.Content.Span);
    }
}
