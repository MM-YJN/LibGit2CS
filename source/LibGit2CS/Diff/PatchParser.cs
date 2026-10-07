// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Collections.Immutable;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Utils;

namespace LibGit2CS.Diff;

/// <summary> Unified-diff parser. Managed port of <c>src/libgit2/patch_parse.c</c> (1,239 LOC) — reads a patch from raw bytes into a <see cref="ParsedPatch"/>.
/// </summary> <remarks> <para> Handles: <c>diff --git</c> extended headers (<c>index</c>, <c>similarity</c>, <c>old mode</c>/<c>new mode</c>, <c>new file
/// mode</c>/<c>deleted file mode</c>, <c>rename from</c>/<c>rename to</c>, <c>copy from</c>/<c>copy to</c>), <c>---</c>/<c>+++</c> path lines, <c>@@</c> hunk
/// headers, hunk bodies (context/addition/deletion/no-newline-at-EOF), binary patches (literal + delta base85), and "Binary files differ" noshow headers.
/// </para> <para> The header parser is a 24-transition × 10-state state machine (<c>transitions[]</c> table, <c>parse_header_git</c> driver). Faithful 1:1 port
/// of the C control flow including the best-effort path recovery on ambiguous <c>diff --git</c> lines. </para> <para> <b>Byte domain</b>: the parse input is
/// raw <see cref="ReadOnlyMemory{T}"/> bytes exactly as in C — no string intermediate. Parsed paths are owned byte buffers (C's <c>git_str</c> copies); the
/// hunk header is a byte copy of the input slice (C's <c>memcpy</c> into <c>char[GIT_DIFF_HUNK_HEADER_SIZE]</c>). The <see cref="FromBuffer(string, GitPatchParseOptions)"/> overload
/// is a UTF-8 convenience tier; binary / invalid-UTF-8 patches must use the byte overload. </para> </remarks>
internal static class PatchParser
{
    private const int HunkHeaderSize = 128; // GIT_DIFF_HUNK_HEADER_SIZE
    private const int OidMinPrefixLen = 4;  // GIT_OID_MINPREFIXLEN

    /// <summary>
    /// Parses a single patch from raw bytes. Matches
    /// <c>git_patch_from_buffer</c> (patch_parse.c:1222-1238).
    /// </summary>
    public static ParsedPatch? FromBuffer(ReadOnlyMemory<byte> content, GitPatchParseOptions? options = null)
    {
        GitPatchParseOptions opts = options ?? new GitPatchParseOptions();
        return Parse(content, opts);
    }

    /// <summary>
    /// Parses a single patch from a UTF-8 text string. Convenience overload:
    /// the string is UTF-8-encoded into the byte parse domain. Binary or
    /// invalid-UTF-8 patches must use
    /// <see cref="FromBuffer(ReadOnlyMemory{byte}, GitPatchParseOptions?)"/>.
    /// </summary>
    public static ParsedPatch? FromBuffer(string content, GitPatchParseOptions? options = null)
        => FromBuffer(content is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(content), options);

    /// <summary>
    /// Parses a single patch from <paramref name="content"/>. Matches
    /// <c>git_patch_parse</c> (patch_parse.c:1169-1220). Returns null on parse
    /// failure (no patch found / corrupt header). For multi-file diffs, use
    /// <see cref="DiffParsed.FromBuffer"/>, which shares one cursor.
    /// </summary>
    internal static ParsedPatch? Parse(ReadOnlyMemory<byte> content, GitPatchParseOptions options)
    {
        var ctx = new ParseContext();
        ctx.Init(content);
        return Parse(ctx, options);
    }

    /// <summary>
    /// Parses a single patch from the given parse context, advancing the cursor
    /// in place. Matches <c>git_patch_parse</c> with an external context (the
    /// <c>git_patch_parse_ctx *</c> variant). The multi-patch loop in
    /// <see cref="DiffParsed.FromBuffer"/> uses this to share one cursor.
    /// </summary>
    internal static ParsedPatch? Parse(ParseContext ctx, GitPatchParseOptions options)
    {
        var patch = new ParsedPatch(options);

        if (ParsePatchHeader(patch, ctx) < 0 ||
            ParsePatchBody(patch, ctx) < 0 ||
            CheckPatch(patch) < 0)
        {
            return null;
        }

        return patch;
    }

    // ===== Error helper =====

    private static GitException Err(string message, int lineNum)
    {
        // C (patch_parse.c:36-46, git_parse_err): git_error_vset(GIT_ERROR_PATCH,
        // "...") with the precise message + line number. The port surfaces the
        // error as a GIT_ERROR_PATCH-class GitException at the parse boundary
        //  instead of collapsing every failure to null.
        throw new GitException(
            GitErrorCode.Error,
            $"{message} at line {lineNum}",
            GitErrorCategory.Patch);
    }

    // ===== Header path parsing =====

    /// <summary>Matches <c>header_path_len</c> (patch_parse.c:48-66).</summary>
    private static int HeaderPathLen(ParseContext ctx)
    {
        bool quoted = ctx.Contains("\""u8);
        int len = quoted ? 1 : 0; // include the opening quote in len
        bool inquote = false;

        for (; len < ctx.LineLength; len++)
        {
            byte ch = ctx.Line[len];

            if (!quoted && IsSpace(ch))
            {
                break;
            }

            if (quoted && !inquote && ch == (byte)'"')
            {
                len++;
                break;
            }

            inquote = !inquote && ch == (byte)'\\';
        }

        return len;
    }

