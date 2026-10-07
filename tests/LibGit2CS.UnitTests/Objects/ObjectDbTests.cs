using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

public sealed class ObjectDbTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;

    public ObjectDbTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectDbTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task EmptyObjectLookup_EmptyBlob_NotStored_ReturnsNull()
    {
        // C: git_odb_read(&empty_blob) → GIT_ENOTFOUND (only the empty TREE is
        // hardcoded, odb.c:60-84).
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);

        GitObject? obj = await db.LookupAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken);

        Assert.Null(obj);
    }

    [Fact]
    public async Task EmptyObjectLookup_EmptyTree_ReturnsRawGitObject()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);

        GitObject? obj = await db.LookupAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Tree, obj!.Type);
        Assert.Equal(0, obj.Size);
    }

    [Fact]
    public async Task Exists_EmptyObjects_NotStored_ReturnsFalse()
    {
        // C: git_odb_exists has no hardcoded check (odb.c:1031-1054).
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);

        Assert.False(await db.ExistsAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
        Assert.False(await db.ExistsAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_NonexistentOid_ReturnsNull()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);

        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var nonexistent = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);

        Assert.Null(await db.LookupAsync(nonexistent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_LooseObject_ReturnsParsedObject()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitObject? obj = await db.LookupAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Blob, obj!.Type);
        Assert.Equal(5, obj.Size);
        Assert.Equal("hello", Encoding.ASCII.GetString(obj.Raw.Span));
        Assert.Equal(oid, obj.Id);
    }

    [Fact]
    public async Task Exists_LooseObject_ReturnsTrue()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        Assert.True(await db.ExistsAsync(oid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistsPrefix_ExistingOid_ReturnsTrue()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        (bool found, GitOid foundOid) = await db.ExistsPrefixAsync(oid, TestContext.Current.CancellationToken);
        Assert.True(found);
        Assert.Equal(oid, foundOid);
    }

    [Fact]
    public async Task ExistsPrefix_NonexistentOid_ReturnsFalse()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var nonexistent = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);

        (bool found, GitOid _) = await db.ExistsPrefixAsync(nonexistent, TestContext.Current.CancellationToken);
        Assert.False(found);
    }

    [Fact]
    public async Task Enumerate_ReturnsAllObjects()
    {
        GitOid oid1 = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());
        GitOid oid2 = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "world"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        var oids = new List<GitOid>();
        await foreach (GitOid oid in db.EnumerateAsync(TestContext.Current.CancellationToken))
        {
            oids.Add(oid);
        }

        Assert.Equal(2, oids.Count);
        Assert.Contains(oid1, oids);
        Assert.Contains(oid2, oids);
    }

    [Fact]
    public async Task Lookup_CacheHit_ReturnsSameInstance()
    {
        // The empty TREE is hardcoded in reads (odb.c:60-84); the empty blob
        // is not — use the tree for the cache-identity check.
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);

        GitObject? first = await db.LookupAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken);
        GitObject? second = await db.LookupAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Lookup_LooseObject_Commit_CacheHit_ReturnsSameInstance()
    {
        // C (cache.c:25-34): blobs are NEVER cached (0 limit) — use a commit
        // for the same-instance cache-hit assertion.
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Commit, "tree 0123456789abcdef0123456789abcdef01234567\nauthor A <a@b.c> 1461698037 +0200\ncommitter A <a@b.c> 1461698037 +0200\n\nmsg\n"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitObject? first = await db.LookupAsync(oid, TestContext.Current.CancellationToken);
        GitObject? second = await db.LookupAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Lookup_LooseBlob_NotCached_ReturnsFreshInstance()
    {
        // C (cache.c:132-136, cache_should_store): blobs have a 0 limit, so
        // repeated lookups return distinct instances.
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitObject? first = await db.LookupAsync(oid, TestContext.Current.CancellationToken);
        GitObject? second = await db.LookupAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task StrictHashVerification_Mismatch_Throws()
    {
        GitOid wrongOid = WriteLooseObjectAtWrongPath(GitObjectType.Blob, "hello"u8.ToArray());

        // Strictness flags are per-context; the standalone ODB uses the supplied context's StrictHashVerification = true (default).
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await db.LookupAsync(wrongOid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Mismatch, ex.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictHashVerification_UsesCallerContextSettings(bool changeAfterConstruction)
    {
        GitOid wrongOid = WriteLooseObjectAtWrongPath(GitObjectType.Blob, "hello"u8.ToArray());
        using var context = new GitContext();
        context.Settings.StrictHashVerification = changeAfterConstruction;
        await using var db = new GitObjectDb(context);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        context.Settings.StrictHashVerification = false;
        GitBlob? blob = await db.LookupAsync<GitBlob>(wrongOid, TestContext.Current.CancellationToken);

        Assert.NotNull(blob);
        Assert.Equal("hello"u8.ToArray(), blob.Content.ToArray());

        // A different context retains its own verification policy.
        using var strictContext = new GitContext();
        await using var strictDb = new GitObjectDb(strictContext);
        strictDb.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await strictDb.LookupAsync(wrongOid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Mismatch, ex.Code);
    }

    [Fact]
    public async Task ReadHeader_LooseObject_ReturnsTypeAndSize()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitObjectHeader? header = await db.ReadHeaderAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(header);
        Assert.Equal(GitObjectType.Blob, header!.Value.Type);
        Assert.Equal(5, header!.Value.Size);
    }

    [Fact]
    public async Task ReadHeader_EmptyBlob_NotStored_ReturnsNull()
    {
        // C: git_odb_read_header(&empty_blob) → GIT_ENOTFOUND.
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);

        GitObjectHeader? header = await db.ReadHeaderAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken);

        Assert.Null(header);
    }

    [Fact]
    public async Task Dispose_Twice_DoesNotThrow()
    {
        using var odbContext = new GitContext();
        var db = new GitObjectDb(odbContext);
        await db.DisposeAsync();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Lookup_Blob_ReturnsBlobConcreteType()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitObject? obj = await db.LookupAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(obj);
        Assert.IsType<GitBlob>(obj);
        Assert.Equal(GitObjectType.Blob, obj!.Type);
    }

    [Fact]
    public async Task Lookup_T_Generic_ReturnsTypedObject()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitBlob? blob = await db.LookupAsync<GitBlob>(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(blob);
        Assert.Equal("hello", Encoding.ASCII.GetString(blob!.Content.Span));
    }

    [Fact]
    public async Task Lookup_T_WrongType_Throws()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        // C (object.c:121-128): a wrong-type lookup returns GIT_ENOTFOUND
        // "the requested type does not match the type in the ODB".
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await db.LookupAsync<Commit>(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Lookup_T_Nonexistent_ReturnsNull()
    {
        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var missing = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);

        Assert.Null(await db.LookupAsync<GitBlob>(missing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_LooseCommit_ReturnsCommitConcreteType()
    {
        // Build a minimal valid commit body and write it as a loose object.
        GitOid treeOid = GitOid.EmptyTreeSha1;
        byte[] commitBody = Encoding.ASCII.GetBytes(
            $"tree {treeOid}\n" +
            "author A U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n" +
            "\n" +
            "test commit\n");
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Commit, commitBody);

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        Commit? commit = await db.LookupAsync<Commit>(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(commit);
        Assert.Equal(treeOid, commit!.Tree);
        Assert.Equal("test commit\n", commit.Message);
    }

    [Fact]
    public void HashObject_EmptyBlob_MatchesEmptyBlobSha1()
    {
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, [], GitHashAlgorithmKind.Sha1);

        Assert.Equal(GitOid.EmptyBlobSha1, oid);
    }

    [Fact]
    public async Task Write_Blob_RoundTripsThroughRead()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitOid oid = await db.WriteAsync(GitObjectType.Blob, "hello world"u8.ToArray(), TestContext.Current.CancellationToken);

        // The OID matches a direct hash.
        GitOid expected = GitObjectDb.HashObject(GitObjectType.Blob, "hello world"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        Assert.Equal(expected, oid);

        // Re-read the object and verify content.
        GitObject? obj = await db.LookupAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Blob, obj!.Type);
        Assert.Equal(11, obj.Size);
        Assert.Equal("hello world", Encoding.ASCII.GetString(obj.Raw.Span));
    }

    [Fact]
    public async Task Write_ExistingObject_FreshensWithoutRewrite()
    {
        // Pre-write an object the old way.
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());
        string path = Path.Combine(_objectsDir, oid.ToPathString());
        DateTime originalTime = File.GetLastWriteTimeUtc(path);

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        // Wait a bit so the mtime change is observable.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Write the same object again via the ODB API.
        GitOid oid2 = await db.WriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(oid, oid2);
        DateTime newTime = File.GetLastWriteTimeUtc(path);
        Assert.True(newTime > originalTime, $"mtime should advance: {newTime} > {originalTime}");
    }

    [Fact]
    public async Task Write_TreeObject_RoundTrips()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        // A minimal tree with one blob entry: "100644 README.md\0<20-byte oid>"
        GitOid entryOid = GitOid.EmptyBlobSha1;
        var treeBody = new List<byte>();
        byte[] entryLine = "100644 README.md\0"u8.ToArray();
        treeBody.AddRange(entryLine);
        treeBody.AddRange(entryOid.RawBytes.ToArray());

        GitOid oid = await db.WriteAsync(GitObjectType.Tree, treeBody.ToArray(), TestContext.Current.CancellationToken);

        GitTree? tree = await db.LookupAsync<GitTree>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        Assert.Equal(1, tree!.EntryCount);
        GitTreeEntry? entry = tree.EntryByIndex(0);
        Assert.NotNull(entry);
        Assert.Equal("README.md", entry!.Value.Name.ToUtf8String());
        Assert.Equal(GitFileMode.Regular, entry.Value.Mode);
    }

    [Fact]
    public async Task Write_AtomicFileExists()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitOid oid = await db.WriteAsync(GitObjectType.Blob, "atomic test"u8.ToArray(), TestContext.Current.CancellationToken);

        string path = Path.Combine(_objectsDir, oid.ToPathString());
        Assert.True(File.Exists(path), $"loose object file should exist at {path}");

        // No temp files should be left behind.
        string[] tempFiles = Directory.GetFiles(_objectsDir, "tmp_object_*");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task OpenWriteStream_RoundTripsThroughRead()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        byte[] body = "streamed content"u8.ToArray();
        GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

        await using ObjectWriteStream stream = await db.OpenWriteStreamAsync(GitObjectType.Blob, body.Length, TestContext.Current.CancellationToken);
        stream.Write(body);
        GitOid oid = await stream.FinalizeAsync(expectedOid, TestContext.Current.CancellationToken);

        Assert.Equal(expectedOid, oid);

        GitObject? obj = await db.LookupAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Blob, obj!.Type);
        Assert.Equal("streamed content", Encoding.ASCII.GetString(obj.Raw.Span));
    }

    [Fact]
    public async Task OpenWriteStream_SizeMismatch_Throws()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        byte[] body = "too short"u8.ToArray();
        GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

        await using ObjectWriteStream stream = await db.OpenWriteStreamAsync(GitObjectType.Blob, declaredSize: 100, TestContext.Current.CancellationToken);
        stream.Write(body);

        // C (odb.c:1767-1772): a length mismatch at finalize is
        // GIT_ERROR_ODB / -1 ("cannot stream_finalize_write() - Invalid length...").
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await stream.FinalizeAsync(expectedOid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
    }

    [Fact]
    public async Task OpenWriteStream_HashMismatch_Throws()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        byte[] body = "content"u8.ToArray();
        var wrongOid = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);

        await using ObjectWriteStream stream = await db.OpenWriteStreamAsync(GitObjectType.Blob, body.Length, TestContext.Current.CancellationToken);
        stream.Write(body);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await stream.FinalizeAsync(wrongOid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Mismatch, ex.Code);
    }

    [Fact]
    public async Task Write_ReadOnlyBackend_Throws()
    {
        // No loose backend (only a pack backend that doesn't exist) → no write capability.
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);

        await Assert.ThrowsAsync<GitException>(async () =>
            await db.WriteAsync(GitObjectType.Blob, "test"u8.ToArray(), TestContext.Current.CancellationToken));
    }

    private GitOid WriteLooseObjectAtWrongPath(GitObjectType type, byte[] body)
    {
        byte[] compressed = ZlibTestHelpers.CompressLooseObject(OdbTestHelpers.BuildObjectBytes(type, body, body.Length));

        var wrongOid = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);
        string path = Path.Combine(_objectsDir, wrongOid.ToPathString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, compressed);
        return wrongOid;
    }
}
