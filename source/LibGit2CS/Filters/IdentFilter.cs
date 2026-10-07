// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Attributes;
using LibGit2CS.Core;

namespace LibGit2CS.Filters;

/// <summary>
/// <c>$Id$</c> keyword expansion filter. Managed port of
/// <c>src/libgit2/ident.c</c> (139 LOC). Implements <see cref="IFilter"/>
/// with attributes <c>"+ident"</c> and priority 100 (runs after CRLF on
/// clean, before CRLF on smudge).
/// </summary>
/// <remarks>
/// On smudge (ODB→workdir), <c>$Id$</c> is expanded to
/// <c>$Id: &lt;40-hex-oid&gt; $</c> using the blob's OID. On clean
/// (workdir→ODB), <c>$Id: ... $</c> is contracted back to <c>$Id$</c>.
/// Binary files are always passthrough. If the source has no OID (unknown
/// blob), smudge is passthrough.
/// </remarks>
internal sealed class IdentFilter : IFilter
{
    public string Name => FilterRegistry.IdentName;
    public string Attributes => "+ident";

    public ValueTask<GitFilterResult> CheckAsync(GitFilterSource source, IReadOnlyList<GitAttrValue> attrValues, CancellationToken cancellationToken = default)
    {
        // The "+ident" attr spec in the registry handles gating — if the
        // ident attribute is not TRUE, Check is never called. C's ident
        // filter has no check callback; it always applies (the attr spec
        // does the filtering). We always return Apply here.
        return ValueTask.FromResult(GitFilterResult.Apply);
    }

    public ValueTask<GitApplyResult> ApplyAsync(GitFilterSource source, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        // Don't filter binary files — matches ident_apply (ident.c:107-108).
        // Don't filter binary files — matches ident_apply (ident.c:107-108),
        // whose gate is git_str_is_binary (str.c:1253-1279), NOT
        // GitTextStats.IsBinary (bare-CR, tab-heavy and UTF-16/32-BOM inputs
        // diverge).
        if (IsBinary(input.Span))
        {
            return ValueTask.FromResult(GitApplyResult.Passthrough);
        }

        if (source.Mode == GitFilterMode.ToWorktree)
        {
            return ValueTask.FromResult(InsertId(source, input));
        }

        return ValueTask.FromResult(RemoveId(input));
    }

    /// <summary>
    /// Exact port of <c>git_str_is_binary</c> (str.c:1253-1279): skips a
    /// leading BOM and returns binary immediately for UTF-16/32 BOMs; counts
    /// <c>\t</c>/<c>\v</c> as neither printable nor non-printable; a NUL byte
    /// is binary; never uses a CR/CRLF rule. The verdict is
    /// <c>(printable &gt;&gt; 7) &lt; nonprintable</c>.
    /// </summary>
    private static bool IsBinary(ReadOnlySpan<byte> input)
    {
        int offset = DetectBom(input, out int bom);

        // GIT_STR_BOM_UTF16_LE (2) and above are binary; UTF-8 (1) and none (0) are not.
        if (bom > 1)
        {
            return true;
        }

        int printable = 0;
        int nonprintable = 0;
        for (int i = offset; i < input.Length; i++)
        {
            byte c = input[i];

            // Printable: above SPACE (0x1F) excluding DEL, plus BS, ESC, FF.
            if (c is > 0x1F and not 127 or ((byte)'\b') or 0x1B or 0x0C)
            {
                printable++;
            }
            else if (c == 0)
            {
                return true;
            }
            else if (!IsAsciiWhitespace(c))
            {
                nonprintable++;
            }
        }

        return (printable >> 7) < nonprintable;
    }

    /// <summary>
    /// Exact port of <c>git_str_detect_bom</c> (str.c:1286-1329): returns the
    /// BOM byte length and sets <paramref name="bom"/> to the
    /// <c>GIT_STR_BOM_*</c> constant.
    /// </summary>
    private static int DetectBom(ReadOnlySpan<byte> buf, out int bom)
    {
        bom = 0;
        if (buf.Length < 2)
        {
            return 0;
        }

        switch (buf[0])
        {
            case 0:
                if (buf.Length >= 4 && buf[1] == 0 && buf[2] == 0xFE && buf[3] == 0xFF)
                {
                    bom = 5; // UTF32_BE
                    return 4;
                }

                break;
            case 0xEF:
                if (buf.Length >= 3 && buf[1] == 0xBB && buf[2] == 0xBF)
                {
                    bom = 1; // UTF8
                    return 3;
                }

                break;
            case 0xFE:
                if (buf[1] == 0xFF)
                {
                    bom = 3; // UTF16_BE
                    return 2;
                }

                break;
            case 0xFF:
                if (buf[1] != 0xFE)
                {
                    break;
                }

                if (buf.Length >= 4 && buf[2] == 0 && buf[3] == 0)
                {
                    bom = 4; // UTF32_LE
                    return 4;
                }

                bom = 2; // UTF16_LE
                return 2;
        }

        return 0;
    }

