using BenchmarkDotNet.Attributes;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Benchmarks;

/// <summary>
/// Micro-benchmarks for the <see cref="GitOid"/> hot paths: hex parsing and
/// formatting run for every object reference in every subsystem.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("micro")]
public class CoreOidBenchmarks
{
    private const string SubjectHex = "0d3f2a4c9b8e71f6a5d4c3b2a190817161514131";
    private const string OtherHex = "ffffffffffffffffffffffffffffffffffffffff";

    private string _hex = null!;
    private GitOid _oid;
    private GitOid _other;

    [GlobalSetup]
    public void Setup()
    {
        _oid = GitOid.Parse(SubjectHex, GitHashAlgorithmKind.Sha1);
        _other = GitOid.Parse(OtherHex, GitHashAlgorithmKind.Sha1);
        _hex = _oid.ToString();
    }

    [Benchmark]
    public GitOid Parse()
        => GitOid.Parse(_hex, GitHashAlgorithmKind.Sha1);

    [Benchmark]
    public bool TryParse()
        => GitOid.TryParse(_hex, GitHashAlgorithmKind.Sha1, out _);

    [Benchmark]
    public string FormatToString()
        => _oid.ToString();

    [Benchmark]
    public int FormatHexStackAlloc()
    {
        Span<char> destination = stackalloc char[GitOid.HexSizeFor(GitHashAlgorithmKind.Sha1)];
        return _oid.FormatHex(destination);
    }

    [Benchmark]
    public int GetHashCodeValue()
        => _oid.GetHashCode();

    [Benchmark]
    public bool EqualsOther()
        => _oid.Equals(_other);

    [Benchmark]
    public int CompareToOther()
        => _oid.CompareTo(_other);

    [Benchmark]
    public string ToLooseObjectPath()
        => _oid.ToPathString();
}
