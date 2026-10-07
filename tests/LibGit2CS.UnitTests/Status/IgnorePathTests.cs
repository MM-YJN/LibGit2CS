using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// <c>.gitignore</c> path evaluation tests. Ported from
/// <c>tests/libgit2/ignore/path.c</c> — the core gitignore pattern-matching
/// suite. Each test rewrites <c>attr/.gitignore</c> and checks
/// <see cref="GitRepository.IsIgnoredAsync(string, CancellationToken)"/> for various paths.
/// </summary>
/// <remarks>
/// Uses the <c>attr</c> fixture (already packaged at
/// <c>Fixtures/diff/attr.zip</c>). The fixture has an existing
/// <c>.gitignore</c> with <c>ign</c> and <c>dir/</c> rules — each test
/// rewrites it.
/// </remarks>
public class IgnorePathTests : StatusGoldenBase, IAsyncDisposable
{
    private GitRepository? _repo;
    private string _gitignorePath = null!;

    public IgnorePathTests()
    {
        // Defer repo open to first use (async). Set up the gitignore path lazily.
    }

    private async Task<GitRepository> GetRepoAsync()
    {
        if (_repo is null)
        {
            _repo = await OpenFixtureRepoAsync("attr");
            _gitignorePath = Path.Combine(_repo.Workdir!, ".gitignore");
        }

        return _repo;
    }

    public async ValueTask DisposeAsync()
    {
        if (_repo is not null)
        {
            await _repo.DisposeAsync();
        }
    }

    private void RewriteGitignore(string content)
        => File.WriteAllText(_gitignorePath, content);

    private async Task AssertIgnoredAsync(bool expected, string filepath)
    {
        GitRepository repo = await GetRepoAsync();
        bool actual = await repo.IsIgnoredAsync(filepath);
        Assert.True(expected == actual,
            $"IsIgnored(\"{filepath}\"): expected {expected}, got {actual}");
    }

    [Fact]
    public async Task HonorTemporaryRules()
    {
        await GetRepoAsync();
        RewriteGitignore("/NewFolder\n/NewFolder/NewFolder");
        await AssertIgnoredAsync(false, "File.txt");
        await AssertIgnoredAsync(true, "NewFolder");
        await AssertIgnoredAsync(true, "NewFolder/NewFolder");
        await AssertIgnoredAsync(true, "NewFolder/NewFolder/File.txt");
    }

    [Fact]
    public async Task AllowRoot()
    {
        await GetRepoAsync();
        RewriteGitignore("/");
        await AssertIgnoredAsync(false, "File.txt");
        await AssertIgnoredAsync(false, "NewFolder");
        await AssertIgnoredAsync(false, "NewFolder/NewFolder");
    }

    [Fact]
    public async Task IgnoreSpace()
    {
        await GetRepoAsync();
        RewriteGitignore("/\n\n/NewFolder \n/NewFolder/NewFolder");
        await AssertIgnoredAsync(false, "File.txt");
        await AssertIgnoredAsync(true, "NewFolder");
        await AssertIgnoredAsync(true, "NewFolder/NewFolder");
    }

    [Fact]
    public async Task IntermittentSpace()
    {
        await GetRepoAsync();
        RewriteGitignore("foo bar\n");
        await AssertIgnoredAsync(false, "foo");
        await AssertIgnoredAsync(false, "bar");
        await AssertIgnoredAsync(true, "foo bar");
    }

    [Fact]
    public async Task TrailingSpace()
    {
        await GetRepoAsync();
        RewriteGitignore("foo \nbar  \n");
        await AssertIgnoredAsync(true, "foo");
        await AssertIgnoredAsync(false, "foo ");
        await AssertIgnoredAsync(true, "bar");
        await AssertIgnoredAsync(false, "bar ");
    }

    [Fact]
    public async Task EscapedTrailingSpaces()
    {
        await GetRepoAsync();
        RewriteGitignore("foo\\ \nbar\\ \\ \nbaz \\ \nqux\\  \n");
        await AssertIgnoredAsync(false, "foo");
        await AssertIgnoredAsync(true, "foo ");
        await AssertIgnoredAsync(false, "bar");
        await AssertIgnoredAsync(false, "bar ");
        await AssertIgnoredAsync(true, "bar  ");
        await AssertIgnoredAsync(true, "baz  ");
        await AssertIgnoredAsync(false, "baz ");
        await AssertIgnoredAsync(true, "qux ");
        await AssertIgnoredAsync(false, "qux");
    }

    [Fact]
    public async Task IgnoreDir()
    {
        await GetRepoAsync();
        RewriteGitignore("dir/\n");
        await AssertIgnoredAsync(true, "dir");
        await AssertIgnoredAsync(true, "dir/file");
    }

    [Fact]
    public async Task IgnoreDirWithTrailingSpace()
    {
        await GetRepoAsync();
        RewriteGitignore("dir/ \n");
        await AssertIgnoredAsync(true, "dir");
        await AssertIgnoredAsync(true, "dir/file");
    }

    [Fact]
    public async Task FullPaths()
    {
        await GetRepoAsync();
        // Test full-path patterns with * and **.
        RewriteGitignore("Folder/*/Contained\nFolder/**/Contained\n");
        await AssertIgnoredAsync(true, "Folder/sub/Contained");
        await AssertIgnoredAsync(true, "Folder/sub/sub2/Contained");
    }

    [Fact]
    public async Task LeadingStars()
    {
        await GetRepoAsync();
        RewriteGitignore("*/onestar\n**/twostars\n");
        await AssertIgnoredAsync(true, "dir/onestar");
        await AssertIgnoredAsync(true, "dir/sub/twostars");
    }

