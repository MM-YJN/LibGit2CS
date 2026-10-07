using BenchmarkDotNet.Attributes;

using LibGit2CS.Core;

namespace LibGit2CS.Benchmarks;

/// <summary>Compares delta-index construction before and after sharing the pack window's owned source buffer.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("allocation")]
public class DeltaIndexAllocationBenchmarks
{
    private byte[] _source = null!;

    [Params(4096, 65536, 1048576)]
    public int SourceLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Both paths start with an existing source buffer; object loading and
        // window preparation are measured by PackPreparationAllocationBenchmarks.
        _source = new byte[SourceLength];
        new Random(42).NextBytes(_source);
    }

    [Benchmark(Baseline = true)]
    public object Before_CopySource() => DeltaEncoder.BuildIndex(_source)!;

    [Benchmark]
    public object After_ShareOwnedSource() => DeltaEncoder.BuildIndexFromRetainedBuffer(_source)!;
}
