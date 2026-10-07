using BenchmarkDotNet.Attributes;

using LibGit2CS.Benchmarks.Fixtures;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Benchmarks;

/// <summary>
/// Object-database read/write paths on the linear-1k fixture: commit/blob
/// lookup, header reads, and blob writes (hash + compress).
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("repo")]
public class ObjectReadBenchmarks
{
    private GitContext _context = null!;
    private GitRepository _repo = null!;
    private GitOid _headCommitId;
    private GitOid _blobId;
    private byte[] _writePayload = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        string path = await FixtureFactory.GetLinearAsync().ConfigureAwait(false);
        _context = new GitContext();
        _repo = await GitRepository.OpenAsync(path, _context, CancellationToken.None).ConfigureAwait(false);

        GitObject? head = await _repo.RevparseSingleAsync("HEAD", CancellationToken.None).ConfigureAwait(false);
        _headCommitId = head!.Id;
        GitTree tree = (await _repo.Objects.LookupAsync<GitTree>(((Commit)head).Tree, CancellationToken.None).ConfigureAwait(false))!;
        GitTreeEntry entry = await tree.EntryByPathAsync("src/mod0/file0.cs", CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("linear fixture is missing src/mod0/file0.cs");

        _blobId = entry.Id;
        _writePayload = FixtureFactory.GeneratePayload(2_048);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _repo.DisposeAsync().ConfigureAwait(false);
        _context.Dispose();
    }

    [Benchmark]
    public async Task<Commit?> LookupCommit()
        => await _repo.Objects.LookupAsync<Commit>(_headCommitId, CancellationToken.None).ConfigureAwait(false);

    [Benchmark]
    public async Task<int> LookupBlobContentLength()
    {
        GitBlob? blob = await _repo.Objects.LookupAsync<GitBlob>(_blobId, CancellationToken.None).ConfigureAwait(false);
        return blob!.Content.Length;
    }

    [Benchmark]
    public async Task<GitObjectHeader?> ReadHeader()
        => await _repo.Objects.ReadHeaderAsync(_blobId, CancellationToken.None).ConfigureAwait(false);

    [Benchmark]
    public async Task<GitOid> WriteBlob2KiB()
        => await _repo.Objects.WriteAsync(GitObjectType.Blob, _writePayload, CancellationToken.None).ConfigureAwait(false);
}
