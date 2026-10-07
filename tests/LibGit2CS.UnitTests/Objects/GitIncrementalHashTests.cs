using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class GitIncrementalHashTests
{
    [Theory]
    [InlineData(GitHashAlgorithmKind.Sha1)]
    [InlineData(GitHashAlgorithmKind.Sha256)]
    public void Finalize_UsesAlgorithmSelectedAtCreation(GitHashAlgorithmKind algorithm)
    {
        byte[] data = "incremental hash test"u8.ToArray();
        using var hash = GitIncrementalHash.Create(algorithm);

        hash.AppendData(data);
        GitOid actual = hash.Finalize();
        byte[] expected = algorithm == GitHashAlgorithmKind.Sha256
            ? SHA256.HashData(data)
            : SHA1.HashData(data);

        Assert.Equal(algorithm, actual.Algorithm);
        Assert.Equal(expected, actual.RawBytes.ToArray());
    }
}
