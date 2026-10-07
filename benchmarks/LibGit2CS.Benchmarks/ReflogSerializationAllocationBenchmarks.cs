using BenchmarkDotNet.Attributes;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Refs;

namespace LibGit2CS.Benchmarks;

/// <summary>Measures byte-native reflog serialization, excluding entry creation and filesystem IO.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("allocation")]
public class ReflogSerializationAllocationBenchmarks
{
    private GitRefLog _log = null!;

    [Params(1, 1000)]
    public int EntryCount { get; set; }

    [Params(64, 4096)]
    public int MessageLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _log = new GitRefLog("HEAD", GitHashAlgorithmKind.Sha1);
        var signature = new GitSignature("Benchmark", "bench@example.com", new GitTime(1700000000, 0));
        byte[] message = new byte[MessageLength];
        Array.Fill(message, (byte)'x');
        message[0] = 0xff; // Serialization must preserve non-UTF-8 bytes.
        for (int i = 0; i < EntryCount; i++)
        {
            _log.Append(GitOid.EmptyBlobSha1, signature, message);
        }

        byte[] serialized = _log.SerializeBytes();
        var roundTrip = new GitRefLog("HEAD", GitHashAlgorithmKind.Sha1, serialized);
        if (roundTrip.EntryCount != EntryCount || !serialized.AsSpan().SequenceEqual(roundTrip.SerializeBytes()))
        {
            throw new InvalidOperationException("Reflog bytes did not round-trip.");
        }
    }

    [Benchmark]
    public byte[] Serialize() => _log.SerializeBytes();
}
