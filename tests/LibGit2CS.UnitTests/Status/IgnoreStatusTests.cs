using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Combined ignore + status tests. Ported from
/// <c>tests/libgit2/ignore/status.c</c> — tests that verify the interaction
/// of <c>.gitignore</c> rules with the status walk. Uses the
/// <c>empty_standard</c> fixture (freshly-init'd repo with no commits).
/// </summary>
public class IgnoreStatusTests : StatusGoldenBase, IAsyncDisposable
{
    private GitRepository? _repo;
    private string _workdir = null!;
    private string _gitignorePath = null!;

    private async Task<GitRepository> OpenEmptyRepoAsync()
    {
        GitRepository repo = await OpenRepoFixtureAsync("empty_standard");
        _repo = repo;
        _workdir = repo.Workdir!;
        _gitignorePath = Path.Combine(_workdir, ".gitignore");
        return repo;
    }

    public async ValueTask DisposeAsync()
    {
        if (_repo is not null)
        {
            await _repo.DisposeAsync();
        }
    }

    private void WriteGitignore(string content)
        => File.WriteAllText(_gitignorePath, content);

    private void CreateFile(string relativePath, string content = "")
    {
        string fullPath = Path.Combine(_workdir, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public async Task EmptyRepoWithGitignoreRewrite()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();

        // Create a file — it should be WT_NEW.
        CreateFile("test.txt", "content");

        // Before .gitignore: test.txt is WT_NEW.
        using GitStatusList list1 = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitStatusEntry entry1 = Assert.Single(list1.Entries);
        Assert.Equal("test.txt", entry1.Path.ToUtf8String());
        Assert.Equal(GitStatusFlags.WorkdirNew, entry1.Status);

        // Add .gitignore that doesn't match — test.txt still WT_NEW,
        // .gitignore is also WT_NEW (untracked).
        WriteGitignore("*.log\n");
        using GitStatusList list2 = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, list2.EntryCount);
        GitStatusEntry testEntry = list2.Entries.Single(e => e.Path.ToUtf8String() == "test.txt");
        Assert.Equal(GitStatusFlags.WorkdirNew, testEntry.Status);

        // Rewrite .gitignore to match — test.txt now IGNORED.
        WriteGitignore("test.txt\n");
        using GitStatusList list3 = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitStatusEntry ignoredEntry = list3.Entries.Single(e => e.Path.ToUtf8String() == "test.txt");
        Assert.Equal(GitStatusFlags.Ignored, ignoredEntry.Status);
    }

    [Fact]
    public async Task IgnorepatternContainsSpace()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("foo bar.txt", "content");
        WriteGitignore("foo bar.txt\n");

