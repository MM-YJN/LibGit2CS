using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Message;

public sealed class TrailerTests
{
    private static void AssertTrailers(string message, params (string Key, string Value)[] expected)
    {
        IReadOnlyList<GitMessageTrailer> trailers = GitTrailers.Parse(message);
        Assert.Equal(expected.Length, trailers.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Key, trailers[i].Key);
            Assert.Equal(expected[i].Value, trailers[i].Value);
        }
    }

    [Fact]
    public void Simple()
    {
        AssertTrailers(
            "Message\n\nSigned-off-by: foo@bar.com\nSigned-off-by: someone@else.com\n",
            ("Signed-off-by", "foo@bar.com"),
            ("Signed-off-by", "someone@else.com"));
    }

    [Fact]
    public void NoWhitespace()
    {
        AssertTrailers(
            "Message\n\nKey:value\n",
            ("Key", "value"));
    }

    [Fact]
    public void ExtraWhitespace()
    {
        AssertTrailers(
            "Message\n\nKey   :   value\n",
            ("Key", "value"));
    }

    [Fact]
    public void NoNewline()
    {
        AssertTrailers(
            "Message\n\nKey: value",
            ("Key", "value"));
    }

    [Fact]
    public void NotLastParagraph()
    {
        AssertTrailers(
            "Message\n\nKey: value\n\nMore stuff\n");
    }

    [Fact]
    public void Conflicts()
    {
        AssertTrailers(
            "Message\n\nKey: value\n\nConflicts:\n\tfoo.c\n",
            ("Key", "value"));
    }

    [Fact]
    public void Patch()
    {
        AssertTrailers(
            "Message\n\nKey: value\n\n---\nMore: stuff\n",
            ("Key", "value"));
    }

    [Fact]
    public void Continuation()
    {
        AssertTrailers(
            "Message\n\nA: b\n c\nD: e\n f: g h\nI: j\n",
            ("A", "b\n c"),
            ("D", "e\n f: g h"),
            ("I", "j"));
    }

    [Fact]
    public void Invalid()
    {
        AssertTrailers(
            "Message\n\nSigned-off-by: some@one.com\nNot a trailer\nAnother: trailer\n",
            ("Signed-off-by", "some@one.com"),
            ("Another", "trailer"));
    }

    [Fact]
    public void IgnoresDashes()
    {
        AssertTrailers(
            "Message\n\nMarkdown header\n---------------\nLorem ipsum\n\nSigned-off-by: some@one.com\nAnother: trailer\n",
            ("Signed-off-by", "some@one.com"),
            ("Another", "trailer"));
    }

    [Fact]
    public void EmptyMessage_ReturnsEmpty()
    {
        Assert.Empty(GitTrailers.Parse(string.Empty));
    }

    [Fact]
    public void NoTrailers_ReturnsEmpty()
    {
        Assert.Empty(GitTrailers.Parse("Just a message with no trailers\n"));
    }

    [Fact]
    public void OnlyTitleNoBlankLine_ReturnsEmpty()
    {
        Assert.Empty(GitTrailers.Parse("Single line message"));
    }
}