    /// <summary>Matches <c>parse_header_path_buf</c> (patch_parse.c:68-90).</summary>
    private static int ParseHeaderPath(out ReadOnlyMemory<byte>? path, ParseContext ctx, int pathLen)
    {
        path = null;
        if (pathLen <= 0)
        {
            throw Err("patch contains empty path", ctx.LineNumber);
        }

        ReadOnlySpan<byte> span = ctx.Line.Slice(0, pathLen);
        ctx.AdvanceChars(pathLen);

        // rtrim (git_str_rtrim — ASCII only; TrimEnd also removed non-ASCII
        // whitespace)
        span = AsciiText.Rtrim(span);

        // unquote if wrapped in "..."
        if (span.Length > 0 && span[0] == (byte)'"')
        {
            path = UnquotePath(span, ctx.LineNumber);
        }
        else
        {
            // C copies the path bytes into a git_str; the owned ToArray matches.
            path = span.ToArray();
        }

        // squash slashes (git_fs_path_squash_slashes)
        path = SquashSlashes(path.Value.Span);

        if (path.Value.Length == 0)
        {
            throw Err("patch contains empty path", ctx.LineNumber);
        }

        return 0;
    }

    /// <summary>Matches <c>parse_header_path</c> (patch_parse.c:92-104).</summary>
    private static int ParseHeaderPath(out ReadOnlyMemory<byte>? path, ParseContext ctx)
        => ParseHeaderPath(out path, ctx, HeaderPathLen(ctx));

    /// <summary> Unquotes a double-quoted path. Matches <c>git_str_unquote</c> (str.c:990-1062): octal escapes ("caf\303\251") decode to their
    /// raw bytes, control escapes ("\n") to the control byte, and invalid escapes are rejected (never copied verbatim or parsed silently).
    /// </summary> <remarks> Byte domain: octal escapes (<c>\303</c>)
    /// produce the raw byte <c>0xC3</c> exactly as C writes into its <c>git_str</c>; decoding to chars would produce U+00C3, which re-encodes to UTF-8 <c>C3
    /// 83</c> (mojibake). </remarks> <exception cref="GitException">Invalid quoted path (Patch category).</exception>
    private static byte[] UnquotePath(ReadOnlySpan<byte> quoted, int lineNumber)
    {
        // The caller has already rtrimmed; the closing quote must be the
        // last character (str.c:1001-1004).
        if (quoted.Length < 2 || quoted[0] != (byte)'"' || quoted[^1] != (byte)'"')
        {
            throw Err("invalid quoted line", lineNumber);
        }

        ReadOnlySpan<byte> inner = quoted.Slice(1, quoted.Length - 2);
        byte[] result = new byte[inner.Length];
        int n = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            byte ch = inner[i];
            if (ch != (byte)'\\')
            {
                result[n++] = ch;
                continue;
            }

            // A backslash as the last inner character (str.c:1013-1015,
            // j == size-2) is invalid.
            if (i == inner.Length - 1)
            {
                throw Err("invalid quoted line", lineNumber);
            }

            ch = inner[++i];
            switch (ch)
            {
                // \" or \\ simply copy the char in.
                case (byte)'"':
                case (byte)'\\':
                    break;

                // Add the appropriate escaped char.
                case (byte)'a': ch = 0x07; break;
                case (byte)'b': ch = 0x08; break;
                case (byte)'f': ch = 0x0C; break;
                case (byte)'n': ch = (byte)'\n'; break;
                case (byte)'r': ch = (byte)'\r'; break;
                case (byte)'t': ch = (byte)'\t'; break;
                case (byte)'v': ch = 0x0B; break;

                // \xyz digits convert to the byte (str.c:1028-1045).
                case (byte)'0':
                case (byte)'1':
                case (byte)'2':
                case (byte)'3':
                    if (i + 2 >= inner.Length)
                    {
                        throw Err($"truncated quoted character \\{(char)ch}", lineNumber);
                    }

                    byte d1 = inner[i + 1];
                    byte d2 = inner[i + 2];
                    if (d1 is < (byte)'0' or > (byte)'7' || d2 is < (byte)'0' or > (byte)'7')
                    {
                        throw Err($"truncated quoted character \\{(char)ch}{(char)d1}{(char)d2}", lineNumber);
                    }

                    ch = (byte)(((ch - (byte)'0') << 6) | ((d1 - (byte)'0') << 3) | (d2 - (byte)'0'));
                    i += 2;
                    break;

                default:
                    throw Err($"invalid quoted character \\{(char)ch}", lineNumber);
            }

            result[n++] = ch;
        }

