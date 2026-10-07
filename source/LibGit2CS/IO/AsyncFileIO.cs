// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).
// Portions adapted from .NET runtime: Copyright (c) .NET Foundation and Contributors.
// Those portions retain the MIT license; see LICENSES/dotnet-runtime-MIT.txt.

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Utils;

using Microsoft.Win32.SafeHandles;

namespace LibGit2CS.IO;

/// <summary>
/// Shared async filesystem IO toolkit. The single entry point for all
/// filesystem reads and writes in the
/// library. Replaces direct <see cref="File"/>/<see cref="FileStream"/>
/// usage throughout <c>source/LibGit2CS/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Return types</b>: every method returns <see cref="ValueTask"/> /
/// <see cref="ValueTask{TResult}"/> so that small-file reads and cached
/// writes can complete synchronously without allocating a <see cref="Task"/>.
/// </para>
/// <para>
/// <b>ConfigureAwait</b>: every <c>await</c> uses
/// <c>.ConfigureAwait(false)</c> (CA2007 is enabled at
/// warning level in <c>.editorconfig</c>).
/// </para>
/// <para>
/// <b>Exemptions</b>: stat-style calls (<see cref="File.Exists"/>,
/// <see cref="Directory.Exists"/>, <see cref="FileInfo"/> metadata) stay
/// synchronous. Atomic rename via
/// <see cref="File.Move(string, string, bool)"/> is sync (metadata op).
/// </para>
/// <para>
/// <b>Encoding</b>: text helpers use UTF-8 without BOM, matching git's
/// on-disk convention (every existing caller uses
/// <c>new UTF8Encoding(false)</c>).
/// </para>
/// </remarks>
internal static class AsyncFileIO
{
    private static readonly Encoding s_utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads an entire file as UTF-8 (no BOM) text.
    /// </summary>
    public static Task<string> ReadAllTextWithNoBomAsync(string path, CancellationToken cancellationToken)
        => File.ReadAllTextAsync(path, s_utf8NoBom, cancellationToken);

    /// <summary>
    /// Writes text to a file using UTF-8 (no BOM) encoding, creating or
    /// overwriting it.
    /// </summary>
    public static Task WriteAllTextWithNoBomAsync(string path, string content, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(path, content, s_utf8NoBom, cancellationToken);

    /// <summary>
    /// Writes text to a file using UTF-8 (no BOM) encoding, creating or
    /// overwriting it.
    /// </summary>
    public static Task WriteAllTextWithNoBomAsync(string path, ReadOnlyMemory<char> content, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(path, content, s_utf8NoBom, cancellationToken);

    /// <summary>
    /// Opens a <see cref="FileStream"/> configured for async sequential
    /// reading (<see cref="FileOptions.Asynchronous"/>, plus the
    /// <see cref="FileOptions.SequentialScan"/> hint on Windows only — it
    /// costs an extra syscall elsewhere). The open itself is a metadata
    /// operation (exempt from the async-IO rule); the returned stream is what
    /// callers <c>await</c> on.
    /// </summary>
    public static FileStream OpenAsyncSequentialReadStream(string path)
    {
        FileOptions options = FileOptions.Asynchronous | (OperatingSystem.IsWindows() ? FileOptions.SequentialScan : FileOptions.None);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, options);
    }

    /// <summary>
    /// Reads an entire file into a pooled buffer writer — the pooled
    /// counterpart of <see cref="File.ReadAllBytesAsync"/>, modeled on the
    /// BCL's internal implementation. The caller owns the returned writer
    /// and must <see cref="IDisposable.Dispose"/> it. An empty file returns
    /// an empty writer without a read; a file that shrinks below its
    /// open-time size before all bytes are read throws
    /// <see cref="IOException"/>.
    /// </summary>
    public static Task<PooledByteBufferWriter> ReadAllBytesToBufferAsync(string path, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<PooledByteBufferWriter>(cancellationToken);
        }

        // SequentialScan is a perf hint that requires extra sys-call on non-Windows OSes.
        FileOptions options = FileOptions.Asynchronous | (OperatingSystem.IsWindows() ? FileOptions.SequentialScan : FileOptions.None);
        SafeFileHandle sfh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, options);

