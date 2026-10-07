using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Tests for <see cref="GitObjectDb.WriteMultiPackIndexAsync"/> — the ODB-level
/// dispatch over <see cref="PackObjectBackend.WriteMultiPackIndexAsync"/> /
/// <see cref="MultiPackIndexWriter"/>. Managed port of libgit2's
/// <c>test_odb_backend__writemidx</c> dispatch path (odb.c:1874-1901).
/// </summary>
public sealed class ObjectDbWriteMidxTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public ObjectDbWriteMidxTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectDbMidx_" + Guid.NewGuid().ToString("N")[..8]);
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
    /// WriteMultiPackIndex dispatches to the pack backend and produces a
    /// <c>multi-pack-index</c> file that round-trips through the reader.
    /// </summary>
    [Fact]
    public async Task WriteMultiPackIndex_PackBackend_WritesFileAndRoundTrips()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string packDir = Path.Combine(repoPath, "objects", "pack");
        string midxPath = Path.Combine(packDir, "multi-pack-index");

        // Remove the pre-existing MIDX so we can verify a fresh write.
        File.Delete(midxPath);

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        await db.AddDefaultBackendsAsync(Path.Combine(repoPath, "objects"), GitHashAlgorithmKind.Sha1, alternateDepth: 0, TestContext.Current.CancellationToken);

        await db.WriteMultiPackIndexAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(midxPath), "multi-pack-index should exist after WriteMultiPackIndex");

        // Round-trip: the reader should parse it and find packs.
        MultiPackIndex? midx = await MultiPackIndex.OpenAsync(packDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(midx);
        Assert.Equal(3, midx!.NumPacks);
        Assert.True(midx.NumObjects > 0);
    }

    /// <summary>
    /// WriteMultiPackIndex overwrites a pre-existing MIDX (C invalidates before
    /// writing via <c>remove_multi_pack_index</c>).
    /// </summary>
    [Fact]
    public async Task WriteMultiPackIndex_OverwritesExistingMidx()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string packDir = Path.Combine(repoPath, "objects", "pack");
        string midxPath = Path.Combine(packDir, "multi-pack-index");

        byte[] original = await File.ReadAllBytesAsync(midxPath, cancellationToken: TestContext.Current.CancellationToken);

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        await db.AddDefaultBackendsAsync(Path.Combine(repoPath, "objects"), GitHashAlgorithmKind.Sha1, alternateDepth: 0, TestContext.Current.CancellationToken);

        await db.WriteMultiPackIndexAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(midxPath));
        byte[] rewritten = await File.ReadAllBytesAsync(midxPath, cancellationToken: TestContext.Current.CancellationToken);

        // The rewritten MIDX should be byte-identical to the original (same packs,
        // same algorithm, deterministic serialization).
        Assert.Equal(original.Length, rewritten.Length);
        Assert.Equal(original, rewritten);
    }

    /// <summary>
    /// WriteMultiPackIndex throws <see cref="GitErrorCode.NotSupported"/> when no
    /// pack backend is configured (matches C's
    /// <c>git_odb__error_unsupported_in_backend</c>).
    /// </summary>
    [Fact]
    public async Task WriteMultiPackIndex_NoPackBackend_ThrowsNotSupported()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        // Only a loose backend, no pack backend.
        db.AddBackend(new LooseObjectBackend(_tempDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await db.WriteMultiPackIndexAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotSupported, ex.Code);
    }

    /// <summary>
    /// WriteMultiPackIndex skips alternate backends (C:
    /// <c>if (internal-&gt;is_alternate) continue;</c>).
    /// </summary>
    [Fact]
    public async Task WriteMultiPackIndex_SkipsAlternateBackends()
    {
        string extractedPath = ExtractRepo();
        string repoPath = Path.Combine(extractedPath, "testrepo.git");
        string packDir = Path.Combine(repoPath, "objects", "pack");
        string midxPath = Path.Combine(packDir, "multi-pack-index");

        File.Delete(midxPath);

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        // Add the real pack backend as an alternate (isAlternate: true).
        // It should be SKIPPED, so WriteMultiPackIndex should throw NotSupported.
        db.AddBackend(new PackObjectBackend(packDir, GitHashAlgorithmKind.Sha1), priority: 1, isAlternate: true);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await db.WriteMultiPackIndexAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotSupported, ex.Code);

        // The alternate backend should NOT have written the MIDX.
        Assert.False(File.Exists(midxPath));
    }
}
