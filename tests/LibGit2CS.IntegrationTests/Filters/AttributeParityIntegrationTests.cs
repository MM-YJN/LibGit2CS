using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.IntegrationTests.Filters;

/// <summary>
/// End-to-end tests for the attribute parity behaviors (per-directory
/// .gitattributes, root precedence, GIT_ATTR_CHECK_* flags) in
/// libgit2 1.9.4. The filter
/// pipeline uses the attribute lookups, so a per-directory "text" rule
/// changes the CRLF filter behavior end-to-end.
/// </summary>
public sealed class AttributeParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public AttributeParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_AttrParityInt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void WriteFile(string repoDir, string relativePath, string content)
    {
        string fullPath = Path.Combine(repoDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public async Task PerDirectoryGitattributes_AffectsBlobHash()
    {
        // end-to-end: with core.autocrlf=false, sub/.gitattributes
        // "*.txt text" alone makes the CRLF filter normalize "a\r\nb" — the
        // blob OID differs from the unfiltered hash (ignoring the per-dir rule
        // would give identical hashes).
        string path = Path.Combine(_tempDir, "r");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        await repo.Config.SetBoolAsync("core.autocrlf", false, TestContext.Current.CancellationToken);
        WriteFile(path, "sub/.gitattributes", "*.txt text\n");
        WriteFile(path, "sub/a.txt", "a\r\nb\n");

        // Raw blob write (no filters).
        GitOid rawOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "a\r\nb\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // Filtered write through the workdir (CRLF → LF).
        GitOid filteredOid = await repo.BlobCreateFromWorkdirAsync("sub/a.txt", TestContext.Current.CancellationToken);

        Assert.NotEqual(rawOid, filteredOid);
        GitBlob? filtered = await repo.ObjectLookupAsync<GitBlob>(filteredOid, TestContext.Current.CancellationToken);
        Assert.Equal("a\nb\n", System.Text.Encoding.UTF8.GetString(filtered!.Raw.Span));
    }

    [Fact]
    public async Task RootWorkdirAttributes_WinsOverIndex()
    {
        // FILE_THEN_INDEX — the workdir root .gitattributes wins over the
        // staged one.
        string path = Path.Combine(_tempDir, "root-attr-wins");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        WriteFile(path, ".gitattributes", "*.txt text\n");
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "*.txt -text\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry(".gitattributes", blobOid, GitFileMode.Regular));
        await index.WriteAsync(TestContext.Current.CancellationToken);

        AttributeCache cache = await repo.GetAttributeCacheAsync(TestContext.Current.CancellationToken);
        var attrPath = new AttrPath();
        attrPath.Init("file.txt", string.Empty, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(attrPath, "text", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(value.IsTrue);
    }

    [Fact]
    public async Task NoSystemAttributes_FilterFlag_IsHonored()
    {
        // GIT_FILTER_NO_SYSTEM_ATTRIBUTES maps to GIT_ATTR_CHECK_NO_SYSTEM
        // (filter.c:443-458) — the system file is skipped. With a system
        // "*.txt text" rule and core.autocrlf=false, the CRLF filter applies
        // by default and is absent with the NoSystemAttributes flag.
        string sysDir = Path.Combine(_tempDir, "sys");
        Directory.CreateDirectory(sysDir);
        await File.WriteAllTextAsync(Path.Combine(sysDir, "gitattributes"), "*.txt text\n", TestContext.Current.CancellationToken);

        string path = Path.Combine(_tempDir, "no-system-attr");
        var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.System, sysDir);
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        await repo.Config.SetBoolAsync("core.autocrlf", false, TestContext.Current.CancellationToken);
        WriteFile(path, "a.txt", "content\n");

        GitFilterList? withSystem = await repo.FilterListLoadAsync(GitPath.FromUtf8String("a.txt"), blobId: null, GitFilterMode.ToOdb, GitFilterListFlags.None, cancellationToken: TestContext.Current.CancellationToken);
        GitFilterList? noSystem = await repo.FilterListLoadAsync(GitPath.FromUtf8String("a.txt"), blobId: null, GitFilterMode.ToOdb, GitFilterListFlags.NoSystemAttributes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(withSystem);
        Assert.Null(noSystem);
    }
}
