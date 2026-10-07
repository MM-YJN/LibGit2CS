// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Utils;

namespace LibGit2CS.Objects;

/// <summary>
/// Loose object backend — reads objects from <c>.git/objects/xx/yyyy...</c> and
/// writes loose objects to disk. Managed port of libgit2's
/// <c>src/libgit2/odb_loose.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Loose object format</b>: <c>&lt;type&gt; &lt;size&gt;\0&lt;deflate-body&gt;</c>
/// where type is exactly one of <c>blob|tree|commit|tag</c>. Strict parse — any
/// other type or whitespace deviation is a corrupt object.
/// </para>
/// <para>
/// <b>Abbreviated OID matching</b> (<c>exists_prefix</c>): navigates to the
/// <c>objects/xx/</c> directory (first 2 hex chars), then scans all filenames
/// matching the remaining prefix.
/// </para>
/// <para>
/// Implements <see cref="IObjectWriteBackend"/>: <c>WriteAsync</c>,
/// <c>OpenWriteStreamAsync</c>, <c>FreshenAsync</c> (atomic loose object write via
/// <c>Zlib.CompressLooseObject</c> + temp + <c>File.Move</c>).
/// </para>
/// </remarks>
internal sealed class LooseObjectBackend : IObjectBackend, IObjectWriteBackend
{
    /// <summary>Bytes read from the file for header-only reads. Matches <c>read_header_loose</c> (odb_loose.c:451-456).</summary>
    private const int HeaderReadSize = 1024;

    private readonly string _objectsDir;
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly int _oidSize;
    private readonly int _oidHexSize;
    private bool _disposed;

    public LooseObjectBackend(string objectsDir, GitHashAlgorithmKind algorithm)
    {
        _objectsDir = PathHelpers.ToDir(objectsDir);
        _algorithm = algorithm;
        _oidSize = GitOid.SizeFor(algorithm);
        _oidHexSize = _oidSize * 2;
    }

    /// <summary>
    /// Stat-like path existence (C <c>git_fs_path_exists</c> — true for
    /// directories too, odb_loose.c:466-472): a directory at the object path
    /// makes C's <c>exists</c> return true and <c>read</c> fail with a read
    /// error, NOT not-found.
    /// </summary>
    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <inheritdoc/>
    public async Task<RawObjectData?> ReadAsync(GitOid id, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return null;
        }

        string path = GetObjectPath(id);
        if (!PathExists(path))
        {
            return null;
        }

        byte[] compressed = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        // C (read_loose, odb_loose.c:408-427): non-zlib files use the legacy
        // packlike format (binary type/size header + zlib body).
        if (!IsZlibCompressedData(compressed))
        {
            return ParsePacklike(compressed, id);
        }

