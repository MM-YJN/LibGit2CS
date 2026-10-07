using System.IO.Hashing;

using LibGit2CS.Core.Compression;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Core.Compression;

public class ZlibTests
{
    [Fact]
    public void DecompressPackObject_RoundTrips_CompressLooseObject()
    {
        byte[] payload = "commit 5\x0test commit payload data"u8.ToArray();

        using var compressed = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(compressed, payload);
        using PooledByteBufferWriter decompressed = Zlib.DecompressPackObject(compressed.WrittenSpan);

        Assert.Equal(payload, decompressed.WrittenSpan);
    }

    [Fact]
    public void DecompressPackObject_RoundTrips()
    {
        // Pack object bodies use zlib-framed compression (same as loose objects).
        byte[] payload = "some object body content"u8.ToArray();

        using var compressed = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(compressed, payload);
        using PooledByteBufferWriter decompressed = Zlib.DecompressPackObject(compressed.WrittenSpan);

        Assert.Equal(payload, decompressed.WrittenSpan);
    }

    [Fact]
    public void DecompressPackObject_EmptyInput_ProducesEmptyOutput()
    {
        using var compressed = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(compressed, []);
        using PooledByteBufferWriter decompressed = Zlib.DecompressPackObject(compressed.WrittenSpan);

        Assert.Equal(0, decompressed.WrittenCount);
    }

    [Fact]
    public void DecompressPackObject_WithBytesConsumed_ReportsExactStreamLength()
    {
        byte[] payload = "some object body content"u8.ToArray();

        using var compressed = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(compressed, payload);

        using var decompressed = new PooledByteBufferWriter();
        Zlib.DecompressPackObject(decompressed, compressed.WrittenSpan, out int bytesConsumed);

        Assert.Equal(payload, decompressed.WrittenSpan);
        Assert.Equal(compressed.WrittenCount, bytesConsumed);
    }

    [Fact]
    public void DecompressPackObject_WithBytesConsumed_StopsAtStreamBoundary_IgnoresTrailingBytes()
    {
        // Simulates the pack-indexer read buffer: one zlib stream followed by
        // bytes belonging to the next object. The decoder must stop at the end
        // of the first stream (after consuming its Adler-32 trailer) and report
        // exactly that length.
        byte[] payload = "first object body"u8.ToArray();

        using var streamWriter = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(streamWriter, payload);
        byte[] stream = streamWriter.WrittenSpan.ToArray();
        byte[] trailing = [0xFF, 0xFE, 0xFD, 0xFC, 0xFB];
        byte[] buffer = [.. stream, .. trailing];

        using var decompressed = new PooledByteBufferWriter();
        Zlib.DecompressPackObject(decompressed, buffer, out int bytesConsumed);

        Assert.Equal(payload, decompressed.WrittenSpan);
        Assert.Equal(stream.Length, bytesConsumed);
        Assert.Equal(trailing.Length, buffer.Length - bytesConsumed);
    }

    [Fact]
    public void DecompressPackObject_WithBytesConsumed_DecodesConcatenatedStreamsIndependently()
    {
        byte[] payloadA = "first object body"u8.ToArray();
        byte[] payloadB = "second object body, longer than the first"u8.ToArray();

        using var streamAWriter = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(streamAWriter, payloadA);
        byte[] streamA = streamAWriter.WrittenSpan.ToArray();
        using var streamBWriter = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(streamBWriter, payloadB);
        byte[] streamB = streamBWriter.WrittenSpan.ToArray();
        byte[] buffer = [.. streamA, .. streamB];

        using var decompressedA = new PooledByteBufferWriter();
        Zlib.DecompressPackObject(decompressedA, buffer, out int consumedA);
        using var decompressedB = new PooledByteBufferWriter();
        Zlib.DecompressPackObject(decompressedB, buffer.AsSpan(consumedA), out int consumedB);

        Assert.Equal(payloadA, decompressedA.WrittenSpan);
        Assert.Equal(payloadB, decompressedB.WrittenSpan);
        Assert.Equal(streamA.Length, consumedA);
        Assert.Equal(streamB.Length, consumedB);
        Assert.Equal(buffer.Length, consumedA + consumedB);
    }

    [Fact]
    public void Crc32_OfKnownInput_ReturnsExpectedValue()
    {
        // CRC-32 of "123456789" is 0xCBF43926 (well-known test vector).
        uint crc = Crc32.HashToUInt32("123456789"u8);

        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void Crc32_OfEmptyInput_ReturnsZero()
    {
        Assert.Equal(0u, Crc32.HashToUInt32([]));
    }

    [Fact]
    public void Crc32_IsConsistentWithRepeatedInput()
    {
        byte[] data = "hello"u8.ToArray();

        Assert.Equal(Crc32.HashToUInt32(data), Crc32.HashToUInt32(data));
    }

    [Fact]
    public void DecompressPackObject_EmptyInput_Throws()
    {
        // C's inflate of zero input yields Z_BUF_ERROR and packfile_unpack_compressed errors "error inflating zlib stream" (pack.c:905-915).
        Assert.Throws<InvalidDataException>(() => Zlib.DecompressPackObject(Array.Empty<byte>()));
    }

    [Fact]
    public void DecompressLooseObjectHeader_WritesIntoDestination_ReturnsCount()
    {
        byte[] payload = "commit 5\x0test commit payload data"u8.ToArray();

        using var compressedWriter = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(compressedWriter, payload);
        byte[] compressed = compressedWriter.WrittenSpan.ToArray();

        Span<byte> destination = stackalloc byte[Zlib.MaxHeaderLen];
        int written = Zlib.DecompressLooseObjectHeader(destination, compressed);

        Assert.True(written > 0, "header decompression wrote nothing");
        Assert.True(written <= Zlib.MaxHeaderLen);
        Assert.Equal(payload.AsSpan(0, written), destination[..written]);
    }

    [Fact]
    public void DecompressLooseObjectHeader_TooSmallDestination_Throws()
    {
        // The destination contract is at least MAX_HEADER_LEN (64) bytes —
        // enforced in release builds too (not just a Debug.Assert).
        using var compressedWriter = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(compressedWriter, "some payload"u8.ToArray());
        byte[] compressed = compressedWriter.WrittenSpan.ToArray();

        byte[] destination = new byte[Zlib.MaxHeaderLen - 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => Zlib.DecompressLooseObjectHeader(destination, compressed));
    }

    [Fact]
    public void DecompressPackObject_AppendsToExistingWriterContent()
    {
        // The writer-taking overload APPENDS to any existing content. Callers
        // that reuse a writer across attempts (the indexer's grow-and-retry
        // window loop) must reset it between attempts — this pins the
        // documented semantics the reset relies on.
        byte[] payload = "some object body content"u8.ToArray();

        using var compressedWriter = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(compressedWriter, payload);
        byte[] compressed = compressedWriter.WrittenSpan.ToArray();
        byte[] prefix = [0xAA, 0xBB, 0xCC];

        using var decompressed = new PooledByteBufferWriter();
        decompressed.Write(prefix);
        Zlib.DecompressPackObject(decompressed, compressed, out _);

        Assert.Equal([.. prefix, .. payload], decompressed.WrittenSpan);
    }
}
