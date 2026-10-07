using LibGit2CS.Core;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

public class DeltaApplierTests
{
    [Fact]
    public void Apply_PureInsert_ProducesLiteralOutput()
    {
        // Delta: base_size=0, result_size=5, INSERT 5 bytes "hello"
        byte[] baseData = Array.Empty<byte>();
        byte[] delta = BuildDelta(0, 5, insertData: "hello"u8.ToArray());

        byte[] result = GitDeltaApplier.Apply(baseData, delta);

        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Apply_PureCopy_CopiesFromBase()
    {
        // Base: "hello world" (11 bytes)
        // Delta: base_size=11, result_size=5, COPY offset=0 len=5
        byte[] baseData = "hello world"u8.ToArray();
        byte[] delta = BuildDelta(11, 5, copyOps: [(Offset: 0, Length: 5)]);

        byte[] result = GitDeltaApplier.Apply(baseData, delta);

        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Apply_CopyFromMiddle_CopiesCorrectSubstring()
    {
        byte[] baseData = "hello world"u8.ToArray();
        byte[] delta = BuildDelta(11, 5, copyOps: [(Offset: 6, Length: 5)]);

        byte[] result = GitDeltaApplier.Apply(baseData, delta);

        Assert.Equal("world", System.Text.Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Apply_MixedCopyAndInsert()
    {
        // Base: "XXXXworld"
        // Delta: copy 4 bytes (XXXX), insert "hello ", copy 5 bytes (world)
        byte[] baseData = "XXXXworld"u8.ToArray();
        byte[] delta = BuildDelta(9, 15,
            copyOps: [(Offset: 0, Length: 4), (Offset: 4, Length: 5)],
            insertData: "hello "u8.ToArray(),
            insertAfterFirstCopy: true);

        byte[] result = GitDeltaApplier.Apply(baseData, delta);

        Assert.Equal("XXXXhello world", System.Text.Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Apply_CopyDefaultLength_Uses64KB()
    {
        // When no size bits are set in a COPY cmd, the length defaults to 0x10000 (65536).
        byte[] baseData = new byte[65536];
        for (int i = 0; i < baseData.Length; i++)
        {
            baseData[i] = (byte)(i & 0xff);
        }

        // Build a delta with a single COPY cmd=0x80 (no offset/size bytes → offset=0, len=65536)
        byte[] delta = new byte[3];
        delta[0] = 0; // base_size varint = 0 → but base is 65536! Need to encode 65536
        // Actually, we need to properly encode base_size and result_size as varints.
        // 65536 = 0x10000 → varint: 0x00 (0x00 | 0x80), 0x04 (0x200 >> 7 = 4, wait...)
        // Let me compute: 65536 in 7-bit varint:
        // 65536 = 0b1_0000_0000_0000_0000
        // 7-bit chunks (LSB first): 0000000 (0), 0000001 (1), 0000100 (4)
        // → bytes: 0x80, 0x81, 0x04
        delta = new byte[6];
        // base_size = 65536
        delta[0] = (0 & 0x7f) | 0x80; // 0x80
        delta[1] = (1 & 0x7f) | 0x80; // wait, 65536 >> 7 = 512, >> 14 = 4
        // Actually: 65536 in binary = 1 0000 0000 0000 0000
        // 7-bit groups from LSB: bits 0-6 = 0000000 (0), bits 7-13 = 0000000 (0), bits 14-20 = 0000100 (4)
        // Wait, 65536 = 2^16. Let me recompute:
        // 65536 = 0x10000
        // >> 0  = 0x10000, & 0x7f = 0x00, with continuation → 0x80
        // >> 7  = 0x0200, & 0x7f = 0x00, with continuation → 0x80
        // >> 14 = 0x0004, & 0x7f = 0x04, no continuation → 0x04
        delta[0] = 0x80;
        delta[1] = 0x80;
        delta[2] = 0x04;
        // result_size = 65536 (same encoding)
        delta[3] = 0x80;
        delta[4] = 0x80;
        delta[5] = 0x04;
        // Then the COPY instruction: cmd=0x80 (copy, offset=0, len=default=65536)
        Array.Resize(ref delta, delta.Length + 1);
        delta[6] = 0x80; // cmd=0x80: COPY with offset=0, len=0 → 65536

        byte[] result = GitDeltaApplier.Apply(baseData, delta);

        Assert.Equal(65536, result.Length);
        Assert.Equal(baseData, result);
    }

    [Fact]
    public void Apply_BaseSizeMismatch_Throws()
    {
        byte[] delta = BuildDelta(10, 5, copyOps: [(Offset: 0, Length: 5)]);

        Assert.Throws<GitException>(() => GitDeltaApplier.Apply(new byte[5], delta));
    }

    [Fact]
    public void Apply_TruncatedDelta_Throws()
    {
        // Delta header says insert 5 bytes but only 2 follow
        byte[] delta = new byte[3];
        delta[0] = 0; // base_size = 0
        delta[1] = 5; // result_size = 5
        delta[2] = 5; // INSERT 5 bytes, but no data follows

        Assert.Throws<GitException>(() => GitDeltaApplier.Apply([], delta));
    }

    [Fact]
    public void Apply_CopyOutOfRange_Throws()
    {
        // Base is 5 bytes, but COPY tries to read from offset 3, length 5 (overflows)
        byte[] baseData = "hello"u8.ToArray();
        byte[] delta = BuildDelta(5, 5, copyOps: [(Offset: 3, Length: 5)]);

        Assert.Throws<GitException>(() => GitDeltaApplier.Apply(baseData, delta));
    }

    [Fact]
    public void Apply_ZeroCommand_Throws()
    {
        byte[] delta = new byte[3];
        delta[0] = 0; // base_size = 0
        delta[1] = 0; // result_size = 0
        delta[2] = 0; // cmd = 0 (reserved)

        Assert.Throws<GitException>(() => GitDeltaApplier.Apply([], delta));
    }

    [Fact]
    public void Apply_EmptyDelta_ThrowsTruncated()
    {
        Assert.Throws<GitException>(() => GitDeltaApplier.Apply([], []));
    }

    [Fact]
    public void Apply_IncompleteDelta_DoesNotFillResult_Throws()
    {
        // result_size = 10 but only INSERT 3 bytes
        byte[] delta = new byte[4];
        delta[0] = 0;  // base_size = 0
        delta[1] = 10; // result_size = 10
        delta[2] = 3;  // INSERT 3 bytes
        delta[3] = 65; // 'A'

        Assert.Throws<GitException>(() => GitDeltaApplier.Apply([], delta));
    }

    [Fact]
    public void ReadHeader_ReturnsBaseAndResultSize()
    {
        byte[] delta = BuildDelta(11, 5, copyOps: [(Offset: 0, Length: 5)]);

        (long baseSize, long resultSize) = GitDeltaApplier.ReadHeader(delta);

        Assert.Equal(11L, baseSize);
        Assert.Equal(5L, resultSize);
    }

    [Fact]
    public void Apply_LargeOffset_UsesAllFourOffsetBytes()
    {
        // Test COPY with offset encoded across all 4 offset bytes
        byte[] baseData = new byte[70000];
        for (int i = 0; i < baseData.Length; i++)
        {
            baseData[i] = (byte)(i & 0xff);
        }

        int offset = 65536; // requires offset bytes 0 and 1
        int copyLen = 100;
        byte[] delta = BuildDelta(baseData.Length, copyLen, copyOps: [(Offset: offset, Length: copyLen)]);

        byte[] result = GitDeltaApplier.Apply(baseData, delta);

        Assert.Equal(baseData.AsSpan(offset, copyLen).ToArray(), result);
    }

    /// <summary>
    /// Builds a delta stream for testing. Supports a sequence of COPY ops
    /// optionally interleaved with one INSERT of literal data.
    /// </summary>
    private static byte[] BuildDelta(
        int baseSize,
        int resultSize,
        IReadOnlyList<(int Offset, int Length)>? copyOps = null,
        byte[]? insertData = null,
        bool insertAfterFirstCopy = false)
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, baseSize);
        WriteVarint(ms, resultSize);

        bool insertWritten = false;

        if (copyOps is not null)
        {
            for (int i = 0; i < copyOps.Count; i++)
            {
                if (insertAfterFirstCopy && !insertWritten && i == 1 && insertData is not null)
                {
                    WriteInsert(ms, insertData);
                    insertWritten = true;
                }

                WriteCopy(ms, copyOps[i].Offset, copyOps[i].Length);
            }
        }

        if (!insertWritten && insertData is not null)
        {
            WriteInsert(ms, insertData);
        }

        return ms.ToArray();
    }

    private static void WriteVarint(Stream ms, long value)
    {
        while (true)
        {
            byte b = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0)
            {
                ms.WriteByte((byte)(b | 0x80));
            }
            else
            {
                ms.WriteByte(b);
                break;
            }
        }
    }

    private static void WriteCopy(Stream ms, int offset, int length)
    {
        byte cmd = 0x80; // COPY bit
        var bytes = new List<byte>();

        // Offset bytes (4 possible)
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)((offset >> (i * 8)) & 0xff);
            if (b != 0)
            {
                cmd |= (byte)(1 << i);
                bytes.Add(b);
            }
        }

        // Length bytes (3 possible)
        if (length != 0x10000)
        {
            for (int i = 0; i < 3; i++)
            {
                byte b = (byte)((length >> (i * 8)) & 0xff);
                if (b != 0)
                {
                    cmd |= (byte)(0x10 << i);
                    bytes.Add(b);
                }
            }
        }

        ms.WriteByte(cmd);
        foreach (byte b in bytes)
        {
            ms.WriteByte(b);
        }
    }

    private static void WriteInsert(Stream ms, byte[] data)
    {
        // INSERT cmd = data.Length (1..127)
        // For longer inserts, split into multiple INSERT commands
        int pos = 0;
        while (pos < data.Length)
        {
            int chunk = Math.Min(127, data.Length - pos);
            ms.WriteByte((byte)chunk);
            ms.Write(data, pos, chunk);
            pos += chunk;
        }
    }
}
