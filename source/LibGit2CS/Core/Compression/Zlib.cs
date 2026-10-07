// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.IO.Compression;

using LibGit2CS.Utils;

namespace LibGit2CS.Core.Compression;

/// <summary>
/// Zlib/DEFLATE compression shim. Replaces libgit2's <c>zstream</c> abstraction.
/// </summary>
/// <remarks>
/// <para>
/// Git object bodies use zlib-framed compression (2-byte zlib header + raw DEFLATE +
/// 4-byte Adler-32 trailer) for both loose and pack objects.
/// <see cref="LibGit2CS.Core.Compression.Zlib.DecompressPackObject(System.ReadOnlySpan{byte})"/> uses the .NET 11 span-based
/// <see cref="ZLibDecoder"/>.
/// </para>
/// <para>
/// Backed by the .NET 11 <see cref="ZLibEncoder"/>/<see cref="ZLibDecoder"/> codecs.
/// No native zlib dependency beyond the BCL's bundled zlib.
/// </para>
/// </remarks>
internal static class Zlib
{
    /// <summary>Maximum possible header length. Matches <c>MAX_HEADER_LEN</c> (odb_loose.c:40).</summary>
    public const int MaxHeaderLen = 64;

    /// <summary>
    /// Inflates at most <c>MAX_HEADER_LEN</c> (64) bytes from the start of a
    /// zlib-framed stream. Matches <c>read_header_loose_standard</c>
    /// (odb_loose.c:429-443): a single inflate chunk that tolerates a stream
    /// ending early or trailing garbage — the caller only needs the header.
    /// Throws <see cref="InvalidDataException"/> only on corrupt data.
    /// </summary>
    /// <param name="destination">The buffer to receive the decompressed header bytes.
    /// Must be at least <see cref="MaxHeaderLen"/> bytes long.</param>
    /// <param name="compressed">The zlib-framed bytes (the loose object file content).</param>
    /// <returns>The number of decompressed bytes written to <paramref name="destination"/>
    /// (at most <see cref="MaxHeaderLen"/>).</returns>
    public static int DecompressLooseObjectHeader(Span<byte> destination, ReadOnlySpan<byte> compressed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, MaxHeaderLen);

        if (compressed.IsEmpty)
        {
            return 0;
        }

        using var decoder = new ZLibDecoder();

        int totalWritten = 0;
        ReadOnlySpan<byte> remaining = compressed;
        while (totalWritten < MaxHeaderLen)
        {
            OperationStatus status = decoder.Decompress(
                remaining,
                destination.Slice(totalWritten),
                out int consumed,
                out int written);

            totalWritten += written;
            remaining = remaining[consumed..];

            switch (status)
            {
                case OperationStatus.Done:
                case OperationStatus.NeedMoreData:
                    // Stream ended (or input exhausted — truncation is tolerated
                    // for header reads, matching C's single-chunk inflate).
                    return totalWritten;
                case OperationStatus.DestinationTooSmall:
                    // 64-byte window filled — enough for the header.
                    if (written == 0)
                    {
                        return totalWritten;
                    }

                    continue;
                default: // OperationStatus.InvalidData
                    throw new InvalidDataException("corrupt zlib stream");
            }
        }

