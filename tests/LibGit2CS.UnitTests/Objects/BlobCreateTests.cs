using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Tests for <see cref="GitBlob.CreateFromWorkdir"/>,
/// <see cref="GitBlob.CreateFromDisk"/>, and <see cref="GitBlob.CreateWriteStream"/>.

/// </summary>
public sealed class BlobCreateTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public BlobCreateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_BlobCreate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task CreateFromWorkdir_WritesBlobAndReturnsOid()
    {
        string relativePath = "hello.txt";
        string fullPath = Path.Combine(_tempDir, relativePath);
        await File.WriteAllTextAsync(fullPath, "hello\nworld\n", cancellationToken: TestContext.Current.CancellationToken);

        GitOid oid = await _repo.BlobCreateFromWorkdirAsync(relativePath, cancellationToken: TestContext.Current.CancellationToken);

        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal("hello\nworld\n", Encoding.UTF8.GetString(blob.Content.Span));
    }

    [Fact]
    public async Task CreateFromWorkdir_NonExistentFile_Throws()
    {
        await Assert.ThrowsAsync<GitException>(async () => await _repo.BlobCreateFromWorkdirAsync("nope.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateFromWorkdir_NullArgs_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await GitBlob.CreateFromWorkdirAsync(null!, "x", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await _repo.BlobCreateFromWorkdirAsync((string)null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateFromDisk_PathInsideWorkdir_AppliesAsWorkdir()
    {
        string relativePath = "inside.txt";
        string fullPath = Path.Combine(_tempDir, relativePath);
        await File.WriteAllTextAsync(fullPath, "inside content\n", cancellationToken: TestContext.Current.CancellationToken);

        GitOid oid = await _repo.BlobCreateFromDiskAsync(fullPath, cancellationToken: TestContext.Current.CancellationToken);

        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal("inside content\n", Encoding.UTF8.GetString(blob.Content.Span));
    }

    [Fact]
    public async Task CreateFromDisk_PathOutsideWorkdir_HashesRawContent()
    {
        // Create a file outside the repo workdir.
        string outsideDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_BlobCreate_outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);
        try
        {
            string outsidePath = Path.Combine(outsideDir, "outside.txt");
            await File.WriteAllTextAsync(outsidePath, "outside content\n", cancellationToken: TestContext.Current.CancellationToken);

            GitOid oid = await _repo.BlobCreateFromDiskAsync(outsidePath, cancellationToken: TestContext.Current.CancellationToken);

            GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(oid, TestContext.Current.CancellationToken);
            Assert.NotNull(blob);
            Assert.Equal("outside content\n", Encoding.UTF8.GetString(blob.Content.Span));
        }
        finally
        {
            try
            {
                Directory.Delete(outsideDir, recursive: true);
            }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task CreateFromDisk_NonExistentFile_Throws()
    {
        await Assert.ThrowsAsync<GitException>(async () => await _repo.BlobCreateFromDiskAsync("/tmp/nonexistent_file_xyz.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateFromDisk_NullArgs_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await GitBlob.CreateFromDiskAsync(null!, "x", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await _repo.BlobCreateFromDiskAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateWriteStream_WritesAndCommitsBlob()
    {
        using GitBlobWriteStream stream = _repo.BlobCreateWriteStream(hintPath: "streamed.txt");
        byte[] content = Encoding.UTF8.GetBytes("streamed content\n");
        await stream.WriteAsync(content, 0, content.Length, cancellationToken: TestContext.Current.CancellationToken);

        GitOid oid = await stream.CommitAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal("streamed content\n", Encoding.UTF8.GetString(blob.Content.Span));
    }

    [Fact]
    public async Task CreateWriteStream_NoHintPath_WritesBlobWithoutFilters()
    {
        using GitBlobWriteStream stream = _repo.BlobCreateWriteStream(hintPath: (string?)null);
        byte[] content = Encoding.UTF8.GetBytes("no filters\n");
        await stream.WriteAsync(content, 0, content.Length, cancellationToken: TestContext.Current.CancellationToken);

        GitOid oid = await stream.CommitAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal("no filters\n", Encoding.UTF8.GetString(blob.Content.Span));
    }

    [Fact]
    public async Task CreateWriteStream_ChunkedBinaryContent_PersistsOnlyWrittenBytes()
    {
        // Cross the initial capacity, leaving spare capacity after the second write.
        byte[] expected = new byte[257];
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)i;
        }

        GitOid oid;
        using (GitBlobWriteStream stream = _repo.BlobCreateWriteStream())
        {
            await stream.WriteAsync(expected.AsMemory(0, 128), TestContext.Current.CancellationToken);
            await stream.WriteAsync(expected.AsMemory(128), TestContext.Current.CancellationToken);
            oid = await stream.CommitAsync(TestContext.Current.CancellationToken);
        }

        using GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal(expected, blob.Content.ToArray());
    }

    [Fact]
    public async Task CreateWriteStream_HintPath_AppliesCleanFilterToWrittenBytes()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".gitattributes"), "*.txt text eol=lf\n", TestContext.Current.CancellationToken);
        byte[] input = "one\r\ntwo\r\n"u8.ToArray();
        GitOid oid;
        using (GitBlobWriteStream stream = _repo.BlobCreateWriteStream("filtered.txt"))
        {
            await stream.WriteAsync(input, TestContext.Current.CancellationToken);
            oid = await stream.CommitAsync(TestContext.Current.CancellationToken);
        }

        using GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal("one\ntwo\n"u8.ToArray(), blob.Content.ToArray());
        Assert.Equal("one\r\ntwo\r\n"u8.ToArray(), input);
    }

    [Fact]
    public async Task CreateWriteStream_WriteAfterCommit_Throws()
    {
        using GitBlobWriteStream stream = _repo.BlobCreateWriteStream();
        await stream.WriteAsync("x"u8.ToArray(), 0, 1, cancellationToken: TestContext.Current.CancellationToken);
        await stream.CommitAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() =>
            stream.Write("y"u8.ToArray(), 0, 1));
    }

    [Fact]
    public async Task CreateWriteStream_DoubleCommit_Throws()
    {
        using GitBlobWriteStream stream = _repo.BlobCreateWriteStream();
        await stream.WriteAsync("x"u8.ToArray(), 0, 1, cancellationToken: TestContext.Current.CancellationToken);
        await stream.CommitAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.CommitAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateWriteStream_EmptyContent_CommitsEmptyBlob()
    {
        using GitBlobWriteStream stream = _repo.BlobCreateWriteStream();
        GitOid oid = await stream.CommitAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitOid.EmptyBlobSha1, oid);
    }

    [Fact]
    public void CreateWriteStream_NullRepo_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GitBlob.CreateWriteStream(null!));
    }
}
