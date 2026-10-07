using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Attributes;

/// <summary>
/// Regression tests for the attribute-parity behaviors (per-directory
/// .gitattributes, root precedence, GIT_ATTR_CHECK_* flags) in
/// libgit2 1.9.4. Semantics
/// verified against libgit2 1.9.4 (attr.c:630-717, 512-546, 701-708).
/// </summary>
public sealed class AttributeCacheParityTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly GitRepository _repo;

    public AttributeCacheParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_AttrParity_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch (IOException)
        {
        }
    }

    private void WriteGitAttributes(string relativePath, string content)
    {
        string fullPath = Path.Combine(_repo.Workdir!, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static AttrPath MakePath(string path)
    {
        var attrPath = new AttrPath();
        attrPath.Init(path, string.Empty, AttrPath.DirFlag.False);
        return attrPath;
    }

    private static async Task<GitAttrValue> LookupAsync(AttributeCache cache, string path, string attr, GitAttrCheckFlags flags = GitAttrCheckFlags.FileThenIndex)
    {
        GitAttrValue[] values = await cache.LookupManyAsync(MakePath(path), [attr], flags, TestContext.Current.CancellationToken);
        return values[0];
    }

    [Fact]
    public async Task Lookup_PerDirectoryGitattributes_Applies()
    {
        // C walks up from the path's dir loading .gitattributes at EVERY
        // ancestor level (attr.c:630-717) — sub/.gitattributes applies to
        // sub/file.txt.
        WriteGitAttributes("sub/.gitattributes", "*.txt text\n");
        AttributeCache cache = await AttributeCache.CreateAsync(_repo, TestContext.Current.CancellationToken);

        GitAttrValue value = await LookupAsync(cache, "sub/file.txt", "text");
        Assert.True(value.IsTrue);
    }

    [Fact]
    public async Task Lookup_DeeperDirectory_OverridesShallower()
    {
        // The DEEPEST directory's file has the highest precedence among the
        // path files.
        WriteGitAttributes("sub/.gitattributes", "*.txt text\n");
        WriteGitAttributes("sub/deep/.gitattributes", "*.txt -text\n");
        AttributeCache cache = await AttributeCache.CreateAsync(_repo, TestContext.Current.CancellationToken);

        GitAttrValue value = await LookupAsync(cache, "sub/deep/file.txt", "text");
        Assert.True(value.IsFalse);
    }

    [Fact]
    public async Task Lookup_WorkdirRootOverridesIndex()
    {
        // GIT_ATTR_CHECK_FILE_THEN_INDEX (default, attr.c:512-546) — the
        // workdir root .gitattributes wins over the staged one.
        WriteGitAttributes(".gitattributes", "*.txt text\n");
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "*.txt -text\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry(".gitattributes", blobOid, GitFileMode.Regular));
        await index.WriteAsync(TestContext.Current.CancellationToken);

        AttributeCache cache = await AttributeCache.CreateAsync(_repo, TestContext.Current.CancellationToken);
        GitAttrValue value = await LookupAsync(cache, "file.txt", "text");
        Assert.True(value.IsTrue);
    }

    [Fact]
    public async Task Lookup_IndexOnly_IgnoresWorkdir()
    {
        // GIT_ATTR_CHECK_INDEX_ONLY consults only the index — with no
        // staged .gitattributes, the workdir file must NOT apply.
        WriteGitAttributes(".gitattributes", "*.txt text\n");
        AttributeCache cache = await AttributeCache.CreateAsync(_repo, TestContext.Current.CancellationToken);

        GitAttrValue value = await LookupAsync(cache, "file.txt", "text", GitAttrCheckFlags.IndexOnly);
        Assert.Equal(GitAttrValueKind.None, value.Kind);
    }

    [Fact]
    public async Task Lookup_IncludeHead_UsesHeadAttributes()
    {
        // GIT_ATTR_CHECK_INCLUDE_HEAD consults .gitattributes from the
        // HEAD tree (attr.c:523-530).
        WriteGitAttributes(".gitattributes", "*.txt text\n");
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(".gitattributes", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0)),
            Committer = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0)),
            Message = "c1\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        // Remove the workdir + index files so only HEAD has the rules.
        File.Delete(Path.Combine(_repo.Workdir!, ".gitattributes"));
        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        index.Remove(".gitattributes", 0);
        await index.WriteAsync(TestContext.Current.CancellationToken);

        AttributeCache cache = await AttributeCache.CreateAsync(_repo, TestContext.Current.CancellationToken);
        GitAttrValue value = await LookupAsync(cache, "file.txt", "text", GitAttrCheckFlags.IncludeHead);
        Assert.True(value.IsTrue);
    }

    [Fact]
    public async Task Lookup_NoSystem_SkipsSystemFile()
    {
        // GIT_ATTR_CHECK_NO_SYSTEM skips the system file (attr.c:701-708).
        string sysDir = Path.Combine(_tempDir, "sys");
        Directory.CreateDirectory(sysDir);
        await File.WriteAllTextAsync(Path.Combine(sysDir, "gitattributes"), "*.txt text\n", TestContext.Current.CancellationToken);
        var dirs = new GitSystemDirs();
        dirs.Set(GitSystemDir.System, sysDir);

        string repoPath = Path.Combine(_tempDir, "no-system");
        var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.System, sysDir);
        await using GitRepository repo2 = await GitRepository.InitAsync(repoPath, isBare: false, ctx, TestContext.Current.CancellationToken);
        AttributeCache cache = await AttributeCache.CreateAsync(repo2, TestContext.Current.CancellationToken);

        GitAttrValue withSystem = await LookupAsync(cache, "file.txt", "text");
        Assert.True(withSystem.IsTrue);

        GitAttrValue noSystem = await LookupAsync(cache, "file.txt", "text", GitAttrCheckFlags.NoSystem);
        Assert.Equal(GitAttrValueKind.None, noSystem.Kind);
    }
}
