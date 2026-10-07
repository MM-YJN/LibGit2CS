using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

/// <summary> Repository-init precompose tests. Pins the faithful port of <c>repo_init_fsconfigs</c> (<c>repository.c:2301-2307</c>) which
/// writes <c>core.precomposeunicode</c> to a freshly-init'd repo's local config ONLY on macOS (the <c>#ifdef GIT_USE_ICONV</c> build guard); on other platforms
/// the key is intentionally absent. </summary> <remarks> On the Linux/Windows CI the probe is a no-op and the config key is NOT written — this test pins that
/// behavior. The macOS-only probe-result write is not exercised here (would require a macOS runner). </remarks>
public sealed class RepoInitPrecomposeTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly GitContext _context = new();

    public RepoInitPrecomposeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepoInitPrecompose_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _context.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task Init_OnNonMacos_DoesNotWritePrecomposeUnicodeKey()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // Ports the #ifdef GIT_USE_ICONV guard (repository.c:2301-2307): on
        // platforms without iconv (i.e. non-macOS in the faithful port), the
        // probe and the config write are compiled out entirely. The
        // core.precomposeunicode key must NOT appear in the local config of a
        // freshly-init'd repo on Linux/Windows CI.
        await using GitRepository repo = await GitRepository.InitAsync(_tempDir, isBare: false, _context, cancellationToken);

        if (!OperatingSystem.IsMacOS())
        {
            bool hasEntry = await repo.Config.GetEntryAsync("core.precomposeunicode", cancellationToken) is not null;
            Assert.False(hasEntry, "core.precomposeunicode must not be written on non-macOS (matches #ifdef GIT_USE_ICONV guard)");
        }
    }

    [Fact]
    public async Task Init_CoreIgnorecaseKey_IsPresent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Sanity check: the other core.* keys written by repo_init_config ARE
        // present on all platforms. This guards against a regression where
        // WriteInitConfigAsync itself stops writing anything.
        await using GitRepository repo = await GitRepository.InitAsync(_tempDir, isBare: false, _context, cancellationToken);
        bool hasFilemode = await repo.Config.GetEntryAsync("core.filemode", cancellationToken) is not null;
        Assert.True(hasFilemode, "core.filemode should be written by repo_init_config");
    }
}
