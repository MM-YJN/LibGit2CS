using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class LooseObjectBackendTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;

    public LooseObjectBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_LooseObjectBackendTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _objectsDir = Path.Combine(_tempDir, "objects");
        Directory.CreateDirectory(_objectsDir);
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
    public async Task Read_ValidObject_ReturnsRawData()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        RawObjectData? raw = await backend.ReadAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(raw);
        Assert.Equal(GitObjectType.Blob, raw!.Value.Type);
        Assert.Equal(5, raw!.Value.Size);
        Assert.Equal("hello", Encoding.ASCII.GetString(raw!.Value.Data));
    }

    [Fact]
    public async Task Exists_ExistingOid_ReturnsTrue()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);

        Assert.True(backend.Exists(oid));
    }

    [Fact]
    public async Task Exists_NonexistentOid_ReturnsFalse()
    {
        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);

        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var nonexistent = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);

        Assert.False(backend.Exists(nonexistent));
    }

    [Fact]
    public async Task Enumerate_ReturnsWrittenOids()
    {
        GitOid oid1 = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());
        GitOid oid2 = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "world"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        var oids = new List<GitOid>();
        await foreach (GitOid oid in backend.EnumerateAsync(TestContext.Current.CancellationToken))
        {
            oids.Add(oid);
        }

        Assert.Equal(2, oids.Count);
        Assert.Contains(oid1, oids);
        Assert.Contains(oid2, oids);
    }

    [Fact]
    public async Task ReadHeader_ReturnsTypeAndSize()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitObjectHeader? header = await backend.ReadHeaderAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(header);
        Assert.Equal(GitObjectType.Blob, header!.Value.Type);
        Assert.Equal(5, header!.Value.Size);
    }

    [Fact]
    public async Task Read_InvalidObject_Throws()
    {
        GitOid oid = WriteGarbageAtObjectPath();

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);

        await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Read_NonexistentOid_ReturnsNull()
    {
        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);

        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var nonexistent = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);

        Assert.Null(await backend.ReadAsync(nonexistent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadHeader_NonexistentOid_ReturnsNull()
    {
        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);

        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var nonexistent = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);

        Assert.Null(await backend.ReadHeaderAsync(nonexistent, TestContext.Current.CancellationToken));
    }

    private GitOid WriteGarbageAtObjectPath()
    {
        var oid = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);
        string path = Path.Combine(_objectsDir, oid.ToPathString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, "this is not a valid zlib stream"u8.ToArray());
        return oid;
    }
}
