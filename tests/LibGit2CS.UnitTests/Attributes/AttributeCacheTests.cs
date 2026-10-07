using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Attributes;

public sealed class AttributeCacheTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly GitRepository _repo;

    public AttributeCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_AttrCache_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _repo = GitRepository.InitAsync(_tempDir, isBare: false, new GitContext()).GetAwaiter().GetResult();
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
    public async Task AttributeCache_HasBinaryMacro()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AttributeCache cache = await _repo.GetAttributeCacheAsync(cancellationToken);
        AttrMacroRegistry macros = cache.Macros;
        IReadOnlyDictionary<string, AttrAssignment>? binary = macros.Lookup("binary");
        Assert.NotNull(binary);
        // binary macro = -diff -merge -text -crlf
        Assert.True(binary!.ContainsKey("diff"));
        Assert.True(binary["diff"].Value.IsFalse);
    }

    [Fact]
    public async Task AddMacro_RegisteredAndLookupable()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AttributeCache cache = await _repo.GetAttributeCacheAsync(cancellationToken);
        cache.AddMacro("custom", "text -diff");
        IReadOnlyDictionary<string, AttrAssignment>? macro = cache.Macros.Lookup("custom");
        Assert.NotNull(macro);
        Assert.True(macro!.ContainsKey("text"));
        Assert.True(macro["text"].Value.IsTrue);
        Assert.True(macro["diff"].Value.IsFalse);
    }

    [Fact]
    public async Task LookupOne_NoAttrFile_ReturnsNone()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AttributeCache cache = await _repo.GetAttributeCacheAsync(cancellationToken);
        var path = new AttrPath();
        path.Init("file.txt", string.Empty, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(path, "text", cancellationToken: cancellationToken);
        Assert.Equal(GitAttrValueKind.None, value.Kind);
    }

    [Fact]
    public async Task LookupOne_FromWorkdirFile()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        WriteGitAttributes("*.txt text\n");
        // Force re-creation of cache to pick up the new .gitattributes file.
        AttributeCache cache = await AttributeCache.CreateAsync(_repo, cancellationToken);
        var path = new AttrPath();
        path.Init("file.txt", string.Empty, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(path, "text", cancellationToken: cancellationToken);
        Assert.True(value.IsTrue);
    }

    [Fact]
    public async Task LookupOne_FromInfoFile_HighestPrecedence()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        WriteGitAttributes("*.txt -text\n");
        WriteInfoAttributes("*.txt text\n");
        AttributeCache cache = await AttributeCache.CreateAsync(_repo, cancellationToken);
        var path = new AttrPath();
        path.Init("file.txt", string.Empty, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(path, "text", cancellationToken: cancellationToken);
        // Info file has higher precedence → text=true.
        Assert.True(value.IsTrue);
    }

    [Fact]
    public async Task LookupOne_WorkdirOverridesIndex()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Write workdir .gitattributes with text=true.
        WriteGitAttributes("*.txt text\n");
        // Stage a .gitattributes with text=false in the index.
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "*.txt -text\n"u8.ToArray(), cancellationToken);
        GitIndex index = await _repo.GetIndexAsync(cancellationToken);
        index.Add(new GitIndexEntry(".gitattributes", blobOid, GitFileMode.Regular));
        await index.WriteAsync(cancellationToken);

        AttributeCache cache = await AttributeCache.CreateAsync(_repo, cancellationToken);
        var path = new AttrPath();
        path.Init("file.txt", string.Empty, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(path, "text", cancellationToken: cancellationToken);
        // C (attr.c:512-546): GIT_ATTR_CHECK_FILE_THEN_INDEX is the default —
        // the workdir .gitattributes wins over the staged one.
        Assert.True(value.IsTrue);
    }

    [Fact]
    public async Task LookupMany_MultipleAttrs()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        WriteGitAttributes("*.txt text eol=lf\n");
        AttributeCache cache = await AttributeCache.CreateAsync(_repo, cancellationToken);
        var path = new AttrPath();
        path.Init("file.txt", string.Empty, AttrPath.DirFlag.False);
        GitAttrValue[] values = await cache.LookupManyAsync(path, ["text", "eol"], cancellationToken: cancellationToken);
        Assert.True(values[0].IsTrue);
        Assert.Equal(GitAttrValueKind.Value, values[1].Kind);
        Assert.Equal("lf", values[1].Text);
    }

    [Fact]
    public async Task LookupMany_PartialMatch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        WriteGitAttributes("*.txt text\n");
        AttributeCache cache = await AttributeCache.CreateAsync(_repo, cancellationToken);
        var path = new AttrPath();
        path.Init("file.txt", string.Empty, AttrPath.DirFlag.False);
        GitAttrValue[] values = await cache.LookupManyAsync(path, ["text", "eol"], cancellationToken: cancellationToken);
        Assert.True(values[0].IsTrue);
        Assert.Equal(GitAttrValueKind.None, values[1].Kind);
    }

    private void WriteGitAttributes(string content)
    {
        string attrPath = Path.Combine(_repo.Workdir!, ".gitattributes");
        File.WriteAllText(attrPath, content);
    }

    private void WriteInfoAttributes(string content)
    {
        string infoDir = Path.Combine(_repo.Path, "info");
        Directory.CreateDirectory(infoDir);
        string attrPath = Path.Combine(infoDir, "attributes");
        File.WriteAllText(attrPath, content);
    }
}
