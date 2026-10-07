using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Objects;

public sealed class TreeBuilderTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;
    private readonly GitContext _context = new();
    private readonly GitObjectDb _db;
    private readonly GitRepository _repo;

    public TreeBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_TreeBuilderTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _objectsDir = Path.Combine(_tempDir, "objects");
        Directory.CreateDirectory(_objectsDir);

        _db = new GitObjectDb(_context);
        _db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        // Create a minimal repository for TreeBuilder (needs Objects + ObjectFormat).
        _repo = CreateMinimalRepository(_db, _context);
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

    [Fact]
    public void New_Empty_HasZeroEntries()
    {
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        Assert.Equal(0, bld.EntryCount);
    }

    [Fact]
    public async Task New_FromSourceTree_CopiesEntries()
    {
        // Write a blob and a tree pointing at it.
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "content"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld1 = _repo.NewTreeBuilder();
        await bld1.InsertAsync("file.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await bld1.WriteAsync(CancellationToken.None);
        GitTree? tree = await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        // Create a new builder from the source tree.
        using GitTreeBuilder bld2 = _repo.NewTreeBuilder(tree);
        Assert.Equal(1, bld2.EntryCount);
        Assert.NotNull(bld2.Get("file.txt"));
    }

    [Fact]
    public async Task Insert_NewEntry_AddsToBuilder()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("README.md", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, bld.EntryCount);
        GitTreeEntry? entry = bld.Get("README.md");
        Assert.NotNull(entry);
        Assert.Equal(blobOid, entry!.Value.Id);
        Assert.Equal(GitFileMode.Regular, entry.Value.Mode);
    }

    [Fact]
    public async Task Insert_ExistingName_UpdatesInPlace()
    {
        GitOid blobOid1 = await _db.WriteAsync(GitObjectType.Blob, "v1"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobOid2 = await _db.WriteAsync(GitObjectType.Blob, "v2"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid1, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        await bld.InsertAsync("file.txt", blobOid2, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, bld.EntryCount);
        GitTreeEntry? entry = bld.Get("file.txt");
        Assert.Equal(blobOid2, entry!.Value.Id);
    }

    [Fact]
    public async Task Insert_InvalidFilemode_Throws()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await Assert.ThrowsAsync<GitException>(async () => await bld.InsertAsync("f", blobOid, (GitFileMode)0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_EmptyName_Throws()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await Assert.ThrowsAsync<GitException>(async () => await bld.InsertAsync("", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_NameWithSlash_Throws()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await Assert.ThrowsAsync<GitException>(async () => await bld.InsertAsync("a/b", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_DotGit_Throws()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await Assert.ThrowsAsync<GitException>(async () => await bld.InsertAsync(".git", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_DotDot_Throws()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await Assert.ThrowsAsync<GitException>(async () => await bld.InsertAsync("..", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_ZeroOid_Throws()
    {
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await Assert.ThrowsAsync<GitException>(async () => await bld.InsertAsync("f", GitOid.Empty, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_NonExistentObject_Throws()
    {
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        var fakeOid = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);
        await Assert.ThrowsAsync<GitException>(async () => await bld.InsertAsync("f", fakeOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remove_ExistingEntry_RemovesIt()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("a", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        await bld.InsertAsync("b", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);

        bld.Remove("a");

        Assert.Equal(1, bld.EntryCount);
        Assert.Null(bld.Get("a"));
        Assert.NotNull(bld.Get("b"));
    }

    [Fact]
    public void Remove_NonExistent_Throws()
    {
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        Assert.Throws<GitException>(() => bld.Remove("nope"));
    }

    [Fact]
    public async Task Filter_RemovesMatchingEntries()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await Bld1InsertAsync(bld, "keep.txt", blobOid);
        await Bld1InsertAsync(bld, "remove.txt", blobOid);
        await Bld1InsertAsync(bld, "keep2.txt", blobOid);

        bld.Filter(e => GitPath.ComparePrefix(e.Name, GitPath.FromUtf8String("remove")) == 0);

        Assert.Equal(2, bld.EntryCount);
        Assert.Null(bld.Get("remove.txt"));
    }

    private static async Task Bld1InsertAsync(GitTreeBuilder bld, string name, GitOid oid)
        => await bld.InsertAsync(name, oid, GitFileMode.Regular);

    [Fact]
    public async Task Clear_RemovesAllEntries()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("a", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        await bld.InsertAsync("b", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);

        bld.Clear();

        Assert.Equal(0, bld.EntryCount);
    }

    [Fact]
    public async Task Write_EmptyTree_RoundTrips()
    {
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        GitOid oid = await bld.WriteAsync(CancellationToken.None);

        GitTree? tree = await _db.LookupAsync<GitTree>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        Assert.Equal(0, tree!.EntryCount);
    }

    [Fact]
    public async Task Write_SingleEntry_RoundTrips()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("README.md", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await bld.WriteAsync(CancellationToken.None);

        GitTree? tree = await _db.LookupAsync<GitTree>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        Assert.Equal(1, tree!.EntryCount);
        GitTreeEntry? entry = tree["README.md"];
        Assert.NotNull(entry);
        Assert.Equal(blobOid, entry!.Value.Id);
        Assert.Equal(GitFileMode.Regular, entry.Value.Mode);
    }

    [Fact]
    public async Task Write_MultipleEntries_SortedInCanonicalOrder()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        // Insert in non-sorted order.
        await bld.InsertAsync("z", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        await bld.InsertAsync("a", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        await bld.InsertAsync("m", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);

        GitOid oid = await bld.WriteAsync(CancellationToken.None);

        GitTree? tree = await _db.LookupAsync<GitTree>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        string[] names = tree!.Select(e => e.Name.ToUtf8String()).ToArray();
        Assert.Equal(["a", "m", "z"], names);
    }

    [Fact]
    public async Task Write_TreeAndBlobEntries_SortedWithSlashSubtlety()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        // Create a subtree to reference.
        using GitTreeBuilder subBld = _repo.NewTreeBuilder();
        await subBld.InsertAsync("inner.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid subOid = await subBld.WriteAsync(CancellationToken.None);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        // Insert "foo" (blob) and "foo.txt" (blob) and "foo" as a tree name.
        // With the canonical tree sort, "foo" (tree, treated as "foo/") sorts after "foo.txt".
        await bld.InsertAsync("foo.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        await bld.InsertAsync("foo", subOid, GitFileMode.Tree, cancellationToken: TestContext.Current.CancellationToken);

        GitOid oid = await bld.WriteAsync(CancellationToken.None);

        GitTree? tree = await _db.LookupAsync<GitTree>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        string[] names = tree!.Select(e => e.Name.ToUtf8String()).ToArray();
        // "foo.txt" sorts before "foo" (as directory "foo/") because '.' (0x2e) < '/' (0x2f).
        Assert.Equal(["foo.txt", "foo"], names);
    }

    [Fact]
    public async Task Write_ProducesCorrectOid()
    {
        // Write a known tree: one entry "README.md" mode 100644 pointing at the
        // empty blob. C's check_entry validates the target via git_object__is_valid
        // (tree.c:488-504), so the empty blob must be WRITTEN first — the ODB no
        // longer invents it.
        _ = await _repo.ObjectWriteAsync(GitObjectType.Blob, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("README.md", GitOid.EmptyBlobSha1, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await bld.WriteAsync(CancellationToken.None);

        // The tree OID should be deterministic. Compute it manually.
        var expectedBody = new List<byte>();
        byte[] entryLine = "100644 README.md\0"u8.ToArray();
        expectedBody.AddRange(entryLine);
        expectedBody.AddRange(GitOid.EmptyBlobSha1.RawBytes.ToArray());
        GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Tree, expectedBody.ToArray(), GitHashAlgorithmKind.Sha1);

        Assert.Equal(expectedOid, oid);
    }

    [Fact]
    public async Task Write_ExecutableEntry_UsesCorrectMode()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "#!/bin/sh"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("script.sh", blobOid, GitFileMode.Executable, cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await bld.WriteAsync(CancellationToken.None);

        GitTree? tree = await _db.LookupAsync<GitTree>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        GitTreeEntry? entry = tree!["script.sh"];
        Assert.NotNull(entry);
        Assert.Equal(GitFileMode.Executable, entry!.Value.Mode);
    }

    [Fact]
    public async Task Write_SymlinkEntry_UsesCorrectMode()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "/target/path"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("link", blobOid, GitFileMode.Symlink, cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await bld.WriteAsync(CancellationToken.None);

        GitTree? tree = await _db.LookupAsync<GitTree>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        GitTreeEntry? entry = tree!["link"];
        Assert.NotNull(entry);
        Assert.Equal(GitFileMode.Symlink, entry!.Value.Mode);
    }

    [Fact]
    public async Task Write_GitLinkEntry_DoesNotCheckOdbExistence()
    {
        // Gitlinks (submodule references) are not checked against the ODB.
        var fakeCommitOid = GitOid.Parse("a".PadRight(40, '0').AsSpan(), GitHashAlgorithmKind.Sha1);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("submodule", fakeCommitOid, GitFileMode.GitLink, cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await bld.WriteAsync(CancellationToken.None);

        GitTree? tree = await _db.LookupAsync<GitTree>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        GitTreeEntry? entry = tree!["submodule"];
        Assert.NotNull(entry);
        Assert.Equal(GitFileMode.GitLink, entry!.Value.Mode);
    }

    // --- Regression: tree builder must encode names as UTF-8 ---

    [Fact]
    public async Task Write_NonAsciiName_RoundTripsAndEncodesAsUtf8()
    {
        // A non-ASCII entry name must survive a build→write→parse round-trip
        // byte-for-byte, with the builder serializing the name as UTF-8 (to
        // match the reader). The OID is computed over the on-wire bytes, so it
        // pins the encoding: under Latin-1 the OID would differ.
        const string name = "汉语.txt";
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync(name, blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid oid = await bld.WriteAsync(CancellationToken.None);

        // Expected body: "100644 " + UTF-8(name) + NUL + raw-oid, hashed as a tree.
        var expectedBody = new List<byte>();
        expectedBody.AddRange("100644 "u8);
        expectedBody.AddRange(Encoding.UTF8.GetBytes(name));
        expectedBody.Add(0);
        expectedBody.AddRange(blobOid.RawBytes.ToArray());
        GitOid expectedOid = GitObjectDb.HashObject(GitObjectType.Tree, expectedBody.ToArray(), GitHashAlgorithmKind.Sha1);
        Assert.Equal(expectedOid, oid);

        GitTree? tree = await _db.LookupAsync<GitTree>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        Assert.Equal(1, tree!.EntryCount);
        GitTreeEntry? entry = tree[name];
        Assert.NotNull(entry);
        Assert.Equal(name, entry!.Value.Name.ToUtf8String());
        Assert.Equal(blobOid, entry.Value.Id);
    }

    // --- Tree.CreateUpdated tests ---

    [Fact]
    public async Task CreateUpdated_NoUpdates_ReturnsBaselineOid()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitTree? tree = await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);

        GitOid result = await _repo.TreeCreateUpdatedAsync(tree, [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(treeOid, result);
    }

    [Fact]
    public async Task CreateUpdated_UpsertNewEntry_AddsEntry()
    {
        GitOid blobOid1 = await _db.WriteAsync(GitObjectType.Blob, "1"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobOid2 = await _db.WriteAsync(GitObjectType.Blob, "2"u8.ToArray(), TestContext.Current.CancellationToken);

        // Baseline: {file1.txt}
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file1.txt", blobOid1, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitTree? tree = await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);

        // Add file2.txt
        GitOid result = await _repo.TreeCreateUpdatedAsync(tree, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "file2.txt", blobOid2, GitFileMode.Regular)
        ], cancellationToken: TestContext.Current.CancellationToken);

        GitTree? newTree = await _db.LookupAsync<GitTree>(result, TestContext.Current.CancellationToken);
        Assert.NotNull(newTree);
        Assert.Equal(2, newTree!.EntryCount);
        Assert.NotNull(newTree["file1.txt"]);
        Assert.NotNull(newTree["file2.txt"]);
    }

    [Fact]
    public async Task CreateUpdated_UpsertExistingEntry_UpdatesOid()
    {
        GitOid blobOid1 = await _db.WriteAsync(GitObjectType.Blob, "v1"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobOid2 = await _db.WriteAsync(GitObjectType.Blob, "v2"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid1, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitTree? tree = await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);

        GitOid result = await _repo.TreeCreateUpdatedAsync(tree, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "file.txt", blobOid2, GitFileMode.Regular)
        ], cancellationToken: TestContext.Current.CancellationToken);

        GitTree? newTree = await _db.LookupAsync<GitTree>(result, TestContext.Current.CancellationToken);
        Assert.NotNull(newTree);
        Assert.Equal(blobOid2, newTree!["file.txt"]!.Value.Id);
    }

    [Fact]
    public async Task CreateUpdated_RemoveEntry_RemovesIt()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("keep.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        await bld.InsertAsync("remove.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitTree? tree = await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);

        GitOid result = await _repo.TreeCreateUpdatedAsync(tree, [
            new GitTreeUpdate(GitTreeUpdateAction.Remove, "remove.txt")
        ], cancellationToken: TestContext.Current.CancellationToken);

        GitTree? newTree = await _db.LookupAsync<GitTree>(result, TestContext.Current.CancellationToken);
        Assert.NotNull(newTree);
        Assert.Equal(1, newTree!.EntryCount);
        Assert.NotNull(newTree["keep.txt"]);
        Assert.Null(newTree["remove.txt"]);
    }

    [Fact]
    public async Task CreateUpdated_NestedPath_CreatesSubtrees()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "nested"u8.ToArray(), TestContext.Current.CancellationToken);

        // Start from an empty tree.
        GitOid result = await _repo.TreeCreateUpdatedAsync(null, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "src/main.cs", blobOid, GitFileMode.Regular)
        ], cancellationToken: TestContext.Current.CancellationToken);

        GitTree? tree = await _db.LookupAsync<GitTree>(result, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        Assert.Equal(1, tree!.EntryCount);

        // "src" should be a tree entry.
        GitTreeEntry? srcEntry = tree["src"];
        Assert.NotNull(srcEntry);
        Assert.True(srcEntry!.Value.IsTree);

        // Load the subtree and verify "main.cs".
        GitTree? subTree = await _db.LookupAsync<GitTree>(srcEntry.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(subTree);
        Assert.Equal(1, subTree!.EntryCount);
        Assert.NotNull(subTree["main.cs"]);
    }

    [Fact]
    public async Task CreateUpdated_NestedPath_ModifiesExistingSubtree()
    {
        GitOid blobOid1 = await _db.WriteAsync(GitObjectType.Blob, "1"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobOid2 = await _db.WriteAsync(GitObjectType.Blob, "2"u8.ToArray(), TestContext.Current.CancellationToken);

        // Create baseline with src/existing.cs
        GitOid baselineOid = await _repo.TreeCreateUpdatedAsync(null, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "src/existing.cs", blobOid1, GitFileMode.Regular)
        ], cancellationToken: TestContext.Current.CancellationToken);
        GitTree? baseline = await _db.LookupAsync<GitTree>(baselineOid, TestContext.Current.CancellationToken);

        // Add src/new.cs
        GitOid result = await _repo.TreeCreateUpdatedAsync(baseline, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "src/new.cs", blobOid2, GitFileMode.Regular)
        ], cancellationToken: TestContext.Current.CancellationToken);

        GitTree? tree = await _db.LookupAsync<GitTree>(result, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        GitTreeEntry? srcEntry = tree!["src"];
        Assert.NotNull(srcEntry);
        GitTree? subTree = await _db.LookupAsync<GitTree>(srcEntry!.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(subTree);
        Assert.Equal(2, subTree!.EntryCount);
        Assert.NotNull(subTree["existing.cs"]);
        Assert.NotNull(subTree["new.cs"]);
    }

    [Fact]
    public async Task CreateUpdated_MultipleUpdates_BatchApply()
    {
        GitOid blobOid1 = await _db.WriteAsync(GitObjectType.Blob, "1"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobOid2 = await _db.WriteAsync(GitObjectType.Blob, "2"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid blobOid3 = await _db.WriteAsync(GitObjectType.Blob, "3"u8.ToArray(), TestContext.Current.CancellationToken);

        GitOid baselineOid = await _repo.TreeCreateUpdatedAsync(null, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "a.txt", blobOid1, GitFileMode.Regular),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "b.txt", blobOid2, GitFileMode.Regular),
        ], cancellationToken: TestContext.Current.CancellationToken);
        GitTree? baseline = await _db.LookupAsync<GitTree>(baselineOid, TestContext.Current.CancellationToken);

        // Remove a.txt, update b.txt, add c.txt
        GitOid result = await _repo.TreeCreateUpdatedAsync(baseline, [
            new GitTreeUpdate(GitTreeUpdateAction.Remove, "a.txt"),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "b.txt", blobOid3, GitFileMode.Regular),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "c.txt", blobOid3, GitFileMode.Regular),
        ], cancellationToken: TestContext.Current.CancellationToken);

        GitTree? tree = await _db.LookupAsync<GitTree>(result, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        Assert.Equal(2, tree!.EntryCount);
        Assert.Null(tree["a.txt"]);
        Assert.Equal(blobOid3, tree["b.txt"]!.Value.Id);
        Assert.NotNull(tree["c.txt"]);
    }

    [Fact]
    public async Task CreateUpdated_DFConflict_Throws()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        // Create a tree with "foo" as a blob.
        GitOid baselineOid = await _repo.TreeCreateUpdatedAsync(null, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "foo", blobOid, GitFileMode.Regular),
        ], cancellationToken: TestContext.Current.CancellationToken);
        GitTree? baseline = await _db.LookupAsync<GitTree>(baselineOid, TestContext.Current.CancellationToken);

        // Try to add "foo/bar" — "foo" is a blob, not a tree → D/F conflict.
        await Assert.ThrowsAsync<GitException>(async () => await _repo.TreeCreateUpdatedAsync(baseline, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "foo/bar", blobOid, GitFileMode.Regular),
        ], cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- Tree.WriteIndex tests ---

    [Fact]
    public async Task WriteIndex_EmptyIndex_WritesEmptyTree()
    {
        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        GitOid oid = await GitTree.WriteIndexAsync(_repo, index, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitOid.EmptyTreeSha1, oid);
    }

    [Fact]
    public async Task WriteIndex_SingleEntry_RoundTrips()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);

        // Write a blob, build a tree with it, read tree into index, then WriteIndex.
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitTree tree = (await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        await index.ReadTreeAsync(tree, CancellationToken.None);
        GitOid result = await GitTree.WriteIndexAsync(_repo, index, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(treeOid, result);
    }

    [Fact]
    public async Task WriteIndex_NestedPaths_CreatesSubtrees()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "nested"u8.ToArray(), TestContext.Current.CancellationToken);

        // Build a tree with nested paths via CreateUpdated, then read into index.
        GitOid treeOid = await _repo.TreeCreateUpdatedAsync(null, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "src/main.cs", blobOid, GitFileMode.Regular),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "src/util/helper.cs", blobOid, GitFileMode.Regular),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "README.md", blobOid, GitFileMode.Regular),
        ], cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = (await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        await index.ReadTreeAsync(tree, CancellationToken.None);
        GitOid result = await GitTree.WriteIndexAsync(_repo, index, cancellationToken: TestContext.Current.CancellationToken);

        // The written tree should match the original.
        Assert.Equal(treeOid, result);

        // Verify the structure is preserved.
        GitTree resultTree = (await _db.LookupAsync<GitTree>(result, TestContext.Current.CancellationToken))!;
        Assert.Equal(2, resultTree.EntryCount); // "src" + "README.md"
        Assert.True(resultTree["src"]!.Value.IsTree);
        GitTree subTree = (await _db.LookupAsync<GitTree>(resultTree["src"]!.Value.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(2, subTree.EntryCount);
        Assert.NotNull(await subTree.EntryByPathAsync("main.cs", TestContext.Current.CancellationToken));
        Assert.NotNull(await subTree.EntryByPathAsync("util/helper.cs", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteIndex_DeepNesting_RoundTrips()
    {
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "deep"u8.ToArray(), TestContext.Current.CancellationToken);

        GitOid treeOid = await _repo.TreeCreateUpdatedAsync(null, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "a/b/c/d/e/f.txt", blobOid, GitFileMode.Regular),
        ], cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = (await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        await index.ReadTreeAsync(tree, CancellationToken.None);
        GitOid result = await GitTree.WriteIndexAsync(_repo, index, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(treeOid, result);
    }

    [Fact]
    public async Task WriteIndex_RemoveAfterReadTree_ExcludesRemovedPath()
    {
        // Regression: GitIndex.Remove must invalidate the tree cache, else
        // WriteIndexAsync's fast path returns the stale cached OID and the
        // removed entry survives into the written tree.
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        GitOid treeOid = await _repo.TreeCreateUpdatedAsync(null, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "a.txt", blobOid, GitFileMode.Regular),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "b.txt", blobOid, GitFileMode.Regular),
        ], cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = (await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        // ReadTreeAsync populates a *valid* tree cache (mirrors git reset --hard).
        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        await index.ReadTreeAsync(tree, CancellationToken.None);

        Assert.True(index.Remove("a.txt"));

        GitOid result = await GitTree.WriteIndexAsync(_repo, index, cancellationToken: TestContext.Current.CancellationToken);

        // Cache must have been invalidated → tree rebuilt without a.txt.
        Assert.NotEqual(treeOid, result);
        GitTree resultTree = (await _db.LookupAsync<GitTree>(result, TestContext.Current.CancellationToken))!;
        Assert.Null(resultTree["a.txt"]);
        Assert.NotNull(resultTree["b.txt"]);
    }

    [Fact]
    public async Task WriteIndex_RemoveNestedPath_InvalidatesSubtree()
    {
        // Regression: removing a nested entry must invalidate the subtree
        // cache node so WriteIndexAsync rebuilds that subtree.
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);

        GitOid treeOid = await _repo.TreeCreateUpdatedAsync(null, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "src/a.txt", blobOid, GitFileMode.Regular),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "src/b.txt", blobOid, GitFileMode.Regular),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "root.txt", blobOid, GitFileMode.Regular),
        ], cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = (await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        await index.ReadTreeAsync(tree, CancellationToken.None);

        Assert.True(index.Remove("src/a.txt"));

        GitOid result = await GitTree.WriteIndexAsync(_repo, index, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(treeOid, result);
        GitTree resultTree = (await _db.LookupAsync<GitTree>(result, TestContext.Current.CancellationToken))!;
        Assert.NotNull(resultTree["root.txt"]);
        Assert.Null(await resultTree.EntryByPathAsync("src/a.txt", TestContext.Current.CancellationToken));
        Assert.NotNull(await resultTree.EntryByPathAsync("src/b.txt", TestContext.Current.CancellationToken));
    }

    // --- TreeCache.Write tests ---

    [Fact]
    public async Task TreeCacheWrite_RoundTripsThroughRead()
    {
        // Build a tree cache from a tree with nested entries.
        GitOid blobOid = await _db.WriteAsync(GitObjectType.Blob, "x"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid treeOid = await _repo.TreeCreateUpdatedAsync(null, [
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "src/a.txt", blobOid, GitFileMode.Regular),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "src/b.txt", blobOid, GitFileMode.Regular),
            new GitTreeUpdate(GitTreeUpdateAction.Upsert, "root.txt", blobOid, GitFileMode.Regular),
        ], cancellationToken: TestContext.Current.CancellationToken);
        GitTree tree = (await _db.LookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
        TreeCache cache = await TreeCache.ReadTreeAsync(tree, GitHashAlgorithmKind.Sha1, CancellationToken.None);

        // Serialize.
        byte[] bytes = cache.Write();

        // Deserialize and verify.
        var restored = TreeCache.Read(bytes, GitHashAlgorithmKind.Sha1);
        Assert.NotNull(restored);
        Assert.Equal(cache.EntryCount, restored!.EntryCount);
        Assert.Equal(cache.Oid, restored.Oid);
        Assert.Equal(cache.Children.Count, restored.Children.Count);
    }

    [Fact]
    public void TreeCacheWrite_InvalidatedNode_NoOidInOutput()
    {
        var cache = new TreeCache(GitPath.FromUtf8String(string.Empty), GitHashAlgorithmKind.Sha1)
        {
            EntryCount = -1, // invalidated
            Oid = GitOid.Empty,
        };
        cache.Children.Add(new TreeCache(GitPath.FromUtf8String("src"), GitHashAlgorithmKind.Sha1)
        {
            EntryCount = 2,
            Oid = GitOid.EmptyTreeSha1,
        });

        byte[] bytes = cache.Write();

        // Read back and verify the invalidated node has no OID.
        var restored = TreeCache.Read(bytes, GitHashAlgorithmKind.Sha1);
        Assert.NotNull(restored);
        Assert.Equal(-1, restored!.EntryCount);
        Assert.True(restored.Oid.IsZero);
        Assert.Single(restored.Children);
        Assert.Equal(2, restored.Children[0].EntryCount);
        Assert.Equal(GitOid.EmptyTreeSha1, restored.Children[0].Oid);
    }

    private static GitRepository CreateMinimalRepository(GitObjectDb db, GitContext ctx)
    {
        // Create a minimal repository with just the ODB and ObjectFormat.
        // TreeBuilder only needs repo.Objects and repo.ObjectFormat.
        return new GitRepository(gitdir: "/tmp/fake", commondir: "/tmp/fake", workdir: null, isBare: true, isWorktree: false, objectFormat: GitHashAlgorithmKind.Sha1, @namespace: null, objects: db, config: new LibGit2CS.Config.GitConfiguration(ctx), context: ctx, refs: null);
    }
}
