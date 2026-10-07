using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Utils;

public class PooledByteBufferWriterTests
{
    [Fact]
    public void Consume_PartialWrite_ExposesOnlyUnconsumedBytes()
    {
        using var writer = new PooledByteBufferWriter();

        Write(writer, [1, 2, 3, 4]);
        writer.Consume(2);

        Assert.Equal(2, writer.WrittenCount);
        Assert.Equal([3, 4], writer.WrittenSpan.ToArray());
        Assert.Equal(writer.Capacity - writer.WrittenCount, writer.FreeCapacity);
    }

    [Fact]
    public void GetSpan_WhenCompactionSatisfiesRequest_DoesNotGrow()
    {
        using var writer = new PooledByteBufferWriter();
        writer.GetSpan(1); // trigger lazy initial rental
        int initialCapacity = writer.Capacity;
        byte[] payload = CreatePayload(initialCapacity);
        int consumed = initialCapacity / 2;

        Write(writer, payload);
        writer.Consume(consumed);

        Span<byte> free = writer.GetSpan(consumed);

        Assert.Equal(initialCapacity, writer.Capacity);
        Assert.True(free.Length >= consumed);
        Assert.Equal(payload.AsSpan(consumed).ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void GetSpan_WhenCompactionCannotSatisfyRequest_GrowsAndPreservesUnconsumedBytes()
    {
        using var writer = new PooledByteBufferWriter();
        writer.GetSpan(1); // trigger lazy initial rental
        int initialCapacity = writer.Capacity;
        byte[] payload = CreatePayload(initialCapacity);

        Write(writer, payload);
        writer.Consume(1);

        Span<byte> free = writer.GetSpan(initialCapacity);

        Assert.True(writer.Capacity > initialCapacity);
        Assert.True(free.Length >= initialCapacity);
        Assert.Equal(payload.AsSpan(1).ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Consume_AllWrittenBytes_ReclaimsEntireBufferImmediately()
    {
        using var writer = new PooledByteBufferWriter();
        writer.GetSpan(1); // trigger lazy initial rental
        int initialCapacity = writer.Capacity;
        byte[] payload = CreatePayload(initialCapacity / 2);

        Write(writer, payload);
        writer.Consume(payload.Length);

        Assert.Equal(0, writer.WrittenCount);
        Assert.Equal(initialCapacity, writer.Capacity);
        Assert.Equal(initialCapacity, writer.FreeCapacity);
        Assert.True(writer.GetSpan(initialCapacity).Length >= initialCapacity);
    }

    [Fact]
    public void Clear_ZeroesWrittenDataAndResetWrittenCount_DoesNot()
    {
        using var writer = new PooledByteBufferWriter();

        Write(writer, [1, 2, 3]);
        writer.Clear();

        Assert.Equal([0, 0, 0], writer.GetSpan(3).Slice(0, 3).ToArray());

        Write(writer, [4, 5, 6]);
        writer.ResetWrittenCount();

        Assert.Equal([4, 5, 6], writer.GetSpan(3).Slice(0, 3).ToArray());
    }

    [Fact]
    public void LazyRent_ParameterlessCtor_DeferRentalUntilFirstUse()
    {
        using var writer = new PooledByteBufferWriter();

        // Before any write/GetSpan, the writer is backed by Array.Empty<byte>()
        Assert.Equal(0, writer.Capacity);
        Assert.Equal(0, writer.FreeCapacity);
        Assert.Equal(0, writer.WrittenCount);

        // First GetSpan triggers the lazy rental
        writer.GetSpan(10);

        Assert.True(writer.Capacity >= 256);
        Assert.True(writer.FreeCapacity >= 10);
    }

    [Fact]
    public void LazyRent_FirstGrowRespectsDefaultInitialBufferSize()
    {
        using var writer = new PooledByteBufferWriter();

        // Smallest non-zero hint still rents at least the 256-byte floor
        writer.GetSpan(1);

        Assert.True(writer.Capacity >= 256);
    }

    [Fact]
    public void LazyRent_LargeSizeHintWinsOverDefaultFloor()
    {
        using var writer = new PooledByteBufferWriter();

        writer.GetSpan(1000);

        Assert.True(writer.FreeCapacity >= 1000);
        Assert.True(writer.Capacity >= 1000);
    }

    [Fact]
    public void Ctor_ZeroCapacity_BehavesAsLazyWriter()
    {
        using var writer = new PooledByteBufferWriter(0);

        // Zero capacity == parameterless ctor: backed by Array.Empty, no
        // rental until first use.
        Assert.Equal(0, writer.Capacity);
        Assert.Equal(0, writer.FreeCapacity);
        Assert.Equal(0, writer.WrittenCount);

        writer.GetSpan(10);

        Assert.True(writer.Capacity >= 256);
        Assert.True(writer.FreeCapacity >= 10);
    }

    [Fact]
    public void Ctor_NegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PooledByteBufferWriter(-1));
    }

    [Fact]
    public void Dispose_BeforeAnyWrite_DoesNotThrow()
    {
        using var writer = new PooledByteBufferWriter();

        // Constructed but never written — Dispose must not throw and must not
        // attempt to return the empty singleton to the pool.
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetMemory());
    }

    [Fact]
    public void InvalidArgumentsAndDisposedWriter_Throw()
    {
        using var writer = new PooledByteBufferWriter();

        Assert.Throws<ArgumentException>(() => writer.Advance(-1));
        Assert.Throws<ArgumentException>(() => writer.GetSpan(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Consume(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Consume(1));

        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetMemory());
        Assert.Throws<ObjectDisposedException>(() => writer.Consume(0));
    }

    private static byte[] CreatePayload(int length)
    {
        byte[] payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % byte.MaxValue);
        }

        return payload;
    }

    private static void Write(PooledByteBufferWriter writer, ReadOnlySpan<byte> data)
    {
        Span<byte> destination = writer.GetSpan(data.Length);
        data.CopyTo(destination);
        writer.Advance(data.Length);
    }
}
