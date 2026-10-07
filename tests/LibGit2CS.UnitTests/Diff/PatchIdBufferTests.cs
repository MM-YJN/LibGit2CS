using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;

namespace LibGit2CS.UnitTests.Diff;

public sealed class PatchIdBufferTests
{
    [Theory]
    [InlineData(GitHashAlgorithmKind.Sha1, 0)]
    [InlineData(GitHashAlgorithmKind.Sha1, 255)]
    [InlineData(GitHashAlgorithmKind.Sha1, 4096)]
    [InlineData(GitHashAlgorithmKind.Sha1, 131072)]
    [InlineData(GitHashAlgorithmKind.Sha256, 0)]
    [InlineData(GitHashAlgorithmKind.Sha256, 4096)]
    public async Task Compute_MixedLineSizesAndWhitespace_MatchesIndependentHash(GitHashAlgorithmKind algorithm, int length)
    {
        string longText = new('x', length);
        // NBSP is retained; only ASCII whitespace is stripped. A long line is
        // followed by short and whitespace-only lines to catch stale scratch bytes.
        string patch = "diff --git a/a.txt b/a.txt\nindex 1111111..2222222 100644\n--- a/a.txt\n+++ b/a.txt\n@@ -1,4 +1,4 @@\n"
            + " context \t\r\n"
            + $"-o \t{longText}\f\v\r\n+n \t{longText}\f\v\r\n"
            + " \t \f\v\r\n"
            + "-\t \f\v\r\n+\u00a0\n"
            + "\\ No newline at end of file\n"
            + "diff --git a/b.txt b/b.txt\nindex 3333333..4444444 100644\n--- a/b.txt\n+++ b/b.txt\n@@ -1 +1 @@\n-old\n+new\n";
        // Configure the algorithm explicitly to test hashing independently of
        // the public FromBuffer option propagation.
        DiffParsed parsed = DiffParsed.FromBuffer(Encoding.UTF8.GetBytes(patch), new GitDiffParseOptions { OidType = algorithm })!;
        using var diff = new GitDiff(parsed, [.. parsed.Deltas], new GitDiffOptions { OidType = algorithm });

        // Construct canonical bytes explicitly, independently of the printer and
        // whitespace stripper. File hashes are added as little-endian integers.
        byte[] first = Encoding.UTF8.GetBytes($"diff--gita/a.txtb/a.txt---a/a.txt+++b/a.txtcontext-o{longText}+n{longText}-+\u00a0");
        byte[] second = "diff--gita/b.txtb/b.txt---a/b.txt+++b/b.txt-old+new"u8.ToArray();
        byte[] hash1 = algorithm == GitHashAlgorithmKind.Sha1 ? SHA1.HashData(first) : SHA256.HashData(first);
        byte[] hash2 = algorithm == GitHashAlgorithmKind.Sha1 ? SHA1.HashData(second) : SHA256.HashData(second);
        BigInteger sum = (new BigInteger(hash1, isUnsigned: true) + new BigInteger(hash2, isUnsigned: true))
            % (BigInteger.One << (hash1.Length * 8));
        byte[] expected = new byte[hash1.Length];
        Assert.True(sum.TryWriteBytes(expected, out _, isUnsigned: true));

        GitOid actual = await GitPatchId.ComputeAsync(diff, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(algorithm, actual.Algorithm);
        Assert.Equal(expected, actual.RawBytes.ToArray());
        // Reusing the parsed diff must not mutate any line data.
        Assert.Equal(actual, await GitPatchId.ComputeAsync(diff, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Compute_NonUtf8Content_PreservesBytes()
    {
        byte[] patch = [.. "diff --git a/a b/a\nindex 1111111..2222222 100644\n--- a/a\n+++ b/a\n@@ -1 +1 @@\n-"u8, 0xFF, (byte)'\n', (byte)'+', 0xFE, (byte)'\n'];
        using var diff = GitDiff.FromBuffer(patch);
        byte[] canonical = [.. "diff--gita/ab/a---a/a+++b/a-"u8, 0xFF, (byte)'+', 0xFE];
        GitOid actual = await GitPatchId.ComputeAsync(diff, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SHA1.HashData(canonical), actual.RawBytes.ToArray());
    }
}
