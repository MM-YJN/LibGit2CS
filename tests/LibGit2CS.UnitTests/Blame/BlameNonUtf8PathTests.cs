using System.Text;

using LibGit2CS.Blame;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitBlame = LibGit2CS.Blame.GitBlame;

namespace LibGit2CS.UnitTests.Blame;

/// <summary> Byte-faithful blame path tests. Pins that the blame engine stores, compares, and follows renames using raw bytes for paths,
/// matching libgit2's <c>strcmp</c>-based <c>git_blame__origin.path</c> / <c>git_blame.paths</c> model. A non-UTF-8 path (invalid in any encoding) survives a
/// blame walk byte-exact and is never corrupted by a lossy UTF-8 decode. </summary>
public sealed class BlameNonUtf8PathTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private readonly GitContext _context = new();

    // A byte sequence that is invalid UTF-8 (0xFF is never a valid leading
    // byte); UTF-8.GetString decodes each to U+FFFD, so it cannot round-trip
    // through a decoded string. Used as the proof path throughout.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];

    public BlameNonUtf8PathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_BlameNonUtf8_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ===== Blame a non-UTF-8 path: OrigPath round-trips byte-exact =====

    [Fact]
    public async Task Blame_NonUtf8Path_OrigPathBytesPreserved()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        byte[] content = Encoding.UTF8.GetBytes("line1\nline2\nline3\n");

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, content, TestContext.Current.CancellationToken);
        GitTree tree = await BuildTreeAsync((nonUtf8, blobOid));
        GitOid commitOid = await CreateCommitAsync(tree, parents: [], "root\n", "refs/heads/main");
        await _repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        // Sanity: the tree has the entry at the non-UTF-8 path.
        Assert.True(tree.EntryCount > 0, "tree should have the entry");
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(tree.EntryByIndex(0)!.Value.Name.Span));
        GitTreeEntry? found = await tree.EntryByPathAsync(nonUtf8, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal(blobOid, found.Value.Id);

        // Sanity: a freshly ODB-loaded tree also finds the entry (the blame
        // engine loads the commit's tree via LookupAsync, not the in-memory one).
        GitTree freshTree = (await _repo.ObjectLookupAsync<GitTree>(tree.Id, TestContext.Current.CancellationToken))!;
        Assert.NotNull(freshTree);
        Assert.True(freshTree.EntryCount > 0, "fresh tree should have the entry");
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(freshTree.EntryByIndex(0)!.Value.Name.Span));
        GitTreeEntry? freshFound = await freshTree.EntryByPathAsync(nonUtf8, TestContext.Current.CancellationToken);
        Assert.NotNull(freshFound);

        using GitBlame blame = await _repo.BlameFileAsync(nonUtf8, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, blame.LineCount);
        Assert.Equal(1, blame.HunkCount);
        BlameHunk hunk = blame.GetHunk(0);
        Assert.Equal(commitOid, hunk.FinalCommitId);
        Assert.Equal(commitOid, hunk.OrigCommitId);

        // OrigPath round-trips byte-exact — the proof that blame carries the
        // path as raw bytes (BlameOrigin.Path is GitPath; BlameHunk.OrigPath
        // is GitPath). A lossy-string reconstruction does NOT match.
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(hunk.OrigPath.Span));
        string lossy = hunk.OrigPath.ToUtf8String();
        Assert.NotEqual(s_nonUtf8, Encoding.UTF8.GetBytes(lossy));
    }

    // ===== BlameScoreboard.Paths byte-faithful (sorted vector + binary search) =====

    [Fact]
    public async Task Blame_NonUtf8Path_PathsSortedVectorFindsByBytes()
    {
        // BlameScoreboard.Paths is a List<GitPath> + BinarySearch (ports
        // git_vector paths sorted by paths_cmp = git__strcmp). The internal
        // find_origin (rename detection) binary-searches Paths by the delta's
        // new path. To exercise this, blame a file that was renamed across
        // commits from a non-UTF-8 path to an ASCII path — the rename
        // detection must follow the non-UTF-8 old path.
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        var ascii = GitPath.FromUtf8String("renamed.txt");
        byte[] content = Encoding.UTF8.GetBytes("shared content\n");

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, content, TestContext.Current.CancellationToken);

        // Root commit: file at non-UTF-8 path.
        GitTree treeA = await BuildTreeAsync((nonUtf8, blobOid));
        GitOid root = await CreateCommitAsync(treeA, parents: [], "root\n", "refs/heads/main");

        // Second commit: same content, renamed to ASCII path. Blame of the
        // ASCII path at HEAD should follow the rename back to the root commit
        // (the line originated there).
        GitTree treeB = await BuildTreeAsync((ascii, blobOid));
        await CreateCommitAsync(treeB, parents: [root], "rename\n", "refs/heads/main");
        await _repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);

        using GitBlame blame = await _repo.BlameFileAsync(ascii, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, blame.LineCount);
        Assert.Equal(1, blame.HunkCount);
        BlameHunk hunk = blame.GetHunk(0);

        // The line is attributed to the root commit (where it originated),
        // at the non-UTF-8 path it had in that commit. This proves the
        // Paths sorted-vector found the non-UTF-8 path by raw bytes and the
        // rename was followed byte-faithfully.
        Assert.Equal(root, hunk.OrigCommitId);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(hunk.OrigPath.Span));
    }

    // ===== helpers =====

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

    private async ValueTask<GitOid> CreateCommitAsync(GitTree tree, IReadOnlyList<GitOid> parents, string message, string? updateRef)
    {
        var sig = new GitSignature("Test", "test@example.com", new GitTime(0, 0));
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree.Id,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = updateRef,
        }, TestContext.Current.CancellationToken);
    }
}
