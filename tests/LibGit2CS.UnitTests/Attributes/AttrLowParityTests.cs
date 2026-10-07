using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Attributes;

// Parity tests for GIT_ATTR_MAX_FILE_SIZE, the attr path DirFlag for filter-list lookups, and the fact that Unicode whitespace is not a token
// separator (git__isspace is ASCII-only).
public sealed class AttrLowParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _cleanupDirs = [];

    public AttrLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_AttrLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string dir in _cleanupDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private string NewRepoDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _cleanupDirs.Add(dir);
        return dir;
    }

    private static GitContext NewContext()
    {
        GitContext ctx = new();
        ctx.Env["HOME"] = Path.Combine(Path.GetTempPath(), "LibGit2CS_nonexistent_" + Guid.NewGuid().ToString("N"));
        ctx.Env["XDG_CONFIG_HOME"] = null;
        ctx.Dirs.Reset();
        ctx.Dirs.Set(GitSystemDir.System, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        return ctx;
    }

    // ── GIT_ATTR_MAX_FILE_SIZE (100 MB) ────────────────────────────────

    [Fact]
    public async Task OversizedAttributesFile_ContributesNoRules()
    {
        // C (attr_file.c:159-161): files larger than GIT_ATTR_MAX_FILE_SIZE
        // are treated as nonexistent — no rules are contributed.
        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);

        string attrsPath = Path.Combine(repo.Workdir!, ".gitattributes");
        await File.WriteAllTextAsync(attrsPath, "*.txt text\n", cancellationToken: TestContext.Current.CancellationToken);
        // Grow the file beyond the cap (sparse — no real 100 MB allocation).
        using (FileStream fs = new(attrsPath, FileMode.Append, FileAccess.Write))
        {
            fs.SetLength(100L * 1024 * 1024 + 1);
        }

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        GitAttrValue value = await cache.LookupOneAsync(PathFor("file.txt"), "text", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitAttrValueKind.None, value.Kind);
    }

    [Fact]
    public async Task OversizedStagedAttributesFile_ContributesNoRules()
    {
        // C (attr_file.c:146-148): the INDEX source skips blobs larger than
        // GIT_ATTR_MAX_FILE_SIZE (no file is created).
        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);

        string attrsPath = Path.Combine(repo.Workdir!, ".gitattributes");
        await File.WriteAllTextAsync(attrsPath, "*.txt text\n", cancellationToken: TestContext.Current.CancellationToken);
        using (FileStream fs = new(attrsPath, FileMode.Append, FileAccess.Write))
        {
            fs.SetLength(100L * 1024 * 1024 + 1);
        }

        GitIndex? index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index!.AddByPathAsync(".gitattributes", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);

        // Force the cache to re-collect from the index by looking up with
        // INDEX_ONLY — the oversized staged blob must contribute no rules.
        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        GitAttrValue value = await cache.LookupOneAsync(
            PathFor("file.txt"),
            "text",
            GitAttrCheckFlags.IndexOnly,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitAttrValueKind.None, value.Kind);
    }

    // ── attr path DirFlag for filter-list lookups ─────────────────────

    [Fact]
    public async Task DirectoryPattern_MatchesDirectoryPath_InDiffDriverLookup()
    {
        // C (attr.c:69-70): dir_flag is GIT_DIR_FLAG_UNKNOWN for non-bare
        // repos, so git_attr_path__init stats the workdir path
        // (attr_file.c:599-613) — a `dir/` DIRECTORY pattern matches a
        // directory path.False, so the
        // pattern never matched.
        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(repo.Workdir!, "dir"));
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "dir/ -diff\n", cancellationToken: TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        var registry = new DiffDriverRegistry(repo, ignoreCase: false, cache);
        DiffDriver driver = await registry.LookupAsync("dir", TestContext.Current.CancellationToken);

        Assert.Equal(DiffDriverType.Binary, driver.Type);
    }

    [Fact]
    public async Task DirectoryPattern_DoesNotMatchFilePath_InDiffDriverLookup()
    {
        // Same pattern against a regular FILE named "dir" must NOT match
        // (is_dir is false).
        await using GitRepository repo = await GitRepository.InitAsync(NewRepoDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "dir"), "content\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitattributes"), "dir/ -diff\n", cancellationToken: TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        var registry = new DiffDriverRegistry(repo, ignoreCase: false, cache);
        DiffDriver driver = await registry.LookupAsync("dir", TestContext.Current.CancellationToken);

        Assert.Equal(DiffDriverType.Auto, driver.Type);
    }

    // ── Unicode whitespace is not a token separator ───────────────────

    [Fact]
    public void ParseAssignments_UnicodeWhitespace_StaysInsideName()
    {
        // C (attr_file.c:904): the assignment scan uses git__isspace — ASCII
        // only. NBSP (U+00A0) is NOT whitespace, so "foo\u00A0bar" is ONE
        // attribute name; char.IsWhiteSpace would split it into two tokens.
        var assigns = new Dictionary<string, AttrAssignment>();
        AttributesFile.ParseAssignments("foo\u00A0bar text".AsSpan(), macros: null, assigns);

        Assert.Equal(2, assigns.Count);
        Assert.True(assigns.ContainsKey("foo\u00A0bar"));
        Assert.True(assigns.ContainsKey("text"));
    }

    private static AttrPath PathFor(string path)
    {
        var ap = new AttrPath();
        ap.Init(path, string.Empty, AttrPath.DirFlag.False);
        return ap;
    }
}
