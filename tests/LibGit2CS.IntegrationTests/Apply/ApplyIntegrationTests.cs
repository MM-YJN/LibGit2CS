using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Reset;

using GitDiff = LibGit2CS.Diff.GitDiff;
using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Apply;

/// <summary>
/// Integration tests for patch application
/// (<see cref="GitRepository.ApplyAsync"/> and
/// <see cref="GitRepository.ApplyToTreeAsync"/>) exercised end-to-end
/// against locally-initialized repos with code-built commit history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>ApplyBothTests</c>/
/// <c>ApplyIndexTests</c>/<c>ApplyWorkdirTests</c>/<c>ApplyToTreeTests</c>
/// cover the apply paths against a fixture-extracted
/// <c>merge-recursive.zip</c> repo with hardcoded expected OIDs. These
/// integration tests build the repo from scratch (no fixture zip), generate
/// the diff with <see cref="GitDiff.TreeToTreeAsync"/> (the real diff
/// pipeline, not a parsed patch buffer), apply it through the repo-level
/// entry points, and verify the resulting index/workdir against the
/// on-disk ODB. This exercises the full
/// <see cref="GitPatchApplier.ApplyPatchAsync"/> → ODB write → index/workdir
/// commit path without golden OIDs.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/apply/both.c</c> (<c>test_apply_both__generated_diff</c>,
/// <c>test_apply_both__removes_file</c>, <c>test_apply_both__adds_file</c>)
/// and <c>tests/libgit2/apply/tree.c</c> (<c>test_apply_tree__generated_diff</c>),
/// adapted to build the sandbox from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class ApplyIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-apply-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates a commit on <paramref name="refName"/> whose tree contains
    /// exactly the given (path → content) files — the index is cleared first
    /// so the resulting commit tree contains <b>only</b> the supplied files,
    /// not leftover staged entries from a previous commit. The workdir files
    /// from prior commits that aren't in <paramref name="files"/> are
    /// deleted. Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> CommitFilesAsync(
        GitRepository repo, string workdir, IReadOnlyDictionary<string, string> files,
        string message, string refName, GitOid? parent, CancellationToken ct)
    {
        GitIndex index = await repo.GetIndexAsync(ct);

        // Snapshot the currently-tracked paths so we can remove workdir files
        // that are no longer in the new tree (for the delete scenario).
        var priorPaths = new List<string>();
        for (int i = 0; i < index.EntryCount; i++)
        {
            priorPaths.Add(index.EntryByIndex(i).Path.ToUtf8String());
        }

        // Start from a clean index — the new commit's tree contains exactly
        // the supplied files, not the parent's files.
        index.Clear();

        foreach ((string path, string content) in files)
        {
            string full = Path.Combine(workdir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, ct);
            await index.AddByPathAsync(path, ct);
        }

        // Delete workdir files that are no longer in the new tree.
        foreach (string prior in priorPaths)
        {
            if (!files.ContainsKey(prior))
            {
                string full = Path.Combine(workdir, prior);
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
        }

        await index.WriteAsync(ct);
        GitOid treeOid = await index.WriteTreeAsync(ct);

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is null ? [] : [parent.Value],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct);
    }

    /// <summary>
    /// Resets HEAD, index, and workdir to the tree of <paramref name="commitOid"/>
    /// using <see cref="GitRepository.ResetAsync"/> with hard reset. Used to
    /// restore the workdir to the preimage state after committing a side
    /// branch (which dirties the workdir with the postimage content).
    /// </summary>
    private static async Task ResetToCommitAsync(GitRepository repo, GitOid commitOid, CancellationToken ct)
    {
        Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
        await repo.ResetAsync(commit, GitResetMode.Hard, cancellationToken: ct);
    }

    /// <summary>
    /// Resolves a commit OID to its tree, looking up via the ODB.
    /// </summary>
    private static async Task<GitTree> TreeOfAsync(GitRepository repo, GitOid commitOid, CancellationToken ct)
    {
        Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, ct))!;
        return (await repo.ObjectLookupAsync<GitTree>(commit.Tree, ct))!;
    }

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

    /// <summary>Reads a workdir file as text, or null if missing.</summary>
    private static async Task<string?> ReadWorkdirFileAsync(string workdir, string path, CancellationToken ct)
    {
        string full = Path.Combine(workdir, path);
        return File.Exists(full) ? await File.ReadAllTextAsync(full, ct) : null;
    }

    // ── ApplyToTreeAsync (in-memory) ────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.ApplyToTreeAsync"/> applies a
    /// tree-to-tree diff to the preimage tree entirely in memory, returning
    /// an ephemeral <see cref="GitIndex"/> containing the postimage. The
    /// repo's real index is not mutated. Mirrors
    /// <c>test_apply_tree__generated_diff</c> (tree.c).
    /// </summary>
    [Fact]
    public async Task ApplyToTree_Modification_ProducesPostimageIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Commit 1: a.txt = "line1\nline2\nline3\n"
            GitOid c1 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2\nline3\n" },
                "init\n", "refs/heads/main", parent: null, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Commit 2 (on a side branch): a.txt modified.
            GitOid c2 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2-modified\nline3\n" },
                "modify a\n", "refs/heads/feature", parent: c1, ct);

            GitTree aTree = await TreeOfAsync(repo, c1, ct);
            GitTree bTree = await TreeOfAsync(repo, c2, ct);

            // Tree-to-tree diff: 539bd01 → 7c7bf85 equivalent.
            using GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Modified, diff.GetDelta(0).Status);

            // Apply to the preimage tree in memory.
            using GitIndex postimage = await repo.ApplyToTreeAsync(aTree, diff, cancellationToken: ct);

            // The postimage has one entry: a.txt pointing at the new blob.
            Assert.Equal(1, postimage.EntryCount);
            GitIndexEntry entry = postimage.EntryByIndex(0);
            Assert.Equal("a.txt", entry.Path.ToUtf8String());

            // The postimage blob OID matches the bTree blob OID.
            GitTreeEntry? bEntry = bTree.EntryByName("a.txt");
            Assert.NotNull(bEntry);
            Assert.Equal(bEntry!.Value.Id, entry.Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.ApplyToTreeAsync"/> with a diff that
    /// adds a new file produces a postimage index containing both the
    /// original entries and the new file. Mirrors
    /// <c>test_apply_tree__adds_file</c> (tree.c).
    /// </summary>
    [Fact]
    public async Task ApplyToTree_AddedFile_PostimageContainsNewEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid c1 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "a\n" },
                "init\n", "refs/heads/main", parent: null, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitOid c2 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "a\n", ["b.txt"] = "b\n" },
                "add b\n", "refs/heads/feature", parent: c1, ct);

            GitTree aTree = await TreeOfAsync(repo, c1, ct);
            GitTree bTree = await TreeOfAsync(repo, c2, ct);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Added, diff.GetDelta(0).Status);

            using GitIndex postimage = await repo.ApplyToTreeAsync(aTree, diff, cancellationToken: ct);
            Assert.Equal(2, postimage.EntryCount);

            var paths = new HashSet<string>();
            for (int i = 0; i < postimage.EntryCount; i++)
            {
                paths.Add(postimage.EntryByIndex(i).Path.ToUtf8String());
            }

            Assert.Contains("a.txt", paths);
            Assert.Contains("b.txt", paths);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.ApplyToTreeAsync"/> with a diff that
    /// deletes a file produces a postimage index without the deleted file.
    /// Mirrors <c>test_apply_tree__removes_file</c> (tree.c).
    /// </summary>
    [Fact]
    public async Task ApplyToTree_DeletedFile_PostimageOmitsEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid c1 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "a\n", ["b.txt"] = "b\n" },
                "init\n", "refs/heads/main", parent: null, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitOid c2 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "a\n" },
                "delete b\n", "refs/heads/feature", parent: c1, ct);

            GitTree aTree = await TreeOfAsync(repo, c1, ct);
            GitTree bTree = await TreeOfAsync(repo, c2, ct);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Deleted, diff.GetDelta(0).Status);

            using GitIndex postimage = await repo.ApplyToTreeAsync(aTree, diff, cancellationToken: ct);
            Assert.Equal(1, postimage.EntryCount);
            Assert.Equal("a.txt", postimage.EntryByIndex(0).Path.ToUtf8String());
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ApplyAsync (workdir + index) ────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.ApplyAsync"/> in
    /// <see cref="GitApplyLocation.Both"/> mode applies a generated diff to
    /// both the workdir and the index: the modified file's content is
    /// written to the workdir, the index entry is updated, and the new blob
    /// is written to the ODB. Mirrors
    /// <c>test_apply_both__generated_diff</c> (both.c).
    /// </summary>
    [Fact]
    public async Task Apply_Both_Modification_WritesWorkdirAndIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid c1 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2\nline3\n" },
                "init\n", "refs/heads/main", parent: null, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitOid c2 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2-modified\nline3\n" },
                "modify a\n", "refs/heads/feature", parent: c1, ct);

            // Reset workdir + index to the preimage (c1) so the apply has a clean
            // preimage to operate on. CommitFilesAsync on `feature` dirtied the
            // workdir with the c2 content; the hard reset restores a.txt to the
            // preimage "line1\nline2\nline3\n".
            await ResetToCommitAsync(repo, c1, ct);

            GitTree aTree = await TreeOfAsync(repo, c1, ct);
            GitTree bTree = await TreeOfAsync(repo, c2, ct);

            // Generate the diff against the current HEAD tree.
            using GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: ct);

            // Apply to both workdir and index.
            await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: ct);

            // Workdir: a.txt has the modified content.
            string? workdirContent = await ReadWorkdirFileAsync(path, "a.txt", ct);
            Assert.Equal("line1\nline2-modified\nline3\n", workdirContent);

            // Index: a.txt points at the new blob.
            GitIndex index = await repo.GetIndexAsync(ct);
            Assert.Equal(1, index.EntryCount);
            GitIndexEntry entry = index.EntryByIndex(0);
            Assert.Equal("a.txt", entry.Path.ToUtf8String());

            GitTreeEntry? bEntry = bTree.EntryByName("a.txt");
            Assert.NotNull(bEntry);
            Assert.Equal(bEntry!.Value.Id, entry.Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.ApplyAsync"/> in
    /// <see cref="GitApplyLocation.Workdir"/> mode writes the postimage to
    /// the workdir only — the repo index is NOT updated. Mirrors
    /// <c>test_apply_workdir__generated_diff</c> (workdir.c).
    /// </summary>
    [Fact]
    public async Task Apply_WorkdirOnly_WritesWorkdir_LeavesIndexUnchanged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid c1 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2\nline3\n" },
                "init\n", "refs/heads/main", parent: null, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitOid c2 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2-modified\nline3\n" },
                "modify a\n", "refs/heads/feature", parent: c1, ct);

            await ResetToCommitAsync(repo, c1, ct);

            GitTree aTree = await TreeOfAsync(repo, c1, ct);
            GitTree bTree = await TreeOfAsync(repo, c2, ct);

            // Snapshot the index's pre-apply entry OID for later comparison.
            GitIndex preIndex = await repo.GetIndexAsync(ct);
            GitOid preOid = preIndex.EntryByIndex(0).Id;

            using GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: ct);
            await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: ct);

            // Workdir: modified.
            string? workdirContent = await ReadWorkdirFileAsync(path, "a.txt", ct);
            Assert.Equal("line1\nline2-modified\nline3\n", workdirContent);

            // Index: unchanged — still points at the preimage blob.
            GitIndex postIndex = await repo.GetIndexAsync(ct);
            Assert.Equal(1, postIndex.EntryCount);
            Assert.Equal(preOid, postIndex.EntryByIndex(0).Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.ApplyAsync"/> in
    /// <see cref="GitApplyLocation.Index"/> mode updates the index only —
    /// the workdir is NOT modified. Mirrors
    /// <c>test_apply_index__generated_diff</c> (index.c).
    /// </summary>
    [Fact]
    public async Task Apply_IndexOnly_UpdatesIndex_LeavesWorkdirUnchanged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid c1 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2\nline3\n" },
                "init\n", "refs/heads/main", parent: null, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitOid c2 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2-modified\nline3\n" },
                "modify a\n", "refs/heads/feature", parent: c1, ct);

            await ResetToCommitAsync(repo, c1, ct);

            GitTree aTree = await TreeOfAsync(repo, c1, ct);
            GitTree bTree = await TreeOfAsync(repo, c2, ct);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: ct);
            await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: ct);

            // Workdir: unchanged (still the preimage content).
            string? workdirContent = await ReadWorkdirFileAsync(path, "a.txt", ct);
            Assert.Equal("line1\nline2\nline3\n", workdirContent);

            // Index: updated to point at the new blob.
            GitIndex postIndex = await repo.GetIndexAsync(ct);
            Assert.Equal(1, postIndex.EntryCount);
            GitTreeEntry? bEntry = bTree.EntryByName("a.txt");
            Assert.NotNull(bEntry);
            Assert.Equal(bEntry!.Value.Id, postIndex.EntryByIndex(0).Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ApplyAsync with Check flag ──────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.ApplyAsync"/> with
    /// <see cref="GitApplyFlags.Check"/> validates the diff in memory
    /// without writing to the workdir or index. Mirrors
    /// <c>test_apply_both__check_mode</c> (both.c).
    /// </summary>
    [Fact]
    public async Task Apply_CheckFlag_DryRun_NoWorkdirOrIndexWrites()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid c1 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2\nline3\n" },
                "init\n", "refs/heads/main", parent: null, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitOid c2 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2-modified\nline3\n" },
                "modify a\n", "refs/heads/feature", parent: c1, ct);

            await ResetToCommitAsync(repo, c1, ct);

            GitTree aTree = await TreeOfAsync(repo, c1, ct);
            GitTree bTree = await TreeOfAsync(repo, c2, ct);

            // Snapshot pre-apply state.
            GitIndex preIndex = await repo.GetIndexAsync(ct);
            GitOid preOid = preIndex.EntryByIndex(0).Id;
            string preWorkdir = await File.ReadAllTextAsync(Path.Combine(path, "a.txt"), ct);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: ct);
            await repo.ApplyAsync(diff, GitApplyLocation.Both,
                new GitApplyOptions { Flags = GitApplyFlags.Check }, ct);

            // Workdir unchanged.
            Assert.Equal(preWorkdir, await File.ReadAllTextAsync(Path.Combine(path, "a.txt"), ct));

            // Index unchanged.
            GitIndex postIndex = await repo.GetIndexAsync(ct);
            Assert.Equal(preOid, postIndex.EntryByIndex(0).Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.ApplyAsync"/> throws
    /// <see cref="GitException"/> with <see cref="GitErrorCode.ApplyFail"/>
    /// when the preimage is not present in the workdir (the file the diff
    /// wants to modify is missing). Mirrors
    /// <c>test_apply_both__application_failure</c> (both.c).
    /// </summary>
    [Fact]
    public async Task Apply_PreimageMissing_ThrowsApplyFail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitOid c1 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2\nline3\n" },
                "init\n", "refs/heads/main", parent: null, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitOid c2 = await CommitFilesAsync(
                repo, path, new Dictionary<string, string> { ["a.txt"] = "line1\nline2-modified\nline3\n" },
                "modify a\n", "refs/heads/feature", parent: c1, ct);

            await ResetToCommitAsync(repo, c1, ct);

            GitTree aTree = await TreeOfAsync(repo, c1, ct);
            GitTree bTree = await TreeOfAsync(repo, c2, ct);

            // Delete a.txt from the workdir so the preimage is missing.
            File.Delete(Path.Combine(path, "a.txt"));
            // Also remove from the index — the preimage reader (WorkdirReader)
            // will not find the file.
            GitIndex idx = await repo.GetIndexAsync(ct);
            idx.RemoveByPath("a.txt");
            await idx.WriteAsync(ct);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: ct);

            GitException ex = await Assert.ThrowsAsync<GitException>(
                () => repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: ct).AsTask());
            Assert.Equal(GitErrorCode.ApplyFail, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
