using System.Buffers.Binary;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

public class PackIndexTests
{
    [Fact]
    public async Task Open_TestRepoPack_ParsesV2Format()
    {
        // testrepo.git has 3 pack/idx pairs; pick the first.
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Equal(2, idx.Version);
        Assert.True(idx.ObjectCount > 0);
        Assert.Equal(GitHashAlgorithmKind.Sha1, idx.Algorithm);
    }

    [Fact]
    public async Task Open_Sha256Index_ParsesCorrectly()
    {
        string idxPath = WriteFixtureToTemp("pack/sha256.idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha256, TestContext.Current.CancellationToken);

        Assert.Equal(2, idx.Version);
        Assert.True(idx.ObjectCount > 0);
        Assert.Equal(GitHashAlgorithmKind.Sha256, idx.Algorithm);

        // SHA-256 OIDs are 32 bytes; verify we can read one.
        GitOid oid = idx.GetOid(0);
        Assert.Equal(32, oid.Size);
    }

    [Fact]
    public void Open_BadIndex_Throws()
    {
        // Create a synthetic bad index: valid v2 signature + version, but
        // non-monotonic fanout.
        byte[] data = new byte[8 + 4 * 256 + 32 * 2]; // minimum size for SHA-256 v2
        WriteUInt32BE(data, 0, GitPackIndex.IdxSignature);
        WriteUInt32BE(data, 4, 2); // version 2
        // Fanout[0] = 10, fanout[1] = 5 (non-monotonic)
        WriteUInt32BE(data, 8, 10);
        WriteUInt32BE(data, 12, 5);

        Assert.Throws<GitException>(() => GitPackIndex.Parse(data, GitHashAlgorithmKind.Sha256));
    }

    [Fact]
    public void Open_TooSmall_Throws()
    {
        // Minimum size for SHA-1 v1: 4*256 + 20*2 = 1064. 100 bytes is too small.
        byte[] data = new byte[100];
        Assert.Throws<GitException>(() => GitPackIndex.Parse(data, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Open_NonMonotonicFanout_Throws()
    {
        // Build a v1 index with non-monotonic fanout.
        byte[] data = new byte[4 * 256 + 20 * 2];
        // Set fanout[0] = 10, fanout[1] = 5 (non-monotonic)
        WriteUInt32BE(data, 0, 10);
        WriteUInt32BE(data, 4, 5);

        Assert.Throws<GitException>(() => GitPackIndex.Parse(data, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Open_WrongSizeV1_Throws()
    {
        // Create a v1 index with correct fanout but wrong file size.
        // Fanout says 5 objects; v1 size = 4*256 + 5*(20+4) + 20*2 = 1024 + 120 + 40 = 1184
        // Make the file 1200 bytes (wrong).
        byte[] data = new byte[1200];
        WriteUInt32BE(data, 255 * 4, 5); // fanout[255] = 5 (total objects)

        Assert.Throws<GitException>(() => GitPackIndex.Parse(data, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public void Open_UnsupportedV3_Throws()
    {
        // Create a v2 index with version=3.
        byte[] data = new byte[8 + 4 * 256 + 20 * 2];
        WriteUInt32BE(data, 0, GitPackIndex.IdxSignature);
        WriteUInt32BE(data, 4, 3); // version 3

        Assert.Throws<GitException>(() => GitPackIndex.Parse(data, GitHashAlgorithmKind.Sha1));
    }

    [Fact]
    public async Task FindIndex_ExactMatch_FindsObject()
    {
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // Get the first OID and look it up.
        GitOid oid = idx.GetOid(0);
        GitPackIndexLookupResult result = idx.FindIndex(oid);

        Assert.True(result.Found);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public async Task FindIndex_AbbreviatedMatch_FindsObject()
    {
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // Get the first OID and look up by first 7 hex chars (4 bytes).
        GitOid oid = idx.GetOid(0);
        GitPackIndexLookupResult result = idx.FindIndex(oid, hexLength: 7);

        // Should find at least one match (could be ambiguous if unlucky, but
        // with 7 hex chars the chance is very low for small packs).
        Assert.True(result.Index >= 0);
    }

    [Fact]
    public async Task FindIndex_NonexistentOid_ReturnsNotFound()
    {
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // Use an OID that definitely doesn't exist (all 0xff).
        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var oid = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);

        GitPackIndexLookupResult result = idx.FindIndex(oid);

        Assert.False(result.Found);
        Assert.Equal(-1, result.Index);
    }

    [Fact]
    public async Task GetObjectOffset_ValidIndex_ReturnsPositiveOffset()
    {
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        for (int i = 0; i < Math.Min(idx.ObjectCount, 10); i++)
        {
            long offset = idx.GetObjectOffset(i);
            Assert.True(offset is >= 0 and < long.MaxValue, $"Object {i} offset out of range");
        }
    }

    [Fact]
    public async Task EnumerateOids_ReturnsAllObjectsInOrder()
    {
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        var oids = idx.EnumerateOids().ToList();

        Assert.Equal(idx.ObjectCount, oids.Count);

        // Verify OIDs are sorted (pack index stores them in sorted order).
        for (int i = 1; i < oids.Count; i++)
        {
            Assert.True(oids[i - 1].CompareTo(oids[i]) <= 0,
                $"OIDs not sorted at index {i}: {oids[i - 1]} > {oids[i]}");
        }
    }

    [Fact]
    public async Task PackChecksum_ReturnsValidOid()
    {
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        GitOid checksum = idx.PackChecksum();

        Assert.Equal(GitHashAlgorithmKind.Sha1, checksum.Algorithm);
        Assert.Equal(20, checksum.Size);
        Assert.False(checksum.IsZero);
    }

    [Fact]
    public async Task FindIndex_AllObjectsInPack_FoundByFullOid()
    {
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        // Every object in the index should be findable by its full OID.
        for (int i = 0; i < idx.ObjectCount; i++)
        {
            GitOid oid = idx.GetOid(i);
            GitPackIndexLookupResult result = idx.FindIndex(oid);
            Assert.True(result.Found, $"Object {i} (OID {oid}) not found by full OID lookup");
            Assert.Equal(i, result.Index);
        }
    }

    [Fact]
    public async Task Dispose_Twice_DoesNotThrow()
    {
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        idx.Dispose();
        idx.Dispose();
    }

    [Fact]
    public async Task GetOid_OutOfRange_Throws()
    {
        string idxPath = WriteFixtureToTemp("pack/pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentOutOfRangeException>(() => idx.GetOid(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => idx.GetOid(idx.ObjectCount));
    }

    private static string WriteFixtureToTemp(string fixturePath)
    {
        byte[] bytes = FixtureLoader.LoadBytes($"Fixtures/{fixturePath}");
        string tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackIndex_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        string fileName = Path.GetFileName(fixturePath);
        string destPath = Path.Combine(tempDir, fileName);
        File.WriteAllBytes(destPath, bytes);
        return destPath;
    }

    private static void WriteUInt32BE(byte[] data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
}
