using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using SubmoduleApi = LibGit2CS.Submodule.GitSubmodule;

namespace LibGit2CS.UnitTests.Submodule;

public sealed class SubmoduleModifyTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public SubmoduleModifyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleModify_" + Guid.NewGuid().ToString("N")[..8]);
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

    // --- SetUrl ---

    [Fact]
    public async Task SetUrl_WritesToGitmodules()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://old.example.com/test.git\n");

        await SubmoduleApi.SetUrlAsync(_repo, "test", "https://new.example.com/test.git", cancellationToken: TestContext.Current.CancellationToken);

        // Reload and check.
        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal("https://new.example.com/test.git", sm!.Url);
    }

    // --- SetBranch ---

    [Fact]
    public async Task SetBranch_WritesToGitmodules()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        await SubmoduleApi.SetBranchAsync(_repo, "test", "develop", cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal("develop", sm!.Branch);
    }

    [Fact]
    public async Task SetBranch_NullClearsBranch()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n    branch = main\n");

        await SubmoduleApi.SetBranchAsync(_repo, "test", null, cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Null(sm!.Branch);
    }

    // --- SetIgnore ---

    [Fact]
    public async Task SetIgnore_WritesToGitmodules()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        await SubmoduleApi.SetIgnoreAsync(_repo, "test", SubmoduleIgnore.Untracked, cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal(SubmoduleIgnore.Untracked, sm!.Ignore);
    }

    [Fact]
    public async Task SetIgnore_All_WritesToGitmodules()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        await SubmoduleApi.SetIgnoreAsync(_repo, "test", SubmoduleIgnore.All, cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal(SubmoduleIgnore.All, sm!.Ignore);
    }

    // --- SetUpdate ---

    [Fact]
    public async Task SetUpdate_WritesToGitmodules()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        await SubmoduleApi.SetUpdateAsync(_repo, "test", SubmoduleUpdateStrategy.Rebase, cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal(SubmoduleUpdateStrategy.Rebase, sm!.UpdateStrategy);
    }

    [Fact]
    public async Task SetUpdate_None_WritesToGitmodules()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        await SubmoduleApi.SetUpdateAsync(_repo, "test", SubmoduleUpdateStrategy.None, cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal(SubmoduleUpdateStrategy.None, sm!.UpdateStrategy);
    }

    // --- SetFetchRecurse ---

    [Fact]
    public async Task SetFetchRecurse_WritesToGitmodules()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        await SubmoduleApi.SetFetchRecurseAsync(_repo, "test", SubmoduleRecurse.Yes, cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal(SubmoduleRecurse.Yes, sm!.FetchRecurse);
    }

    [Fact]
    public async Task SetFetchRecurse_OnDemand_WritesToGitmodules()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        await SubmoduleApi.SetFetchRecurseAsync(_repo, "test", SubmoduleRecurse.OnDemand, cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        Assert.Equal(SubmoduleRecurse.OnDemand, sm!.FetchRecurse);
    }

    // --- Init ---

    [Fact]
    public async Task Init_CopiesUrlToConfig()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        await sm!.InitAsync(cancellationToken: TestContext.Current.CancellationToken);

        string? configUrl = await _repo.Config.GetStringAsync("submodule.test.url", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/test.git", configUrl);
    }

    [Fact]
    public async Task Init_DoesNotOverwriteExisting()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        await _repo.Config.SetStringAsync("submodule.test.url", "https://already.set/test.git", cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        await sm!.InitAsync(overwrite: false, cancellationToken: TestContext.Current.CancellationToken);

        string? configUrl = await _repo.Config.GetStringAsync("submodule.test.url", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("https://already.set/test.git", configUrl);
    }

    [Fact]
    public async Task Init_Overwrite_OverwritesExisting()
    {
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");
        await _repo.Config.SetStringAsync("submodule.test.url", "https://already.set/test.git", cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);
        await sm!.InitAsync(overwrite: true, cancellationToken: TestContext.Current.CancellationToken);

        string? configUrl = await _repo.Config.GetStringAsync("submodule.test.url", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/test.git", configUrl);
    }

    // --- ResolveUrl ---

    [Fact]
    public async Task ResolveUrl_AbsoluteUrl_ReturnedAsIs()
    {
        string url = await SubmoduleApi.ResolveUrlAsync(_repo, "https://example.com/test.git", TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/test.git", url);
    }

    [Fact]
    public async Task ResolveUrl_RelativeUrl_ResolvedAgainstWorkdir()
    {
        // a ./ relative url resolves against the parent's URL base
        // (default remote, else workdir) via git_fs_path_apply_relative.
        string url = await SubmoduleApi.ResolveUrlAsync(_repo, "./sub.git", TestContext.Current.CancellationToken);
        Assert.Equal($"{_repo.Workdir!}sub.git", url);
    }
}
