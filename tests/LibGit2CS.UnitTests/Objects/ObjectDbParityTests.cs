using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Regression tests for the ODB object behaviors (empty blob/tree
/// hardcoding; hashobj/write type validation) in
/// libgit2 1.9.4. Every expectation
/// is C-verified against libgit2 1.9.4.
/// </summary>
public sealed class ObjectDbParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;

    public ObjectDbParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectDbParity_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ---------------------------------------------------------------
    // C hardcodes only the EMPTY TREE (odb.c:60-84); the empty blob is
    // never special-cased in the ODB — exists/read/read_header all miss.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Exists_EmptyBlob_NotStored_ReturnsFalse()
    {
        // C: git_odb_exists(&empty_blob) on a repo without the object → 0.
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        Assert.False(await db.ExistsAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exists_EmptyTree_NotStored_ReturnsFalse()
    {
        // C: git_odb_exists(&empty_tree) → 0 (exists has no hardcoded check).
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        Assert.False(await db.ExistsAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadHeader_EmptyBlob_NotStored_ReturnsNull()
    {
        // C: git_odb_read_header(&empty_blob) → GIT_ENOTFOUND.
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        Assert.Null(await db.ReadHeaderAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadHeader_EmptyTree_NotStored_ReturnsTreeHeader()
    {
        // C: git_odb_read_header(&empty_tree) → rc 0, GIT_OBJECT_TREE, len 0.
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        GitObjectHeader? header = await db.ReadHeaderAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken);
        Assert.NotNull(header);
        Assert.Equal(GitObjectType.Tree, header!.Value.Type);
        Assert.Equal(0, header.Value.Size);
    }

    [Fact]
    public async Task Lookup_EmptyBlob_NotStored_ReturnsNull()
    {
        // C: git_odb_read(&empty_blob) → GIT_ENOTFOUND.
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        Assert.Null(await db.LookupAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_EmptyTree_NotStored_ReturnsEmptyTree()
    {
        // C: git_odb_read(&empty_tree) → rc 0, TREE, size 0.
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        GitObject? obj = await db.LookupAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Tree, obj!.Type);
        Assert.Equal(0, obj.Size);
    }

    [Fact]
    public async Task Exists_EmptyObjects_AfterWrite_ReturnsTrue()
    {
        // C: writing the empty blob/tree makes them visible to exists/read.
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitOid blobOid = await db.WriteAsync(GitObjectType.Blob, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        GitOid treeOid = await db.WriteAsync(GitObjectType.Tree, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        Assert.Equal(GitOid.EmptyBlobSha1, blobOid);
        Assert.Equal(GitOid.EmptyTreeSha1, treeOid);

        Assert.True(await db.ExistsAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
        Assert.True(await db.ExistsAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken));
        Assert.NotNull(await db.ReadHeaderAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
        Assert.NotNull(await db.LookupAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------
    // git_odb__hashobj / git_odb_write reject non-loose types with
    // "invalid object type" (odb.c:119-122; object.c:345-351).
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(GitObjectType.OfsDelta)]
    [InlineData(GitObjectType.RefDelta)]
    [InlineData(GitObjectType.Ext1)]
    [InlineData(GitObjectType.Ext2)]
    [InlineData(GitObjectType.Any)]
    public void HashObject_NonLooseType_ThrowsInvalid(GitObjectType type)
    {
        // C: git_odb_hash(...) → rc -1, "invalid object type".
        GitException ex = Assert.Throws<GitException>(() =>
            GitObjectDb.HashObject(type, "x"u8, GitHashAlgorithmKind.Sha1));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid object type", ex.Message);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }

    [Fact]
    public void HashObject_LooseTypes_Works()
    {
        GitOid commit = GitObjectDb.HashObject(GitObjectType.Commit, "data"u8, GitHashAlgorithmKind.Sha1);
        GitOid blob = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8, GitHashAlgorithmKind.Sha1);
        Assert.False(commit.IsZero);
        // Well-known value: `git hash-object` of "hello" as a blob.
        Assert.Equal("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0", blob.ToString());
    }

    [Fact]
    public async Task WriteAsync_NonLooseType_ThrowsInvalid_FileUntouched()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await db.WriteAsync(GitObjectType.OfsDelta, "x"u8.ToArray(), TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid object type", ex.Message);

        // Nothing was written.
        Assert.Empty(Directory.EnumerateFiles(_objectsDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WriteAsync_LooseType_Writes()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitOid oid = await db.WriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.True(await db.ExistsAsync(oid, TestContext.Current.CancellationToken));
        GitObject? obj = await db.LookupAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Blob, obj!.Type);
        Assert.Equal("hello", Encoding.ASCII.GetString(obj.Raw.Span));
    }
}
