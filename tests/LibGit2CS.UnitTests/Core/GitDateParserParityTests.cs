using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

/// <summary>
/// Regression tests for the date-parser parity behaviors in
/// libgit2 1.9.4. Expected values were
/// differentially verified against the C reference (libgit2 1.9.4,
/// <c>src/util/date.c</c>) via C <c>date</c> and <c>date_fmt</c> harnesses. All strict-parse inputs carry an explicit
/// timezone so the expectations are TZ-independent.
/// </summary>
public class GitDateParserParityTests
{
    // ── ParseDateBasic initializes tm fields to 0 instead of -1 ──

    [Fact]
    public void Rfc2822DayMonthOrdering_KeepsTheDay()
    {
        // C: 1736929800 (2025-01-15 08:30 UTC). tm_mday must start at -1 so
        // the "15" is kept (0 would drop the day and yield 1735633800).
        GitTime t = GitDateParser.Parse("Mon, 15 Jan 2025 10:30:00 +0200".AsSpan());

        Assert.Equal(1736929800, t.Seconds);
        Assert.Equal(120, t.OffsetMinutes);
    }

    [Fact]
    public void EpochOnlyInput_ParsesViaStrictEpochPath()
    {
        // C: 1461698037 (tm_gmt, no timezone adjustment).
        Assert.Equal(1461698037, GitDateParser.Parse("1461698037".AsSpan()).Seconds);
        Assert.Equal(1461698037, GitDateParser.Parse("1461698037\n".AsSpan()).Seconds);
        Assert.Equal(1461698037, GitDateParser.Parse("1461698037 +0200".AsSpan()).Seconds);
    }

    [Fact]
    public void EpochWithRejectedTimezone_StillReturnsEpoch()
    {
        // "+1560" → minutes 60 → match_tz rejects; tm_gmt keeps the raw epoch.
        // C: 1461698037 (the rejected timezone must not fall back to approxidate).
        GitTime t = GitDateParser.Parse("1461698037 +1560".AsSpan());

        Assert.Equal(1461698037, t.Seconds);
    }

    [Fact]
    public void ZeroWithZeroOffset_FallsBackToApproxidate()
    {
        // C: parse_date_basic fails (no date fields) → approxidate = now
        // (the strict parser must not "succeed" with all-zero tm fields).
        Assert.True(GitDateParser.Parse("0 +0000".AsSpan()).Seconds > 1700000000);
    }

    [Fact]
    public void OutOfRangeEpoch_FallsBackToApproxidate()
    {
        // Year 2286 is unencodable → strict parse fails in C → approxidate
        // (the public object-header path must not accept it).
        Assert.True(GitDateParser.Parse("9999999999 +0000".AsSpan()).Seconds < 5000000000);
    }

    [Fact]
    public void FarFutureDate_ParsesCorrectly()
    {
        // C: 4102444799 (2099-12-31 23:59:59 UTC). The is_date future-guard
        // must not fire off a tm_to_time_t that succeeds with hour=0.
        GitTime t = GitDateParser.Parse("2099-12-31 23:59:59 +0000".AsSpan());

        Assert.Equal(4102444799, t.Seconds);
    }

    // ── TmToTimeT leap-year decrement condition inverted ──

    [Theory]
    [InlineData("1970-03-01 00:00:00 +0000", 5097600)]    // non-leap (naive: 5184000)
    [InlineData("1972-03-01 00:00:00 +0000", 68256000)]   // leap (naive: 68169600)
    [InlineData("2000-03-01 00:00:00 +0000", 951868800)]  // leap (naive: 951782400)
    [InlineData("2005-04-07 22:13:13 +0000", 1112911993)] // non-leap (naive: 1112998393)
    public void MarchAndLaterDates_UseCLeapYearRule(string input, long expected)
    {
        Assert.Equal(expected, GitDateParser.Parse(input.AsSpan()).Seconds);
    }

    [Theory]
    [InlineData("1970-01-01 00:00:00 +0000", 0)]
    // C's tm_to_time_t decrements for month < 2 too, so Feb 29 is computed
    // with Feb-28 day arithmetic: (34*365 + 8 + 31 + 28) * 86400. The port
    // matches this quirk.
    [InlineData("2004-02-29 00:00:00 +0000", 1078012800)]
    public void JanuaryFebruaryDates_MatchC(string input, long expected)
    {
        Assert.Equal(expected, GitDateParser.Parse(input.AsSpan()).Seconds);
    }

    // ── MatchTz hh:mm branch never sets hour ──

    [Fact]
    public void ColonTimezone_SetsHour()
    {
        // C: offset 330 (a naive hour-only parse gives 30). 1112911993 - 330*60 = 1112892193.
        GitTime t = GitDateParser.Parse("2005-04-07 22:13:13 +05:30".AsSpan());

        Assert.Equal(330, t.OffsetMinutes);
        Assert.Equal(1112892193, t.Seconds);
    }

