using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public class ParseContextTests
{
    [Fact]
    public void Init_EmptyContent_LineLengthZero()
    {
        var ctx = new ParseContext();
        ctx.Init(ReadOnlyMemory<byte>.Empty);

        Assert.Equal(0, ctx.ContentLength);
        Assert.Equal(0, ctx.LineLength);
        Assert.False(ctx.HasRemaining);
        Assert.Equal(1, ctx.LineNumber);
    }

    [Fact]
    public void Init_DefaultMemory_TreatedAsEmpty()
    {
        var ctx = new ParseContext();
        ctx.Init(default);

        Assert.True(ctx.Content.IsEmpty);
        Assert.Equal(0, ctx.ContentLength);
        Assert.False(ctx.HasRemaining);
    }

    [Fact]
    public void Init_SingleLineNoNewline_LineLengthEqualsBuffer()
    {
        var ctx = new ParseContext();
        ctx.Init("hello"u8.ToArray());

        Assert.Equal(5, ctx.ContentLength);
        Assert.Equal(5, ctx.LineLength);
        Assert.True(ctx.HasRemaining);
        Assert.Equal(1, ctx.LineNumber);
        Assert.True(ctx.Line.SequenceEqual("hello"u8));
    }

    [Fact]
    public void Init_MultiLine_LineLengthIncludesNewline()
    {
        var ctx = new ParseContext();
        ctx.Init("line1\nline2\nline3\n"u8.ToArray());

        Assert.Equal(1, ctx.LineNumber);
        Assert.Equal(6, ctx.LineLength); // "line1\n"
        Assert.True(ctx.Line.SequenceEqual("line1\n"u8));
        Assert.True(ctx.Contains("line1"u8));
    }

    [Fact]
    public void Init_NonAsciiBytes_RoundTrip()
    {
        // arbitrary bytes flow through the cursor unchanged (lines "caf\xC3\xA9\n" then "\xFF\n").
        byte[] content = [0x63, 0x61, 0x66, 0xC3, 0xA9, 0x0A, 0xFF, 0x0A];
        var ctx = new ParseContext();
        ctx.Init(content);

        Assert.Equal(content.Length, ctx.ContentLength);
        Assert.Equal(6, ctx.LineLength);
        Assert.True(ctx.Line.SequenceEqual<byte>([0x63, 0x61, 0x66, 0xC3, 0xA9, 0x0A]));

        ctx.AdvanceLine();
        Assert.True(ctx.Line.SequenceEqual<byte>([0xFF, 0x0A]));
    }

    [Fact]
    public void AdvanceLine_MovesToNextLine_IncrementsLineNumber()
    {
        var ctx = new ParseContext();
        ctx.Init("line1\nline2\n"u8.ToArray());

        ctx.AdvanceLine();

        Assert.Equal(2, ctx.LineNumber);
        Assert.Equal(6, ctx.LineLength);
        Assert.True(ctx.Line.SequenceEqual("line2\n"u8));
    }

    [Fact]
    public void AdvanceLine_LastLineWithNewline_Terminates()
    {
        var ctx = new ParseContext();
        ctx.Init("a\n"u8.ToArray());

        ctx.AdvanceLine();

        Assert.Equal(2, ctx.LineNumber);
        Assert.Equal(0, ctx.LineLength);
        Assert.False(ctx.HasRemaining);
    }

    [Fact]
    public void AdvanceChars_MovesWithinLine_ShortensLineLength()
    {
        var ctx = new ParseContext();
        ctx.Init("diff --git a/foo b/foo\n"u8.ToArray());

        ctx.AdvanceChars(10); // past "diff --git"

        Assert.Equal(13, ctx.LineLength); // remaining " a/foo b/foo\n"
        Assert.True(ctx.Line.StartsWith(" a/foo"u8));
        Assert.Equal(1, ctx.LineNumber); // same line
    }

    [Fact]
    public void AdvanceExpected_Match_Consumes()
    {
        var ctx = new ParseContext();
        ctx.Init("--- a/foo\n"u8.ToArray());

        Assert.True(ctx.AdvanceExpected("--- "u8));
        Assert.True(ctx.Line.StartsWith("a/foo"u8));
    }

    [Fact]
    public void AdvanceExpected_NoMatch_DoesNotConsume()
    {
        var ctx = new ParseContext();
        ctx.Init("+++ b/foo\n"u8.ToArray());

        Assert.False(ctx.AdvanceExpected("--- "u8));
        Assert.True(ctx.Line.StartsWith("+++"u8));
    }

    [Fact]
    public void AdvanceWs_SkipsSpaces_NotPastNewline()
    {
        var ctx = new ParseContext();
        ctx.Init("   \n"u8.ToArray());

        Assert.True(ctx.AdvanceWs());
        Assert.Equal(1, ctx.LineLength); // only "\n" remains
        Assert.Equal((byte)'\n', ctx.Line[0]);
    }

    [Fact]
    public void AdvanceWs_NoWhitespace_ReturnsFalse()
    {
        var ctx = new ParseContext();
        ctx.Init("x\n"u8.ToArray());

        Assert.False(ctx.AdvanceWs());
        Assert.Equal(2, ctx.LineLength);
    }

    [Fact]
    public void AdvanceNl_SingleNewline_AdvancesToNextLine()
    {
        var ctx = new ParseContext();
        ctx.Init("\nsecond\n"u8.ToArray());

        Assert.True(ctx.AdvanceNl());
        Assert.Equal(2, ctx.LineNumber);
        Assert.True(ctx.Line.SequenceEqual("second\n"u8));
    }

    [Fact]
    public void AdvanceNl_NotSingleNewline_ReturnsFalse()
    {
        var ctx = new ParseContext();
        ctx.Init("x\n"u8.ToArray());

        Assert.False(ctx.AdvanceNl());
        Assert.Equal(1, ctx.LineNumber);
    }

    [Fact]
    public void AdvanceDigit_Decimal_Consumes()
    {
        var ctx = new ParseContext();
        ctx.Init("42 abc\n"u8.ToArray());

        Assert.True(ctx.AdvanceDigit(out long value, 10));
        Assert.Equal(42, value);
        Assert.True(ctx.Line.StartsWith(" abc"u8));
    }

    [Fact]
    public void AdvanceDigit_Octal_Consumes()
    {
        var ctx = new ParseContext();
        ctx.Init("0100644 path\n"u8.ToArray());

        Assert.True(ctx.AdvanceDigit(out long value, 8));
        Assert.Equal(33188, value); // 0o100644 = 33188 decimal
        Assert.True(ctx.Line.StartsWith(" path"u8));
    }

    [Fact]
    public void AdvanceDigit_NoDigit_ReturnsFalse()
    {
        var ctx = new ParseContext();
        ctx.Init("abc\n"u8.ToArray());

        Assert.False(ctx.AdvanceDigit(out _, 10));
    }

    [Fact]
    public void Peek_ReturnsFirstNonWhitespaceByte()
    {
        var ctx = new ParseContext();
        ctx.Init("  x\n"u8.ToArray());

        Assert.True(ctx.Peek(out byte b, skipWhitespace: true));
        Assert.Equal((byte)'x', b);
        Assert.Equal(4, ctx.LineLength); // peek does not advance
    }

    [Fact]
    public void Peek_NoSkipWhitespace_ReturnsFirstByte()
    {
        var ctx = new ParseContext();
        ctx.Init("  x\n"u8.ToArray());

        Assert.True(ctx.Peek(out byte b));
        Assert.Equal((byte)' ', b);
    }

    [Fact]
    public void Contains_ExactMatch_ReturnsTrue()
    {
        var ctx = new ParseContext();
        ctx.Init("diff --git a/foo b/foo\n"u8.ToArray());

        Assert.True(ctx.Contains("diff --git"u8));
        Assert.False(ctx.Contains("diff --baz"u8));
    }

    [Fact]
    public void Clear_ResetsToEmpty()
    {
        var ctx = new ParseContext();
        ctx.Init("some content\n"u8.ToArray());

        ctx.Clear();

        Assert.True(ctx.Content.IsEmpty);
        Assert.Equal(0, ctx.ContentLength);
        Assert.Equal(1, ctx.LineNumber);
        Assert.False(ctx.HasRemaining);
    }
}
