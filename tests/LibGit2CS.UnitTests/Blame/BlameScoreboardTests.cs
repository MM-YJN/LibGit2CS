using LibGit2CS.Blame;

namespace LibGit2CS.UnitTests.Blame;

/// <summary>
/// Direct tests for the byte-level line index used by blame.
/// </summary>
public sealed class BlameScoreboardTests
{
    [Theory]
    [MemberData(nameof(LineCases))]
    public void IndexBlobLines_UsesRawBlobBytes(
        byte[] content,
        int expectedLineCount,
        int[] expectedLineIndex,
        (int Offset, int Length)[] expectedLines)
    {
        BlameScoreboard scoreboard = CreateScoreboard(content);

        int lineCount = scoreboard.IndexBlobLines();

        Assert.Equal(expectedLineCount, lineCount);
        Assert.Equal(expectedLineCount, scoreboard.NumLines);
        Assert.Equal(expectedLineIndex, scoreboard.LineIndex);
        Assert.Equal(expectedLines, scoreboard.Lines);
    }

    [Fact]
    public void IndexBlobLines_UsesOnlyTheSelectedMemorySlice()
    {
        byte[] storage = [0xA5, 0xA6, 0xFF, (byte)'\n', 0x80, (byte)'\n', 0xA7];
        ReadOnlyMemory<byte> content = storage.AsMemory(2, 4);
        BlameScoreboard scoreboard = CreateScoreboard(content);

        int lineCount = scoreboard.IndexBlobLines();

        Assert.True(content.Span.SequenceEqual(scoreboard.FinalBuf.Span));
        Assert.Equal(2, lineCount);
        Assert.Equal([0, 2, 4], scoreboard.LineIndex);
        Assert.Equal([(0, 1), (2, 1)], scoreboard.Lines);
    }

    public static TheoryData<byte[], int, int[], (int Offset, int Length)[]> LineCases()
        => new()
        {
            { [], 0, [0], [] },
            { "one\n"u8.ToArray(), 1, [0, 4], [(0, 3)] },
            { "one\ntwo"u8.ToArray(), 2, [0, 4, 7], [(0, 3), (4, 3)] },
            { "a\n\nb\n"u8.ToArray(), 3, [0, 2, 3, 5], [(0, 1), (2, 0), (3, 1)] },
            { "a\r\nb\r\n"u8.ToArray(), 2, [0, 3, 6], [(0, 2), (3, 2)] },
            { [0xFF, 0x00, (byte)'\n', 0x80, (byte)'\n'], 2, [0, 3, 5], [(0, 2), (3, 1)] },
        };

    private static BlameScoreboard CreateScoreboard(ReadOnlyMemory<byte> content)
        => new()
        {
            Repository = null!,
            FinalBuf = content,
        };
}
