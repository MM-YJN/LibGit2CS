using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Config;

/// <summary>
/// Integration tests for the settings/env/util parity behavior in
/// libgit2 1.9.4: the GLOBAL
/// config backend is registered even when <c>~/.gitconfig</c> does not
/// exist, and the repo LOCAL backend is registered even when the repo's
/// <c>config</c> file is missing.
///
/// C: <c>git_config_open_default</c> falls back to
/// <c>git_config__global_location</c> (config.c:1305-1311) and
/// <c>config_file_open</c> opens nonexistent files as empty backends
/// (config_file.c:103-117); the repo <c>load_config</c> adds the LOCAL
/// backend unconditionally (repository.c:1279-1286), not gated
/// on file existence.
/// </summary>
public sealed class ConfigGlobalBackendParityIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigGlobalInt_" + Guid.NewGuid().ToString("N")[..8]);

    public ConfigGlobalBackendParityIntegrationTests()
    {
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

    private GitContext CreateContextWithHome(string homeDir)
    {
        GitContext ctx = new();
        ctx.Env["HOME"] = homeDir;
        ctx.Env["XDG_CONFIG_HOME"] = Path.Combine(_tempDir, "xdg_nonexistent");
        ctx.Dirs.Reset();
        return ctx;
    }

    /// <summary>
    /// End-to-end: a default config written with no <c>~/.gitconfig</c>
    /// present creates the file, and a repository opened with the same
    /// context then sees the global value in its merged config view (the
    /// repo path registers the now-existing global backend).
    /// </summary>
    [Fact]
    public async Task DefaultConfigWrite_CreatesGlobalFile_RepoSeesGlobalValue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string homeDir = Path.Combine(_tempDir, "home");
        string repoPath = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(homeDir);
        try
        {
            using (GitContext ctx = CreateContextWithHome(homeDir))
            {
                await using GitConfiguration config = await GitConfiguration.OpenDefaultAsync(ctx, ct);
                await config.SetStringAsync("user.name", "tester", ct);
            }

            string globalPath = Path.Combine(homeDir, ".gitconfig");
            Assert.True(File.Exists(globalPath), $"expected {globalPath} to be created");

            using (GitContext ctx = CreateContextWithHome(homeDir))
            {
                await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, ctx, cancellationToken: ct);

                // The repo's merged config view includes the global value.
                string? name = await repo.Config.GetStringAsync("user.name", ct);
                Assert.Equal("tester", name);

                // The GLOBAL level is present (openable).
                await using GitConfiguration global = await repo.Config.OpenLevelAsync(GitConfigLevel.Global, ct);
                Assert.Equal("tester", await global.GetStringAsync("user.name", ct));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }

            try
            {
                Directory.Delete(homeDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A repo whose <c>.git/config</c> file has been deleted still opens
    /// with a LOCAL backend (empty) — C's load_config adds it
    /// unconditionally.
    /// <c>OpenLevelAsync(Local)</c> threw NotFound.
    /// </summary>
    [Fact]
    public async Task RepoOpen_MissingLocalConfigFile_StillRegistersLocalBackend()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string homeDir = Path.Combine(_tempDir, "home2");
        string repoPath = Path.Combine(_tempDir, "repo2");
        Directory.CreateDirectory(homeDir);
        try
        {
            using (GitContext ctx = CreateContextWithHome(homeDir))
            {
                await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, ctx, cancellationToken: ct);
                File.Delete(Path.Combine(repoPath, ".git", "config"));
            }

            using GitContext ctx2 = CreateContextWithHome(homeDir);
            await using GitRepository repo2 = await GitRepository.OpenAsync(repoPath, ctx2, cancellationToken: ct);

            await using GitConfiguration local = await repo2.Config.OpenLevelAsync(GitConfigLevel.Local, ct);
            Assert.Null(await local.GetStringAsync("core.repositoryformatversion", ct));
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }

            try
            {
                Directory.Delete(homeDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
