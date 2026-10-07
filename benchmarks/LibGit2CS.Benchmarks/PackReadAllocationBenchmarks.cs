using BenchmarkDotNet.Attributes;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.Benchmarks;

/// <summary>Measures uncached pack reads using the existing upstream pack fixture, including OFS_DELTA resolution.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("allocation")]
public class PackReadAllocationBenchmarks
{
    private PackFile _pack = null!;
    private GitOid _oid;
    private GitObjectHeader _expected;

    [Params(false, true)]
    public bool Delta { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "LibGit2CS.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        string path = Path.Combine(root ?? throw new InvalidOperationException("Repository root not found."),
            "tests", "LibGit2CS.UnitTests", "Fixtures", "pack", "pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.pack");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using GitPackIndex index = await GitPackIndex.OpenAsync(Path.ChangeExtension(path, ".idx"), GitHashAlgorithmKind.Sha1);
        bool found = false;
        for (int i = 0; i < index.ObjectCount; i++)
        {
            int type = (bytes[(int)index.GetObjectOffset(i)] >> 4) & 7;
            if (Delta ? type == 6 : type == 3)
            {
                _oid = index.GetOid(i);
                found = true;
                break;
            }
        }

        if (!found)
        {
            throw new InvalidOperationException("Required pack object type not found.");
        }

        _pack = await PackFile.OpenAsync(path, GitHashAlgorithmKind.Sha1, CancellationToken.None);
        RawObjectData raw = (await _pack.ReadAsync(_oid, CancellationToken.None))!.Value;
        _expected = (await _pack.ReadHeaderAsync(_oid, CancellationToken.None))!.Value;
        if (raw.Type != _expected.Type || raw.Data.Length != _expected.Size)
        {
            throw new InvalidOperationException("Pack header and body disagree.");
        }
    }

    [Benchmark]
    public Task<GitObjectHeader?> ReadHeader() => _pack.ReadHeaderAsync(_oid, CancellationToken.None);

    [Benchmark]
    public async Task<int> ReadBody()
    {
        RawObjectData raw = (await _pack.ReadAsync(_oid, CancellationToken.None))!.Value;
        return raw.Data.Length;
    }

    [GlobalCleanup]
    public void Cleanup() => _pack.Dispose();
}
