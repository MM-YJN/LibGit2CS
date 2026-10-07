using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Core;

public class GitOidTests
{
    [Fact]
    public void EmptyBlobSha1_MatchesKnownConstant()
    {
        GitOid oid = GitOid.EmptyBlobSha1;

        Assert.Equal(GitHashAlgorithmKind.Sha1, oid.Algorithm);
        Assert.Equal(SHA1.HashSizeInBytes, oid.Size);
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", oid.ToString());
    }

    [Fact]
    public void EmptyTreeSha1_MatchesKnownConstant()
    {
        GitOid oid = GitOid.EmptyTreeSha1;

        Assert.Equal("4b825dc642cb6eb9a060e54bf8d69288fbee4904", oid.ToString());
    }

    [Fact]
    public void EmptyBlobSha256_MatchesKnownConstant()
    {
        GitOid oid = GitOid.EmptyBlobSha256;

        Assert.Equal(GitHashAlgorithmKind.Sha256, oid.Algorithm);
        Assert.Equal(SHA256.HashSizeInBytes, oid.Size);
        // Verified via: printf 'blob 0\0' | sha256sum
        Assert.Equal("473a0f4c3be8a93681a267e3b1e9a7dcda1185436fe141f7749120a303721813", oid.ToString());
    }

    [Fact]
    public void Parse_FullHexSha1_RoundTripsThroughToString()
    {
        string hex = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";

        var oid = GitOid.Parse(hex.AsSpan(), GitHashAlgorithmKind.Sha1);

        Assert.Equal(hex, oid.ToString());
    }

