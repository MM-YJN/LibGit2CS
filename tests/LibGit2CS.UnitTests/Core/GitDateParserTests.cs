using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public class GitDateParserTests
{
    [Fact]
    public void TryParseObjectHeader_ValidInput_ReturnsTime()
    {
        bool ok = GitDateParser.TryParseObjectHeader("1461698037 +0200".AsSpan(), out GitTime time);

        Assert.True(ok);
        Assert.Equal(1461698037, time.Seconds);
        Assert.Equal(120, time.OffsetMinutes);
    }

    [Fact]
    public void TryParseObjectHeader_NegativeOffset_ReturnsNegativeMinutes()
    {
        bool ok = GitDateParser.TryParseObjectHeader("100000000 -0530".AsSpan(), out GitTime time);

        Assert.True(ok);
        Assert.Equal(-330, time.OffsetMinutes);
    }

    [Fact]
    public void TryParseObjectHeader_UtcZeroOffset_ReturnsZeroMinutes()
    {
        bool ok = GitDateParser.TryParseObjectHeader("0 +0000".AsSpan(), out GitTime time);

        Assert.True(ok);
        Assert.Equal(0, time.OffsetMinutes);
    }

    [Fact]
    public void TryParseObjectHeader_RejectsNonDigitStart()
    {
        Assert.False(GitDateParser.TryParseObjectHeader("abc +0200".AsSpan(), out _));
    }

    [Fact]
    public void TryParseObjectHeader_RejectsMissingSpace()
    {
        Assert.False(GitDateParser.TryParseObjectHeader("1461698037+0200".AsSpan(), out _));
    }

    [Fact]
    public void TryParseObjectHeader_RejectsMissingSign()
    {
        Assert.False(GitDateParser.TryParseObjectHeader("1461698037 0200".AsSpan(), out _));
    }

    [Fact]
    public void TryParseObjectHeader_AllowsTrailingNewline()
    {
        bool ok = GitDateParser.TryParseObjectHeader("1461698037 +0200\n".AsSpan(), out _);

        Assert.True(ok);
    }

    [Fact]
    public void TryParseOffset_FourDigitForm_ReturnsCorrectMinutes()
    {
        int consumed = GitDateParser.TryParseOffset("+0200".AsSpan(), out int offset);

        Assert.Equal(5, consumed);
        Assert.Equal(120, offset);
    }

    [Fact]
    public void TryParseOffset_ColonForm_ReturnsCorrectMinutes()
    {
        int consumed = GitDateParser.TryParseOffset("+02:00".AsSpan(), out int offset);

        Assert.Equal(6, consumed);
        Assert.Equal(120, offset);
    }

    [Fact]
    public void TryParseOffset_NegativeOffset_ReturnsNegativeMinutes()
    {
        GitDateParser.TryParseOffset("-0530".AsSpan(), out int offset);

        Assert.Equal(-330, offset);
    }

    [Fact]
    public void TryParseOffset_RejectsInvalidSign()
    {
        Assert.Equal(0, GitDateParser.TryParseOffset("x0200".AsSpan(), out _));
    }

    [Fact]
    public void Parse_ObjectHeaderFormat_ReturnsTime()
    {
        GitTime time = GitDateParser.Parse("1461698037 +0200".AsSpan());

        Assert.Equal(1461698037, time.Seconds);
        Assert.Equal(120, time.OffsetMinutes);
    }

    [Fact]
    public void Parse_AtPrefixedObjectHeader_ReturnsTime()
    {
        GitTime time = GitDateParser.Parse("@1461698037 +0200".AsSpan());

        Assert.Equal(1461698037, time.Seconds);
    }

    [Fact]
    public void Parse_ApproxidateInput_ReturnsApproximateTime()
    {
        GitTime time = GitDateParser.Parse("2 weeks ago".AsSpan());
        Assert.True(time.Seconds > 0);
    }

    [Fact]
    public void FormatRfc2822_PositiveOffset_MatchesGitFormat()
    {
        // Commit 9264b96c in diff_format_email fixture: 2014-04-09 20:57:01 +0200.
        // UTC epoch (2014-04-09 18:57:01 UTC) = 1397069821.
        string result = GitDateParser.FormatRfc2822(1397069821, 120);

        Assert.Equal("Wed, 9 Apr 2014 20:57:01 +0200", result);
    }

    [Fact]
    public void FormatRfc2822_NegativeOffset_MatchesGitFormat()
    {
        // 2025-01-15 10:30:00 -0500 → UTC 15:30:00 → epoch 1736955000.
        string result = GitDateParser.FormatRfc2822(1736955000, -300);

        Assert.Equal("Wed, 15 Jan 2025 10:30:00 -0500", result);
    }

    [Fact]
    public void FormatRfc2822_UtcZero_MatchesGitFormat()
    {
        // 1970-01-01 00:00:00 +0000.
        string result = GitDateParser.FormatRfc2822(0, 0);

        Assert.Equal("Thu, 1 Jan 1970 00:00:00 +0000", result);
    }

    [Fact]
    public void FormatRfc2822_OffsetMinutesNotWholeHour_PadsCorrectly()
    {
        // -0530 offset → -330 minutes. 2025-01-15 10:00:00 -0530 → UTC 15:30:00.
        // C prints "%+03d%02d" of (offset/60, offset%60): for -330 the minutes
        // keep their sign → "-05-30".
        string result = GitDateParser.FormatRfc2822(1736955000, -330);

        Assert.Equal("Wed, 15 Jan 2025 10:00:00 -05-30", result);
    }
}
