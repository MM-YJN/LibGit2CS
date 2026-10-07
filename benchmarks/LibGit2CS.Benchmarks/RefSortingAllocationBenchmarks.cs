using BenchmarkDotNet.Attributes;

using LibGit2CS.Core.Hashing;
using LibGit2CS.Refs;

namespace LibGit2CS.Benchmarks;

/// <summary>Measures loose-ref name enumeration, including directory sorting and ref-file reads.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("allocation")]
public class RefSortingAllocationBenchmarks
{
    private string _directory = null!;
    private FileRefBackend _backend = null!;

    [Params(16, 1024)]
    public int RefCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        // Fixed-length path components keep sort-key sizes identical between runs.
        _directory = Path.Combine(Path.GetTempPath(), "LibGit2CS_RefSort_" + Guid.NewGuid().ToString("N"));
        string heads = Path.Combine(_directory, "refs", "heads");
        Directory.CreateDirectory(heads);
        for (int i = RefCount - 1; i >= 0; i--)
        {
            string prefix = (i % 3) switch { 0 => "ascii", 1 => "\uE000", _ => "\U0001F600" };
            await File.WriteAllTextAsync(Path.Combine(heads, $"{prefix}-{i:D4}"), "0000000000000000000000000000000000000000\n").ConfigureAwait(false);
        }

        _backend = new FileRefBackend(_directory, _directory, GitHashAlgorithmKind.Sha1);
        if (await EnumerateNames().ConfigureAwait(false) != RefCount)
        {
            throw new InvalidOperationException("Incomplete loose-ref fixture.");
        }
    }

    [Benchmark]
    public async Task<int> EnumerateNames()
    {
        int count = 0;
        await foreach (RefNameKey name in _backend.EnumerateNamesAsync(null, CancellationToken.None).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _backend.DisposeAsync().ConfigureAwait(false);
        Directory.Delete(_directory, recursive: true);
    }
}
