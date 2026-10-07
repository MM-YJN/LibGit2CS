using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;
using SubmoduleApi = LibGit2CS.Submodule.GitSubmodule;

namespace LibGit2CS.UnitTests.Submodule;

public sealed class SubmoduleAddTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public SubmoduleAddTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleAdd_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitOid> CreateSourceRepo()
    {
        string subDir = Path.Combine(_tempDir, "sourcerepo");
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

    /// <summary>
    /// The source repo's filesystem path as a URL (forward slashes) — a
    /// valid submodule URL. A bare 40-hex OID is rejected by C's
    /// git_submodule__resolve_url ("invalid format for submodule URL").
    /// </summary>
    private string SourceRepoUrl()
        => Path.Combine(_tempDir, "sourcerepo").Replace('\\', '/');

    /// <summary>
    /// Gives the (empty, just-inited) submodule at <paramref name="path"/> a
    /// HEAD commit. C's <c>git_submodule_add_setup</c> clones the source into
    /// the submodule, so a real add flow always has a HEAD. AddSetup only
    /// inits, so the tests commit directly.
    /// AddToIndex on an
    /// unborn submodule must fail, so these tests need a populated repo.
    /// </summary>
    private async Task CommitInSubmoduleAsync(string path)
    {
        string subPath = Path.Combine(_repo.Workdir!, path);
        await using GitRepository subRepo = await GitRepository.OpenAsync(subPath, new GitContext());
        LibGit2CS.Index.GitIndex idx = await subRepo.GetIndexAsync();
        await File.WriteAllTextAsync(
            Path.Combine(subPath, "file.txt"), "sub content\n",
            cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file.txt");
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        await subRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "sub init\n",
            UpdateRef = "HEAD",
        });
    }

    // --- AddSetup ---

    [Fact]
    public async Task AddSetup_WritesGitmodules()
    {
        _ = await CreateSourceRepo();

        SubmoduleApi sm = await SubmoduleApi.AddSetupAsync(_repo, SourceRepoUrl(), "sub", useGitlink: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("sub", sm.Name);
        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, ".gitmodules")));
    }

    [Fact]
    public async Task AddSetup_WithAbsoluteUrl_StoresUrl()
    {
        SubmoduleApi sm = await SubmoduleApi.AddSetupAsync(_repo, "https://example.com/test.git", "test", useGitlink: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/test.git", sm.Url);
    }

    [Fact]
    public async Task AddSetup_CreatesSubmoduleDir()
    {
        _ = await SubmoduleApi.AddSetupAsync(_repo, "https://example.com/test.git", "test", useGitlink: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Combine(_repo.Workdir!, "test")));
    }

    [Fact]
    public async Task AddSetup_GitlinkMode_CreatesModulesDir()
    {
        _ = await SubmoduleApi.AddSetupAsync(_repo, "https://example.com/test.git", "test", useGitlink: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Combine(_repo.Path, "modules", "test")));
        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "test", ".git")));
    }

    [Fact]
    public async Task AddSetup_InitsSubmodule()
    {
        _ = await SubmoduleApi.AddSetupAsync(_repo, "https://example.com/test.git", "test", useGitlink: false, cancellationToken: TestContext.Current.CancellationToken);

        // Init should have copied the URL to .git/config.
        string? configUrl = await _repo.Config.GetStringAsync("submodule.test.url", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/test.git", configUrl);
    }

    // --- AddToIndex ---

    [Fact]
    public async Task AddToIndex_StagesGitlink()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        _ = await CreateSourceRepo();
        SubmoduleApi sm = await SubmoduleApi.AddSetupAsync(_repo, SourceRepoUrl(), "sub", useGitlink: false, cancellationToken: TestContext.Current.CancellationToken);
        await CommitInSubmoduleAsync("sub");

        await sm.AddToIndexAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The index should have a gitlink entry at "sub".
        GitIndexEntry entry = idx.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "sub");
        Assert.Equal(GitFileMode.GitLink, entry.Mode);
        Assert.False(entry.Id.IsZero);
    }

    // --- AddFinalize ---

    [Fact]
    public async Task AddFinalize_StagesGitmodulesAndGitlink()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        _ = await CreateSourceRepo();
        SubmoduleApi sm = await SubmoduleApi.AddSetupAsync(_repo, SourceRepoUrl(), "sub", useGitlink: false, cancellationToken: TestContext.Current.CancellationToken);
        await CommitInSubmoduleAsync("sub");

        await sm.AddFinalizeAsync(cancellationToken: TestContext.Current.CancellationToken);

        // .gitmodules should be staged.
        GitIndexEntry gitmodulesEntry = idx.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == ".gitmodules");
        Assert.Equal(".gitmodules", gitmodulesEntry.Path.ToUtf8String());

        // Gitlink should be staged.
        GitIndexEntry gitlinkEntry = idx.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "sub");
        Assert.Equal(GitFileMode.GitLink, gitlinkEntry.Mode);
        Assert.False(gitlinkEntry.Id.IsZero);
    }

    // --- Update (with local clone) ---

    [Fact]
    public async Task Update_UninitializedSubmodule_WithoutInit_Throws()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        // Write .gitmodules with a local path as URL.
        GitOid subOid = await CreateSourceRepo();
        string subDir = Path.Combine(_tempDir, "sourcerepo");

        // Create the submodule entry in .gitmodules but don't clone.
        // Use forward slashes in the URL: the config parser treats backslash
        // as an escape character (e.g. '\U' is invalid), and libgit2's
        // LocalPathFromUrl accepts forward-slash Windows paths.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, ".gitmodules"),
            $"[submodule \"sub\"]\n    path = sub\n    url = {subDir.Replace('\\', '/')}\n", cancellationToken: TestContext.Current.CancellationToken);

        // Add a gitlink to the index pointing at the source commit.
        var entry = new GitIndexEntry("sub", subOid, GitFileMode.GitLink);
        idx.Add(entry);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);

        // Update without init should throw (submodule not initialized in .git/config).
        // But first, let's init the submodule to create the workdir dir (empty).
        Directory.CreateDirectory(Path.Combine(_repo.Workdir!, "sub"));

        await Assert.ThrowsAsync<GitException>(async () => await sm!.UpdateAsync(init: false, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_WithInit_ClonesAndChecksOut()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid subOid = await CreateSourceRepo();
        string subDir = Path.Combine(_tempDir, "sourcerepo");

        // Write .gitmodules. Use forward slashes in the URL: the config
        // parser treats backslash as an escape character (e.g. '\U' is
        // invalid), and libgit2's LocalPathFromUrl accepts forward-slash
        // Windows paths.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, ".gitmodules"),
            $"[submodule \"sub\"]\n    path = sub\n    url = {subDir.Replace('\\', '/')}\n", cancellationToken: TestContext.Current.CancellationToken);

        // Add a gitlink to the index.
        var entry = new GitIndexEntry("sub", subOid, GitFileMode.GitLink);
        idx.Add(entry);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleApi? sm = await SubmoduleApi.LookupAsync(_repo, "sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sm);

        // Create the workdir dir (empty) to simulate WD_UNINITIALIZED.
        Directory.CreateDirectory(Path.Combine(_repo.Workdir!, "sub"));

        // Update with init should clone and checkout.
        await sm!.UpdateAsync(init: true, cancellationToken: TestContext.Current.CancellationToken);

        // The submodule workdir should now have the file checked out.
        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "sub", "sub.txt")));
    }
}