    [Fact]
    public void ColonTimezone_QuarterHour()
    {
        // C: offset 345 (a naive hour-only parse gives 45). 1112911993 - 345*60 = 1112891293.
        GitTime t = GitDateParser.Parse("2005-04-07 22:13:13 +05:45".AsSpan());

        Assert.Equal(345, t.OffsetMinutes);
        Assert.Equal(1112891293, t.Seconds);
    }

    // ── offset fallback sign flipped ──

    [Fact]
    public void LocalOffsetFallback_SignMatchesC()
    {
        // C: offset = (UTC - local) minutes; seconds = local-epoch
        // interpretation. The opposite sign would be off by 2*|offset|.
        DateTime dt = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(dt, TimeZoneInfo.Local);
        long localEpochSeconds = (long)(utc - DateTime.UnixEpoch).TotalSeconds;
        int expectedOffset = -(int)(utc - dt).TotalMinutes;

        GitTime t = GitDateParser.Parse("1970-01-01 00:00:00".AsSpan());

        Assert.Equal(expectedOffset, t.OffsetMinutes);
        Assert.Equal(localEpochSeconds, t.Seconds);
    }

    // ── public object-header path must not accept bare (non-@) input ──

    [Fact]
    public void AtPrefixedInput_StillUsesObjectHeaderMatcher()
    {
        // "@1461698037 +1560": the internal matcher (like C) does not validate
        // hours<=14/min<=59 → offset 960. This locks in the lenient @ path.
        GitTime t = GitDateParser.Parse("@1461698037 +1560".AsSpan());

        Assert.Equal(1461698037, t.Seconds);
        Assert.Equal(960, t.OffsetMinutes);
    }

    [Fact]
    public void FailedAtMatch_KeepsLocalOffsetFallback()
    {
        // "@1461698037" (no tz): match_object_header_date fails and must not
        // clobber the offset -1 sentinel (C writes outputs only on success),
        // so the epoch path falls back to the local standard offset. The
        // timestamp itself is TZ-independent (tm_gmt skips the adjustment).
        GitTime t = GitDateParser.Parse("@1461698037".AsSpan());

        Assert.Equal(1461698037, t.Seconds);
        Assert.Equal((int)TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes, t.OffsetMinutes);
    }

    [Fact]
    public void ApproxidateNever_ReturnsEpochZero()
    {
        // C's date_never does localtime_r(0) and the mktime round-trip lands
        // back on epoch 0 in every zone.
        // UTC wall-clock fields of epoch 0 as local time (18000 under
        // America/New_York).
        Assert.Equal(0, GitDateParser.Parse("never".AsSpan()).Seconds);
    }

    [Fact]
    public void ApproxidateInvalidFebruaryDate_NormalizesLikeMktime()
    {
        // 2005-02-29 does not exist: C's strict parse fails (hour unset) and
        // approxidate's mktime normalizes Feb 29 → Mar 1. With an explicit
        // tz the strict Feb-28 day arithmetic is C-verified:
        // (35*365 + 9 + 31 + 28) days * 86400 + 12h = 1109678400.
        GitTime t = GitDateParser.Parse("2005-02-29 12:00:00 +0000".AsSpan());

        Assert.Equal(1109678400, t.Seconds);
    }

    // ── FormatRfc2822 negative fractional-hour offsets ──

    [Theory]
    [InlineData(-345, "Wed, 9 Apr 2014 13:12:01 -05-45")]
    [InlineData(-90, "Wed, 9 Apr 2014 17:27:01 -01-30")]
    [InlineData(-61, "Wed, 9 Apr 2014 17:56:01 -01-1")]
    [InlineData(-60, "Wed, 9 Apr 2014 17:57:01 -0100")]
    [InlineData(-59, "Wed, 9 Apr 2014 17:58:01 +00-59")]
    [InlineData(-5, "Wed, 9 Apr 2014 18:52:01 +00-5")]
    [InlineData(-1, "Wed, 9 Apr 2014 18:56:01 +00-1")]
    [InlineData(1, "Wed, 9 Apr 2014 18:58:01 +0001")]
    [InlineData(5, "Wed, 9 Apr 2014 19:02:01 +0005")]
    [InlineData(60, "Wed, 9 Apr 2014 19:57:01 +0100")]
    [InlineData(90, "Wed, 9 Apr 2014 20:27:01 +0130")]
    [InlineData(345, "Thu, 10 Apr 2014 00:42:01 +0545")]
    [InlineData(-660, "Wed, 9 Apr 2014 07:57:01 -1100")]
    [InlineData(-1440, "Tue, 8 Apr 2014 18:57:01 -2400")]
    public void Rfc2822Format_NegativeFractionalOffsets_MatchC(int offset, string expected)
    {
        // C prints "%+03d%02d" of (offset/60, offset%60): the minutes keep
        // their own sign, not one sign plus absolute values ("-0545").
        Assert.Equal(expected, GitDateParser.FormatRfc2822(1397069821, offset));
    }
}
