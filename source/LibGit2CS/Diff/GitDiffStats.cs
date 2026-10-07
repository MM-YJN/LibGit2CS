// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Globalization;

using LibGit2CS.IO;
using LibGit2CS.Utils;

namespace LibGit2CS.Diff;

/// <summary>
/// Diff statistics with per-file data. Managed equivalent of
/// <c>git_diff_stats</c> (<c>diff_stats.c:24-35</c>). Computed by
/// <see cref="GitDiff.GetStatsAsync"/> which materializes every patch (two-pass: gather
/// maxes, then format).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-pass design</b>: <c>git_diff_get_stats</c> materializes every patch
/// to compute <see cref="MaxName"/>, <see cref="MaxFilestat"/>, and
/// <see cref="MaxDigits"/>, which are needed for column alignment in the
/// <c>--stat</c> histogram. The <see cref="Format"/> method then uses these
/// maxima in a second formatting pass.
/// </para>
/// <para>
/// The format ordering matches <c>git_diff__stats_to_buf</c> (diff_stats.c:294):
/// NUMBER → FULL → SHORT → INCLUDE_SUMMARY (bitmask combinable).
/// </para>
/// </remarks>
public sealed class GitDiffStats
{
    private const int StatsFullMinScale = 7;
    private const int DefaultWidth = 80;

    private readonly GitDiffFileStat[] _perFile;

    internal GitDiffStats(
        GitDiffFileStat[] perFile, int filesChanged,
        int insertions, int deletions,
        int maxName, int maxFilestat, int maxDigits)
    {
        _perFile = perFile;
        FilesChanged = filesChanged;
        Insertions = insertions;
        Deletions = deletions;
        MaxName = maxName;
        MaxFilestat = maxFilestat;
        MaxDigits = maxDigits;
    }

    /// <summary>Number of files changed.</summary>
    public int FilesChanged { get; }

    /// <summary>Total lines added across all deltas.</summary>
    public int Insertions { get; }

    /// <summary>Total lines removed across all deltas.</summary>
    public int Deletions { get; }

    /// <summary>Longest displayed file name (for column alignment).</summary>
    internal int MaxName { get; }

    /// <summary>Largest single-file insertion+deletion count.</summary>
    internal int MaxFilestat { get; }

    /// <summary>Number of digits in <see cref="MaxFilestat"/> + 1.</summary>
    internal int MaxDigits { get; }

    /// <summary>
    /// Formats the statistics into a byte buffer. Matches
    /// <c>git_diff__stats_to_buf</c> (diff_stats.c:294-373).
    /// </summary>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="format">Bitmask of <see cref="GitDiffStatsFormat"/> values.</param>
    /// <param name="width">Target terminal width (affects FULL histogram scaling).</param>
    public void Format(IBufferWriter<byte> writer, GitDiffStatsFormat format, int width = DefaultWidth)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // NUMBER: per-file insertions/deletions
        if ((format & GitDiffStatsFormat.Number) != 0)
        {
            foreach (GitDiffFileStat stat in _perFile)
            {
                FormatNumber(writer, stat);
            }
        }

        // FULL: histogram with +/- bars
        if ((format & GitDiffStatsFormat.Full) != 0)
        {
            int barWidth = width;
            if (barWidth > 0)
            {
                if (barWidth > MaxName + MaxDigits + 5)
                {
                    barWidth -= MaxName + MaxDigits + 5;
                }

                if (barWidth < StatsFullMinScale)
                {
                    barWidth = StatsFullMinScale;
                }
            }

            if (barWidth > MaxFilestat)
            {
                barWidth = 0; // raw counting when bars fit
            }

            foreach (GitDiffFileStat stat in _perFile)
            {
                FormatFull(writer, stat, barWidth);
            }
        }

        // SHORT (or FULL): summary line
        if ((format & (GitDiffStatsFormat.Short | GitDiffStatsFormat.Full)) != 0)
        {
            FormatShort(writer);
        }

