using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

public class PatchGeneratorTests
{
    [Fact]
    public async Task FromBuffers_Modified_ProducesHunks()
    {
        byte[] oldBuf = "line1\nline2\nline3\n"u8.ToArray();
        byte[] newBuf = "line1\nmodified\nline3\n"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        Assert.Equal(1, (await patch.GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken)));
        GitDiffHunk? hunk = await patch.GetHunkAsync(0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(hunk);
        Assert.Equal(1, hunk!.OldStart);
        Assert.Equal(3, hunk.OldCount);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(3, hunk.NewCount);

        // Context, Deletion, Addition, Context
        Assert.Equal(4, hunk.Lines.Count);
        Assert.Equal(GitDiffLineOrigin.Context, hunk.Lines[0].Origin);
        Assert.Equal(GitDiffLineOrigin.Deletion, hunk.Lines[1].Origin);
        Assert.Equal(GitDiffLineOrigin.Addition, hunk.Lines[2].Origin);
        Assert.Equal(GitDiffLineOrigin.Context, hunk.Lines[3].Origin);
    }

    [Fact]
    public async Task FromBuffers_Identical_ProducesNoHunks()
    {
        byte[] buf = "same content\nsame\n"u8.ToArray();

        using GitPatch patch = CreatePatch(buf, buf);

        Assert.Equal(0, (await patch.GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task FromBuffers_AddedLine()
    {
        byte[] oldBuf = "a\nb\n"u8.ToArray();
        byte[] newBuf = "a\nb\nc\n"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        Assert.Equal(1, (await patch.GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken)));
        GitDiffHunk? hunk = await patch.GetHunkAsync(0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(hunk);
        Assert.Equal(GitDiffLineOrigin.Addition, hunk.Lines[^1].Origin);
    }

    [Fact]
    public async Task FromBuffers_LineStats()
    {
        byte[] oldBuf = "keep\ndelete1\ndelete2\nkeep\n"u8.ToArray();
        byte[] newBuf = "keep\nadd1\nadd2\nkeep\n"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        (int context, int additions, int deletions) = await patch.LineStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, context);   // two "keep" context lines
        Assert.Equal(2, additions); // add1, add2
        Assert.Equal(2, deletions); // delete1, delete2
    }

    [Fact]
    public async Task FromBuffers_Delta_StatusIsModified()
    {
        byte[] oldBuf = "old\n"u8.ToArray();
        byte[] newBuf = "new\n"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        Assert.Equal(GitDeltaStatus.Modified, patch.Delta.Status);
    }

    [Fact]
    public async Task FromBuffers_EofnlMarker()
    {
        // Old ends with newline, new does not → DelEofnl
        byte[] oldBuf = "line\n"u8.ToArray();
        byte[] newBuf = "line"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        Assert.Equal(1, (await patch.GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken)));
        GitDiffHunk? hunk = await patch.GetHunkAsync(0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(hunk);
        // Should have context + DelEofnl marker
        Assert.Contains(hunk.Lines, l => l.Origin == GitDiffLineOrigin.DelEofnl);
    }

    [Fact]
    public async Task LineStats_EofnlMarker_NotCounted()
    {
        // Regression guard: the *_EOFNL markers ("\ No newline at end of file")
        // must NOT inflate additions/deletions — git_patch_line_stats skips them
        // (patch.c:93-128). Here old has a trailing newline and new does not, so
        // a DelEofnl marker appears in the hunk. We verify that the marker is
        // NOT included in the counts by checking that LineStats' total matches
        // only the real CONTEXT/ADDITION/DELETION lines.
        byte[] oldBuf = "line\n"u8.ToArray();
        byte[] newBuf = "line"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        GitDiffHunk? hunk = await patch.GetHunkAsync(0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(hunk);

        // The EOFNL marker must be present (otherwise the test is vacuous).
        Assert.Contains(hunk!.Lines, l => l.Origin is GitDiffLineOrigin.DelEofnl or GitDiffLineOrigin.AddEofnl);

        (int context, int additions, int deletions) = await patch.LineStatsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Count the real content lines directly from the hunk — these are the
        // only origins git_patch_line_stats counts.
        int expectedContext = 0, expectedAdds = 0, expectedDels = 0;
        foreach (GitDiffLine line in hunk.Lines)
        {
            switch (line.Origin)
            {
                case GitDiffLineOrigin.Context: expectedContext++; break;
                case GitDiffLineOrigin.Addition: expectedAdds++; break;
                case GitDiffLineOrigin.Deletion: expectedDels++; break;
            }
        }

        Assert.Equal(expectedContext, context);
        Assert.Equal(expectedAdds, additions);
        Assert.Equal(expectedDels, deletions);

        // There must be at least one EOFNL marker that was skipped.
        int eofnlCount = 0;
        foreach (GitDiffLine line in hunk.Lines)
        {
            if (line.Origin is GitDiffLineOrigin.ContextEofnl or GitDiffLineOrigin.AddEofnl or GitDiffLineOrigin.DelEofnl)
            {
                eofnlCount++;
            }
        }
        Assert.True(eofnlCount > 0, "expected at least one EOFNL marker in the hunk");
    }

    [Fact]
    public async Task LineStats_AddedFile_EofnlNotCountedAsDeletion()
    {
        // A pure addition (empty old side → new content). xdiff may emit a
        // phantom EOFNL marker for the empty old side; it must not be counted
        // as a deletion. git diff --numstat would report "1\t0" here.
        byte[] oldBuf = Array.Empty<byte>();
        byte[] newBuf = "x\n"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        (int context, int additions, int deletions) = await patch.LineStatsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, context);
        Assert.Equal(1, additions);
        Assert.Equal(0, deletions);
    }

    [Fact]
    public async Task FromBuffers_MultipleHunks()
    {
        // Two separate change regions with enough context gap
        byte[] oldBuf = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\n"u8.ToArray();
        byte[] newBuf = "A\nb\nc\nd\ne\nf\ng\nh\ni\nJ\n"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        Assert.True((await patch.GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken)) >= 1);
    }

    [Fact]
    public async Task FromBuffers_ForceBinary_DetectedAsBinary()
    {
        // Binary content with NUL bytes. Buffer sources don't auto-detect binary
        // (matching C: git_diff_file_content__load skips binary-by-content for
        // pre-loaded buffers). Use ForceBinary to mark it.
        byte[] oldBuf = new byte[] { 0, 1, 2, 3, 0, 4, 5 };
        byte[] newBuf = new byte[] { 0, 1, 2, 9, 0, 4, 5 };

        var opts = new GitDiffOptions { Flags = GitDiffOptionsFlags.ForceBinary };
        using GitPatch patch = CreatePatch(oldBuf, newBuf, opts);

        Assert.True((await patch.GetIsBinaryAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task FromBuffers_ForceBinary_NoTextHunks()
    {
        byte[] oldBuf = new byte[] { 0, 1, 2, 3, 0, 4, 5 };
        byte[] newBuf = new byte[] { 0, 1, 2, 9, 0, 4, 5 };

        var opts = new GitDiffOptions { Flags = GitDiffOptionsFlags.ForceBinary };
        using GitPatch patch = CreatePatch(oldBuf, newBuf, opts);

        // Binary files produce no text hunks
        Assert.Equal(0, (await patch.GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task NumLinesInHunk_ReturnsCorrectCount()
    {
        byte[] oldBuf = "a\nb\nc\n"u8.ToArray();
        byte[] newBuf = "a\nB\nc\n"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        Assert.Equal(1, (await patch.GetHunkCountAsync(cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(4, await patch.NumLinesInHunkAsync(0, cancellationToken: TestContext.Current.CancellationToken)); // context, del, add, context
    }

    [Fact]
    public async Task GetLineInHunk_ReturnsSpecificLine()
    {
        byte[] oldBuf = "a\nb\nc\n"u8.ToArray();
        byte[] newBuf = "a\nB\nc\n"u8.ToArray();

        using GitPatch patch = CreatePatch(oldBuf, newBuf);

        GitDiffLine line = await patch.GetLineInHunkAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken); // The deletion line
        Assert.Equal(GitDiffLineOrigin.Deletion, line.Origin);
    }

    /// <summary>
    /// Creates a Patch directly from two buffers via the internal
    /// <see cref="PatchGenerator.FromBuffers"/> factory. No repository needed.
    /// </summary>
    private static GitPatch CreatePatch(byte[] oldBuf, byte[] newBuf, GitDiffOptions? opts = null)
    {
        opts ??= new GitDiffOptions();
        var gen = PatchGenerator.FromBuffers(null!, oldBuf, newBuf, opts, null, null, new GitDiffDrivers());
        return new GitPatch(gen);
    }
}
