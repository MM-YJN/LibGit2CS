using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Iterator;

public sealed class IndexIteratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public IndexIteratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexIter_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task Advance_TestRepoIndex_YieldsEntries()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex index = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(index);

        using var iter = new IndexIterator(index, repo);

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        Assert.Equal(109, entries.Count);

        // Verify entries are sorted using the same comparison the index uses
        // (case-insensitive on Windows/macOS where core.ignorecase defaults
        // true, case-sensitive on Linux). Matches GitIndex.SortEntries.
        StringComparison cmp = index.IgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(
                string.Compare(entries[i - 1], entries[i], cmp) <= 0,
                $"Entries not sorted: '{entries[i - 1]}' > '{entries[i]}'");
        }
    }

    [Fact]
    public async Task Advance_IncludeTrees_YieldsPseudoTreeEntries()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex index = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(index);

        using var iter = new IndexIterator(index, repo, new IteratorOptions
        {
            Flags = IteratorFlags.IncludeTrees,
        });

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        // Should contain at least one directory entry (ending with '/').
        Assert.Contains(entries, e => e.EndsWith('/'));
    }

    [Fact]
    public async Task ForIndex_NullIndex_ReturnsEmptyIterator()
    {
        IIterator iter = IndexIterator.ForIndex(null, null);
        Assert.Null(await iter.AdvanceAsync(TestContext.Current.CancellationToken));
        iter.Dispose();
    }

    [Fact]
    public async Task Reset_RestartsIteration()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex index = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(index);

        using var iter = new IndexIterator(index, repo);

        GitIndexEntry? first = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        await iter.ResetAsync(TestContext.Current.CancellationToken);

        GitIndexEntry? firstAgain = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(firstAgain);

        Assert.Equal(first!.Value.Path, firstAgain!.Value.Path);
    }

    [Fact]
    public async Task Advance_RangeStart_FiltersEntries()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex index = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(index);

        using var iter = new IndexIterator(index, repo, new IteratorOptions
        {
            Start = GitPath.FromUtf8String("README"),
        });

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        Assert.NotEmpty(entries);
        foreach (string path in entries)
        {
            Assert.True(string.Compare(path, "README", StringComparison.Ordinal) >= 0);
        }
    }

    /// <summary>
    /// Regression test for the submodule start-suffix clause of
    /// <c>iterator_has_started</c> (iterator.c:200-202). A gitlink index entry
    /// <c>submod</c> must be matched by a start path of <c>submod/</c> (the form
    /// <c>git_pathspec_prefix</c> produces when the pathspec is a directory /
    /// submodule). Without the clause, the gitlink sorts before the start
    /// boundary and is skipped.
    /// </summary>
    [Fact]
    public async Task Advance_RangeStart_GitLinkEntry_MatchedByTrailingSlashStart()
    {
        // The submodule_simple fixture is a superproject whose index carries a
        // gitlink entry (a submodule, "testrepo") alongside regular files.
        string extracted = FixtureLoader.ExtractTreeToTemp("Fixtures.submodule.submodule_simple.zip");
        _extractedPaths.Add(extracted);
        await using GitRepository repo = await GitRepository.OpenAsync(
            Path.Combine(extracted, "submodule_simple"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex index = await repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(index);

        // Discover the gitlink entry path (a leaf index entry with GitLink mode).
        string? gitlinkPath = null;
        using (var discover = new IndexIterator(index, repo))
        {
            while (await discover.AdvanceAsync(TestContext.Current.CancellationToken) is { } e)
            {
                if (e.Mode == GitFileMode.GitLink)
                {
                    gitlinkPath = e.Path.ToUtf8String();
                    break;
                }
            }
        }

        Assert.NotNull(gitlinkPath);

        // Range collapsed to <gitlink>/ — the start-suffix-of-'/' form.
        using var iter = new IndexIterator(index, repo, new IteratorOptions
        {
            Start = GitPath.FromUtf8String(gitlinkPath + "/"),
            End = GitPath.FromUtf8String(gitlinkPath + "/"),
        });

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        // The gitlink is reached via the submodule clause; nothing else in the
        // index sorts into the [path/, path/] range.
        Assert.Equal([gitlinkPath], entries);
    }
}
