using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

// hashsig's
// initial whitespace-skip state. C's hashsig_in_progress_init (hashsig.c)
// sets prog->use_ignores = 1 for BOTH GIT_HASHSIG_IGNORE_WHITESPACE and
// GIT_HASHSIG_SMART_WHITESPACE, so the FIRST run of a smart-whitespace hash
// skips leading non-LF whitespace before hashing — Compare("foo",
// "  foo") must score 100. Reachable via DiffTransform's default
// rename/copy detection (SmartWhitespace + AllowSmallFiles,
// diff_tform.c:343-348).
public sealed class HashsigSmartWhitespaceRegressionTests
{
    private const SimilarityHashOptions Smart =
        SimilarityHashOptions.SmartWhitespace | SimilarityHashOptions.AllowSmallFiles;

    private const SimilarityHashOptions Ignore =
        SimilarityHashOptions.IgnoreWhitespace | SimilarityHashOptions.AllowSmallFiles;

    private static int Score(string a, string b, SimilarityHashOptions options)
    {
        var sa = SimilarityHash.Create(Encoding.UTF8.GetBytes(a), options);
        var sb = SimilarityHash.Create(Encoding.UTF8.GetBytes(b), options);
        Assert.NotNull(sa);
        Assert.NotNull(sb);
        return SimilarityHash.Compare(sa, sb);
    }

    [Fact]
    public void SmartWhitespace_IndentedFirstLine_Scores100()
    {
        // C (use_ignores=1 initially): "  foo" hashes as "foo", so the two
        // signatures are identical; hashing "  foo" verbatim would score 0.
        Assert.Equal(100, Score("foo", "  foo", Smart));
        Assert.Equal(100, Score("foo", "\tfoo", Smart));
    }

    [Fact]
    public void SmartWhitespace_Multiline_FirstLineIndented_Scores100()
    {
        // Same divergence on the first line of a multi-line file.
        Assert.Equal(100, Score("foo\nbar\n", "  foo\nbar\n", Smart));
    }

    [Fact]
    public void SmartWhitespace_OnlyLaterLinesIndented_Scores100()
    {
        // Control: after the first newline the toggle logic already skips
        // leading whitespace in both.
        Assert.Equal(100, Score("foo\nbar\n", "foo\n  bar\n", Smart));
    }

    [Fact]
    public void SmartWhitespace_LeadingWhitespaceOnlyFile_Scores100()
    {
        // A file consisting of only indentation hashes to zero lines in C
        // (leading whitespace skipped, then end of data). With AllowSmallFiles
        // both create, so the hash sets are { } vs { } — 100
        // (both _lines == 0).
        Assert.Equal(100, Score("foo\n", "  \nfoo\n", Smart));
    }

    [Fact]
    public void IgnoreWhitespace_Mode_Unchanged()
    {
        // Control: IgnoreWhitespace mode already skips leading
        // whitespace before hashing.
        Assert.Equal(100, Score("foo", "  foo", Ignore));
        Assert.Equal(100, Score("foo\nbar\n", "  foo\n\tbar\n", Ignore));
    }

    [Fact]
    public void Normal_Mode_LeadingWhitespaceCounts()
    {
        // Control: without any whitespace option, leading spaces are hashed
        // on the first run in both C and the port.
        Assert.Equal(0, Score("foo", "  foo", SimilarityHashOptions.AllowSmallFiles));
    }
}