        // C inflates at
        // most a 64-byte head window, parses the header, then inflates only
        // hdr.size body bytes (read_loose_standard, odb_loose.c:305-337) — a
        // crafted tiny loose object that decompresses to gigabytes fails
        // cleanly instead of forcing a multi-GB allocation.
        try
        {
            return ParseStandardLoose(compressed);
        }
        catch (GitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to decompress loose object {id}: {ex.Message}",
                GitErrorCategory.Zlib);
        }
    }

    /// <inheritdoc/>
    public async Task<GitObjectHeader?> ReadHeaderAsync(GitOid id, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return null;
        }

        string path = GetObjectPath(id);
        if (!PathExists(path))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HeaderReadSize);
        try
        {
            // C (read_header_loose, odb_loose.c:422-491): a single read of at most
            // 1024 bytes, not a fill loop or a read waiting for EOF.
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
            int read = await fs.ReadAsync(buffer.AsMemory(0, HeaderReadSize), cancellationToken).ConfigureAwait(false);
            return ParseLooseObjectHeader(buffer.AsSpan(0, read), id);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static GitObjectHeader ParseLooseObjectHeader(ReadOnlySpan<byte> data, GitOid id)
    {
        if (!IsZlibCompressedData(data))
        {
            // read_header_loose_packlike: binary header from the raw bytes.
            (GitObjectType type, long size) = ParsePacklikeHeader(data);
            if (!IsLooseType(type))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "failed to read loose object header",
                    GitErrorCategory.Zlib);
            }

            return new GitObjectHeader(type, size);
        }

        // read_header_loose_standard: inflate a 64-byte window and parse the
        // header from it. A truncated stream is tolerated (single inflate
        // chunk, no stream-end requirement).
        Span<byte> windowBuffer = stackalloc byte[Zlib.MaxHeaderLen];
        int windowLength;
        try
        {
            windowLength = Zlib.DecompressLooseObjectHeader(windowBuffer, data);
        }
        catch (Exception ex)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to decompress loose object header {id}: {ex.Message}",
                GitErrorCategory.Zlib);
        }

        GitObjectHeader header = ParseHeader(windowBuffer.Slice(0, windowLength))
            ?? throw new GitException(
                GitErrorCode.Error,
                "failed to parse loose object: invalid header",
                GitErrorCategory.Object);

        if (!IsLooseType(header.Type))
        {
            throw new GitException(
                GitErrorCode.Error,
                "failed to read loose object header",
                GitErrorCategory.Zlib);
        }

        return header;
    }

    /// <inheritdoc/>
    public bool Exists(GitOid id)
    {
        if (_disposed)
        {
            return false;
        }

        return PathExists(GetObjectPath(id));
    }

    /// <inheritdoc/>
    public ValueTask<(bool Found, GitOid Oid)> ExistsPrefixAsync(GitOid prefix, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return ValueTask.FromResult<(bool, GitOid)>((false, default));
        }

        string hex = prefix.ToString();
        int hexLength = prefix.HexLength;
        // First 2 hex chars → directory name.
        string dirName = hex[..2];
        string remaining = hex[2..hexLength];
        string dirPath = Path.Join(_objectsDir, dirName);

        if (!Directory.Exists(dirPath))
        {
            return ValueTask.FromResult<(bool, GitOid)>((false, default));
        }

        GitOid? match = null;
        foreach (string filePath in Directory.EnumerateFiles(dirPath))
        {
            string fileName = Path.GetFileName(filePath);
            if (fileName.Length != _oidHexSize - 2)
            {
                continue;
            }

            if (!fileName.StartsWith(remaining, StringComparison.Ordinal))
            {
                continue;
            }

            // Reconstruct the full OID.
            string fullHex = dirName + fileName;
            if (!GitOid.TryParse(fullHex.AsSpan(), _algorithm, out GitOid oid))
            {
                continue;
            }

            if (match is not null && !match.Value.Equals(oid))
            {
                throw new GitException(
                    GitErrorCode.Ambiguous,
                    "abbreviated OID matches multiple loose objects",
                    GitErrorCategory.Odb);
            }

            match = oid;
        }

        if (match is { } foundOid)
        {
            return ValueTask.FromResult<(bool, GitOid)>((true, foundOid));
        }

        return ValueTask.FromResult<(bool, GitOid)>((false, default));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<GitOid> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            yield break;
        }

        if (!Directory.Exists(_objectsDir))
        {
            yield break;
        }

        // Each subdirectory named "xx" (2 hex chars) contains loose objects.
        foreach (string dir in Directory.EnumerateDirectories(_objectsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dirName = Path.GetFileName(dir);
            if (dirName.Length != 2 || !IsHex(dirName))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(file);
                if (fileName.Length != _oidHexSize - 2)
                {
                    continue;
                }

                string fullHex = dirName + fileName;
                if (GitOid.TryParse(fullHex.AsSpan(), _algorithm, out GitOid oid))
                {
                    yield return oid;
                }
            }
        }
    }

    /// <summary>
    /// Writes a complete object atomically. Matches <c>loose_backend__write</c>
    /// (odb_loose.c:1089-1126). Hashes are precomputed by
    /// <see cref="GitObjectDb.WriteAsync"/>; this method formats the header
    /// (<c>"&lt;type&gt; &lt;size&gt;\0"</c>), compresses header+body with zlib,
    /// creates the <c>xx/</c> subdirectory, writes to a temp file, and renames
    /// to the final <c>objects/xx/yyyy...</c> path. The keep-existing-if-present
    /// semantic is preserved by <see cref="LibGit2CS.IO.AsyncFileIO.WriteAtomicIfMissingAsync(string, System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>.
    /// </summary>
    async Task IObjectWriteBackend.WriteAsync(GitOid oid, GitObjectType type, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Combine header + body into a pooled buffer, then zlib-compress.
        // The loose object file format is: zlib(<header><body>).
        // C compresses at Z_BEST_SPEED (level 1) — normalize_options,
        // odb_loose.c:1161-1163.
        using var combined = new PooledByteBufferWriter(32 + body.Length);
        GitOid.WriteHeader(combined, type, body.Length);
        combined.Write(body.Span);

        using var compressed = new PooledByteBufferWriter();
        Zlib.CompressLooseObject(compressed, combined.WrittenSpan, CompressionLevel.Fastest);
        await WriteAtomicIfMissingAsync(oid, compressed.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a streaming write. Matches <c>loose_backend__writestream</c>
    /// (odb_loose.c:856-888). The returned stream compresses body bytes on the
    /// fly and performs the atomic rename at finalize.
    /// </summary>
    Task<ObjectWriteStream> IObjectWriteBackend.OpenWriteStreamAsync(GitObjectType type, long declaredSize, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.FromResult<ObjectWriteStream>(new LooseWriteStream(this, type, declaredSize));
    }

    /// <summary>
    /// Touches an existing object's mtime to prevent GC. Matches
    /// <c>loose_backend__freshen</c> (odb_loose.c:1128-1143). Stat + metadata
    /// only — synchronous.
    /// </summary>
    bool IObjectWriteBackend.Freshen(GitOid oid)
    {
        if (_disposed)
        {
            return false;
        }

        string path = GetObjectPath(oid);
        if (!PathExists(path))
        {
            return false;
        }

        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Atomically writes <paramref name="compressed"/> bytes to the loose object
    /// path for <paramref name="oid"/> unless the target already exists (preserves
    /// the keep-existing-if-present semantic). Delegates to
    /// <see cref="LibGit2CS.IO.AsyncFileIO.WriteAtomicIfMissingAsync(string, System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>, creating the file
    /// with C's object file mode (0444 — <c>GIT_OBJECT_FILE_MODE</c>, odb.h:26).
    /// </summary>
    internal async Task WriteAtomicIfMissingAsync(GitOid oid, ReadOnlyMemory<byte> compressed, CancellationToken cancellationToken)
    {
        string finalPath = GetObjectPath(oid);
        // C creates the
        // object via open(2) with mode 0444, which applies mode & ~umask
        // (futils.c:26-51) — a restrictive umask (e.g. 077) yields 0400.
        // UnixCreateMode applies the umask atomically at temp-file creation,
        // exactly like C's git_futils_mktmp.
        UnixFileMode? createMode = OperatingSystem.IsWindows() ? null : ObjectFileMode;
        await AsyncFileIO.WriteAtomicIfMissingAsync(finalPath, compressed, createMode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// C's object file mode: 0444 (<c>GIT_OBJECT_FILE_MODE</c>, odb.h:26),
    /// masked by the process umask at creation.
    /// </summary>
    private const UnixFileMode ObjectFileMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// Streaming write handle for the loose backend. Compresses body bytes via
    /// <see cref="Zlib.CompressLooseObject"/> at finalize (buffers in memory).
    /// Matches <c>loose_writestream</c> (odb_loose.c:32-35, 811-848).
    /// </summary>
    /// <remarks>
    /// libgit2's <c>git_filebuf</c> streams deflate compression to disk. The
    /// managed port buffers the body in memory and compresses at finalize —
    /// simpler, same byte output, and objects are typically small. Large
    /// streaming writes (pack creation) are in <see cref="MemPackBackend"/>.
    /// </remarks>
    private sealed class LooseWriteStream(LooseObjectBackend owner, GitObjectType type, long declaredSize)
        : ObjectWriteStream(type, declaredSize)
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Intentional inverted ownership — the stream borrows the backend for writes but does not own it; disposing the backend here would release a long-lived shared resource.")]
        private readonly LooseObjectBackend _owner = owner;
        private readonly PooledByteBufferWriter _buffer = new();
        private bool _finalized;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _buffer.Write(buffer);
            ReceivedBytes += buffer.Length;
        }

        public override async Task<GitOid> FinalizeAsync(GitOid expectedOid, CancellationToken cancellationToken)
        {
            if (_finalized)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "loose write stream already finalized",
                    GitErrorCategory.Odb);
            }

            _finalized = true;

            // Combine header + body into a pooled buffer, then zlib-compress.
            // The loose object file format is: zlib(<header><body>).
            // C compresses at Z_BEST_SPEED (level 1).
            using var combined = new PooledByteBufferWriter(32 + _buffer.WrittenCount);
            GitOid.WriteHeader(combined, Type, _buffer.WrittenCount);
            combined.Write(_buffer.WrittenSpan);

            using var compressed = new PooledByteBufferWriter();
            Zlib.CompressLooseObject(compressed, combined.WrittenSpan, CompressionLevel.Fastest);
            await _owner.WriteAtomicIfMissingAsync(expectedOid, compressed.WrittenMemory, cancellationToken).ConfigureAwait(false);
            return expectedOid;
        }

        [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "ObjectWriteStream uses a parameterless DisposeAsyncCore() async-dispose pattern; the base is a no-op (ValueTask.CompletedTask) with nothing to forward.")]
        protected override Task DisposeAsyncCore()
        {
            _buffer.Dispose();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Constructs the path <c>objects/xx/yyyy...</c> for <paramref name="id"/>.
    /// </summary>
    private string GetObjectPath(GitOid id)
    {
        string pathStr = id.ToPathString();
        return Path.Join(_objectsDir, pathStr);
    }

    /// <summary>
    /// Reads a standard (zlib-framed) loose object with C's bounded two-phase
    /// inflation. Matches <c>read_loose_standard</c> (odb_loose.c:305-337);
    /// the error messages and their selection match the C code.
    /// </summary>
    /// <param name="compressed">The full compressed loose object file content.</param>
    private static RawObjectData ParseStandardLoose(byte[] compressed)
    {
        // C inflates at
        // most a 64-byte head window (MAX_HEADER_LEN), parses the header from
        // it, then inflates only hdr.size body bytes — a crafted tiny loose
        // object that decompresses to gigabytes fails after ~64 bytes instead
        // of forcing a multi-GB allocation (a full inflate into an unbounded
        // growable buffer would read the whole stream first).
        using var decoder = new ZLibDecoder();
        ReadOnlySpan<byte> remaining = compressed;
        int bytesConsumed = 0;

        // Inflate at most MAX_HEADER_LEN bytes to parse the header.
        byte[] head = new byte[Zlib.MaxHeaderLen];
        int headLen = 0;
        bool streamEnded = false;
        while (headLen < Zlib.MaxHeaderLen)
        {
            OperationStatus status = decoder.Decompress(remaining, head.AsSpan(headLen), out int consumed, out int written);
            bytesConsumed += consumed;
            headLen += written;
            remaining = remaining[consumed..];

            if (status == OperationStatus.Done)
            {
                streamEnded = true;
                break; // stream ended within the head window
            }

            if (status == OperationStatus.NeedMoreData)
            {
                throw new InvalidDataException("truncated zlib stream");
            }

            if (status == OperationStatus.InvalidData)
            {
                throw new InvalidDataException("corrupt zlib stream");
            }

            // DestinationTooSmall: head window filled.
            if (written == 0)
            {
                throw new InvalidDataException("corrupt zlib stream");
            }
        }

        // C parses the header from the 64-byte head window (parse_header).
        int window = Math.Min(Zlib.MaxHeaderLen, headLen);
        GitObjectHeader header = ParseHeader(head.AsSpan(0, window))
            ?? throw new GitException(
                GitErrorCode.Error,
                "failed to parse loose object: invalid header",
                GitErrorCategory.Object);

        // C (read_loose_standard, odb_loose.c:316-320): non-loose types are
        // rejected with GIT_ERROR_ODB "failed to inflate disk object".
        if (!IsLooseType(header.Type))
        {
            throw new GitException(
                GitErrorCode.Error,
                "failed to inflate disk object",
                GitErrorCategory.Odb);
        }

        int headerEnd = FindNullTerminator(head.AsSpan(0, window));
        if (headerEnd < 0)
        {
            throw new GitException(
                GitErrorCode.Error,
                "loose object header missing null terminator",
                GitErrorCategory.Object);
        }

        // C (read_loose_standard, odb_loose.c:318-322): when the head window
        // already holds more body bytes than the header declares, the error
        // is GIT_ERROR_ODB "malformed object: body was longer than specified
        // in header" — BEFORE the zlib done-check below.
        int windowBody = window - headerEnd - 1;
        if (windowBody > header.Size)
        {
            throw new GitException(
                GitErrorCode.Error,
                "malformed object: body was longer than specified in header",
                GitErrorCategory.Odb);
        }

        // A declared
        // body size above int.MaxValue cannot be materialized — fail with a
        // clean GitException instead of an unhandled OverflowException from
        // the array allocation (C fails with an OOM-class error,
        // odb_loose.c:309-313).
        if (header.Size > int.MaxValue)
        {
            throw new GitException(
                GitErrorCode.Error,
                "object is larger than available memory",
                GitErrorCategory.Object);
        }

        // C (read_loose_standard, odb_loose.c:323-326): allocate hdr.size
        // bytes (calloc — silently zero-padding short bodies) and inflate
        // only the remaining body budget (the window's leftover body bytes
        // count against it).
        byte[] body = new byte[(int)header.Size];
        if (windowBody > 0)
        {
            Array.Copy(head, headerEnd + 1, body, 0, windowBody);
        }

        int bodyTotal = windowBody;
        while (bodyTotal < (int)header.Size)
        {
            OperationStatus status = decoder.Decompress(remaining, body.AsSpan(bodyTotal), out int consumed, out int written);
            bytesConsumed += consumed;
            bodyTotal += written;
            remaining = remaining[consumed..];

            if (status == OperationStatus.Done)
            {
                streamEnded = true;
                break;
            }

            if (status == OperationStatus.NeedMoreData)
            {
                throw new InvalidDataException("truncated zlib stream");
            }

            if (status == OperationStatus.InvalidData)
            {
                throw new InvalidDataException("corrupt zlib stream");
            }

            // DestinationTooSmall: the body budget is filled — the stream
            // still has output pending; the done-check below reports it.
            if (written == 0)
            {
                throw new InvalidDataException("corrupt zlib stream");
            }
        }

        // C (read_loose_standard, odb_loose.c:330-335): trailing bytes after
        // the zlib stream are rejected. When the stream ended within the
        // 64-byte head window the error is "zlib input had trailing garbage"
        // (the get_output entry check, zstream.c:143-146); when it
        // ended during the body inflation (or never ended) the done-check fails
        // with the "stream aborted prematurely" message.
        if (bytesConsumed < compressed.Length)
        {
            if (bodyTotal == windowBody)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "zlib input had trailing garbage",
                    GitErrorCategory.Zlib);
            }

            throw new GitException(
                GitErrorCode.Error,
                "failed to finish zlib inflation: stream aborted prematurely",
                GitErrorCategory.Zlib);
        }

        if (!streamEnded)
        {
            throw new GitException(
                GitErrorCode.Error,
                "failed to finish zlib inflation: stream aborted prematurely",
                GitErrorCategory.Zlib);
        }

        return new RawObjectData(header.Type, header.Size, body);
    }

    /// <summary>
    /// Parses a legacy packlike loose object (binary type/size header +
    /// zlib-compressed body). Matches <c>read_loose_packlike</c> +
    /// <c>parse_header_packlike</c> (odb_loose.c:112-150, 358-394).
    /// </summary>
    private static RawObjectData ParsePacklike(byte[] data, GitOid id)
    {
        (GitObjectType type, long size, int headerLen) = ParsePacklikeHeaderWithLen(data);

        // C (read_loose_packlike, odb_loose.c:371-374): non-loose types are
        // rejected with GIT_ERROR_ODB "failed to inflate loose object".
        if (!IsLooseType(type) || headerLen > data.Length)
        {
            throw new GitException(
                GitErrorCode.Error,
                "failed to inflate loose object",
                GitErrorCategory.Odb);
        }

        byte[] body;
        try
        {
            using PooledByteBufferWriter inflated = Zlib.DecompressPackObject(data.AsSpan(headerLen));
            body = inflated.WrittenSpan.ToArray();
        }
        catch (Exception ex)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to inflate loose object {id}: {ex.Message}",
                GitErrorCategory.Zlib);
        }

        // C sets out->len = hdr.size without validating the inflated length.
        return new RawObjectData(type, size, body);
    }

    /// <summary>
    /// Parses the binary header of a packlike loose object.
    /// Matches <c>parse_header_packlike</c> (odb_loose.c:112-150).
    /// </summary>
    private static (GitObjectType Type, long Size, int HeaderLen) ParsePacklikeHeaderWithLen(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            throw InvalidPacklikeHeader();
        }

        int used = 0;
        byte c = data[used++];
        var type = (GitObjectType)((c >> 4) & 7);
        ulong size = (ulong)(c & 15);
        int shift = 4;
        while ((c & 0x80) != 0)
        {
            if (data.Length <= used || shift >= sizeof(ulong) * 8)
            {
                throw InvalidPacklikeHeader();
            }

            c = data[used++];
            size += (ulong)(c & 0x7f) << shift;
            shift += 7;
        }

        // C accumulates
        // into an unsigned size_t; a wrapped value trips
        // GIT_ADD_SIZET_OVERFLOW in read_loose_packlike (odb_loose.c:258-262)
        // and the object is rejected. Mirror the unsigned semantics: a size
        // that does not fit a signed long is an error.
        if (size > long.MaxValue)
        {
            throw new GitException(
                GitErrorCode.Error,
                "failed to inflate loose object",
                GitErrorCategory.Odb);
        }

        return (type, (long)size, used);
    }

    private static (GitObjectType Type, long Size) ParsePacklikeHeader(ReadOnlySpan<byte> data)
    {
        (GitObjectType type, long size, _) = ParsePacklikeHeaderWithLen(data);
        return (type, size);
    }

    private static GitException InvalidPacklikeHeader() => new(
        GitErrorCode.Error,
        "failed to parse loose object: invalid header",
        GitErrorCategory.Object);

    /// <summary>
    /// Parses just the header portion (<c>&lt;type&gt; &lt;size&gt;</c>) before the
    /// null terminator, from a window of at most <c>MAX_HEADER_LEN</c> bytes.
    /// Matches <c>parse_header</c> (odb_loose.c:152-203).
    /// </summary>
    private static GitObjectHeader? ParseHeader(ReadOnlySpan<byte> window)
    {
        int nullPos = FindNullTerminator(window);
        if (nullPos < 0)
        {
            return null;
        }

        ReadOnlySpan<byte> headerSpan = window[..nullPos];
        int spacePos = headerSpan.IndexOf((byte)' ');
        if (spacePos < 0)
        {
            return null;
        }

        string typeStr = Encoding.ASCII.GetString(headerSpan[..spacePos]);
        string sizeStr = Encoding.ASCII.GetString(headerSpan[(spacePos + 1)..]);

        // C (git_object_stringn2type, object.c:330-343): the type name is
        // matched by PREFIX — the table entry must be a prefix of the name
        // ("blobX" parses as blob; "comm" maps to GIT_OBJECT_INVALID because
        // "commit" is not a prefix of "comm"). parse_header does NOT reject
        // unmatched names — the caller's git_object_typeisloose check does
        // (odb_loose.c:316-320, 481-485).
        GitObjectType type = StringToType(typeStr);

        // C (git__strntol64 as used by parse_header): leading whitespace and a
        // sign are skipped, digits parse until the first non-digit (trailing
        // garbage tolerated), negatives and overflow are rejected.
        if (!TryParseHeaderSize(sizeStr, out long size))
        {
            return null;
        }

        return new GitObjectHeader(type, size);
    }

    /// <summary>
    /// Port of <c>git__strntol64</c> (util.c:34-130) restricted to the way
    /// <c>parse_header</c> calls it (base 10, endptr NULL).
    /// </summary>
    private static bool TryParseHeaderSize(string s, out long size)
    {
        size = 0;
        int i = 0;
        while (i < s.Length && IsAsciiSpace(s[i]))
        {
            i++;
        }

        bool neg = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
        {
            neg = s[i] == '-';
            i++;
        }

        long n = 0;
        bool anyDigit = false;
        bool overflow = false;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c is < '0' or > '9')
            {
                break;
            }

            anyDigit = true;
            if (overflow)
            {
                continue;
            }

            int v = c - '0';
            long nn = unchecked(n * 10 + (neg ? -v : v));
            if (nn / 10 != n)
            {
                overflow = true;
                continue;
            }

            n = nn;
        }

        // ndig == 0 → "not a number"; overflow → error; parse_header also
        // rejects size < 0 (odb_loose.c:190-191).
        if (!anyDigit || overflow || neg)
        {
            return false;
        }

        size = n;
        return true;
    }

    private static bool IsAsciiSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static int FindNullTerminator(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether the buffer starts with a zlib stream. Matches
    /// <c>is_zlib_compressed_data</c> (odb_loose.c:205-211).
    /// </summary>
    private static bool IsZlibCompressedData(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return false;
        }

        int w = (data[0] << 8) + data[1];
        return (data[0] & 0x8F) == 0x08 && w % 31 == 0;
    }

    private static bool IsLooseType(GitObjectType type) => type is >= GitObjectType.Commit and <= GitObjectType.Tag;

    /// <summary>
    /// Maps a loose-header type name to a <see cref="GitObjectType"/> by PREFIX
    /// match against <c>git_objects_table</c> (object.c:35-56) in table order.
    /// Unmatched names map to <see cref="GitObjectType.Ext1"/> (invalid).
    /// </summary>
    private static GitObjectType StringToType(string s) => s switch
    {
        { } when s.StartsWith("commit", StringComparison.Ordinal) => GitObjectType.Commit,
        { } when s.StartsWith("tree", StringComparison.Ordinal) => GitObjectType.Tree,
        { } when s.StartsWith("blob", StringComparison.Ordinal) => GitObjectType.Blob,
        { } when s.StartsWith("tag", StringComparison.Ordinal) => GitObjectType.Tag,
        { } when s.StartsWith("OFS_DELTA", StringComparison.Ordinal) => GitObjectType.OfsDelta,
        { } when s.StartsWith("REF_DELTA", StringComparison.Ordinal) => GitObjectType.RefDelta,
        _ => GitObjectType.Ext1, // invalid
    };

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
