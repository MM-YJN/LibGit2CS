using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Core;

/// <summary> Parity tests for settings/env/util. </summary>
public sealed class SettingsEnvUtilMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsEnvUtilMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SettingsMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string NewDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GitContext NewContext()
    {
        GitContext ctx = new();
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
        ctx.Dirs.Set(GitSystemDir.System, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        return ctx;
    }

    // ── GIT_CONFIG_GLOBAL / GIT_CONFIG_NOSYSTEM / GIT_CONFIG_SYSTEM ────

    [Fact]
    public async Task Open_FromEnv_GitConfigGlobal_ReplacesGlobalPath()
    {
        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, NewContext(), TestContext.Current.CancellationToken);

        // A custom global config file.
        string global = Path.Combine(_tempDir, "custom_global_" + Guid.NewGuid().ToString("N")[..8]);
        await File.WriteAllTextAsync(global, "[user]\n\tname = FromEnvGlobal\n", cancellationToken: TestContext.Current.CancellationToken);

        GitContext ctx = NewContext();
        ctx.Env["GIT_CONFIG_GLOBAL"] = global;
        ctx.Dirs.Reset();

        await using GitRepository reopened = await GitRepository.OpenExtAsync(
            Path.Combine(repoPath, ".git"),
            RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv,
            ceilingDirs: null,
            ctx,
            TestContext.Current.CancellationToken);

        // C (repository.c:1392-1397): GIT_CONFIG_GLOBAL replaces ~/.gitconfig.
        string? name = await reopened.Config.GetStringAsync("user.name", TestContext.Current.CancellationToken);
        Assert.Equal("FromEnvGlobal", name);
    }

    [Fact]
    public async Task Open_FromEnv_NoSystem_OmitsSystemLevel()
    {
        // Point the system dir at a gitconfig that sets a key; with
        // GIT_CONFIG_NOSYSTEM=1 the key must NOT be visible.
        string systemDir = Path.Combine(_tempDir, "sys_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(systemDir);
        await File.WriteAllTextAsync(Path.Combine(systemDir, "gitconfig"), "[user]\n\tname = FromSystem\n", cancellationToken: TestContext.Current.CancellationToken);

        string repoPath = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, NewContext(), TestContext.Current.CancellationToken);

        GitContext ctx = NewContext();
        ctx.Dirs.Set(GitSystemDir.System, systemDir);
        ctx.Env["GIT_CONFIG_NOSYSTEM"] = "1";
        ctx.Dirs.Reset();

        await using GitRepository reopened = await GitRepository.OpenExtAsync(
            Path.Combine(repoPath, ".git"),
            RepositoryOpenFlags.NoSearch | RepositoryOpenFlags.FromEnv,
            ceilingDirs: null,
            ctx,
            TestContext.Current.CancellationToken);

        string? name = await reopened.Config.GetStringAsync("user.name", TestContext.Current.CancellationToken);
        Assert.Null(name);
    }

    // ── ~ expansion — backslash rejected, missing HOME errors ─────────

    [Fact]
    public void ParsePath_BackslashAfterTilde_Throws()
    {
        // C (config.c:926-930): only '\0' or '/' may follow '~'.
        GitContext ctx = NewContext();
        ctx.Env["HOME"] = NewDir();
        ctx.Dirs.Reset();

        GitException ex = Assert.Throws<GitException>(() => ConfigurationValueParser.ParsePath("~\\foo", ctx.Dirs));
        Assert.Contains("retrieving a homedir by name is not supported", ex.Message);
    }

    [Fact]
    public void ParsePath_MissingHomeDir_ThrowsNotFound()
    {
        // C (sysdir.c:548-590): the home directory must EXIST —
        // "the home directory doesn't exist".
        GitContext ctx = NewContext();
        ctx.Env["HOME"] = Path.Combine(_tempDir, "does_not_exist_" + Guid.NewGuid().ToString("N")[..8]);
        ctx.Dirs.Reset();

        GitException ex = Assert.Throws<GitException>(() => ConfigurationValueParser.ParsePath("~/x", ctx.Dirs));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("the home directory doesn't exist", ex.Message);
    }

    // ── "~/" expands to the home dir (no trailing slash) ────

    [Fact]
    public void ParsePath_TildeSlash_ReturnsHomeWithoutTrailingSlash()
    {
        // C (config.c:926-933 + sysdir.c:640-650): "~/" passes an empty
        // filename to git_sysdir_expand_homedir_file, which returns the home
        // directory AS-IS (no trailing separator).
        GitContext ctx = NewContext();
        string home = NewDir();
        ctx.Env["HOME"] = home;
        ctx.Dirs.Reset();

        Assert.Equal(home, ConfigurationValueParser.ParsePath("~/", ctx.Dirs));
        Assert.Equal(home, ConfigurationValueParser.ParsePath("~", ctx.Dirs));
    }

    [Fact]
    public void ParsePath_TildeSlashFile_JoinsHome()
    {
        GitContext ctx = NewContext();
        string home = NewDir();
        ctx.Env["HOME"] = home;
        ctx.Dirs.Reset();

        // C (config.c:926-933 + sysdir.c:640-650): the filename is joined
        // with git_str_joinpath, which always uses '/' as the separator —
        // even on Windows the result is home + "/x" (forward slash).
        Assert.Equal(home + "/x", ConfigurationValueParser.ParsePath("~/x", ctx.Dirs));
    }

    // ── $PATH magic in GitSystemDirs.Set ───────────────────────────────

    [Fact]
    public void Set_PathMagic_ExpandsToCurrentValue()
    {
        GitContext ctx = NewContext();
        ctx.Dirs.Set(GitSystemDir.System, "/etc");

        // C (sysdir.c:504-546): "$PATH" is replaced by the CURRENT value.
        // The path-list separator is ';' on Windows and ':' elsewhere
        // (GIT_PATH_LIST_SEPARATOR).
        string sep = OperatingSystem.IsWindows() ? ";" : ":";
        ctx.Dirs.Set(GitSystemDir.System, "/a" + sep + "$PATH" + sep + "/b");
        Assert.Equal("/a" + sep + "/etc" + sep + "/b", ctx.Dirs.Get(GitSystemDir.System));

        // Plain set without magic.
        ctx.Dirs.Set(GitSystemDir.System, "/x");
        Assert.Equal("/x", ctx.Dirs.Get(GitSystemDir.System));

        // Null resets to the guess.
        ctx.Dirs.Set(GitSystemDir.System, null);
        Assert.NotEqual("/x", ctx.Dirs.Get(GitSystemDir.System));
    }

    // ── signature from env — date + EMAIL fallback + committer ─────────

    [Fact]
    public async Task DefaultFromEnv_AuthorDate_Parsed()
    {
        using GitContext ctx = new();
        ctx.Env["GIT_AUTHOR_NAME"] = "A";
        ctx.Env["GIT_AUTHOR_EMAIL"] = "a@b.c";
        ctx.Env["GIT_COMMITTER_NAME"] = "C";
        ctx.Env["GIT_COMMITTER_EMAIL"] = "c@b.c";
        ctx.Env["GIT_AUTHOR_DATE"] = "@1461698037 +0200";
        ctx.Env["GIT_COMMITTER_DATE"] = "@1461698037 +0200";

        (GitSignature author, GitSignature committer) = await GitSignature.DefaultFromEnvAsync(config: null, ctx, cancellationToken: TestContext.Current.CancellationToken);

        // C (signature.c:254-266): the date env var is parsed via
        // git_date_offset_parse.
        Assert.Equal(1461698037L, author.When.Seconds);
        Assert.Equal(120, author.When.OffsetMinutes);
        Assert.Equal(1461698037L, committer.When.Seconds);
    }

    [Fact]
    public async Task DefaultFromEnv_EmailFallback_Used()
    {
        using GitContext ctx = new();
        ctx.Env["GIT_AUTHOR_NAME"] = "A";
        ctx.Env["GIT_AUTHOR_EMAIL"] = null;
        ctx.Env["GIT_COMMITTER_NAME"] = "C";
        ctx.Env["GIT_COMMITTER_EMAIL"] = null;
        ctx.Env["EMAIL"] = "fallback@mail.com";

        // C (signature.c:244-252): when user.email is unset, EMAIL is used.
        (GitSignature author, GitSignature committer) = await GitSignature.DefaultFromEnvAsync(config: null, ctx, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("fallback@mail.com", author.Email);
        Assert.Equal("fallback@mail.com", committer.Email);
    }

    // ── user-agent defaults ────────────────────────────────────────────

    [Fact]
    public void UserAgent_Defaults_MatchC()
    {
        using GitContext ctx = new();
        // C (settings.c:97-104): "git/2.0" product + "libgit2 1.9.4" comment.
        Assert.Equal("git/2.0", ctx.Settings.UserAgentProduct);
        Assert.Equal("libgit2 1.9.4", ctx.Settings.UserAgentComment);
    }
}
