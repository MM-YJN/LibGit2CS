using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Config;

/// <summary> Parity tests for config/attr/filter/status config-side items: (refresh checksum over raw bytes), (onbranch: HEAD ASCII-only
/// rtrim), (core.excludesfile ~name is a literal path). Expectations are C-verified against libgit2 1.9.4 (config_file.c:894-896, 683; attrcache.c:338-343).
/// </summary>
public sealed class ConfigLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── refresh checksum over RAW bytes (BOM included) ─────────

    [Fact]
    public async Task Refresh_BomFile_ChecksumOverRawBytes()
    {
        // C (config_file.c:894-896): the refresh checksum hashes the raw file bytes; IsModifiedAsync (config_file.c:147-156) compares the same raw-byte hash.
        // Hashing a BOM-stripped string would never match for BOM-bearing files — every stamp change caused a spurious re-parse.
        string cfgPath = Path.Combine(_tempDir, "bom.config");
        byte[] raw = Encoding.UTF8.GetBytes("\uFEFF[section]\n\tkey = value\n");
        await File.WriteAllBytesAsync(cfgPath, raw, TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(cfgPath, repoGitDirPath: null, new GitSystemDirs());
        await backend.OpenAsync(GitConfigLevel.Local, TestContext.Current.CancellationToken);

        // The stored checksum must be SHA-256 over the raw bytes (BOM included).
        byte[] expected = SHA256.HashData(raw);
        byte[] actual = GetChecksum(backend);
        Assert.Equal(expected, actual);

        // An mtime-only touch must NOT mark the file modified.
        File.SetLastWriteTimeUtc(cfgPath, DateTime.UtcNow.AddSeconds(5));
        Assert.False(await IsModifiedAsync(backend));
    }

    private static byte[] GetChecksum(FileConfigBackend backend)
    {
        FieldInfo fileField = typeof(FileConfigBackend).GetField("_file", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object file = fileField.GetValue(backend)!;
        FieldInfo checksumField = file.GetType().GetField("_checksum", BindingFlags.Public | BindingFlags.Instance)!;
        return (byte[])checksumField.GetValue(file)!;
    }

    private static async ValueTask<bool> IsModifiedAsync(FileConfigBackend backend)
    {
        FieldInfo fileField = typeof(FileConfigBackend).GetField("_file", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object file = fileField.GetValue(backend)!;
        MethodInfo method = typeof(FileConfigBackend).GetMethod(
            "IsModifiedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var vt = (ValueTask<bool>)method.Invoke(null, [file, CancellationToken.None])!;
        return await vt;
    }

    // ── onbranch: HEAD ASCII-only rtrim ────────────────────────

    [Fact]
    public async Task IncludeIf_OnBranch_HeadWithTrailingNbsp_NotApplied()
    {
        // C (config_file.c:683): the HEAD content is rtrimmed with the ASCII set only (git_str_rtrim). A trailing non-ASCII whitespace (NBSP) survives, so
        // "ref: refs/heads/main\u00A0" does not match the onbranch:main condition. string.TrimEnd removed the NBSP and applied the include.
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        // HEAD pointing at "main" with a trailing NBSP before the newline.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "HEAD"), "ref: refs/heads/main\u00A0\n", TestContext.Current.CancellationToken);

        string extra = Path.Combine(_tempDir, "extra.config");
        await File.WriteAllTextAsync(extra, "[marker]\n\tpresent = yes\n", TestContext.Current.CancellationToken);

        // The path value must be config-escaped: backslashes (Windows paths)
        // are written as "\\" so the parser unescapes them back to "\".
        string cfg = $"[includeIf \"onbranch:main\"]\n\tpath = {extra.Replace("\\", "\\\\")}\n";
        string cfgPath = Path.Combine(_tempDir, "inc.config");
        await File.WriteAllTextAsync(cfgPath, cfg, TestContext.Current.CancellationToken);

        await using GitConfiguration config = new(repo.Context);
        await config.AddFileOnDiskAsync(cfgPath, GitConfigLevel.Local, repoGitDirPath: repo.Path, cancellationToken: TestContext.Current.CancellationToken);

        string? present = await config.GetStringAsync("marker.present", TestContext.Current.CancellationToken);
        Assert.Null(present);
    }

    // ── core.excludesfile ~name is a literal path ──────────────

    [Fact]
    public async Task ExcludesFile_TildeName_LiteralPath_NoThrow()
    {
        // C (attrcache.c:338-343, attr_cache__lookup_path): only a leading "~/" is expanded; "~foo" is taken verbatim (a relative filename that silently fails
        // to open). Routing this through git_config__parse_path would throw "retrieving a homedir by name is not supported".
        string repoDir = NewDir();
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        await repo.Config.SetStringAsync("core.excludesfile", "~foo", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), "x\n", TestContext.Current.CancellationToken);

        bool ignored = await IgnoreContext.PathIsIgnoredAsync(repo, "file.txt", TestContext.Current.CancellationToken);
        Assert.False(ignored);
    }
}
