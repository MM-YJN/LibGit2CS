using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Merge;

public sealed class MergeIteratorsTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public MergeIteratorsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteFileAndCommitAsync(string fileName, string content, GitOid? parent = null)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await _repo.GetIndexAsync();
        await index.AddByPathAsync(fileName);
        await index.WriteAsync();
        GitOid treeOid = await index.WriteTreeAsync();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is not null ? [parent.Value] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        });
    }

    private async Task<GitTree> GetTreeAsync(GitOid commitOid)
    {
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        return tree;
    }

    // ── No-op merge (both sides match ancestor) ──────────────────────────

    [Fact]
    public async Task Trees_AllSame_ProducesUnmodifiedIndex()
    {
        GitOid baseOid = await WriteFileAndCommitAsync("file.txt", "hello\n");
        GitTree tree = await GetTreeAsync(baseOid);

        GitIndex result = await _repo.MergeTreesAsync(tree, tree, tree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.EntryCount);
        Assert.False(result.HasConflicts);
    }

    // ── One side modified ─────────────────────────────────────────────────

    [Fact]
    public async Task Trees_OursModified_NoConflict()
    {
        GitOid baseOid = await WriteFileAndCommitAsync("file.txt", "hello\n");
        GitTree baseTree = await GetTreeAsync(baseOid);

        // Modify file on "ours" side.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "hello modified\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid ourTreeOid = await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitTree? ourTree = await _repo.ObjectLookupAsync<GitTree>(ourTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(ourTree);

        GitIndex result = await _repo.MergeTreesAsync(baseTree, ourTree, baseTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasConflicts);
        Assert.Equal(1, result.EntryCount);
        GitIndexEntry entry = result.EntryByIndex(0);
        Assert.Equal("file.txt", entry.Path.ToUtf8String());
    }

    // ── Both sides modified differently → conflict ───────────────────────

    [Fact]
    public async Task Trees_BothModifiedDifferently_ProducesConflict()
    {
        GitOid baseOid = await WriteFileAndCommitAsync("file.txt", "hello\n");
        GitTree baseTree = await GetTreeAsync(baseOid);

        // Create "ours" with one modification.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "hello ours\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid ourTreeOid = await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitTree? ourTree = await _repo.ObjectLookupAsync<GitTree>(ourTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(ourTree);

        // Create "theirs" with different modification.
        // Need a separate index for theirs.
        (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).Clear();
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "hello theirs\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid theirTreeOid = await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitTree? theirTree = await _repo.ObjectLookupAsync<GitTree>(theirTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(theirTree);

        GitIndex result = await _repo.MergeTreesAsync(baseTree, ourTree, theirTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.HasConflicts);
    }

    // ── One side deleted ─────────────────────────────────────────────────

    [Fact]
    public async Task Trees_OneSideDeleted_NoConflict()
    {
        GitOid baseOid = await WriteFileAndCommitAsync("file.txt", "hello\n");
        GitTree baseTree = await GetTreeAsync(baseOid);

        // "Ours" deletes the file.
        (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).Clear();
        GitOid emptyTreeOid = await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeToAsync(_repo, cancellationToken: TestContext.Current.CancellationToken);
        GitTree? ourTree = await _repo.ObjectLookupAsync<GitTree>(emptyTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(ourTree);

        GitIndex result = await _repo.MergeTreesAsync(baseTree, ourTree, baseTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasConflicts);
        Assert.Equal(0, result.EntryCount);
    }

    // ── Both sides added same content ───────────────────────────────────

    [Fact]
    public async Task Trees_BothAddedSameContent_NoConflict()
    {
        // Empty base.
        GitOid baseTreeOid = (await _repo.NewTreeBuilder().WriteAsync(CancellationToken.None));
        GitTree? baseTree = await _repo.ObjectLookupAsync<GitTree>(baseTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(baseTree);

        // Both sides add the same file.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "new.txt"), "new content\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("new.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitTree? bothTree = await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(bothTree);

        GitIndex result = await _repo.MergeTreesAsync(baseTree, bothTree, bothTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasConflicts);
        Assert.Equal(1, result.EntryCount);
    }

    // ── Both sides deleted ──────────────────────────────────────────────

    [Fact]
    public async Task Trees_BothDeleted_NoConflict()
    {
        GitOid baseOid = await WriteFileAndCommitAsync("file.txt", "hello\n");
        GitTree baseTree = await GetTreeAsync(baseOid);

        // Both sides have empty tree (file deleted).
        GitOid emptyTreeOid = (await _repo.NewTreeBuilder().WriteAsync(CancellationToken.None));
        GitTree? emptyTree = await _repo.ObjectLookupAsync<GitTree>(emptyTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(emptyTree);

        GitIndex result = await _repo.MergeTreesAsync(baseTree, emptyTree, emptyTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasConflicts);
        Assert.Equal(0, result.EntryCount);
    }

    // ── One side added, other unmodified ────────────────────────────────

    [Fact]
    public async Task Trees_OursAddedOtherUnmodified_NoConflict()
    {
        GitOid baseOid = await WriteFileAndCommitAsync("base.txt", "base\n");
        GitTree baseTree = await GetTreeAsync(baseOid);

        // Ours adds a new file.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "new.txt"), "new\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("new.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid ourTreeOid = await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitTree? ourTree = await _repo.ObjectLookupAsync<GitTree>(ourTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(ourTree);

        GitIndex result = await _repo.MergeTreesAsync(baseTree, ourTree, baseTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasConflicts);
        Assert.Equal(2, result.EntryCount);
    }

    // ── Empty ancestor ───────────────────────────────────────────────────

    [Fact]
    public async Task Trees_EmptyAncestor_BothAdded_ProducesConflict()
    {
        GitOid emptyTreeOid = (await _repo.NewTreeBuilder().WriteAsync(CancellationToken.None));
        GitTree? emptyTree = await _repo.ObjectLookupAsync<GitTree>(emptyTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(emptyTree);

        // Ours adds file.txt with one content.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "ours\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid ourTreeOid = await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitTree? ourTree = await _repo.ObjectLookupAsync<GitTree>(ourTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(ourTree);

        // Theirs adds file.txt with different content.
        (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).Clear();
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "theirs\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid theirTreeOid = await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitTree? theirTree = await _repo.ObjectLookupAsync<GitTree>(theirTreeOid, TestContext.Current.CancellationToken);
        Assert.NotNull(theirTree);

        GitIndex result = await _repo.MergeTreesAsync(emptyTree, ourTree, theirTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.HasConflicts);
    }

    // ── IndexAndTree basic ───────────────────────────────────────────────

    [Fact]
    public async Task IndexAndTree_BasicMerge_NoConflict()
    {
        GitOid baseOid = await WriteFileAndCommitAsync("file.txt", "hello\n");
        GitTree baseTree = await GetTreeAsync(baseOid);

        // Modify file in workdir, stage it (repo index has the modification).
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "hello modified\n", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)).WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Theirs = base tree (unchanged).
        GitIndex result = await _repo.MergeIndexAndTreeAsync(baseTree, (await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken)), baseTree, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.HasConflicts);
        Assert.Equal(1, result.EntryCount);
    }
}
