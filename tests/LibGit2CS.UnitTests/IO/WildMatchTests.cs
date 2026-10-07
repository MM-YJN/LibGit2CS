using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

public class WildMatchTests
{
    [Fact]
    public void LiteralMatch_Succeeds()
    {
        Assert.True(WildMatch.IsMatch("foo", "foo"));
    }

    [Fact]
    public void LiteralMatch_DifferentStrings_Fails()
    {
        Assert.False(WildMatch.IsMatch("foo", "bar"));
    }

    [Fact]
    public void Star_MatchesAnySequence()
    {
        Assert.True(WildMatch.IsMatch("f*", "foo"));
        Assert.True(WildMatch.IsMatch("f*", "f"));
        Assert.True(WildMatch.IsMatch("*o", "foo"));
        Assert.True(WildMatch.IsMatch("*", "anything"));
    }

    [Fact]
    public void Star_Pathname_DoesNotCrossSlash()
    {
        Assert.True(WildMatch.IsMatch("f*", "foo", WildMatchFlags.Pathname));
        Assert.False(WildMatch.IsMatch("f*", "foo/bar", WildMatchFlags.Pathname));
        Assert.True(WildMatch.IsMatch("f*/*", "foo/bar", WildMatchFlags.Pathname));
    }

    [Fact]
    public void DoubleStar_Pathname_CrossesSlash()
    {
        // "**" at start of pattern matches everything (prev_p < pattern)
        Assert.True(WildMatch.IsMatch("**", "foo/bar/baz", WildMatchFlags.Pathname));
        // "foo/**" — ** preceded by / — crosses slashes
        Assert.True(WildMatch.IsMatch("foo/**", "foo/a/b", WildMatchFlags.Pathname));
        // "foo/**/bar" matches with dirs between
        Assert.True(WildMatch.IsMatch("foo/**/bar", "foo/a/b/bar", WildMatchFlags.Pathname));
    }

    [Fact]
    public void DoubleStar_TrailingSlash_MatchesEverything()
    {
        Assert.True(WildMatch.IsMatch("foo/**", "foo/anything/here", WildMatchFlags.Pathname));
    }

    [Fact]
    public void QuestionMark_MatchesOneChar()
    {
        Assert.True(WildMatch.IsMatch("f?o", "foo"));
        Assert.False(WildMatch.IsMatch("f?o", "fooo"));
    }

    [Fact]
    public void QuestionMark_Pathname_DoesNotMatchSlash()
    {
        Assert.False(WildMatch.IsMatch("f?o", "f/o", WildMatchFlags.Pathname));
    }

    [Fact]
    public void CharacterClass_Matches()
    {
        Assert.True(WildMatch.IsMatch("[abc]", "a"));
        Assert.True(WildMatch.IsMatch("[abc]", "b"));
        Assert.True(WildMatch.IsMatch("[abc]", "c"));
        Assert.False(WildMatch.IsMatch("[abc]", "d"));
    }

    [Fact]
    public void CharacterRange_Matches()
    {
        Assert.True(WildMatch.IsMatch("[a-z]", "m"));
        Assert.False(WildMatch.IsMatch("[a-z]", "M"));
    }

    [Fact]
    public void NegatedClass_Matches()
    {
        Assert.False(WildMatch.IsMatch("[!abc]", "a"));
        Assert.True(WildMatch.IsMatch("[!abc]", "d"));
    }

    [Fact]
    public void CaseInsensitive_Matches()
    {
        Assert.False(WildMatch.IsMatch("foo", "FOO"));
        Assert.True(WildMatch.IsMatch("foo", "FOO", WildMatchFlags.CaseInsensitive));
    }

    [Fact]
    public void TrailingSlashPattern_AppendsDoubleStar()
    {
        // Used by includeIf gitdir: matching — trailing / means /**/
        Assert.True(WildMatch.IsMatch("/repo/**", "/repo/anything", WildMatchFlags.Pathname));
    }

    [Fact]
    public void DoubleSlashStar_MatchesZeroOrMoreDirs()
    {
        // foo/**/bar matches foo/bar (zero dirs between)
        Assert.True(WildMatch.IsMatch("foo/**/bar", "foo/bar", WildMatchFlags.Pathname));
        // and foo/a/bar (one dir)
        Assert.True(WildMatch.IsMatch("foo/**/bar", "foo/a/bar", WildMatchFlags.Pathname));
    }

    [Fact]
    public void Backslash_EscapesNextChar()
    {
        Assert.True(WildMatch.IsMatch("\\*", "*"));
        Assert.False(WildMatch.IsMatch("\\*", "a"));
    }

    [Fact]
    public void EmptyPattern_OnlyMatchesEmpty()
    {
        Assert.True(WildMatch.IsMatch("", ""));
        Assert.False(WildMatch.IsMatch("", "x"));
    }
}
