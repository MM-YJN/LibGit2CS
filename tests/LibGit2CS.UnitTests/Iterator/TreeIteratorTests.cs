using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Iterator;

public sealed class TreeIteratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public TreeIteratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_TreeIter_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static async Task<GitTree> GetHeadTreeAsync(GitRepository repo)
    {
        GitObject headObj = await repo.RevparseSingleAsync("HEAD")
            ?? throw new InvalidOperationException("HEAD not found");
        Commit headCommit = headObj as Commit
            ?? throw new InvalidOperationException("HEAD is not a commit");
        return (await repo.ObjectLookupAsync<GitTree>(headCommit.Tree, TestContext.Current.CancellationToken))
            ?? throw new InvalidOperationException("HEAD tree not found");
    }

    [Fact]
    public async Task Advance_EmptyTree_ReturnsNull()
    {
        using var iter = new TreeIterator(null, null);
        Assert.Null(await iter.AdvanceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Advance_TestRepoTree_YieldsEntries()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = await GetHeadTreeAsync(repo);

        using var iter = new TreeIterator(tree, repo);

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        Assert.NotEmpty(entries);
        // The testrepo HEAD tree contains these files.
        Assert.Contains("README", entries);
        Assert.Contains("branch_file.txt", entries);
        Assert.Contains("new.txt", entries);

        // Entries should be sorted.
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(
                string.Compare(entries[i - 1], entries[i], StringComparison.Ordinal) <= 0,
                $"Entries not sorted: '{entries[i - 1]}' > '{entries[i]}'");
        }
    }

    [Fact]
    public async Task Advance_IncludeTreesFlag_NoDirectoriesInFlatTree()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = await GetHeadTreeAsync(repo);

        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            Flags = IteratorFlags.IncludeTrees,
        });

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        // The testrepo HEAD tree is flat (no subdirectories), so there
        // should be no directory entries (paths ending with '/').
        Assert.DoesNotContain(entries, e => e.EndsWith('/'));
    }

    [Fact]
    public async Task Advance_DontAutoexpand_FlatTreeReturnsAllFiles()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = await GetHeadTreeAsync(repo);

        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            Flags = IteratorFlags.IncludeTrees | IteratorFlags.DontAutoexpand,
        });

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        // In a flat tree, DONT_AUTOEXPAND should still return all files.
        Assert.Contains("README", entries);
        Assert.Contains("branch_file.txt", entries);
        Assert.Contains("new.txt", entries);
    }

    [Fact]
    public async Task AdvanceInto_OnFileEntry_IsNoOp()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = await GetHeadTreeAsync(repo);

        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            Flags = IteratorFlags.IncludeTrees | IteratorFlags.DontAutoexpand,
        });

        // Advance to the first entry (the testrepo HEAD tree is flat, so the
        // first entry is a file).
        GitIndexEntry? first = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.NotEqual(GitFileMode.Tree, first!.Value.Mode);

        // C (iterator.c:871-873): advance_into on a non-tree entry returns 0
        // with *out = NULL and leaves the current entry unchanged.
        GitIndexEntry? into = await iter.AdvanceIntoAsync(TestContext.Current.CancellationToken);
        Assert.Null(into);
        GitIndexEntry? cur = await iter.CurrentAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.Value.Path.ToUtf8String(), cur!.Value.Path.ToUtf8String());
    }

    [Fact]
    public async Task AdvanceOver_OnFileEntry_AdvancesToNext()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = await GetHeadTreeAsync(repo);

        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            Flags = IteratorFlags.IncludeTrees | IteratorFlags.DontAutoexpand,
        });

        // Advance to the first entry.
        GitIndexEntry? first = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        // AdvanceOver on a file entry should just advance.
        (GitIndexEntry? next, IteratorStatus status) = await iter.AdvanceOverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(IteratorStatus.Normal, status);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Reset_RestartsIteration()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = await GetHeadTreeAsync(repo);

        using var iter = new TreeIterator(tree, repo);

        GitIndexEntry? first = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        await iter.ResetAsync(TestContext.Current.CancellationToken);

        GitIndexEntry? firstAgain = await iter.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(firstAgain);

        Assert.Equal(first!.Value.Path, firstAgain!.Value.Path);
    }

    [Fact]
    public async Task ForTree_NullTree_ReturnsEmptyIterator()
    {
        IIterator iter = TreeIterator.ForTree(null, null);
        Assert.Null(await iter.AdvanceAsync(TestContext.Current.CancellationToken));
        iter.Dispose();
    }

    [Fact]
    public async Task Advance_RangeStart_FiltersEntries()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = await GetHeadTreeAsync(repo);

        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            Start = GitPath.FromUtf8String("R"),
        });

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        Assert.NotEmpty(entries);
        // All entries should be >= "R".
        foreach (string path in entries)
        {
            Assert.True(string.Compare(path, "R", StringComparison.Ordinal) >= 0,
                $"Entry '{path}' is before range start 'R'");
        }
    }

    [Fact]
    public async Task Advance_RangeEnd_FiltersEntries()
    {
        string repoPath = ExtractRepo("testrepo.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(repoPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = await GetHeadTreeAsync(repo);

        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            End = GitPath.FromUtf8String("n"),
        });

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        // With git__prefixcmp semantics, End = "n" includes entries that start
        // with "n" (e.g. "new.txt") and entries that sort before "n". The walk
        // ends at the first entry that sorts after "n" AND does not start with
        // "n". So all entries should either start with "n" or sort before "n".
        foreach (string path in entries)
        {
            Assert.True(path.StartsWith('n') ||
                string.Compare(path, "n", StringComparison.Ordinal) < 0,
                $"Entry '{path}' is after range end 'n' and does not start with 'n'");
        }
    }

    /// <summary>
    /// Regression test for the directory-recurse clause of
    /// <c>iterator_has_started</c> (iterator.c:208-210). When a pathspec
    /// collapses the iterator range to <c>Start == End == &lt;full nested file
    /// path&gt;</c>, the tree iterator must still descend into every ancestor
    /// directory (whose path sorts before the start boundary) to reach the
    /// file. Without the clause, the tree side yields nothing and a staged
    /// modification is misreported as <c>Added</c>.
    /// </summary>
    [Fact]
    public async Task Advance_RangeStart_NestedFilePath_DescendsIntoAncestors()
    {
        // Build a repo whose HEAD tree contains a nested file plus siblings
        // (top-level and same-directory) that the range must filter out.
        string repoPath = Path.Combine(_tempDir, "nested_range.git");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "top_level.txt"), "t\n", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workdir, "dir"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "dir", "sibling.txt"), "s\n", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workdir, "dir", "sub"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "dir", "sub", "file.txt"), "f\n", TestContext.Current.CancellationToken);

        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("top_level.txt", TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("dir/sibling.txt", TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("dir/sub/file.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = new GitSignature("t", "t@t", new GitTime(1700000000, 0)),
            Committer = new GitSignature("t", "t@t", new GitTime(1700000000, 0)),
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        GitTree tree = await GetHeadTreeAsync(repo);

        // Range collapsed to the full nested file path (what
        // diff_prepare_iterator_opts + git_pathspec_prefix produce for a
        // single literal pathspec).
        using var iter = new TreeIterator(tree, repo, new IteratorOptions
        {
            Start = GitPath.FromUtf8String("dir/sub/file.txt"),
            End = GitPath.FromUtf8String("dir/sub/file.txt"),
        });

        var entries = new List<string>();
        while (await iter.AdvanceAsync(TestContext.Current.CancellationToken) is { } entry)
        {
            entries.Add(entry.Path.ToUtf8String());
        }

        // The nested file is reached by recursing through dir/ and dir/sub/.
        Assert.Equal(["dir/sub/file.txt"], entries);
    }
}
