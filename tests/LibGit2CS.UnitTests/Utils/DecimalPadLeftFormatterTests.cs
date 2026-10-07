using System.Globalization;
using System.Text;

using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Utils;

public sealed class DecimalPadLeftFormatterTests
{
    [Theory]
    [InlineData(0, 1, '0', "0")]
    [InlineData(83, 1, '0', "83")]
    [InlineData(83, 3, '0', "083")]
    [InlineData(83, 5, '_', "___83")]
    [InlineData(-1, 20, '0', "18446744073709551615")]
    [InlineData(int.MinValue, 1, '0', "18446744071562067968")]
    [InlineData(int.MaxValue, 11, '0', "02147483647")]
    [InlineData(long.MaxValue, 1, '0', "9223372036854775807")]
    [InlineData(83, 0, '0', "83")]
    public void Formatting_WritesExpectedDecimal(long value, int totalWidth, char paddingChar, string expected)
    {
        var formatter = new DecimalPadLeftFormatter(value, totalWidth, paddingChar);
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
    [InlineData(83, 1, '0', "83")]
    [InlineData(83, 3, '0', "083")]
    [InlineData(83, 5, '中', "中中中83")]
    [InlineData(-1, 20, '0', "18446744073709551615")]
    public void Formatting_WritesExpectedUtf8(long value, int totalWidth, char paddingChar, string expected)
    {
        var formatter = new DecimalPadLeftFormatter(value, totalWidth, paddingChar);
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
    [InlineData(-1, 1, 19)]
    public void TryFormat_ReturnsFalseWhenDestinationIsTooSmall(int value, int totalWidth, int destinationLength)
    {
        var formatter = new DecimalPadLeftFormatter(value, totalWidth);
        char[] destination = new string('x', destinationLength).ToCharArray();

        bool success = formatter.TryFormat(destination, out int charsWritten, ReadOnlySpan<char>.Empty, null);

        Assert.False(success);
        Assert.Equal(0, charsWritten);
        Assert.Equal(new string('x', destinationLength), new string(destination));
    }

    [Theory]
    [InlineData(83, 3, '0', 2)]
    [InlineData(83, 5, '中', 10)]
    public void Utf8TryFormat_ReturnsFalseWhenDestinationIsTooSmall(long value, int totalWidth, char paddingChar, int destinationLength)
    {
        var formatter = new DecimalPadLeftFormatter(value, totalWidth, paddingChar);
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
        var formatter = new DecimalPadLeftFormatter(83, -1);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => formatter.ToString("ignored", CultureInfo.InvariantCulture));

        Assert.Equal("totalWidth", exception.ParamName);
    }
}
