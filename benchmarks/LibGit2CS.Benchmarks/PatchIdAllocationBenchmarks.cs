using System.Text;

using BenchmarkDotNet.Attributes;

using LibGit2CS.Core;
using LibGit2CS.Diff;

namespace LibGit2CS.Benchmarks;

/// <summary>Measures patch-ID hashing with parsing excluded and short/long lines.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("allocation")]
public class PatchIdAllocationBenchmarks
{
    private GitDiff _diff = null!;

    [Params(16, 1024)]
    public int LineCount { get; set; }

    [Params(32, 4096)]
    public int LineLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var patch = new StringBuilder($"diff --git a/a.txt b/a.txt\nindex 1111111..2222222 100644\n--- a/a.txt\n+++ b/a.txt\n@@ -1,{LineCount} +1,{LineCount} @@\n");
        string padding = new('x', LineLength);
        for (int i = 0; i < LineCount; i++)
        {
            patch.Append("-old ").Append(padding).Append(" \t\n");
            patch.Append("+new ").Append(padding).Append(" \t\n");
        }

        _diff = GitDiff.FromBuffer(patch.ToString());
    }

    [Benchmark]
    public Task<GitOid> ComputePatchId()
        => GitPatchId.ComputeAsync(_diff, cancellationToken: CancellationToken.None);

    [GlobalCleanup]
    public void Cleanup() => _diff.Dispose();
}
