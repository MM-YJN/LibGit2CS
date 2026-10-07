using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

// DecompressBody reads the zlib-framed object body through incrementally
// growing windows (4 KiB initial, doubling on NeedMoreData, capped at 1 MiB).
// These tests force the growth path and the capped multi-window path with
// incompressible bodies whose compressed size exceeds the window boundaries.
public sealed class PackFileDecompressWindowTests : IDisposable
{
    private readonly string _tempDir;

    public PackFileDecompressWindowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackWindow_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    public async Task ExactSizedBody_ReadsCorrectly(int length)
    {
        byte[] body = MakeIncompressible(length);
        RawObjectData raw = await ReadSingleObjectPackAsync(body);
        Assert.Equal(body, raw.Data);
        Assert.Equal(length, raw.Data.Length);
    }

    [Fact]
    public async Task CompressedBodyLargerThanInitialWindow_ReadsCorrectly()
    {
        // 32 KiB of incompressible data compresses to ~32 KiB — beyond the
        // 4 KiB initial window, forcing 4K→8K→… growth rounds before Done.
        byte[] body = MakeIncompressible(32 * 1024);
        Assert.True(ZlibTestHelpers.CompressLooseObject(body).Length > 4096);

        RawObjectData raw = await ReadSingleObjectPackAsync(body);

        Assert.Equal(GitObjectType.Blob, raw.Type);
        Assert.Equal(body.Length, raw.Data.Length);
        Assert.Equal(body, raw.Data);
    }

    [Fact]
    public async Task CompressedBodyLargerThanMaxWindow_ReadsCorrectly()
    {
        // 2.5 MiB of incompressible data exceeds the 1 MiB window cap: the
        // window grows to the cap, then the stream spans multiple full
        // 1 MiB windows plus a remainder.
        byte[] body = MakeIncompressible((2 * 1024 + 512) * 1024);
        Assert.True(ZlibTestHelpers.CompressLooseObject(body).Length > 1024 * 1024);

        RawObjectData raw = await ReadSingleObjectPackAsync(body);

        Assert.Equal(GitObjectType.Blob, raw.Type);
        Assert.Equal(body.Length, raw.Data.Length);
        Assert.Equal(body, raw.Data);
    }

    private async Task<RawObjectData> ReadSingleObjectPackAsync(byte[] body)
    {
        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, body);
        byte[] packBytes = builder.Build();

        string packDir = Path.Combine(_tempDir, "win_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken);
        await indexer.CommitAsync(stats, TestContext.Current.CancellationToken);

        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);
        using PackFile pack = await PackFile.OpenAsync(indexer.PackPath!, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        RawObjectData? raw = await pack.ReadAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(raw);
        return raw!.Value;
    }

    private static byte[] MakeIncompressible(int length)
    {
        // Deterministic xorshift32 stream — zlib cannot compress it, so the
        // compressed body length tracks the raw body length.
        byte[] data = new byte[length];
        uint state = 0x9E3779B9u;
        for (int i = 0; i < length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)(state >> 24);
        }

        return data;
    }
}
