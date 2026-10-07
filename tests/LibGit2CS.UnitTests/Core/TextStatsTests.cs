using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public sealed class TextStatsTests
{
    [Fact]
    public void Gather_EmptyInput_ReturnsZero()
    {
        var stats = GitTextStats.Gather([]);
        Assert.Equal(0, stats.Nul);
        Assert.Equal(0, stats.Cr);
        Assert.Equal(0, stats.Lf);
        Assert.Equal(0, stats.Crlf);
        Assert.False(stats.IsBinary);
    }

    [Fact]
    public void Gather_PureLf_NoCrlf()
    {
        var stats = GitTextStats.Gather("line1\nline2\n"u8);
        Assert.Equal(0, stats.Cr);
        Assert.Equal(2, stats.Lf);
        Assert.Equal(0, stats.Crlf);
        Assert.False(stats.IsBinary);
    }

    [Fact]
    public void Gather_PureCrlf()
    {
        var stats = GitTextStats.Gather("line1\r\nline2\r\n"u8);
        Assert.Equal(2, stats.Cr);
        Assert.Equal(2, stats.Lf);
        Assert.Equal(2, stats.Crlf);
        Assert.False(stats.IsBinary);
    }

    [Fact]
    public void Gather_MixedCrlfAndLf()
    {
        var stats = GitTextStats.Gather("line1\r\nline2\n"u8);
        Assert.Equal(1, stats.Cr);
        Assert.Equal(2, stats.Lf);
        Assert.Equal(1, stats.Crlf);
        Assert.False(stats.IsBinary);
    }

    [Fact]
    public void Gather_BareCr_IsBinary()
    {
        // A bare CR (not part of CRLF) → binary per str.c:1382.
        var stats = GitTextStats.Gather("line\rmore"u8);
        Assert.Equal(1, stats.Cr);
        Assert.Equal(0, stats.Lf);
        Assert.Equal(0, stats.Crlf);
        Assert.True(stats.IsBinary);
    }

    [Fact]
    public void Gather_NulByte_IsBinary()
    {
        var stats = GitTextStats.Gather([0x41, 0x00, 0x42]);
        Assert.Equal(1, stats.Nul);
        Assert.True(stats.IsBinary);
    }

    [Fact]
    public void Gather_Utf8Bom_NotBinary()
    {
        var stats = GitTextStats.Gather([0xEF, 0xBB, 0xBF, (byte)'A', (byte)'B']);
        Assert.Equal(3, stats.BomLength);
        Assert.False(stats.IsBinary);
    }

    [Fact]
    public void Gather_Utf16Bom_IsBinary()
    {
        var stats = GitTextStats.Gather([0xFF, 0xFE, 0x41, 0x00]);
        Assert.True(stats.IsBinary);
    }

    [Fact]
    public void Gather_Utf32LeBom_DetectsFourBytes()
    {
        // FF FE 00 00 is a UTF-32 LE BOM (4 bytes), not a UTF-16 LE BOM (2).
        // C's git_str_detect_bom checks the 4-byte form before the 2-byte form.
        var stats = GitTextStats.Gather([0xFF, 0xFE, 0x00, 0x00, (byte)'A', (byte)'\n']);
        Assert.Equal(4, stats.BomLength);
    }

    [Fact]
    public void Gather_Utf32LeBom_SkipBom_SkipsFourBytes()
    {
        // With skipBom=true, C skips 4 bytes for a UTF-32 LE BOM.
        var stats = GitTextStats.Gather([0xFF, 0xFE, 0x00, 0x00, (byte)'A', (byte)'\n'], skipBom: true);
        Assert.Equal(4, stats.BomLength);
        Assert.Equal(1, stats.Lf);
        Assert.Equal(0, stats.Nul);
    }

    [Fact]
    public void Gather_Utf16LeBom_StillDetectsTwoBytes()
    {
        // FF FE followed by non-zero bytes is a UTF-16 LE BOM (2 bytes).
        var stats = GitTextStats.Gather([0xFF, 0xFE, 0x41, 0x00]);
        Assert.Equal(2, stats.BomLength);
    }

    [Fact]
    public void Gather_SkipBom_SkipsBomBytes()
    {
        byte[] data = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A', (byte)'\n' };
        var stats = GitTextStats.Gather(data, skipBom: true);
        Assert.Equal(0, stats.Nul);
        Assert.Equal(1, stats.Lf);
        Assert.Equal(3, stats.BomLength);
    }

    [Fact]
    public void CrlfToLf_ConvertsCrlfToLf()
    {
        byte[] result = GitTextStats.CrlfToLf("a\r\nb\r\n"u8);
        Assert.Equal("a\nb\n"u8.ToArray(), result);
    }

    [Fact]
    public void CrlfToLf_PreservesBareCr()
    {
        byte[] result = GitTextStats.CrlfToLf("a\rb\r\n"u8);
        Assert.Equal("a\rb\n"u8.ToArray(), result);
    }

    [Fact]
    public void CrlfToLf_NoCr_ReturnsInput()
    {
        ReadOnlySpan<byte> input = "abc\n"u8;
        byte[] result = GitTextStats.CrlfToLf(input);
        Assert.Equal(input.ToArray(), result);
    }

    [Fact]
    public void LfToCrlf_ConvertsLfToCrlf()
    {
        byte[] result = GitTextStats.LfToCrlf("a\nb\n"u8);
        Assert.Equal("a\r\nb\r\n"u8.ToArray(), result);
    }

    [Fact]
    public void LfToCrlf_DoesNotDoubleExistingCrlf()
    {
        byte[] result = GitTextStats.LfToCrlf("a\r\nb\n"u8);
        Assert.Equal("a\r\nb\r\n"u8.ToArray(), result);
    }

    [Fact]
    public void LfToCrlf_NoLf_ReturnsInput()
    {
        ReadOnlySpan<byte> input = "abc"u8;
        byte[] result = GitTextStats.LfToCrlf(input);
        Assert.Equal(input.ToArray(), result);
    }
}
