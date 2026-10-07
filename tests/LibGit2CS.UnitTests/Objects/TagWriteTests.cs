using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

public sealed class TagWriteTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;
    private readonly GitContext _context = new();
    private readonly GitObjectDb _db;
    private readonly GitRepository _repo;

    public TagWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_TagWriteTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _objectsDir = Path.Combine(_tempDir, "objects");
        Directory.CreateDirectory(_objectsDir);

        _db = new GitObjectDb(_context);
        _db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);
        _repo = CreateMinimalRepository(_db, _tempDir, _context);
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        await _db.DisposeAsync();
        _context.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GitSignature TestSig()
        => new("Tag Tagger", "tagger@example.com", new GitTime(1227814297, 0));

    private async Task<GitOid> WriteCommit()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        var sig = new GitSignature("A", "a@b.com", new GitTime(100, 0));
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "base\n",
        });
    }

    [Fact]
    public async Task CreateAnnotation_RoundTrips()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _db.LookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitSignature tagger = TestSig();

        GitOid tagOid = await _repo.TagCreateAnnotationAsync("v1.0", commit, tagger, "release v1.0\n", TestContext.Current.CancellationToken);

        GitTag? tag = await _db.LookupAsync<GitTag>(tagOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.Equal("v1.0", tag!.Name);
        Assert.Equal(commitOid, tag.Target);
        Assert.Equal(GitObjectType.Commit, tag.TargetType);
        Assert.Equal("Tag Tagger", tag.Tagger!.Name);
        Assert.Equal("release v1.0\n", tag.Message);
    }

    [Fact]
    public async Task CreateAnnotation_TargetsTree()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("f.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitTree tree = (await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
        GitSignature tagger = TestSig();

        GitOid tagOid = await _repo.TagCreateAnnotationAsync("tree-tag", tree, tagger, "tree tag\n", TestContext.Current.CancellationToken);

        GitTag? tag = await _db.LookupAsync<GitTag>(tagOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.Equal(GitObjectType.Tree, tag!.TargetType);
        Assert.Equal(treeOid, tag.Target);
    }

    [Fact]
    public async Task CreateAnnotation_TargetsBlob()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "data"u8.ToArray(), TestContext.Current.CancellationToken);
        GitBlob blob = (await _db.LookupAsync<GitBlob>(blobOid, TestContext.Current.CancellationToken))!;
        GitSignature tagger = TestSig();

        GitOid tagOid = await _repo.TagCreateAnnotationAsync("blob-tag", blob, tagger, "blob tag\n", TestContext.Current.CancellationToken);

        GitTag? tag = await _db.LookupAsync<GitTag>(tagOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.Equal(GitObjectType.Blob, tag!.TargetType);
        Assert.Equal(blobOid, tag.Target);
    }

    [Fact]
    public async Task CreateAnnotation_InvalidTagName_Throws()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _db.LookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitSignature tagger = TestSig();

        await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.TagCreateAnnotationAsync("-bad", commit, tagger, "msg\n", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.TagCreateAnnotationAsync("HEAD", commit, tagger, "msg\n", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAnnotation_ProducesCorrectBuffer()
    {
        GitOid commitOid = await WriteCommit();
        GitSignature tagger = TestSig();

        string expected =
            $"object {commitOid}\n" +
            "type commit\n" +
            "tag v1.0\n" +
            $"tagger {tagger}\n" +
            "\n" +
            "msg\n";

        // Use CreateFromBuffer to write raw, then read back to verify.
        GitOid tagOid = await _repo.TagCreateFromBufferAsync(expected, cancellationToken: TestContext.Current.CancellationToken);
        GitTag? tag = await _db.LookupAsync<GitTag>(tagOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.Equal("v1.0", tag!.Name);
        Assert.Equal(commitOid, tag.Target);
        Assert.Equal("msg\n", tag.Message);
    }

    [Fact]
    public async Task CreateFromBuffer_RoundTrips()
    {
        GitOid commitOid = await WriteCommit();
        GitSignature tagger = TestSig();

        string buffer =
            $"object {commitOid}\n" +
            "type commit\n" +
            "tag test\n" +
            $"tagger {tagger}\n" +
            "\n" +
            "buffer test\n";

        GitOid oid = await _repo.TagCreateFromBufferAsync(buffer, cancellationToken: TestContext.Current.CancellationToken);

        GitTag? tag = await _db.LookupAsync<GitTag>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.Equal("test", tag!.Name);
        Assert.Equal("buffer test\n", tag.Message);
    }

    [Fact]
    public async Task Create_Annotated_CreatesRef()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _db.LookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitSignature tagger = TestSig();

        GitOid tagOid = await _repo.TagCreateAsync("v1.0", commit, tagger, "msg\n", cancellationToken: TestContext.Current.CancellationToken);

        // The ref should point at the tag object.
        GitReference? tagRef = await _repo.ReferenceLookupAsync("refs/tags/v1.0", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tagRef);
        Assert.False(tagRef!.IsSymbolic);
        Assert.Equal(tagOid, ((GitDirectReference)tagRef).Target);
    }

    [Fact]
    public async Task Create_Lightweight_CreatesRef()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _db.LookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;

        await _repo.TagCreateAsync("v1.0", commit, null, null, cancellationToken: TestContext.Current.CancellationToken);

        // Lightweight tag: ref points directly at the target.
        GitReference? tagRef = await _repo.ReferenceLookupAsync("refs/tags/v1.0", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tagRef);
        Assert.False(tagRef!.IsSymbolic);
        Assert.Equal(commitOid, ((GitDirectReference)tagRef).Target);
    }

    [Fact]
    public async Task Delete_RemovesRef()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _db.LookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;

        await _repo.TagCreateAsync("v1.0", commit, null, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(await _repo.ReferenceLookupAsync("refs/tags/v1.0", cancellationToken: TestContext.Current.CancellationToken));

        await _repo.TagDeleteAsync("v1.0", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await _repo.ReferenceLookupAsync("refs/tags/v1.0", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAnnotation_EmptyMessage_RoundTrips()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _db.LookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitSignature tagger = TestSig();

        GitOid tagOid = await _repo.TagCreateAnnotationAsync("empty", commit, tagger, "", TestContext.Current.CancellationToken);

        GitTag? tag = await _db.LookupAsync<GitTag>(tagOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.Equal("empty", tag!.Name);
    }

    [Fact]
    public async Task CreateAnnotation_ProducesCorrectOid()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _db.LookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitSignature tagger = TestSig();
        string message = "tag msg\n";

        // Build expected buffer and compute expected OID.
        string expectedBuffer =
            $"object {commitOid}\n" +
            "type commit\n" +
            "tag v1.0\n" +
            $"tagger {tagger}\n" +
            "\n" +
            message;

        GitOid expectedOid = GitObjectDb.HashObject(
            GitObjectType.Tag,
            Encoding.ASCII.GetBytes(expectedBuffer),
            GitHashAlgorithmKind.Sha1);

        GitOid actualOid = await _repo.TagCreateAnnotationAsync("v1.0", commit, tagger, message, TestContext.Current.CancellationToken);

        Assert.Equal(expectedOid, actualOid);
    }

    private static GitRepository CreateMinimalRepository(GitObjectDb db, string gitDir, GitContext ctx)
    {
        var refDb = new RefDatabase();
        var refBackend = new FileRefBackend(gitDir, gitDir, GitHashAlgorithmKind.Sha1);
        refDb.SetBackend(refBackend);
        var refs = new GitReferences(refDb);

        return new GitRepository(gitdir: gitDir, commondir: gitDir, workdir: null, isBare: true, isWorktree: false, objectFormat: GitHashAlgorithmKind.Sha1, @namespace: null, objects: db, config: new LibGit2CS.Config.GitConfiguration(ctx), context: ctx, refs: refs);
    }
}
