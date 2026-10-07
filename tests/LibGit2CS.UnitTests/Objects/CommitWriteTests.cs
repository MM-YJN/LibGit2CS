using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

public sealed class CommitWriteTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;
    private readonly GitContext _context = new();
    private readonly GitObjectDb _db;
    private readonly GitRepository _repo;

    public CommitWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CommitWriteTests_" + Guid.NewGuid().ToString("N")[..8]);
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
        => new("A U Thor", "author@example.com", new GitTime(1227814297, 0));

    [Fact]
    public async Task Create_RootCommit_RoundTrips()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = _repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitSignature sig = TestSig();
        GitOid commitOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial commit\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Commit? commit = await _db.LookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Equal(treeOid, commit!.Tree);
        Assert.Empty(commit.Parents);
        Assert.Equal("A U Thor", commit.Author.Name);
        Assert.Equal("initial commit\n", commit.Message);
        Assert.Equal(1227814297, commit.Time.Seconds);
    }

    [Fact]
    public async Task Create_WithParent_RoundTrips()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "v1"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = _repo.NewTreeBuilder();
        await treeBld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitSignature sig = TestSig();
        GitOid parentOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "first\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Create child commit with the first as parent.
        GitOid blobOid2 = await _db.WriteAsync(GitObjectType.Blob, "v2"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld2 = _repo.NewTreeBuilder();
        await treeBld2.InsertAsync("file.txt", blobOid2, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid2 = await treeBld2.WriteAsync(CancellationToken.None);

        GitOid childOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid2,
            Parents = [parentOid],
            Author = sig,
            Committer = sig,
            Message = "second\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Commit? child = await _db.LookupAsync<Commit>(childOid, TestContext.Current.CancellationToken);
        Assert.NotNull(child);
        Assert.Single(child!.Parents);
        Assert.Equal(parentOid, child.Parents[0]);
    }

    [Fact]
    public async Task Create_WithEncoding_RoundTrips()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();

        GitOid commitOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            MessageEncoding = "UTF-8",
            Message = "encoded commit\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Commit? commit = await _db.LookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Equal("UTF-8", commit!.Encoding);
    }

    [Fact]
    public async Task Create_NonExistentTree_Throws()
    {
        GitSignature sig = TestSig();
        var fakeTree = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);

        await Assert.ThrowsAsync<GitException>(async () => await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = fakeTree,
            Author = sig,
            Committer = sig,
            Message = "x\n",
        }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_NonExistentParent_Throws()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();
        var fakeParent = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);

        await Assert.ThrowsAsync<GitException>(async () => await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [fakeParent],
            Author = sig,
            Committer = sig,
            Message = "x\n",
        }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_WithUpdateRef_CreatesRef()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();

        GitOid commitOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "x\n",
            UpdateRef = "HEAD",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // The ref should now point at the new commit.
        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.False(head!.IsSymbolic);
        Assert.Equal(commitOid, ((GitDirectReference)head).Target);
    }

    [Fact]
    public void Create_ProducesCorrectBuffer()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();

        string expectedBuffer =
            $"tree {treeOid}\n" +
            $"author {sig}\n" +
            $"committer {sig}\n" +
            "\n" +
            "test\n";

        byte[] actualBuffer = Commit.CreateBuffer(sig, sig, null, "test\n", treeOid, []);

        Assert.Equal(expectedBuffer, Encoding.ASCII.GetString(actualBuffer));
    }

    [Fact]
    public void CreateBuffer_WithParents_IncludesParentLines()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        var parentOid = GitOid.Parse("a".PadRight(40, '0').AsSpan(), GitHashAlgorithmKind.Sha1);
        GitSignature sig = TestSig();

        string expected =
            $"tree {treeOid}\n" +
            $"parent {parentOid}\n" +
            $"author {sig}\n" +
            $"committer {sig}\n" +
            "\n" +
            "merge\n";

        byte[] actual = Commit.CreateBuffer(sig, sig, null, "merge\n", treeOid, [parentOid]);

        Assert.Equal(expected, Encoding.ASCII.GetString(actual));
    }

    [Fact]
    public void CreateBuffer_WithEncoding_IncludesEncodingHeader()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();

        string expected =
            $"tree {treeOid}\n" +
            $"author {sig}\n" +
            $"committer {sig}\n" +
            "encoding UTF-8\n" +
            "\n" +
            "msg\n";

        byte[] actual = Commit.CreateBuffer(sig, sig, "UTF-8", "msg\n", treeOid, []);

        Assert.Equal(expected, Encoding.ASCII.GetString(actual));
    }

    [Fact]
    public async Task Create_ProducesCorrectOid()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();
        string message = "test commit\n";

        // Compute expected OID from the buffer.
        byte[] buffer = Commit.CreateBuffer(sig, sig, null, message, treeOid, []);
        GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Commit, buffer, GitHashAlgorithmKind.Sha1);

        GitOid actualOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = message,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectedOid, actualOid);
    }

    [Fact]
    public async Task Amend_NewMessage_RoundTrips()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();

        GitOid originalOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "original\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Commit original = (await _db.LookupAsync<Commit>(originalOid, TestContext.Current.CancellationToken))!;
        GitOid amendedOid = await Commit.AmendAsync(original, message: "amended\n", cancellationToken: TestContext.Current.CancellationToken);

        Commit? amended = await _db.LookupAsync<Commit>(amendedOid, TestContext.Current.CancellationToken);
        Assert.NotNull(amended);
        Assert.Equal("amended\n", amended!.Message);
        Assert.Equal(original.Tree, amended.Tree);
        Assert.Equal(original.Parents, amended.Parents);
    }

    [Fact]
    public async Task Amend_NewTree_UsesNewTree()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("f.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid tree1 = await bld.WriteAsync(CancellationToken.None);

        using GitTreeBuilder bld2 = _repo.NewTreeBuilder();
        await bld2.InsertAsync("g.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid tree2 = await bld2.WriteAsync(CancellationToken.None);

        GitSignature sig = TestSig();
        GitOid originalOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree1,
            Author = sig,
            Committer = sig,
            Message = "orig\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Commit original = (await _db.LookupAsync<Commit>(originalOid, TestContext.Current.CancellationToken))!;
        GitOid amendedOid = await Commit.AmendAsync(original, tree: tree2, cancellationToken: TestContext.Current.CancellationToken);

        Commit amended = (await _db.LookupAsync<Commit>(amendedOid, TestContext.Current.CancellationToken))!;
        Assert.Equal(tree2, amended.Tree);
    }

    [Fact]
    public async Task Amend_NewAuthor_UsesNewAuthor()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        var sig1 = new GitSignature("Author One", "one@example.com", new GitTime(100, 0));
        var sig2 = new GitSignature("Author Two", "two@example.com", new GitTime(200, 0));

        GitOid originalOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig1,
            Committer = sig1,
            Message = "orig\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Commit original = (await _db.LookupAsync<Commit>(originalOid, TestContext.Current.CancellationToken))!;
        GitOid amendedOid = await Commit.AmendAsync(original, author: sig2, cancellationToken: TestContext.Current.CancellationToken);

        Commit amended = (await _db.LookupAsync<Commit>(amendedOid, TestContext.Current.CancellationToken))!;
        Assert.Equal("Author Two", amended.Author.Name);
        Assert.Equal("Author One", amended.Committer.Name); // committer unchanged
    }

    [Fact]
    public async Task CreateWithSignature_RoundTrips()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();

        // Build a commit content (without signature).
        string content =
            $"tree {treeOid}\n" +
            $"author {sig}\n" +
            $"committer {sig}\n" +
            "\n" +
            "signed commit\n";

        string signature = "-----BEGIN PGP SIGNATURE-----\nfake signature\n-----END PGP SIGNATURE-----\n";

        GitOid oid = await _repo.CommitCreateWithSignatureAsync(content, signature, null, TestContext.Current.CancellationToken);

        Commit? commit = await _db.LookupAsync<Commit>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Equal("signed commit\n", commit!.Message);

        // The signature should be in the gpgsig header field.
        string? extractedSig = commit.HeaderField("gpgsig");
        Assert.NotNull(extractedSig);
        Assert.Contains("fake signature", extractedSig);
    }

    [Fact]
    public async Task CreateWithSignature_NoSignature_WritesAsIs()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();

        string content =
            $"tree {treeOid}\n" +
            $"author {sig}\n" +
            $"committer {sig}\n" +
            "\n" +
            "unsigned\n";

        GitOid oid = await _repo.CommitCreateWithSignatureAsync(content, null, null, TestContext.Current.CancellationToken);

        Commit? commit = await _db.LookupAsync<Commit>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Equal("unsigned\n", commit!.Message);
    }

    [Fact]
    public async Task CreateWithSignature_MalformedContent_Throws()
    {
        await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CommitCreateWithSignatureAsync("no separator here", "sig", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_MultipleParents_RoundTrips()
    {
        GitOid treeOid = GitOid.EmptyTreeSha1;
        GitSignature sig = TestSig();

        GitOid parent1 = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "p1\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitOid parent2 = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "p2\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitOid mergeOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parent1, parent2],
            Author = sig,
            Committer = sig,
            Message = "merge\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Commit? merge = await _db.LookupAsync<Commit>(mergeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(merge);
        Assert.Equal(2, merge!.Parents.Count);
        Assert.Equal(parent1, merge.Parents[0]);
        Assert.Equal(parent2, merge.Parents[1]);
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
