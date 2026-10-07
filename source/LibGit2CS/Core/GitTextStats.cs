// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Text statistics gathered by scanning byte content. Managed port of
/// <c>git_str_text_stats</c> (<c>src/util/str.h:27-31</c>) and
/// <c>git_str_gather_text_stats</c> (<c>src/util/str.c:1336-1383</c>).
/// Used by the CRLF filter for binary detection and line-ending counts.
/// </summary>
/// <param name="Nul">Count of NUL (0x00) bytes.</param>
/// <param name="Cr">Count of CR (0x0D) bytes, including those in CRLF pairs.</param>
/// <param name="Lf">Count of LF (0x0A) bytes, including those in CRLF pairs.</param>
/// <param name="Crlf">Count of CRLF (0x0D0A) pairs.</param>
/// <param name="Printable">Count of printable bytes (> 0x1F, != 0x7F, plus tab/FF/VT/BS/ESC).</param>
/// <param name="NonPrintable">Count of non-printable, non-whitespace bytes (excluding NUL which is counted separately).</param>
/// <param name="BomLength">Length in bytes of the detected BOM at the start (0 if none).</param>
/// <param name="IsBinary">True if the content is classified as binary. Matches <c>git_str_gather_text_stats</c> return value: <c>cr != crlf || nul > 0 || (printable >> 7) &lt; nonprintable</c>.</param>
public readonly record struct GitTextStats(
    int Nul,
    int Cr,
    int Lf,
    int Crlf,
    int Printable,
    int NonPrintable,
    int BomLength,
    bool IsBinary)
{
    /// <summary>
    /// Gathers text statistics from the given byte span. Matches
    /// <c>git_str_gather_text_stats</c> (<c>src/util/str.c:1336-1383</c>).
    /// </summary>
    /// <param name="data">The content to scan.</param>
    /// <param name="skipBom">If true, BOM bytes are skipped before counting (matching C's <c>skip_bom</c> param).</param>
    public static GitTextStats Gather(ReadOnlySpan<byte> data, bool skipBom = false)
    {
        int bomLen = DetectBom(data);
        ReadOnlySpan<byte> scan = data;
        if (skipBom && bomLen > 0)
        {
            scan = data[bomLen..];
        }

        int end = scan.Length;

        // Ignore trailing EOF character (0x1A = ^Z) — matches str.c:1350-1351.
        if (end > 0 && scan[end - 1] == 0x1A)
        {
            end--;
        }

        int nul = 0;
        int cr = 0;
        int lf = 0;
        int crlf = 0;
        int printable = 0;
        int nonPrintable = 0;

        for (int i = 0; i < end; i++)
        {
            byte c = scan[i];

            if (c is > 0x1F and not 0x7F)
            {
                printable++;
            }
            else
            {
                switch (c)
                {
                    case 0x00:
                        nul++;
                        nonPrintable++;
                        break;
                    case (byte)'\n':
                        lf++;
                        break;
                    case (byte)'\r':
                        cr++;
                        if (i + 1 < end && scan[i + 1] == (byte)'\n')
                        {
                            crlf++;
                        }

                        break;
                    case (byte)'\t':
                    case 0x0C: // FF
                    case 0x0B: // VT
                    case 0x08: // BS
                    case 0x1B: // ESC
                        printable++;
                        break;
                    default:
                        nonPrintable++;
                        break;
                }
            }
        }

        // Treat files with a bare CR (not part of CRLF) as binary,
        // files with NUL as binary, or where nonprintable dominates.
        // Matches str.c:1382-1383.
        bool isBinary = cr != crlf
            || nul > 0
            || (printable >> 7) < nonPrintable;

        return new GitTextStats(nul, cr, lf, crlf, printable, nonPrintable, bomLen, isBinary);
    }

    /// <summary>
    /// Detects the BOM at the start of the data. Matches
    /// <c>git_str_detect_bom</c> (<c>src/util/str.c</c>). Returns the BOM
    /// length in bytes (0 if no BOM).
    /// </summary>
    private static int DetectBom(ReadOnlySpan<byte> data)
    {
        // UTF-8 BOM: EF BB BF
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return 3;
        }

        // UTF-16 BE BOM: FE FF
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return 2;
        }

        // UTF-32 BE BOM: 00 00 FE FF
        if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFE && data[3] == 0xFF)
        {
            return 4;
        }

        // UTF-32 LE BOM: FF FE 00 00 — must be checked before the 2-byte
        // UTF-16 LE form (str.c git_str_detect_bom case '\xFF').
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00)
        {
            return 4;
        }

        // UTF-16 LE BOM: FF FE
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return 2;
        }

        return 0;
    }

    /// <summary>
    /// Converts CRLF line endings to LF. Matches <c>git_str_crlf_to_lf</c>
    /// (<c>src/util/str.c:1131-1176</c>). Drops CR unless it is not followed
    /// by LF (bare CR is preserved).
    /// </summary>
    public static byte[] CrlfToLf(ReadOnlySpan<byte> input)
    {
        int outputLength = input.Length;
        for (int i = 0; i + 1 < input.Length; i++)
        {
            if (input[i] == (byte)'\r' && input[i + 1] == (byte)'\n')
            {
                outputLength--;
            }
        }

        byte[] result = new byte[outputLength];
        int outIdx = 0;

        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == (byte)'\r')
            {
                // Do not drop \r unless it is followed by \n.
                if (i + 1 == input.Length || input[i + 1] != (byte)'\n')
                {
                    result[outIdx++] = (byte)'\r';
                }

                // If \r\n, skip the \r (the \n will be copied next).
            }
            else
            {
                result[outIdx++] = input[i];
            }
        }

        return result;
    }

    /// <summary>
    /// Converts LF line endings to CRLF. Matches <c>git_str_lf_to_crlf</c>
    /// (<c>src/util/str.c:1178-1228</c>). Existing CRLF pairs are not doubled.
    /// </summary>
    public static byte[] LfToCrlf(ReadOnlySpan<byte> input)
    {
        // Fast path: no LF at all → no change.
        if (!input.Contains((byte)'\n'))
        {
            return input.ToArray();
        }

        // Pre-count LFs that are not already preceded by CR to size the output.
        int extra = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == (byte)'\n' && (i == 0 || input[i - 1] != (byte)'\r'))
            {
                extra++;
            }
        }

        byte[] result = new byte[input.Length + extra];
        int outIdx = 0;

        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == (byte)'\n' && (i == 0 || input[i - 1] != (byte)'\r'))
            {
                result[outIdx++] = (byte)'\r';
            }

            result[outIdx++] = input[i];
        }

        return result;
    }
}
