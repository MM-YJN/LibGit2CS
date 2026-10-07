using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Objects;

/// <summary>
/// Integration tests for <see cref="GitTree.CreateUpdatedAsync"/> (the
/// <c>git_tree_create_updated</c> port) exercised end-to-end against
/// locally-initialized repos with real ODB lookups.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>TreeBuilderTests</c> cover
/// <see cref="GitTreeBuilder"/> in isolation against an in-memory ODB.
/// <see cref="GitTreeUpdate"/> (the <c>readonly record struct</c> passed
/// to <see cref="GitTree.CreateUpdatedAsync"/>) had <c>0%</c> coverage, and
/// the deep branches of <see cref="GitTree.CreateUpdatedAsync"/> — the
/// stack-walk over nested paths, the empty-subtree removal
/// (<c>CreatePoppedTreeAsync</c>, tree.c:1113-1148), the D/F-conflict guard,
/// and the type-mismatch-on-upsert guard — were never exercised
/// end-to-end. These tests build a baseline tree via
/// <see cref="GitTreeBuilder"/>, then apply update lists through
/// <see cref="GitTree.CreateUpdatedAsync"/> and verify the resulting tree
/// by reading it back from the ODB.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/tree/update.c</c>
/// (<c>test_tree_create_updated__create_a_tree_from_an_empty_tree</c>,
/// <c>test_tree_create_updated__add_a_subfolder</c>,
/// <c>test_tree_create_updated__replace_a_subfolder</c>), adapted to build
/// the sandbox from scratch (no fixture repo).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class TreeUpdateIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-treeupd-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Writes a blob to the ODB and returns its OID.</summary>
    private static async ValueTask<GitOid> WriteBlobAsync(GitRepository repo, byte[] content, CancellationToken ct)
        => await repo.ObjectWriteAsync(GitObjectType.Blob, content, ct).ConfigureAwait(false);

    /// <summary>
    /// Builds a tree from the given entries, writing it to the ODB. Each
    /// entry is (relative path, blob OID, file mode). Paths with <c>/</c>
    /// create nested subtrees by recursively building child tree builders
    /// (<see cref="GitTreeBuilder.InsertAsync"/> rejects embedded slashes,
    /// so nested paths must be assembled bottom-up).
    /// </summary>
    private static async ValueTask<GitOid> BuildTreeAsync(
        GitRepository repo,
        IEnumerable<(string Path, GitOid Blob, GitFileMode Mode)> entries,
        CancellationToken ct)
    {
        // Group by top-level path component. Leaf entries go straight into
        // the root builder; entries whose path contains '/' are aggregated
        // by their first component and built recursively as subtrees.
        var leaves = new List<(string Name, GitOid Blob, GitFileMode Mode)>();
        var byDir = new Dictionary<string, List<(string Suffix, GitOid Blob, GitFileMode Mode)>>();

        foreach ((string p, GitOid blob, GitFileMode mode) in entries)
        {
            int slash = p.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                leaves.Add((p, blob, mode));
            }
            else
            {
                string head = p[..slash];
                string rest = p[(slash + 1)..];
                if (!byDir.TryGetValue(head, out List<(string, GitOid, GitFileMode)>? list))
                {
                    list = [];
                    byDir[head] = list;
                }

                list.Add((rest, blob, mode));
            }
        }

        using GitTreeBuilder bld = repo.NewTreeBuilder();
        foreach ((string name, GitOid blob, GitFileMode mode) in leaves)
        {
            await bld.InsertAsync(name, blob, mode, ct).ConfigureAwait(false);
        }

        foreach ((string dir, List<(string Suffix, GitOid Blob, GitFileMode Mode)> children) in byDir)
        {
            // Recursively build the subtree from the suffix paths (the parts
            // after the first slash), then insert it under `dir` in the root.
            GitOid subtreeOid = await BuildTreeAsync(
                repo,
                children.Select(c => (c.Suffix, c.Blob, c.Mode)),
                ct).ConfigureAwait(false);
            await bld.InsertAsync(dir, subtreeOid, GitFileMode.Tree, ct).ConfigureAwait(false);
        }

        return await bld.WriteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects (path, mode) tuples for every leaf entry in the tree,
    /// walking subtrees recursively. Paths use <c>/</c> separators.
    /// </summary>
    private static async Task<List<(string Path, GitFileMode Mode, GitOid Id)>> FlattenTreeAsync(GitRepository repo, GitOid treeOid, string prefix, CancellationToken ct)
    {
        var result = new List<(string, GitFileMode, GitOid)>();
        GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException($"tree {treeOid} not found");

        foreach (GitTreeEntry e in tree)
        {
            string childPath = prefix.Length == 0 ? e.Name.ToUtf8String() : prefix + "/" + e.Name.ToUtf8String();
            if (e.IsTree)
            {
                result.AddRange(await FlattenTreeAsync(repo, e.Id, childPath, ct).ConfigureAwait(false));
            }
            else
            {
                result.Add((childPath, e.Mode, e.Id));
            }
        }

        return result;
    }

    // ── empty / no-op updates ───────────────────────────────────────────

    /// <summary>
    /// <see cref="GitTree.CreateUpdatedAsync"/> with no updates and a
    /// null baseline writes the empty tree (the canonical empty-tree OID).
    /// Exercises the <c>updates.Count == 0 + baseline is null</c> early-return
    /// branch that calls <c>repo.ObjectWriteAsync(Tree, [])</c>.
    /// </summary>
    [Fact]
    public async Task CreateUpdated_NoUpdatesNullBaseline_WritesEmptyTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid emptyTreeOid = await repo.TreeCreateUpdatedAsync(baseline: null, updates: [], ct).ConfigureAwait(false);
            GitTree emptyTree = (await repo.ObjectLookupAsync<GitTree>(emptyTreeOid, ct).ConfigureAwait(false))!;
            Assert.Equal(0, emptyTree.EntryCount);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitTree.CreateUpdatedAsync"/> with no updates and a
    /// non-null baseline returns the baseline's OID unchanged (the
    /// <c>updates.Count == 0 + baseline is not null</c> early-return branch).
    /// </summary>
    [Fact]
    public async Task CreateUpdated_NoUpdatesWithBaseline_ReturnsBaselineId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid blob = await WriteBlobAsync(repo, "x\n"u8.ToArray(), ct).ConfigureAwait(false);
            GitOid baselineOid = await BuildTreeAsync(repo, [("a.txt", blob, GitFileMode.Regular)], ct).ConfigureAwait(false);

            GitOid result = await repo.TreeCreateUpdatedAsync(baseline: (await repo.ObjectLookupAsync<GitTree>(baselineOid, ct).ConfigureAwait(false))!, updates: [], ct).ConfigureAwait(false);
            Assert.Equal(baselineOid, result);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── upsert paths ────────────────────────────────────────────────────

    /// <summary>
    /// A single <see cref="GitTreeUpdateAction.Upsert"/> at the root
    /// level produces a tree with one entry whose OID and mode match the
    /// update.
    /// </summary>
    [Fact]
    public async Task CreateUpdated_UpsertSingleFile_CreatesOneEntryTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid blob = await WriteBlobAsync(repo, "hello\n"u8.ToArray(), ct).ConfigureAwait(false);
            var updates = new List<GitTreeUpdate>
            {
                new(GitTreeUpdateAction.Upsert, "hello.txt", blob, GitFileMode.Regular),
            };

            GitOid treeOid = await repo.TreeCreateUpdatedAsync(baseline: null, updates: updates, ct).ConfigureAwait(false);
            List<(string Path, GitFileMode Mode, GitOid Id)> entries = await FlattenTreeAsync(repo, treeOid, "", ct).ConfigureAwait(false);

            Assert.Single(entries);
            Assert.Equal(("hello.txt", GitFileMode.Regular, blob), entries[0]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// An <see cref="GitTreeUpdateAction.Upsert"/> at a nested path
    /// (<c>a/b/c.txt</c>) creates the intermediate subtrees <c>a</c> and
    /// <c>b</c> automatically (the stack-walk-down branch +
    /// <c>CreatePoppedTreeAsync</c> writing each popped subtree into its
    /// parent). The resulting tree has one leaf at <c>a/b/c.txt</c>.
    /// </summary>
    [Fact]
    public async Task CreateUpdated_UpsertNestedPath_CreatesIntermediateSubtrees()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid blob = await WriteBlobAsync(repo, "deep\n"u8.ToArray(), ct).ConfigureAwait(false);
            var updates = new List<GitTreeUpdate>
            {
                new(GitTreeUpdateAction.Upsert, "a/b/c.txt", blob, GitFileMode.Regular),
            };

            GitOid treeOid = await repo.TreeCreateUpdatedAsync(baseline: null, updates: updates, ct).ConfigureAwait(false);
            List<(string Path, GitFileMode Mode, GitOid Id)> entries = await FlattenTreeAsync(repo, treeOid, "", ct).ConfigureAwait(false);

            Assert.Single(entries);
            Assert.Equal("a/b/c.txt", entries[0].Path);
            Assert.Equal(blob, entries[0].Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── remove paths ────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitTreeUpdateAction.Remove"/> on a baseline with two
    /// files removes the named entry and leaves its sibling intact.
    /// Exercises the <c>topBuilder.Remove(basename)</c> branch.
    /// </summary>
    [Fact]
    public async Task CreateUpdated_RemoveEntry_LeavesSiblingIntact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid blobA = await WriteBlobAsync(repo, "a\n"u8.ToArray(), ct).ConfigureAwait(false);
            GitOid blobB = await WriteBlobAsync(repo, "b\n"u8.ToArray(), ct).ConfigureAwait(false);
            GitOid baselineOid = await BuildTreeAsync(
                repo,
                [("a.txt", blobA, GitFileMode.Regular), ("b.txt", blobB, GitFileMode.Regular)],
                ct).ConfigureAwait(false);
            GitTree baseline = (await repo.ObjectLookupAsync<GitTree>(baselineOid, ct).ConfigureAwait(false))!;

            var updates = new List<GitTreeUpdate>
            {
                new(GitTreeUpdateAction.Remove, "a.txt"),
            };

            GitOid treeOid = await repo.TreeCreateUpdatedAsync(baseline, updates, ct).ConfigureAwait(false);
            List<(string Path, GitFileMode Mode, GitOid Id)> entries = await FlattenTreeAsync(repo, treeOid, "", ct).ConfigureAwait(false);

            Assert.Single(entries);
            Assert.Equal("b.txt", entries[0].Path);
            Assert.Equal(blobB, entries[0].Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitTreeUpdateAction.Remove"/> that empties a subtree
    /// triggers <c>CreatePoppedTreeAsync</c>'s empty-subtree-removal branch
    /// (tree.c:1113-1148): the popped builder has <c>EntryCount == 0</c>, so
    /// the parent entry for that directory is removed. A baseline with
    /// <c>dir/a.txt</c>, after removing <c>dir/a.txt</c>, yields a root tree
    /// with NO <c>dir</c> entry.
    /// </summary>
    [Fact]
    public async Task CreateUpdated_RemoveAllInSubtree_RemovesEmptySubtree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid blob = await WriteBlobAsync(repo, "x\n"u8.ToArray(), ct).ConfigureAwait(false);
            GitOid baselineOid = await BuildTreeAsync(
                repo,
                [("dir/a.txt", blob, GitFileMode.Regular), ("top.txt", blob, GitFileMode.Regular)],
                ct).ConfigureAwait(false);
            GitTree baseline = (await repo.ObjectLookupAsync<GitTree>(baselineOid, ct).ConfigureAwait(false))!;

            var updates = new List<GitTreeUpdate>
            {
                new(GitTreeUpdateAction.Remove, "dir/a.txt"),
            };

            GitOid treeOid = await repo.TreeCreateUpdatedAsync(baseline, updates, ct).ConfigureAwait(false);
            List<(string Path, GitFileMode Mode, GitOid Id)> entries = await FlattenTreeAsync(repo, treeOid, "", ct).ConfigureAwait(false);

            // Only top.txt remains; the now-empty dir/ subtree is gone.
            Assert.Single(entries);
            Assert.Equal("top.txt", entries[0].Path);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── error paths ─────────────────────────────────────────────────────

    /// <summary>
    /// Upserting <c>foo/bar.txt</c> when the baseline has a blob at
    /// <c>foo</c> throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.Invalid"/> (the D/F-conflict guard:
    /// <c>existing entry must be a tree</c>).
    /// </summary>
    [Fact]
    public async Task CreateUpdated_DirectoryFileConflict_ThrowsInvalid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid blob = await WriteBlobAsync(repo, "x\n"u8.ToArray(), ct).ConfigureAwait(false);
            // Baseline: file at "foo" (a blob, not a tree).
            GitOid baselineOid = await BuildTreeAsync(repo, [("foo", blob, GitFileMode.Regular)], ct).ConfigureAwait(false);
            GitTree baseline = (await repo.ObjectLookupAsync<GitTree>(baselineOid, ct).ConfigureAwait(false))!;

            var updates = new List<GitTreeUpdate>
            {
                new(GitTreeUpdateAction.Upsert, "foo/bar.txt", blob, GitFileMode.Regular),
            };

            GitException ex = await Assert.ThrowsAsync<GitException>(() => repo.TreeCreateUpdatedAsync(baseline, updates, ct));
            Assert.Equal(GitErrorCode.Invalid, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Upserting a tree at a path where the baseline has a blob of a
    /// different type throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.Invalid"/> — the "cannot replace Blob with
    /// Tree" type-mismatch guard at the upsert site.
    /// </summary>
    [Fact]
    public async Task CreateUpdated_TypeMismatchOnUpsert_ThrowsInvalid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid blob = await WriteBlobAsync(repo, "x\n"u8.ToArray(), ct).ConfigureAwait(false);
            GitOid baselineOid = await BuildTreeAsync(repo, [("foo", blob, GitFileMode.Regular)], ct).ConfigureAwait(false);
            GitTree baseline = (await repo.ObjectLookupAsync<GitTree>(baselineOid, ct).ConfigureAwait(false))!;

            // Upsert at "foo" with Tree mode while baseline has a blob there.
            var updates = new List<GitTreeUpdate>
            {
                new(GitTreeUpdateAction.Upsert, "foo", baselineOid, GitFileMode.Tree),
            };

            GitException ex = await Assert.ThrowsAsync<GitException>(() => repo.TreeCreateUpdatedAsync(baseline, updates, ct));
            Assert.Equal(GitErrorCode.Invalid, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
