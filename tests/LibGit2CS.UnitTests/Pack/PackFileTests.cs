using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

public sealed class PackFileTests : IDisposable
{
    private readonly string _tempDir;

    public PackFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackFileTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Open_ValidPack_ParsesHeader()
    {
        string packPath = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.True(pack.ObjectCount > 0);
        Assert.Equal(GitHashAlgorithmKind.Sha1, pack.Algorithm);
    }

    [Fact]
    public async Task Open_SHA256Pack_ParsesCorrectly()
    {
        string packPath = WritePackToTemp("sha256");

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha256, TestContext.Current.CancellationToken);

        Assert.True(pack.ObjectCount > 0);
        Assert.Equal(GitHashAlgorithmKind.Sha256, pack.Algorithm);
    }

    [Fact]
    public async Task Read_FirstObject_ReturnsRawData()
    {
        string packPath = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitOid oid = idx.GetOid(0);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        RawObjectData? raw = await pack.ReadAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(raw);
        Assert.True(raw!.Value.Type is GitObjectType.Commit or GitObjectType.Tree or GitObjectType.Blob or GitObjectType.Tag);
        Assert.Equal(raw!.Value.Size, raw!.Value.Data.Length);
    }

    [Fact]
    public async Task Read_NonexistentOid_ReturnsNull()
    {
        string packPath = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var nonexistent = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);

        Assert.Null(await pack.ReadAsync(nonexistent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadHeader_ReturnsTypeAndSize()
    {
        string packPath = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitOid oid = idx.GetOid(0);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitObjectHeader? header = await pack.ReadHeaderAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(header);
        Assert.True(header!.Value.Type is GitObjectType.Commit or GitObjectType.Tree or GitObjectType.Blob or GitObjectType.Tag);
        Assert.True(header!.Value.Size >= 0);
    }

    [Fact]
    public async Task Exists_ExistingOid_ReturnsTrue()
    {
        string packPath = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");
        string idxPath = Path.ChangeExtension(packPath, ".idx");

        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitOid oid = idx.GetOid(0);

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        Assert.True(pack.Exists(oid));
    }

    [Fact]
    public async Task Exists_NonexistentOid_ReturnsFalse()
    {
        string packPath = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var nonexistent = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);

        Assert.False(pack.Exists(nonexistent));
    }

    [Fact]
    public async Task Enumerate_ReturnsAllOids()
    {
        string packPath = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");

        using PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);

        var oids = pack.Enumerate().ToList();

        Assert.Equal(pack.ObjectCount, oids.Count);
    }

    [Fact]
    public async Task Dispose_Twice_DoesNotThrow()
    {
        string packPath = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");

        PackFile pack = await PackFile.OpenAsync(packPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        pack.Dispose();
        pack.Dispose();
    }

    [Fact]
    public async Task Read_ConcurrentOperations_PreserveObjectBytes()
    {
        string path = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");
        CancellationToken ct = TestContext.Current.CancellationToken;
        using PackFile pack = await PackFile.OpenAsync(path, GitHashAlgorithmKind.Sha1, ct);
        GitOid[] ids = pack.Enumerate().Take(16).ToArray();
        await Task.WhenAll(ids.Select(async id =>
        {
            RawObjectData? result = await pack.ReadAsync(id, ct);
            GitObjectHeader? header = await pack.ReadHeaderAsync(id, ct);
            Assert.NotNull(result);
            Assert.NotNull(header);
            Assert.Equal(id, GitObjectDb.HashObject(result.Value.Type, result.Value.Data, GitHashAlgorithmKind.Sha1));
            Assert.Equal(result.Value.Type, header.Value.Type);
            Assert.Equal(result.Value.Data.LongLength, header.Value.Size);
        }));
    }

    [Fact]
    public async Task ReadHeader_CancelledRead_DoesNotAffectNextRead()
    {
        string path = WritePackToTemp("pack-a81e489679b7d3418f9ab594bda8ceb37dd4c695");
        using PackFile pack = await PackFile.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitOid id = pack.Enumerate().First();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pack.ReadHeaderAsync(id, cancelled.Token));
        Assert.NotNull(await pack.ReadHeaderAsync(id, TestContext.Current.CancellationToken));
    }

    private string WritePackToTemp(string fixtureBaseName)
    {
        byte[] packBytes = FixtureLoader.LoadBytes($"Fixtures/pack/{fixtureBaseName}.pack");
        byte[] idxBytes = FixtureLoader.LoadBytes($"Fixtures/pack/{fixtureBaseName}.idx");
        string packPath = Path.Combine(_tempDir, $"{fixtureBaseName}.pack");
        string idxPath = Path.Combine(_tempDir, $"{fixtureBaseName}.idx");
        File.WriteAllBytes(packPath, packBytes);
        File.WriteAllBytes(idxPath, idxBytes);
        return packPath;
    }
}
