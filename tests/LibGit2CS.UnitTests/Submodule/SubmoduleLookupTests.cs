using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;
using SubmoduleApi = LibGit2CS.Submodule.GitSubmodule;

namespace LibGit2CS.UnitTests.Submodule;

public sealed class SubmoduleLookupTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public SubmoduleLookupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleLookup_" + Guid.NewGuid().ToString("N")[..8]);
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

        // C (commit.c:113-117): a commit created with update_ref must have
        // the current tip as its first parent — chain onto it.
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

    private void WriteGitmodules(string content)
    {
        File.WriteAllText(Path.Combine(_repo.Workdir!, ".gitmodules"), content);
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

    // --- Lookup ---

    [Fact]
    public async Task Lookup_NoGitmodules_ThrowsNotFound()
    {
        // (submodule.c:403-425): C's git_submodule_lookup fails with
        // GIT_ENOTFOUND "no submodule named 'nonexistent'" when the
        // submodule is not configured and no repo exists at the path.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SubmoduleApi.LookupAsync(_repo, "nonexistent", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("no submodule named 'nonexistent'", ex.Message);
    }

    [Fact]
    public async Task Lookup_WithGitmodules_ReturnsSubmodule()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        GitOid subOid = await CreateSubmoduleRepo();
        await AddGitlinkToIndex("test", subOid);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal("test", sm!.Name);
        Assert.Equal("test", sm.Path?.ToUtf8String());
        Assert.Equal("https://example.com/test.git", sm.Url);
    }

    [Fact]
    public async Task Lookup_BareRepo_Throws()
    {
        string bareDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleBare_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using GitRepository bare = await GitRepository.InitAsync(bareDir, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<GitException>(async () => await SubmoduleApi.LookupAsync(bare, "test", cancellationToken: TestContext.Current.CancellationToken));
            await bare.DisposeAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(bareDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // --- ForEach ---

    [Fact]
    public async Task ForEach_NoGitmodules_ReturnsEmpty()
    {
        var subs = new List<SubmoduleApi>();
        await foreach (SubmoduleApi e in SubmoduleApi.ForEachAsync(_repo, cancellationToken: TestContext.Current.CancellationToken))
        {
            subs.Add(e);
        }
        Assert.Empty(subs);
    }

    [Fact]
    public async Task ForEach_WithGitmodules_ReturnsAll()
    {
        WriteGitmodules(
            "[submodule \"sub1\"]\n    path = sub1\n    url = https://example.com/sub1.git\n" +
            "[submodule \"sub2\"]\n    path = sub2\n    url = https://example.com/sub2.git\n");

        var subs = new List<SubmoduleApi>();
        await foreach (SubmoduleApi e in SubmoduleApi.ForEachAsync(_repo, cancellationToken: TestContext.Current.CancellationToken))
        {
            subs.Add(e);
        }
        Assert.Equal(2, subs.Count);
    }

    [Fact]
    public async Task ForEach_BareRepo_Throws()
    {
        string bareDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleBare2_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using GitRepository bare = await GitRepository.InitAsync(bareDir, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<GitException>(async () =>
            {
                await foreach (SubmoduleApi _ in SubmoduleApi.ForEachAsync(bare, cancellationToken: TestContext.Current.CancellationToken))
                {
                }
            });
            await bare.DisposeAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(bareDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // --- Accessors ---

    [Fact]
    public async Task Accessors_NamePathUrl_ReturnCorrectValues()
    {
        WriteGitmodules("[submodule \"myname\"]\n    path = mypath\n    url = https://example.com/sub.git\n    branch = develop\n");
        GitOid subOid = await CreateSubmoduleRepo();
        await AddGitlinkToIndex("mypath", subOid);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "myname", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal("myname", sm!.Name);
        Assert.Equal("mypath", sm.Path?.ToUtf8String());
        Assert.Equal("https://example.com/sub.git", sm.Url);
        Assert.Equal("develop", sm.Branch);
    }

    [Fact]
    public async Task Accessors_IndexId_ReturnsGitlinkOid()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        GitOid subOid = await CreateSubmoduleRepo();
        await AddGitlinkToIndex("test", subOid);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal(subOid, sm!.IndexId);
    }

    [Fact]
    public async Task Accessors_HeadId_ReturnsTreeOid()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        GitOid subOid = await CreateSubmoduleRepo();
        await AddGitlinkToIndex("test", subOid);

        // Commit the gitlink to HEAD. C (commit.c:113-117): chain onto the
        // current tip.
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();
        List<GitOid> parentChain = [];
        if (await _repo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parentChain.Add(tipRef.Target);
        }

        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parentChain,
            Author = sig,
            Committer = sig,
            Message = "add submodule\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal(subOid, sm!.HeadId);
    }

    // --- Unborn head / missing index ---

    [Fact]
    public async Task Lookup_WithUnbornHead_Works()
    {
        // The repo was Init'd but we haven't committed in this test variant.
        string freshDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleUnborn_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(freshDir);
            await using GitRepository freshRepo = await GitRepository.InitAsync(freshDir, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(freshRepo.Workdir!, ".gitmodules"),
                "[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n", cancellationToken: TestContext.Current.CancellationToken);

            SubmoduleApi? sm = await SubmoduleApi.LookupAsync(freshRepo, "test", cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(sm);
            Assert.Equal("test", sm!.Name);
        }
        finally
        {
            try
            {
                Directory.Delete(freshDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
