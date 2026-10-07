using BenchmarkDotNet.Attributes;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Repository;

namespace LibGit2CS.Benchmarks;

/// <summary>Measures fresh pack preparation, including loose-object reads and window rotation.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("allocation")]
public class PackPreparationAllocationBenchmarks
{
    private string _directory = null!;
    private GitContext _context = null!;
    private GitRepository _repo = null!;
    private readonly GitOid[] _ids = new GitOid[24];

    [Params(4096, 65536, 1048576)]
    public int PayloadLength { get; set; }

    [Params(0, 4096)]
    public int WindowMemory { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackPreparation_" + Guid.NewGuid().ToString("N"));
        _context = new GitContext();
        _repo = await GitRepository.InitAsync(_directory, isBare: true, _context).ConfigureAwait(false);
        await _repo.Config.SetIntAsync("pack.windowMemory", WindowMemory).ConfigureAwait(false);
        byte[] payload = new byte[PayloadLength];
        new Random(42).NextBytes(payload);
        for (int i = 0; i < _ids.Length; i++)
        {
            payload[PayloadLength / 2] = (byte)i;
            _ids[i] = await _repo.ObjectWriteAsync(GitObjectType.Blob, payload).ConfigureAwait(false);
        }
    }

    // A new writer prevents PrepareAsync's already-prepared fast path. Fixture
    // creation is excluded; insertion, IO, delta search and disposal are included.
    [Benchmark]
    public async Task<int> Prepare()
    {
        using GitPackWriter writer = _repo.NewPackWriter();
        foreach (GitOid id in _ids)
        {
            await writer.InsertAsync(id).ConfigureAwait(false);
        }

        await writer.PrepareAsync().ConfigureAwait(false);
        return writer.ObjectCount;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _repo.DisposeAsync().ConfigureAwait(false);
        _context.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
