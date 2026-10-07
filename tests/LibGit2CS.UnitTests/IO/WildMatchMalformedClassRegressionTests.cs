using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

// the byte-based
// matcher (DoWildBytes) infinite-looped on malformed POSIX class patterns
// such as "[[:alpha]]" (a '[' followed by ':' whose class is not closed by
// ":]"). C dowild (wildmatch.c:281-289) rewinds `p = s - 2; p_ch = '['`
// and its do-while condition `(prev_ch = p_ch, (p_ch = *++p) != ']')`
// ADVANCES p, so the literal-'[' handling cannot re-enter the malformed
// branch; the C# port's `continue` jumped straight to the condition
// without advancing p and re-entered the branch forever.
//
// Expected results below were verified against the reference C dowild
// (compiled from src/util/wildmatch.c): NOMATCH/ABORT_ALL map to `false`,
// MATCH maps to `true`.
public sealed class WildMatchMalformedClassRegressionTests
{
    // Runs the byte matcher on a pool thread so a regression (infinite
    // loop) surfaces as a TimeoutException — a test failure — instead of
    // hanging the whole test runner.
    private static async Task<bool> MatchWithTimeoutAsync(
        ReadOnlyMemory<byte> pattern,
        ReadOnlyMemory<byte> text,
        WildMatchFlags flags = WildMatchFlags.None)
    {
        return await Task.Run(() => WildMatch.IsMatch(pattern.Span, text.Span, flags))
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MalformedAlphaClass_DoesNotHang_MatchesC()
    {
        ReadOnlyMemory<byte> pattern = "[[:alpha]]"u8.ToArray();

        // C: WM_NOMATCH.
        Assert.False(await MatchWithTimeoutAsync(pattern, "x"u8.ToArray()));

        // C: WM_NOMATCH under WM_PATHNAME too.
        Assert.False(await MatchWithTimeoutAsync(pattern, "x"u8.ToArray(), WildMatchFlags.Pathname));

        // C: WM_ABORT_ALL — the trailing ']' has no text char to consume.
        Assert.False(await MatchWithTimeoutAsync(pattern, "["u8.ToArray()));
        Assert.False(await MatchWithTimeoutAsync(pattern, "a"u8.ToArray()));
        Assert.False(await MatchWithTimeoutAsync(pattern, ":"u8.ToArray()));

        // C: WM_MATCH — the malformed class degrades to a literal set
        // { '[', ':', 'a', 'l', 'p', 'h', 'a' } and the trailing ']' is
        // consumed by the second text char.
        Assert.True(await MatchWithTimeoutAsync(pattern, "[]"u8.ToArray()));
    }

    [Fact]
    public async Task MalformedDigitClass_DoesNotHang_MatchesC()
    {
        // C: WM_NOMATCH (both with and without WM_PATHNAME).
        Assert.False(await MatchWithTimeoutAsync("[[:digit]"u8.ToArray(), "5"u8.ToArray()));
        Assert.False(await MatchWithTimeoutAsync("[[:digit]"u8.ToArray(), "5"u8.ToArray(), WildMatchFlags.Pathname));
    }

    [Fact]
    public async Task MalformedShortClass_DoesNotHang_MatchesC()
    {
        ReadOnlyMemory<byte> pattern = "[[:x]]"u8.ToArray();

        // C: WM_ABORT_ALL.
        Assert.False(await MatchWithTimeoutAsync(pattern, "x"u8.ToArray()));
        Assert.False(await MatchWithTimeoutAsync(pattern, "["u8.ToArray()));

        // C: WM_MATCH.
        Assert.True(await MatchWithTimeoutAsync(pattern, "[]"u8.ToArray()));
        Assert.True(await MatchWithTimeoutAsync(pattern, "x]"u8.ToArray()));
    }

    [Fact]
    public async Task MalformedClass_UnderCasefold_DoesNotHang()
    {
        // C: WM_ABORT_ALL — 'A' folds to 'a', which is a class member, but
        // the trailing ']' then has no text char to consume.
        Assert.False(await MatchWithTimeoutAsync("[[:alpha]]"u8.ToArray(), "A"u8.ToArray(), WildMatchFlags.CaseInsensitive));
    }

    [Fact]
    public async Task MalformedClass_UnderPathname_MatchesC()
    {
        // C: WM_MATCH.
        Assert.True(await MatchWithTimeoutAsync("[[:alpha]]"u8.ToArray(), "[]"u8.ToArray(), WildMatchFlags.Pathname));
    }
}