        // INCLUDE_SUMMARY: mode changes
        if ((format & GitDiffStatsFormat.IncludeSummary) != 0)
        {
            foreach (GitDiffFileStat stat in _perFile)
            {
                FormatSummary(writer, stat);
            }
        }
    }

    private static void FormatNumber(IBufferWriter<byte> writer, GitDiffFileStat stat)
    {
        // diff_stats.c:158-170 — "%-8" left-justified 8-char fields.
        // Binary: "-       -       path\n". Non-binary: "ins     del     path\n".
        if (stat.IsBinary)
        {
            Span<byte> buf = writer.GetSpan(16);
            buf[0] = (byte)'-';
            buf.Slice(1, 7).Fill((byte)' ');
            buf[8] = (byte)'-';
            buf.Slice(9, 7).Fill((byte)' ');
            writer.Advance(16);
        }
        else
        {
            AppendLeftJustified(writer, stat.Insertions, 8);
            AppendLeftJustified(writer, stat.Deletions, 8);
        }

        Span<byte> buffer = writer.GetSpan(stat.Path.Length + 1);
        stat.Path.Span.CopyTo(buffer);
        buffer[stat.Path.Length] = (byte)'\n';
        writer.Advance(stat.Path.Length + 1);
    }

    private void FormatFull(IBufferWriter<byte> writer, GitDiffFileStat stat, int barWidth)
    {
        // diff_stats.c:60-155 — " <name padded> | <count> <bars>\n".
        // Rename display: " {common}{old_rest => new_rest}" or " {old} => {new}".
        GitPath path = stat.Path;
        GitPath oldPath = stat.OldPath is { } op ? op : default;
        int padding;
        writer.Write(" "u8);

        if (stat.IsRename)
        {
            // "IsRename implies OldPath is set"
            int commonDirLen = PathByteHelpers.CommonDirLength(oldPath.Span, path.Span);
            if (commonDirLen > 0)
            {
                // " {common}{old_rest => new_rest}"
                // length: commonDirLen + 2 + oldPathValue.Length - commonDirLen + 4 + path.Length - commonDirLen
                int length = 6 + oldPath.Length + path.Length - commonDirLen;
                Span<byte> buf = writer.GetSpan(length);

                oldPath.Span.Slice(0, commonDirLen).CopyTo(buf);
                buf = buf.Slice(commonDirLen);

                buf[0] = (byte)'{';
                buf = buf.Slice(1);

                oldPath.Span.Slice(commonDirLen).CopyTo(buf);
                buf = buf.Slice(oldPath.Length - commonDirLen);

                " => "u8.CopyTo(buf);
                buf = buf.Slice(4);

                path.Span.Slice(commonDirLen).CopyTo(buf);
                buf = buf.Slice(path.Length - commonDirLen);

                buf[0] = (byte)'}';

                writer.Advance(length);

                padding = MaxName - length;
            }
            else
            {
                // " {old} => {new}"
                Span<byte> buf = writer.GetSpan(oldPath.Length + path.Length + 4);

                oldPath.Span.CopyTo(buf);
                buf = buf.Slice(oldPath.Length);

                " => "u8.CopyTo(buf);
                buf = buf.Slice(4);

                path.Span.CopyTo(buf);

                writer.Advance(oldPath.Length + path.Length + 4);

                padding = MaxName - oldPath.Length - path.Length - 4;
            }
        }
        else
        {
            writer.Write(path.Span);
            padding = MaxName - path.Length;
        }

        if (padding > 0)
        {
            Span<byte> buf = writer.GetSpan(padding);
            buf.Slice(0, padding).Fill((byte)' ');
            writer.Advance(padding);
        }

        writer.Write(" | "u8);

        if (stat.IsBinary)
        {
            // diff_stats.c:127-129 — "Bin <old> -> <new> bytes"
            writer.Write("Bin "u8);
            writer.WriteSpanFormattable(stat.OldSize, provider: CultureInfo.InvariantCulture);
            writer.Write(" -> "u8);
            writer.WriteSpanFormattable(stat.NewSize, provider: CultureInfo.InvariantCulture);
            writer.Write(" bytes"u8);
        }
        else
        {
            int total = stat.Insertions + stat.Deletions;
            writer.WriteSpanFormattable(new DecimalPadLeftFormatter(total, MaxDigits, ' '), provider: CultureInfo.InvariantCulture);

            if (stat.Insertions > 0 || stat.Deletions > 0)
            {
                writer.Write(" "u8);

                if (barWidth > 0 && MaxFilestat > 0)
                {
                    // Scaled bars.
                    // C computes in size_t (diff_stats.c:119-127); an
                    // unchecked int multiplication would wrap negative for
                    // files with >~26.8M changed lines (30M × 80 = 2.4e9),
                    // collapsing the bar to "+-". Compute in 64-bit.
                    long full = ((long)total * barWidth + MaxFilestat / 2) / MaxFilestat;
                    long plus = full * stat.Insertions / total;
                    long minus = full - plus;

                    // C (diff_stats.c:125-126): at least one '+' and one '-'
                    // whenever the file has any change — max(plus, 1),
                    // max(minus, 1) — even for pure additions/deletions.
                    int plusCount = (int)Math.Max(plus, 1);
                    int minusCount = (int)Math.Max(minus, 1);
                    Span<byte> buf = writer.GetSpan(plusCount + minusCount);
                    buf.Slice(0, plusCount).Fill((byte)'+');
                    buf.Slice(plusCount, minusCount).Fill((byte)'-');
                    writer.Advance(plusCount + minusCount);
                }
                else
                {
                    // Raw bars
                    Span<byte> buf = writer.GetSpan(stat.Insertions + stat.Deletions);
                    buf.Slice(0, stat.Insertions).Fill((byte)'+');
                    buf.Slice(stat.Insertions, stat.Deletions).Fill((byte)'-');
                    writer.Advance(stat.Insertions + stat.Deletions);
                }
            }
        }

        writer.Write("\n"u8);
    }

    private void FormatShort(IBufferWriter<byte> writer)
    {
        // diff_stats.c:347-361 — show insertions if ins>0 OR del==0; show
        // deletions if del>0 OR ins==0. This means both are shown when both=0.
        writer.Write(" "u8);
        writer.WriteSpanFormattable(FilesChanged, provider: CultureInfo.InvariantCulture);
        writer.Write(" file"u8);
        writer.Write(FilesChanged == 1 ? ""u8 : "s"u8);
        writer.Write(" changed"u8);

        if (Insertions > 0 || Deletions == 0)
        {
            writer.Write(", "u8);
            writer.WriteSpanFormattable(Insertions, provider: CultureInfo.InvariantCulture);
            writer.Write(" insertion"u8);
            writer.Write(Insertions == 1 ? ""u8 : "s"u8);
            writer.Write("(+)"u8);
        }

        if (Deletions > 0 || Insertions == 0)
        {
            writer.Write(", "u8);
            writer.WriteSpanFormattable(Deletions, provider: CultureInfo.InvariantCulture);
            writer.Write(" deletion"u8);
            writer.Write(Deletions == 1 ? ""u8 : "s"u8);
            writer.Write("(-)"u8);
        }

        writer.Write("\n"u8);
    }

    private static void FormatSummary(IBufferWriter<byte> writer, GitDiffFileStat stat)
    {
        // diff_stats.c:172-184 — mode create/delete/change lines.
        // %06o = octal, 6 digits, zero-padded.
        GitPath path = stat.Path;
        GitPath? oldPath = stat.OldPath;
        if (stat.OldMode != stat.NewMode)
        {
            if (stat.OldMode == 0)
            {
                writer.Write(" create mode "u8);
                writer.WriteSpanFormattable(FormatMode(stat.NewMode), provider: CultureInfo.InvariantCulture);
                WriteSpacePathNewLine(writer, path);
            }
            else if (stat.NewMode == 0)
            {
                writer.Write(" delete mode "u8);
                writer.WriteSpanFormattable(FormatMode(stat.OldMode), provider: CultureInfo.InvariantCulture);
                WriteSpacePathNewLine(writer, oldPath ?? path);
            }
            else
            {
                writer.Write(" mode change "u8);
                writer.WriteSpanFormattable(FormatMode(stat.OldMode), provider: CultureInfo.InvariantCulture);
                writer.Write(" => "u8);
                writer.WriteSpanFormattable(FormatMode(stat.NewMode), provider: CultureInfo.InvariantCulture);
                WriteSpacePathNewLine(writer, path);
            }
        }
    }

    private static void WriteSpacePathNewLine(IBufferWriter<byte> writer, GitPath path)
    {
        Span<byte> buf = writer.GetSpan(path.Length + 2);
        buf[0] = (byte)' ';
        path.Span.CopyTo(buf.Slice(1));
        buf[path.Length + 1] = (byte)'\n';
        writer.Advance(path.Length + 2);
    }

    /// <summary>Formats a file mode as 6-digit octal (%06o).</summary>
    private static OctalPadLeftFormatter FormatMode(uint mode) => new(mode, 6);

    /// <summary>Appends an integer left-justified in a fixed-width field.</summary>
    private static void AppendLeftJustified(IBufferWriter<byte> writer, int value, int width)
    {
        int valueLength = writer.WriteSpanFormattable(value, provider: CultureInfo.InvariantCulture);
        int pad = width - valueLength;
        if (pad > 0)
        {
            Span<byte> buf = writer.GetSpan(pad);
            buf.Slice(0, pad).Fill((byte)' ');
            writer.Advance(pad);
        }
    }
}
