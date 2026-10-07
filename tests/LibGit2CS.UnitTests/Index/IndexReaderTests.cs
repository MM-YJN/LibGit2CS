using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Index;

public sealed class IndexReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public IndexReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private string ExtractRepo(string zipFileName)
    {
        string path = FixtureLoader.ExtractTreeToTemp($"Fixtures.repo.{zipFileName}");
        _extractedPaths.Add(path);
        return path;
    }

    [Fact]
    public async Task Open_TestRepoIndex_HasCorrectEntryCount()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(109, index.EntryCount);
    }

    [Fact]
    public async Task Open_TestRepoIndex_VersionIs2()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(2, index.Version);
    }

    [Fact]
    public async Task EntryByIndex_FirstEntry_HasCorrectPath()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        GitIndexEntry entry = index.EntryByIndex(0);
        Assert.Equal(".HEADER", entry.Path.ToUtf8String());
        Assert.Equal(GitFileMode.Regular, entry.Mode);
        Assert.Equal(0, entry.Stage);
        Assert.False(entry.IsConflict);
    }

    [Fact]
    public async Task EntryByPath_ExistingPath_ReturnsEntry()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        GitIndexEntry? entry = index.EntryByPath(".HEADER");
        Assert.NotNull(entry);
        Assert.Equal(".HEADER", entry!.Value.Path.ToUtf8String());
    }

    [Fact]
    public async Task EntryByPath_NonexistentPath_ReturnsNull()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Null(index.EntryByPath("nonexistent_file.txt"));
    }

    [Fact]
    public async Task HasConflicts_TestRepoIndex_ReturnsFalse()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.False(index.HasConflicts);
    }

    [Fact]
    public async Task Find_ExistingPath_ReturnsCorrectPosition()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        int pos = index.Find(".HEADER");
        Assert.True(pos >= 0);
        Assert.Equal(".HEADER", index.EntryByIndex(pos).Path.ToUtf8String());
    }

    [Fact]
    public async Task Find_NonexistentPath_ReturnsMinusOne()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(-1, index.Find("nonexistent_file.txt"));
    }

    [Fact]
    public async Task FindPrefix_MatchingPrefix_ReturnsPosition()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        int pos = index.FindPrefix(".HEA");
        Assert.True(pos >= 0);
        string entryPath = index.EntryByIndex(pos).Path.ToUtf8String();
        Assert.StartsWith(".HEA", entryPath);
    }

    [Fact]
    public async Task Open_GitgitIndex_HasCorrectEntryCount()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.gitgit_index");
        string tempPath = Path.Combine(_tempDir, "gitgit_index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(1437, index.EntryCount);
    }

    [Fact]
    public async Task Open_CorruptIndex_ThrowsGitException()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.bad_index");
        string tempPath = Path.Combine(_tempDir, "bad_index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () =>
            await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Snapshot_ReturnsAllEntriesInOrder()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        IReadOnlyList<GitIndexEntry> snapshot = index.Snapshot();

        Assert.Equal(109, snapshot.Count);

        // Verify entries are sorted.
        for (int i = 1; i < snapshot.Count; i++)
        {
            Assert.True(
                string.Compare(snapshot[i - 1].Path.ToUtf8String(), snapshot[i].Path.ToUtf8String(), StringComparison.Ordinal) <= 0,
                $"Entries not sorted: '{snapshot[i - 1].Path.ToUtf8String()}' > '{snapshot[i].Path.ToUtf8String()}' at index {i}");
        }
    }

    [Fact]
    public async Task Open_TestRepoIndex_HasReucExtension()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // The testrepo index has a REUC extension.
        Assert.True(index.ReucCount > 0);
    }

    [Fact]
    public async Task Repository_Index_LazilyLoadsIndex()
    {
        string repoPath = ExtractRepo("testrepo.zip");

        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(109, (await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).EntryCount);
    }

    [Fact]
    public async Task IgnoreCase_ToggledAfterOpen_RebuildsMap()
    {
        byte[] indexBytes = FixtureLoader.LoadBytes("Fixtures.index.testrepo_index");
        string tempPath = Path.Combine(_tempDir, "index");
        await File.WriteAllBytesAsync(tempPath, indexBytes, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(tempPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // Case-sensitive lookup.
        Assert.NotNull(index.EntryByPath(".HEADER"));

        // Toggle to case-insensitive.
        index.IgnoreCase = true;

        // Case-insensitive lookup should now work.
        Assert.NotNull(index.EntryByPath(".header"));
    }
}
