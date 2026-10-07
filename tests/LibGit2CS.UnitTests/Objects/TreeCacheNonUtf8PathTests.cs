using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Objects;

/// <summary> Byte-faithful TreeCache tests. Pins that the index←tree path — <c>ReadTreeAsync</c> →
/// <c>GitTree.WalkAsync</c> → <see cref="TreeCache"/> → index entries — carries non-UTF-8 path bytes verbatim end-to-end, and that <see cref="TreeCache"/>
/// serializes/deserializes non-UTF-8 names byte-exact (matching libgit2's <c>git_tree_cache</c> flex-array <c>name</c> + <c>memcmp</c> in <c>find_child</c>).
/// <para> A string-keyed cache that decodes names via <c>Encoding.UTF8.GetString</c> on read and re-encodes via
/// <c>Encoding.UTF8.GetBytes</c> on write corrupts non-UTF-8 path bytes (0xFF 0xFE 0x80 → U+FFFD → 0xEF 0xBF 0xBD). <c>ReadTreeAsync(tree)</c> would then
/// produce index entries whose paths differed from the tree's raw bytes, so the byte keys are stored and compared directly. </para> </summary>
public sealed class TreeCacheNonUtf8PathTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private readonly GitContext _context = new();

    // A byte sequence that is invalid UTF-8 (0xFF is never a valid leading
    // byte); UTF-8.GetString decodes each to U+FFFD, so it cannot round-trip
    // through a decoded string. Used as the proof path throughout.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];

    public TreeCacheNonUtf8PathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_TreeCacheNonUtf8_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, _context);
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        _context.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ===== ReadTreeAsync carries non-UTF-8 path bytes byte-exact =====

    [Fact]
    public async Task ReadTreeAsync_NonUtf8Path_IndexEntryIsByteExact()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        GitOid blobOid = await WriteBlobAsync("content\n");
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));

        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.ReadTreeAsync(tree, TestContext.Current.CancellationToken);

        Assert.Equal(1, index.EntryCount);
        GitIndexEntry entry = index.EntryByIndex(0);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(entry.Path.Span));
    }

    [Fact]
    public async Task ReadTreeAsync_NonUtf8Path_LookupByRawBytesFindsEntry()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        GitOid blobOid = await WriteBlobAsync("content\n");
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));

        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.ReadTreeAsync(tree, TestContext.Current.CancellationToken);

        GitIndexEntry? found = index.EntryByPath(nonUtf8);
        Assert.NotNull(found);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(found!.Value.Path.Span));

        // The lossy decoded string re-encodes to U+FFFD bytes, NOT the original,
        // so the lookup must miss — proving the index stored raw bytes, not a
        // decoded-then-re-encoded string.
        string lossy = nonUtf8.ToUtf8String();
        Assert.NotEqual(s_nonUtf8, Encoding.UTF8.GetBytes(lossy));
        Assert.Null(index.EntryByPath(GitPath.FromUtf8String(lossy)));
    }

    // ===== ReadTreeAsync → WriteTreeAsync round-trip preserves bytes =====

    [Fact]
    public async Task ReadTreeAsync_ThenWriteTreeAsync_RoundTripsByteExact()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        GitOid blobOid = await WriteBlobAsync("content\n");
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));

        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.ReadTreeAsync(tree, TestContext.Current.CancellationToken);

        // Write the index back to a tree and verify the entry is byte-exact.
        GitOid writtenTreeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitTree writtenTree = (await _repo.ObjectLookupAsync<GitTree>(writtenTreeOid, TestContext.Current.CancellationToken))!;

        GitTreeEntry entry = Assert.Single(writtenTree);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(entry.Name.Span));
    }

    // ===== Nested non-UTF-8 path exercises WriteTreeRecursiveAsync byte-slicing =====

    [Fact]
    public async Task WriteTreeAsync_NestedNonUtf8Dir_ByteExact()
    {
        // A path with a non-UTF-8 directory component "0xFF 0xFE 0x80" + "/file".
        // WriteTreeRecursiveAsync slices the path on '/' boundaries in the byte
        // domain to build the subtree. A string-based slice would corrupt the
        // non-UTF-8 dir name.
        byte[] dirBytes = s_nonUtf8;
        byte[] fileBytes = Encoding.UTF8.GetBytes("file.txt");
        byte[] fullPath = [.. dirBytes, (byte)'/', .. fileBytes];
        var path = GitPath.FromUtf8Bytes(fullPath);
        GitOid blobOid = await WriteBlobAsync("content\n");

        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        index.Add(new GitIndexEntry { Path = path, Id = blobOid, Mode = GitFileMode.Regular });

        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        // The root tree has one entry: the non-UTF-8 directory (tree mode).
        GitTreeEntry dirEntry = Assert.Single(tree);
        Assert.True(GitFileMode.Tree == dirEntry.Mode);
        Assert.True(dirBytes.AsSpan().SequenceEqual(dirEntry.Name.Span));

        // The subtree has one entry: "file.txt".
        GitTree subTree = (await _repo.ObjectLookupAsync<GitTree>(dirEntry.Id, TestContext.Current.CancellationToken))!;
        GitTreeEntry fileEntry = Assert.Single(subTree);
        Assert.True(fileBytes.AsSpan().SequenceEqual(fileEntry.Name.Span));
    }

    // ===== TreeCache binary write/read round-trip is byte-exact =====

    [Fact]
    public void TreeCache_WriteRead_NonUtf8Name_ByteExact()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        var rootOid = GitOid.FromRaw(new byte[GitOid.SizeFor(GitHashAlgorithmKind.Sha1)], GitHashAlgorithmKind.Sha1);
        var childOid = GitOid.FromRaw(new byte[GitOid.SizeFor(GitHashAlgorithmKind.Sha1)], GitHashAlgorithmKind.Sha1);
        var cache = new TreeCache(default, GitHashAlgorithmKind.Sha1)
        {
            EntryCount = 1,
            Oid = rootOid,
        };
        var child = new TreeCache(nonUtf8, GitHashAlgorithmKind.Sha1)
        {
            EntryCount = 1,
            Oid = childOid,
        };
        cache.Children.Add(child);

        byte[] written = cache.Write();
        var read = TreeCache.Read(written, GitHashAlgorithmKind.Sha1);
        Assert.NotNull(read);

        TreeCache? found = read!.Get(nonUtf8);
        Assert.NotNull(found);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(found!.Name.Span));
        Assert.Equal(1, found.EntryCount);
    }

    [Fact]
    public void TreeCache_InvalidatePath_NonUtf8_ByteExact()
    {
        // InvalidatePath walks a '/'-separated path, invalidating the root and
        // each intermediate dir. To exercise the byte-wise find_child on a
        // non-UTF-8-named child, the path must be "<nonUtf8>/file" — the
        // non-UTF-8 bytes are a path component (a dir name), not a leaf.
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        GitPath nested = nonUtf8 + "/" + "file";
        var rootOid = GitOid.FromRaw(new byte[GitOid.SizeFor(GitHashAlgorithmKind.Sha1)], GitHashAlgorithmKind.Sha1);
        var childOid = GitOid.FromRaw(new byte[GitOid.SizeFor(GitHashAlgorithmKind.Sha1)], GitHashAlgorithmKind.Sha1);
        var cache = new TreeCache(default, GitHashAlgorithmKind.Sha1)
        {
            EntryCount = 2,
            Oid = rootOid,
        };
        var child = new TreeCache(nonUtf8, GitHashAlgorithmKind.Sha1)
        {
            EntryCount = 1,
            Oid = childOid,
        };
        cache.Children.Add(child);

        TreeCache.InvalidatePath(cache, nested);

        // Root and the non-UTF-8-named child are both invalidated.
        Assert.Equal(-1, cache.EntryCount);
        TreeCache? found = cache.Get(nonUtf8);
        Assert.NotNull(found);
        Assert.Equal(-1, found!.EntryCount);
    }

    // ===== helpers =====

    private async ValueTask<GitOid> WriteBlobAsync(string content)
        => await _repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);

    private async ValueTask<GitTree> BuildTreeAsync(params (GitPath Name, GitOid Oid)[] entries)
    {
        GitTreeBuilder builder = _repo.NewTreeBuilder();
        foreach ((GitPath name, GitOid oid) in entries)
        {
            await builder.InsertAsync(name, oid, GitFileMode.Regular, TestContext.Current.CancellationToken);
        }

        GitOid treeOid = await builder.WriteAsync(TestContext.Current.CancellationToken);
        return (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
    }
}
