using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Tests for <see cref="GitObjectDb.ExpandIds"/> — the batch short-OID expansion
/// port of <c>git_odb_expand_ids</c> (odb.c:1144-1211). Covers prefix expansion,
/// type verification, not-found/ambiguous clearing, and the
/// <see cref="GitObjectType.Ext1"/> → <see cref="GitObjectType.Any"/> normalization.
/// </summary>
public sealed class ObjectDbExpandIdsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;

    public ObjectDbExpandIdsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ExpandIds_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch (IOException) { }
    }

    /// <summary>
    /// A full OID with <see cref="GitObjectType.Any"/> is verified: the type is
    /// filled in from <see cref="GitObjectDb.ReadHeader"/>.
    /// </summary>
    [Fact]
    public async Task ExpandIds_FullOid_AnyType_FillsType()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitOdbExpandId[] ids =
        [
            new GitOdbExpandId(oid, oid.HexSize, GitObjectType.Any),
        ];

        await db.ExpandIdsAsync(ids, TestContext.Current.CancellationToken);

        Assert.Equal(oid, ids[0].Id);
        Assert.Equal(oid.HexSize, ids[0].Length);
        Assert.Equal(GitObjectType.Blob, ids[0].Type);
    }

    /// <summary>
    /// A full OID with a specific type that matches is left intact (type filled in).
    /// </summary>
    [Fact]
    public async Task ExpandIds_FullOid_MatchingType_Succeeds()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitOdbExpandId[] ids =
        [
            new GitOdbExpandId(oid, oid.HexSize, GitObjectType.Blob),
        ];

        await db.ExpandIdsAsync(ids, TestContext.Current.CancellationToken);

        Assert.Equal(oid, ids[0].Id);
        Assert.Equal(oid.HexSize, ids[0].Length);
        Assert.Equal(GitObjectType.Blob, ids[0].Type);
    }

    /// <summary>
    /// A full OID whose actual type does not match the requested type is cleared
    /// (matches C's <c>if (query-&gt;type != GIT_OBJECT_ANY &amp;&amp; query-&gt;type != actual_type) error = GIT_ENOTFOUND;</c>).
    /// </summary>
    [Fact]
    public async Task ExpandIds_FullOid_TypeMismatch_ClearsEntry()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitOdbExpandId[] ids =
        [
            new GitOdbExpandId(oid, oid.HexSize, GitObjectType.Commit), // wrong type
        ];

        await db.ExpandIdsAsync(ids, TestContext.Current.CancellationToken);

        // Cleared: zero OID, length 0, type Ext1.
        Assert.True(ids[0].Id.IsZero);
        Assert.Equal(0, ids[0].Length);
        Assert.Equal(GitObjectType.Ext1, ids[0].Type);
    }

    /// <summary>
    /// A short OID (length &lt; HexSize, &gt;= 4) is expanded to the full OID and
    /// the type is filled in.
    /// </summary>
    [Fact]
    public async Task ExpandIds_ShortOid_ExpandsToFullOid()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "expand me"u8.ToArray());
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        // Build a short prefix OID: first 8 hex chars. GitOid.TryParse accepts
        // strings shorter than HexSize and records HexLength = hex.Length (8).
        string shortHex = oid.ToString()[..8];
        var shortOid = GitOid.Parse(shortHex.AsSpan(), GitHashAlgorithmKind.Sha1);
        Assert.Equal(8, shortOid.HexLength); // sanity: it's a prefix, not a full OID
        GitOdbExpandId[] ids =
        [
            new GitOdbExpandId(shortOid, Length: 8, GitObjectType.Any),
        ];

        await db.ExpandIdsAsync(ids, TestContext.Current.CancellationToken);

        Assert.Equal(oid, ids[0].Id);
        Assert.Equal(oid.HexSize, ids[0].Length);
        Assert.Equal(GitObjectType.Blob, ids[0].Type);
    }

    /// <summary>
    /// A short OID that does not match any object is cleared (NotFound).
    /// </summary>
    [Fact]
    public async Task ExpandIds_ShortOid_NotFound_ClearsEntry()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        // A prefix that definitely doesn't exist (all 0xff).
        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var shortOid = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);
        GitOdbExpandId[] ids =
        [
            new GitOdbExpandId(shortOid, Length: 8, GitObjectType.Any),
        ];

        await db.ExpandIdsAsync(ids, TestContext.Current.CancellationToken);

        Assert.True(ids[0].Id.IsZero);
        Assert.Equal(0, ids[0].Length);
        Assert.Equal(GitObjectType.Ext1, ids[0].Type);
    }

    /// <summary>
    /// A full OID that doesn't exist is cleared (ReadHeader returns null).
    /// </summary>
    [Fact]
    public async Task ExpandIds_FullOid_NotFound_ClearsEntry()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xff);
        var missing = GitOid.FromRaw(bytes, GitHashAlgorithmKind.Sha1);
        GitOdbExpandId[] ids =
        [
            new GitOdbExpandId(missing, missing.HexSize, GitObjectType.Any),
        ];

        await db.ExpandIdsAsync(ids, TestContext.Current.CancellationToken);

        Assert.True(ids[0].Id.IsZero);
        Assert.Equal(0, ids[0].Length);
    }

    /// <summary>
    /// <see cref="GitObjectType.Ext1"/> (the C# zero-value, matching C's
    /// <c>GIT_OBJECT__EXT1 = 0</c>) is normalized to <see cref="GitObjectType.Any"/>
    /// before lookup, matching C's <c>if (!query-&gt;type) query-&gt;type = GIT_OBJECT_ANY;</c>.
    /// </summary>
    [Fact]
    public async Task ExpandIds_Ext1Type_NormalizedToAny()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "normalize"u8.ToArray());
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        // Default(OdbExpandId).Type == Ext1 (0). Should be treated as Any.
        GitOdbExpandId[] ids =
        [
            new GitOdbExpandId(oid, oid.HexSize, GitObjectType.Ext1),
        ];

        await db.ExpandIdsAsync(ids, TestContext.Current.CancellationToken);

        // Should succeed (Ext1 normalized to Any → any type matches).
        Assert.Equal(oid, ids[0].Id);
        Assert.Equal(oid.HexSize, ids[0].Length);
        Assert.Equal(GitObjectType.Blob, ids[0].Type);
    }

    /// <summary>
    /// A mix of found, not-found, and type-mismatch entries in a single batch
    /// call — each is handled independently (in-place mutation).
    /// </summary>
    [Fact]
    public async Task ExpandIds_MixedBatch_HandlesEachIndependently()
    {
        GitOid blobOid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "blob1"u8.ToArray());
        byte[] commitBody = Encoding.ASCII.GetBytes(
            $"tree {GitOid.EmptyTreeSha1}\n" +
            "authorA U Thor <author@example.com> 1227814297 +0000\n" +
            "committer C O Mitter <committer@example.com> 1227814297 +0000\n\n" +
            "msg\n");
        GitOid commitOid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Commit, commitBody);

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        byte[] missingBytes = new byte[20];
        Array.Fill(missingBytes, (byte)0xff);
        var missingOid = GitOid.FromRaw(missingBytes, GitHashAlgorithmKind.Sha1);

        GitOdbExpandId[] ids =
        [
            // 0: blob, full OID, Any → success
            new GitOdbExpandId(blobOid, blobOid.HexSize, GitObjectType.Any),
            // 1: commit, full OID, wrong type (Blob) → cleared
            new GitOdbExpandId(commitOid, commitOid.HexSize, GitObjectType.Blob),
            // 2: missing full OID → cleared
            new GitOdbExpandId(missingOid, missingOid.HexSize, GitObjectType.Any),
            // 3: blob, short OID, Any → expanded
            new GitOdbExpandId(blobOid, Length: 8, GitObjectType.Any),
        ];

        await db.ExpandIdsAsync(ids, TestContext.Current.CancellationToken);

        // 0: success
        Assert.Equal(blobOid, ids[0].Id);
        Assert.Equal(GitObjectType.Blob, ids[0].Type);

        // 1: type mismatch → cleared
        Assert.True(ids[1].Id.IsZero);
        Assert.Equal(0, ids[1].Length);

        // 2: not found → cleared
        Assert.True(ids[2].Id.IsZero);
        Assert.Equal(0, ids[2].Length);

        // 3: expanded
        Assert.Equal(blobOid, ids[3].Id);
        Assert.Equal(blobOid.HexSize, ids[3].Length);
        Assert.Equal(GitObjectType.Blob, ids[3].Type);
    }

    /// <summary>
    /// An empty span is a no-op (no throw).
    /// </summary>
    [Fact]
    public async Task ExpandIds_EmptySpan_NoOp()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        await db.ExpandIdsAsync(Array.Empty<GitOdbExpandId>(), TestContext.Current.CancellationToken);
    }
}
