using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

/// <summary> Parity tests for core primitives: (DefaultFromEnv local offset), (signature numeric leniency), (negative-zero offset sign),
/// (object-header date trailing check). </summary>
public sealed class CoreMedParityTests
{
    // ── DefaultFromEnvAsync must use the LOCAL offset, not 0 ──

    [Fact]
    public async Task DefaultFromEnv_NoDateEnv_UsesLocalOffset()
    {
        using GitContext ctx = new();
        ctx.Env["GIT_AUTHOR_NAME"] = "A U Thor";
        ctx.Env["GIT_AUTHOR_EMAIL"] = "a@b.c";
        ctx.Env["GIT_AUTHOR_DATE"] = null;
        ctx.Env["GIT_COMMITTER_NAME"] = "C O Mitter";
        ctx.Env["GIT_COMMITTER_EMAIL"] = "c@d.e";
        ctx.Env["GIT_COMMITTER_DATE"] = null;

        (GitSignature author, GitSignature committer) = await GitSignature.DefaultFromEnvAsync(
            config: null, ctx, cancellationToken: TestContext.Current.CancellationToken);

        int localOffset = (int)DateTimeOffset.Now.Offset.TotalMinutes;
        Assert.Equal(localOffset, author.When.OffsetMinutes);
        Assert.Equal(localOffset, committer.When.OffsetMinutes);
    }

    // ── git__strntol64/32 leniency in the signature parse ──

    [Theory]
    [InlineData("A U Thor <a@b.c>  123 +0200", 123L, 120)] // leading whitespace skipped
    [InlineData("A U Thor <a@b.c> 123 +02", 123L, 2)]      // variable-length tz digits
    [InlineData("A U Thor <a@b.c> 123 +020", 123L, 20)]
    [InlineData("A U Thor <a@b.c> 123 -02", 123L, -2)]
    [InlineData("A U Thor <a@b.c> +123 +0200", 123L, 120)] // sign accepted on timestamp
    [InlineData("A U Thor <a@b.c> -123 +0200", -123L, 120)]
    public void FromBuffer_CNumericLeniency_Parses(string buffer, long time, int offset)
    {
        var sig = GitSignature.FromBuffer(buffer.AsSpan());

        Assert.Equal(time, sig.When.Seconds);
        Assert.Equal(offset, sig.When.OffsetMinutes);
    }

    [Fact]
    public void FromBuffer_InvalidTimestamp_StillFails()
    {
        Assert.False(GitSignature.TryParse("A U Thor <a@b.c> abc +0200".AsSpan(), out _, out _));
    }

    [Fact]
    public void FromBuffer_NoDigitsAfterSign_StillFails()
    {
        // git__strntol64 requires at least one digit after an optional sign.
        Assert.False(GitSignature.TryParse("A U Thor <a@b.c> + +0200".AsSpan(), out _, out _));
    }

    [Fact]
    public void FromBuffer_MalformedTz_AssumesZeroOffset()
    {
        // C: strntol failure on the tz digits → offset=0 (not a parse failure).
        var sig = GitSignature.FromBuffer("A U Thor <a@b.c> 123 +zz".AsSpan());

        Assert.Equal(123, sig.When.Seconds);
        Assert.Equal(0, sig.When.OffsetMinutes);
    }

    [Fact]
    public void FromBuffer_OutOfRangeTzHours_NotStored()
    {
        // C: hours > 14 → the tz is not stored at all (offset 0, sign unset).
        var sig = GitSignature.FromBuffer("A U Thor <a@b.c> 123 +9999".AsSpan());

        Assert.Equal(0, sig.When.OffsetMinutes);
        Assert.Equal("+0000", sig.When.FormatOffset());
    }

    [Fact]
    public void FromBuffer_DoubleSignTz_CFaithfulArithmetic()
    {
        // C: digits " -02" → strntol32 skips whitespace and accepts an inner
        // sign → offset=-2 → hours*60+mins = -2 → negated by the outer '-'
        // → +2, but the outer sign '-' is stored. Faithful quirk.
        var sig = GitSignature.FromBuffer("A U Thor <a@b.c> 123 - -02".AsSpan());

        Assert.Equal(2, sig.When.OffsetMinutes);
        Assert.Equal("-0002", sig.When.FormatOffset());
    }

    // ── negative-zero offset sign must survive round-trips ──

    [Fact]
    public void FromBuffer_NegativeZeroOffset_KeepsMinusSign()
    {
        var sig = GitSignature.FromBuffer("A U Thor <a@b.c> 123 -0000".AsSpan());

        Assert.Equal(0, sig.When.OffsetMinutes);
        Assert.Equal("-0000", sig.When.FormatOffset());
        Assert.Equal("A U Thor <a@b.c> 123 -0000", sig.ToString());
    }

    [Fact]
    public void FromBuffer_MalformedNegativeTz_ZeroOffsetKeepsMinusSign()
    {
        // C: "-zz" → strntol fails → offset=0 but sign='-' is stored.
        var sig = GitSignature.FromBuffer("A U Thor <a@b.c> 123 -zz".AsSpan());

        Assert.Equal(0, sig.When.OffsetMinutes);
        Assert.Equal("-0000", sig.When.FormatOffset());
    }

    [Fact]
    public void FromBuffer_NoTimezone_PositiveZeroSign()
    {
        var sig = GitSignature.FromBuffer("A U Thor <a@b.c> 123".AsSpan());

        Assert.Equal(0, sig.When.OffsetMinutes);
        Assert.Equal("+0000", sig.When.FormatOffset());
    }

    // ── @-prefixed object-header date trailing/exact-digit check ──

    [Fact]
    public void Parse_AtPrefixedFiveDigitOffset_RejectedByObjectHeaderMatcher()
    {
        // C: match_object_header_date requires exactly 4 tz digits with
        // nothing trailing ("+52000" → rejected) and match_tz rejects the
        // 5-digit form too → the epoch path falls back to the local standard
        // offset. Parsing "5200" from "+52000" would yield offset 3120.
        GitTime t = GitDateParser.Parse("@1461698037 +52000".AsSpan());

        Assert.Equal(1461698037, t.Seconds);
        Assert.Equal((int)TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes, t.OffsetMinutes);
    }

    [Fact]
    public void Parse_AtPrefixedTrailingGarbage_ConvergesThroughGeneralParser()
    {
        // C: the object-header matcher rejects "+0200extra" (trailing garbage)
        // and the general parser re-parses "+0200" via match_tz → same result
        // (offset 120) but through the fallthrough path.
        GitTime t = GitDateParser.Parse("@1461698037 +0200extra".AsSpan());

        Assert.Equal(1461698037, t.Seconds);
        Assert.Equal(120, t.OffsetMinutes);
    }
}
