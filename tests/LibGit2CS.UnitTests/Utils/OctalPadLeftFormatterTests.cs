using System.Globalization;
using System.Text;

using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Utils;

public sealed class OctalPadLeftFormatterTests
{
    [Theory]
    [InlineData(0, 1, '0', "0")]
    [InlineData(83, 2, '0', "123")]
    [InlineData(83, 3, '0', "123")]
    [InlineData(83, 5, '_', "__123")]
    [InlineData(-1, 12, '0', "1777777777777777777777")]
    [InlineData(int.MinValue, 1, '0', "1777777777760000000000")]
    [InlineData(int.MaxValue, 11, '0', "17777777777")]
    public void Formatting_WritesExpectedOctal(int value, int totalWidth, char paddingChar, string expected)
    {
        var formatter = new OctalPadLeftFormatter(value, totalWidth, paddingChar);
        Span<char> destination = stackalloc char[32];

        bool success = formatter.TryFormat(destination, out int charsWritten, ReadOnlySpan<char>.Empty, null);

        Assert.True(success);
        Assert.Equal(expected.Length, charsWritten);
        Assert.Equal(expected, destination[..charsWritten].ToString());
        Assert.Equal(expected, formatter.ToString());
        Assert.Equal(expected, formatter.ToString("ignored", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(0, 1, '0', "0")]
    [InlineData(83, 2, '0', "123")]
    [InlineData(83, 5, '中', "中中123")]
    [InlineData(-1, 22, '0', "1777777777777777777777")]
    public void Formatting_WritesExpectedUtf8(long value, int totalWidth, char paddingChar, string expected)
    {
        var formatter = new OctalPadLeftFormatter(value, totalWidth, paddingChar);
        byte[] destination = new byte[Encoding.UTF8.GetByteCount(expected)];
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);

        bool success = formatter.TryFormat(destination, out int bytesWritten, ReadOnlySpan<char>.Empty, null);

        Assert.True(success);
        Assert.Equal(expectedBytes.Length, bytesWritten);
        Assert.Equal(expectedBytes, destination[..bytesWritten]);
        Assert.Equal(expected, Encoding.UTF8.GetString(destination[..bytesWritten]));
        Assert.Equal(expected, formatter.ToString());
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(83, 3, 2)]
    [InlineData(-1, 1, 10)]
    public void TryFormat_ReturnsFalseWhenDestinationIsTooSmall(int value, int totalWidth, int destinationLength)
    {
        var formatter = new OctalPadLeftFormatter(value, totalWidth);
        char[] destination = new string('x', destinationLength).ToCharArray();

        bool success = formatter.TryFormat(destination, out int charsWritten, ReadOnlySpan<char>.Empty, null);

        Assert.False(success);
        Assert.Equal(0, charsWritten);
        Assert.Equal(new string('x', destinationLength), new string(destination));
    }

    [Theory]
    [InlineData(83, 3, '0', 2)]
    [InlineData(83, 5, '中', 8)]
    public void Utf8TryFormat_ReturnsFalseWhenDestinationIsTooSmall(long value, int totalWidth, char paddingChar, int destinationLength)
    {
        var formatter = new OctalPadLeftFormatter(value, totalWidth, paddingChar);
        byte[] destination = new byte[destinationLength];
        Array.Fill(destination, (byte)'x');
        byte[] original = (byte[])destination.Clone();

        bool success = formatter.TryFormat(destination, out int bytesWritten, ReadOnlySpan<char>.Empty, null);

        Assert.False(success);
        Assert.Equal(0, bytesWritten);
        Assert.Equal(original, destination);
    }

    [Fact]
    public void ToString_WithNegativeWidth_Throws()
    {
        var formatter = new OctalPadLeftFormatter(83, -1);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => formatter.ToString("ignored", CultureInfo.InvariantCulture));

        Assert.Equal("totalWidth", exception.ParamName);
    }
}
