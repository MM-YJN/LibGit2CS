using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

/// <summary>
/// Regression tests for the repository open/discover/init parity behaviors in
/// libgit2 1.9.4. Expected behaviors were verified against
/// the C reference (libgit2 1.9.4, repository.c).
/// </summary>
public sealed class RepositoryParityTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepoParity_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static async ValueTask<GitRepository> InitAsync(string path, GitContext ctx)
        => await GitRepository.InitAsync(path, isBare: false, ctx);

    // ── git_repository_open is NO_SEARCH (no upward walk) ──

    [Fact]
    public async Task OpenAsyncFromSubdirectory_ThrowsNotFound()
    {
        using GitContext ctx = new();
        string repo = NewDir("open-subdir");
        _ = await InitAsync(repo, ctx);
        string sub = Path.Combine(repo, "sub", "deep");
        Directory.CreateDirectory(sub);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenAsync(sub, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal($"could not find repository at '{sub}'", ex.Message);
    }

    [Fact]
    public async Task OpenExtWithNoFlags_SearchesUpward()
    {
        using GitContext ctx = new();
        string repo = NewDir("open-noflags");
        _ = await InitAsync(repo, ctx);
        string sub = Path.Combine(repo, "sub", "deep");
        Directory.CreateDirectory(sub);

        await using GitRepository opened = await GitRepository.OpenExtAsync(sub, RepositoryOpenFlags.None, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repo, ".git")), opened.Path);
    }

    // ── git_repository_discover walks upward ──

    [Fact]
    public async Task DiscoverFromSubdirectory_FindsRepo()
    {
        using GitContext ctx = new();
        string repo = NewDir("discover-subdir");
        _ = await InitAsync(repo, ctx);
        string sub = Path.Combine(repo, "sub", "deep");
        Directory.CreateDirectory(sub);

        string gitDir = await GitRepository.DiscoverAsync(sub, acrossFs: false, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repo, ".git")), gitDir);
    }

    // ── GIT_DIR is gated on FROM_ENV ──

    [Fact]
    public async Task GitDirEnv_IgnoredWithoutFromEnv()
    {
        using GitContext ctx = new();
        string repoA = NewDir("gitdir-env-off-a");
        string repoB = NewDir("gitdir-env-off-b");
        _ = await InitAsync(repoA, ctx);
        _ = await InitAsync(repoB, ctx);
        ctx.Env["GIT_DIR"] = repoB;

        // Without FROM_ENV, C never consults GIT_DIR: opening repoA's gitdir
        // must yield repoA even though GIT_DIR points at repoB.
        await using GitRepository opened = await GitRepository.OpenExtAsync(Path.Combine(repoA, ".git"), RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repoA, ".git")), opened.Path);
    }

    [Fact]
    public async Task GitDirEnv_HonoredWithFromEnv()
    {
        using GitContext ctx = new();
        string repoA = NewDir("gitdir-env-on-a");
        string repoB = NewDir("gitdir-env-on-b");
        _ = await InitAsync(repoA, ctx);
        _ = await InitAsync(repoB, ctx);
        ctx.Env["GIT_DIR"] = Path.Combine(repoB, ".git");

        // C (repository.c:920-929): FROM_ENV + no start path → GIT_DIR, with
        // NO_SEARCH|NO_DOTGIT forced.
        await using GitRepository opened = await GitRepository.OpenExtAsync(string.Empty, RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PathHelpers.PrettifyDir(Path.Combine(repoB, ".git")), opened.Path);
    }

    // ── GIT_WORK_TREE / GIT_NAMESPACE gated on FROM_ENV ──

    [Fact]
    public async Task WorkTreeEnv_IgnoredWithoutFromEnv()
    {
        using GitContext ctx = new();
        string repo = NewDir("worktree-env-off");
        _ = await InitAsync(repo, ctx);
        string other = NewDir("worktree-env-off-other");
        ctx.Env["GIT_WORK_TREE"] = other;

        await using GitRepository opened = await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PathHelpers.PrettifyDir(repo), opened.Workdir);
    }

    [Fact]
    public async Task WorkTreeEnv_HonoredWithFromEnv()
    {
        using GitContext ctx = new();
        string repo = NewDir("worktree-env-on");
        _ = await InitAsync(repo, ctx);
        string other = NewDir("worktree-env-on-other");
        ctx.Env["GIT_WORK_TREE"] = other;

        await using GitRepository opened = await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PathHelpers.PrettifyDir(other), opened.Workdir);
    }

    [Fact]
    public async Task NamespaceEnv_IgnoredWithoutFromEnv()
    {
        using GitContext ctx = new();
        string repo = NewDir("namespace-env-off");
        _ = await InitAsync(repo, ctx);
        ctx.Env["GIT_NAMESPACE"] = "my-ns";

        await using GitRepository opened = await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.Null(opened.Namespace);
    }

    // ── an empty .git directory is not a repository ──

    [Fact]
    public async Task EmptyDotGitDirectory_NotARepository()
    {
        using GitContext ctx = new();
        string base2 = NewDir("empty-dotgit");
        string dir = Path.Combine(base2, "a", "b");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git")); // empty — invalid

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenExtAsync(dir, RepositoryOpenFlags.None, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── gitfile parsing ──

    [Fact]
    public async Task GitfileUpperCasePrefix_IsMalformed()
    {
        using GitContext ctx = new();
        string wd = NewDir("gitfile-upper");
        string repo = NewDir("gitfile-upper-repo");
        _ = await InitAsync(repo, ctx);
        await File.WriteAllTextAsync(Path.Combine(wd, ".git"), $"GITDIR: {Path.Combine(repo, ".git")}\n", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenExtAsync(wd, RepositoryOpenFlags.None, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("is malformed", ex.Message);
    }

    [Fact]
    public async Task GitfileMalformedContent_Aborts()
    {
        using GitContext ctx = new();
        string wd = NewDir("gitfile-malformed");
        string repo = NewDir("gitfile-malformed-repo");
        _ = await InitAsync(repo, ctx);
        await File.WriteAllTextAsync(Path.Combine(wd, ".git"), "not a gitdir line\n", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenExtAsync(wd, RepositoryOpenFlags.None, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task GitfilePrefixOnly_IsMalformed()
    {
        using GitContext ctx = new();
        string wd = NewDir("gitfile-prefix");
        await File.WriteAllTextAsync(Path.Combine(wd, ".git"), "gitdir:", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenExtAsync(wd, RepositoryOpenFlags.None, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task GitfilePointingAtNonRepo_NotFound()
    {
        using GitContext ctx = new();
        string wd = NewDir("gitfile-nonrepo");
        string notARepo = NewDir("gitfile-nonrepo-absent");
        await File.WriteAllTextAsync(Path.Combine(wd, ".git"), $"gitdir: {notARepo}\n", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenExtAsync(wd, RepositoryOpenFlags.None, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── OpenBareAsync fast path ──

    [Fact]
    public async Task OpenBareNonRepo_ThrowsNotFoundWithCMessage()
    {
        using GitContext ctx = new();
        string notARepo = NewDir("open-bare-nonrepo");

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await using GitRepository? _ = await GitRepository.OpenBareAsync(notARepo, ctx, TestContext.Current.CancellationToken);
            }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal($"path is not a repository: {notARepo}", ex.Message);
    }

    // ── linked-worktree gitdir opens via commondir ──

    [Fact]
    public async Task LinkedWorktreeGitdir_OpensDirectly()
    {
        using GitContext ctx = new();
        string repo = NewDir("linked-worktree-gitdir");
        _ = await InitAsync(repo, ctx);

        // Build a linked-worktree gitdir layout: HEAD + commondir file.
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
    public async Task WorktreeWithBareCommonConfig_IsNotBare()
    {
        using GitContext ctx = new();
        string repo = NewDir("bare-common-config");
        _ = await InitAsync(repo, ctx);

        string wtGitDir = Path.Combine(repo, ".git", "worktrees", "wt");
        Directory.CreateDirectory(wtGitDir);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "HEAD"), "ref: refs/heads/master\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "commondir"), "../..\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "gitdir"), Path.Combine(repo, "wt") + "/\n", TestContext.Current.CancellationToken);

        // core.bare = true in the COMMON config must not make the worktree bare.
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), "\n[core]\n\tbare = true\n", TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenExtAsync(wtGitDir, RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.False(opened.IsBare);
        Assert.NotNull(opened.Workdir);
    }

    // ── known extensions.* accepted ──

    [Fact]
    public async Task WorktreeConfigExtension_Opens()
    {
        using GitContext ctx = new();
        string repo = NewDir("worktree-config-ext");
        _ = await InitAsync(repo, ctx);
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), "\n[core]\n\trepositoryformatversion = 1\n[extensions]\n\tworktreeconfig = true\n", TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.NotNull(opened);
    }

    // ── negative repositoryformatversion accepted ──

    [Fact]
    public async Task NegativeRepositoryFormatVersion_Opens()
    {
        using GitContext ctx = new();
        string repo = NewDir("neg-format-version");
        _ = await InitAsync(repo, ctx);
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), "\n[core]\n\trepositoryformatversion = -1\n", TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenExtAsync(Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.NotNull(opened);
    }

    // ── config.worktree without the extension is silently skipped ──

    [Fact]
    public async Task WorktreeConfigWithoutExtension_Opens()
    {
        using GitContext ctx = new();
        string repo = NewDir("worktree-config-noext");
        _ = await InitAsync(repo, ctx);
        string wtGitDir = Path.Combine(repo, ".git", "worktrees", "wt");
        Directory.CreateDirectory(wtGitDir);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "HEAD"), "ref: refs/heads/master\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "commondir"), "../..\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "gitdir"), Path.Combine(repo, "wt") + "/\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "config.worktree"), "[core]\n\tbare = false\n", TestContext.Current.CancellationToken);

        // C silently ignores config.worktree when extensions.worktreeConfig is unset.
        await using GitRepository opened = await GitRepository.OpenExtAsync(wtGitDir, RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.NotNull(opened);
    }

    // ── a non-.git-named gitdir gets the parent workdir ──

    [Fact]
    public async Task CustomNamedGitdir_GetsParentWorkdir()
    {
        using GitContext ctx = new();
        string repo = NewDir("custom-gitdir-workdir");
        _ = await InitAsync(repo, ctx);

        // Copy the gitdir to a differently-named directory (valid HEAD/objects/refs).
        string custom = NewDir("f20-custom.git");
        foreach (string entry in Directory.EnumerateFileSystemEntries(Path.Combine(repo, ".git")))
        {
            string dest = Path.Combine(custom, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(dest);
                foreach (string sub in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(Path.Combine(repo, ".git"), sub);
                    string target = Path.Combine(custom, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(sub, target);
                }
            }
            else
            {
                File.Copy(entry, dest);
            }
        }

        await using GitRepository opened = await GitRepository.OpenExtAsync(custom, RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PathHelpers.PrettifyDir(PathHelpers.Dirname(custom)), opened.Workdir);
    }

    // ── NO_REINIT ──

    [Fact]
    public async Task NoReinitOnExistingRepo_ThrowsExists()
    {
        using GitContext ctx = new();
        string repo = NewDir("reinit-exists");
        _ = await InitAsync(repo, ctx);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => { await using GitRepository? _ = await GitRepository.InitExtAsync(repo, new GitRepositoryInitOptions { Flags = GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.NoReinit }, ctx, TestContext.Current.CancellationToken).ConfigureAwait(false); }).ConfigureAwait(false);

        Assert.Equal(GitErrorCode.Exists, ex.Code);
        Assert.Contains("attempt to reinitialize", ex.Message);
    }

    [Fact]
    public async Task Reinit_KeepsExistingHead()
    {
        using GitContext ctx = new();
        string repo = NewDir("reinit-head");
        _ = await InitAsync(repo, ctx);
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/custom\n", TestContext.Current.CancellationToken);

        _ = await GitRepository.InitExtAsync(repo, new GitRepositoryInitOptions { Flags = GitRepositoryInitFlags.Mkpath }, ctx, TestContext.Current.CancellationToken);

        Assert.Equal("ref: refs/heads/custom\n", await File.ReadAllTextAsync(Path.Combine(repo, ".git", "HEAD"), TestContext.Current.CancellationToken));
    }

    // ── init on a path ending in /.git ──

    [Fact]
    public async Task InitOnDotGitSuffixedPath_NoNestedDotGit()
    {
        using GitContext ctx = new();
        string base2 = NewDir("init-dotgit-suffix");
        string gitPath = Path.Combine(base2, ".git");
        Directory.CreateDirectory(gitPath);

        await using GitRepository repo = await GitRepository.InitAsync(gitPath, isBare: false, ctx, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(gitPath, ".git")));
        Assert.True(File.Exists(Path.Combine(gitPath, "HEAD")));
        Assert.Equal(PathHelpers.PrettifyDir(gitPath), repo.Path);
    }

    // ── init config key order ──

    [Fact]
    public async Task InitConfig_KeyOrderMatchesC()
    {
        using GitContext ctx = new();
        string repo = NewDir("init-config-order");
        _ = await InitAsync(repo, ctx);

        string config = await File.ReadAllTextAsync(Path.Combine(repo, ".git", "config"), TestContext.Current.CancellationToken);
        int bare = config.IndexOf("bare", StringComparison.Ordinal);
        int fmt = config.IndexOf("repositoryformatversion", StringComparison.Ordinal);
        int filemode = config.IndexOf("filemode", StringComparison.Ordinal);

        Assert.True(bare >= 0 && fmt >= 0 && filemode >= 0);
        Assert.True(bare < fmt, "core.bare must precede core.repositoryformatversion");
        Assert.True(fmt < filemode, "core.repositoryformatversion must precede core.filemode");
    }

    // ── GIT_INIT_BRANCH is not a libgit2 env var ──

    [Fact]
    public async Task InitBranchEnv_Ignored()
    {
        using GitContext ctx = new();
        ctx.Env["GIT_INIT_BRANCH"] = "main";
        string repo = NewDir("init-branch-env");

        _ = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);

        Assert.Equal("ref: refs/heads/master\n", await File.ReadAllTextAsync(Path.Combine(repo, ".git", "HEAD"), TestContext.Current.CancellationToken));
    }

    // ── gitlink content is absolute unless RELATIVE_GITLINK ──

    [Fact]
    public async Task Gitlink_IsAbsoluteByDefault()
    {
        using GitContext ctx = new();
        string gitDir = NewDir("gitlink-absolute-gitdir");
        string workDir = NewDir("gitlink-absolute-workdir");

        _ = await GitRepository.InitExtAsync(gitDir, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.NoDotgitDir,
            WorkdirPath = workDir,
        }, ctx, TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(Path.Combine(workDir, ".git"), TestContext.Current.CancellationToken);
        Assert.StartsWith("gitdir: ", content, StringComparison.Ordinal);
        string target = content["gitdir: ".Length..].Trim();
        Assert.True(Path.IsPathRooted(target), $"gitlink must be absolute, was: {content.Trim()}");
        Assert.Equal(PathHelpers.PrettifyDir(gitDir), PathHelpers.PrettifyDir(target));
    }

    // ── external template copy ──

    [Fact]
    public async Task ExternalTemplate_CopiesFilesAndSkipsInternal()
    {
        using GitContext ctx = new();
        string tpl = NewDir("external-template-tpl");
        await File.WriteAllTextAsync(Path.Combine(tpl, "custom-file"), "x\n", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(tpl, "hooks"));
        await File.WriteAllTextAsync(Path.Combine(tpl, "hooks", "sample.sh"), "#!/bin/sh\n", TestContext.Current.CancellationToken);
        string repo = NewDir("external-template-repo");

        _ = await GitRepository.InitExtAsync(repo, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath | GitRepositoryInitFlags.ExternalTemplate,
            TemplatePath = tpl,
        }, ctx, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(repo, ".git", "custom-file")));
        Assert.True(File.Exists(Path.Combine(repo, ".git", "hooks", "sample.sh")));
        // C skips the internal template files when an external template is used.
        Assert.False(File.Exists(Path.Combine(repo, ".git", "description")));
    }

    // ── set_workdir ──

    [Fact]
    public async Task SetWorkdir_UpdatesGitlinkAndConfig()
    {
        using GitContext ctx = new();
        string repo = NewDir("setworkdir-repo");
        _ = await InitAsync(repo, ctx);
        string newWd = NewDir("setworkdir-new-wd");

        await using (GitRepository pre = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken))
        {
            await pre.SetWorkdirAsync(newWd, updateGitlink: true, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        // The .git gitlink file is written into the new workdir.
        Assert.True(File.Exists(Path.Combine(newWd, ".git")));
        string gitlink = await File.ReadAllTextAsync(Path.Combine(newWd, ".git"), TestContext.Current.CancellationToken);
        Assert.StartsWith("gitdir: ", gitlink, StringComparison.Ordinal);

        // core.worktree is set to the ABSOLUTE prettified path; core.bare = false.
        await using GitRepository reopened = await GitRepository.OpenAsync(Path.Combine(repo, ".git"), ctx, TestContext.Current.CancellationToken);
        string? worktree = await reopened.Config.GetStringAsync("core.worktree", TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(PathHelpers.PrettifyDir(newWd), worktree);
        Assert.False(reopened.IsBare);
    }
}
