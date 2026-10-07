using System.Text;

using BenchmarkDotNet.Attributes;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.Benchmarks;

/// <summary>Measures object parsing without object-database IO or cache hits.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("allocation")]
public class DecodingAllocationBenchmarks
{
    private byte[] _tag = null!;
    private byte[] _commit = null!;
    private GitOid _id;

    [Params(64, 16384)]
    public int MessageLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _id = GitOid.Empty;
        string message = new('x', MessageLength);
        _tag = Encoding.UTF8.GetBytes($"object {_id}\ntype commit\ntag v1-é\ntagger T <t@t> 0 +0000\n\n{message}\n");
        _commit = Encoding.UTF8.GetBytes($"tree {_id}\nauthor T <t@t> 0 +0000\ncommitter T <t@t> 0 +0000\nencoding UTF-8\n\n{message}\n");
    }

    [Benchmark]
    public int ParseTag()
    {
        using var tag = GitTag.Parse(null, _id, _tag, GitHashAlgorithmKind.Sha1);
        return tag.Message!.Length;
    }

    [Benchmark]
    public int ParseCommit()
    {
        using var commit = Commit.Parse(null, _id, _commit, GitHashAlgorithmKind.Sha1);
        return commit.RawMessage.Length;
    }
}
