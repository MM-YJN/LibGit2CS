using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;
using SubmoduleApi = LibGit2CS.Submodule.GitSubmodule;

namespace LibGit2CS.UnitTests.Submodule;

public sealed class SubmoduleStatusTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public SubmoduleStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleStatus_" + Guid.NewGuid().ToString("N")[..8]);
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
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
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

    private async Task AddGitlinkToIndex(string path, GitOid commitOid)
    {
        var entry = new GitIndexEntry(path, commitOid, GitFileMode.GitLink);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync();
        idx.Add(entry);
        await idx.WriteAsync();
    }

    private async Task CommitGitlink(string path, GitOid commitOid)
    {
        await AddGitlinkToIndex(path, commitOid);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        GitSignature sig = TestSig();

        // C (commit.c:113-117): a commit created with update_ref must have
        // the current tip as its first parent — chain onto it.
        List<GitOid> parentChain = [];
        if (await _repo.ReferenceResolveAsync("refs/heads/master") is GitDirectReference tipRef)
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
        });
    }

    // --- Status ---

    [Fact]
    public async Task Status_SubmoduleInConfigOnly_HasInConfigFlag()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        SubmoduleStatus status = await SubmoduleApi.StatusAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((status & SubmoduleStatus.InConfig) != 0);
        Assert.True((status & SubmoduleStatus.InHead) == 0);
        Assert.True((status & SubmoduleStatus.InIndex) == 0);
    }

    [Fact]
    public async Task Status_SubmoduleInIndexAndHead_HasInBothFlags()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        GitOid subOid = await CreateSubmoduleRepo();
        await CommitGitlink("test", subOid);

        SubmoduleStatus status = await SubmoduleApi.StatusAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((status & SubmoduleStatus.InConfig) != 0);
        Assert.True((status & SubmoduleStatus.InHead) != 0);
        Assert.True((status & SubmoduleStatus.InIndex) != 0);
    }

    [Fact]
    public async Task Status_IndexAdded_WhenInIndexNotInHead()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        GitOid subOid = await CreateSubmoduleRepo();
        await AddGitlinkToIndex("test", subOid);
        // Don't commit to HEAD.

        SubmoduleStatus status = await SubmoduleApi.StatusAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((status & SubmoduleStatus.IndexAdded) != 0);
    }

    [Fact]
    public async Task Status_IndexModified_WhenIndexAndHeadDiffer()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        GitOid subOid = await CreateSubmoduleRepo();
        await CommitGitlink("test", subOid);

        // Create a second commit in the submodule.
        string subDir = Path.Combine(_tempDir, "subrepo");
        await using GitRepository subRepo = await GitRepository.OpenAsync(subDir, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx_subRepo = await subRepo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subRepo.Workdir!, "sub2.txt"), "sub2 content\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx_subRepo.AddByPathAsync("sub2.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx_subRepo.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid2 = await idx_subRepo.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();

        // C (commit.c:113-117): chain onto the current tip.
        List<GitOid> parentChain = [];
        if (await subRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) is GitDirectReference subTip)
        {
            parentChain.Add(subTip.Target);
        }

        GitOid subOid2 = await subRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid2,
            Parents = parentChain,
            Author = sig,
            Committer = sig,
            Message = "sub second\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Update the index with the new submodule OID.
        await AddGitlinkToIndex("test", subOid2);

        SubmoduleStatus status = await SubmoduleApi.StatusAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((status & SubmoduleStatus.IndexModified) != 0);
    }

    [Fact]
    public async Task Status_IgnoreAll_ReturnsOnlyLocationFlags()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        GitOid subOid = await CreateSubmoduleRepo();
        await AddGitlinkToIndex("test", subOid);

        SubmoduleStatus status = await SubmoduleApi.StatusAsync(_repo, "test", SubmoduleIgnore.All, cancellationToken: TestContext.Current.CancellationToken);
        // With ignore=All, no index/wd status flags.
        Assert.True((status & SubmoduleStatus.IndexAdded) == 0);
        Assert.True((status & SubmoduleStatus.InConfig) != 0);
    }

    [Fact]
    public async Task Status_NonExistentSubmodule_Throws()
    {
        await Assert.ThrowsAsync<GitException>(async () => await SubmoduleApi.StatusAsync(_repo, "nonexistent", cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- Location ---

    [Fact]
    public async Task Location_ReturnsOnlyLocationBits()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        GitOid subOid = await CreateSubmoduleRepo();
        await CommitGitlink("test", subOid);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        SubmoduleStatus loc = sm!.Location();

        Assert.True((loc & SubmoduleStatus.InConfig) != 0);
        Assert.True((loc & SubmoduleStatus.InHead) != 0);
        Assert.True((loc & SubmoduleStatus.InIndex) != 0);
        // No index/wd status flags in location.
        Assert.True((loc & SubmoduleStatus.IndexAdded) == 0);
    }
}