    [Fact]
    public async Task SubdirectoryGitignore()
    {
        GitRepository repo = await GetRepoAsync();
        // Root .gitignore ignores file1; subdir .gitignore ignores file2/.
        RewriteGitignore("file1\n");
        string subDir = Path.Combine(repo.Workdir!, "dir");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, ".gitignore"), "file2/\n", cancellationToken: TestContext.Current.CancellationToken);

        await AssertIgnoredAsync(true, "file1");
        await AssertIgnoredAsync(true, "dir/file1");
        // file2/ is a directory-only pattern — files inside file2/ are ignored.
        await AssertIgnoredAsync(true, "dir/file2/actual_file");
        await AssertIgnoredAsync(false, "dir/file3");
    }

    [Fact]
    public async Task DontIgnoreFilesForFolder()
    {
        await GetRepoAsync();
        // test/ pattern should ignore directory but not a file named "test".
        RewriteGitignore("test/\n");
        await AssertIgnoredAsync(true, "test/file");
        // A file named "test" should NOT be ignored by test/ (dir-only).
        // But this depends on whether "test" exists as a file or dir.
        // The ignore engine treats the path as a dir if it ends with /.
        // For a file path "test", the directory-only rule doesn't match.
        await AssertIgnoredAsync(false, "test");
    }

    [Fact]
    public async Task Test_Negation()
    {
        await GetRepoAsync();
        // /*/ + !/src → ignore all top-level dirs except src.
        RewriteGitignore("/*/\n!/src\n");
        await AssertIgnoredAsync(true, "dist/");
        await AssertIgnoredAsync(false, "src");
        await AssertIgnoredAsync(false, "src/file");
    }

    [Fact]
    public async Task UnignoreDirSucceeds()
    {
        await GetRepoAsync();
        RewriteGitignore("*.c\n!src/*.c\n");
        await AssertIgnoredAsync(true, "foo.c");
        await AssertIgnoredAsync(false, "src/foo.c");
    }

    [Fact]
    public async Task IgnoredSubdirfilesWithNegations()
    {
        await GetRepoAsync();
        RewriteGitignore("dir/*\n!dir/a.test\n");
        await AssertIgnoredAsync(true, "dir/b.test");
        await AssertIgnoredAsync(false, "dir/a.test");
    }

    [Fact]
    public async Task NegativeDirectoryRulesOnlyMatchDirectories()
    {
        await GetRepoAsync();
        // * then !/**/ then !*.keep — dirs unignored, files not.
        RewriteGitignore("*\n!/**/\n!*.keep\n!.gitignore\n");
        await AssertIgnoredAsync(false, "subdir/");
        await AssertIgnoredAsync(true, "file.txt");
        await AssertIgnoredAsync(false, "file.keep");
    }

    [Fact]
    public async Task EscapedCharacter()
    {
        await GetRepoAsync();
        RewriteGitignore("\\c\n");
        await AssertIgnoredAsync(true, "c");
        await AssertIgnoredAsync(false, "\\c");
    }

    [Fact]
    public async Task EscapedGlob()
    {
        await GetRepoAsync();
        RewriteGitignore("\\*\n");
        await AssertIgnoredAsync(true, "*");
        await AssertIgnoredAsync(false, "foo");
    }

    [Fact]
    public async Task EscapedComments()
    {
        await GetRepoAsync();
        RewriteGitignore("\\#foo\n");
        await AssertIgnoredAsync(true, "#foo");
        await AssertIgnoredAsync(false, "foo");
    }

    [Fact]
    public async Task InvalidPattern()
    {
        await GetRepoAsync();
        // A lone '[' is an incomplete bracket expression; wildmatch aborts
        // (WM_ABORT_ALL) so no path matches the rule — nothing is ignored.
        // Matches clar ignore/path.c::invalid_pattern.
        RewriteGitignore("[\n");
        await AssertIgnoredAsync(false, "[f");
        await AssertIgnoredAsync(false, "f");
    }

    [Fact]
    public async Task NegativePrefixRule()
    {
        await GetRepoAsync();
        RewriteGitignore("ff*\n!f\n");
        await AssertIgnoredAsync(false, "f");
        await AssertIgnoredAsync(true, "ff");
        await AssertIgnoredAsync(true, "fff");
    }

    [Fact]
    public async Task AutomaticallyIgnoreBadFiles()
    {
        await GetRepoAsync();
        // ., .., .git are always ignored.
        await AssertIgnoredAsync(true, ".");
        await AssertIgnoredAsync(true, "..");
        await AssertIgnoredAsync(true, ".git");
    }

    [Fact]
    public async Task AddingInternalIgnores()
    {
        GitRepository repo = await GetRepoAsync();
        await repo.IgnoreClearInternalRulesAsync(cancellationToken: TestContext.Current.CancellationToken);
        await repo.IgnoreAddRuleAsync("*.tmp\n*.log\n", cancellationToken: TestContext.Current.CancellationToken);
        await AssertIgnoredAsync(true, "foo.tmp");
        await AssertIgnoredAsync(true, "bar.log");
        await AssertIgnoredAsync(false, "foo.txt");
    }

    [Fact]
    public async Task FilenamesWithSpecialPrefixesDoNotInterfere()
    {
        await GetRepoAsync();
        // !file, #blah, [blah] should NOT be treated as ignore/comment/attr
        // patterns by the status walk — they are literal filenames.
        // Here we just verify the ignore engine doesn't crash on them.
        RewriteGitignore("*.txt\n");
        await AssertIgnoredAsync(true, "foo.txt");
        await AssertIgnoredAsync(false, "!file");
        await AssertIgnoredAsync(false, "#blah");
    }
}
