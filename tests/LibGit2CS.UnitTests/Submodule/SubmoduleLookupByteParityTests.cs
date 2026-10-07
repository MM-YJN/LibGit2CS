using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;
using SubmoduleApi = LibGit2CS.Submodule.GitSubmodule;

namespace LibGit2CS.UnitTests.Submodule;

/// <summary> The submodule cache is byte-keyed — the name-keyed cache and the path→name map operate on raw bytes (C's
/// <c>git_submodule_cache</c>/<c>git_submodule_namemap</c>, submodule.c:268, 172) and <see cref="SubmoduleApi.LookupAsync(GitRepository, GitPath,
/// CancellationToken)"/> takes the raw path bytes (submodule.c:308-433). A non-UTF-8 gitlink path with a matching .gitmodules entry resolves byte-exact; the
/// trailing-<c>/</c> trim (submodule.c:364-367) is byte-domain. </summary>
public sealed class SubmoduleLookupByteParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public SubmoduleLookupByteParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleLookupByte_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
        await WriteCommit();
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

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommit()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync();
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("README.md");
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        GitSignature sig = TestSig();

        List<GitOid> parentChain = [];
        if (await _repo.ReferenceResolveAsync("refs/heads/master") is GitDirectReference tipRef)
        {
            parentChain.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parentChain,
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = "refs/heads/master",
        });
    }

    private void WriteGitmodules(byte[] content)
    {
        File.WriteAllBytes(Path.Combine(_repo.Workdir!, ".gitmodules"), content);
    }

    private async Task AddGitlinkToIndex(string path, GitOid commitOid)
    {
        var entry = new GitIndexEntry(path, commitOid, GitFileMode.GitLink);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync();
        idx.Add(entry);
        await idx.WriteAsync();
    }

    private async Task<GitOid> CreateSubmoduleRepo()
    {
        string subDir = Path.Combine(_tempDir, "subrepo");
        Directory.CreateDirectory(subDir);
        await using GitRepository subRepo = await GitRepository.InitAsync(subDir, isBare: false, new GitContext());
        LibGit2CS.Index.GitIndex idx_subRepo = await subRepo.GetIndexAsync();
        await File.WriteAllTextAsync(Path.Combine(subRepo.Workdir!, "sub.txt"), "sub content\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx_subRepo.AddByPathAsync("sub.txt");
        await idx_subRepo.WriteAsync();
        GitOid treeOid = await idx_subRepo.WriteTreeAsync();
        GitSignature sig = TestSig();
        return await subRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "sub init\n",
            UpdateRef = "refs/heads/master",
        });
    }

    // ── 1: non-UTF-8 gitlink path resolves via GitPath lookup ─────

    [Fact]
    public async Task Lookup_NonUtf8Path_ResolvesByteExact()
    {
        // A .gitmodules entry whose path contains a raw 0xE9 byte (invalid
        // UTF-8): the GitPath lookup must resolve it byte-exact (C's
        // git_submodule_lookup takes the raw path bytes, submodule.c:308-433).
        byte[] pathBytes = [.. "caf"u8.ToArray(), 0xE9];
        string pathString = Encoding.UTF8.GetString(pathBytes); // "caf\uFFFD"
        WriteGitmodules([.. "[submodule \"sm\"]\n    path = "u8.ToArray(), .. pathBytes, .. "\n    url = https://example.com/sm.git\n"u8.ToArray()]);
        GitOid subOid = await CreateSubmoduleRepo();
        await AddGitlinkToIndex(pathString, subOid);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, GitPath.FromUtf8Bytes(pathBytes), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal("sm", sm!.Name);
        Assert.Equal(pathBytes, sm.Path!.Value.ToUtf8Bytes().ToArray());
    }

    // ── 2: trailing-slash trim is byte-domain ──────────────────────

    [Fact]
    public async Task Lookup_TrailingSlash_ByteTrim()
    {
        // (submodule.c:364-367): C trims trailing '/' from the lookup
        // name — byte-domain (0x2F).
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n"u8.ToArray());
        GitOid subOid = await CreateSubmoduleRepo();
        await AddGitlinkToIndex("test", subOid);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, GitPath.FromUtf8String("test/"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal("test", sm!.Name);
    }

    // ── 3: path≠name entry resolves via the path→name map ────────

    [Fact]
    public async Task Lookup_ByPath_ResolvesNameMismatch()
    {
        // C (submodule.c:357-396): a lookup by PATH resolves the .gitmodules
        // entry whose path differs from its name (find_by_path).
        WriteGitmodules("[submodule \"sm\"]\n    path = dir/sub\n    url = https://example.com/sm.git\n"u8.ToArray());
        GitOid subOid = await CreateSubmoduleRepo();
        await AddGitlinkToIndex("dir/sub", subOid);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, GitPath.FromUtf8String("dir/sub"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal("sm", sm!.Name);
        Assert.Equal("dir/sub", sm.Path!.Value.ToUtf8String());
    }
}
