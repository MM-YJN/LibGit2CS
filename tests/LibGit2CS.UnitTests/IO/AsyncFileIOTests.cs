using LibGit2CS.IO;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.IO;

public sealed class AsyncFileIOTests : IDisposable
{
    private readonly string _tempDir;

    public AsyncFileIOTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_AsyncFileIO_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
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

    [Fact]
    public async Task WriteAllTextAsync_ReadAllTextAsync_RoundTrips()
    {
        string path = Path.Combine(_tempDir, "text.txt");
        const string content = "hello world\nsecond line";

        await AsyncFileIO.WriteAllTextWithNoBomAsync(path, content, CancellationToken.None);
        string actual = await AsyncFileIO.ReadAllTextWithNoBomAsync(path, CancellationToken.None);

        Assert.Equal(content, actual);
    }

    [Fact]
    public async Task WriteAllTextAsync_NoBom()
    {
        string path = Path.Combine(_tempDir, "nobom.txt");
        await AsyncFileIO.WriteAllTextWithNoBomAsync(path, "abc", CancellationToken.None);

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0x61, bytes[0]); // 'a' — no BOM prefix
        Assert.Equal(3, bytes.Length);
    }

    [Fact]
    public async Task ReadLinesAsync_StreamsLinesLazily()
    {
        string path = Path.Combine(_tempDir, "lines.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\n", cancellationToken: TestContext.Current.CancellationToken);

        var lines = new List<string>();
        await foreach (string line in AsyncFileIO.ReadLinesAsync(path, CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(["alpha", "beta", "gamma"], lines);
    }

    [Fact]
    public async Task AppendAllTextAsync_AppendsWithUtf8NoBom()
    {
        string path = Path.Combine(_tempDir, "append.txt");
        await AsyncFileIO.AppendAllTextAsync(path, "first", CancellationToken.None);
        await AsyncFileIO.AppendAllTextAsync(path, "second", CancellationToken.None);

        string actual = await AsyncFileIO.ReadAllTextWithNoBomAsync(path, CancellationToken.None);
        Assert.Equal("firstsecond", actual);
    }

    [Fact]
    public async Task WriteAtomicBytesAsync_CreatesFileWithContent()
    {
        string path = Path.Combine(_tempDir, "atomic.bin");
        byte[] data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        await AsyncFileIO.WriteAtomicAsync(path, data, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(data, await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAtomicBytesAsync_OverwritesExistingFile()
    {
        string path = Path.Combine(_tempDir, "atomic_overwrite.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }, cancellationToken: TestContext.Current.CancellationToken);
        byte[] newData = new byte[] { 0xAA, 0xBB };

        await AsyncFileIO.WriteAtomicAsync(path, newData, CancellationToken.None);

        Assert.Equal(newData, await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAtomicBytesAsync_LeavesNoTempFile()
    {
        string path = Path.Combine(_tempDir, "atomic_notemp.bin");

        await AsyncFileIO.WriteAtomicAsync(path, new byte[] { 0x01 }, CancellationToken.None);

        string[] tempFiles = Directory.GetFiles(_tempDir, "*.tmp.*");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task WriteAtomicBytesAsync_CreatesParentDirectory()
    {
        string path = Path.Combine(_tempDir, "sub", "deep", "atomic.bin");

        await AsyncFileIO.WriteAtomicAsync(path, new byte[] { 0x42 }, CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task WriteAtomicTextAsync_WritesUtf8NoBom()
    {
        string path = Path.Combine(_tempDir, "atomic_text.txt");

        await AsyncFileIO.WriteAtomicTextAsync(path, "héllo", CancellationToken.None);

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0x68, 0xC3, 0xA9, 0x6C, 0x6C, 0x6F }, bytes);
    }

    [Fact]
    public async Task WriteAtomicBytesAsync_PreCancelledToken_Throws()
    {
        string path = Path.Combine(_tempDir, "atomic_cancel.bin");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AsyncFileIO.WriteAtomicAsync(path, new byte[1024 * 1024], cts.Token));
    }

    [Fact]
    public async Task OpenForWriteStreamAsync_ReturnsAsyncStream()
    {
        string path = Path.Combine(_tempDir, "stream.bin");

        await using (FileStream stream = await AsyncFileIO.OpenForWriteStreamAsync(path, FileMode.Create, FileAccess.Write, CancellationToken.None))
        {
            await stream.WriteAsync(new byte[] { 0x01, 0x02, 0x03 }, cancellationToken: TestContext.Current.CancellationToken);
            await stream.FlushAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAllBytesToBufferAsync_EmptyFile_ReturnsEmptyWriter()
    {
        string path = Path.Combine(_tempDir, "empty.bin");
        await File.WriteAllBytesAsync(path, [], cancellationToken: TestContext.Current.CancellationToken);

        using PooledByteBufferWriter writer = await AsyncFileIO.ReadAllBytesToBufferAsync(path, CancellationToken.None);

        Assert.Equal(0, writer.WrittenCount);
        Assert.True(writer.WrittenMemory.IsEmpty);
    }

    [Fact]
    public async Task ReadAllBytesToBufferAsync_ReadsFileContent()
    {
        string path = Path.Combine(_tempDir, "content.bin");
        byte[] data = [0xDE, 0xAD, 0xBE, 0xEF, 0x42];

        await File.WriteAllBytesAsync(path, data, cancellationToken: TestContext.Current.CancellationToken);
        using PooledByteBufferWriter writer = await AsyncFileIO.ReadAllBytesToBufferAsync(path, CancellationToken.None);

        Assert.Equal(data, writer.WrittenMemory.ToArray());
    }
}
