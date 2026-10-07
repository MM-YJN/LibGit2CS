using LibGit2CS.Core;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

public sealed class RefLogTests : IDisposable
{
    private readonly List<string> _extractedPaths = [];

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task ReadLog_HEAD_HasEntries()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitRefLog? log = await repo.ReferenceReadLogAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(log);
        Assert.True(log!.EntryCount >= 2);
    }

    [Fact]
    public async Task ReadLog_EntryCount_MatchesEnumeration()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitRefLog? log = await repo.ReferenceReadLogAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(log);

        var enumerated = log!.ToList();
        Assert.Equal(log.EntryCount, enumerated.Count);
    }

    [Fact]
    public async Task ReadLog_Indexer_ZeroIsMostRecent()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitRefLog? log = await repo.ReferenceReadLogAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(log);

        // The most recent entry (index 0) should be the last line in the file.
        GitRefLogEntry entry = log![0];
        Assert.NotEqual(GitOid.Empty, entry.NewId);
    }

    [Fact]
    public async Task ReadLog_OldId_NewId_AreGitOid()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitRefLog? log = await repo.ReferenceReadLogAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(log);
        Assert.True(log!.EntryCount >= 2);

        // First entry should have zero OldId (branch creation).
        GitRefLogEntry oldest = log[-1];
        Assert.True(oldest.OldId.IsZero);
        Assert.False(oldest.NewId.IsZero);
    }

    [Fact]
    public async Task ReadLog_Committer_HasName()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitRefLog? log = await repo.ReferenceReadLogAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(log);

        GitRefLogEntry entry = log![0];
        Assert.Equal("Ben Straub", entry.Committer.Name);
    }

    [Fact]
    public async Task ReadLog_Message_HasContent()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitRefLog? log = await repo.ReferenceReadLogAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(log);

        // The oldest entry should have "clone: from ..." message.
        GitRefLogEntry oldest = log![-1];
        Assert.Contains("clone", oldest.Message);
    }

    [Fact]
    public async Task ReadLog_NoLogFile_CreatesEmptyReflog()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // C (refdb_fs.c:2149-2155): git_reflog_read creates the missing log
        // file and returns an empty reflog — a packed-only ref with no reflog
        // reads as an empty log, and the file now exists.
        GitRefLog reflog = (await repo.ReferenceReadLogAsync("refs/heads/packed", cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(0, reflog.EntryCount);
        Assert.True(await repo.Refs.HasLogAsync("refs/heads/packed", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadLog_EmptyRefLog_ForRefWithNoLog()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // HasLog should return false for refs without a log file.
        Assert.False(await repo.Refs.HasLogAsync("refs/heads/packed", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadLog_EnumeratesMostRecentFirst()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitRefLog? log = await repo.ReferenceReadLogAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(log);

        var entries = log!.ToList();
        Assert.NotEmpty(entries);

        // First enumerated entry = index 0 = most recent.
        Assert.Equal(log[0].NewId, entries[0].NewId);
    }

    private async ValueTask<GitRepository> OpenTestRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "testrepo.git"), new GitContext());
    }
}
