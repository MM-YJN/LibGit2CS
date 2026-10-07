using System.Buffers;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Core;

public class BinaryInfraTests
{
    // ━━ Base85 ━━

    [Fact]
    public void Base85_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Base85.Encode([]));
    }

    [Fact]
    public void Base85_FourBytes_ProducesFiveChars()
    {
        // "test" → base85. The first 4 bytes pack into a uint32 big-endian,
        // then emit 5 base85 digits (least-significant last).
        string result = Base85.Encode("test"u8.ToArray());
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public void Base85_KnownGitValue_MatchesExactly()
    {
        // git's binary patch uses base85. The byte sequence [0x00, 0x00, 0x00, 0x01]
        // packs to 0x00000001 → digits: 0,0,0,0,1 → "00001".
        // acc=1, last digit (i=4): 1%85=1 → '1'; acc=0; i=3: 0 → '0'; etc.
        Assert.Equal("00000", Base85.Encode([0, 0, 0, 0]));
        Assert.Equal("00001", Base85.Encode([0, 0, 0, 1]));
    }

    [Fact]
    public void Base85_PartialBlock_HandlesRemainingBytes()
    {
        // 1 byte → still 5 chars (partial block, 3 bytes of padding).
        Assert.Equal(5, Base85.Encode([42]).Length);
        // 5 bytes → 10 chars (one full + one partial block).
        Assert.Equal(10, Base85.Encode([1, 2, 3, 4, 5]).Length);
    }

    // ━━ Base85.TryDecode ━━

    [Fact]
    public void Base85_TryDecode_EmptyInput_ReturnsTrueWithNoOutput()
    {
        var writer = new ArrayBufferWriter<byte>();
        Assert.True(Base85.TryDecode([], 0, writer));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void Base85_TryDecode_KnownValues_DecodeCorrectly()
    {
        // Encode produces "00000" for [0,0,0,0] and "00001" for [0,0,0,1];
        // decoding must invert that exactly.
        var w1 = new ArrayBufferWriter<byte>();
        Assert.True(Base85.TryDecode("00000"u8, 4, w1));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, w1.WrittenSpan.ToArray());

        var w2 = new ArrayBufferWriter<byte>();
        Assert.True(Base85.TryDecode("00001"u8, 4, w2));
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, w2.WrittenSpan.ToArray());
    }

    [Fact]
    public void Base85_TryDecode_MaxUint32Value_DecodesCorrectly()
    {
        // 0xFFFFFFFF is the largest value representable in a 5-digit group;
        // encode([0xFF,0xFF,0xFF,0xFF]) yields "|NsC0".
        var writer = new ArrayBufferWriter<byte>();
        Assert.True(Base85.TryDecode("|NsC0"u8, 4, writer));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Base85_TryDecode_ZeroOutputFromValidInput_ReturnsTrue()
    {
        // A valid 5-char block with outputLen=0 decodes to nothing.
        var writer = new ArrayBufferWriter<byte>();
        Assert.True(Base85.TryDecode("00000"u8, 0, writer));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void Base85_TryDecode_RoundTrips_FullAndPartialBlocks()
    {
        // Full blocks (4n), partial trailing blocks (1-3 bytes), and multi-block mixes.
        byte[][] inputs =
        {
            [42],
            [1, 2],
            [0xFF, 0x01, 0xFE],
            [0x01, 0x02, 0x04, 0x08],
            [1, 2, 3, 4, 5, 6, 7, 8, 9],
        };

        foreach (byte[] data in inputs)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(Base85.Encode(data));
            var writer = new ArrayBufferWriter<byte>();
            Assert.True(Base85.TryDecode(encoded, data.Length, writer));
            Assert.Equal(data, writer.WrittenSpan.ToArray());
        }
    }

    [Fact]
    public void Base85_TryDecode_All256ByteValues_RoundTrip()
    {
        // 256 bytes = 64 full blocks → 320 chars; exercises every byte value.
        byte[] data = new byte[256];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        byte[] encoded = Encoding.ASCII.GetBytes(Base85.Encode(data));
        var writer = new ArrayBufferWriter<byte>();
        Assert.True(Base85.TryDecode(encoded, data.Length, writer));
        Assert.Equal(data, writer.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    public void Base85_TryDecode_LengthNotMultipleOfFive_ReturnsFalse(int len)
    {
        byte[] bytes = new byte[len];
        Array.Fill(bytes, (byte)'0');
        var writer = new ArrayBufferWriter<byte>();
        Assert.False(Base85.TryDecode(bytes, 0, writer));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void Base85_TryDecode_OutputLenExceedsInputCapacity_ReturnsFalse()
    {
        // 5 chars can decode at most 4 bytes; requesting 5 must fail.
        var writer = new ArrayBufferWriter<byte>();
        Assert.False(Base85.TryDecode("00000"u8, 5, writer));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void Base85_TryDecode_InvalidCharacter_ReturnsFalse()
    {
        // '/' is not in git's base85 alphabet; a 5-char block containing it is rejected.
        var writer = new ArrayBufferWriter<byte>();
        Assert.False(Base85.TryDecode("0/000"u8, 4, writer));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void Base85_TryDecode_OverflowAllMaxDigits_ReturnsFalse()
    {
        // "~~~~~" == 85^5 - 1, which exceeds uint32 max; the overflow guard rejects it.
        var writer = new ArrayBufferWriter<byte>();
        Assert.False(Base85.TryDecode("~~~~~"u8, 4, writer));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void Base85_TryDecode_OverflowJustBeyondMax_ReturnsFalse()
    {
        // "|NsC0" decodes to 0xFFFFFFFF (the max). Bumping the last digit to '1'
        // pushes acc*85+1 past uint32 max → overflow rejection.
        var writer = new ArrayBufferWriter<byte>();
        Assert.False(Base85.TryDecode("|NsC1"u8, 4, writer));
        Assert.Equal(0, writer.WrittenCount);
    }

    // ━━ Zlib round-trip ━━

    [Fact]
    public void Zlib_CompressThenDecompress_RoundTrips()
    {
        byte[] input = "The quick brown fox jumps over the lazy dog. The quick brown fox jumps over the lazy dog."u8.ToArray();

        byte[] compressed = ZlibTestHelpers.CompressLooseObject(input);
        using PooledByteBufferWriter decompressed = Zlib.DecompressPackObject(compressed);

        Assert.Equal(input, decompressed.WrittenSpan);
    }

    [Fact]
    public void Zlib_BinaryData_RoundTrips()
    {
        // Binary content with many repeated runs (good compression candidate).
        byte[] input = new byte[4096];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 17);
        }

        byte[] compressed = ZlibTestHelpers.CompressLooseObject(input);
        using PooledByteBufferWriter decompressed = Zlib.DecompressPackObject(compressed);

        Assert.Equal(input, decompressed.WrittenSpan);
        Assert.True(compressed.Length < input.Length, "should compress repeated data");
    }

    // ━━ DeltaEncoder ━━

    [Fact]
    public void Delta_EmptySource_ReturnsNonNull()
    {
        // An empty source can't build an index; create returns empty array.
        byte[]? delta = DeltaEncoder.Create(Array.Empty<byte>(), "hello"u8.ToArray(), 0);
        Assert.NotNull(delta);
    }

    [Fact]
    public void Delta_IdenticalBuffers_ProducesSmallDelta()
    {
        byte[] data = "line one\nline two\nline three\nline four\nline five\n"u8.ToArray();
        byte[]? delta = DeltaEncoder.Create(data, data, 0);

        Assert.NotNull(delta);
        // Delta header (base size + result size varints) + minimal ops should be
        // much smaller than the original for identical content.
        Assert.True(delta!.Length < data.Length, "identical buffers should compress well");
    }

    [Fact]
    public void Delta_CreateApply_RoundTrips_CompletelyDifferent()
    {
        byte[] source = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"u8.ToArray();
        byte[] target = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"u8.ToArray();

        byte[]? delta = DeltaEncoder.Create(source, target, 0);
        Assert.NotNull(delta);

        byte[] reconstructed = DeltaEncoder.Apply(source, delta!);
        Assert.Equal(target, reconstructed);
    }

    [Fact]
    public void Delta_CreateApply_RoundTrips_SmallModification()
    {
        byte[] source = "The quick brown fox jumps over the lazy dog. The lazy dog is jumped over by the quick brown fox repeatedly."u8.ToArray();
        byte[] target = "The quick red fox jumps over the lazy dog. The lazy dog is jumped over by the quick red fox repeatedly."u8.ToArray();

        byte[]? delta = DeltaEncoder.Create(source, target, 0);
        Assert.NotNull(delta);

        byte[] reconstructed = DeltaEncoder.Apply(source, delta!);
        Assert.Equal(target, reconstructed);
    }

    [Fact]
    public void Delta_CreateApply_RoundTrips_Insertion()
    {
        byte[] source = "header\ncontent\nfooter\n"u8.ToArray();
        byte[] target = "header\ninserted line\ncontent\nfooter\n"u8.ToArray();

        byte[]? delta = DeltaEncoder.Create(source, target, 0);
        Assert.NotNull(delta);

        byte[] reconstructed = DeltaEncoder.Apply(source, delta!);
        Assert.Equal(target, reconstructed);
    }

    [Fact]
    public void Delta_CreateApply_RoundTrips_Deletion()
    {
        byte[] source = "line1\nline2\nline3\nline4\nline5\n"u8.ToArray();
        byte[] target = "line1\nline2\nline5\n"u8.ToArray();

        byte[]? delta = DeltaEncoder.Create(source, target, 0);
        Assert.NotNull(delta);

        byte[] reconstructed = DeltaEncoder.Apply(source, delta!);
        Assert.Equal(target, reconstructed);
    }

    [Fact]
    public void Delta_CreateApply_RoundTrips_LargeRepetitiveBuffer()
    {
        // 16KB of repeating pattern — exercises the hash index and copy ops.
        byte[] source = new byte[16 * 1024];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)('A' + (i % 26));
        }

        // Target = source with a chunk in the middle replaced.
        byte[] target = (byte[])source.Clone();
        for (int i = 4000; i < 4050; i++)
        {
            target[i] = (byte)'Z';
        }

        byte[]? delta = DeltaEncoder.Create(source, target, 0);
        Assert.NotNull(delta);

        byte[] reconstructed = DeltaEncoder.Apply(source, delta!);
        Assert.Equal(target, reconstructed);
    }

    [Fact]
    public void Delta_MaxSizeExceeded_ReturnsNull()
    {
        byte[] source = "AAAAAAAAAA"u8.ToArray();
        byte[] target = "BBBBBBBBBB"u8.ToArray();

        // A tiny max size forces the delta to exceed it.
        byte[]? delta = DeltaEncoder.Create(source, target, maxDeltaSize: 1);
        Assert.Null(delta);
    }
}
