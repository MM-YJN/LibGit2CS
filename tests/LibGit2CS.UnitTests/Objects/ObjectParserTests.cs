using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public class ObjectParserTests
{
    [Fact]
    public void Init_SingleLine_NoNewline()
    {
        var parser = new GitObjectParser(Encoding.ASCII.GetBytes("hello"));

        Assert.Equal("hello", Encoding.ASCII.GetString(parser.Line));
        Assert.Equal(1, parser.LineNumber);
        Assert.False(parser.IsAtEnd);
    }

    [Fact]
    public void Init_MultipleLines_FirstLineReady()
    {
        var parser = new GitObjectParser("tree abc123\nparent def\n"u8.ToArray());

        Assert.Equal("tree abc123", Encoding.ASCII.GetString(parser.Line));
        Assert.Equal(1, parser.LineNumber);
    }

    [Fact]
    public void Init_EmptyContent_EmptyLine()
    {
        var parser = new GitObjectParser([]);

        Assert.True(parser.Line.IsEmpty);
        Assert.True(parser.IsAtEnd);
        Assert.Equal(1, parser.LineNumber);
    }

    [Fact]
    public void AdvanceLine_MovesToNextLine()
    {
        var parser = new GitObjectParser("line1\nline2\nline3\n"u8.ToArray());

        parser.AdvanceLine();

        Assert.Equal("line2", Encoding.ASCII.GetString(parser.Line));
        Assert.Equal(2, parser.LineNumber);
    }

    [Fact]
    public void AdvanceLine_AtLastLine_MovesToEnd()
    {
        var parser = new GitObjectParser("only line\n"u8.ToArray());

        parser.AdvanceLine();

        Assert.True(parser.Line.IsEmpty);
        Assert.True(parser.IsAtEnd);
        Assert.Equal(2, parser.LineNumber);
    }

    [Fact]
    public void AdvanceLine_NoTrailingNewline()
    {
        var parser = new GitObjectParser("line1\nline2 no newline"u8.ToArray());

        parser.AdvanceLine();

        Assert.Equal("line2 no newline", Encoding.ASCII.GetString(parser.Line));
        Assert.False(parser.IsAtEnd);
    }

    [Fact]
    public void AdvanceLine_EmptyLines_PreservesCount()
    {
        var parser = new GitObjectParser("\n\n\n"u8.ToArray());

        Assert.True(parser.Line.IsEmpty);
        parser.AdvanceLine();
        Assert.True(parser.Line.IsEmpty);
        Assert.Equal(2, parser.LineNumber);
        parser.AdvanceLine();
        Assert.True(parser.Line.IsEmpty);
        Assert.Equal(3, parser.LineNumber);
        parser.AdvanceLine();
        Assert.True(parser.IsAtEnd);
        Assert.Equal(4, parser.LineNumber);
    }

    [Fact]
    public void AdvanceChars_ConsumesWithinLine()
    {
        var parser = new GitObjectParser("tree abc\n"u8.ToArray());

        parser.AdvanceChars(5); // "tree "

        Assert.Equal("abc", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceChars_ExceedsLineLength_Throws()
    {
        var parser = new GitObjectParser("ab\n"u8.ToArray());

        try
        {
            parser.AdvanceChars(3);
            Assert.Fail("Expected ArgumentException was not thrown.");
        }
        catch (ArgumentException)
        {
            // expected
        }
    }

    [Fact]
    public void AdvanceExpected_Match_AdvancesAndReturnsTrue()
    {
        var parser = new GitObjectParser("tree abc\n"u8.ToArray());

        Assert.True(parser.AdvanceExpected("tree"u8));
        Assert.Equal(" abc", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceExpected_NoMatch_ReturnsFalseNoAdvance()
    {
        var parser = new GitObjectParser("tree abc\n"u8.ToArray());

        Assert.False(parser.AdvanceExpected("blob"u8));
        Assert.Equal("tree abc", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceExpected_StringOverload()
    {
        var parser = new GitObjectParser("commit 123\n"u8.ToArray());

        Assert.True(parser.AdvanceExpected("commit"));
        Assert.Equal(" 123", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceWhitespace_SkipsSpacesAndTabs()
    {
        var parser = new GitObjectParser("  \t  value\n"u8.ToArray());

        Assert.True(parser.AdvanceWhitespace());
        Assert.Equal("value", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceWhitespace_NoWhitespace_ReturnsFalse()
    {
        var parser = new GitObjectParser("value\n"u8.ToArray());

        Assert.False(parser.AdvanceWhitespace());
        Assert.Equal("value", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceNewline_AtNewline_AdvancesLine()
    {
        var parser = new GitObjectParser("line1\n\nline3\n"u8.ToArray());

        parser.AdvanceLine(); // past "line1"
        Assert.True(parser.AdvanceNewline()); // skip the empty line
        Assert.Equal("line3", Encoding.ASCII.GetString(parser.Line));
        Assert.Equal(3, parser.LineNumber);
    }

    [Fact]
    public void AdvanceNewline_NotAtNewline_ReturnsFalse()
    {
        var parser = new GitObjectParser("line1\n"u8.ToArray());

        Assert.False(parser.AdvanceNewline());
        Assert.Equal("line1", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceDigit_ParsesDecimalAndAdvances()
    {
        var parser = new GitObjectParser("12345 rest\n"u8.ToArray());

        long? value = parser.AdvanceDigit();

        Assert.Equal(12345L, value);
        Assert.Equal(" rest", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceDigit_NoDigit_ReturnsNull()
    {
        var parser = new GitObjectParser("abc\n"u8.ToArray());

        Assert.Null(parser.AdvanceDigit());
    }

    [Fact]
    public void AdvanceDigit_HexRadix()
    {
        // C (parse.c:91): the first char must be a decimal digit even for
        // base 16, so a leading 'a'-'f' is a parse failure.
        var parser = new GitObjectParser("ff end\n"u8.ToArray());

        Assert.Null(parser.AdvanceDigit(16));
        Assert.Equal("ff end", Encoding.ASCII.GetString(parser.Line));

        // strntol64 skips the "0x" prefix (util.c:69-71): 0xff = 255.
        var parser2 = new GitObjectParser("0xff end\n"u8.ToArray());
        Assert.Equal(255L, parser2.AdvanceDigit(16));
        Assert.Equal(" end", Encoding.ASCII.GetString(parser2.Line));

        var parser3 = new GitObjectParser("0x end\n"u8.ToArray());
        Assert.Null(parser3.AdvanceDigit(16)); // no digits after the prefix
    }

    [Fact]
    public void AdvanceDigit_Overflow_ReturnsNullWithoutAdvancing()
    {
        // C (util.c:145-148): integer overflow fails the parse with
        // "overflow error" and does NOT advance the context.
        var parser = new GitObjectParser("99999999999999999999 rest\n"u8.ToArray());

        Assert.Null(parser.AdvanceDigit());
        Assert.Equal("99999999999999999999 rest", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceOid_Sha1_ParsesAndAdvances()
    {
        string hex = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";
        var parser = new GitObjectParser(Encoding.ASCII.GetBytes($"{hex} rest\n"));

        GitOid? oid = parser.AdvanceOid(GitHashAlgorithmKind.Sha1);

        Assert.NotNull(oid);
        Assert.Equal(hex, oid!.Value.ToString());
        Assert.Equal(" rest", Encoding.ASCII.GetString(parser.Line));
    }

    [Fact]
    public void AdvanceOid_Sha256_ParsesAndAdvances()
    {
        string hex = "473a0f4c3be8a93681a267e3b1e9a7dcda1185436fe141f7749120a303721813";
        var parser = new GitObjectParser(Encoding.ASCII.GetBytes($"{hex}\n"));

        GitOid? oid = parser.AdvanceOid(GitHashAlgorithmKind.Sha256);

        Assert.NotNull(oid);
        Assert.Equal(hex, oid!.Value.ToString());
    }

    [Fact]
    public void AdvanceOid_LineTooShort_ReturnsNull()
    {
        var parser = new GitObjectParser("abc\n"u8.ToArray());

        Assert.Null(parser.AdvanceOid(GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void AdvanceOid_InvalidHex_ReturnsNull()
    {
        var parser = new GitObjectParser(Encoding.ASCII.GetBytes("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz rest\n"));

        Assert.Null(parser.AdvanceOid(GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Peek_ReturnsFirstByteOfLine()
    {
        var parser = new GitObjectParser("tree abc\n"u8.ToArray());

        Assert.Equal('t', parser.Peek());
    }

    [Fact]
    public void Peek_EmptyLine_ReturnsMinusOne()
    {
        var parser = new GitObjectParser("\n"u8.ToArray());

        Assert.Equal(-1, parser.Peek());
    }

    [Fact]
    public void PeekSkipWhitespace_SkipsLeadingWhitespace()
    {
        var parser = new GitObjectParser("  \t  value\n"u8.ToArray());

        Assert.Equal('v', parser.PeekSkipWhitespace());
    }

    [Fact]
    public void PeekSkipWhitespace_AllWhitespace_ReturnsMinusOne()
    {
        var parser = new GitObjectParser("  \t  \n"u8.ToArray());

        Assert.Equal(-1, parser.PeekSkipWhitespace());
    }

    [Fact]
    public void LineStartsWith_Match_ReturnsTrue()
    {
        var parser = new GitObjectParser("parent abc\n"u8.ToArray());

        Assert.True(parser.LineStartsWith("parent"u8));
    }

    [Fact]
    public void LineStartsWith_NoMatch_ReturnsFalse()
    {
        var parser = new GitObjectParser("tree abc\n"u8.ToArray());

        Assert.False(parser.LineStartsWith("parent"u8));
    }

    [Fact]
    public void LineStartsWith_LineShorterThanExpected_ReturnsFalse()
    {
        var parser = new GitObjectParser("ab\n"u8.ToArray());

        Assert.False(parser.LineStartsWith("abc"u8));
    }

    [Fact]
    public void Remain_ShrinksAfterAdvance()
    {
        var parser = new GitObjectParser("line1\nline2\n"u8.ToArray());

        Assert.Equal("line1\nline2\n", Encoding.ASCII.GetString(parser.Remain));
        parser.AdvanceLine();
        Assert.Equal("line2\n", Encoding.ASCII.GetString(parser.Remain));
        parser.AdvanceLine();
        Assert.True(parser.Remain.IsEmpty);
    }

    [Fact]
    public void FullCommitParse_Simulation()
    {
        // Simulates parsing a minimal commit body:
        // tree <oid>\nparent <oid>\nauthor ...\ncommitter ...\n\nmessage
        // The raw string literal inherits the source file's CRLF line
        // endings; C's parser rejects CRLF, so normalize to LF first.
        string raw = """
            tree e69de29bb2d1d6434b8b29ae775ad8c2e48c5391
            parent a65fedf39aefe402d3bb6e24df4d4f5fe4547750
            author Test <test@example.com> 1234567890 +0000
            committer Test <test@example.com> 1234567890 +0000

            Initial commit
            """.Replace("\r\n", "\n");
        byte[] content = Encoding.ASCII.GetBytes(raw);

        var parser = new GitObjectParser(content);

        // tree line
        Assert.True(parser.AdvanceExpected("tree"u8));
        Assert.True(parser.AdvanceWhitespace());
        GitOid? treeOid = parser.AdvanceOid(GitHashAlgorithmKind.Sha1);
        Assert.NotNull(treeOid);
        parser.AdvanceLine();

        // parent line
        Assert.True(parser.AdvanceExpected("parent"u8));
        Assert.True(parser.AdvanceWhitespace());
        GitOid? parentOid = parser.AdvanceOid(GitHashAlgorithmKind.Sha1);
        Assert.NotNull(parentOid);
        parser.AdvanceLine();

        // author line — skip the whole line
        Assert.True(parser.LineStartsWith("author"u8));
        parser.AdvanceLine();

        // committer line — skip
        Assert.True(parser.LineStartsWith("committer"u8));
        parser.AdvanceLine();

        // empty line (message separator)
        Assert.True(parser.Line.IsEmpty);
        parser.AdvanceLine();

        // message
        Assert.Equal("Initial commit", Encoding.ASCII.GetString(parser.Line));
    }
}
