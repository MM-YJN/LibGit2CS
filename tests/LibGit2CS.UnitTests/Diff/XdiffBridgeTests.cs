using System.Text;

using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

public class XdiffBridgeTests
{
    [Fact]
    public void BasicModification_EmitsCorrectHunkAndLines()
    {
        (List<GitDiffHunk>? hunks, List<(GitDiffHunk Hunk, GitDiffLine Line)>? lines) = ComputeDiff("line1\nline2\nline3\n", "line1\nmodified\nline3\n");

        Assert.Single(hunks);
        Assert.Equal("@@ -1,3 +1,3 @@\n", hunks[0].HeaderText);
        Assert.Equal("@@ -1,3 +1,3 @@\n"u8, hunks[0].Header.Span);

        // Context, Deletion, Addition, Context
        Assert.Equal(4, lines.Count);
        AssertLine(lines[0], GitDiffLineOrigin.Context, 1, 1, "line1\n");
        AssertLine(lines[1], GitDiffLineOrigin.Deletion, 2, -1, "line2\n");
        AssertLine(lines[2], GitDiffLineOrigin.Addition, -1, 2, "modified\n");
        AssertLine(lines[3], GitDiffLineOrigin.Context, 3, 3, "line3\n");
    }

    [Fact]
    public void NoNewlineAtEof_Addition_EmitsDelEofnl()
    {
        // New buffer ends without \n on an added line.
        (List<GitDiffHunk>? hunks, List<(GitDiffHunk Hunk, GitDiffLine Line)>? lines) = ComputeDiff("line1\n", "line1\nline2");

        Assert.Single(hunks);

        // Context(line1), Addition(line2 no \n), DelEofnl marker
        Assert.Equal(3, lines.Count);
        AssertLine(lines[0], GitDiffLineOrigin.Context, 1, 1, "line1\n");
        AssertLine(lines[1], GitDiffLineOrigin.Addition, -1, 2, "line2");
        AssertLine(lines[2], GitDiffLineOrigin.DelEofnl, -1, 2, "\n\\ No newline at end of file\n");
    }

    [Fact]
    public void NoNewlineAtEof_Deletion_EmitsAddEofnl()
    {
        // Old buffer ends without \n on a deleted line.
        (List<GitDiffHunk>? hunks, List<(GitDiffHunk Hunk, GitDiffLine Line)>? lines) = ComputeDiff("line1\nline2", "line1\n");

        Assert.Single(hunks);

        // Context(line1), Deletion(line2 no \n), AddEofnl marker
        Assert.Equal(3, lines.Count);
        AssertLine(lines[0], GitDiffLineOrigin.Context, 1, 1, "line1\n");
        AssertLine(lines[1], GitDiffLineOrigin.Deletion, 2, -1, "line2");
        AssertLine(lines[2], GitDiffLineOrigin.AddEofnl, 2, -1, "\n\\ No newline at end of file\n");
    }

    [Fact]
    public void NoNewlineAtEof_BothSides_EmitsTwoEofnlMarkers()
    {
        // Both old and new end without \n — deletion of old last line + addition
        // of new last line, each followed by its EOFNL marker.
        (List<GitDiffHunk>? hunks, List<(GitDiffHunk Hunk, GitDiffLine Line)>? lines) = ComputeDiff("a\nb", "a\nc");

        Assert.Single(hunks);

        // Context(a), Deletion(b no \n), AddEofnl, Addition(c no \n), DelEofnl
        Assert.Equal(5, lines.Count);
        AssertLine(lines[0], GitDiffLineOrigin.Context, 1, 1, "a\n");
        AssertLine(lines[1], GitDiffLineOrigin.Deletion, 2, -1, "b");
        AssertLine(lines[2], GitDiffLineOrigin.AddEofnl, 2, -1, "\n\\ No newline at end of file\n");
        AssertLine(lines[3], GitDiffLineOrigin.Addition, -1, 2, "c");
        AssertLine(lines[4], GitDiffLineOrigin.DelEofnl, -1, 2, "\n\\ No newline at end of file\n");
    }

    [Fact]
    public void IdenticalContent_ProducesNoHunks()
    {
        (List<GitDiffHunk>? hunks, List<(GitDiffHunk Hunk, GitDiffLine Line)>? lines) = ComputeDiff("same\n", "same\n");
        Assert.Empty(hunks);
        Assert.Empty(lines);
    }

