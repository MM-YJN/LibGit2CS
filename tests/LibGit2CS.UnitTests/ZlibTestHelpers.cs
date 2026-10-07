using System.IO.Compression;

using LibGit2CS.Core.Compression;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests;

/// <summary>
/// Test-side convenience over <see cref="Zlib"/>: compresses into a pooled
/// writer and materializes the bytes. Production callers use the writer-based
/// <see cref="Zlib.CompressLooseObject(PooledByteBufferWriter, ReadOnlySpan{byte}, CompressionLevel)"/>
/// directly; tests that need a <c>byte[]</c> fixture use this helper.
/// </summary>
internal static class ZlibTestHelpers
{
    public static byte[] CompressLooseObject(ReadOnlySpan<byte> raw, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(writer, raw, level);
        return writer.WrittenSpan.ToArray();
    }
}
