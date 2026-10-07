using BenchmarkDotNet.Attributes;

using LibGit2CS.Benchmarks.Fixtures;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Benchmarks;

/// <summary>
/// Diff benchmarks exercising the Xdiff engine through LibGit2CS public
/// APIs only. <see cref="GitRepository.DiffTreeToTreeAsync"/> just pairs
/// files (deltas); the Xdiff run happens when a patch is materialized
/// (<c>PatchFromBuffers</c> directly, or <c>GetHunkCountAsync</c>/
/// <c>LineStatsAsync</c>/<c>ToBufferAsync</c> on lazy diffs). Algorithms:
/// default Myers, Patience, Minimal — mirroring the flags libgit2 exposes.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("diff")]
public class DiffBenchmarks
{
    private static readonly GitDiffOptions s_myers = new();
    private static readonly GitDiffOptions s_patience = new() { Flags = GitDiffOptionsFlags.Patience };
    private static readonly GitDiffOptions s_minimal = new() { Flags = GitDiffOptionsFlags.Minimal };
    private static readonly GitDiffOptions s_contextZero = new() { ContextLines = 0 };

    private GitContext _context = null!;
    private GitRepository _repo = null!;
    private byte[] _smallOld = null!;
    private byte[] _smallNew = null!;
    private byte[] _largeOld = null!;
    private byte[] _largeNew = null!;
    private byte[] _dupOld = null!;
    private byte[] _dupNew = null!;
    private GitTree _headTree = null!;
    private GitTree _prevTree = null!;
    private GitTree _baseTree = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        (_smallOld, _smallNew) = FixtureFactory.GenerateChurnedPair(50, seed: 101, duplicateHeavy: false);
        (_largeOld, _largeNew) = FixtureFactory.GenerateChurnedPair(2_000, seed: 202, duplicateHeavy: false);
        (_dupOld, _dupNew) = FixtureFactory.GenerateChurnedPair(1_000, seed: 303, duplicateHeavy: true);

        string path = await FixtureFactory.GetLinearAsync().ConfigureAwait(false);
        _context = new GitContext();
        _repo = await GitRepository.OpenAsync(path, _context, CancellationToken.None).ConfigureAwait(false);

        GitObject? headObject = await _repo.RevparseSingleAsync("HEAD", CancellationToken.None).ConfigureAwait(false);
        var head = (Commit)headObject!;
        _headTree = (await _repo.Objects.LookupAsync<GitTree>(head.Tree, CancellationToken.None).ConfigureAwait(false))!;

        Commit previous = (await _repo.Objects.LookupAsync<Commit>(head.Parents[0], CancellationToken.None).ConfigureAwait(false))!;
        _prevTree = (await _repo.Objects.LookupAsync<GitTree>(previous.Tree, CancellationToken.None).ConfigureAwait(false))!;

        Commit cursor = head;
        for (int i = 0; i < 50; i++)
        {
            cursor = (await _repo.Objects.LookupAsync<Commit>(cursor.Parents[0], CancellationToken.None).ConfigureAwait(false))!;
        }

        _baseTree = (await _repo.Objects.LookupAsync<GitTree>(cursor.Tree, CancellationToken.None).ConfigureAwait(false))!;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _repo.DisposeAsync().ConfigureAwait(false);
        _context.Dispose();
    }

    [Benchmark]
    public async Task<int> PatchFromBuffers_Small_Myers()
        => await PatchFromBuffersAsync(_smallOld, _smallNew, s_myers).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> PatchFromBuffers_Small_Patience()
        => await PatchFromBuffersAsync(_smallOld, _smallNew, s_patience).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> PatchFromBuffers_Small_Minimal()
        => await PatchFromBuffersAsync(_smallOld, _smallNew, s_minimal).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> PatchFromBuffers_Large_Myers()
        => await PatchFromBuffersAsync(_largeOld, _largeNew, s_myers).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> PatchFromBuffers_Large_Patience()
        => await PatchFromBuffersAsync(_largeOld, _largeNew, s_patience).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> PatchFromBuffers_Large_Minimal()
        => await PatchFromBuffersAsync(_largeOld, _largeNew, s_minimal).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> PatchFromBuffers_Large_ContextZero()
        => await PatchFromBuffersAsync(_largeOld, _largeNew, s_contextZero).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> PatchFromBuffers_DupHeavy_Myers()
        => await PatchFromBuffersAsync(_dupOld, _dupNew, s_myers).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> TreeToTree_DeltasOnly_OneCommit()
    {
        using GitDiff diff = await _repo.DiffTreeToTreeAsync(_prevTree, _headTree, null, CancellationToken.None).ConfigureAwait(false);
        return diff.DeltaCount;
    }

    [Benchmark]
    public async Task<int> TreeToTree_FullPatchBytes_FiftyCommits()
    {
        using GitDiff diff = await _repo.DiffTreeToTreeAsync(_baseTree, _headTree, null, CancellationToken.None).ConfigureAwait(false);
        byte[] buffer = await diff.ToBufferAsync(GitDiffPrintFormat.Patch, CancellationToken.None).ConfigureAwait(false);
        return buffer.Length;
    }

    [Benchmark]
    public async Task<(int Context, int Additions, int Deletions)> PatchLineStats_OneCommit()
    {
        using GitDiff diff = await _repo.DiffTreeToTreeAsync(_prevTree, _headTree, null, CancellationToken.None).ConfigureAwait(false);
        using GitPatch patch = await _repo.PatchFromDiffAsync(diff, 0, CancellationToken.None).ConfigureAwait(false);
        return await patch.LineStatsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<int> PatchFromBuffersAsync(byte[] oldText, byte[] newText, GitDiffOptions options)
    {
        using GitPatch patch = _repo.PatchFromBuffers(oldText, newText, options);
        return await patch.GetHunkCountAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
