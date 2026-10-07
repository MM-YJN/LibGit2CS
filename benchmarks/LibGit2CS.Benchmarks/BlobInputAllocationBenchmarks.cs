using BenchmarkDotNet.Attributes;

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Benchmarks;

/// <summary>Measures input staging for index and blob-stream writes.</summary>
/// <remarks>
/// The blob already exists in the ODB so iterations exercise hashing and freshening
/// without compression or growing the object database. Stream measurements include
/// staging the input, committing, and disposal. Repository setup is excluded.
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory("allocation")]
public class BlobInputAllocationBenchmarks
{
    private string _directory = null!;
    private GitContext _context = null!;
    private GitRepository _repo = null!;
    private GitIndex _index = null!;
    private GitIndexEntry _entry;
    private byte[] _payload = null!;

    [Params(2048, 1048576)]
    public int PayloadLength { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LibGit2CS_BlobInput_" + Guid.NewGuid().ToString("N"));
        _context = new GitContext();
        _repo = await GitRepository.InitAsync(_directory, isBare: false, _context).ConfigureAwait(false);
        _index = await _repo.GetIndexAsync().ConfigureAwait(false);
        _entry = new GitIndexEntry("buffer.bin", GitOid.Empty, GitFileMode.Regular);
        _payload = new byte[PayloadLength];
        new Random(42).NextBytes(_payload);
        GitOid expected = await _repo.ObjectWriteAsync(GitObjectType.Blob, _payload).ConfigureAwait(false);
        await AddIndexBuffer().ConfigureAwait(false);
        if (_index.EntryByIndex(0).Id != expected || await CommitBlobStream().ConfigureAwait(false) != expected)
        {
            throw new InvalidOperationException("Blob input benchmark fixture did not round-trip.");
        }
    }

    [Benchmark]
    public Task AddIndexBuffer()
        => _index.AddFromBufferAsync(_entry, _payload);

    [Benchmark]
    public async Task<GitOid> CommitBlobStream()
    {
        using GitBlobWriteStream stream = _repo.BlobCreateWriteStream();
#pragma warning disable CA1849 // Staging only appends to a MemoryStream; exclude Stream's async adapter overhead.
        stream.Write(_payload, 0, _payload.Length);
#pragma warning restore CA1849
        return await stream.CommitAsync().ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _repo.DisposeAsync().ConfigureAwait(false);
        _context.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
