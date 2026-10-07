using LibGit2CS.Core;
using LibGit2CS.Merge;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Merge;

public sealed class MergeConfigTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public MergeConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeConfig_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch { }
    }

    // ── Default flags ───────────────────────────────────────────────────

    [Fact]
    public async Task Default_HasFindRenames_On()
    {
        // Matches GIT_MERGE_OPTIONS_INIT (merge.h:329-330) which sets
        // GIT_MERGE_FIND_RENAMES.
        Assert.True(
            (GitMergeOptions.Default.Flags & GitMergeFlags.FindRenames) != 0,
            "MergeOptions.Default should include FindRenames to match C's GIT_MERGE_OPTIONS_INIT");
    }

    [Fact]
    public async Task Default_RenameThreshold_Is50()
    {
        Assert.Equal(50, GitMergeOptions.Default.RenameThreshold);
    }

    // ── NormalizeOptions: merge.default ──────────────────────────────────

    [Fact]
    public async Task NormalizeOptions_NoGiven_UsesDefaults()
    {
        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(null, CancellationToken.None);

        Assert.True((opts.Flags & GitMergeFlags.FindRenames) != 0);
        Assert.Equal(50, opts.RenameThreshold);
        Assert.Equal(200, opts.TargetLimit); // GIT_MERGE_DEFAULT_TARGET_LIMIT
        Assert.Null(opts.DefaultDriver);
    }

    [Fact]
    public async Task NormalizeOptions_ConfigMergeDefault_SetsDefaultDriver()
    {
        await _repo.Config.SetStringAsync("merge.default", "text", cancellationToken: TestContext.Current.CancellationToken);

        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(null, CancellationToken.None);

        Assert.Equal("text", opts.DefaultDriver);
    }

    [Fact]
    public async Task NormalizeOptions_GivenDefaultDriver_OverridesConfig()
    {
        await _repo.Config.SetStringAsync("merge.default", "text", cancellationToken: TestContext.Current.CancellationToken);

        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(new GitMergeOptions { DefaultDriver = "union" }, CancellationToken.None);

        Assert.Equal("union", opts.DefaultDriver);
    }

    [Fact]
    public async Task NormalizeOptions_NoMergeDefaultConfig_DriverStaysNull()
    {
        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(null, CancellationToken.None);

        Assert.Null(opts.DefaultDriver);
    }

    // ── NormalizeOptions: renamelimit ───────────────────────────────────

    [Fact]
    public async Task NormalizeOptions_ConfigMergeRenameLimit_SetsTargetLimit()
    {
        await _repo.Config.SetIntAsync("merge.renamelimit", 42, cancellationToken: TestContext.Current.CancellationToken);

        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(null, CancellationToken.None);

        Assert.Equal(42, opts.TargetLimit);
    }

    [Fact]
    public async Task NormalizeOptions_ConfigDiffRenameLimit_Fallback()
    {
        // When merge.renamelimit is unset, fall back to diff.renamelimit
        // (merge.c:1917-1920).
        await _repo.Config.SetIntAsync("diff.renamelimit", 77, cancellationToken: TestContext.Current.CancellationToken);

        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(null, CancellationToken.None);

        Assert.Equal(77, opts.TargetLimit);
    }

    [Fact]
    public async Task NormalizeOptions_MergeRenameLimit_PreferredOverDiff()
    {
        await _repo.Config.SetIntAsync("merge.renamelimit", 10, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetIntAsync("diff.renamelimit", 20, cancellationToken: TestContext.Current.CancellationToken);

        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(null, CancellationToken.None);

        Assert.Equal(10, opts.TargetLimit);
    }

    [Fact]
    public async Task NormalizeOptions_NoRenameLimitConfig_DefaultsTo200()
    {
        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(null, CancellationToken.None);

        Assert.Equal(200, opts.TargetLimit);
    }

    [Fact]
    public async Task NormalizeOptions_GivenTargetLimit_OverridesConfig()
    {
        await _repo.Config.SetIntAsync("merge.renamelimit", 42, cancellationToken: TestContext.Current.CancellationToken);

        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(new GitMergeOptions { TargetLimit = 99 }, CancellationToken.None);

        Assert.Equal(99, opts.TargetLimit);
    }

    // ── NormalizeOptions: rename_threshold ──────────────────────────────

    [Fact]
    public async Task NormalizeOptions_FindRenamesWithZeroThreshold_DefaultsTo50()
    {
        // merge.c:1897-1898: if FIND_RENAMES and threshold is 0, use 50.
        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(new GitMergeOptions { RenameThreshold = 0 }, CancellationToken.None);

        Assert.Equal(50, opts.RenameThreshold);
    }

    [Fact]
    public async Task NormalizeOptions_NonZeroThreshold_Preserved()
    {
        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(new GitMergeOptions { RenameThreshold = 75 }, CancellationToken.None);

        Assert.Equal(75, opts.RenameThreshold);
    }

    [Fact]
    public async Task NormalizeOptions_NoFindRenames_ZeroThresholdStaysZero()
    {
        // merge.c:1897: the default threshold is only set when FIND_RENAMES is on.
        GitMergeOptions opts = await _repo.NormalizeMergeOptionsAsync(
            new GitMergeOptions { Flags = GitMergeFlags.None, RenameThreshold = 0 }, CancellationToken.None);

        Assert.Equal(0, opts.RenameThreshold);
    }
}
