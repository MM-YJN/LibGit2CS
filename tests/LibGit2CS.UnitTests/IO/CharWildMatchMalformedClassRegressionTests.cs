using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

// the char-based
// matcher (DoWild) treated a malformed POSIX class like "[[:alpha]]" as a
// one-member set {'['} and skipped to the first ']'. C's dowild rewinds
// `p = s - 2; p_ch = '['; continue` and its do-while condition
// `(prev_ch = p_ch, (p_ch = *++p) != ']')` ADVANCES p (wildmatch.c:281-289),
// so ':' and every inner char become class members and only the FIRST ']'
// terminates the class — the trailing ']' then needs a text char, which C
// answers with WM_ABORT_ALL. Expected results were verified against the
// reference C dowild (compiled from src/util/wildmatch.c): NOMATCH/ABORT_ALL
// map to `false`, MATCH maps to `true` — matching the byte-matcher regression
// tests.
public sealed class CharWildMatchMalformedClassRegressionTests
{
    [Fact]
    public void MalformedAlphaClass_MatchesC()
    {
        // C: WM_NOMATCH.
        Assert.False(WildMatch.IsMatch("[[:alpha]]", "x"));
        Assert.False(WildMatch.IsMatch("[[:alpha]]", "x", WildMatchFlags.Pathname));

        // C: WM_ABORT_ALL — the trailing ']' has no text char to consume
        // (a class that is just {'['} would match "[").
        Assert.False(WildMatch.IsMatch("[[:alpha]]", "["));
        Assert.False(WildMatch.IsMatch("[[:alpha]]", "a"));
        Assert.False(WildMatch.IsMatch("[[:alpha]]", ":"));

        // C: WM_MATCH — the malformed class degrades to the literal set
        // { '[', ':', 'a', 'l', 'p', 'h', 'a' } and the trailing ']' is
        // consumed by the second text char.
        Assert.True(WildMatch.IsMatch("[[:alpha]]", "[]"));
        Assert.True(WildMatch.IsMatch("[[:alpha]]", ":]"));
        Assert.True(WildMatch.IsMatch("[[:alpha]]", "a]"));

        // A single class member alone is not enough — the trailing ']'
        // still needs a text char (C: WM_ABORT_ALL).
        Assert.False(WildMatch.IsMatch("[[:alpha]]", "a"));
    }

    [Fact]
    public void MalformedDigitClass_MatchesC()
    {
        // C: WM_NOMATCH.
        Assert.False(WildMatch.IsMatch("[[:digit]", "5"));
        Assert.False(WildMatch.IsMatch("[[:digit]", "5", WildMatchFlags.Pathname));
    }

    [Fact]
    public void MalformedShortClass_MatchesC()
    {
        // C: WM_ABORT_ALL.
        Assert.False(WildMatch.IsMatch("[[:x]]", "x"));
        Assert.False(WildMatch.IsMatch("[[:x]]", "["));

        // C: WM_MATCH.
        Assert.True(WildMatch.IsMatch("[[:x]]", "[]"));
        Assert.True(WildMatch.IsMatch("[[:x]]", "x]"));
    }

    [Fact]
    public void MalformedClass_UnderCasefold_MatchesC()
    {
        // C: WM_ABORT_ALL — 'A' folds to 'a', which is a class member, but
        // the trailing ']' then has no text char to consume.
        Assert.False(WildMatch.IsMatch("[[:alpha]]", "A", WildMatchFlags.CaseInsensitive));

        // C: WM_MATCH.
        Assert.True(WildMatch.IsMatch("[[:alpha]]", "A]", WildMatchFlags.CaseInsensitive));
    }

    [Fact]
    public void MalformedClass_UnderPathname_MatchesC()
    {
        // C: WM_MATCH.
        Assert.True(WildMatch.IsMatch("[[:alpha]]", "[]", WildMatchFlags.Pathname));
    }
}
