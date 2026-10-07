using LibGit2CS.Index;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Merge;

/// <summary>
/// Serializes a <see cref="GitIndex"/> to the canonical text format produced
/// by <c>git ls-files -s</c>, so that merge golden tests can compare the C#
/// merge result byte-exact against reference git output.
/// </summary>
/// <remarks>
/// The <c>git ls-files -s</c> format is one entry per line:
/// <c>&lt;mode-octal-6&gt; &lt;oid-40hex&gt; &lt;stage&gt;\t&lt;path&gt;</c>
/// (e.g. <c>100644 ffb36e51... 0\tasparagus.txt</c>). Entries are emitted in
/// the index's sorted order, which matches git's on-disk order (path-then-stage
/// ascending). The tab separator and 6-digit zero-padded octal mode are
/// required for byte-exact comparison.
/// </remarks>
internal static class IndexGoldenFormatter
{
    /// <summary>
    /// Formats the index entries as <c>git ls-files -s</c> output. Each line
    /// ends with a single <c>\n</c>; the final line includes a trailing
    /// newline to match git's stdout.
    /// </summary>
    public static string FormatLsFiles(GitIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var sb = new System.Text.StringBuilder();
        foreach (GitIndexEntry entry in index.Entries)
        {
            AppendLsFilesLine(sb, entry);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats the index entries as <c>git ls-files -s</c> output, excluding
    /// stage-0 (non-conflicted) entries. Useful for golden tests that only
    /// care about the conflict entries, since the set of unconflicted files
    /// can differ between C# and git when rename detection diverges.
    /// </summary>
    public static string FormatLsFilesConflictsOnly(GitIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var sb = new System.Text.StringBuilder();
        foreach (GitIndexEntry entry in index.Entries)
        {
            if (entry.Stage == 0)
            {
                continue;
            }

            AppendLsFilesLine(sb, entry);
        }

        return sb.ToString();
    }

    private static void AppendLsFilesLine(System.Text.StringBuilder sb, GitIndexEntry entry)
    {
        // Mode: 6-digit octal, zero-padded. git uses the raw on-disk mode
        // (e.g. 0100644) but ls-files prints it without the leading 0
        // (100644). The GitFileMode enum values are the canonical ushort
        // (0x81A4 = 100644 octal), so format as octal with 6 digits.
        sb.Append(FormatModeOctal((ushort)entry.Mode));
        sb.Append(' ');
        sb.Append(entry.Id.ToString());
        sb.Append(' ');
        sb.Append(entry.Stage);
        sb.Append('\t');
        sb.Append(entry.Path);
        sb.Append('\n');
    }

    /// <summary>
    /// Formats a 16-bit mode as a 6-digit octal string (e.g. 0x81A4 → "100644").
    /// Matches <c>git ls-files</c> output which prints the full POSIX mode
    /// bits without a leading <c>0</c> prefix.
    /// </summary>
    private static string FormatModeOctal(ushort mode)
    {
        // Convert to octal and zero-pad to 6 digits. .NET has no built-in
        // octal formatter, so compute it from the bits.
        Span<char> buf = stackalloc char[6];
        for (int i = 5; i >= 0; i--)
        {
            buf[i] = (char)('0' + (mode & 0x7));
            mode >>= 3;
        }

        return new string(buf);
    }
}
