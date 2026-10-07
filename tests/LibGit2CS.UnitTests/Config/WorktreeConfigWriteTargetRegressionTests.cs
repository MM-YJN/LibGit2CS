using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Config;

// FindWritableBackend
// returned the first file backend in the level-descending readers list, so
// a repo with extensions.worktreeconfig wrote to gitdir/config.worktree
// (WORKTREE level). C's load_config calls
// git_config_set_writeorder(cfg, &write_order /* LOCAL */, 1)
// (repository.c:1342-1344), giving every other backend write_order -1 —
// get_writer_instance (config.c:663-675) always returns LOCAL, so writes
// target commondir/config.
public sealed class WorktreeConfigWriteTargetRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public WorktreeConfigWriteTargetRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_WtConfig_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task SetString_WithWorktreeConfig_WritesLocalFile()
    {
        using GitContext ctx = new();
        string repo = Path.Combine(_tempDir, "repo");
        GitRepository init = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);
        await init.DisposeAsync();

        await File.AppendAllTextAsync(
            Path.Combine(repo, ".git", "config"),
            "\n[extensions]\n\tworktreeconfig = true\n",
            TestContext.Current.CancellationToken);

        string wtGitDir = Path.Combine(repo, ".git", "worktrees", "wt");
        Directory.CreateDirectory(wtGitDir);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "HEAD"), "ref: refs/heads/master\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "commondir"), "../..\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "gitdir"), Path.Combine(repo, "wt") + "/\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "config.worktree"), "[core]\n\tbare = false\n", TestContext.Current.CancellationToken);

        await using GitRepository opened = await GitRepository.OpenExtAsync(
            wtGitDir, RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        await opened.Config.SetStringAsync("testsection.section.value", "from-wt", TestContext.Current.CancellationToken);

        // C writes to commondir/config (LOCAL is the only writable backend) —
        // never to the worktree file.
        string localConfig = await File.ReadAllTextAsync(
            Path.Combine(repo, ".git", "config"), TestContext.Current.CancellationToken);
        string wtConfig = await File.ReadAllTextAsync(
            Path.Combine(wtGitDir, "config.worktree"), TestContext.Current.CancellationToken);

        Assert.Contains("from-wt", localConfig);
        Assert.DoesNotContain("from-wt", wtConfig);
    }

    [Fact]
    public async Task SetString_WithoutWorktreeConfig_StillWritesLocalFile()
    {
        // Control: without the extension there is no WORKTREE backend at all.
        using GitContext ctx = new();
        string repo = Path.Combine(_tempDir, "repo2");
        GitRepository init = await GitRepository.InitAsync(repo, isBare: false, ctx, TestContext.Current.CancellationToken);
        await init.DisposeAsync();

        await using GitRepository opened = await GitRepository.OpenExtAsync(
            Path.Combine(repo, ".git"), RepositoryOpenFlags.NoSearch, ceilingDirs: null, ctx, TestContext.Current.CancellationToken);

        await opened.Config.SetStringAsync("testsection.section.value", "from-main", TestContext.Current.CancellationToken);

        string localConfig = await File.ReadAllTextAsync(
            Path.Combine(repo, ".git", "config"), TestContext.Current.CancellationToken);
        Assert.Contains("from-main", localConfig);
    }
}