        using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        // foo bar.txt is ignored; .gitignore is untracked (WT_NEW).
        GitStatusEntry fooEntry = list.Entries.Single(e => e.Path.ToUtf8String() == "foo bar.txt");
        Assert.Equal(GitStatusFlags.Ignored, fooEntry.Status);
    }

    [Fact]
    public async Task AutomaticallyIgnoreBadFiles()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("test.txt", "content");

        // ., .., .git are always ignored.
        Assert.True(await repo.IsIgnoredAsync(".", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await repo.IsIgnoredAsync("..", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await repo.IsIgnoredAsync(".git", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await repo.IsIgnoredAsync(".git/HEAD", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddingInternalIgnores()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("foo.tmp", "content");
        CreateFile("bar.log", "content");
        CreateFile("baz.txt", "content");

        await repo.IgnoreClearInternalRulesAsync(cancellationToken: TestContext.Current.CancellationToken);
        await repo.IgnoreAddRuleAsync("*.tmp\n*.log\n", cancellationToken: TestContext.Current.CancellationToken);

        // The internal rules should ignore .tmp and .log files.
        Assert.True(await repo.IsIgnoredAsync("foo.tmp", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await repo.IsIgnoredAsync("bar.log", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("baz.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FilenamesWithSpecialPrefixesDoNotInterfere()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("!file", "content");
        CreateFile("#blah", "content");
        CreateFile("[blah]", "content");

        // These files should appear as WT_NEW, not be treated as ignore patterns.
        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
        }, cancellationToken: TestContext.Current.CancellationToken);

        var paths = list.Entries.Select(e => e.Path.ToUtf8String()).ToHashSet();
        Assert.Contains("!file", paths);
        Assert.Contains("#blah", paths);
        Assert.Contains("[blah]", paths);
    }

    [Fact]
    public async Task Subdirectories()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("ignore_me", "content");
        CreateFile("test/ignore_me/file1", "content");
        CreateFile("test/regular.txt", "content");

        WriteGitignore("ignore_me\n");

        // ignore_me at root is ignored. Files inside test/ignore_me/ are also
        // ignored (the basename pattern matches at any depth).
        Assert.True(await repo.IsIgnoredAsync("ignore_me", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await repo.IsIgnoredAsync("test/ignore_me/file1", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("test/regular.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LeadingSlashIgnores()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("a.test", "content");
        CreateFile("sub/b.test", "content");

        WriteGitignore("/a.test\n");

        // Leading / anchors to root — only root-level a.test is ignored.
        Assert.True(await repo.IsIgnoredAsync("a.test", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("sub/a.test", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MultipleLeadingSlash()
    {
        // C (attr_file.c:772-777): `if (slash_count == 1 && pattern == scan)
        // pattern++;` fires ONCE — exactly one leading '/' is consumed.
        // "//c.test" keeps "/c.test" (FULLPATH) which never matches a
        // workdir-relative path, and "///d.test" keeps "//d.test" — both are
        // ignored by NOBODY. (C-verified: git_ignore_path_is_ignored returns
        // 0 for "c.test"/"d.test" with these rules.)
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("a.test", "content");
        CreateFile("b.test", "content");
        CreateFile("c.test", "content");
        CreateFile("d.test", "content");

        WriteGitignore("a.test\n/b.test\n//c.test\n///d.test\n");

        Assert.True(await repo.IsIgnoredAsync("a.test", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await repo.IsIgnoredAsync("b.test", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("c.test", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("d.test", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NegativeIgnoresInSlashStar()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("bin/foo.txt", "content");
        CreateFile("bin/what-about-me.txt", "content");

        WriteGitignore("bin/*\n!bin/w*\n");

        // bin/* ignores everything in bin/, but !bin/w* unignores w* files.
        Assert.True(await repo.IsIgnoredAsync("bin/foo.txt", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("bin/what-about-me.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnignoreEntryInIgnoredDir()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        WriteGitignore("bar.txt\n!parent/child/bar.txt\n");

        // bar.txt is ignored globally, but parent/child/bar.txt is unignored.
        Assert.True(await repo.IsIgnoredAsync("bar.txt", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("parent/child/bar.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DoNotUnignoreBasenamePrefix()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        WriteGitignore("foo_bar.txt\n!bar.txt\n");

        // !bar.txt does NOT unignore foo_bar.txt (no prefix match).
        Assert.True(await repo.IsIgnoredAsync("foo_bar.txt", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("bar.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubdirDoesntMatchAbove()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        string subDir = Path.Combine(_workdir, "src");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, ".gitignore"), "src\n", cancellationToken: TestContext.Current.CancellationToken);

        // src/.gitignore with "src" only ignores src/src/..., NOT src/ itself.
        Assert.True(await repo.IsIgnoredAsync("src/src/file", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("src/file", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NegateExactPrevious()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        WriteGitignore("tags\n!tags/\n.buildpath\n");

        // tags (file) is ignored, but tags/ (dir) is unignored.
        Assert.True(await repo.IsIgnoredAsync("tags", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("tags/file", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await repo.IsIgnoredAsync(".buildpath", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NegateStarStar()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        WriteGitignore("code/projects/**/packages/*\n!code/projects/**/packages/repositories.config\n");

        Assert.True(await repo.IsIgnoredAsync("code/projects/foo/packages/bar", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("code/projects/foo/packages/repositories.config", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IgnoreAllToplevelDirsIncludeFiles()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("README.md", "content");
        Directory.CreateDirectory(Path.Combine(_workdir, "src"));
        Directory.CreateDirectory(Path.Combine(_workdir, "dist"));

        WriteGitignore("/*/\n!/src\n");

        // /*/ ignores all top-level dirs except src.
        Assert.True(await repo.IsIgnoredAsync("dist/", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("src/", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.IsIgnoredAsync("README.md", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SkipsBom()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        CreateFile("test.txt", "content");

        // .gitignore with UTF-8 BOM — BOM should be skipped.
        string bom = "\uFEFF";
        await File.WriteAllTextAsync(_gitignorePath, bom + "test.txt\n", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await repo.IsIgnoredAsync("test.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NestedGitignoreAtDepthTwoIsHonoredByStatusWalk()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();

        // Unrelated root .gitignore (ensures the root stack entry exists but
        // does not match *.tmp — the nested rule must do the work).
        WriteGitignore("*.user\n");

        // Depth-2 nested .gitignore with a basename rule not covered by any parent.
        CreateFile("nested/deep/.gitignore", "*.tmp\n");
        CreateFile("nested/deep/foo.tmp", "x");
        CreateFile("nested/deep/keep.txt", "y");

        using GitStatusList list = await repo.StatusNewAsync(new GitStatusOptions
        {
            Flags = GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.IncludeIgnored
                  | GitStatusFlags.RecurseUntrackedDirs,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Walk-up path agrees with git.
        Assert.True(await repo.IsIgnoredAsync("nested/deep/foo.tmp", cancellationToken: TestContext.Current.CancellationToken));

        // Status walk must agree: foo.tmp is ignored by nested/deep/.gitignore.
        GitStatusEntry foo = list.Entries.Single(e => e.Path.ToUtf8String() == "nested/deep/foo.tmp");
        Assert.Equal(GitStatusFlags.Ignored, foo.Status);

        // keep.txt is not ignored — must surface as WorkdirNew.
        GitStatusEntry keep = list.Entries.Single(e => e.Path.ToUtf8String() == "nested/deep/keep.txt");
        Assert.Equal(GitStatusFlags.WorkdirNew, keep.Status);
    }
}
