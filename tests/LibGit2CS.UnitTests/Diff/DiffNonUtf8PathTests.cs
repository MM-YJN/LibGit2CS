using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary> Byte-faithful diff path tests. Pins that the diff pipeline stores, compares, sorts, and pairs (via rename detection) paths as
/// raw bytes, matching libgit2's <c>strcmp</c>/<c>memcmp</c> bag-of-bytes path model. A non-UTF-8 path (invalid in any encoding) survives a tree-to-tree diff
/// byte-exact and is never corrupted by a lossy UTF-8 decode. </summary>
public sealed class DiffNonUtf8PathTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private readonly GitContext _context = new();

    // A byte sequence that is invalid UTF-8 (0xFF is never a valid leading
    // byte); UTF-8.GetString decodes each to U+FFFD, so it cannot round-trip
    // through a decoded string. Used as the proof path throughout.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];

    public DiffNonUtf8PathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffNonUtf8_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ===== Delta paths round-trip byte-exact =====

    [Fact]
    public async Task TreeToTree_NonUtf8Path_DeltaPathBytesPreserved()
    {
        GitOid blobOid = await WriteBlobAsync("content\n");
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);

        GitTree treeA = await BuildTreeAsync((nonUtf8, blobOid));
        GitTree treeB = await BuildTreeAsync(); // empty → nonUtf8 is deleted

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);

        GitDiffDelta delta = Assert.Single(diff.Deltas);
        Assert.Equal(GitDeltaStatus.Deleted, delta.Status);

        // The old side carries the raw non-UTF-8 bytes byte-exact.
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(delta.OldFile.Path!.Value.Span));

        // A lossy-string reconstruction does NOT match the original bytes — the
        // proof that a string-based model could not represent this path.
        string lossy = delta.OldFile.Path!.Value.ToUtf8String();
        Assert.NotEqual(s_nonUtf8, Encoding.UTF8.GetBytes(lossy));
    }

    // ===== Byte-wise delta sort (0xFF sorts after ASCII) =====

    [Fact]
    public async Task TreeToTree_NonUtf8Path_SortsAfterAscii()
    {
        GitOid blobOid = await WriteBlobAsync("x\n");
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        var ascii = GitPath.FromUtf8String("zzz.txt");

        // Old tree has both; new tree has neither → two deletes. The ASCII path
        // must sort before the 0xFF path (byte order, not Unicode order).
        GitTree treeA = await BuildTreeAsync((ascii, blobOid), (nonUtf8, blobOid));
        GitTree treeB = await BuildTreeAsync();

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, diff.DeltaCount);
        Assert.Equal("zzz.txt", diff.GetDelta(0).Path.ToUtf8String());
        // The 0xFF path sorts after every ASCII path.
        Assert.True(diff.GetDelta(1).OldFile.Path!.Value.Span[0] == 0xFF);
    }

    // ===== Rename detection pairs non-UTF-8 paths by content (byte-faithful comparator slots) =====

    [Fact]
    public async Task FindSimilar_NonUtf8Paths_PairedAsRename()
    {
        GitOid blobOid = await WriteBlobAsync("rename me\n");
        var src = GitPath.FromUtf8Bytes(s_nonUtf8);
        // A second non-UTF-8 path (distinct bytes) for the rename target.
        var dst = GitPath.FromUtf8Bytes((byte[])[0xFF, 0xFE, 0x81]);

        // Old tree: blob at src. New tree: same blob at dst. Without rename
        // detection this is a delete + add; FindSimilar must pair them into a
        // single RENAMED delta (exact OID match → 100% similarity), proving the
        // comparator slots are byte-faithful end-to-end through diff_tform.
        GitTree treeA = await BuildTreeAsync((src, blobOid));
        GitTree treeB = await BuildTreeAsync((dst, blobOid));

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames }, cancellationToken: TestContext.Current.CancellationToken);

        GitDiffDelta delta = Assert.Single(diff.Deltas);
        Assert.Equal(GitDeltaStatus.Renamed, delta.Status);

        // Both sides carry the exact raw bytes.
        Assert.True(src.Span.SequenceEqual(delta.OldFile.Path!.Value.Span));
        Assert.True(dst.Span.SequenceEqual(delta.NewFile.Path!.Value.Span));
        Assert.Equal(100, delta.Similarity);
    }

    // ===== Matched-item byte compare (non-UTF-8 path unchanged → UNMODIFIED) =====

    [Fact]
    public async Task TreeToTree_NonUtf8Path_Unchanged_IsUnmodified()
    {
        GitOid blobOid = await WriteBlobAsync("same\n");
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);

        // Both trees have the same blob at the same non-UTF-8 path. The lock-step
        // walk's entrycomp slot must compare bytes and find them equal → no delta
        // (Unmodified deltas are excluded by default).
        GitTree treeA = await BuildTreeAsync((nonUtf8, blobOid));
        GitTree treeB = await BuildTreeAsync((nonUtf8, blobOid));

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(diff.Deltas);
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
