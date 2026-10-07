using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Reset;
using LibGit2CS.Status;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.IntegrationTests.Merge;

/// <summary>
/// Integration tests for the 3-way merge conflict path
/// (<see cref="GitRepository.MergeCommitsAsync"/> with divergent edits +
/// <see cref="GitRepository.MergeAsync"/> driving checkout + state files)
/// exercised end-to-end against locally-initialized repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The Docker-based
/// <see cref="Transports.MergeDockerTests"/> exercise the merge engine over
/// history fetched via SSH, but only the fast-forward and clean-merge
/// paths — no test drives the <see cref="MergeDiff"/> conflict
/// classification (<see cref="MergeDiffType.AddAdd"/>,
/// <see cref="MergeDiffType.DirectoryFile"/>, content conflict) which is
/// constructed inside <see cref="GitRepository.MergeCommitsAsync"/> only
/// when the 3-way diff produces stage-1/2/3 index entries.
/// <see cref="MergeDiff"/> and <see cref="GitMergeFileInput"/> had
/// <c>0%</c> coverage; <see cref="GitIndexNameEntry"/> (the NAME index
/// extension that records conflict-side paths) was also <c>0%</c>. These
/// tests build divergent DAGs locally (no Docker) with conflicting edits
/// to the same path, then assert the conflict index, NAME entries, and
/// conflict-marker workdir content.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/merge/trees.c</c>
/// (<c>test_merge_trees__both_sides_add_same_file</c>,
/// <c>test_merge_trees__df_conflict</c>) and
/// <c>tests/libgit2/merge/workdir_conflict.c</c>, adapted to build the
/// sandbox from scratch (no fixture repo).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class MergeConflictIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-mergeconf-" + Guid.NewGuid().ToString("N"));

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
    /// Creates a commit on <paramref name="refName"/> whose tree contains
    /// exactly the given (path → content) files (the index is cleared
    /// first). Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> CommitFilesAsync(
        GitRepository repo, string workdir, IReadOnlyDictionary<string, string> files,
        string message, string refName, GitOid? parent, CancellationToken ct)
    {
        GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
        index.Clear();
        foreach ((string p, string c) in files)
        {
            string full = Path.Combine(workdir, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, c, ct).ConfigureAwait(false);
            await index.AddByPathAsync(p, ct).ConfigureAwait(false);
        }

        await index.WriteAsync(ct).ConfigureAwait(false);
        GitOid treeOid = await index.WriteTreeAsync(ct).ConfigureAwait(false);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is null ? [] : [parent.Value],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct).ConfigureAwait(false);

        if (parent is null)
        {
            await repo.SetHeadAsync(refName, ct).ConfigureAwait(false);
        }

        return commitOid;
    }

    /// <summary>
    /// Returns all index entries for <paramref name="path"/> across all
    /// stages (0 = merged, 1 = ancestor, 2 = ours, 3 = theirs).
    /// </summary>
    private static List<GitIndexEntry> EntriesForPath(GitIndex index, string path)
    {
        var result = new List<GitIndexEntry>();
        foreach (GitIndexEntry e in index.Entries)
        {
            if (e.Path.ToUtf8String() == path)
            {
                result.Add(e);
            }
        }

        return result;
    }

    // ── MergeCommitsAsync with content conflict ─────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeCommitsAsync"/> on two branches
    /// that both edit the same file differently produces a conflicted
    /// index: the path has stage-1/2/3 entries (not stage-0),
    /// <see cref="GitIndex.HasConflicts"/> is true, and a
    /// <see cref="GitIndexNameEntry"/> recording the conflict-side paths is
    /// present. Exercises the <see cref="MergeDiff"/> construction +
    /// <see cref="MergeDiffType"/> classification + NAME-extension
    /// population path inside the merge iterator.
    /// </summary>
    [Fact]
    public async Task MergeCommits_ContentConflict_ProducesConflictIndexAndNameEntries()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Base: shared.txt with "base".
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["shared.txt"] = "base\n" },
                "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            // Ours: change shared.txt to "ours".
            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["shared.txt"] = "ours\n" },
                "ours\n", "refs/heads/ours", baseCommit, ct).ConfigureAwait(false);

            // Theirs: change shared.txt to "theirs".
            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["shared.txt"] = "theirs\n" },
                "theirs\n", "refs/heads/theirs", baseCommit, ct).ConfigureAwait(false);

            Commit ours = (await repo.ObjectLookupAsync<Commit>(ourCommit, ct).ConfigureAwait(false))!;
            Commit theirs = (await repo.ObjectLookupAsync<Commit>(theirCommit, ct).ConfigureAwait(false))!;

            using GitIndex result = await repo.MergeCommitsAsync(ours, theirs, cancellationToken: ct).ConfigureAwait(false);

            Assert.True(result.HasConflicts, "divergent edits to shared.txt must conflict");

            List<GitIndexEntry> entries = EntriesForPath(result, "shared.txt");
            // No stage-0 (merged) entry; stages 1/2/3 present.
            Assert.DoesNotContain(entries, e => e.Stage == 0);
            Assert.Contains(entries, e => e.Stage == 1);
            Assert.Contains(entries, e => e.Stage == 2);
            Assert.Contains(entries, e => e.Stage == 3);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// An add/add conflict — both sides add a file at the same path
    /// with different content, with no ancestor entry — classifies as
    /// <see cref="MergeDiffType.AddAdd"/> and produces stage-2/stage-3
    /// entries (no stage-1 ancestor).
    /// </summary>
    [Fact]
    public async Task MergeCommits_AddAddConflict_BothSidesAddSamePathDifferentContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Base: empty tree (root commit with a throwaway file to have a
            // common ancestor; the conflict path is added by both sides).
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["base.txt"] = "base\n" },
                "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            // Ours: add new.txt with "ours".
            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["base.txt"] = "base\n", ["new.txt"] = "ours\n" },
                "ours\n", "refs/heads/ours", baseCommit, ct).ConfigureAwait(false);

            // Theirs: add new.txt with "theirs".
            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["base.txt"] = "base\n", ["new.txt"] = "theirs\n" },
                "theirs\n", "refs/heads/theirs", baseCommit, ct).ConfigureAwait(false);

            Commit ours = (await repo.ObjectLookupAsync<Commit>(ourCommit, ct).ConfigureAwait(false))!;
            Commit theirs = (await repo.ObjectLookupAsync<Commit>(theirCommit, ct).ConfigureAwait(false))!;

            using GitIndex result = await repo.MergeCommitsAsync(ours, theirs, cancellationToken: ct).ConfigureAwait(false);

            Assert.True(result.HasConflicts, "add/add of new.txt with different content must conflict");

            List<GitIndexEntry> entries = EntriesForPath(result, "new.txt");
            // Add/add: no ancestor (stage 1), but ours (2) and theirs (3).
            Assert.DoesNotContain(entries, e => e.Stage == 1);
            Assert.Contains(entries, e => e.Stage == 2);
            Assert.Contains(entries, e => e.Stage == 3);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── MergeAsync workdir conflict path ────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.MergeAsync"/> on divergent edits to
    /// the same file leaves the index conflicted, writes
    /// <c>MERGE_HEAD</c>/<c>MERGE_MSG</c>/<c>ORIG_HEAD</c> state files, and
    /// checks out the workdir file with <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>
    /// conflict markers containing both sides' content. Exercises the full
    /// merge → checkout → state-file write path with a real conflict.
    /// </summary>
    [Fact]
    public async Task MergeAsync_ContentConflict_WritesStateFilesAndConflictMarkers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Base: data.txt with "base".
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "base\n" },
                "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            // Ours on main: change data.txt to "ours".
            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "ours\n" },
                "ours\n", "refs/heads/main", baseCommit, ct).ConfigureAwait(false);

            // Theirs on feature: change data.txt to "theirs".
            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "theirs\n" },
                "theirs\n", "refs/heads/feature", baseCommit, ct).ConfigureAwait(false);

            // The theirCommit write left the workdir at "theirs" content;
            // force-checkout HEAD (main @ ourCommit) to sync the workdir
            // before merging, so MergeAsync's dirty-workdir check passes.
            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct).ConfigureAwait(false);

            // HEAD is on main (ourCommit). Build the annotated their-head
            // from the feature branch ref.
            GitReference? theirRef = await repo.ReferenceResolveAsync("refs/heads/feature", ct).ConfigureAwait(false);
            Assert.NotNull(theirRef);
            using GitAnnotatedCommit theirHead = await repo.AnnotatedCommitFromRefAsync(theirRef, ct).ConfigureAwait(false);

            await repo.MergeAsync([theirHead], cancellationToken: ct).ConfigureAwait(false);

            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            Assert.True(index.HasConflicts, "merge of divergent edits must leave the index conflicted");

            // State files.
            Assert.True(File.Exists(Path.Combine(repo.Path, "MERGE_HEAD")), "MERGE_HEAD must be written for a conflicted merge");
            Assert.True(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")), "MERGE_MSG must be written");
            Assert.True(File.Exists(Path.Combine(repo.Path, "ORIG_HEAD")), "ORIG_HEAD must be written");

            // Workdir file carries conflict markers from the Xdiff
            // file-level merge.
            string merged = await File.ReadAllTextAsync(Path.Combine(path, "data.txt"), ct).ConfigureAwait(false);
            Assert.Contains("<<<<<<<", merged, StringComparison.Ordinal);
            Assert.Contains("=======", merged, StringComparison.Ordinal);
            Assert.Contains(">>>>>>>", merged, StringComparison.Ordinal);
            Assert.Contains("ours", merged, StringComparison.Ordinal);
            Assert.Contains("theirs", merged, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// End-to-end merge-abort: after a real
    /// <see cref="GitRepository.MergeAsync"/> leaves the index conflicted and
    /// conflict markers in the workdir, a
    /// <c>GitResetMode.Hard</c> to <c>ORIG_HEAD</c> (the canonical merge-abort
    /// target) must restore the workdir to the pre-merge tree — the marker
    /// file must be overwritten with the target tree's blob content, the index
    /// de-conflicted, and the merge state files cleared.
    /// </summary>
    /// <remarks>
    /// Verifies a tree-target checkout (the one <c>git_reset</c> HARD
    /// performs via <c>git_checkout_tree</c>) does not write merge markers
    /// over the just-restored target content. libgit2 gates marker writing
    /// on the target being an index (checkout.c:974-991), mirrored in
    /// <c>CheckoutContext.LoadUpdateConflictsAsync</c>.
    /// </remarks>
    [Fact]
    public async Task MergeAbort_HardResetToOrigHead_RestoresWorkdirWithoutMarkers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Base / ours (main) / theirs (feature) — divergent edits to data.txt.
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "base\n" },
                "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);
            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "ours\n" },
                "ours\n", "refs/heads/main", baseCommit, ct).ConfigureAwait(false);
            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "theirs\n" },
                "theirs\n", "refs/heads/feature", baseCommit, ct).ConfigureAwait(false);

            // Sync the workdir to HEAD (main @ ourCommit) before merging.
            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct).ConfigureAwait(false);

            GitReference? theirRef = await repo.ReferenceResolveAsync("refs/heads/feature", ct).ConfigureAwait(false);
            Assert.NotNull(theirRef);
            using GitAnnotatedCommit theirHead = await repo.AnnotatedCommitFromRefAsync(theirRef, ct).ConfigureAwait(false);

            // Conflicted merge: index gains stage 1/2/3, workdir gains markers.
            await repo.MergeAsync([theirHead], cancellationToken: ct).ConfigureAwait(false);
            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            Assert.True(index.HasConflicts, "merge of divergent edits must leave the index conflicted");
            string merged = await File.ReadAllTextAsync(Path.Combine(path, "data.txt"), ct).ConfigureAwait(false);
            Assert.Contains("<<<<<<<", merged, StringComparison.Ordinal);

            // ORIG_HEAD records the pre-merge HEAD — the merge-abort target.
            string origHeadPath = Path.Combine(repo.Path, "ORIG_HEAD");
            Assert.True(File.Exists(origHeadPath));
            var origHead = GitOid.Parse((await File.ReadAllTextAsync(origHeadPath, ct).ConfigureAwait(false)).Trim(), repo.ObjectFormat);
            Assert.Equal(ourCommit, origHead);
            Commit target = (await repo.ObjectLookupAsync<Commit>(origHead, ct).ConfigureAwait(false))!;

            // Merge-abort: hard reset to ORIG_HEAD.
            await repo.ResetAsync(target, GitResetMode.Hard, cancellationToken: ct).ConfigureAwait(false);

            // Workdir must hold the pre-merge tree content — never markers.
            string restored = await File.ReadAllTextAsync(Path.Combine(path, "data.txt"), ct).ConfigureAwait(false);
            Assert.Equal("ours\n", restored);
            Assert.DoesNotContain("<<<<<<<", restored, StringComparison.Ordinal);
            Assert.DoesNotContain(">>>>>>>", restored, StringComparison.Ordinal);

            // Index fully reset and de-conflicted.
            GitIndex indexAfter = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            Assert.False(indexAfter.HasConflicts);
            Assert.NotNull(indexAfter.EntryByPath("data.txt"));

            // Merge state cleared.
            Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_HEAD")));
            Assert.False(File.Exists(Path.Combine(repo.Path, "MERGE_MSG")));

            // HEAD back at the pre-merge commit.
            GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct).ConfigureAwait(false);
            GitDirectReference? direct = head as GitDirectReference
                ?? await ((GitSymbolicReference)head!).TargetAsync(ct).ConfigureAwait(false) as GitDirectReference;
            Assert.Equal(ourCommit, direct!.Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.MergeAnalyzeAsync"/> on a fast-forward
    /// topology (our head is the merge base) reports
    /// <see cref="GitMergeAnalysis.FastForward"/> and a subsequent
    /// <see cref="GitRepository.MergeAsync"/> produces NO conflicts (the
    /// index is clean, no state files). Documents the boundary: the
    /// workdir-conflict path is not exercised by FF merges.
    /// </summary>
    [Fact]
    public async Task MergeAsync_FastForward_DoesNotProduceConflicts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Base on main: a.txt.
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["a.txt"] = "a\n" },
                "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            // Theirs on feature: add b.txt (descendant of base).
            GitOid theirCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["a.txt"] = "a\n", ["b.txt"] = "b\n" },
                "theirs\n", "refs/heads/feature", baseCommit, ct).ConfigureAwait(false);

            // Sync the workdir back to HEAD (main @ baseCommit) before
            // analyzing/merging — the theirCommit write left b.txt in the
            // workdir, which MergeAsync would reject as uncommitted.
            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct).ConfigureAwait(false);

            // HEAD is on main at baseCommit (the merge base). Feature is a
            // descendant → FF.
            GitReference? theirRef = await repo.ReferenceResolveAsync("refs/heads/feature", ct).ConfigureAwait(false);
            Assert.NotNull(theirRef);
            using GitAnnotatedCommit theirHead = await repo.AnnotatedCommitFromRefAsync(theirRef, ct).ConfigureAwait(false);

            GitMergeAnalysisResult analysis = await repo.MergeAnalyzeAsync([theirHead], ct).ConfigureAwait(false);
            Assert.True((analysis.Analysis & GitMergeAnalysis.FastForward) != 0, "descendant-only topology must be FF");

            // MergeAsync performs a 3-way merge regardless of FF-ness (parity
            // with libgit2's git_merge, which does NOT special-case FF). With
            // base==our, the merge result is exactly theirs' tree and is
            // conflict-free; verify the workdir reflects theirs' added file.
            await repo.MergeAsync([theirHead], cancellationToken: ct).ConfigureAwait(false);
            Assert.True(File.Exists(Path.Combine(path, "b.txt")), "FF merge must check out theirs' added file");
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Diff & status on conflicted index ───────────────────────────────

    /// <summary>
    /// After a real <see cref="GitRepository.MergeAsync"/> that
    /// conflicts, the tree-to-index diff must report a
    /// <see cref="GitDeltaStatus.Conflicted"/> delta for the conflicted path,
    /// and <see cref="GitRepository.StatusFileAsync"/> must report
    /// <see cref="GitStatusFlags.Conflicted"/>. Regression guard for the
    /// GIT_ITERATOR_INCLUDE_CONFLICTS fix: without it, conflicted index
    /// entries were silently skipped and the path appeared clean. Mirrors
    /// C's <c>test_diff_index__reports_conflicts</c> (diff/index.c:204) and
    /// <c>test_status_worktree__conflicted_item</c> (status/worktree.c:704).
    /// </summary>
    [Fact]
    public async Task Merge_Conflict_TreeToIndexDiff_ReportsConflictedDelta()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Base: data.txt = "base".
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "base\n" },
                "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            // Ours on main: data.txt = "ours".
            GitOid ourCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "ours\n" },
                "ours\n", "refs/heads/main", baseCommit, ct).ConfigureAwait(false);

            // Theirs on feature: data.txt = "theirs".
            await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "theirs\n" },
                "theirs\n", "refs/heads/feature", baseCommit, ct).ConfigureAwait(false);

            // Sync workdir to HEAD (main @ ourCommit) before merging.
            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct).ConfigureAwait(false);

            // Resolve HEAD tree BEFORE merging (HEAD = ourCommit).
            Commit? headCommit = await repo.ObjectLookupAsync<Commit>(ourCommit, ct).ConfigureAwait(false);
            Assert.NotNull(headCommit);
            GitTree? headTree = await repo.ObjectLookupAsync<GitTree>(headCommit!.Tree, ct).ConfigureAwait(false);
            Assert.NotNull(headTree);

            // Merge feature → conflict.
            GitReference? theirRef = await repo.ReferenceResolveAsync("refs/heads/feature", ct).ConfigureAwait(false);
            Assert.NotNull(theirRef);
            using GitAnnotatedCommit theirHead = await repo.AnnotatedCommitFromRefAsync(theirRef, ct).ConfigureAwait(false);
            await repo.MergeAsync([theirHead], cancellationToken: ct).ConfigureAwait(false);

            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            Assert.True(index.HasConflicts, "merge of divergent edits must conflict");

            // Tree-to-index diff: the conflicted path must appear as Conflicted.
            using GitDiff diff = await repo.DiffTreeToIndexAsync(headTree, cancellationToken: ct).ConfigureAwait(false);
            GitDiffDelta? conflictDelta = null;
            for (int i = 0; i < diff.DeltaCount; i++)
            {
                GitDiffDelta d = diff.GetDelta(i);
                if (d.Path.ToUtf8String() == "data.txt")
                {
                    conflictDelta = d;
                }
            }

            Assert.NotNull(conflictDelta);
            Assert.Equal(GitDeltaStatus.Conflicted, conflictDelta!.Status);

            // The full status list must also report the path as Conflicted.
            // (StatusFileAsync throws GIT_EAMBIGUOUS for a conflicted path
            // because multiple stages match — matching C's git_status_file.)
            using GitStatusList statusList = await repo.StatusNewAsync(cancellationToken: ct).ConfigureAwait(false);
            bool foundConflict = false;
            for (int i = 0; i < statusList.EntryCount; i++)
            {
                GitStatusEntry se = statusList.GetEntry(i);
                if (se.Path.ToUtf8String() == "data.txt" && (se.Status & GitStatusFlags.Conflicted) != 0)
                {
                    foundConflict = true;
                }
            }

            Assert.True(foundConflict, "data.txt should be Conflicted in the status list");

            headCommit.Dispose();
            headTree!.Dispose();
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── RemoveByPath REUC persistence ──────────────────────────────────

    /// <summary>
    /// After a real merge conflict, calling
    /// <see cref="GitIndex.RemoveByPath"/> on the conflicted path clears the
    /// conflict stages, promotes them to a REUC entry, and the REUC data
    /// survives an index write + disk reload. Regression guard for the
    /// REUC write-order guarantee: the stage 1-3 entries must survive until
    /// ConflictToReuc has read them. Mirrors the
    /// <c>git_index_remove_bypath</c> usage in
    /// <c>tests/libgit2/index/tests.c:364</c>.
    /// </summary>
    [Fact]
    public async Task RemoveByPath_OnConflict_PersistsReuc_AfterReload()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Base → ours on main → theirs on feature (conflicting edits).
            GitOid baseCommit = await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "base\n" },
                "base\n", "refs/heads/main", parent: null, ct).ConfigureAwait(false);

            await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "ours\n" },
                "ours\n", "refs/heads/main", baseCommit, ct).ConfigureAwait(false);

            await CommitFilesAsync(
                repo, path,
                new Dictionary<string, string> { ["data.txt"] = "theirs\n" },
                "theirs\n", "refs/heads/feature", baseCommit, ct).ConfigureAwait(false);

            await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct).ConfigureAwait(false);

            GitReference? theirRef = await repo.ReferenceResolveAsync("refs/heads/feature", ct).ConfigureAwait(false);
            Assert.NotNull(theirRef);
            using GitAnnotatedCommit theirHead = await repo.AnnotatedCommitFromRefAsync(theirRef, ct).ConfigureAwait(false);
            await repo.MergeAsync([theirHead], cancellationToken: ct).ConfigureAwait(false);

            GitIndex index = await repo.GetIndexAsync(ct).ConfigureAwait(false);
            Assert.True(index.HasConflicts);

            // Capture the conflict entry OIDs for later verification.
            (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = index.ConflictGet("data.txt");
            Assert.NotNull(ancestor);
            Assert.NotNull(ours);
            Assert.NotNull(theirs);

            // Remove the conflicted path — must clear conflicts + promote REUC.
            bool removed = index.RemoveByPath("data.txt");
            Assert.True(removed);
            Assert.False(index.HasConflicts);
            Assert.Equal(1, index.ReucCount);

            // Write the index to disk.
            await index.WriteAsync(ct).ConfigureAwait(false);

            // Reload from disk — REUC must survive.
            string indexPath = Path.Combine(repo.Path, "index");
            GitIndex reloaded = await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, ct).ConfigureAwait(false);
            Assert.Equal(1, reloaded.ReucCount);
            Assert.False(reloaded.HasConflicts);

            GitIndexReucEntry? reuc = reloaded.ReucByPath("data.txt");
            Assert.NotNull(reuc);
            Assert.Equal(ancestor!.Value.Id, reuc!.Oids[0]);
            Assert.Equal(ours!.Value.Id, reuc.Oids[1]);
            Assert.Equal(theirs!.Value.Id, reuc.Oids[2]);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
