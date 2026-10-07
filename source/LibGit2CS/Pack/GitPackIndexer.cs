// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Utils;

namespace LibGit2CS.Pack;

/// <summary>
/// Pack indexer: streams pack data from the network, decompresses each
/// object, resolves delta chains, and writes the <c>.pack</c> + <c>.idx</c> v2
/// files. Managed port of <c>src/libgit2/indexer.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IGitWritePack"/> for the keep-as-pack path. Fetched
/// objects land in a <c>.pack</c> file with a corresponding <c>.idx</c> v2
/// index, instead of being unpacked as loose objects.
/// </para>
/// <para>
/// <b>.idx v2 format</b> (all multi-byte fields big-endian):
/// <code>
/// magic \377tOc (4 bytes)
/// version 2    (4 bytes)
/// fanout table (256 × 4 bytes — cumulative counts)
/// OID table    (N × oid_size — sorted by raw bytes)
/// CRC32 table  (N × 4 bytes)
/// offset table (N × 4 bytes — 0x80000000 | idx if ≥ 2³¹)
/// large offsets (L × 8 bytes — only for offsets ≥ 2³¹)
/// pack checksum (oid_size bytes — SHA-1 of .pack)
/// idx checksum  (oid_size bytes — SHA-1 of all preceding .idx bytes)
/// </code>
/// </para>
/// <para>
/// <b>Async model:</b> all stream IO is now async. The
/// <see cref="FileStream"/> is opened with <see cref="FileOptions.Asynchronous"/>
/// via <see cref="AsyncFileIO.OpenForWriteStreamAsync"/>; <c>Append</c>
/// → <see cref="AppendAsync"/> (<c>_packStream.Write</c>/<c>Flush</c> →
/// <c>WriteAsync</c>/<c>FlushAsync</c>); <see cref="Commit"/> →
/// <see cref="CommitAsync"/> (<c>Read</c>/<c>ReadExactly</c> →
/// <c>ReadAsync</c>/<c>ReadExactlyAsync</c>; decompression is now span-based
/// via <see cref="Zlib.DecompressPackObject(PooledByteBufferWriter, ReadOnlySpan{byte}, out int)"/> —
/// no stream copy). <see cref="TryResolveDeltaAsync"/> calls
/// <c>_odb.LookupAsync</c> for thin-pack base resolution. Disposal is async —
/// closes the temp pack
/// file and deletes it on rollback.
/// </para>
/// </remarks>
public sealed class GitPackIndexer : IGitWritePack
{
    private readonly string _packDir;
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly GitObjectDb? _odb;
    private readonly int _oidSize;
    private readonly string _tempPackPath;
    private readonly FileStream _packStream;
    private GitIncrementalHash _packHash;
    private GitIncrementalHash _idxHash;

    // Objects fully parsed (non-delta or resolved delta), keyed by offset.
    private readonly Dictionary<long, IndexEntry> _objectsByOffset = [];
    private readonly Dictionary<GitOid, IndexEntry> _objectsByOid = [];
    private readonly List<IndexEntry> _objects = [];
    private readonly List<PendingDelta> _pendingDeltas = [];

    private bool _headerParsed;
    private long _expectedObjects;
    private int _processedObjects;
    private long _packOffset;
    private bool _committed;
    private bool _doFsync; // core.fsyncObjectFiles (indexer.c:261-264)
    private bool _disposed;

    /// <summary>The computed pack trailer hash (available after <see cref="CommitAsync"/>).</summary>
    private byte[]? _packChecksum;

    /// <summary>
    /// Creates a pack indexer that will write the <c>.pack</c> + <c>.idx</c>
    /// files to <paramref name="packDir"/>.
    /// </summary>
    /// <param name="packDir">Directory for <c>pack-*.pack</c> / <c>pack-*.idx</c> files.</param>
    /// <param name="oidType">SHA-1 or SHA-256.</param>
    /// <param name="odb">Optional ODB for thin-pack base resolution (fetch path).</param>
    public GitPackIndexer(string packDir, GitHashAlgorithmKind oidType, GitObjectDb? odb = null)
    {
        _packDir = packDir;
        _algorithm = oidType;
        _odb = odb;
        _oidSize = GitOid.SizeFor(oidType);

        Directory.CreateDirectory(packDir);

        _tempPackPath = Path.Join(packDir, "tmp_pack_" + Guid.NewGuid().ToString("N")[..12]);
        if (OperatingSystem.IsWindows())
        {
            _packStream = new FileStream(_tempPackPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 4096, FileOptions.Asynchronous);
        }
        else
        {
            // C creates
            // the pack temp with GIT_PACK_FILE_MODE (0444) via
            // git_futils_mktmp (pack.h:31; indexer.c:183, 201) — umask-masked
            // by open(2). The default 0666&~umask (0644) left the pack
            // owner-writable in shared repos. The open ReadWrite handle keeps
            // the indexer's own reads working on the read-only file.
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = PackFileMode,
            };
            _packStream = new FileStream(_tempPackPath, options);
        }

