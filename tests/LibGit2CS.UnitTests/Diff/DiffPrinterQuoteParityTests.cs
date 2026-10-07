using System.Text;

using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Regression tests for the DiffPrinter path-quoting parity behavior in
/// libgit2 1.9.4. The printer must quote
/// patch paths like C's <c>git_str_quote</c> (str.c:924-968) — applied by
/// <c>diff_delta_format_path</c> (diff_print.c:333-345) to the prefixed
/// <c>diff --git</c> / <c>---</c> / <c>+++</c> / <c>Binary files</c> paths,
/// and by <c>diff_delta_format_similarity_header</c> (diff_print.c:375-401)
/// to the raw <c>rename from</c>/<c>rename to</c>/<c>copy from</c>/<c>copy
/// to</c> paths.
/// Expected outputs were differentially verified against the C reference
/// (libgit2 1.9.4) with a harness that built a real repo with
/// non-ASCII/control-character filenames and printed <c>GIT_DIFF_FORMAT_PATCH</c>:
/// <code>
/// diff --git "a/caf\303\251.txt" "b/caf\303\251.txt"
/// --- "a/caf\303\251.txt"
/// +++ "b/caf\303\251.txt"
/// Binary files "a/bin\303\251.dat" and "b/bin\303\251.dat" differ
/// rename from "ren\303\251.txt"
/// </code>
/// The tests round-trip through <see cref="GitDiff.FromBuffer"/> with
/// raw UTF-8 paths (the parser stores the raw bytes; the printer must quote
/// them on output).
/// </summary>
public class DiffPrinterQuoteParityTests
{
    private static async Task<string> RenderPatchAsync(string patchText, CancellationToken ct)
    {
        var diff = GitDiff.FromBuffer(patchText);
        var sb = new StringBuilder();
        await diff.PrintAsync(GitDiffPrintFormat.Patch, (_, _, line) =>
        {
            sb.Append(Encoding.UTF8.GetString(line.Content.Span));
        }, ct);
        return sb.ToString();
    }

    [Fact]
    public async Task NonAsciiPath_HeaderPathsAreQuotedWithOctalEscapes()
    {
        // C: diff --git "a/caf\303\251.txt" "b/caf\303\251.txt" + quoted
        // ---/+++ lines. All four paths are quoted, not unquoted.
        string patch =
            "diff --git a/caf\u00e9.txt b/caf\u00e9.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/caf\u00e9.txt\n" +
            "+++ b/caf\u00e9.txt\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+b\n";

        string output = await RenderPatchAsync(patch, TestContext.Current.CancellationToken);

        Assert.Contains("diff --git \"a/caf\\303\\251.txt\" \"b/caf\\303\\251.txt\"\n", output);
        Assert.Contains("--- \"a/caf\\303\\251.txt\"\n", output);
        Assert.Contains("+++ \"b/caf\\303\\251.txt\"\n", output);
    }

    [Fact]
    public async Task TabInPath_QuotedWithTabEscape()
    {
        // C: "a/ta\tb.txt" — control bytes use the \a..\r escape map.
        string patch =
            "diff --git a/ta\tb.txt b/ta\tb.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/ta\tb.txt\n" +
            "+++ b/ta\tb.txt\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+b\n";

        string output = await RenderPatchAsync(patch, TestContext.Current.CancellationToken);

        Assert.Contains("diff --git \"a/ta\\tb.txt\" \"b/ta\\tb.txt\"\n", output);
        Assert.Contains("--- \"a/ta\\tb.txt\"\n", output);
        Assert.Contains("+++ \"b/ta\\tb.txt\"\n", output);
    }

    [Fact]
    public async Task DoubleQuoteInPath_EscapedAsBackslashQuote()
    {
        // C: "a/a\"b.txt".
        string patch =
            "diff --git a/a\"b.txt b/a\"b.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/a\"b.txt\n" +
            "+++ b/a\"b.txt\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+b\n";

        string output = await RenderPatchAsync(patch, TestContext.Current.CancellationToken);

        Assert.Contains("diff --git \"a/a\\\"b.txt\" \"b/a\\\"b.txt\"\n", output);
    }

    [Fact]
    public async Task RenamePaths_RawPathIsQuoted()
    {
        // C: rename from "ren\303\251.txt" / rename to renamed.txt — the
        // from/to lines quote the RAW path (no prefix), and only when needed.
        string patch =
            "diff --git a/ren\u00e9.txt b/renamed.txt\n" +
            "similarity index 100%\n" +
            "rename from ren\u00e9.txt\n" +
            "rename to renamed.txt\n";

        string output = await RenderPatchAsync(patch, TestContext.Current.CancellationToken);

        Assert.Contains("diff --git \"a/ren\\303\\251.txt\" b/renamed.txt\n", output);
        Assert.Contains("rename from \"ren\\303\\251.txt\"\n", output);
        Assert.Contains("rename to renamed.txt\n", output);
    }

    [Fact]
    public async Task BinaryFilesLine_QuotedPrefixedPaths()
    {
        // C: Binary files "a/bin\303\251.dat" and "b/bin\303\251.dat" differ.
        string patch =
            "diff --git a/bin\u00e9.dat b/bin\u00e9.dat\n" +
            "index 9583496..d775202 100644\n" +
            "Binary files a/bin\u00e9.dat and b/bin\u00e9.dat differ\n";

        string output = await RenderPatchAsync(patch, TestContext.Current.CancellationToken);

        Assert.Contains("Binary files \"a/bin\\303\\251.dat\" and \"b/bin\\303\\251.dat\" differ\n", output);
    }

    [Fact]
    public async Task PlainAsciiPath_NotQuoted()
    {
        // No quoting when nothing needs it — the common case must stay
        // byte-identical.
        string patch =
            "diff --git a/plain.txt b/plain.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/plain.txt\n" +
            "+++ b/plain.txt\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+b\n";

        string output = await RenderPatchAsync(patch, TestContext.Current.CancellationToken);

        Assert.Contains("diff --git a/plain.txt b/plain.txt\n", output);
        Assert.Contains("--- a/plain.txt\n", output);
        Assert.Contains("+++ b/plain.txt\n", output);
    }
}
