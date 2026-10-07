using System.Diagnostics;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

// ReadAsync must bound inflation the way C's read_loose_standard
// (odb_loose.c:305-337) does: inflate at most a 64-byte head window, parse
// the header, then inflate only hdr.size body bytes — a crafted tiny loose
// object that decompresses to gigabytes fails cleanly instead of forcing a
// multi-GB allocation (zlib-bomb memory amplification on malicious repos).
//
// ReadHeaderAsync must do a single bounded read of at most 1024 bytes like
// C's read_header_loose (odb_loose.c:422-459), so a header-only lookup on a
// multi-GB loose blob allocates 1 KB, not the whole file.
public sealed class LooseObjectBoundedReadRegressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;

    public LooseObjectBoundedReadRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_LooseObjectBounded_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _objectsDir = Path.Combine(_tempDir, "objects");
        Directory.CreateDirectory(_objectsDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Writes arbitrary compressed bytes at the object path of <paramref name="oid"/>.</summary>
    private void WriteCompressed(GitOid oid, byte[] compressed)
    {
        string path = Path.Combine(_objectsDir, oid.ToPathString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, compressed);
    }

    private static byte[] Compress(string content) => ZlibTestHelpers.CompressLooseObject(Encoding.ASCII.GetBytes(content));

    [Fact]
    public async Task ZlibBomb_StreamLongerThanDeclared_ThrowsPrematureAbort()
    {
        // Declared "blob 1000" but the stream carries ~1 MiB of body. C
        // inflates at most 1000 + 64 bytes and fails the done-check.
        string content = "blob 1000\0" + new string('a', 1024 * 1024);
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, Encoding.ASCII.GetBytes(new string('a', 1000)), GitHashAlgorithmKind.Sha1);
        WriteCompressed(oid, Compress(content));

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Zlib, ex.Category);
        Assert.Contains("stream aborted prematurely", ex.Message);
    }

    [Fact]
    public async Task ZlibBomb_DeclaredSizeOverIntMax_ThrowsCleanGitException()
    {
        // A crafted header declaring a size above int.MaxValue with a short
        // body fails with a clean GitException like
        // C's OOM-class error (odb_loose.c:309-313) — and never
        // materializes the declared size.
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        WriteCompressed(oid, Compress("blob 5000000000\0hello"));

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Contains("larger than available memory", ex.Message);
    }

    [Fact]
    public async Task TrailingGarbage_WithinHeadWindow_ThrowsTrailingGarbage()
    {
        // Control: a complete tiny object with trailing garbage — the stream
        // ends within the 64-byte head window, so the error is "zlib input
        // had trailing garbage" (the get_output entry check,
        // zstream.c:143-146), not the premature-abort message.
        byte[] compressed = Compress("blob 5\0hello");
        byte[] withGarbage = [.. compressed, (byte)'X', (byte)'Y', (byte)'Z'];
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        WriteCompressed(oid, withGarbage);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Contains("trailing garbage", ex.Message);
    }

    [Fact]
    public async Task ShortBody_IsZeroPadded()
    {
        // Control: a stream shorter than the declared size is silently
        // zero-padded (read_loose_standard, odb_loose.c:333) — bounded
        // inflation must preserve this.
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, new byte[60], GitHashAlgorithmKind.Sha1);
        WriteCompressed(oid, Compress("blob 60\0hello")); // declares 60, carries 5

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        RawObjectData? raw = await backend.ReadAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(raw);
        Assert.Equal(60, raw!.Value.Size);
        byte[] data = raw!.Value.Data;
        Assert.Equal("hello", Encoding.ASCII.GetString(data.AsSpan(0, 5)));
        Assert.All(data.AsSpan(5).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task ReadHeader_OnFifo_DoesNotWaitForEof()
    {
        // C's read_header_loose does a SINGLE 1024-byte p_read
        // (odb_loose.c:433-437): a single bounded read returns the header
        // immediately, without waiting for the writer to close a FIFO.
        if (OperatingSystem.IsWindows())
        {
            return; // FIFOs via mkfifo are POSIX-only
        }

        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        string path = Path.Combine(_objectsDir, oid.ToPathString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Process mkfifo = Process.Start(new ProcessStartInfo("mkfifo", path) { RedirectStandardError = true })!;
        await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, mkfifo.ExitCode);

        byte[] compressed = Compress("blob 5\0hello");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Writer: push the object bytes into the FIFO, then HOLD the pipe
        // open (no EOF) until cancelled.
        var writer = Task.Run(async () =>
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
                await fs.WriteAsync(compressed, cts.Token);
                await fs.FlushAsync(cts.Token);
                await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }, cts.Token);

        try
        {
            await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);

            // The single bounded read returns the header immediately; a
            // whole-file read would block until the writer closes the pipe.
            GitObjectHeader? header = await backend.ReadHeaderAsync(oid, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotNull(header);
            Assert.Equal(GitObjectType.Blob, header!.Value.Type);
            Assert.Equal(5, header!.Value.Size);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await writer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
            }
        }
    }
}
