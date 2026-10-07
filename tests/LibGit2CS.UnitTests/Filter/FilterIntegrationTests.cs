using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Filter;

public sealed class FilterIntegrationTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly GitRepository _repo;

    public FilterIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_FilterInt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _repo = GitRepository.InitAsync(_tempDir, isBare: false, TestGitContext.CreateIsolatedFromHostConfig()).GetAwaiter().GetResult();
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
    public async Task AddByPath_WithTextAttr_AppliesCleanFilter()
    {
        // Write .gitattributes with text attr.
        string attrPath = Path.Combine(_repo.Workdir!, ".gitattributes");
        await File.WriteAllTextAsync(attrPath, "*.txt text\n", cancellationToken: TestContext.Current.CancellationToken);

        // Write a workdir file with CRLF.
        string filePath = Path.Combine(_repo.Workdir!, "test.txt");
        await File.WriteAllBytesAsync(filePath, "line1\r\nline2\r\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);

        // AddByPath should apply the clean filter (CRLF→LF).
        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await index.AddByPathAsync("test.txt", cancellationToken: TestContext.Current.CancellationToken);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The blob in the ODB should have LF, not CRLF.
        GitIndexEntry? entry = index.EntryByPath("test.txt");
        Assert.True(entry.HasValue);
        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(entry.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal("line1\nline2\n"u8.ToArray(), blob!.Content.ToArray());
    }

    [Fact]
    public async Task AddByPath_NoAttr_StoresRawContent()
    {
        string filePath = Path.Combine(_repo.Workdir!, "test.txt");
        await File.WriteAllBytesAsync(filePath, "line1\r\nline2\r\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await index.AddByPathAsync("test.txt", cancellationToken: TestContext.Current.CancellationToken);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitIndexEntry? entry = index.EntryByPath("test.txt");
        Assert.True(entry.HasValue);
        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(entry.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        // No .gitattributes → no filter → raw CRLF preserved.
        Assert.Equal("line1\r\nline2\r\n"u8.ToArray(), blob!.Content.ToArray());
    }

    [Fact]
    public async Task AddByPath_BinaryAttr_NoFilter()
    {
        string attrPath = Path.Combine(_repo.Workdir!, ".gitattributes");
        await File.WriteAllTextAsync(attrPath, "*.txt binary\n", cancellationToken: TestContext.Current.CancellationToken);

        string filePath = Path.Combine(_repo.Workdir!, "test.txt");
        await File.WriteAllBytesAsync(filePath, "line1\r\nline2\r\n"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await index.AddByPathAsync("test.txt", cancellationToken: TestContext.Current.CancellationToken);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitIndexEntry? entry = index.EntryByPath("test.txt");
        Assert.True(entry.HasValue);
        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(entry.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        // binary attr → no CRLF conversion.
        Assert.Equal("line1\r\nline2\r\n"u8.ToArray(), blob!.Content.ToArray());
    }
}
