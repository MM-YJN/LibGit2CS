using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Reset;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Index;

/// <summary>
/// Integration tests for the <see cref="GitIndex.Remove(string, int)"/> tree
/// cache invalidation regression. <see cref="GitIndex.Remove"/> must call
/// <c>InvalidateTreeCache</c> so that a subsequent
/// <see cref="GitIndex.WriteTreeAsync"/> does not hit the fast path in
/// <see cref="GitTree.WriteIndexAsync"/> and return a stale cached OID that
/// still contains the removed entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The fast path at
/// <c>GitTree.WriteIndexAsync</c> returns the cached tree OID verbatim when
/// the cache is valid. The cache is populated by three entry points —
/// <see cref="GitIndex.ReadTreeAsync"/> (used by hard/mixed reset),
/// <see cref="GitTree.WriteIndexAsync"/> itself (re-seeds after every write),
/// and <see cref="GitIndex.OpenAsync"/> (parses the on-disk <c>TREE</c>
/// extension). <see cref="GitIndex.Remove(string, int)"/> was the sole index
/// mutator that failed to invalidate the cache, silently dropping deletions
/// from trees built after a reset or after reopening an index whose on-disk
/// <c>TREE</c> extension was parsed.
/// </para>
/// <para>
/// <b>libgit2 counterpart.</b> <c>git_index_remove</c> →
/// <c>index_remove_entry</c> calls <c>git_tree_cache_invalidate_path</c>
/// (libgit2 <c>index.c</c>). These tests cover the two end-to-end paths the
/// unit tests cannot reach: the reset path (cache populated via
/// <see cref="GitIndex.ReadTreeAsync"/>) and the on-disk path (cache
/// populated by parsing a persisted <c>TREE</c> extension on reopen).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class IndexRemoveTreeCacheIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-idxrmtr-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// Inits a non-bare repo, sets HEAD to <c>refs/heads/main</c>, stages
    /// <c>a.txt</c> and <c>b.txt</c>, and creates a root commit on
    /// <c>refs/heads/main</c> whose tree contains both files. Returns the
    /// repo (caller disposes). HEAD resolves to the new commit via the
    /// symref → <c>refs/heads/main</c>.
    /// </summary>
    private static async Task<GitRepository> InitRepoWithTwoFilesAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "a.txt"), "a\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "b.txt"), "b\n", ct);
        GitIndex index = await repo.GetIndexAsync(ct);
        await index.AddByPathAsync("a.txt", ct);
        await index.AddByPathAsync("b.txt", ct);
        await index.WriteAsync(ct);
        GitOid treeOid = await index.WriteTreeAsync(ct);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        return repo;
    }

    // ── Tests ────────────────────────────────────────────────────────────

    /// <summary>
    /// Regression: after a hard reset the index tree cache is valid (populated
    /// via <see cref="GitIndex.ReadTreeAsync"/>). <see cref="GitIndex.Remove"/>
    /// must invalidate it so <see cref="GitIndex.WriteTreeAsync"/> rebuilds a
    /// tree without the removed entry, instead of returning the stale cached
    /// OID verbatim.
    /// </summary>
    [Fact]
    public async Task Remove_AfterHardReset_WriteTreeExcludesRemovedPath()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithTwoFilesAsync(path, ct);
        try
        {
            GitOid headOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            Commit head = (await repo.ObjectLookupAsync<Commit>(headOid, ct))!;

            // Hard reset → ResetAsync calls index.ReadTreeAsync, populating a
            // valid tree cache.
            await repo.ResetAsync(head, GitResetMode.Hard, checkoutOpts: null, cancellationToken: ct);

            GitIndex index = await repo.GetIndexAsync(ct);
            Assert.True(index.Remove("a.txt"));

            GitOid treeOid = await index.WriteTreeAsync(ct);
            GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, ct))!;
            Assert.Null(tree["a.txt"]);
            Assert.NotNull(tree["b.txt"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Regression: when an index carrying a valid tree cache is persisted, the
    /// <c>TREE</c> extension is written to <c>.git/index</c>. Reopening the
    /// repo parses that extension back into a valid cache. <see cref="GitIndex.Remove"/>
    /// must invalidate it so a subsequent <see cref="GitIndex.WriteTreeAsync"/>
    /// rebuilds without the removed entry.
    /// </summary>
    [Fact]
    public async Task Remove_AfterReopenIndex_WriteTreeExcludesRemovedPath()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();

        // Phase 1: build an index whose on-disk form carries the TREE extension.
        // WriteTreeAsync seeds the in-memory tree cache; WriteAsync then persists
        // it to .git/index.
        await using (GitRepository seed = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
        {
            await seed.SetHeadAsync("refs/heads/main", ct);
            string workdir = seed.Workdir!;
            await File.WriteAllTextAsync(Path.Combine(workdir, "a.txt"), "a\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir, "b.txt"), "b\n", ct);
            GitIndex index = await seed.GetIndexAsync(ct);
            await index.AddByPathAsync("a.txt", ct);
            await index.AddByPathAsync("b.txt", ct);
            await index.WriteTreeAsync(ct);
            await index.WriteAsync(ct);
        }

        // Phase 2: reopen → GetIndexAsync parses the on-disk TREE extension
        // into a valid cache.
        await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
        try
        {
            GitIndex index = await repo.GetIndexAsync(ct);
            Assert.True(index.Remove("a.txt"));

            GitOid treeOid = await index.WriteTreeAsync(ct);
            GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, ct))!;
            Assert.Null(tree["a.txt"]);
            Assert.NotNull(tree["b.txt"]);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
