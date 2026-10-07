using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

/// <summary>
/// Regression tests for the delta-create parity behavior in
/// libgit2 1.9.4. Delta bytes were differentially verified against the C
/// reference (libgit2 1.9.4, <c>src/libgit2/delta.c</c>) with a C
/// <c>delta_harness</c> (git_delta_index_init + git_delta_create_from_index
/// on the identical buffers).
///
/// C initializes <c>msize</c>/<c>moff</c> once before the loop and persists
/// them across iterations (delta.c:336-337, 418): after a copy op that
/// split a &gt;64 KB match, <c>msize = left</c> carries the remainder
/// (&lt; 4096) into the next iteration as the match floor AND <c>moff</c>
/// (already advanced by the copy size) as the fallback copy source. Resetting
/// both at the top of every match-finding block would discard the
/// floor/fallback and emit a different (valid but not bit-exact) delta
/// for targets with a &gt;64 KB match whose remainder is in (0, 4096).
/// </summary>
public class DeltaEncoderParityTests
{
    /// <summary>
    /// Builds the regression vector: source = 'A'×run + 'B'×64; target =
    /// 'A'×run + 'B'×64 + 'C'×16 (run = 0x10000 + k). C emits
    /// copy(0x10000@0) [opcode 0x80, no size byte → 64 KB], copy(left@0x10000),
    /// insert 16 'C' — 27/28 bytes. Re-matching from scratch would emit a
    /// 31-byte delta.
    /// </summary>
    private static (byte[] Source, byte[] Target) BuildVector(int k)
    {
        int run = 0x10000 + k;
        byte[] source = new byte[run + 64];
        byte[] target = new byte[run + 64 + 16];
        Array.Fill(source, (byte)'A', 0, run);
        Array.Fill(source, (byte)'B', run, 64);
        Array.Fill(target, (byte)'A', 0, run);
        Array.Fill(target, (byte)'B', run, 64);
        Array.Fill(target, (byte)'C', run + 64, 16);
        return (source, target);
    }