    [Fact]
    public void Parse_InvalidHexChar_Throws()
    {
        Assert.Throws<FormatException>(() =>
            GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c53zz".AsSpan(), GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Parse_WrongLength_Throws()
    {
        // 41 chars is too long for SHA-1 (max 40 hex)
        Assert.Throws<FormatException>(() =>
            GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c53910".AsSpan(), GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void TryParse_AcceptsAbbreviatedHex()
    {
        bool ok = GitOid.TryParse("e69de2".AsSpan(), GitHashAlgorithmKind.Sha1, out GitOid oid);

        Assert.True(ok);
        Assert.Equal(0xe6, oid.RawBytes[0]);
        Assert.Equal(0x9d, oid.RawBytes[1]);
        Assert.Equal(0xe2, oid.RawBytes[2]);
        // The rest is zero-padded
        Assert.Equal(0, oid.RawBytes[3]);
    }

    [Fact]
    public void FromRaw_RoundTrips()
    {
        byte[] raw = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef, 0x01, 0x23,
                               0x45, 0x67, 0x89, 0xab, 0xcd, 0xef, 0x01, 0x23, 0x45, 0x67 };

        var oid = GitOid.FromRaw(raw, GitHashAlgorithmKind.Sha1);

        Assert.Equal("0123456789abcdef0123456789abcdef01234567", oid.ToString());
    }

    [Fact]
    public void FromRaw_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            GitOid.FromRaw(new byte[10], GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void IsZero_DefaultStruct_IsTrue()
    {
        GitOid oid = default;

        Assert.True(oid.IsZero);
    }

    [Fact]
    public void IsZero_EmptyConstant_IsTrue()
    {
        Assert.True(GitOid.Empty.IsZero);
    }

    [Fact]
    public void IsZero_NonZeroOid_IsFalse()
    {
        Assert.False(GitOid.EmptyBlobSha1.IsZero);
    }

    [Fact]
    public void Equals_SameBytes_DifferentArrays_AreEqual()
    {
        var a = GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1);
        var b = GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1);

        // Different underlying byte[] instances but same content
        Assert.NotSame(a.RawBytes.ToArray(), b.RawBytes.ToArray());
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equals_DifferentBytes_AreNotEqual()
    {
        var a = GitOid.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1);
        var b = GitOid.Parse("4b825dc642cb6eb9a060e54bf8d69288fbee4904".AsSpan(), GitHashAlgorithmKind.Sha1);

        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_DifferentAlgorithms_AreNotEqual()
    {
        // Same first 20 bytes, different algorithm
        GitOid sha1Oid = GitOid.EmptyBlobSha1;
        byte[] sha256Bytes = new byte[32];
        sha1Oid.RawBytes.CopyTo(sha256Bytes);
        var sha256Oid = GitOid.FromRaw(sha256Bytes, GitHashAlgorithmKind.Sha256);

        Assert.NotEqual(sha1Oid, sha256Oid);
    }

    [Fact]
    public void GetHashCode_SameBytes_ProduceSameHash()
    {
        GitOid a = GitOid.EmptyBlobSha1;
        GitOid b = GitOid.EmptyBlobSha1;

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentBytes_ProduceDifferentHash()
    {
        GitOid a = GitOid.EmptyBlobSha1;
        GitOid b = GitOid.EmptyTreeSha1;

        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void CompareTo_SortsCorrectly()
    {
        var smaller = GitOid.Parse("0000000000000000000000000000000000000001".AsSpan(), GitHashAlgorithmKind.Sha1);
        var larger = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);

        Assert.True(smaller < larger);
        Assert.True(smaller.CompareTo(larger) < 0);
        Assert.True(larger.CompareTo(smaller) > 0);
    }

    [Fact]
    public void ToPathString_ReturnsFirstTwoCharsSlashRest()
    {
        GitOid oid = GitOid.EmptyBlobSha1;

        Assert.Equal("e6/9de29bb2d1d6434b8b29ae775ad8c2e48c5391", oid.ToPathString());
    }

    [Fact]
    public void StartsWith_MatchesPrefix()
    {
        GitOid full = GitOid.EmptyBlobSha1;
        var prefix = GitOid.Parse("e69de2".AsSpan(), GitHashAlgorithmKind.Sha1);

        Assert.True(full.StartsWith(prefix, 6));
    }

    [Fact]
    public void StartsWith_RejectsNonPrefix()
    {
        GitOid full = GitOid.EmptyBlobSha1;
        var prefix = GitOid.Parse("4b825d".AsSpan(), GitHashAlgorithmKind.Sha1);

        Assert.False(full.StartsWith(prefix, 6));
    }

    [Fact]
    public void StartsWith_OddHexLength_MatchesHighNibble()
    {
        GitOid full = GitOid.EmptyBlobSha1;
        // "e69" = 3 hex chars: e6 + high nibble 9
        var prefix = GitOid.Parse("e69".AsSpan(), GitHashAlgorithmKind.Sha1);

        Assert.True(full.StartsWith(prefix, 3));
    }

    [Fact]
    public void SizeFor_ReturnsCorrectSizes()
    {
        Assert.Equal(20, GitOid.SizeFor(GitHashAlgorithmKind.Sha1));
        Assert.Equal(40, GitOid.HexSizeFor(GitHashAlgorithmKind.Sha1));
        Assert.Equal(32, GitOid.SizeFor(GitHashAlgorithmKind.Sha256));
        Assert.Equal(64, GitOid.HexSizeFor(GitHashAlgorithmKind.Sha256));
    }

    // ── ComputeOid / WriteHeader (git_odb__format_object_header parity) ────

    [Fact]
    public void ComputeOid_EmptyBlob_MatchesKnownGitOid()
    {
        var oid = GitOid.ComputeOid(GitObjectType.Blob, [], GitHashAlgorithmKind.Sha1);

        Assert.Equal(GitOid.EmptyBlobSha1, oid);
    }

    [Fact]
    public void ComputeOid_EmptyBlobSha256_MatchesKnownGitOid()
    {
        var oid = GitOid.ComputeOid(GitObjectType.Blob, [], GitHashAlgorithmKind.Sha256);

        Assert.Equal(GitOid.EmptyBlobSha256, oid);
    }

    [Fact]
    public void ComputeOid_EmptyTree_MatchesKnownGitOid()
    {
        var oid = GitOid.ComputeOid(GitObjectType.Tree, [], GitHashAlgorithmKind.Sha1);

        Assert.Equal(GitOid.EmptyTreeSha1, oid);
    }

    [Fact]
    public void ComputeOid_EmptyTreeSha256_MatchesKnownGitOid()
    {
        var oid = GitOid.ComputeOid(GitObjectType.Tree, [], GitHashAlgorithmKind.Sha256);

        Assert.Equal(GitOid.EmptyTreeSha256, oid);
    }

    [Fact]
    public void ComputeOid_HelloWorldBlob_MatchesKnownGitOid()
    {
        // Verified via: printf 'hello world' | git hash-object --stdin
        var oid = GitOid.ComputeOid(GitObjectType.Blob, "hello world"u8, GitHashAlgorithmKind.Sha1);

        Assert.Equal("95d09f2b10159347eece71399a7e2e907ea3df4f", oid.ToString());
    }

    [Fact]
    public void ComputeOid_HelloWorldBlobSha256_MatchesKnownGitOid()
    {
        // Verified via: printf 'hello world' | git hash-object --stdin --literally
        var oid = GitOid.ComputeOid(GitObjectType.Blob, "hello world"u8, GitHashAlgorithmKind.Sha256);

        Assert.Equal("fee53a18d32820613c0527aa79be5cb30173c823a9b448fa4817767cc84c6f03", oid.ToString());
    }

    [Fact]
    public void WriteHeader_Span_Blob_ProducesExactBytes()
    {
        Span<byte> buffer = stackalloc byte[32];
        int length = GitOid.WriteHeader(buffer, GitObjectType.Blob, 11);

        Assert.Equal(8, length);
        Assert.Equal("blob 11\0"u8.ToArray(), buffer[..length].ToArray());
    }

    [Fact]
    public void WriteHeader_Span_AllTypes_ProduceExactBytes()
    {
        Span<byte> buffer = stackalloc byte[32];

        Assert.Equal(9, GitOid.WriteHeader(buffer, GitObjectType.Commit, 0));
        Assert.Equal("commit 0\0"u8.ToArray(), buffer[..9].ToArray());

        Assert.Equal(7, GitOid.WriteHeader(buffer, GitObjectType.Tree, 0));
        Assert.Equal("tree 0\0"u8.ToArray(), buffer[..7].ToArray());

        Assert.Equal(6, GitOid.WriteHeader(buffer, GitObjectType.Tag, 0));
        Assert.Equal("tag 0\0"u8.ToArray(), buffer[..6].ToArray());

        Assert.Equal(12, GitOid.WriteHeader(buffer, GitObjectType.OfsDelta, 0));
        Assert.Equal("OFS_DELTA 0\0"u8.ToArray(), buffer[..12].ToArray());

        Assert.Equal(12, GitOid.WriteHeader(buffer, GitObjectType.RefDelta, 0));
        Assert.Equal("REF_DELTA 0\0"u8.ToArray(), buffer[..12].ToArray());
    }

    [Fact]
    public void WriteHeader_Span_LongMaxSize_FitsIn32Bytes()
    {
        Span<byte> buffer = stackalloc byte[32];
        int length = GitOid.WriteHeader(buffer, GitObjectType.Blob, long.MaxValue);

        Assert.Equal(25, length);
        Assert.Equal("blob 9223372036854775807\0"u8.ToArray(), buffer[..length].ToArray());
    }

    [Fact]
    public void WriteHeader_BufferWriter_ProducesExactBytes()
    {
        using var buffer = new PooledByteBufferWriter(32);
        GitOid.WriteHeader(buffer, GitObjectType.Blob, 11);

        Assert.Equal("blob 11\0"u8.ToArray(), buffer.WrittenSpan.ToArray());
    }

    [Fact]
    public void ComputeOid_MatchesHashOfWriteHeaderBytes()
    {
        // The OID of an object must equal the hash of the exact header bytes
        // (as produced by WriteHeader) followed by the content — guards the
        // header buffer against stray bytes past the NUL terminator.
        byte[] body = "hello world"u8.ToArray();
        Span<byte> header = stackalloc byte[32];
        int headerLength = GitOid.WriteHeader(header, GitObjectType.Blob, body.Length);

        byte[] combined = new byte[headerLength + body.Length];
        header[..headerLength].CopyTo(combined);
        body.CopyTo(combined.AsSpan(headerLength));

        var expected = GitOid.FromRaw(SHA1.HashData(combined), GitHashAlgorithmKind.Sha1);
        var actual = GitOid.ComputeOid(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

        Assert.Equal(expected, actual);
    }

    // ── git_oid_t parity ────────────────────────────────────────────────────

    [Theory]
    [InlineData(GitHashAlgorithmKind.Sha1, 1)]
    [InlineData(GitHashAlgorithmKind.Sha256, 2)]
    public void HashAlgorithmKind_HasLibGit2Value(GitHashAlgorithmKind kind, int value)
    {
        // libgit2's git_oid_t: GIT_OID_SHA1 = 1, GIT_OID_SHA256 = 2. The raw
        // values leak into on-disk formats (commit-graph chunk fanout, midx,
        // config core.repositoryformatversion=1) so they must never change.
        Assert.Equal(value, (int)kind);
    }
}
