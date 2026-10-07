using BenchmarkDotNet.Attributes;

using LibGit2CS.Benchmarks.Fixtures;
using LibGit2CS.Core;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.Benchmarks;

/// <summary>
/// Status benchmarks on the wide (1,000-file) fixtures: clean vs dirty
/// workdir, with and without untracked traversal, plus ignore-rule lookup.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("status")]
public class StatusBenchmarks
{
    private static readonly GitStatusOptions s_withoutUntracked = new();
    private static readonly GitStatusOptions s_withUntracked = new()
    {
        Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
    };

    private GitContext _cleanContext = null!;
    private GitRepository _cleanRepo = null!;
    private GitContext _dirtyContext = null!;
    private GitRepository _dirtyRepo = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        string cleanPath = await FixtureFactory.GetWideCleanAsync().ConfigureAwait(false);
        _cleanContext = new GitContext();
        _cleanRepo = await GitRepository.OpenAsync(cleanPath, _cleanContext, CancellationToken.None).ConfigureAwait(false);

        string dirtyPath = await FixtureFactory.GetWideDirtyAsync().ConfigureAwait(false);
        _dirtyContext = new GitContext();
        _dirtyRepo = await GitRepository.OpenAsync(dirtyPath, _dirtyContext, CancellationToken.None).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _dirtyRepo.DisposeAsync().ConfigureAwait(false);
        _dirtyContext.Dispose();
        await _cleanRepo.DisposeAsync().ConfigureAwait(false);
        _cleanContext.Dispose();
    }

    [Benchmark]
    public async Task<int> StatusClean_WithoutUntracked()
        => await StatusAsync(_cleanRepo, s_withoutUntracked).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> StatusClean_WithUntracked()
        => await StatusAsync(_cleanRepo, s_withUntracked).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> StatusDirty_WithoutUntracked()
        => await StatusAsync(_dirtyRepo, s_withoutUntracked).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> StatusDirty_WithUntracked()
        => await StatusAsync(_dirtyRepo, s_withUntracked).ConfigureAwait(false);

    [Benchmark]
    public async Task<bool> IsIgnored_BuildArtifact()
        => await _dirtyRepo.IsIgnoredAsync("build/artifact.obj", CancellationToken.None).ConfigureAwait(false);

    private static async Task<int> StatusAsync(GitRepository repo, GitStatusOptions options)
    {
        using GitStatusList list = await repo.StatusNewAsync(options, CancellationToken.None).ConfigureAwait(false);
        return list.EntryCount;
    }
}
