using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.Core;

/// <summary> Parity tests for settings/env/util. </summary>
public sealed class SettingsEnvUtilLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsEnvUtilLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SettingsLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── empty-but-SET XDG_CONFIG_HOME derives a relative "git" path ───

    [Fact]
    public void XdgConfigHome_EmptyButSet_DerivesRelativeGitPath()
    {
        // C (sysdir.c:401-410): git__getenv distinguishes SET (even to "")
        // from unset — XDG_CONFIG_HOME="" yields the RELATIVE path "git",
        // not a fallback to $HOME/.config/git.
        var env = new GitEnvironment();
        env["HOME"] = "/home/user";
        env["XDG_CONFIG_HOME"] = "";
        var dirs = new GitSystemDirs(env);

        Assert.Equal("git", dirs.Get(GitSystemDir.Xdg));
    }

    [Fact]
    public void XdgConfigHome_Unset_FallsBackToHome()
    {
        var env = new GitEnvironment();
        env["HOME"] = "/home/user";
        env["XDG_CONFIG_HOME"] = null; // unset
        var dirs = new GitSystemDirs(env);

        Assert.Equal("/home/user/.config/git", dirs.Get(GitSystemDir.Xdg));
    }

    [Fact]
    public void XdgConfigHome_SetToRealPath_UsedVerbatim()
    {
        var env = new GitEnvironment();
        env["HOME"] = "/home/user";
        env["XDG_CONFIG_HOME"] = "/custom/xdg";
        var dirs = new GitSystemDirs(env);

        Assert.Equal("/custom/xdg/git", dirs.Get(GitSystemDir.Xdg));
    }

    // ── search-path splitting honors the backslash-escaped separator ─

    [Fact]
    public async Task FindXdgFile_EscapedSeparator_KeepsEntryWithBackslash()
    {
        // C (sysdir.c:548-590): the dirlist is split on the path-list
        // separator only when the separator is NOT preceded by a backslash —
        // an escaped separator stays inside the entry (backslash retained),
        // so a directory whose name literally contains "a\<sep>" is searched.
        // The separator is ';' on Windows and ':' elsewhere
        // (GIT_PATH_LIST_SEPARATOR). A literal ':' is invalid in a Windows
        // directory name, so this test only runs on POSIX.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string dirA = Path.Combine(_tempDir, "a\\:"); // literal backslash + colon
        string dirB = Path.Combine(_tempDir, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        await File.WriteAllTextAsync(Path.Combine(dirA, "config"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dirB, "other"), "y\n", cancellationToken: TestContext.Current.CancellationToken);

        var dirs = new GitSystemDirs(new GitEnvironment());
        dirs.Set(GitSystemDir.Xdg, dirA + ":" + dirB);

        string? found = dirs.FindXdgFile("config");
        Assert.Equal(Path.Combine(dirA, "config"), found);
    }

    [Fact]
    public async Task FindXdgFile_PlainSeparators_StillSplit()
    {
        // Regression guard for an unescaped separator still splits
        // entries. The separator is ';' on Windows and ':' elsewhere
        // (GIT_PATH_LIST_SEPARATOR).
        string sep = OperatingSystem.IsWindows() ? ";" : ":";
        string dirA = Path.Combine(_tempDir, "a");
        string dirB = Path.Combine(_tempDir, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        await File.WriteAllTextAsync(Path.Combine(dirB, "config"), "x\n", cancellationToken: TestContext.Current.CancellationToken);

        var dirs = new GitSystemDirs(new GitEnvironment());
        dirs.Set(GitSystemDir.Xdg, dirA + sep + dirB);

        // C (sysdir.c:548-590): the candidate is joined with
        // git_str_joinpath, which always uses '/' as the separator.
        string? found = dirs.FindXdgFile("config");
        Assert.Equal(dirB + "/config", found);
    }

    // ── LibraryInit.Features matches the reference build ─────────────

    [Fact]
    public void Features_MatchReferenceBuild()
    {
        // C build: THREADS | HTTP_PARSER | REGEX | COMPRESSION | SHA1 | NSEC — no SHA256 (GIT_EXPERIMENTAL_ SHA256 off) and no SSH (no libssh2 in this build).
        LibraryFeatures expected =
            LibraryFeatures.Threads |
            LibraryFeatures.HttpParser |
            LibraryFeatures.Regex |
            LibraryFeatures.Compression |
            LibraryFeatures.Sha1 |
            LibraryFeatures.Nsec;
        Assert.Equal(expected, LibraryInit.Features);

        const LibraryFeatures absent =
            LibraryFeatures.Sha256 |
            LibraryFeatures.Ssh |
            LibraryFeatures.Https |
            LibraryFeatures.I18n |
            LibraryFeatures.AuthNtlm |
            LibraryFeatures.AuthNegotiate;
        Assert.Equal((LibraryFeatures)0, LibraryInit.Features & absent);
    }
}