        return result[..n];
    }

    private static byte[] SquashSlashes(ReadOnlySpan<byte> path)
    {
        // git_fs_path_squash_slashes: collapse consecutive '/' into one.
        byte[] result = new byte[path.Length];
        int n = 0;
        bool prevSlash = false;
        foreach (byte c in path)
        {
            if (c == (byte)'/')
            {
                if (!prevSlash)
                {
                    result[n++] = c;
                }
                prevSlash = true;
            }
            else
            {
                result[n++] = c;
                prevSlash = false;
            }
        }

        return result[..n];
    }

    // ===== --- / +++ path parsers =====

    private static int ParseOldPath(ParsedPatch patch, ParseContext ctx)
    {
        if (patch.OldPath is not null)
        {
            throw Err("patch contains duplicate old path", ctx.LineNumber);
        }

        // line_len - 1 (consume the rest of the line minus the trailing \n)
        int len = ctx.LineLength - 1;
        if (ParseHeaderPath(out ReadOnlyMemory<byte>? path, ctx, len) < 0)
        {
            return -1;
        }

        patch.OldPath = path;
        return 0;
    }

    private static int ParseNewPath(ParsedPatch patch, ParseContext ctx)
    {
        if (patch.NewPath is not null)
        {
            throw Err("patch contains duplicate new path", ctx.LineNumber);
        }

        int len = ctx.LineLength - 1;
        if (ParseHeaderPath(out ReadOnlyMemory<byte>? path, ctx, len) < 0)
        {
            return -1;
        }

        patch.NewPath = path;
        return 0;
    }

    // ===== Mode / OID / index parsers =====

    private static int ParseMode(out ushort mode, ParseContext ctx)
    {
        mode = 0;
        if (!ctx.AdvanceDigit(out long m, 8))
        {
            throw Err("invalid file mode", ctx.LineNumber);
        }

        if (m > ushort.MaxValue)
        {
            return -1;
        }

        mode = (ushort)m;
        return 0;
    }

    private static int ParseOid(out GitOid oid, out int oidLen, ParseContext ctx, GitHashAlgorithmKind oidType)
    {
        oid = default;
        oidLen = 0;

        int hexsize = oidType == GitHashAlgorithmKind.Sha256 ? 64 : 40;

        // Scan hex chars up to hexsize.
        int len = 0;
        ReadOnlySpan<byte> line = ctx.Line;
        for (; len < line.Length && len < hexsize; len++)
        {
            if (!IsHexDigit(line[len]))
            {
                break;
            }
        }

        if (len < OidMinPrefixLen || len > hexsize)
        {
            throw Err("invalid hex formatted object id", ctx.LineNumber);
        }

        if (!GitOid.TryParse(line.Slice(0, len), oidType, out oid))
        {
            throw Err("invalid hex formatted object id", ctx.LineNumber);
        }

        ctx.AdvanceChars(len);
        oidLen = len;
        return 0;
    }

    private static int ParseIndex(ParsedPatch patch, ParseContext ctx)
    {
        if (ParseOid(out GitOid oldId, out int oldAbbrev, ctx, patch.Options.OidType) < 0 ||
            !ctx.AdvanceExpected(".."u8) ||
            ParseOid(out GitOid newId, out int newAbbrev, ctx, patch.Options.OidType) < 0)
        {
            return -1;
        }

        patch.Delta.OldFile.Id = oldId;
        patch.Delta.OldFile.IdAbbrev = oldAbbrev;
        patch.Delta.NewFile.Id = newId;
        patch.Delta.NewFile.IdAbbrev = newAbbrev;

        // Optional " <mode>" suffix.
        if (ctx.Peek(out byte c) && c == (byte)' ')
        {
            ctx.AdvanceChars(1);
            if (ParseMode(out ushort mode, ctx) < 0)
            {
                return -1;
            }

            if (patch.Delta.NewFile.Mode == 0)
            {
                patch.Delta.NewFile.Mode = (GitFileMode)mode;
            }

            if (patch.Delta.OldFile.Mode == 0)
            {
                patch.Delta.OldFile.Mode = (GitFileMode)mode;
            }
        }

        return 0;
    }

    private static int ParseOldMode(ParsedPatch patch, ParseContext ctx)
        => ParseMode(out ushort m, ctx) < 0 ? -1 : SetMode(patch, isOld: true, m);

    private static int ParseNewMode(ParsedPatch patch, ParseContext ctx)
        => ParseMode(out ushort m, ctx) < 0 ? -1 : SetMode(patch, isOld: false, m);

    private static int SetMode(ParsedPatch patch, bool isOld, ushort mode)
    {
        if (isOld)
        {
            patch.Delta.OldFile.Mode = (GitFileMode)mode;
        }
        else
        {
            patch.Delta.NewFile.Mode = (GitFileMode)mode;
        }
        return 0;
    }

    private static int ParseDeletedFileMode(ParsedPatch patch, ParseContext ctx)
    {
        patch.Delta.NewFile.Path = null;
        patch.Delta.Status = GitDeltaStatus.Deleted;
        patch.Delta.FileCount = 1;
        return ParseMode(out ushort m, ctx) < 0 ? -1 : SetMode(patch, isOld: true, m);
    }

    private static int ParseNewFileMode(ParsedPatch patch, ParseContext ctx)
    {
        patch.Delta.OldFile.Path = null;
        patch.Delta.Status = GitDeltaStatus.Added;
        patch.Delta.FileCount = 1;
        return ParseMode(out ushort m, ctx) < 0 ? -1 : SetMode(patch, isOld: false, m);
    }

    // ===== Rename / copy / similarity =====

    private static int ParseRenameOrCopy(out ReadOnlyMemory<byte>? path, ParseContext ctx)
    {
        path = null;
        return ParseHeaderPath(out path, ctx, HeaderPathLen(ctx));
    }

    private static int ParseRenameFrom(ParsedPatch patch, ParseContext ctx)
    {
        patch.Delta.Status = GitDeltaStatus.Renamed;
        int err = ParseRenameOrCopy(out ReadOnlyMemory<byte>? path, ctx);
        if (err < 0)
        {
            return err;
        }

        patch.RenameOldPath = path;
        return 0;
    }

    private static int ParseRenameTo(ParsedPatch patch, ParseContext ctx)
    {
        patch.Delta.Status = GitDeltaStatus.Renamed;
        int err = ParseRenameOrCopy(out ReadOnlyMemory<byte>? path, ctx);
        if (err < 0)
        {
            return err;
        }

        patch.RenameNewPath = path;
        return 0;
    }

    private static int ParseCopyFrom(ParsedPatch patch, ParseContext ctx)
    {
        patch.Delta.Status = GitDeltaStatus.Copied;
        int err = ParseRenameOrCopy(out ReadOnlyMemory<byte>? path, ctx);
        if (err < 0)
        {
            return err;
        }

        patch.RenameOldPath = path;
        return 0;
    }

    private static int ParseCopyTo(ParsedPatch patch, ParseContext ctx)
    {
        patch.Delta.Status = GitDeltaStatus.Copied;
        int err = ParseRenameOrCopy(out ReadOnlyMemory<byte>? path, ctx);
        if (err < 0)
        {
            return err;
        }

        patch.RenameNewPath = path;
        return 0;
    }

    private static int ParsePercent(out ushort value, ParseContext ctx)
    {
        value = 0;
        if (!ctx.AdvanceDigit(out long val, 10))
        {
            return -1;
        }

        if (!ctx.AdvanceExpected("%"u8))
        {
            return -1;
        }

        if (val is < 0 or > 100)
        {
            return -1;
        }

        value = (ushort)val;
        return 0;
    }

    private static int ParseSimilarity(ParsedPatch patch, ParseContext ctx)
    {
        if (ParsePercent(out ushort sim, ctx) < 0)
        {
            throw Err("invalid similarity percentage", ctx.LineNumber);
        }

        patch.Delta.Similarity = sim;
        return 0;
    }

    private static int ParseDissimilarity(ParsedPatch patch, ParseContext ctx)
    {
        if (ParsePercent(out ushort dissim, ctx) < 0)
        {
            throw Err("invalid similarity percentage", ctx.LineNumber);
        }

        patch.Delta.Similarity = (ushort)(100 - dissim);
        return 0;
    }

    // ===== diff --git start line =====

    private static int ParseStart(ParsedPatch patch, ParseContext ctx)
    {
        if (ParseHeaderPath(out ReadOnlyMemory<byte>? oldPath, ctx) < 0)
        {
            throw Err("corrupt old path in git diff header", ctx.LineNumber);
        }

        patch.HeaderOldPath = oldPath;

        if (!ctx.AdvanceWs() || ParseHeaderPath(out ReadOnlyMemory<byte>? newPath, ctx) < 0)
        {
            throw Err("corrupt new path in git diff header", ctx.LineNumber);
        }

        patch.HeaderNewPath = newPath;

        // Best-effort recovery: if trailing content remains (not just \n or \r\n),
        // give up on the header paths and fall back to ---/+++ lines.
        if (!ctx.Contains("\n"u8) && !ctx.Contains("\r\n"u8))
        {
            ctx.AdvanceChars(ctx.LineLength - 1);
            patch.HeaderOldPath = null;
            patch.HeaderNewPath = null;
        }

        return 0;
    }

    // ===== Header state machine =====

    private enum HeaderState
    {
        Start,
        Diff,
        FileMode,
        Mode,
        Index,
        Path,
        Similarity,
        Rename,
        Copy,
        End,
    }

    private sealed class HeaderTransition
    {
        public byte[] Str { get; init; } = [];
        public HeaderState Expected { get; init; }
        public HeaderState Next { get; init; }
        public Func<ParsedPatch, ParseContext, int>? Fn { get; init; }
    }

    private static readonly ImmutableArray<HeaderTransition> s_transitions =
    [
        // Start
        new() { Str = "diff --git "u8.ToArray(), Expected = HeaderState.Start, Next = HeaderState.Diff, Fn = ParseStart },

        // Mode / filemode
        new() { Str = "deleted file mode "u8.ToArray(), Expected = HeaderState.Diff, Next = HeaderState.FileMode, Fn = ParseDeletedFileMode },
        new() { Str = "new file mode "u8.ToArray(), Expected = HeaderState.Diff, Next = HeaderState.FileMode, Fn = ParseNewFileMode },
        new() { Str = "old mode "u8.ToArray(), Expected = HeaderState.Diff, Next = HeaderState.Mode, Fn = ParseOldMode },
        new() { Str = "new mode "u8.ToArray(), Expected = HeaderState.Mode, Next = HeaderState.End, Fn = ParseNewMode },

        // Index (multiple entry states)
        new() { Str = "index "u8.ToArray(), Expected = HeaderState.FileMode, Next = HeaderState.Index, Fn = ParseIndex },
        new() { Str = "index "u8.ToArray(), Expected = HeaderState.Diff, Next = HeaderState.Index, Fn = ParseIndex },
        new() { Str = "index "u8.ToArray(), Expected = HeaderState.End, Next = HeaderState.Index, Fn = ParseIndex },

        // --- / +++ paths
        new() { Str = "--- "u8.ToArray(), Expected = HeaderState.Diff, Next = HeaderState.Path, Fn = ParseOldPath },
        new() { Str = "--- "u8.ToArray(), Expected = HeaderState.Index, Next = HeaderState.Path, Fn = ParseOldPath },
        new() { Str = "--- "u8.ToArray(), Expected = HeaderState.FileMode, Next = HeaderState.Path, Fn = ParseOldPath },
        new() { Str = "+++ "u8.ToArray(), Expected = HeaderState.Path, Next = HeaderState.End, Fn = ParseNewPath },

        // Binary terminators (fn=null → stop header, go to END)
        new() { Str = "GIT binary patch"u8.ToArray(), Expected = HeaderState.Index, Next = HeaderState.End, Fn = null },
        new() { Str = "Binary files "u8.ToArray(), Expected = HeaderState.Index, Next = HeaderState.End, Fn = null },

        // Similarity / dissimilarity
        new() { Str = "similarity index "u8.ToArray(), Expected = HeaderState.End, Next = HeaderState.Similarity, Fn = ParseSimilarity },
        new() { Str = "similarity index "u8.ToArray(), Expected = HeaderState.Diff, Next = HeaderState.Similarity, Fn = ParseSimilarity },
        new() { Str = "dissimilarity index "u8.ToArray(), Expected = HeaderState.Diff, Next = HeaderState.Similarity, Fn = ParseDissimilarity },

        // Rename / copy
        new() { Str = "rename from "u8.ToArray(), Expected = HeaderState.Similarity, Next = HeaderState.Rename, Fn = ParseRenameFrom },
        new() { Str = "rename old "u8.ToArray(), Expected = HeaderState.Similarity, Next = HeaderState.Rename, Fn = ParseRenameFrom },
        new() { Str = "copy from "u8.ToArray(), Expected = HeaderState.Similarity, Next = HeaderState.Copy, Fn = ParseCopyFrom },
        new() { Str = "rename to "u8.ToArray(), Expected = HeaderState.Rename, Next = HeaderState.End, Fn = ParseRenameTo },
        new() { Str = "rename new "u8.ToArray(), Expected = HeaderState.Rename, Next = HeaderState.End, Fn = ParseRenameTo },
        new() { Str = "copy to "u8.ToArray(), Expected = HeaderState.Copy, Next = HeaderState.End, Fn = ParseCopyTo },

        // Next-patch separators (fn=null, Next=0 → stop header without advancing)
        new() { Str = "diff --git "u8.ToArray(), Expected = HeaderState.End, Next = 0, Fn = null },
        new() { Str = "@@ -"u8.ToArray(), Expected = HeaderState.End, Next = 0, Fn = null },
        new() { Str = "-- "u8.ToArray(), Expected = HeaderState.Index, Next = 0, Fn = null },
        new() { Str = "-- "u8.ToArray(), Expected = HeaderState.End, Next = 0, Fn = null },
    ];

    /// <summary>Matches <c>parse_header_git</c> (patch_parse.c:436-496).</summary>
    private static int ParseHeaderGit(ParsedPatch patch, ParseContext ctx)
    {
        HeaderState state = HeaderState.Start;

        for (; ctx.RemainLength > 0; ctx.AdvanceLine())
        {
            // Line must end with \n.
            if (ctx.LineLength == 0 || ctx.Line[ctx.LineLength - 1] != (byte)'\n')
            {
                break;
            }

            bool found = false;
            foreach (HeaderTransition t in s_transitions)
            {
                if (t.Expected != state || !ctx.Contains(t.Str))
                {
                    continue;
                }

                state = t.Next;

                // Do not advance if this is a patch separator (fn == null).
                if (t.Fn is null)
                {
                    return 0; // goto done
                }

                ctx.AdvanceChars(t.Str.Length);

                if (t.Fn(patch, ctx) < 0)
                {
                    return -1;
                }

                ctx.AdvanceWs();

                // Expect the rest of the line to be just \n.
                if (!ctx.AdvanceExpected("\n"u8) || ctx.LineLength > 0)
                {
                    throw Err("trailing data", ctx.LineNumber);
                }

                found = true;
                break;
            }

            if (!found)
            {
                throw Err("invalid patch header", ctx.LineNumber);
            }
        }

        if (state != HeaderState.End)
        {
            throw Err("unexpected header line", ctx.LineNumber);
        }

        return 0;
    }

    // ===== Hunk parsing =====

    private static bool ParseInt(out int value, ParseContext ctx)
    {
        value = 0;
        if (!ctx.AdvanceDigit(out long num, 10) || num > int.MaxValue)
        {
            return false;
        }

        value = (int)num;
        return true;
    }

    /// <summary>Matches <c>parse_hunk_header</c> (patch_parse.c:509-561).</summary>
    private static int ParseHunkHeader(
        out int oldStart, out int oldLines,
        out int newStart, out int newLines,
        out ReadOnlyMemory<byte> header,
        ParseContext ctx)
    {
        oldStart = 0;
        oldLines = 1;
        newStart = 0;
        newLines = 1;
        header = ReadOnlyMemory<byte>.Empty;

        int headerStartOffset = ctx.ContentLength - ctx.RemainLength;

        if (!ctx.AdvanceExpected("@@ -"u8) || !ParseInt(out oldStart, ctx))
        {
            goto Fail;
        }

        if (ctx.Peek(out byte c) && c == (byte)',')
        {
            if (!ctx.AdvanceExpected(","u8) || !ParseInt(out oldLines, ctx))
            {
                goto Fail;
            }
        }

        if (!ctx.AdvanceExpected(" +"u8) || !ParseInt(out newStart, ctx))
        {
            goto Fail;
        }

        if (ctx.Peek(out c) && c == (byte)',')
        {
            if (!ctx.AdvanceExpected(","u8) || !ParseInt(out newLines, ctx))
            {
                goto Fail;
            }
        }

        if (!ctx.AdvanceExpected(" @@"u8))
        {
            goto Fail;
        }

        ctx.AdvanceLine();

        if (oldLines == 0 && newLines == 0)
        {
            goto Fail;
        }

        // Compute header bytes (from headerStartOffset to current position).
        // C memcpy's the line into parsed->header[128] — the owned copy here
        // matches (the slice of the caller's buffer must not be retained).
        int headerLen = (ctx.ContentLength - ctx.RemainLength) - headerStartOffset;
        if (headerLen > HunkHeaderSize - 1)
        {
            throw Err("oversized patch hunk header", ctx.LineNumber);
        }

        header = ctx.Content.Slice(headerStartOffset, headerLen).ToArray();

        return 0;

Fail:
        throw Err("invalid patch hunk header", ctx.LineNumber);
    }

    /// <summary>Matches <c>eof_for_origin</c> (patch_parse.c:563-569).</summary>
    private static GitDiffLineOrigin EofForOrigin(GitDiffLineOrigin origin)
        => origin == GitDiffLineOrigin.Addition ? GitDiffLineOrigin.DelEofnl
         : origin == GitDiffLineOrigin.Deletion ? GitDiffLineOrigin.AddEofnl
         : GitDiffLineOrigin.ContextEofnl;

    /// <summary>Matches <c>parse_hunk_body</c> (patch_parse.c:571-715).</summary>
    private static int ParseHunkBody(
        ParsedPatch patch,
        int oldStart, int oldLines,
        int newStart, int newLines,
        int _,
        out int lineCount,
        ParseContext ctx)
    {
        lineCount = 0;
        int remainingOld = oldLines;
        int remainingNew = newLines;
        GitDiffLineOrigin lastOrigin = GitDiffLineOrigin.Context;

        for (;
            ctx.RemainLength > 1 && (remainingOld > 0 || remainingNew > 0) && !ctx.Contains("@@ -"u8);
            ctx.AdvanceLine())
        {
            // Compute line numbers (matches C's overflow-safe arithmetic).
            // C# int arithmetic is unchecked by default, so the overflow must
            // be explicitly checked for the guard to fire (patch_parse.c:592-599).
            int oldLineno, newLineno;
            try
            {
                checked
                {
                    oldLineno = oldStart + oldLines - remainingOld;
                    newLineno = newStart + newLines - remainingNew;
                }
            }
            catch (OverflowException)
            {
                throw Err("unrepresentable line count", ctx.LineNumber);
            }

            if (ctx.LineLength == 0 || ctx.Line[ctx.LineLength - 1] != (byte)'\n')
            {
                throw Err("invalid patch instruction", ctx.LineNumber);
            }

            ctx.Peek(out byte c);
            int prefix = 1;
            GitDiffLineOrigin origin;

            switch (c)
            {
                case (byte)'\n':
                    prefix = 0;
                    goto case (byte)' ';
                case (byte)' ':
                    origin = GitDiffLineOrigin.Context;
                    remainingOld--;
                    remainingNew--;
                    break;
                case (byte)'-':
                    origin = GitDiffLineOrigin.Deletion;
                    remainingOld--;
                    newLineno = -1;
                    break;
                case (byte)'+':
                    origin = GitDiffLineOrigin.Addition;
                    remainingNew--;
                    oldLineno = -1;
                    break;
                case (byte)'\\':
                    // "\ No newline at end of file" — only when no oldlines left.
                    if (remainingOld == 0)
                    {
                        prefix = 0;
                        origin = EofForOrigin(lastOrigin);
                        oldLineno = -1;
                        newLineno = -1;
                        break;
                    }
                    goto default;
                default:
                    throw Err("invalid patch hunk", ctx.LineNumber);
            }

            int contentLen = ctx.LineLength - prefix;
            byte[] contentBytes = ctx.Line.Slice(prefix, contentLen).ToArray();

            patch.Lines.Add(new GitDiffLine(origin, oldLineno, newLineno, 1, contentBytes));
            lineCount++;
            lastOrigin = origin;
        }

        // C rejects ANY
        // non-zero remainder (`if (oldlines || newlines)`, patch_parse.c:671-
        // 676) — a NEGATIVE counter (one side over-consumed) is an error too,
        // and a hunk with more lines of one kind than declared cannot absorb
        // the extra line.
        if (remainingOld != 0 || remainingNew != 0)
        {
            throw Err($"invalid patch hunk, expected {oldLines} old lines and {newLines} new lines", ctx.LineNumber);
        }

        // Trailing "\ No newline at end of file" marker.
        if (ctx.Contains("\\ "u8) && patch.Lines.Count > 0)
        {
            GitDiffLine lastLine = patch.Lines[^1];
            if (lastLine.Content.Length < 1)
            {
                throw Err("last line has no trailing newline", ctx.LineNumber);
            }

            byte[] contentBytes = ctx.Line.ToArray();
            patch.Lines.Add(new GitDiffLine(
                EofForOrigin(lastOrigin), -1, -1, 1, contentBytes));
            lineCount++;
            ctx.AdvanceLine();
        }

        return 0;
    }

    // ===== Patch header / body / binary =====

    /// <summary>Matches <c>parse_patch_header</c> (patch_parse.c:717-766).</summary>
    private static int ParsePatchHeader(ParsedPatch patch, ParseContext ctx)
    {
        for (; ctx.RemainLength > 0; ctx.AdvanceLine())
        {
            // Line too short to be a patch header.
            if (ctx.LineLength < 6)
            {
                continue;
            }

            // Might be a hunk header without a patch header — error.
            if (ctx.Contains("@@ -"u8))
            {
                int lineNum = ctx.LineNumber;
                // Try to parse as hunk header; if it fails, it's just noise.
                if (ParseHunkHeader(out _, out _, out _, out _, out _, ctx) < 0)
                {
                    continue;
                }

                throw Err("invalid hunk header outside patch", lineNum);
            }

            // Buffer too short to contain a patch.
            if (ctx.RemainLength < ctx.LineLength + 6)
            {
                break;
            }

            // A proper git patch.
            if (ctx.Contains("diff --git "u8))
            {
                return ParseHeaderGit(patch, ctx);
            }

            continue;
        }

        // C (patch_parse.c:757-762): "no patch found" is GIT_ENOTFOUND —
        // the parser's null result (the caller treats it as not-a-patch).
        return -1;
    }

    /// <summary>Matches <c>parse_patch_binary_side</c> (patch_parse.c:768-848).</summary>
    private static int ParseBinarySide(out GitBinaryFile? binary, ParseContext ctx)
    {
        binary = null;
        GitBinaryPatchType type;

        if (ctx.Contains("literal "u8))
        {
            type = GitBinaryPatchType.Literal;
            ctx.AdvanceChars(8);
        }
        else if (ctx.Contains("delta "u8))
        {
            type = GitBinaryPatchType.Delta;
            ctx.AdvanceChars(6);
        }
        else
        {
            throw Err("unknown binary delta type", ctx.LineNumber);
        }

        if (!ctx.AdvanceDigit(out long len, 10) || !ctx.AdvanceNl() || len < 0)
        {
            throw Err("invalid binary size", ctx.LineNumber);
        }

        // An unchecked
        // (int) cast would wrap declared lengths >= 2^31 (e.g. 'literal
        // 3000000000' → -1294967296), so EmitBinarySide would print the
        // wrapped value and appliers would see a negative InflatedLength. C
        // stores the full int64 (patch_parse.c:840); the port cannot represent
        // lengths above int.MaxValue — reject them like the sibling ParseInt
        // helpers.
        if (len > int.MaxValue)
        {
            throw Err("invalid binary size", ctx.LineNumber);
        }

        int inflatedLen = (int)len;
        using var decoded = new PooledByteBufferWriter();

        while (ctx.LineLength > 0)
        {
            ctx.Peek(out byte c);
            if (c == (byte)'\n')
            {
                break;
            }

            int decodedLen;
            if (c is >= (byte)'A' and <= (byte)'Z')
            {
                decodedLen = c - (byte)'A' + 1;
            }
            else if (c is >= (byte)'a' and <= (byte)'z')
            {
                decodedLen = c - (byte)'a' + ('z' - 'a') + 1 + 1;
            }
            else
            {
                throw Err("invalid binary length", ctx.LineNumber);
            }

            ctx.AdvanceChars(1);

            int encodedLen = (decodedLen / 4) + (decodedLen % 4 != 0 ? 1 : 0) * 5;
            // C: encoded_len = ((decoded_len / 4) + !!(decoded_len % 4)) * 5
            encodedLen = ((decodedLen / 4) + (decodedLen % 4 != 0 ? 1 : 0)) * 5;

            if (encodedLen == 0 || ctx.LineLength == 0 || encodedLen > ctx.LineLength - 1)
            {
                throw Err("truncated binary data", ctx.LineNumber);
            }

            ReadOnlySpan<byte> encodedSpan = ctx.Line.Slice(0, encodedLen);
            if (!Base85.TryDecode(encodedSpan, decodedLen, decoded))
            {
                throw Err("truncated binary data", ctx.LineNumber);
            }

            ctx.AdvanceChars(encodedLen);

            if (!ctx.AdvanceNl())
            {
                throw Err("trailing data", ctx.LineNumber);
            }
        }

        binary = new GitBinaryFile
        {
            Type = type,
            Data = decoded.WrittenSpan.ToArray(),
            InflatedLength = inflatedLen,
        };

        return 0;
    }

    /// <summary>Matches <c>parse_patch_binary</c> (patch_parse.c:850-881).</summary>
    private static int ParseBinary(ParsedPatch patch, ParseContext ctx)
    {
        if (!ctx.AdvanceExpected("GIT binary patch"u8) || !ctx.AdvanceNl())
        {
            throw Err("corrupt git binary header", ctx.LineNumber);
        }

        // old→new side
        if (ParseBinarySide(out GitBinaryFile? newFile, ctx) < 0 || newFile is null)
        {
            return -1;
        }

        if (!ctx.AdvanceNl())
        {
            throw Err("corrupt git binary separator", ctx.LineNumber);
        }

        // new→old side
        if (ParseBinarySide(out GitBinaryFile? oldFile, ctx) < 0 || oldFile is null)
        {
            return -1;
        }

        if (!ctx.AdvanceNl())
        {
            throw Err("corrupt git binary patch separator", ctx.LineNumber);
        }

        patch.Binary = new GitBinaryPatch
        {
            ContainsData = true,
            NewFile = newFile,
            OldFile = oldFile,
        };
        patch.Delta.Flags |= GitDiffFileFlags.Binary;
        return 0;
    }

    /// <summary>Matches <c>parse_patch_binary_nodata</c> (patch_parse.c:883-908).</summary>
    private static int ParseBinaryNoData(ParsedPatch patch, ParseContext ctx)
    {
        ReadOnlyMemory<byte>? old = patch.OldPath ?? patch.HeaderOldPath;
        ReadOnlyMemory<byte>? @new = patch.NewPath ?? patch.HeaderNewPath;

        if (old is null || @new is null)
        {
            throw Err("corrupt binary data without paths", ctx.LineNumber);
        }

        ReadOnlySpan<byte> oldSpan = patch.Delta.Status == GitDeltaStatus.Added
            ? "/dev/null"u8
            : old.Value.Span;
        ReadOnlySpan<byte> newSpan = patch.Delta.Status == GitDeltaStatus.Deleted
            ? "/dev/null"u8
            : @new.Value.Span;

        if (!ctx.AdvanceExpected("Binary files "u8) ||
            !ctx.AdvanceExpected(oldSpan) ||
            !ctx.AdvanceExpected(" and "u8) ||
            !ctx.AdvanceExpected(newSpan) ||
            !ctx.AdvanceExpected(" differ"u8) ||
            !ctx.AdvanceNl())
        {
            throw Err("corrupt git binary header", ctx.LineNumber);
        }

        patch.Binary = new GitBinaryPatch { ContainsData = false };
        patch.Delta.Flags |= GitDiffFileFlags.Binary;
        return 0;
    }

    /// <summary>Matches <c>parse_patch_hunks</c> (patch_parse.c:911-936).</summary>
    private static int ParseHunks(ParsedPatch patch, ParseContext ctx)
    {
        while (ctx.Contains("@@ -"u8))
        {
            if (ParseHunkHeader(out int oldStart, out int oldLines,
                out int newStart, out int newLines, out ReadOnlyMemory<byte> header, ctx) < 0)
            {
                return -1;
            }

            int lineStartIndex = patch.Lines.Count;
            if (ParseHunkBody(patch, oldStart, oldLines, newStart, newLines,
                lineStartIndex, out int lineCount, ctx) < 0)
            {
                return -1;
            }

            // Build the DiffHunk with its lines slice.
            var hunkLines = new List<GitDiffLine>(lineCount);
            for (int i = 0; i < lineCount; i++)
            {
                hunkLines.Add(patch.Lines[lineStartIndex + i]);
            }

            patch.Hunks.Add(new GitDiffHunk(oldStart, oldLines, newStart, newLines, header, hunkLines));
        }

        patch.Delta.Flags |= GitDiffFileFlags.NotBinary;
        return 0;
    }

    /// <summary>Matches <c>parse_patch_body</c> (patch_parse.c:938-947).</summary>
    private static int ParsePatchBody(ParsedPatch patch, ParseContext ctx)
    {
        if (ctx.Contains("GIT binary patch"u8))
        {
            return ParseBinary(patch, ctx);
        }

        if (ctx.Contains("Binary files "u8))
        {
            return ParseBinaryNoData(patch, ctx);
        }

        return ParseHunks(patch, ctx);
    }

    // ===== Post-parse validation =====

    /// <summary>Matches <c>check_header_names</c> (patch_parse.c:949-965).</summary>
    private static int CheckHeaderNames(ReadOnlyMemory<byte>? one, ReadOnlyMemory<byte>? two, string oldOrNew, bool twoNull)
    {
        if (one is null || two is null)
        {
            return 0;
        }

        if (twoNull && !two.Value.Span.SequenceEqual("/dev/null"u8))
        {
            throw Err($"expected {oldOrNew} path of '/dev/null'", 0);
        }

        if (!twoNull && !one.Value.Span.SequenceEqual(two.Value.Span))
        {
            throw Err($"mismatched {oldOrNew} path names", 0);
        }

        return 0;
    }

    /// <summary>Matches <c>check_prefix</c> (patch_parse.c:967-1004).</summary>
    private static int CheckPrefix(out ReadOnlyMemory<byte>? prefix, out int prefixLen, ParsedPatch patch, ReadOnlySpan<byte> pathStart)
    {
        prefix = null;
        prefixLen = 0;
        int prefixLenRequested = patch.Options.PrefixLength;

        if (prefixLenRequested == 0)
        {
            return 0;
        }

        int pathIdx = 0;
        // Skip leading slashes (don't count as part of prefix).
        while (pathIdx < pathStart.Length && pathStart[pathIdx] == (byte)'/')
        {
            pathIdx++;
        }

        int remainLen = prefixLenRequested;
        while (pathIdx < pathStart.Length && remainLen > 0)
        {
            if (pathStart[pathIdx] == (byte)'/')
            {
                remainLen--;
            }
            pathIdx++;
        }

        if (remainLen > 0 || pathIdx >= pathStart.Length)
        {
            throw Err($"header filename does not contain {prefixLenRequested} path components", 0);
        }

        prefixLen = pathIdx;
        prefix = pathStart[..prefixLen].ToArray();
        return 0;
    }

    /// <summary>Matches <c>check_filenames</c> (patch_parse.c:1006-1051).</summary>
    private static int CheckFilenames(ParsedPatch patch)
    {
        bool added = patch.Delta.Status == GitDeltaStatus.Added;
        bool deleted = patch.Delta.Status == GitDeltaStatus.Deleted;

        if (patch.OldPath is not null && patch.NewPath is null)
        {
            throw Err("missing new path", 0);
        }

        if (patch.OldPath is null && patch.NewPath is not null)
        {
            throw Err("missing old path", 0);
        }

        if (CheckHeaderNames(patch.HeaderOldPath, patch.OldPath, "old", added) < 0 ||
            CheckHeaderNames(patch.HeaderNewPath, patch.NewPath, "new", deleted) < 0)
        {
            return -1;
        }

        ReadOnlyMemory<byte>? prefixedOld = (!added && patch.OldPath is not null) ? patch.OldPath : patch.HeaderOldPath;
        ReadOnlyMemory<byte>? prefixedNew = (!deleted && patch.NewPath is not null) ? patch.NewPath : patch.HeaderNewPath;

        int oldPrefixLen = 0, newPrefixLen = 0;
        ReadOnlyMemory<byte>? oldPrefix = null, newPrefix = null;
        if (prefixedOld is not null && CheckPrefix(out oldPrefix, out oldPrefixLen, patch, prefixedOld.Value.Span) < 0)
        {
            return -1;
        }

        patch.OldPrefix = oldPrefix;

        if (prefixedNew is not null && CheckPrefix(out newPrefix, out newPrefixLen, patch, prefixedNew.Value.Span) < 0)
        {
            return -1;
        }

        patch.NewPrefix = newPrefix;

        // Prefer rename filenames (unambiguous, unprefixed). The paths are
        // zero-copy slices of the owned parse buffers — matching C's pointer
        // into the git_str the parser copied.
        if (patch.RenameOldPath is { } renameOldPath)
        {
            patch.Delta.OldFile.Path = GitPath.FromUtf8Bytes(renameOldPath);
        }
        else if (prefixedOld is { } old)
        {
            patch.Delta.OldFile.Path = GitPath.FromUtf8Bytes(old.Slice(oldPrefixLen));
        }
        else
        {
            patch.Delta.OldFile.Path = null;
        }

        if (patch.RenameNewPath is { } renameNewPath)
        {
            patch.Delta.NewFile.Path = GitPath.FromUtf8Bytes(renameNewPath);
        }
        else if (prefixedNew is { } @new)
        {
            patch.Delta.NewFile.Path = GitPath.FromUtf8Bytes(@new.Slice(newPrefixLen));
        }
        else
        {
            patch.Delta.NewFile.Path = null;
        }

        if (patch.Delta.OldFile.Path is null && patch.Delta.NewFile.Path is null)
        {
            throw Err("git diff header lacks old / new paths", 0);
        }

        return 0;
    }

    /// <summary>Matches <c>check_patch</c> (patch_parse.c:1053-1084).</summary>
    private static int CheckPatch(ParsedPatch patch)
    {
        GitDiffDelta delta = patch.Delta;

        if (CheckFilenames(patch) < 0)
        {
            return -1;
        }

        if (delta.OldFile.Path is not null &&
            delta.Status != GitDeltaStatus.Deleted &&
            delta.NewFile.Mode == 0)
        {
            delta.NewFile.Mode = delta.OldFile.Mode;
        }

        if (delta.Status == GitDeltaStatus.Modified &&
            (delta.Flags & GitDiffFileFlags.Binary) == 0 &&
            delta.NewFile.Mode == delta.OldFile.Mode &&
            patch.Hunks.Count == 0)
        {
            throw Err("patch with no hunks", 0);
        }

        if (delta.Status == GitDeltaStatus.Added)
        {
            delta.OldFile.Id = GitOid.Empty;
            delta.OldFile.IdAbbrev = 0;
        }

        if (delta.Status == GitDeltaStatus.Deleted)
        {
            delta.NewFile.Id = GitOid.Empty;
            delta.NewFile.IdAbbrev = 0;
        }

        return 0;
    }

    // ===== Helpers =====

    private static bool IsSpace(byte c)
        => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)'\v';

    private static bool IsHexDigit(byte c)
        => c is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
}
