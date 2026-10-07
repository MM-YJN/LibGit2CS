using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

public sealed class RepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepositoryTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Open_EmptyBareRepo_ReturnsBareRepository()
    {
        string extractedPath = ExtractRepo("empty_bare.zip");

        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(extractedPath, "empty_bare.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(repo.IsBare);
        Assert.Null(repo.Workdir);
        Assert.Equal(GitHashAlgorithmKind.Sha1, repo.ObjectFormat);
    }

    [Fact]
    public async Task Open_EmptyStandardRepo_ReturnsNonBareRepository()
    {
        // The fixture uses the ".gitted" extension name, so the
        // upward search (git_repository_open_ext(path, 0)) is needed.
        string extractedPath = ExtractRepo("empty_standard.zip");

        await using GitRepository repo = await GitRepository.OpenExtAsync(Path.Combine(extractedPath, "empty_standard_repo"), RepositoryOpenFlags.None, ceilingDirs: null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(repo.IsBare);
        Assert.NotNull(repo.Workdir);
        Assert.EndsWith("empty_standard_repo", repo.Workdir!.TrimEnd('/'));
    }

    [Fact]
    public async Task Open_TestRepo_ReturnsRepositoryWithObjects()
    {
        string extractedPath = ExtractRepo("testrepo.zip");

        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(extractedPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        var oid = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
        Assert.True(await repo.Objects.ExistsAsync(oid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_NonexistentPath_Throws()
    {
        await Assert.ThrowsAsync<GitException>(() =>
            GitRepository.OpenAsync(Path.Combine(_tempDir, "does_not_exist"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_FromSubdirectory_ThrowsNotFound()
    {
        // C: git_repository_open is NO_SEARCH (repository.c:1188-1192) — only
        // path/.git and path itself are checked.
        string extractedPath = ExtractRepo("empty_standard.zip");
        string subdir = Path.Combine(extractedPath, "empty_standard_repo", "subdir");
        Directory.CreateDirectory(subdir);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenAsync(subdir, new GitContext(), cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task OpenExt_FromSubdirectory_FindsRepo()
    {
        // The upward search is git_repository_open_ext(path, 0).
        string extractedPath = ExtractRepo("empty_standard.zip");
        string subdir = Path.Combine(extractedPath, "empty_standard_repo", "subdir");
        Directory.CreateDirectory(subdir);

        await using GitRepository repo = await GitRepository.OpenExtAsync(subdir, RepositoryOpenFlags.None, ceilingDirs: null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(repo.IsBare);
        Assert.NotNull(repo.Workdir);
        Assert.EndsWith(".gitted", repo.Path.TrimEnd('/'));
    }

    [Fact]
    public async Task Discover_FromRepoRoot_FindsGitDir()
    {
        string extractedPath = ExtractRepo("empty_standard.zip");
        string repoRoot = Path.Combine(extractedPath, "empty_standard_repo");

        string gitdir = await GitRepository.DiscoverAsync(repoRoot, cancellationToken: TestContext.Current.CancellationToken);

        Assert.EndsWith(".gitted", gitdir.TrimEnd('/'));
    }

    [Fact]
    public async Task OpenBare_OpensBareRepo()
    {
        string extractedPath = ExtractRepo("empty_bare.zip");

        await using GitRepository repo = await GitRepository.OpenBareAsync(Path.Combine(extractedPath, "empty_bare.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(repo.IsBare);
        Assert.Null(repo.Workdir);
    }

    [Fact]
    public async Task Dispose_Twice_DoesNotThrow()
    {
        string extractedPath = ExtractRepo("empty_bare.zip");
        GitRepository repo = await GitRepository.OpenAsync(Path.Combine(extractedPath, "empty_bare.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        await repo.DisposeAsync();
        await repo.DisposeAsync();
    }

    [Fact]
    public async Task ObjectFormat_DefaultsToSha1()
    {
        string extractedPath = ExtractRepo("empty_bare.zip");

        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(extractedPath, "empty_bare.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitHashAlgorithmKind.Sha1, repo.ObjectFormat);
    }

    [Fact]
    public async Task Open_TestRepo_HasConfigAndObjects()
    {
        string extractedPath = ExtractRepo("testrepo.zip");

        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(extractedPath, "testrepo.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(repo.Config);
        Assert.NotNull(repo.Objects);
        Assert.Equal("testrepo.git", Path.GetFileName(repo.Path.TrimEnd('/')));
    }

    [Fact]
    public async Task OpenExt_WithNoSearch_OnGitDir_OpensRepo()
    {
        string extractedPath = ExtractRepo("empty_bare.zip");

        await using GitRepository repo = await GitRepository.OpenExtAsync(Path.Combine(extractedPath, "empty_bare.git"), RepositoryOpenFlags.NoSearch, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(repo.IsBare);
    }

    /// <summary>
    /// Writes a <c>.gitconfig</c> at a fake <c>HOME</c>, then opens a repo
    /// whose own config has no <c>user.email</c>. The opened repo must see the
    /// global value — i.e. <c>RepositoryOpener.OpenRepoConfigAsync</c> merged
    /// the global level into the repo's config, matching libgit2's
    /// <c>load_config</c>. Regression guard: global/xdg/system/programdata
    /// must all be loaded, not just local + worktree.
    /// </summary>
    [Fact]
    public async Task Open_GlobalLevelConfig_IsVisibleThroughRepo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string fakeHome = Path.Combine(_tempDir, "fakehome-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fakeHome);
        await File.WriteAllTextAsync(Path.Combine(fakeHome, ".gitconfig"), "[user]\n\temail = global@example.com\n", ct);

        string repoPath = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        await using (GitRepository init = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: ct))
        {
            await init.DisposeAsync();
        }

        GitContext ctx = new();
        ctx.Env["HOME"] = fakeHome;
        ctx.Dirs.Reset();

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, ctx, cancellationToken: ct);
        string? email = await repo.Config.GetStringAsync("user.email", ct);
        Assert.Equal("global@example.com", email);
    }

    /// <summary>
    /// The local level overrides the global level for the same key, locking in
    /// libgit2's backend priority ordering
    /// (local &gt; worktree &gt; global &gt; xdg &gt; system &gt; programdata).
    /// </summary>
    [Fact]
    public async Task Open_LocalLevel_OverridesGlobalForSameKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string fakeHome = Path.Combine(_tempDir, "fakehome-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fakeHome);
        await File.WriteAllTextAsync(Path.Combine(fakeHome, ".gitconfig"), "[user]\n\temail = global@example.com\n", ct);

        string repoPath = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        await using (GitRepository init = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: ct))
        {
            await init.Config.SetStringAsync("user.email", "local@example.com", ct);
            await init.DisposeAsync();
        }

        GitContext ctx = new();
        ctx.Env["HOME"] = fakeHome;
        ctx.Dirs.Reset();

        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, ctx, cancellationToken: ct);
        string? email = await repo.Config.GetStringAsync("user.email", ct);
        Assert.Equal("local@example.com", email);
    }

    private string ExtractRepo(string zipFileName)
    {
        string path = FixtureLoader.ExtractTreeToTemp($"Fixtures/repo/{zipFileName}");
        _extractedPaths.Add(path);
        return path;
    }
}
