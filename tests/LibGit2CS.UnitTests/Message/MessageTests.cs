using Msg = LibGit2CS.Core.GitMessage;

namespace LibGit2CS.UnitTests.Message;

public sealed class MessageTests
{
    [Fact]
    public void Prettify_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Msg.Prettify(string.Empty, '#'));
    }

    [Fact]
    public void Prettify_StripsTrailingWhitespace()
    {
        string input = "line1   \nline2\t\n";
        string result = Msg.Prettify(input, '#', stripComments: false);
        Assert.Equal("line1\nline2\n", result);
    }

    [Fact]
    public void Prettify_CollapsesConsecutiveBlankLines()
    {
        string input = "para1\n\n\n\npara2\n";
        string result = Msg.Prettify(input, '#', stripComments: false);
        Assert.Equal("para1\n\npara2\n", result);
    }

    [Fact]
    public void Prettify_EnsuresTrailingNewline()
    {
        string input = "line1\nline2";
        string result = Msg.Prettify(input, '#', stripComments: false);
        Assert.Equal("line1\nline2\n", result);
    }

    [Fact]
    public void Prettify_StripsCommentLines()
    {
        string input = "line1\n# comment\nline2\n";
        string result = Msg.Prettify(input, '#', stripComments: true);
        Assert.Equal("line1\nline2\n", result);
    }

    [Fact]
    public void Prettify_KeepsCommentLinesWhenNotStripping()
    {
        string input = "line1\n# comment\nline2\n";
        string result = Msg.Prettify(input, '#', stripComments: false);
        Assert.Equal("line1\n# comment\nline2\n", result);
    }

    [Fact]
    public void Prettify_DifferentCommentChar()
    {
        string input = "line1\n; comment\nline2\n";
        string result = Msg.Prettify(input, ';', stripComments: true);
        Assert.Equal("line1\nline2\n", result);
    }

    [Fact]
    public void Prettify_LeadingBlankLinesCollapsed()
    {
        string input = "\n\n\nhello\n";
        string result = Msg.Prettify(input, '#', stripComments: false);
        Assert.Equal("hello\n", result);
    }

    [Fact]
    public void Prettify_OnlyBlankLines_ReturnsEmpty()
    {
        string input = "\n\n\n";
        string result = Msg.Prettify(input, '#', stripComments: false);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Prettify_OnlyCommentLines_ReturnsEmpty()
    {
        string input = "# comment1\n# comment2\n";
        string result = Msg.Prettify(input, '#', stripComments: true);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Prettify_StripsComments_NoBlankLineInserted()
    {
        // Stripped comment lines don't create blank separators — only real
        // blank lines do (matching C's `continue` on comment lines).
        string input = "# comment\nreal content\n# another comment\nmore content\n";
        string result = Msg.Prettify(input, '#', stripComments: true);
        Assert.Equal("real content\nmore content\n", result);
    }

    [Fact]
    public void Prettify_LineWithOnlySpaces_TreatedAsBlank()
    {
        string input = "line1\n   \nline2\n";
        string result = Msg.Prettify(input, '#', stripComments: false);
        Assert.Equal("line1\n\nline2\n", result);
    }
}