        return totalWritten;
    }

    /// <summary>
    /// Decompresses a zlib-framed stream (pack object body format).
    /// The caller is responsible for having already consumed any pack-frame bytes
    /// (type/size header, OFS_DELTA base offset, or REF_DELTA base OID).
    /// </summary>
    /// <param name="compressed">The zlib-framed bytes starting immediately after the pack object header.</param>
    /// <returns>The decompressed object body. The caller owns the returned writer
    /// and must dispose it to return the buffer to the pool.</returns>
    public static PooledByteBufferWriter DecompressPackObject(ReadOnlySpan<byte> compressed)
    {
        int initialCapacity = Math.Max(256, compressed.Length * 4);
        var writer = new PooledByteBufferWriter(initialCapacity);
        try
        {
            DecompressZlib(writer, compressed, out _);
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        return writer;
    }

    /// <summary>
    /// Decompresses the <b>first</b> zlib-framed stream in <paramref name="compressed"/>
    /// and reports how many compressed bytes were consumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stops at the end of the first complete zlib stream (Adler-32 trailer
    /// consumed); any trailing bytes (e.g. the next pack object's stream in a
    /// multi-object read buffer) are ignored. <paramref name="bytesConsumed"/>
    /// is the exact length of the single stream just decoded.
    /// </para>
    /// <para>
    /// Used by the pack indexer, which reads a generous chunk that may span
    /// several objects and needs the exact compressed length of each to advance
    /// to the next.
    /// </para>
    /// <para>
    /// The decompressed body is <b>appended</b> to <paramref name="writer"/>.
    /// Callers that reuse a writer across attempts (e.g. the indexer's
    /// grow-and-retry window loop) must reset it between attempts — a failed
    /// attempt may have already advanced the writer with partial output.
    /// </para>
    /// </remarks>
    /// <param name="writer">The writer to receive the decompressed body of the first stream.</param>
    /// <param name="compressed">A buffer whose head is one zlib-framed stream.</param>
    /// <param name="bytesConsumed">Receives the number of bytes consumed from <paramref name="compressed"/> (the full length of the decoded stream).</param>
    public static void DecompressPackObject(PooledByteBufferWriter writer, ReadOnlySpan<byte> compressed, out int bytesConsumed)
    {
        int initialCapacity = Math.Max(256, compressed.Length * 4);
        writer.GetSpan(initialCapacity); // Ensure initial capacity is allocated
        DecompressZlib(writer, compressed, out bytesConsumed);
    }

    /// <summary>
    /// Compresses bytes into a zlib-framed stream (loose object format).
    /// Used by the loose object write path.
    /// </summary>
    public static void CompressLooseObject(PooledByteBufferWriter writer, ReadOnlySpan<byte> raw, CompressionLevel level = CompressionLevel.Optimal)
    {
        int quality = level switch
        {
            CompressionLevel.NoCompression => 0,
            CompressionLevel.Fastest => 1,
            CompressionLevel.Optimal => -1,
            CompressionLevel.SmallestSize => 9,
            _ => -1,
        };

        int maxLen = (int)ZLibEncoder.GetMaxCompressedLength(raw.Length);
        if (maxLen == 0)
        {
            return;
        }

        Span<byte> buffer = writer.GetSpan(maxLen);

        using var encoder = new ZLibEncoder(quality);
        int totalWritten = 0;
        ReadOnlySpan<byte> src = raw;

        while (true)
        {
            OperationStatus status = encoder.Compress(
                src,
                buffer.Slice(totalWritten),
                out int consumed,
                out int written,
                isFinalBlock: true);

            totalWritten += written;
            src = src[consumed..];

            if (status == OperationStatus.Done)
            {
                break;
            }

            if (status == OperationStatus.DestinationTooSmall)
            {
                throw new InvalidDataException(
                    "zlib compressed output exceeded the GetMaxCompressedLength bound");
            }

            // NeedMoreData: input not fully consumed; loop again with the same isFinalBlock.
            if (src.IsEmpty)
            {
                throw new InvalidDataException("zlib encoder stalled with no remaining input");
            }
        }

        // Drain any pending output the encoder deferred until finalization.
        while (true)
        {
            OperationStatus flushStatus = encoder.Flush(buffer.Slice(totalWritten), out int flushWritten);
            totalWritten += flushWritten;

            if (flushStatus == OperationStatus.Done)
            {
                break;
            }

            throw new InvalidDataException("zlib flush output exceeded the GetMaxCompressedLength bound");
        }

        writer.Advance(totalWritten);
    }

    /// <summary>
    /// Decompresses the first complete zlib-framed byte sequence in
    /// <paramref name="compressed"/> into a freshly allocated buffer, reporting
    /// the number of source bytes consumed. Uses the resumable
    /// <see cref="ZLibDecoder"/> with a pooled, growable destination.
    /// </summary>
    private static void DecompressZlib(PooledByteBufferWriter writer, ReadOnlySpan<byte> compressed, out int bytesConsumed)
    {
        if (compressed.IsEmpty)
        {
            // C's inflate of zero input yields Z_BUF_ERROR (zstream.c:148-156); packfile_unpack_ compressed errors "error inflating zlib stream" (pack.c:905-
            // 915) and the loose path fails too (in C it hangs, so the port errors).
            throw new InvalidDataException("truncated zlib stream");
        }

        using var decoder = new ZLibDecoder();

        int totalConsumed = 0;
        ReadOnlySpan<byte> remaining = compressed;
        while (true)
        {
            Span<byte> dst = writer.GetSpan(remaining.Length);
            OperationStatus status = decoder.Decompress(remaining, dst, out int consumed, out int written);

            writer.Advance(written);
            totalConsumed += consumed;
            remaining = remaining[consumed..];

            switch (status)
            {
                case OperationStatus.Done:
                    bytesConsumed = totalConsumed;
                    return;
                case OperationStatus.DestinationTooSmall:
                    // Decoder produced some output but more is pending. Force a
                    // bigger span if no progress was made this iteration to
                    // avoid an infinite loop.
                    if (written == 0)
                    {
                        writer.GetSpan(writer.Capacity * 2);
                    }

                    continue;
                case OperationStatus.NeedMoreData:
                    if (remaining.IsEmpty)
                    {
                        throw new InvalidDataException("truncated zlib stream");
                    }

                    continue;
                default: // OperationStatus.InvalidData
                    throw new InvalidDataException("corrupt zlib stream");
            }
        }
    }
}
