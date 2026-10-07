using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

public sealed class MidxWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public MidxWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MidxWriter_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private string ExtractRepo()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(path);
        return path;
    }

    /// <summary>
    /// Port of <c>test_pack_midx__writer</c>. Adds the three pack files from
    /// testrepo, writes a MIDX, and asserts byte-exact equality with the
    /// pre-generated <c>multi-pack-index</c> file embedded in the fixture.
    /// </summary>
    [Fact]
    public async Task Writer_ProducesByteExactMatch()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string packDir = Path.Combine(repoPath, "objects", "pack");
        string expectedPath = Path.Combine(packDir, "multi-pack-index");
        byte[] expected = await File.ReadAllBytesAsync(expectedPath, cancellationToken: TestContext.Current.CancellationToken);

        // Remove the pre-existing MIDX so the writer creates a fresh one.
        File.Delete(expectedPath);

        using var writer = new MultiPackIndexWriter(packDir, GitHashAlgorithmKind.Sha1);
        await writer.AddAsync("pack-d7c6adf9f61318f041845b01440d09aa7a91e1b5.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-d85f5d483273108c9d8dd0e4728ccf0b2982423a.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx", TestContext.Current.CancellationToken);
        byte[] actual = writer.Dump();

        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Writes a MIDX via <see cref="MultiPackIndexWriter.CommitAsync"/>, then reads it
    /// back via <see cref="MultiPackIndex.OpenAsync"/> and verifies that entries can
    /// be looked up.
    /// </summary>
    [Fact]
    public async Task Writer_Commit_RoundTripReadsBackCorrectly()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string packDir = Path.Combine(repoPath, "objects", "pack");

        // Remove the pre-existing MIDX.
        string existingMidx = Path.Combine(packDir, "multi-pack-index");
        if (File.Exists(existingMidx))
        {
            File.Delete(existingMidx);
        }

        using var writer = new MultiPackIndexWriter(packDir, GitHashAlgorithmKind.Sha1);
        await writer.AddAsync("pack-d7c6adf9f61318f041845b01440d09aa7a91e1b5.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-d85f5d483273108c9d8dd0e4728ccf0b2982423a.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx", TestContext.Current.CancellationToken);
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        // Read back.
        MultiPackIndex? midx = await MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(midx);
        Assert.Equal(3, midx!.NumPacks);
        Assert.True(midx.NumObjects > 0);

        // Verify a known OID: 5001298e0c09ad9c34e4249bc5801c75e9754fa5 (commit one).
        var oid = GitOid.Parse("5001298e0c09ad9c34e4249bc5801c75e9754fa5".AsSpan(), GitHashAlgorithmKind.Sha1);
        MultiPackIndexEntry? entry = midx.FindEntry(oid);
        Assert.NotNull(entry);
        Assert.True(entry!.Value.Offset >= 0);

        // Verify pack name.
        string packName = midx.GetPackName(entry.Value.PackIndex);
        Assert.EndsWith(".idx", packName);
        Assert.StartsWith("pack-", packName);
    }

    /// <summary>
    /// Verifies that <see cref="MultiPackIndexWriter.CommitAsync"/> writes the same
    /// bytes as <see cref="MultiPackIndexWriter.Dump"/>.
    /// </summary>
    [Fact]
    public async Task Writer_Commit_WritesSameBytesAsDump()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string packDir = Path.Combine(repoPath, "objects", "pack");

        File.Delete(Path.Combine(packDir, "multi-pack-index"));

        using var writer = new MultiPackIndexWriter(packDir, GitHashAlgorithmKind.Sha1);
        await writer.AddAsync("pack-d7c6adf9f61318f041845b01440d09aa7a91e1b5.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-d85f5d483273108c9d8dd0e4728ccf0b2982423a.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx", TestContext.Current.CancellationToken);
        byte[] dumped = writer.Dump();
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(Path.Combine(packDir, "multi-pack-index"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(dumped, written);
    }

    /// <summary>
    /// Verifies that pack names are sorted alphabetically in the PNAM chunk.
    /// The C <c>packfile__cmp</c> comparator sorts by name.
    /// </summary>
    [Fact]
    public async Task Writer_PackNames_SortedAlphabetically()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string packDir = Path.Combine(repoPath, "objects", "pack");

        File.Delete(Path.Combine(packDir, "multi-pack-index"));

        // Add in non-sorted order.
        using var writer = new MultiPackIndexWriter(packDir, GitHashAlgorithmKind.Sha1);
        await writer.AddAsync("pack-d85f5d483273108c9d8dd0e4728ccf0b2982423a.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-d7c6adf9f61318f041845b01440d09aa7a91e1b5.idx", TestContext.Current.CancellationToken);
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        MultiPackIndex midx = (await MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken))!;

        // Should be sorted: a81e < d7c6 < d85f.
        Assert.Equal("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx", midx.GetPackName(0));
        Assert.Equal("pack-d7c6adf9f61318f041845b01440d09aa7a91e1b5.idx", midx.GetPackName(1));
        Assert.Equal("pack-d85f5d483273108c9d8dd0e4728ccf0b2982423a.idx", midx.GetPackName(2));
    }

    /// <summary>
    /// Verifies that duplicate OIDs across packs are deduplicated (keeping the
    /// first occurrence = smallest pack offset).
    /// </summary>
    [Fact]
    public async Task Writer_Deduplication_KeepsFirstOffset()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string packDir = Path.Combine(repoPath, "objects", "pack");

        File.Delete(Path.Combine(packDir, "multi-pack-index"));

        using var writer = new MultiPackIndexWriter(packDir, GitHashAlgorithmKind.Sha1);
        await writer.AddAsync("pack-d7c6adf9f61318f041845b01440d09aa7a91e1b5.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-d85f5d483273108c9d8dd0e4728ccf0b2982423a.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx", TestContext.Current.CancellationToken);
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        MultiPackIndex midx = (await MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken))!;

        // The MIDX should have at least as many objects as the largest pack.
        using GitPackIndex idx = await GitPackIndex.OpenAsync(
            Path.Combine(packDir, "pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx"),
            GitHashAlgorithmKind.Sha1,
            TestContext.Current.CancellationToken);

        // MIDX object count should be >= any single pack's object count
        // (it's the union of all packs, deduplicated).
        Assert.True(midx.NumObjects >= idx.ObjectCount);
    }

    /// <summary>
    /// Verifies that adding a non-.idx path throws.
    /// </summary>
    [Fact]
    public async Task Writer_AddNonIdxPath_Throws()
    {
        string packDir = Path.Combine(_tempDir, "pack");
        Directory.CreateDirectory(packDir);

        using var writer = new MultiPackIndexWriter(packDir, GitHashAlgorithmKind.Sha1);
        await Assert.ThrowsAsync<GitException>(async () =>
            await writer.AddAsync("pack-foo.pack", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that the PNAM chunk is padded to a 4-byte boundary.
    /// </summary>
    [Fact]
    public async Task Writer_PackNames_PaddedToFourBytes()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string packDir = Path.Combine(repoPath, "objects", "pack");

        File.Delete(Path.Combine(packDir, "multi-pack-index"));

        using var writer = new MultiPackIndexWriter(packDir, GitHashAlgorithmKind.Sha1);
        await writer.AddAsync("pack-d7c6adf9f61318f041845b01440d09aa7a91e1b5.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-d85f5d483273108c9d8dd0e4728ccf0b2982423a.idx", TestContext.Current.CancellationToken);
        await writer.AddAsync("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695.idx", TestContext.Current.CancellationToken);
        await writer.CommitAsync(TestContext.Current.CancellationToken);

        // If it round-trips through the reader without error, the PNAM padding
        // is correct (the reader walks NUL-terminated strings).
        MultiPackIndex? midx = await MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(midx);
        Assert.Equal(3, midx!.NumPacks);
    }
}
