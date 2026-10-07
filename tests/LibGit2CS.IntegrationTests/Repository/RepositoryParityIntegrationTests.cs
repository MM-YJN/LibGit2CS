using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Repository;

/// <summary>
/// End-to-end tests for the repository open/discover/init parity behaviors
/// (against repos built from scratch: the open/discover flag inversion,
/// FROM_ENV gating, discovery validation, gitfile parsing, NO_REINIT and
/// reinit HEAD preservation, init on a /.git path, external
/// templates, init config key order, GIT_INIT_BRANCH,
/// absolute gitlinks, set_workdir, linked-worktree gitdirs,
/// and known extensions).
/// </summary>
public sealed class RepositoryParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepoParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string NewDir(string name)
    {
        string path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task OpenAsync_FromSubdirectory_ThrowsNotFound_ButDiscoverFinds()
    {
        using GitContext ctx = new();
        string repo = NewDir("int-repo");
        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);
        string sub = Path.Combine(repo, "sub", "deep");
        Directory.CreateDirectory(sub);

        // git_repository_open is NO_SEARCH.
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenAsync(sub, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);
        Assert.Equal(GitErrorCode.NotFound, ex.Code);

        // git_repository_discover walks upward.
        string gitdir = await GitRepository.DiscoverAsync(sub, acrossFs: false, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repo, ".git")), gitdir);
    }

    [Fact]
    public async Task GitDirEnv_OnlyHonoredWithFromEnv()
    {
        using GitContext ctx = new();
        string repoA = NewDir("int-a");
        string repoB = NewDir("int-b");
        _ = await GitRepository.InitAsync(repoA, isBare: false, ctx, TestContext.Current.CancellationToken);
        _ = await GitRepository.InitAsync(repoB, isBare: false, ctx, TestContext.Current.CancellationToken);
        ctx.Env["GIT_DIR"] = Path.Combine(repoB, ".git");

        // Without FROM_ENV the env is ignored.
        await using GitRepository plain = await GitRepository.OpenExtAsync(Path.Combine(repoA, ".git"), RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repoA, ".git")), plain.Path);

        // With FROM_ENV + empty start, GIT_DIR wins.
        await using GitRepository fromEnv = await GitRepository.OpenExtAsync(string.Empty, RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repoB, ".git")), fromEnv.Path);
    }

    [Fact]
    public async Task EmptyDotGitDirectory_IsNotARepository()
    {
        using GitContext ctx = new();
        string dir = Path.Combine(NewDir("int-empty"), "a", "b");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenExtAsync(dir, RepositoryOpenFlags.None, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Gitfile_PointsAtRealRepo()
    {
        using GitContext ctx = new();
        string wd = NewDir("int-wd");
        string repo = NewDir("int-repo2");
        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wd, ".git"), $"gitdir: {Path.Combine(repo, ".git")}\n", TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenExtAsync(wd, RepositoryOpenFlags.None, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repo, ".git")), opened.Path);
    }

    [Fact]
    public async Task Gitfile_Malformed_Aborts()
    {
        using GitContext ctx = new();
        string wd = NewDir("int-wd2");
        string repo = NewDir("int-repo3");
        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wd, ".git"), "gitdir: ", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenExtAsync(wd, RepositoryOpenFlags.None, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("is malformed", ex.Message);
    }

    [Fact]
    public async Task NoReinit_OnExistingRepo_ThrowsExists()
    {
        using GitContext ctx = new();
        string repo = NewDir("int-noreinit");
        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.InitExtAsync(repo, new GitRepositoryInitOptions { Flags = GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.NoReinit }, ctx, TestContext.Current.CancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Exists, ex.Code);
        Assert.Contains("attempt to reinitialize", ex.Message);
    }

    [Fact]
    public async Task Reinit_KeepsCustomHead()
    {
        using GitContext ctx = new();
        string repo = NewDir("int-reinit");
        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/custom\n", TestContext.Current.CancellationToken);

        _ = await GitRepository.InitExtAsync(repo, new GitRepositoryInitOptions { Flags = GitRepositoryInitFlags.Mkpath }, ctx, TestContext.Current.CancellationToken);

        Assert.Equal("ref: refs/heads/custom\n", await File.ReadAllTextAsync(Path.Combine(repo, ".git", "HEAD"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Init_OnDotGitPath_NoNestedDotGit()
    {
        using GitContext ctx = new();
        string base2 = NewDir("int-dotgit");
        string gitPath = Path.Combine(base2, ".git");
        Directory.CreateDirectory(gitPath);

        await using GitRepository repo = await GitRepository.InitAsync(gitPath, isBare: false, ctx, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(gitPath, ".git")));
        Assert.True(File.Exists(Path.Combine(gitPath, "HEAD")));
    }

    [Fact]
    public async Task InitConfig_KeyOrder_MatchesC()
    {
        using GitContext ctx = new();
        string repo = NewDir("int-order");
        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);

        string config = await File.ReadAllTextAsync(Path.Combine(repo, ".git", "config"), TestContext.Current.CancellationToken);
        Assert.True(config.IndexOf("bare", StringComparison.Ordinal) < config.IndexOf("repositoryformatversion", StringComparison.Ordinal));
        Assert.True(config.IndexOf("repositoryformatversion", StringComparison.Ordinal) < config.IndexOf("filemode", StringComparison.Ordinal));
        Assert.True(config.IndexOf("filemode", StringComparison.Ordinal) < config.IndexOf("logallrefupdates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitBranchEnv_Ignored()
    {
        using GitContext ctx = new();
        ctx.Env["GIT_INIT_BRANCH"] = "main";
        string repo = NewDir("int-branch");

        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);

        Assert.Equal("ref: refs/heads/master\n", await File.ReadAllTextAsync(Path.Combine(repo, ".git", "HEAD"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Gitlink_IsAbsoluteByDefault_RelativeWithFlag()
    {
        using GitContext ctx = new();
        string gitDir = NewDir("int-gd");
        string workDir = NewDir("int-wk");

        _ = await GitRepository.InitExtAsync(gitDir, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.NoDotgitDir,
            WorkdirPath = workDir,
        }, ctx, TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(Path.Combine(workDir, ".git"), TestContext.Current.CancellationToken);
        Assert.True(Path.IsPathRooted(content["gitdir: ".Length..].Trim()), $"gitlink should be absolute: {content.Trim()}");

        string gitDir2 = NewDir("int-gd2");
        string workDir2 = NewDir("int-wk2");
        _ = await GitRepository.InitExtAsync(gitDir2, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.NoDotgitDir | GitRepositoryInitFlags.RelativeGitlink,
            WorkdirPath = workDir2,
        }, ctx, TestContext.Current.CancellationToken);

        string content2 = await File.ReadAllTextAsync(Path.Combine(workDir2, ".git"), TestContext.Current.CancellationToken);
        Assert.False(Path.IsPathRooted(content2["gitdir: ".Length..].Trim()), $"relative gitlink expected: {content2.Trim()}");
    }

    [Fact]
    public async Task ExternalTemplate_CopiesAndSkipsInternal()
    {
        using GitContext ctx = new();
        string tpl = NewDir("int-tpl");
        await File.WriteAllTextAsync(Path.Combine(tpl, "custom-file"), "x\n", TestContext.Current.CancellationToken);
        string repo = NewDir("int-tpl-repo");

        _ = await GitRepository.InitExtAsync(repo, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.ExternalTemplate,
            TemplatePath = tpl,
        }, ctx, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(repo, ".git", "custom-file")));
        Assert.False(File.Exists(Path.Combine(repo, ".git", "description")));
    }

    [Fact]
    public async Task SetWorkdir_UpdatesGitlinkAndConfig()
    {
        using GitContext ctx = new();
        string repo = NewDir("int-setwd");
        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);
        string newWd = NewDir("int-setwd-new");

        await using (GitRepository pre = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken))
        {
            await pre.SetWorkdirAsync(newWd, updateGitlink: true, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        Assert.True(File.Exists(Path.Combine(newWd, ".git")));
        await using GitRepository reopened = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken);
        string? worktree = await reopened.Config.GetStringAsync("core.worktree", TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(PathHelpers.PrettifyDir(newWd), worktree);
        Assert.False(reopened.IsBare);
    }

    [Fact]
    public async Task LinkedWorktreeGitdir_OpensViaCommondir()
    {
        using GitContext ctx = new();
        string repo = NewDir("int-wt");
        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);

        string wtGitDir = Path.Combine(repo, ".git", "worktrees", "wt");
        Directory.CreateDirectory(wtGitDir);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "HEAD"), "ref: refs/heads/master\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "commondir"), "../..\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "gitdir"), Path.Combine(repo, "wt") + "/\n", TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenExtAsync(wtGitDir, RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PathHelpers.PrettifyDir(wtGitDir), opened.Path);
        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repo, ".git")), opened.CommonDir);
        Assert.True(opened.IsWorktree);
    }

    [Fact]
    public async Task KnownExtensions_Open()
    {
        using GitContext ctx = new();
        string repo = NewDir("int-ext");
        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), "\n[core]\n\trepositoryformatversion = 1\n[extensions]\n\tworktreeconfig = true\n", TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.NotNull(opened);
    }
}