    private static string ToHex(byte[] bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    [Theory]
    [InlineData(0x0, "c08004d08004809401401043434343434343434343434343434343")]
    [InlineData(0x1, "c18004d18004809401411043434343434343434343434343434343")]
    [InlineData(0x3f, "ff80048f81048094017f1043434343434343434343434343434343")]
    [InlineData(0x40, "808104908104809401801043434343434343434343434343434343")]
    [InlineData(0x41, "818104918104809401811043434343434343434343434343434343")]
    [InlineData(0x7f, "bf8104cf8104809401bf1043434343434343434343434343434343")]
    [InlineData(0x80, "c08104d08104809401c01043434343434343434343434343434343")]
    [InlineData(0xff, "bf8204cf820480b4013f011043434343434343434343434343434343")]
    [InlineData(0xfff, "bfa004cfa00480b4013f101043434343434343434343434343434343")]
    [InlineData(0x1000, "c0a004d0a00480b40140101043434343434343434343434343434343")]
    [InlineData(0x10ff, "bfa204cfa20480b4013f111043434343434343434343434343434343")]
    [InlineData(0x2fff, "bfe004cfe00480b4013f301043434343434343434343434343434343")]
    public void CreateFromIndex_LargeMatchRemainder_BitExactWithC(int k, string expectedHex)
    {
        (byte[] source, byte[] target) = BuildVector(k);

        byte[]? delta = DeltaEncoder.Create(source, target, maxDeltaSize: 0);
        Assert.NotNull(delta);
        Assert.Equal(expectedHex, ToHex(delta!));
        DeltaEncoder.DeltaIndex shared = Assert.IsType<DeltaEncoder.DeltaIndex>(DeltaEncoder.BuildIndexFromRetainedBuffer(source));
        Assert.Equal(delta, DeltaEncoder.CreateFromIndex(shared, target, maxDeltaSize: 0));
    }

    [Fact]
    public void CreateFromIndex_LargeMatchRemainder_RoundTrips()
    {
        // All 12 vectors must also apply back to the target (the C harness
        // verified "apply ok" on the C side; this guards the C# Apply).
        foreach (int k in new[] { 0, 1, 0x3f, 0x40, 0x41, 0x7f, 0x80, 0xff, 0xfff, 0x1000, 0x10ff, 0x2fff })
        {
            (byte[] source, byte[] target) = BuildVector(k);
            byte[]? delta = DeltaEncoder.Create(source, target, maxDeltaSize: 0);
            Assert.NotNull(delta);
            byte[] applied = DeltaEncoder.Apply(source, delta!);
            Assert.Equal(target, applied);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedIndex_MultipleTargetsAndRejectedDelta_MatchFreshIndexes(bool shareSource)
    {
        (byte[] source, byte[] target) = BuildVector(0x1000);
        DeltaEncoder.DeltaIndex index = Assert.IsType<DeltaEncoder.DeltaIndex>(shareSource
            ? DeltaEncoder.BuildIndexFromRetainedBuffer(source)
            : DeltaEncoder.BuildIndex(source));
        byte[] changed = (byte[])target.Clone();
        changed[changed.Length / 2] ^= 0x7f;

        foreach (byte[] candidate in new[] { target, changed, source, target })
        {
            foreach (int limit in new[] { 1, 0, 100 })
            {
                byte[]? expected = DeltaEncoder.Create(source, candidate, limit);
                byte[]? actual = DeltaEncoder.CreateFromIndex(index, candidate, limit);
                Assert.Equal(expected, actual);
                if (actual is not null)
                {
                    Assert.Equal(candidate, DeltaEncoder.Apply(source, actual));
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void BuildIndex_SnapshotsSourceSlice(int offset)
    {
        (byte[] source, byte[] target) = BuildVector(0x1000);
        byte[] buffer = new byte[source.Length + offset + 5];
        source.CopyTo(buffer, offset);
        DeltaEncoder.DeltaIndex index = Assert.IsType<DeltaEncoder.DeltaIndex>(
            DeltaEncoder.BuildIndex(buffer.AsMemory(offset, source.Length)));
        byte[]? expected = DeltaEncoder.Create(source, target, 0);

        Array.Fill(buffer, (byte)0xff);

        Assert.Equal(source, index.Src.ToArray());
        Assert.Equal(expected, DeltaEncoder.CreateFromIndex(index, target, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(65536)]
    public void BuildIndexFromRetainedBuffer_SharesSourceAndPreservesDeltas(int length)
    {
        byte[] source = new byte[length];
        new Random(42).NextBytes(source);
        byte[] original = (byte[])source.Clone();
        byte[] target = (byte[])source.Clone();
        target[^1] ^= 0x7f;
        DeltaEncoder.DeltaIndex index = Assert.IsType<DeltaEncoder.DeltaIndex>(DeltaEncoder.BuildIndexFromRetainedBuffer(source));

        Assert.True(index.Src.Equals(source.AsMemory()));
        byte[]? delta = DeltaEncoder.CreateFromIndex(index, target, 0);
        Assert.Equal(DeltaEncoder.Create(source, target, 0), delta);
        Assert.NotNull(delta);
        Assert.Equal(target, DeltaEncoder.Apply(source, delta));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(65536)]
    public void RetainedIndex_SlicedBuffers_PreserveBoundsAndBytes(int length)
    {
        byte[] backing = new byte[length + 23];
        new Random(42).NextBytes(backing);
        byte[] original = (byte[])backing.Clone();
        ReadOnlyMemory<byte> source = backing.AsMemory(7, length);
        byte[] targetBacking = new byte[length + 29];
        source.CopyTo(targetBacking.AsMemory(11, length));
        targetBacking[11 + length - 1] ^= 0x7f;
        ReadOnlyMemory<byte> target = targetBacking.AsMemory(11, length);
        DeltaEncoder.DeltaIndex index = Assert.IsType<DeltaEncoder.DeltaIndex>(DeltaEncoder.BuildIndexFromRetainedBuffer(source));

        Assert.True(source.Equals(index.Src));
        byte[]? delta = DeltaEncoder.CreateFromIndex(index, target, 0);
        Assert.Equal(DeltaEncoder.Create(source, target, 0), delta);
        Assert.NotNull(delta);
        Assert.Equal(target.ToArray(), DeltaEncoder.Apply(source.Span, delta));
        Assert.Equal(original, backing);
    }

    [Fact]
    public void BuildIndex_EmptySource_ReturnsNullForBothBuilders()
    {
        Assert.Null(DeltaEncoder.BuildIndex(ReadOnlyMemory<byte>.Empty));
        Assert.Null(DeltaEncoder.BuildIndexFromRetainedBuffer(ReadOnlyMemory<byte>.Empty));
    }
}
