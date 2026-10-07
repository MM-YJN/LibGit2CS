using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Config;

/// <summary>
/// Regression tests for the settings/env/util parity behavior in
/// libgit2 1.9.4: the GLOBAL
/// config backend is registered even when <c>~/.gitconfig</c> does not
/// exist.
/// C (config.c:1305-1311): <c>git_config_open_default</c> adds the GLOBAL
/// backend whenever <c>git_config__find_global</c> finds an existing file
/// OR <c>git_config__global_location</c> resolves <c>$HOME/.gitconfig</c>
/// (the GLOBAL sysdir is non-empty), and <c>config_file_open</c>
/// (config_file.c:103-117) opens a nonexistent file as an empty backend,
/// so writes create <c>~/.gitconfig</c> where needed.
/// </summary>
public sealed class ConfigGlobalBackendParityTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigGlobal_" + Guid.NewGuid().ToString("N")[..8]);

    public ConfigGlobalBackendParityTests()
    {
        Directory.CreateDirectory(_homeDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_homeDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A context whose HOME is the (existing, .gitconfig-less) temp dir,
    /// with the XDG/system levels neutralized.
    /// </summary>
    private GitContext CreateIsolatedContext()
    {
        GitContext ctx = new();
        ctx.Env["HOME"] = _homeDir;
        ctx.Env["XDG_CONFIG_HOME"] = Path.Combine(Path.GetTempPath(), "LibGit2CS_xdg_nonexistent_" + Guid.NewGuid().ToString("N"));
        ctx.Dirs.Reset();
        return ctx;
    }

    [Fact]
    public async Task OpenDefault_NoGlobalFile_RegistersEmptyGlobalBackend()
    {
        using GitContext ctx = CreateIsolatedContext();
        await using GitConfiguration config = await GitConfiguration.OpenDefaultAsync(ctx, TestContext.Current.CancellationToken);

        // C: git_config_open_level(GLOBAL) succeeds with an empty backend.
        await using GitConfiguration global = await config.OpenLevelAsync(GitConfigLevel.Global, TestContext.Current.CancellationToken);

        string? name = await global.GetStringAsync("user.name", TestContext.Current.CancellationToken);
        Assert.Null(name);
    }

    [Fact]
    public async Task OpenDefault_SetString_CreatesGitconfigFile()
    {
        using GitContext ctx = CreateIsolatedContext();
        await using GitConfiguration config = await GitConfiguration.OpenDefaultAsync(ctx, TestContext.Current.CancellationToken);

        // C: git_config_set_string on the default config creates ~/.gitconfig
        // (the GLOBAL backend is writable).
        await config.SetStringAsync("user.name", "tester", TestContext.Current.CancellationToken);

        string path = Path.Combine(_homeDir, ".gitconfig");
        Assert.True(File.Exists(path), $"expected {path} to be created");
        string content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("[user]", content);
        Assert.Contains("name = tester", content);

        // And the value is now readable back through the default config.
        string? name = await config.GetStringAsync("user.name", TestContext.Current.CancellationToken);
        Assert.Equal("tester", name);
    }

    [Fact]
    public async Task OpenDefault_HomeUnset_NoGlobalBackend()
    {
        using GitContext ctx = new();
        ctx.Env["HOME"] = null;
        ctx.Env["XDG_CONFIG_HOME"] = null;
        if (OperatingSystem.IsWindows())
        {
            // C (sysdir.c, git_sysdir_guess_home_dirs): on Windows the home
            // dirlist falls back through %HOMEDRIVE%%HOMEPATH% and
            // %USERPROFILE% — neutralize those too so the GLOBAL sysdir is
            // empty like the POSIX HOME-unset case.
            ctx.Env["HOMEDRIVE"] = null;
            ctx.Env["HOMEPATH"] = null;
            ctx.Env["USERPROFILE"] = null;
        }

        ctx.Dirs.Reset();

        await using GitConfiguration config = await GitConfiguration.OpenDefaultAsync(ctx, TestContext.Current.CancellationToken);

        // C: git_config__global_location fails when the GLOBAL sysdir is
        // empty → no GLOBAL backend; OpenLevel(GLOBAL) errors with NotFound.
        await Assert.ThrowsAsync<GitException>(() => config.OpenLevelAsync(GitConfigLevel.Global, TestContext.Current.CancellationToken).AsTask());
    }
}