    [Fact]
    public void FuncnameExtractor_IncludedInHunkHeader()
    {
        // The change is at line 5; with context=3 the hunk starts at line 2,
        // so the funcname search starts at line 1 (s1-1=0) and finds "def foo():".
        string oldText = "def foo():\n    pass\n    a = 1\n    b = 2\n    c = 3\n";
        string newText = "def foo():\n    pass\n    a = 1\n    b = 2\n    c = 4\n";

        static (bool IsMatch, Range NameRange) Extractor(ReadOnlySpan<byte> line)
        {
            if (line.Length > 4 && line[0] == (byte)'d' && line[1] == (byte)'e' &&
                line[2] == (byte)'f' && line[3] == (byte)' ')
            {
                // Trim trailing whitespace/newline
                int len = line.Length;
                while (len > 0 && (line[len - 1] is (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)'\t'))
                {
                    len--;
                }

                return (true, 0..len);
            }

            return (false, default);
        }

        (List<GitDiffHunk>? hunks, List<(GitDiffHunk Hunk, GitDiffLine Line)> _) = ComputeDiff(oldText, newText, extractor: Extractor);

        Assert.Single(hunks);
        Assert.StartsWith("@@ -2,4 +2,4 @@ ", hunks[0].HeaderText);
        Assert.Contains("def foo():", hunks[0].HeaderText);
    }

    [Fact]
    public void FuncnameExtractor_NonFirstLine_UsesLineRelativeRange()
    {
        // Regression: the extractor returns a Range relative to the single line
        // span, and SliceFuncName applies it there — not to the full-file span.
        // When the funcname match is on a non-first line (rec.Offset > 0), using
        // the wrong span slices the wrong bytes. Here the match is on line 3 (0-indexed
        // line 2), so the funcname must be "def bar():" — not bytes from the
        // start of the file. The change is on line 7 so that with default
        // context=3 the search (s1-1=2) reaches the "def bar():" line.
        string oldText = "module top\n\ndef bar():\n    x = 1\n    y = 2\n    z = 3\n    w = 4\n";
        string newText = "module top\n\ndef bar():\n    x = 1\n    y = 2\n    z = 3\n    w = 5\n";

        static (bool IsMatch, Range NameRange) Extractor(ReadOnlySpan<byte> line)
        {
            if (line.Length > 4 && line[0] == (byte)'d' && line[1] == (byte)'e' &&
                line[2] == (byte)'f' && line[3] == (byte)' ')
            {
                int len = line.Length;
                while (len > 0 && (line[len - 1] is (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)'\t'))
                {
                    len--;
                }

                return (true, 0..len);
            }

            return (false, default);
        }

        (List<GitDiffHunk>? hunks, List<(GitDiffHunk Hunk, GitDiffLine Line)> _) = ComputeDiff(oldText, newText, extractor: Extractor);

        Assert.Single(hunks);
        Assert.Contains("def bar():", hunks[0].HeaderText);
        Assert.DoesNotContain("module top", hunks[0].HeaderText);
    }

    private static (List<GitDiffHunk> Hunks, List<(GitDiffHunk Hunk, GitDiffLine Line)> Lines) ComputeDiff(
        string oldText,
        string newText,
        GitDiffOptions? options = null,
        Func<ReadOnlySpan<byte>, (bool IsMatch, Range NameRange)>? extractor = null)
    {
        var hunks = new List<GitDiffHunk>();
        XdiffBridge.Compute(
            hunks,
            Encoding.UTF8.GetBytes(oldText),
            Encoding.UTF8.GetBytes(newText),
            options ?? new GitDiffOptions(),
            extractor);

        var lines = hunks
            .SelectMany(h => h.Lines.Select(l => (h, l)))
            .ToList();

        return (hunks, lines);
    }

    private static void AssertLine(
        (GitDiffHunk Hunk, GitDiffLine Line) entry,
        GitDiffLineOrigin origin,
        int oldLine,
        int newLine,
        string content)
    {
        Assert.Equal(origin, entry.Line.Origin);
        Assert.Equal(oldLine, entry.Line.OldLine);
        Assert.Equal(newLine, entry.Line.NewLine);
        Assert.Equal(content, Encoding.UTF8.GetString(entry.Line.Content.Span));
    }
}