        _packHash = GitIncrementalHash.Create(oidType);
        _idxHash = GitIncrementalHash.Create(oidType);
    }

    /// <summary>
    /// C's pack/index file mode: 0444 (<c>GIT_PACK_FILE_MODE</c>, pack.h:31),
    /// masked by the process umask at creation.
    /// </summary>
    private const UnixFileMode PackFileMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// Enables/disables flush-to-disk on pack writes. Matches
    /// <c>git_indexer__set_fsync</c> (indexer.c:261-264); the packbuilder
    /// sets it from <c>core.fsyncObjectFiles</c> (pack-objects.c:1453-1454).
    /// </summary>
    public void SetFsync(bool doFsync) => _doFsync = doFsync;

    /// <inheritdoc/>
    public async Task<bool> AppendAsync(ReadOnlyMemory<byte> data, GitIndexerProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_disposed)
        {
            return false;
        }

        // Seek to the end before writing — TryParseObjectsAsync may have
        // moved the stream position during parsing. Without this, a
        // subsequent AppendAsync would overwrite earlier data instead of
        // appending.
        _packStream.Position = _packStream.Length;

        // Write to the pack file
        await _packStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        if (_doFsync)
        {
            // C (indexer.c:1410-1412): with do_fsync set, writes are fsync'd
            // to durable storage (p_fsync, a sync syscall; the BCL async
            // flush has no flush-to-disk variant on this TFM).
            cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable CA1849 // Flush(true) is the only flush-to-disk variant; parity requires the fsync.
            _packStream.Flush(true);
#pragma warning restore CA1849
        }
        else
        {
            await _packStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Parse as many complete objects as possible
        await TryParseObjectsAsync(progress, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> CommitAsync(GitIndexerProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_disposed)
        {
            return false;
        }

        // Parse any remaining objects
        await TryParseObjectsAsync(progress, cancellationToken).ConfigureAwait(false);

        // C (indexer.c:1254-1261, git_indexer_commit): the pack must be
        // exactly <objects><oid_size trailer> — trailing bytes are rejected
        // ("unexpected data at the end of the pack") and a missing trailer is
        // rejected ("missing trailer at the end of the pack"), both
        // GIT_ERROR_INDEXER.
        if (_packOffset + _oidSize < _packStream.Length)
        {
            throw new GitException(GitErrorCode.Error, "unexpected data at the end of the pack", GitErrorCategory.Indexer);
        }

        if (_packOffset + _oidSize > _packStream.Length)
        {
            throw new GitException(GitErrorCode.Error, "missing trailer at the end of the pack", GitErrorCategory.Indexer);
        }

        // The last oidSize bytes of the pack file are the SHA-1/SHA-256 trailer
        // sent by the server. We re-hash only the data bytes (excluding trailer).
        // Use _packOffset (end of the last parsed object) as the data length,
        // NOT _packStream.Length - _oidSize, because the pack stream may
        // contain extra bytes after the trailer (from side-band data packets
        // that include trailing data beyond the pack).
        long dataLen = _packOffset;
        _packHash.Dispose();
        _packHash = GitIncrementalHash.Create(_algorithm);

        _packStream.Position = 0;
        byte[] readBuf = new byte[64 * 1024];
        long remaining = dataLen;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(readBuf.Length, remaining);
            int read = await _packStream.ReadAsync(readBuf.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            _packHash.AppendData(readBuf.AsSpan(0, read));
            remaining -= read;
        }

        // Compute the pack checksum
        GitOid packOid = _packHash.Finalize();
        _packChecksum = packOid.RawBytes.ToArray();
        _packHash.Dispose();

        // Read and verify the trailer from the pack file
        _packStream.Position = dataLen;
        byte[] trailer = new byte[_oidSize];
        await _packStream.ReadExactlyAsync(trailer, cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < _oidSize; i++)
        {
            if (trailer[i] != _packChecksum[i])
            {
                // C (indexer.c:1274-1277): "packfile trailer mismatch",
                // GIT_ERROR_INDEXER.
                throw new GitException(GitErrorCode.Error, "packfile trailer hash mismatch", GitErrorCategory.Indexer);
            }
        }

        // Resolve all pending deltas. Matches git_indexer_commit (indexer.c:1280):
        // total_deltas = total_objects - indexed_objects (here, the deltas still
        // pending after the streaming parse). The per-delta indexed_objects++ and
        // indexed_deltas++ happen inside ResolveDeltasAsync (indexer.c:1163).
        progress.TotalDeltas = _pendingDeltas.Count;
        await ResolveDeltasAsync(progress, cancellationToken).ConfigureAwait(false);

        // If thin-pack bases were injected, rewrite the pack header count +
        // re-hash the full pack + write the real trailer over the fake.
        // Mirrors git_indexer_commit (indexer.c:1290-1296) calling
        // update_header_and_rehash when local_objects > 0.
        if (progress.LocalObjects > 0)
        {
            await UpdateHeaderAndRehashAsync(progress.LocalObjects, cancellationToken).ConfigureAwait(false);
            packOid = GitOid.FromRaw(_packChecksum!, _algorithm);
        }

        // Validate object count (C classifies indexer-stage failures GIT_ERROR_INDEXER).
        if (_processedObjects != _expectedObjects)
        {
            throw new GitException(GitErrorCode.Error, $"pack has {_processedObjects} objects, expected {_expectedObjects}", GitErrorCategory.Indexer);
        }

        // Matches git_indexer_commit (indexer.c:1285 area) — after delta
        // resolution, indexed_objects is the full resolved object count.
        progress.IndexedObjects = _objects.Count;

        // Sort objects by OID (binary order) for the .idx
        _objects.Sort((a, b) => a.Oid.CompareTo(b.Oid));

        // Compute the pack name
        string packHex = Convert.ToHexStringLower(_packChecksum);
        string packName = $"pack-{packHex}";
        string finalPackPath = Path.Join(_packDir, $"{packName}.pack");
        string finalIdxPath = Path.Join(_packDir, $"{packName}.idx");

        // Write the .idx v2 file
        await WriteIdxV2Async(finalIdxPath, cancellationToken).ConfigureAwait(false);

        // Rename the pack file
        await _packStream.DisposeAsync().ConfigureAwait(false);
        File.Move(_tempPackPath, finalPackPath, overwrite: true);

        PackPath = finalPackPath;
        PackChecksum = packOid;

        _committed = true;
        return true;
    }

    /// <summary>The path to the final <c>.pack</c> file (available after <see cref="CommitAsync"/>).</summary>
    public string? PackPath { get; private set; }

    /// <summary>The pack checksum (available after <see cref="CommitAsync"/>).</summary>
    public GitOid? PackChecksum { get; private set; }

    /// <summary>
    /// Writes the <c>.idx</c> v2 file to <paramref name="idxPath"/>.
    /// </summary>
    private async Task WriteIdxV2Async(string idxPath, CancellationToken cancellationToken)
    {
        // Build the complete idx data (everything except the final idx checksum)
        using var idxDataWriter = new PooledByteBufferWriter();
        BuildIdxV2Data(idxDataWriter);
        ReadOnlyMemory<byte> idxData = idxDataWriter.WrittenMemory;

        // Compute the idx checksum over all idx bytes
        _idxHash.Dispose();
        _idxHash = GitIncrementalHash.Create(_algorithm);
        _idxHash.AppendData(idxData.Span);
        GitOid idxOid = _idxHash.Finalize();

        // C writes the
        // .idx via git_filebuf (temp + atomic rename, indexer.c:1383-1391)
        // with GIT_PACK_FILE_MODE 0444 — the previous direct write to the
        // final name left a truncated .idx at the final path on crash and
        // umask-default (0644) permissions.
        string tempIdxPath = idxPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (OperatingSystem.IsWindows())
            {
                FileStream idxStream = new(tempIdxPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.Asynchronous);
                await using (idxStream.ConfigureAwait(false))
                {
                    await idxStream.WriteAsync(idxData, cancellationToken).ConfigureAwait(false);
                    await idxStream.WriteAsync(idxOid.RawBytes.ToArray(), cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.Asynchronous,
                    UnixCreateMode = PackFileMode,
                };
                FileStream idxStream = new(tempIdxPath, options);
                await using (idxStream.ConfigureAwait(false))
                {
                    await idxStream.WriteAsync(idxData, cancellationToken).ConfigureAwait(false);
                    await idxStream.WriteAsync(idxOid.RawBytes.ToArray(), cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(tempIdxPath, idxPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempIdxPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Builds the complete .idx v2 data (everything except the final idx checksum).
    /// </summary>
    private void BuildIdxV2Data(PooledByteBufferWriter writer)
    {
        Span<byte> buffer = writer.GetSpan(8 + 256 * 4);

        // Magic + version (big-endian)

        BinaryPrimitives.WriteUInt32BigEndian(buffer, 0xff744f63);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4), 2);

        buffer = buffer.Slice(8);

        // Fanout table: 256 × uint32 BE (cumulative counts)
        int[] byteCounts = new int[256];
        foreach (IndexEntry entry in _objects)
        {
            byteCounts[entry.Oid.RawBytes[0]]++;
        }

        int cumulative = 0;
        for (int i = 0; i < 256; i++)
        {
            cumulative += byteCounts[i];
            BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(i * 4), (uint)cumulative);
        }

        writer.Advance(8 + 256 * 4);

        // OID table: N × oid_size, sorted by raw bytes (already sorted)
        foreach (IndexEntry entry in _objects)
        {
            writer.Write(entry.Oid.RawBytes);
        }

        // CRC32 table: N × uint32 BE
        foreach (IndexEntry entry in _objects)
        {
            buffer = writer.GetSpan(4);
            BinaryPrimitives.WriteUInt32BigEndian(buffer, entry.Crc);
            writer.Advance(4);
        }

        // Offset table: N × uint32 BE
        // For offsets >= 2^31, store 0x80000000 | index_into_large_offset_table
        var largeOffsets = new List<long>();
        int largeIdx = 0;
        foreach (IndexEntry entry in _objects)
        {
            buffer = writer.GetSpan(4);
            if (entry.Offset >= 0x80000000L)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer, 0x80000000u | (uint)largeIdx);
                largeOffsets.Add(entry.Offset);
                largeIdx++;
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)entry.Offset);
            }
            writer.Advance(4);
        }

        // Large offset table: L × 8 bytes (big-endian uint64)
        buffer = writer.GetSpan(largeOffsets.Count * 8);
        foreach (long off in largeOffsets)
        {
            BinaryPrimitives.WriteUInt64BigEndian(buffer, (ulong)off);
            buffer = buffer.Slice(8);
        }
        writer.Advance(largeOffsets.Count * 8);

        // Pack checksum (SHA-1/SHA-256 of the .pack file)
        byte[]? packChecksum = _packChecksum;
        Debug.Assert(packChecksum is not null, "_packChecksum is set after the pack trailer is verified");
        buffer = writer.GetSpan(_oidSize);
        packChecksum.AsSpan(0, _oidSize).CopyTo(buffer);
        writer.Advance(_oidSize);
    }

    private async Task TryParseObjectsAsync(GitIndexerProgress progress, CancellationToken cancellationToken)
    {
        if (!_headerParsed)
        {
            if (_packStream.Length < 12)
            {
                return;
            }

            _packStream.Position = 0;
            byte[] header = new byte[12];
            await _packStream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

            if (header[0] != (byte)'P' || header[1] != (byte)'A' ||
                header[2] != (byte)'C' || header[3] != (byte)'K')
            {
                // C (indexer.c:108): "wrong pack signature", GIT_ERROR_INDEXER.
                throw new GitException(GitErrorCode.Error, "invalid pack header: bad magic", GitErrorCategory.Indexer);
            }

            int version = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
            if (version != 2)
            {
                // C (indexer.c:113): "wrong pack version", GIT_ERROR_INDEXER.
                throw new GitException(GitErrorCode.Error, $"unsupported pack version {version}", GitErrorCategory.Indexer);
            }

            // C (indexer.c:907-915): the object count is unsigned; values the port cannot represent (>= 2^31 — the int progress surface) are rejected up front
            // with "too many objects" (GIT_ERROR_INDEXER) instead of wrapping negative.
            long expected = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
            if (expected > int.MaxValue)
            {
                throw new GitException(GitErrorCode.Error, "too many objects", GitErrorCategory.Indexer);
            }

            _expectedObjects = expected;
            _headerParsed = true;
            _packOffset = 12;

            // Matches git_indexer_append (indexer.c:924-929): on header parse,
            // publish the total object count to the shared progress accumulator
            // and zero the counters. The packetsize callback in
            // GitSmartProtocol.DownloadPackAsync reads progress.TotalObjects
            // on every network chunk, so the consumer sees the pack's object
            // count as soon as the 12-byte header is parsed.
            progress.TotalObjects = (int)_expectedObjects;
            progress.IndexedObjects = 0;
            progress.ReceivedObjects = 0;
            progress.LocalObjects = 0;
            progress.TotalDeltas = 0;
            progress.IndexedDeltas = 0;
        }

        while (_processedObjects < _expectedObjects)
        {
            (bool parsed, long endOffset, IndexEntry? entry) = await TryParseOneObjectAsync(cancellationToken).ConfigureAwait(false);
            if (!parsed)
            {
                return; // Need more data
            }

            _packOffset = endOffset;
            _processedObjects++;

            // Matches git_indexer_append (indexer.c:869-871): every parsed
            // object increments received_objects; non-delta objects also
            // increment indexed_objects. Deltas are indexed later in
            // ResolveDeltasAsync.
            progress.ReceivedObjects++;
            if (entry is not null)
            {
                // C (indexer.c:524-534, store_object): a pack containing the
                // same OID twice is rejected ("duplicate object %s found in
                // pack", GIT_ERROR_INDEXER).
                if (_objectsByOid.ContainsKey(entry.Oid))
                {
                    throw new GitException(
                        GitErrorCode.Error,
                        $"duplicate object {entry.Oid} found in pack",
                        GitErrorCategory.Indexer);
                }

                progress.IndexedObjects++;
                _objectsByOffset[entry.Offset] = entry;
                _objectsByOid[entry.Oid] = entry;
                _objects.Add(entry);
            }
        }
    }

    private async Task<(bool Parsed, long EndOffset, IndexEntry? Entry)> TryParseOneObjectAsync(CancellationToken cancellationToken)
    {
        long endOffset = _packOffset;

        long startPos = _packOffset;
        _packStream.Position = startPos;

        // Read the object header (type + size) — variable length
        int b = _packStream.ReadByte();
        if (b < 0)
        {
            return (false, endOffset, null);
        }

        var type = (GitObjectType)((b >> 4) & 7);
        long size = b & 15;
        int shift = 4;
        int headerLen = 1;

        while ((b & 0x80) != 0)
        {
            // C (pack.c:434-438): a continuation chain reaching shift >= 64
            // is corrupt ("packfile corrupted" → git_packfile_unpack_header
            // surfaces "header length is zero", GIT_ERROR_ODB). Without this
            // guard the .NET shift masking silently wraps the size.
            if (shift >= 64)
            {
                throw new GitException(GitErrorCode.Error, "header length is zero", GitErrorCategory.Odb);
            }

            b = _packStream.ReadByte();
            if (b < 0)
            {
                return (false, endOffset, null);
            }

            size |= (long)(b & 0x7f) << shift;
            shift += 7;
            headerLen++;
        }

        long pos = startPos + headerLen;

        if (type == GitObjectType.OfsDelta)
        {
            // Read OFS_DELTA base offset (variable-length, continuation-bit encoding)
            b = _packStream.ReadByte();
            if (b < 0)
            {
                return (false, endOffset, null);
            }

            ulong unsignedBase = (ulong)(b & 127);
            int ofsLen = 1;

            while ((b & 128) != 0)
            {
                unsignedBase += 1;

                // C (pack.c:992-994): a zero or overflowed base offset is
                // corrupt ("invalid pack file - overflow"); MSB(x, 7) covers
                // bits 57..63.
                if (unsignedBase == 0 || (unsignedBase & (~0UL << 57)) != 0)
                {
                    throw new GitException(GitErrorCode.Error, "invalid pack file - overflow", GitErrorCategory.Odb);
                }

                b = _packStream.ReadByte();
                if (b < 0)
                {
                    return (false, endOffset, null);
                }

                unsignedBase = (unsignedBase << 7) + (ulong)(b & 127);
                ofsLen++;
            }

            // C (pack.c:997-999): a zero base or one reaching before the
            // object start is "invalid pack file - out of bounds".
            // The ulong
            // accumulation keeps C's unsigned semantics — a wrapped value
            // stays huge and hits this check instead of wrapping negative.
            if (unsignedBase == 0 || (ulong)startPos <= unsignedBase)
            {
                throw new GitException(GitErrorCode.Error, "invalid pack file - out of bounds", GitErrorCategory.Odb);
            }

            pos = _packStream.Position;
            long baseOffset = startPos - (long)unsignedBase;

            // Decompress the delta body — transiently, only to learn the
            // compressed stream length.
            // the body is NOT retained; resolve_deltas re-inflates it from the
            // pack on demand, mirroring C's read_object_stream draining the
            // stream through a fixed buffer (indexer.c:344-356) + store_delta
            // keeping only delta_off (indexer.c:273-281).
            using var decompressedDataWriter = new PooledByteBufferWriter();
            (bool decompressed, int compressedLen) = await TryDecompressFromPackAsync(decompressedDataWriter, pos, cancellationToken).ConfigureAwait(false);
            if (!decompressed)
            {
                return (false, endOffset, null);
            }

            endOffset = pos + compressedLen;

            _pendingDeltas.Add(new PendingDelta(
                DeltaType: GitObjectType.OfsDelta,
                BaseOffset: baseOffset,
                BaseOid: default,
                ObjectOffset: startPos,
                HeaderLength: headerLen + ofsLen,
                CompressedLength: compressedLen,
                DeclaredSize: size));

            return (true, endOffset, null);
        }
        else if (type == GitObjectType.RefDelta)
        {
            // Read REF_DELTA base OID
            _packStream.Position = pos;
            byte[] baseOidBytes = new byte[_oidSize];
            if (!await TryReadExactAsync(baseOidBytes, cancellationToken).ConfigureAwait(false))
            {
                return (false, endOffset, null);
            }

            pos = _packStream.Position;
            var baseOid = GitOid.FromRaw(baseOidBytes, _algorithm);

            // Decompress the delta body — transiently, only to learn the
            // compressed stream length (see the OFS_DELTA branch above).
            using var decompressedDataWriter = new PooledByteBufferWriter();
            (bool decompressed, int compressedLen) = await TryDecompressFromPackAsync(decompressedDataWriter, pos, cancellationToken).ConfigureAwait(false);
            if (!decompressed)
            {
                return (false, endOffset, null);
            }

            endOffset = pos + compressedLen;

            _pendingDeltas.Add(new PendingDelta(
                DeltaType: GitObjectType.RefDelta,
                BaseOffset: 0,
                BaseOid: baseOid,
                ObjectOffset: startPos,
                HeaderLength: headerLen + _oidSize,
                CompressedLength: compressedLen,
                DeclaredSize: size));

            return (true, endOffset, null);
        }
        else if (type is GitObjectType.Commit or GitObjectType.Tree or GitObjectType.Blob or GitObjectType.Tag)
        {
            // Non-delta: decompress the body — transiently, for hashing only.
            // the body is NOT retained (C's store_object / hash_and_save
            // free it right after hashing, indexer.c:1162); only metadata
            // {oid, crc, offset} + the compressed-stream location are kept so
            // delta resolution can re-inflate this object on demand.
            using var body = new PooledByteBufferWriter();
            (bool decompressed, int compressedLen) = await TryDecompressFromPackAsync(body, pos, cancellationToken).ConfigureAwait(false);
            if (!decompressed)
            {
                return (false, endOffset, null);
            }

            endOffset = pos + compressedLen;

            // Compute OID
            GitOid oid = GitObjectDb.HashObject(type, body.WrittenSpan, _algorithm);

            // Compute CRC32 over the raw packed bytes (header + compressed body)
            int packedLen = headerLen + compressedLen;
            _packStream.Position = startPos;
            byte[] packedBytes = new byte[packedLen];
            if (!await TryReadExactAsync(packedBytes, cancellationToken).ConfigureAwait(false))
            {
                return (false, endOffset, null);
            }

            uint crc = Crc32.HashToUInt32(packedBytes);

            return (true, endOffset, new IndexEntry(oid, crc, startPos, type, headerLen, compressedLen));
        }
        else
        {
            throw new GitException(GitErrorCode.Invalid, $"unknown pack object type {type} at offset {startPos}", GitErrorCategory.Odb);
        }
    }

    private async Task<bool> TryReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await _packStream.ReadAsync(buffer.Slice(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    /// <summary>
    /// Decompresses a zlib stream from the pack file starting at <paramref name="pos"/>.
    /// Returns the decompressed data and the number of compressed bytes consumed.
    /// </summary>
    /// <remarks>
    /// The read buffer is generously sized (up to 256 KB) and may span several
    /// objects; <see cref="Zlib.DecompressPackObject(PooledByteBufferWriter, ReadOnlySpan{byte}, out int)"/>
    /// stops at the first stream boundary and reports the exact compressed
    /// length via <c>compressedLen</c>, so no Adler-32 trailer scan
    /// is needed.
    /// </remarks>
    private async Task<(bool Success, int CompressedLen)> TryDecompressFromPackAsync(PooledByteBufferWriter decompressedDataWriter, long pos, CancellationToken cancellationToken)
    {
        long available = _packStream.Length - pos;
        if (available <= 0)
        {
            return (false, 0);
        }

        // the whole first zlib stream must be present in one buffer
        // (Zlib.DecompressPackObject is single-shot). Start with the legacy
        // 256 KB window — enough for the common case — and grow it until the
        // stream completes or the whole remaining stream is included. This
        // mirrors C's incremental inflate (git_packfile_stream_read): objects
        // with compressed bodies larger than 256 KB index correctly.
        //
        // The window is rented from the shared pool per attempt (Rent may
        // return a larger array — reads and decompress use exact slices) and
        // returned on every exit path; the pool returns arrays ≥ 256 KB
        // without clearing, so only the bytes actually read are ever used.
        int toRead = (int)Math.Min(available, 256 * 1024);
        while (true)
        {
            byte[] compressed = ArrayPool<byte>.Shared.Rent(toRead);
            int actualRead;
            try
            {
                _packStream.Position = pos;
                actualRead = await _packStream.ReadAsync(compressed.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);

                if (actualRead == 0)
                {
                    return (false, 0);
                }

                Zlib.DecompressPackObject(decompressedDataWriter, compressed.AsSpan(0, actualRead), out int compressedLen);
                return (true, compressedLen);
            }
            catch (Exception) when (toRead < available)
            {
                // Stream not complete within this window — grow and retry.
                // (Genuinely corrupt data fails again at the full window and
                // falls through to the catch below.)
                //
                // The failed attempt may have already advanced the writer with
                // partial output (DecompressZlib advances before reporting
                // truncation), so reset it before the retry — otherwise the
                // full stream would be appended after the partial prefix and
                // the caller would hash/consume corrupted data.
                decompressedDataWriter.ResetWrittenCount();
                toRead = NextDecompressWindow(available, toRead);
                if (toRead < 0)
                {
                    return (false, 0);
                }
            }
            catch
            {
                return (false, 0);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }
        }
    }

    /// <summary>
    /// Computes the next decompression window size for
    /// <see cref="TryDecompressFromPackAsync"/>. Returns -1 when the window
    /// would exceed the array limit — the single-shot decompressor cannot
    /// handle it (C streams incrementally, pack.c:847-879). An unchecked
    /// <c>(int)Math.Min(available, toRead * 4)</c> would wrap negative for
    /// packs with &gt;2GB remaining (256K→…→1G→4G) and throw
    /// OverflowException from <c>new byte[toRead]</c>.
    /// </summary>
    internal static int NextDecompressWindow(long available, int current)
    {
        long next = (long)current * 4;
        if (next > int.MaxValue)
        {
            return -1;
        }

        return (int)Math.Min(available, next);
    }

    private async Task ResolveDeltasAsync(GitIndexerProgress progress, CancellationToken cancellationToken)
    {
        var stillPending = new List<PendingDelta>();

        while (_pendingDeltas.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stillPending.Clear();
            bool progressed = false;

            foreach (PendingDelta delta in _pendingDeltas)
            {
                (bool resolved, GitObjectType type, byte[]? data) = await TryResolveDeltaAsync(delta, cancellationToken).ConfigureAwait(false);
                if (resolved)
                {
                    Debug.Assert(data is not null, "data is set when a delta resolves successfully");
                    GitOid oid = GitObjectDb.HashObject(type, data, _algorithm);

                    // Compute CRC32 over the packed bytes
                    _packStream.Position = delta.ObjectOffset;
                    int packedLen = delta.HeaderLength + delta.CompressedLength;
                    byte[] packedBytes = new byte[packedLen];
                    await _packStream.ReadExactlyAsync(packedBytes, cancellationToken).ConfigureAwait(false);
                    uint crc = Crc32.HashToUInt32(packedBytes);

                    // Metadata-only entry: the body is not retained;
                    // IsDelta + BaseOffset/BaseOid let TryRehydrateAsync
                    // re-resolve the chain from the pack on demand.
                    var entry = new IndexEntry(
                        oid, crc, delta.ObjectOffset, type,
                        delta.HeaderLength, delta.CompressedLength,
                        IsDelta: true,
                        BaseOffset: delta.DeltaType == GitObjectType.OfsDelta ? delta.BaseOffset : 0,
                        BaseOid: delta.DeltaType == GitObjectType.RefDelta ? delta.BaseOid : default);
                    if (_objectsByOid.ContainsKey(oid))
                    {
                        throw new GitException(
                            GitErrorCode.Error,
                            $"duplicate object {oid} found in pack",
                            GitErrorCategory.Indexer);
                    }

                    _objectsByOffset[delta.ObjectOffset] = entry;
                    _objectsByOid[oid] = entry;
                    _objects.Add(entry);

                    // Matches git_indexer_commit (indexer.c:1163): each resolved
                    // delta increments both indexed_objects and indexed_deltas in
                    // the shared accumulator.
                    progress.IndexedObjects++;
                    progress.IndexedDeltas++;
                    progressed = true;
                }
                else
                {
                    stillPending.Add(delta);
                }
            }

            _pendingDeltas.Clear();
            _pendingDeltas.AddRange(stillPending);

            if (!progressed)
            {
                // A full pass resolved nothing — the remaining deltas reference
                // bases not yet in the pack. Try to inject one missing base from
                // the ODB (mirrors fix_thin_pack called from resolve_deltas at
                // indexer.c:1178). If injection is not possible (no ODB, no
                // REF_DELTA, or ODB miss), the pack is irrecoverably corrupt.
                bool injected = await FixThinPackAsync(progress, cancellationToken).ConfigureAwait(false);
                if (!injected)
                {
                    // C (indexer.c:1009-1012, 1082-1106): thin-pack fixup failures are "missing delta bases" / -1, GIT_ERROR_INDEXER.
                    throw new GitException(GitErrorCode.Error, $"cannot resolve {_pendingDeltas.Count} delta(s) — missing base objects", GitErrorCategory.Indexer);
                }
            }
        }
    }

    private async Task<(bool Resolved, GitObjectType Type, byte[]? Data)> TryResolveDeltaAsync(PendingDelta delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // neither delta bodies nor base bodies are retained — both are
        // re-inflated from the pack on demand, mirroring C's resolve_deltas →
        // git_packfile_unpack (indexer.c:1141-1148).
        using var decompressedDataWriter = new PooledByteBufferWriter();
        bool deltaOk = await TryReadCompressedAsync(decompressedDataWriter,
            delta.ObjectOffset + delta.HeaderLength, delta.CompressedLength, cancellationToken).ConfigureAwait(false);
        if (!deltaOk)
        {
            return (false, GitObjectType.Ext1, null);
        }

        // C re-reads each
        // delta via git_packfile_unpack → packfile_unpack_compressed, which
        // rejects `total != size` — the inflated delta length must equal the
        // pack header's declared size (pack.c:938-942). A pack whose delta
        // header size mismatches the stream is rejected during indexing rather
        // than committed and failing later at
        // read time.
        if (decompressedDataWriter.WrittenCount != delta.DeclaredSize)
        {
            throw new GitException(GitErrorCode.Error, "error inflating zlib stream", GitErrorCategory.Zlib);
        }

        if (delta.DeltaType == GitObjectType.OfsDelta)
        {
            if (!_objectsByOffset.TryGetValue(delta.BaseOffset, out IndexEntry? baseEntry))
            {
                return (false, GitObjectType.Ext1, null);
            }

            (bool baseOk, byte[]? baseBody) = await TryRehydrateAsync(baseEntry, cancellationToken).ConfigureAwait(false);
            if (!baseOk)
            {
                return (false, GitObjectType.Ext1, null);
            }

            byte[] data = GitDeltaApplier.Apply(baseBody, decompressedDataWriter.WrittenSpan);
            return (true, baseEntry.Type, data);
        }
        else if (delta.DeltaType == GitObjectType.RefDelta)
        {
            // Only resolve against in-pack bases here. When the base is not in
            // the pack, return false — the C# equivalent of libgit2's
            // GIT_PASSTHROUGH from git_packfile_unpack (indexer.c:1148-1150).
            // The ODB is consulted only by FixThinPackAsync → InjectObjectAsync,
            // which appends the missing base to the pack so a subsequent
            // resolution pass succeeds. This mirrors fix_thin_pack
            // (indexer.c:1067) being called from resolve_deltas when a full
            // pass makes no progress.
            if (_objectsByOid.TryGetValue(delta.BaseOid, out IndexEntry? packedBase))
            {
                (bool baseOk, byte[]? baseBody) = await TryRehydrateAsync(packedBase, cancellationToken).ConfigureAwait(false);
                if (!baseOk)
                {
                    return (false, GitObjectType.Ext1, null);
                }

                byte[] data = GitDeltaApplier.Apply(baseBody, decompressedDataWriter.WrittenSpan);
                return (true, packedBase.Type, data);
            }

            return (false, GitObjectType.Ext1, null);
        }

        return (false, GitObjectType.Ext1, null);
    }

    /// <summary>
    /// Re-inflates an object's body from the pack file on demand. Mirrors C's
    /// <c>git_packfile_unpack</c> (pack.c:513-560): object bodies are never
    /// retained, so bases are re-read from the pack; a base that is
    /// itself a resolved delta recurses through its own base, like C's
    /// <c>packfile_unpack_delta</c>.
    /// </summary>
    /// <returns><c>true</c> with the fully resolved body, or <c>false</c> if
    /// the pack is corrupt / the base chain is incomplete.</returns>
    private async Task<(bool Success, byte[] Data)> TryRehydrateAsync(IndexEntry entry, CancellationToken cancellationToken)
    {
        using var data = new PooledByteBufferWriter();
        bool ok = await TryReadCompressedAsync(data,
            entry.Offset + entry.HeaderLength, entry.CompressedLength, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            return (false, []);
        }

        if (!entry.IsDelta)
        {
            return (true, data.WrittenSpan.ToArray());
        }

        IndexEntry? baseEntry = entry.BaseOffset != 0
            ? (_objectsByOffset.TryGetValue(entry.BaseOffset, out IndexEntry? byOffset) ? byOffset : null)
            : (_objectsByOid.TryGetValue(entry.BaseOid, out IndexEntry? byOid) ? byOid : null);
        if (baseEntry is null)
        {
            return (false, []);
        }

        (bool baseOk, byte[] baseBody) = await TryRehydrateAsync(baseEntry, cancellationToken).ConfigureAwait(false);
        if (!baseOk)
        {
            return (false, []);
        }

        return (true, GitDeltaApplier.Apply(baseBody, data.WrittenSpan));
    }

    /// <summary>
    /// Reads exactly <paramref name="compressedLen"/> compressed bytes at
    /// <paramref name="compressedStart"/> and inflates them. Used to
    /// re-read object bodies from the pack on demand (delta resolution).
    /// </summary>
    private async Task<bool> TryReadCompressedAsync(PooledByteBufferWriter decompressedDataWriter, long compressedStart, int compressedLen, CancellationToken cancellationToken)
    {
        if (compressedLen <= 0 || compressedStart < 0 || compressedStart + compressedLen > _packStream.Length)
        {
            return false;
        }

        _packStream.Position = compressedStart;
        byte[] compressed = ArrayPool<byte>.Shared.Rent(compressedLen);
        try
        {
            if (!await TryReadExactAsync(compressed.AsMemory(0, compressedLen), cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            Zlib.DecompressPackObject(decompressedDataWriter, compressed.AsSpan(0, compressedLen), out int consumed);
            if (consumed != compressedLen)
            {
                // The stream ended before the recorded length — corrupt.
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    /// <summary>
    /// Finds the first unresolved REF_DELTA whose base is not in the pack and
    /// injects that base from the ODB. Managed port of <c>fix_thin_pack</c>
    /// (<c>indexer.c:1067</c>). Called by <see cref="ResolveDeltasAsync"/> when a
    /// full pass makes no progress.
    /// </summary>
    /// <returns><c>true</c> if an object was injected; <c>false</c> if injection
    /// was not possible (no ODB, no REF_DELTA among pending deltas, base already
    /// in pack, or ODB miss).</returns>
    private async Task<bool> FixThinPackAsync(GitIndexerProgress progress, CancellationToken cancellationToken)
    {
        if (_odb is null)
        {
            // C (indexer.c:1083): "cannot fix a thin pack without an ODB", GIT_ERROR_INDEXER.
            throw new GitException(GitErrorCode.Error, "cannot fix a thin pack without an ODB", GitErrorCategory.Indexer);
        }

        // Find the first pending REF_DELTA (mirrors fix_thin_pack's
        // git_vector_foreach + GIT_OBJECT_REF_DELTA check, indexer.c:1088-1101).
        // The port's PendingDelta already carries DeltaType + BaseOid, so no
        // re-reading the pack header is needed.
        GitOid baseOid = default;
        bool found = false;
        foreach (PendingDelta delta in _pendingDeltas)
        {
            if (delta.DeltaType == GitObjectType.RefDelta)
            {
                baseOid = delta.BaseOid;
                found = true;
                break;
            }
        }

        if (!found)
        {
            // No REF_DELTA among the stalled deltas — they're all OFS_DELTAs
            // with missing in-pack bases. The pack is corrupt.
            return false;
        }

        // If the base is already known (in-pack), don't re-inject. Mirrors
        // has_entry (indexer.c:1118). In practice this shouldn't trigger when
        // !progressed (the delta would have resolved), but guard defensively.
        if (_objectsByOid.ContainsKey(baseOid))
        {
            return false;
        }

        return await InjectObjectAsync(baseOid, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <paramref name="id"/> from the ODB and appends it as a full
    /// (non-delta) pack entry at the tail of the pack stream, followed by a
    /// fake zero trailer so the pack stays readable mid-fixup. Managed port of
    /// <c>inject_object</c> (<c>indexer.c:988</c>).
    /// </summary>
    /// <returns><c>true</c> on success; <c>false</c> if the ODB doesn't have the object.</returns>
    private async Task<bool> InjectObjectAsync(GitOid id, GitIndexerProgress progress, CancellationToken cancellationToken)
    {
        GitObject? obj;
        try
        {
            obj = await _odb!.LookupAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException)
        {
            // ODB lookup failed (e.g. a pack claims the OID but the content
            // is unreadable due to corruption). Return false to let
            // FixThinPackAsync report "cannot inject" — matching C libgit2's
            // inject_object returning -1 on git_odb_read failure.
            return false;
        }

        if (obj is null)
        {
            return false;
        }

        using (obj)
        {
            // Serialize the pack object header (type + size), then zlib-compress the
            // body. Mirrors git_packfile__object_header + git_zstream_deflatebuf
            // (indexer.c:1023, 1030).
            Span<byte> hdr = stackalloc byte[16];
            int hdrLen = PackEncoding.WriteObjectHeader(hdr, obj.Type, obj.Size);
            using var compressed = new PooledByteBufferWriter();
            Zlib.CompressLooseObject(compressed, obj.Raw.Span);

            // CRC32 over header + compressed body (mirrors inject_object's
            // incremental crc32, indexer.c:1020, 1028, 1038).
            var crc32 = new Crc32();
            crc32.Append(hdr[..hdrLen]);
            crc32.Append(compressed.WrittenSpan);
            uint crc = crc32.GetCurrentHashAsUInt32();

            // Write at _packOffset — which already excludes the old trailer (the
            // port's equivalent of seek_back_trailer reducing mwf.size by oid_size,
            // indexer.c:982-985). This overwrites the prior trailer in place.
            long entryOffset = _packOffset;
            int fakeTrailerLen = _oidSize;
            _packStream.Position = entryOffset;
            await _packStream.WriteAsync(hdr[..hdrLen].ToArray(), cancellationToken).ConfigureAwait(false);
            await _packStream.WriteAsync(compressed.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _packStream.WriteAsync(new byte[fakeTrailerLen], cancellationToken).ConfigureAwait(false);
            await _packStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Truncate any stale bytes beyond the fake trailer (old trailer remnant
            // or sideband trailing data). The file is now exactly:
            //   [header][objects...][injected object][fake zero trailer]
            _packStream.SetLength(entryOffset + hdrLen + compressed.WrittenCount + fakeTrailerLen);

            // Advance _packOffset to the start of the fake trailer — preserves the
            // invariant that _packOffset ≡ (data length excluding trailer).
            _packOffset = entryOffset + hdrLen + compressed.WrittenCount;

            // Register the injected object so subsequent resolution passes find it.
            // Mirrors save_entry (indexer.c:1055) populating idx_cache, objects, fanout.
            // Metadata-only: the body lives only in the pack file.
            var entry = new IndexEntry(id, crc, entryOffset, obj.Type, hdrLen, compressed.WrittenCount);
            _objectsByOffset[entryOffset] = entry;
            _objectsByOid[id] = entry;
            _objects.Add(entry);
        }

        progress.LocalObjects++;
        return true;
    }

    /// <summary>
    /// Rewrites the pack header's object count to include injected objects,
    /// then re-hashes the full pack (excluding the fake trailer) and writes the
    /// real trailer checksum over the fake one. Managed port of
    /// <c>update_header_and_rehash</c> (<c>indexer.c:1186</c>) plus the trailer
    /// write in <c>git_indexer_commit</c> (<c>indexer.c:1294-1295</c>).
    /// </summary>
    /// <remarks>Called by <see cref="CommitAsync"/> only when
    /// <see cref="GitIndexerProgress.LocalObjects"/> &gt; 0.</remarks>
    private async Task UpdateHeaderAndRehashAsync(int localObjects, CancellationToken cancellationToken)
    {
        // Rewrite the object count at bytes 8-11 (big-endian) to include the
        // injected bases. Mirrors update_header_and_rehash indexer.c:1201-1203.
        long newCount = _expectedObjects + localObjects;
        _packStream.Position = 8;
        byte[] countBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(countBuf, (uint)newCount);
        await _packStream.WriteAsync(countBuf, cancellationToken).ConfigureAwait(false);
        await _packStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Re-hash the full pack from 0 to _packOffset (excluding the fake
        // trailer). Mirrors update_header_and_rehash indexer.c:1215-1224 +
        // hash_partially's trailer exclusion.
        _packHash.Dispose();
        _packHash = GitIncrementalHash.Create(_algorithm);

        _packStream.Position = 0;
        byte[] readBuf = new byte[64 * 1024];
        long remaining = _packOffset;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(readBuf.Length, remaining);
            int read = await _packStream.ReadAsync(readBuf.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            _packHash.AppendData(readBuf.AsSpan(0, read));
            remaining -= read;
        }

        // Compute the real trailer and write it over the fake zero trailer.
        // Mirrors git_indexer_commit indexer.c:1294-1295.
        GitOid newChecksum = _packHash.Finalize();
        _packChecksum = newChecksum.RawBytes.ToArray();
        _packStream.Position = _packOffset;
        await _packStream.WriteAsync(_packChecksum, cancellationToken).ConfigureAwait(false);
        _packStream.SetLength(_packOffset + _oidSize);
        await _packStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!_committed)
        {
            try
            {
                await _packStream.DisposeAsync().ConfigureAwait(false);
                if (File.Exists(_tempPackPath))
                {
                    File.Delete(_tempPackPath);
                }
            }
            catch
            {
            }
        }
        else
        {
            try
            {
                await _packStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _packHash.Dispose();
        _idxHash.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Metadata for one indexed object.
    /// no decompressed body is retained — only {oid, crc, offset, type} plus
    /// the compressed-stream location (Offset + HeaderLength, CompressedLength
    /// bytes) so the body can be re-inflated on demand, and — for entries that
    /// were resolved deltas — the base reference for chain re-resolution.
    /// Mirrors C's <c>struct entry</c> {oid, crc, offset, offset_long}
    /// (indexer.c:45-56) plus the pack position metadata.
    /// </summary>
    private sealed record IndexEntry(
        GitOid Oid,
        uint Crc,
        long Offset,
        GitObjectType Type,
        int HeaderLength,
        int CompressedLength,
        bool IsDelta = false,
        long BaseOffset = 0,
        GitOid BaseOid = default);

    private sealed record PendingDelta(
        GitObjectType DeltaType,
        long BaseOffset,
        GitOid BaseOid,
        long ObjectOffset,
        int HeaderLength,
        int CompressedLength,
        long DeclaredSize);
}
