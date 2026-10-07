using LibGit2CS.Attributes;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.Attributes;

// ParseBuffer
// pre-split each line at the first non-escaped whitespace, so an indented
// rule like "  *.txt text" produced an EMPTY pattern span and the whole
// rule was dropped. C's git_attr_fnmatch__parse skips leading whitespace
// BEFORE scanning for the pattern/assignment split (attr_file.c:736-739),
// so indented rules are valid and must be kept.
public sealed class AttributesIndentedRulesRegressionTests
{
    [Fact]
    public void ParseBuffer_IndentedRules_AreKept()
    {
        var file = new AttributesFile();
        file.ParseBuffer("  *.txt text\n\t*.md markdown\n*.cs csharp\n", null, ignoreCase: false);

        Assert.Equal(3, file.Rules.Count);
        Assert.Equal("*.txt", file.Rules[0].Match.Pattern.ToUtf8String());
        Assert.Equal("*.md", file.Rules[1].Match.Pattern.ToUtf8String());
        Assert.Equal("*.cs", file.Rules[2].Match.Pattern.ToUtf8String());
        Assert.True(file.Rules[0].Assigns.ContainsKey("text"));
        Assert.True(file.Rules[1].Assigns.ContainsKey("markdown"));
    }

    [Fact]
    public void ParseBuffer_ByteOverload_IndentedRules_AreKept()
    {
        var file = new AttributesFile();
        file.ParseBuffer("  *.txt text\n"u8.ToArray(), null, ignoreCase: false);

        Assert.Single(file.Rules);
        Assert.Equal("*.txt", file.Rules[0].Match.Pattern.ToUtf8String());
        Assert.True(file.Rules[0].Assigns.ContainsKey("text"));
    }

    [Fact]
    public void ParseBuffer_BlankAndCommentLines_StillSkipped()
    {
        // Control: whitespace-only and comment lines remain non-rules.
        var file = new AttributesFile();
        file.ParseBuffer("   \n\t\n# comment\n  # indented comment\n*.txt text\n", null, ignoreCase: false);

        Assert.Single(file.Rules);
        Assert.Equal("*.txt", file.Rules[0].Match.Pattern.ToUtf8String());
    }

    [Fact]
    public void ParseBuffer_IndentedNegatedPattern_IsKept()
    {
        // Control: the negation flag still applies after the whitespace skip
        // (the '!' prefix is stored as the Negative flag, not in the pattern).
        var file = new AttributesFile();
        file.ParseBuffer("\t!*.tmp -text\n", null, ignoreCase: false);

        Assert.Single(file.Rules);
        Assert.Equal("*.tmp", file.Rules[0].Match.Pattern.ToUtf8String());
        Assert.True((file.Rules[0].Match.Flags & FnMatchPattern.Flag.Negative) != 0);
    }
}
