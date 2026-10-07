using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.UnitTests.Core;

public sealed class GitOidToHexPrefixTests
{
    private const string FullSha1Hex = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";

    [Fact]
    public void ToHexPrefix_FullLength_MatchesToString()
    {
        var oid = GitOid.Parse(FullSha1Hex.AsSpan(), GitHashAlgorithmKind.Sha1);

        string prefix = oid.ToHexPrefix(40);

        Assert.Equal(FullSha1Hex, prefix);
    }

    [Fact]
    public void ToHexPrefix_PartialLength_MatchesFirstNChars()
    {
        var oid = GitOid.Parse(FullSha1Hex.AsSpan(), GitHashAlgorithmKind.Sha1);

        string prefix = oid.ToHexPrefix(7);

        Assert.Equal(FullSha1Hex[..7], prefix);
        Assert.Equal("e69de29", prefix);
    }

    [Fact]
    public void ToHexPrefix_ZeroLength_ReturnsEmpty()
    {
        var oid = GitOid.Parse(FullSha1Hex.AsSpan(), GitHashAlgorithmKind.Sha1);

        string prefix = oid.ToHexPrefix(0);

        Assert.Equal(string.Empty, prefix);
    }

    [Fact]
    public void ToHexPrefix_LargerThanHexSize_ClampsToHexSize()
    {
        var oid = GitOid.Parse(FullSha1Hex.AsSpan(), GitHashAlgorithmKind.Sha1);

        // 50 > 40 (SHA-1 hex size) — clamps to 40.
        string prefix = oid.ToHexPrefix(50);

        Assert.Equal(FullSha1Hex, prefix);
        Assert.Equal(40, prefix.Length);
    }

    [Fact]
    public void ToHexPrefix_Negative_Throws()
    {
        var oid = GitOid.Parse(FullSha1Hex.AsSpan(), GitHashAlgorithmKind.Sha1);

        Assert.Throws<ArgumentOutOfRangeException>(() => oid.ToHexPrefix(-1));
    }

    [Fact]
    public void ToPathString_MatchesGitOidPathfmt_2CharSlashRest()
    {
        var oid = GitOid.Parse(FullSha1Hex.AsSpan(), GitHashAlgorithmKind.Sha1);

        string path = oid.ToPathString();

        // git_oid_pathfmt: first 2 hex chars + '/' + remaining 38 hex chars.
        Assert.Equal("e6/9de29bb2d1d6434b8b29ae775ad8c2e48c5391", path);
        Assert.Equal(41, path.Length); // 40 hex + 1 slash
    }

    [Fact]
    public void ToHexPrefix_OddLength_IncludesPartialNibble()
    {
        var oid = GitOid.Parse(FullSha1Hex.AsSpan(), GitHashAlgorithmKind.Sha1);

        // 9 chars — odd length means the last byte's low nibble is omitted.
        string prefix = oid.ToHexPrefix(9);

        Assert.Equal(FullSha1Hex[..9], prefix);
        Assert.Equal("e69de29bb", prefix);
    }
}
