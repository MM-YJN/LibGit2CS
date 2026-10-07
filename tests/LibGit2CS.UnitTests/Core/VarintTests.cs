using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

public sealed class VarintTests
{
    [Fact]
    public void EncodeDecode_Zero_RoundTrips()
    {
        byte[] buf = new byte[16];
        int len = GitVarint.Encode(buf, 0);
        Assert.Equal(1, len);
        Assert.Equal(0x00, buf[0]);

        ulong decoded = GitVarint.Decode(buf.AsSpan(0, len), out int decodedLen);
        Assert.Equal(0UL, decoded);
        Assert.Equal(1, decodedLen);
    }

    [Fact]
    public void EncodeDecode_127_RoundTrips()
    {
        byte[] buf = new byte[16];
        int len = GitVarint.Encode(buf, 127);
        Assert.Equal(1, len);
        Assert.Equal(0x7F, buf[0]);

        ulong decoded = GitVarint.Decode(buf.AsSpan(0, len), out int decodedLen);
        Assert.Equal(127UL, decoded);
        Assert.Equal(1, decodedLen);
    }

    [Fact]
    public void EncodeDecode_128_RoundTrips()
    {
        // 128 = 0x80 0x01 in git varint
        byte[] buf = new byte[16];
        int len = GitVarint.Encode(buf, 128);
        Assert.Equal(2, len);

        ulong decoded = GitVarint.Decode(buf.AsSpan(0, len), out int decodedLen);
        Assert.Equal(128UL, decoded);
        Assert.Equal(2, decodedLen);
    }

    [Fact]
    public void EncodeDecode_LargeValue_RoundTrips()
    {
        ulong value = 0xDEADBEEFCAFEUL;
        byte[] buf = new byte[16];
        int len = GitVarint.Encode(buf, value);
        Assert.True(len > 0);

        ulong decoded = GitVarint.Decode(buf.AsSpan(0, len), out int decodedLen);
        Assert.Equal(value, decoded);
        Assert.Equal(len, decodedLen);
    }

    [Fact]
    public void Encode_MaxUlong_RoundTrips()
    {
        ulong value = ulong.MaxValue;
        byte[] buf = new byte[16];
        int len = GitVarint.Encode(buf, value);
        Assert.True(len > 0);

        ulong decoded = GitVarint.Decode(buf.AsSpan(0, len), out int decodedLen);
        Assert.Equal(value, decoded);
        Assert.Equal(len, decodedLen);
    }

    [Fact]
    public void Encode_BufferTooSmall_Throws()
    {
        byte[] buf = new byte[1];
        Assert.Throws<ArgumentException>(() => GitVarint.Encode(buf, 1UL << 35));
    }

    [Fact]
    public void Decode_EmptyBuffer_ReturnsZero()
    {
        ulong decoded = GitVarint.Decode([], out int len);
        Assert.Equal(0UL, decoded);
        Assert.Equal(0, len);
    }

    [Theory]
    [InlineData(0UL, new byte[] { 0x00 })]
    [InlineData(1UL, new byte[] { 0x01 })]
    [InlineData(127UL, new byte[] { 0x7F })]
    [InlineData(128UL, new byte[] { 0x80, 0x00 })]
    [InlineData(129UL, new byte[] { 0x80, 0x01 })]
    [InlineData(255UL, new byte[] { 0x80, 0x7F })]
    [InlineData(16383UL, new byte[] { 0xFE, 0x7F })]
    [InlineData(16384UL, new byte[] { 0xFF, 0x00 })]
    public void Encode_KnownValues_ProduceExpectedBytes(ulong value, byte[] expected)
    {
        byte[] buf = new byte[16];
        int len = GitVarint.Encode(buf, value);
        Assert.Equal(expected.Length, len);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], buf[i]);
        }
    }
}
