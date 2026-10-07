using BenchmarkDotNet.Attributes;

using LibGit2CS.Benchmarks.Fixtures;
using LibGit2CS.Core;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.Benchmarks;

/// <summary>
/// Revision-walk benchmarks: full walks of the linear-1k history under the
/// three sort modes, and graph queries (merge base, ahead/behind,
/// descendant-of) on the criss-cross DAG.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("revwalk")]
public class RevwalkBenchmarks
{
    private GitContext _linearContext = null!;
    private GitRepository _linearRepo = null!;
    private GitContext _dagContext = null!;
    private GitRepository _dagRepo = null!;
    private GitOid _rootId;
    private GitOid _tip1Id;
    private GitOid _tip2Id;

    [GlobalSetup]
    public async Task Setup()
    {
        string linearPath = await FixtureFactory.GetLinearAsync().ConfigureAwait(false);
        _linearContext = new GitContext();
        _linearRepo = await GitRepository.OpenAsync(linearPath, _linearContext, CancellationToken.None).ConfigureAwait(false);

        string dagPath = await FixtureFactory.GetDagAsync().ConfigureAwait(false);
        _dagContext = new GitContext();
        _dagRepo = await GitRepository.OpenAsync(dagPath, _dagContext, CancellationToken.None).ConfigureAwait(false);
        (_rootId, _tip1Id, _tip2Id) = await FixtureFactory.ReadDagOidsAsync(dagPath).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _dagRepo.DisposeAsync().ConfigureAwait(false);
        _dagContext.Dispose();
        await _linearRepo.DisposeAsync().ConfigureAwait(false);
        _linearContext.Dispose();
    }

    [Benchmark]
    public async Task<int> WalkLinear_None()
        => await WalkAsync(_linearRepo, GitSortMode.None).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> WalkLinear_Topological()
        => await WalkAsync(_linearRepo, GitSortMode.Topological).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> WalkLinear_Time()
        => await WalkAsync(_linearRepo, GitSortMode.Time).ConfigureAwait(false);

    [Benchmark]
    public async Task<GitOid?> MergeBase_CrissCross()
        => await _dagRepo.MergeBaseFindAsync(_tip1Id, _tip2Id, CancellationToken.None).ConfigureAwait(false);

    [Benchmark]
    public async Task<IReadOnlyList<GitOid>> MergeBaseAll_CrissCross()
        => await _dagRepo.MergeBaseFindAllAsync(_tip1Id, _tip2Id, CancellationToken.None).ConfigureAwait(false);

    [Benchmark]
    public async Task<(int Ahead, int Behind)> AheadBehind_CrissCross()
        => await _dagRepo.AheadBehindAsync(_tip1Id, _tip2Id, CancellationToken.None).ConfigureAwait(false);

    [Benchmark]
    public async Task<bool> DescendantOf_TipFromRoot()
        => await _dagRepo.DescendantOfAsync(_tip1Id, _rootId, CancellationToken.None).ConfigureAwait(false);

    private static async Task<int> WalkAsync(GitRepository repo, GitSortMode sort)
    {
        using GitRevWalker walker = repo.NewRevWalker();
        walker.Sort = sort;
        await walker.PushHeadAsync(CancellationToken.None).ConfigureAwait(false);
        int count = 0;
        await foreach (GitOid id in walker.WalkAsync(CancellationToken.None).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }
}
