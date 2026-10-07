using System.Globalization;

using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public class GitTimeTests
{
    // ── TryFormat: sign derivation and offset rendering ──
    // Matches git_signature__writebuf (signature.c:430):
    //   sign = (offset < 0 || sign == '-') ? '-' : '+'
    // So '-0000' re-serializes as '-0000' and a preserved '-' sign overrides
    // a positive offset (signature.c:387-392).

    [Theory]
    [InlineData(1461698037, 120, '\0', "1461698037 +0200")]   // positive offset
    [InlineData(200, -330, '\0', "200 -0530")]                 // negative offset
    [InlineData(0, 0, '\0', "0 +0000")]                        // zero offset, default sign
    [InlineData(0, 0, '-', "0 -0000")]                         // libgit2 preserves '-' for zero offset
    [InlineData(100, 120, '-', "100 -0200")]                  // preserved '-' overrides positive offset
    [InlineData(100, -120, '+', "100 -0200")]                  // negative offset overrides '+' sign
    [InlineData(100, 1560, '\0', "100 +2600")]                // unvalidated offset (26h) is not clamped
    [InlineData(100, -1560, '\0', "100 -2600")]               // negative unvalidated offset
    public void TryFormat_RendersSecondsSpaceSignHHMM(long seconds, int offsetMinutes, char sign, string expected)
    {
        var t = new GitTime(seconds, offsetMinutes, sign);

        Span<char> buffer = new char[64];
        bool ok = t.TryFormat(buffer, out int charsWritten, default, provider: null);

        Assert.True(ok);
        Assert.Equal(expected.Length, charsWritten);
        Assert.Equal(expected, new string(buffer.Slice(0, charsWritten)));
    }

    [Fact]
    public void TryFormat_ExactBufferSize_Succeeds()
    {
        var t = new GitTime(1461698037, 120);

        Span<char> buffer = new char["1461698037 +0200".Length];
        bool ok = t.TryFormat(buffer, out int charsWritten, default, provider: null);

        Assert.True(ok);
        Assert.Equal(buffer.Length, charsWritten);
        Assert.Equal("1461698037 +0200", new string(buffer));
    }

    [Fact]
    public void TryFormat_BufferTooSmall_ReturnsFalseAndZeroCharsWritten()
    {
        var t = new GitTime(1461698037, 120);

        Span<char> buffer = new char["1461698037 +0200".Length - 1];
        bool ok = t.TryFormat(buffer, out int charsWritten, default, provider: null);

        Assert.False(ok);
        Assert.Equal(0, charsWritten);
    }

    [Fact]
    public void TryFormat_IgnoresFormatAndProvider()
    {
        var t = new GitTime(1461698037, 120);

        Span<char> buffer = new char[64];
        bool ok = t.TryFormat(buffer, out int charsWritten, "g".AsSpan(), CultureInfo.InvariantCulture);

        Assert.True(ok);
        Assert.Equal("1461698037 +0200", new string(buffer.Slice(0, charsWritten)));
    }

    [Theory]
    [InlineData(1461698037, 120, '\0', "1461698037 +0200")]
    [InlineData(200, -330, '\0', "200 -0530")]
    [InlineData(0, 0, '\0', "0 +0000")]
    [InlineData(0, 0, '-', "0 -0000")]
    [InlineData(100, 120, '-', "100 -0200")]
    [InlineData(100, -120, '+', "100 -0200")]
    public void ToString_WithFormatAndProvider_RendersSecondsSpaceSignHHMM(long seconds, int offsetMinutes, char sign, string expected)
    {
        var t = new GitTime(seconds, offsetMinutes, sign);

        Assert.Equal(expected, t.ToString(format: null, formatProvider: null));
    }

    [Fact]
    public void ToString_Parameterless_MatchesToStringWithNullArgs()
    {
        var t = new GitTime(1461698037, -330);

        Assert.Equal(t.ToString(format: null, formatProvider: null), t.ToString());
    }

    [Fact]
    public void ToString_IgnoresFormatAndProvider()
    {
        var t = new GitTime(1461698037, 120);

        Assert.Equal("1461698037 +0200", t.ToString("g", CultureInfo.InvariantCulture));
    }
}
