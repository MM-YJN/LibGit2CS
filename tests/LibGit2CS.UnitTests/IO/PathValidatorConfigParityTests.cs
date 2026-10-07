using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.IO;

/// <summary>
/// Regression tests for the <c>dotgit_flags</c> (path.c:263-287) parity
/// behaviors: <c>core.protectHFS</c>/<c>core.protectNTFS</c> are read from the
/// repository config, config parse errors are swallowed with the protection
/// flag left OFF (the "if (!error &amp;&amp; …)" gates), and the config
/// defaults are <c>GIT_PROTECTHFS_DEFAULT</c> = false /
/// <c>GIT_PROTECTNTFS_DEFAULT</c> = true. Expectations are C-verified
/// against libgit2 1.9.4.
/// </summary>
public sealed class PathValidatorConfigParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public PathValidatorConfigParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PathValidatorConfig_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
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

    /// <summary>
    /// "git~1" is an NTFS short name for ".git": rejected when
    /// DotGitNtfs is on, accepted when the protection is off.
    /// </summary>
    private static async ValueTask<bool> ValidNtfsNameAsync(GitRepository? repo)
    {
        return await GitPathValidator.IsValidAsync(
            "git~1", GitPathRejectFlags.DotGit, repo, fileMode: 0x81A4,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProtectNtfs_DefaultTrue_RejectsNtfsShortName()
    {
        // C (repository.h:121): GIT_PROTECTNTFS_DEFAULT = true.
        Assert.False(await ValidNtfsNameAsync(_repo));
    }

    [Fact]
    public async Task ProtectNtfs_False_AcceptsNtfsShortName()
    {
        await _repo.Config.SetStringAsync("core.protectNTFS", "false", TestContext.Current.CancellationToken);
        Assert.True(await ValidNtfsNameAsync(_repo));
    }

    [Fact]
    public async Task ProtectNtfs_UnparseableValue_SwallowedFlagOff()
    {
        // C (path.c:279-283): the lookup error is swallowed and the NTFS flag
        // is left OFF — validation proceeds, no exception.
        await _repo.Config.SetStringAsync("core.protectNTFS", "banana", TestContext.Current.CancellationToken);
        Assert.True(await ValidNtfsNameAsync(_repo));
    }

    [Fact]
    public async Task ProtectHfs_UnparseableValue_SwallowedFlagOff()
    {
        // C (path.c:274-278): non-Apple → the HFS lookup runs; an error
        // leaves the flag off and is swallowed. ".g\u0301it" (combining
        // acute) is NOT skipped by next_hfs_char (path.c:20-59) — the literal
        // check accepts it, and it does not match ".git" under the HFS
        // ignore-list rules, so it stays accepted either way; the important
        // assertion is that the unparseable value does not throw.
        await _repo.Config.SetStringAsync("core.protectHFS", "banana", TestContext.Current.CancellationToken);
        Assert.True(await GitPathValidator.IsValidAsync(
            ".g\u0301it", GitPathRejectFlags.DotGit, _repo, fileMode: 0x81A4,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProtectHfs_True_RejectsHfsIgnoreListForm()
    {
        // C (path.c:30-50): next_hfs_char skips U+200C (ZERO WIDTH
        // NON-JOINER), so ".g\u200cit" normalizes to ".git" under the HFS
        // rules — rejected only when core.protectHFS is on.
        await _repo.Config.SetStringAsync("core.protectHFS", "true", TestContext.Current.CancellationToken);
        Assert.False(await GitPathValidator.IsValidAsync(
            ".g\u200cit", GitPathRejectFlags.DotGit, _repo, fileMode: 0x81A4,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProtectHfs_DefaultFalse_AcceptsHfsIgnoreListForm()
    {
        // C (repository.h:119): GIT_PROTECTHFS_DEFAULT = false on non-Apple.
        Assert.True(await GitPathValidator.IsValidAsync(
            ".g\u200cit", GitPathRejectFlags.DotGit, _repo, fileMode: 0x81A4,
            TestContext.Current.CancellationToken));
    }
}