    /// <summary>ASCII-only <c>git__isspace</c> (ctype_compat.h:43-47).</summary>
    private static bool IsAsciiWhitespace(byte c)
        => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)'\v';

    /// <summary>
    /// Smudge: expands <c>$Id$</c> → <c>$Id: &lt;40-hex&gt; $</c>. Matches
    /// <c>ident_insert_id</c> (ident.c:42-73).
    /// </summary>
    private static GitApplyResult InsertId(GitFilterSource source, ReadOnlyMemory<byte> input)
    {
        // If no OID available, passthrough — matches ident.c:51-52.
        GitOid? oid = source.SourceId;
        if (oid is null)
        {
            return GitApplyResult.Passthrough;
        }

        GitOid oidValue = oid.Value;

        // Find $Id$ in the input.
        if (!FindId(input.Span, out int idStart, out int idEnd))
        {
            return GitApplyResult.Passthrough;
        }

        // Build: prefix + "$Id: " + oidHex + " $" + suffix
        int prefixLen = idStart;
        int suffixStart = idEnd;
        int suffixLen = input.Length - suffixStart;
        byte[] output = new byte[prefixLen + 5 + oidValue.HexSize + 2 + suffixLen];
        int pos = 0;

        // Prefix (before $Id$).
        if (prefixLen > 0)
        {
            input.Span[..prefixLen].CopyTo(output.AsSpan(pos));
            pos += prefixLen;
        }

        // "$Id: "
        output[pos++] = (byte)'$';
        output[pos++] = (byte)'I';
        output[pos++] = (byte)'d';
        output[pos++] = (byte)':';
        output[pos++] = (byte)' ';

        // OID hex
        pos += oidValue.FormatHex(output.AsSpan(pos));

        // " $"
        output[pos++] = (byte)' ';
        output[pos++] = (byte)'$';

        // Suffix (after closing $)
        if (suffixLen > 0)
        {
            input.Span[suffixStart..].CopyTo(output.AsSpan(pos));
        }

        return GitApplyResult.WithOutput(output);
    }

    /// <summary>
    /// Clean: contracts <c>$Id: ... $</c> → <c>$Id$</c>. Matches
    /// <c>ident_remove_id</c> (ident.c:75-94).
    /// </summary>
    private static GitApplyResult RemoveId(ReadOnlyMemory<byte> input)
    {
        if (!FindId(input.Span, out int idStart, out int idEnd))
        {
            return GitApplyResult.Passthrough;
        }

        // Build: prefix + "$Id$" + suffix
        int prefixLen = idStart;
        int suffixStart = idEnd;
        int suffixLen = input.Length - suffixStart;
        byte[] output = new byte[prefixLen + 4 + suffixLen];
        int pos = 0;

        if (prefixLen > 0)
        {
            input.Span[..prefixLen].CopyTo(output.AsSpan(pos));
            pos += prefixLen;
        }

        // "$Id$"
        output[pos++] = (byte)'$';
        output[pos++] = (byte)'I';
        output[pos++] = (byte)'d';
        output[pos++] = (byte)'$';

        if (suffixLen > 0)
        {
            input.Span[suffixStart..].CopyTo(output.AsSpan(pos));
        }

        return GitApplyResult.WithOutput(output);
    }

    /// <summary>
    /// Finds the <c>$Id...$</c> pattern in the input. Matches
    /// <c>ident_find_id</c> (ident.c:14-40). Returns the byte index of the
    /// opening <c>$</c> (idStart) and the byte index after the closing
    /// <c>$</c> (idEnd).
    /// </summary>
    private static bool FindId(ReadOnlySpan<byte> input, out int idStart, out int idEnd)
    {
        idStart = 0;
        idEnd = 0;

        int end = input.Length;
        int len = input.Length;
        int start = 0;
        int found = -1; // absolute index of the LAST '$' the scan located

        while (len > 3)
        {
            int rel = input[start..end].IndexOf((byte)'$');
            if (rel < 0)
            {
                break;
            }

            found = start + rel;
            int remaining = end - found - 1;
            if (remaining < 3)
            {
                return false;
            }

            start = found + 1;
            len = remaining;

            if (input[start] == (byte)'I' && input[start + 1] == (byte)'d')
            {
                break;
            }
        }

        // C (ident.c:31-38): the post-loop fall-through — the LAST located '$' is treated as id_start even when it was not followed by "Id" (only `len < 3 ||
        // !found` fails). An input ending in "$XY$" with exactly 3 remaining bytes is therefore matched and rewritten.
        if (len < 3 || found < 0)
        {
            return false;
        }

        idStart = found;

        // memchr(start + 2, '$', len - 2): start == found+1, so the search
        // begins at found+3 and runs to the end of the input.
        int closeRel = input[(found + 3)..end].IndexOf((byte)'$');
        if (closeRel < 0)
        {
            return false;
        }

        idEnd = found + 3 + closeRel + 1; // after closing $
        return true;
    }
}
