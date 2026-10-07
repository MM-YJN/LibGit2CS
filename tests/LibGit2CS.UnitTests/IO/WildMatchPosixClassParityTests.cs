using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

// The char-based matcher diverges on
// POSIX classes under casefold and on non-ASCII input. C dowild
// (wildmatch.c:97-314) folds t_ch/p_ch ASCII-only at the top of the loop and
// tests classes against the FOLDED char; the [:upper:] class additionally
// accepts ISLOWER(t_ch) under WM_CASEFOLD (wildmatch.c:295-296); all classes
// are ASCII-only via the sane_ctype table (bytes >= 0x80 are in no class).
public sealed class WildMatchPosixClassParityTests
{
    // ----- char-based matcher (config globs, refspec wildcards, revwalk globs) -----

    [Fact]
    public void UpperClass_UnderCasefold_AcceptsLowercase()
    {
        // C (wildmatch.c:295-296): ISUPPER(t_ch) || (WM_CASEFOLD && ISLOWER(t_ch)).
        Assert.True(WildMatch.IsMatch("[[:upper:]]", "q", WildMatchFlags.CaseInsensitive));
        Assert.True(WildMatch.IsMatch("[[:upper:]]", "Q", WildMatchFlags.CaseInsensitive));
    }

    [Fact]
    public void UpperClass_WithoutCasefold_RejectsLowercase()
    {
        Assert.False(WildMatch.IsMatch("[[:upper:]]", "q"));
        Assert.True(WildMatch.IsMatch("[[:upper:]]", "Q"));
    }

    [Fact]
    public void LowerClass_UnderCasefold_TestsFoldedChar()
    {
        // C folds 'Q' -> 'q' first, then ISLOWER('q') matches.
        Assert.True(WildMatch.IsMatch("[[:lower:]]", "Q", WildMatchFlags.CaseInsensitive));
        Assert.True(WildMatch.IsMatch("[[:lower:]]", "q", WildMatchFlags.CaseInsensitive));
    }

    [Fact]
    public void LowerClass_WithoutCasefold_RejectsUppercase()
    {
        Assert.False(WildMatch.IsMatch("[[:lower:]]", "Q"));
        Assert.True(WildMatch.IsMatch("[[:lower:]]", "q"));
    }

    [Fact]
    public void Classes_AreAsciiOnly_CharBased()
    {
        // sane_ctype maps bytes 128..255 to 0 — non-ASCII chars are in NO class.
        Assert.False(WildMatch.IsMatch("[[:cntrl:]]", "\u0080"));
        Assert.False(WildMatch.IsMatch("[[:space:]]", "\u00A0")); // NBSP
        Assert.False(WildMatch.IsMatch("[[:upper:]]", "\u00C9")); // É
        Assert.False(WildMatch.IsMatch("[[:lower:]]", "\u00E0")); // à
        Assert.False(WildMatch.IsMatch("[[:alnum:]]", "\u00E9")); // é
    }

    [Fact]
    public void CaseFold_IsAsciiOnly_CharBased()
    {
        // C tolower() folds 'A'-'Z' only — É vs é never folds.
        Assert.False(WildMatch.IsMatch("\u00E9", "\u00C9", WildMatchFlags.CaseInsensitive));
        Assert.False(WildMatch.IsMatch("\u00C9", "\u00E9", WildMatchFlags.CaseInsensitive));
        Assert.False(WildMatch.IsMatch("[[:upper:]]", "\u00E9", WildMatchFlags.CaseInsensitive));
    }

    // ----- byte-based matcher (status/ignore/pathspec paths) -----

    [Fact]
    public void UpperClass_UnderCasefold_AcceptsLowercase_Bytes()
    {
        Assert.True(WildMatch.IsMatch("[[:upper:]]"u8, "q"u8, WildMatchFlags.CaseInsensitive));
        Assert.True(WildMatch.IsMatch("[[:upper:]]"u8, "Q"u8, WildMatchFlags.CaseInsensitive));
    }

    [Fact]
    public void LowerClass_UnderCasefold_TestsFoldedChar_Bytes()
    {
        Assert.True(WildMatch.IsMatch("[[:lower:]]"u8, "Q"u8, WildMatchFlags.CaseInsensitive));
    }

    [Fact]
    public void Classes_AreAsciiOnly_Bytes()
    {
        Assert.False(WildMatch.IsMatch("[[:cntrl:]]"u8, new byte[] { 0x80 }));
        Assert.False(WildMatch.IsMatch("[[:space:]]"u8, [0xC2, 0xA0])); // UTF-8 NBSP
        Assert.False(WildMatch.IsMatch("[[:upper:]]"u8, [0xC3, 0x89])); // UTF-8 É
        Assert.False(WildMatch.IsMatch("[[:lower:]]"u8, [0xC3, 0xA9])); // UTF-8 é
    }

    [Fact]
    public void GraphClass_ExcludesDel()
    {
        // isgraph(0x7F) is false in the C locale (DEL is a control char).
        Assert.False(WildMatch.IsMatch("[[:graph:]]"u8, new byte[] { 0x7F }));
        Assert.False(WildMatch.IsMatch("[[:graph:]]", "\u007F"));
        Assert.True(WildMatch.IsMatch("[[:graph:]]"u8, "~"u8));
    }

    [Fact]
    public void CntrlClass_IncludesAsciiControlsOnly()
    {
        Assert.True(WildMatch.IsMatch("[[:cntrl:]]"u8, new byte[] { 0x01 }));
        Assert.True(WildMatch.IsMatch("[[:cntrl:]]"u8, new byte[] { 0x7F }));
        Assert.False(WildMatch.IsMatch("[[:cntrl:]]"u8, new byte[] { 0x20 }));
    }

    // ----- escaped characters are not folded -----

    [Fact]
    public void EscapedChar_IsNotFolded_UnderCasefold()
    {
        // C (wildmatch.c:112-119): the char after '\' is compared raw; only the
        // top-of-loop t_ch/p_ch are folded. "\A" never matches "a".
        Assert.False(WildMatch.IsMatch("\\A", "a", WildMatchFlags.CaseInsensitive));
        Assert.False(WildMatch.IsMatch("\\A"u8, "a"u8, WildMatchFlags.CaseInsensitive));
        Assert.True(WildMatch.IsMatch("\\a", "a", WildMatchFlags.CaseInsensitive));
        Assert.True(WildMatch.IsMatch("\\a"u8, "a"u8, WildMatchFlags.CaseInsensitive));
        // The folded text char still compares case-insensitively against a
        // lowercase escaped literal: "\a" matches "A".
        Assert.True(WildMatch.IsMatch("\\a", "A", WildMatchFlags.CaseInsensitive));
    }

    // ----- flag numeric values match C -----

    [Fact]
    public void FlagValues_MatchC()
    {
        // wildmatch.h:13-14: WM_CASEFOLD = 1, WM_PATHNAME = 2.
        Assert.Equal(1, (int)WildMatchFlags.CaseInsensitive);
        Assert.Equal(2, (int)WildMatchFlags.Pathname);
    }
}