        long fileLength;
        try
        {
            fileLength = RandomAccess.GetLength(sfh);
        }
        catch
        {
            sfh.Dispose();
            throw;
        }

        if (fileLength > Array.MaxLength)
        {
            sfh.Dispose();
            return Task.FromException<PooledByteBufferWriter>(new IOException("File is too long."));
        }

        if (fileLength == 0)
        {
            // C (futils.c git_futils_readbuffer): reads exactly st_size bytes — a
            // 0-length stat (an empty file, or a special file whose size is
            // inherently 0: pipes, char devices, procfs) yields an empty buffer.
            // The BCL's unknown-length fallback is a convenience we don't port.
            sfh.Dispose();
            return Task.FromResult(new PooledByteBufferWriter());
        }

#pragma warning disable CA2025 // Do not pass 'IDisposable' instances into unawaited tasks
        return ReadAllBytesToBufferAsync(sfh, (int)fileLength, cancellationToken);
#pragma warning restore CA2025 // Do not pass 'IDisposable' instances into unawaited tasks
    }

    private static async Task<PooledByteBufferWriter> ReadAllBytesToBufferAsync(SafeFileHandle sfh, int count, CancellationToken cancellationToken)
    {
        var writer = new PooledByteBufferWriter(count);
        bool ownsWriter = true;
        try
        {
            int index = 0;
            // The entire array is overwritten by the read loop below (an exception is thrown
            // if the file is truncated before all the bytes are read), so it does not need to be zeroed.
            Memory<byte> bytes = writer.GetMemory(count);
            do
            {
                int n = await RandomAccess.ReadAsync(sfh, bytes.Slice(index), index, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new IOException("Unexpected end of file.");
                }

                index += n;
                writer.Advance(n);
            } while (index < count);

            ownsWriter = false;
            return writer;
        }
        finally
        {
            sfh.Dispose();
            if (ownsWriter)
            {
                writer.Dispose();
            }
        }
    }

    /// <summary>
    /// Lazily enumerates the lines of a UTF-8 (no BOM) text file. The file
    /// is streamed line-by-line via <see cref="System.IO.StreamReader.ReadLineAsync()"/>
    /// so callers can exit early on large files without materializing the
    /// whole content.
    /// </summary>
    /// <remarks>
    /// Hot path: config/ref files are read line-by-line. Returns
    /// <see cref="IAsyncEnumerable{T}"/> so callers
    /// <c>await foreach</c>.
    /// </remarks>
    public static async IAsyncEnumerable<string> ReadLinesAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, s_utf8NoBom, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// Appends text to a file using UTF-8 (no BOM) encoding, creating the
    /// file if it does not exist.
    /// </summary>
    public static Task AppendAllTextAsync(string path, string content, CancellationToken cancellationToken)
        => File.AppendAllTextAsync(path, content, s_utf8NoBom, cancellationToken);

    /// <summary> Appends raw bytes to a file, creating the file if it does not exist. Byte-parity surface — C's <c>git_filebuf</c> append mode writes raw bytes
    /// (e.g. <c>git_merge__append_conflicts_to_merge_msg</c>, merge.c:3137-3149). </summary>
    public static Task AppendAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => File.AppendAllBytesAsync(path, data, cancellationToken);

    /// <summary>
    /// Atomically writes bytes to <paramref name="path"/> via a temp-file
    /// + rename. The canonical implementation of the temp-file-then-rename
    /// pattern — the single implementation shared by
    /// <c>LooseObjectBackend</c>, <c>FileConfigBackend</c>,
    /// <c>FileRefBackend</c>, <c>CommitGraphWriter</c>,
    /// <c>MultiPackIndexWriter</c>, and <c>MergeState</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The temp file is a sibling of the target:
    /// <c>{path}.tmp.{guid}</c>. The parent directory is created if missing
    /// (<see cref="System.IO.Directory.CreateDirectory(string)"/> is sync — metadata op, exempt).
    /// </para>
    /// <para>
    /// The rename uses <see cref="File.Move(string, string, bool)"/> with
    /// <c>overwrite: true</c>, which is atomic on POSIX and replaces the
    /// delete-then-move pattern in the original copies. The stray temp file
    /// is cleaned up in <c>finally</c> if the write or rename throws.
    /// </para>
    /// </remarks>
    public static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => await WriteAtomicAsync(path, data, createLeadingDirs: true, createMode: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Atomically writes bytes to <paramref name="path"/> via a temp-file +
    /// rename.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The temp file is a sibling of the target: <c>{path}.tmp.{guid}</c>.
    /// With <paramref name="createLeadingDirs"/> the parent directory is
    /// created if missing (<see cref="System.IO.Directory.CreateDirectory(string)"/> is sync —
    /// metadata op, exempt). C's filebuf creates leading dirs ONLY with
    /// <c>GIT_FILEBUF_CREATE_LEADING_DIRS</c> (filebuf.c:54-59); without it a
    /// missing parent fails with GIT_ENOTFOUND (futils.c:80-81), which the
    /// port surfaces as a GitException.
    /// </para>
    /// <para>
    /// The rename uses <see cref="File.Move(string, string, bool)"/> with
    /// <c>overwrite: true</c>, which is atomic on POSIX and replaces the
    /// delete-then-move pattern in the original copies. The stray temp file
    /// is cleaned up in <c>finally</c> if the write or rename throws.
    /// </para>
    /// </remarks>
    public static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> data, bool createLeadingDirs, CancellationToken cancellationToken)
        => await WriteAtomicAsync(path, data, createLeadingDirs, createMode: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Atomically writes bytes to <paramref name="path"/> via a temp-file +
    /// rename, creating the temp file with <paramref name="createMode"/>.
    /// With a mode set, the temp is created via
    /// <see cref="FileStreamOptions.UnixCreateMode"/>, which the OS masks
    /// with the process umask at open(2) time — the same semantics as C's
    /// <c>git_futils_mktmp</c> (futils.c:26-51). Ignored on Windows. See the
    /// <c>createLeadingDirs</c> overload for the full semantics.
    /// </summary>
    public static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> data, bool createLeadingDirs, UnixFileMode? createMode, CancellationToken cancellationToken)
    {
        if (createLeadingDirs)
        {
            CreateParentDirectory(path);
        }

        string tempPath = $"{path}.tmp.{HexNumber.CreateRandom()}";
        try
        {
            try
            {
                if (createMode is { } mode && !OperatingSystem.IsWindows())
                {
                    // C (futils.c:26-51): the file is created with the given
                    // mode, which open(2) masks with the process umask.
                    // UnixCreateMode applies the same masking atomically.
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = 4096,
                        Options = FileOptions.Asynchronous,
                        UnixCreateMode = mode,
                    };
                    FileStream fs = new(tempPath, options);
                    await using (fs.ConfigureAwait(false))
                    {
                        await fs.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await File.WriteAllBytesAsync(tempPath, data, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (DirectoryNotFoundException) when (!createLeadingDirs)
            {
                // C (futils.c:74-84): a missing parent without
                // CREATE_LEADING_DIRS fails with GIT_ENOTFOUND.
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"failed to create file '{path}'",
                    GitErrorCategory.Os);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }
    }

    /// <summary>
    /// Atomically writes UTF-8 (no BOM) text to <paramref name="path"/>.
    /// Text-overload counterpart of <see cref="WriteAtomicAsync(string, ReadOnlyMemory{byte}, CancellationToken)"/>.
    /// </summary>
    public static async Task WriteAtomicTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        byte[] rentedArray = ArrayPool<byte>.Shared.Rent(s_utf8NoBom.GetByteCount(content));
        try
        {
            int byteCount = s_utf8NoBom.GetBytes(content, rentedArray);
            await WriteAtomicAsync(path, rentedArray.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    /// <summary>
    /// Atomically writes bytes to <paramref name="path"/> via a temp-file +
    /// rename, but only if the target does not already exist. Preserves the
    /// keep-existing-if-present semantic of libgit2's
    /// <c>loose_backend__write</c> (odb_loose.c:1089-1126): duplicate object
    /// writes (a hot path during fetch/clone) must not waste IO + zlib work on
    /// a temp that will be discarded. The existence check is a stat (exempt
    /// the metadata exemption); only the temp write + rename are async.
    /// </summary>
    /// <returns><c>true</c> if the file was written; <c>false</c> if it already existed.</returns>
    public static async Task<bool> WriteAtomicIfMissingAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => await WriteAtomicIfMissingAsync(path, data, createMode: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Atomically writes bytes to <paramref name="path"/> via a temp-file +
    /// rename (with <paramref name="createMode"/> applied at temp creation,
    /// umask-masked by the OS), but only if the target does not already
    /// exist. Preserves the keep-existing-if-present semantic of libgit2's
    /// <c>loose_backend__write</c> (odb_loose.c:1089-1126): duplicate object
    /// writes (a hot path during fetch/clone) must not waste IO + zlib work on
    /// a temp that will be discarded. The existence check is a stat (exempt
    /// the metadata exemption); only the temp write + rename are async.
    /// </summary>
    /// <returns><c>true</c> if the file was written; <c>false</c> if it already existed.</returns>
    public static async Task<bool> WriteAtomicIfMissingAsync(string path, ReadOnlyMemory<byte> data, UnixFileMode? createMode, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return false;
        }

        await WriteAtomicAsync(path, data, createLeadingDirs: true, createMode, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Opens a <see cref="FileStream"/> configured for async IO
    /// (<see cref="FileOptions.Asynchronous"/>). The open itself is a
    /// metadata operation (exempt from the async-IO rule); the returned stream is
    /// what callers <c>await</c> on.
    /// </summary>
    /// <remarks>
    /// Used by pack/index writers that need streaming writes
    /// (e.g. <c>WriteAsync</c>/<c>FlushAsync</c> on the raw <c>FileStream</c>).
    /// </remarks>
    public static ValueTask<FileStream> OpenForWriteStreamAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new FileStream(path, mode, access, FileShare.None, bufferSize: 4096, FileOptions.Asynchronous);
        return ValueTask.FromResult(stream);
    }

    /// <summary>
    /// Reads the trailing <paramref name="length"/> bytes of a file. Used by
    /// <see cref="Index.GitIndex"/>'s <c>compare_checksum</c> port
    /// (index.c:656-673): the reload trigger for <c>git_index_read</c> is the
    /// trailing-checksum comparison, not the mtime.
    /// </summary>
    public static async Task<byte[]> ReadTailAsync(string path, int length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
        fs.Seek(-length, SeekOrigin.End);
        byte[] buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>
    /// Adapts a synchronous <see cref="IEnumerable{T}"/> to
    /// <see cref="IAsyncEnumerable{T}"/>. Used by in-memory backends
    /// (<c>MemoryConfigBackend</c>, <c>SnapshotConfigBackend</c>, and
    /// <c>MemPackBackend</c>) whose data is already materialized in memory and
    /// therefore complete synchronously. The async-iterator form has no
    /// <c>await</c> by design — CS1998 is suppressed for that reason.
    /// </summary>
#pragma warning disable CS1998
    public static async IAsyncEnumerable<T> ToAsyncEnumerableAsync<T>(
        this IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (T item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }
#pragma warning restore CS1998

    private static void CreateParentDirectory(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of stray temp file after a failed write/rename.
        }
    }

    private readonly record struct HexNumber(uint value) : ISpanFormattable
    {
        public static HexNumber CreateRandom()
        {
            Span<byte> buffer = stackalloc byte[4];
            RandomNumberGenerator.Fill(buffer);
            uint value = MemoryMarshal.Read<uint>(buffer);
            return new HexNumber(value);
        }

        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            return string.Create(8, value, (span, val) =>
            {
                Span<byte> buffer = stackalloc byte[4];
                MemoryMarshal.Write(buffer, val);
                bool result = Convert.TryToHexStringLower(buffer, span, out int charsWritten);
                Debug.Assert(result);
                Debug.Assert(charsWritten == 8);
            });
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            Span<byte> buffer = stackalloc byte[4];
            MemoryMarshal.Write(buffer, value);
            return Convert.TryToHexStringLower(buffer, destination, out charsWritten);
        }
    }
}
