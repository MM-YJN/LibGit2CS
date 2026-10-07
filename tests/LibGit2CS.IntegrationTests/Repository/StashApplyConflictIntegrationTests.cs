using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Repository;

/// <summary>
/// Integration tests for <see cref="GitRepository.StashApplyAsync"/> conflict
/// scenarios, exercised end-to-end against locally-initialized repos with real
/// stashes and repo reopens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The stash apply conflict path — where a
/// divergent commit makes the 3-way merge non-clean — was broken: conflict
/// entries (stages 1/2/3) were written to the workdir but never persisted to
/// the repo index. These tests verify the full round-trip: stash, diverge,
/// apply, <b>reopen the repo</b>, and assert that the on-disk index carries
/// the conflict entries with the correct blob OIDs.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Ports
/// <c>test_stash_apply__conflict_commit_with_default</c>,
/// <c>test_stash_apply__conflict_workdir_with_default</c>,
/// <c>test_stash_apply__conflict_untracked_with_default</c>, and
/// <c>test_stash_apply__conflict_index_with_default</c> from
/// <c>tests/libgit2/stash/apply.c</c>, adapted to build the sandbox from
/// scratch and verify conflict entries across repo reopens.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class StashApplyConflictIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-stashconflict-" + Guid.NewGuid().ToString("N"));

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
    /// Inits a non-bare repo and creates an initial commit on
    /// <c>refs/heads/main</c> with <paramref name="fileName"/> containing
    /// <paramref name="content"/>. Closes the repo.
    /// </summary>
    private static async Task InitRepoWithFileAsync(string path, string fileName, string content, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, ct);
        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync(fileName, ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
    }

    /// <summary>
    /// Reopens the repo, modifies <paramref name="fileName"/> in the workdir
    /// (without staging), and stashes the workdir change. Closes the repo.
    /// </summary>
    private static async Task ModifyAndStashAsync(string repoPath, string fileName, string newContent, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: ct);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, fileName), newContent, ct);
        await repo.StashSaveAsync(Sig, "wip", GitStashFlags.Default, ct);
    }

    /// <summary>
    /// Reopens the repo, stages <paramref name="fileName"/> with its current
    /// workdir content, writes the index, and creates a commit on
    /// <c>refs/heads/main</c>. Closes the repo.
    /// </summary>
    private static async Task CommitDivergentChangeAsync(string repoPath, string fileName, string content, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: ct);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, fileName), content, ct);
        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync(fileName, ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);
        GitObject head = (await repo.RevparseSingleAsync("HEAD", ct))!;
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [head.Id],
            Author = Sig,
            Committer = Sig,
            Message = "divergent\n",
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    /// <summary>
    /// Reads a blob identified by <paramref name="oid"/> from the repo as a
    /// UTF-8 string.
    /// </summary>
    private static async Task<string> ReadBlobAsync(GitRepository repo, GitOid oid, CancellationToken ct)
    {
        GitBlob? blob = await repo.ObjectLookupAsync<GitBlob>(oid, ct);
        Assert.NotNull(blob);
        return System.Text.Encoding.UTF8.GetString(blob!.Content.ToArray());
    }

    // ── Merge conflict from divergent commit ────────────────────────────

    /// <summary>
    /// A stash apply that hits a modify/modify conflict (divergent
    /// commit to the same line) must succeed, write stages 1/2/3 into the
    /// persisted repo index, and leave conflict markers in the workdir file.
    /// After reopening the repo, <see cref="GitIndex.HasConflicts"/> is true
    /// and <see cref="GitIndex.ConflictGet"/> returns the correct blob OIDs.
    /// </summary>
    /// <remarks>
    /// Ports <c>test_stash_apply__conflict_commit_with_default</c>
    /// (tests/libgit2/stash/apply.c:246-264).
    /// </remarks>
    [Fact]
    public async Task Conflict_Commit_Default_WritesConflictEntriesToPersistedIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "file.txt", "base\n", ct);
        try
        {
            await ModifyAndStashAsync(path, "file.txt", "stashed\n", ct);
            await CommitDivergentChangeAsync(path, "file.txt", "divergent\n", ct);

            // Apply — must succeed (not throw) despite the merge conflict.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await repo.StashApplyAsync(0, options: null, ct);
            }

            // Reopen and verify the on-disk index persisted the conflict entries.
            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitIndex idx = await repo2.GetIndexAsync(ct);

            Assert.True(idx.HasConflicts);

            (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = idx.ConflictGet("file.txt");
            Assert.NotNull(ancestor);
            Assert.NotNull(ours);
            Assert.NotNull(theirs);

            Assert.Equal("base\n", await ReadBlobAsync(repo2, ancestor!.Value.Id, ct));
            Assert.Equal("divergent\n", await ReadBlobAsync(repo2, ours!.Value.Id, ct));
            Assert.Equal("stashed\n", await ReadBlobAsync(repo2, theirs!.Value.Id, ct));

            // Workdir file must contain conflict markers.
            string wdContent = await File.ReadAllTextAsync(Path.Combine(path, "file.txt"), ct);
            Assert.Contains("<<<<<<< Updated upstream", wdContent);
            Assert.Contains("=======", wdContent);
            Assert.Contains(">>>>>>> Stashed changes", wdContent);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Non-conflicting files still applied ──────────────────────────────

    /// <summary>
    /// When a multi-file stash has one conflicting file and one clean
    /// file, the non-conflicting file must still be applied to the workdir.
    /// Ports <c>test_stash_apply__conflict_index_with_default</c>
    /// (tests/libgit2/stash/apply.c:138-157).
    /// </summary>
    [Fact]
    public async Task Conflict_OneFile_NonConflictingFileStillApplied()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "conflict.txt", "base\n", ct);

        // Add a second file in a separate commit.
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "clean.txt"), "base\n", ct);
            GitIndex idx = await repo.GetIndexAsync(ct);
            await idx.AddByPathAsync("clean.txt", ct);
            await idx.WriteAsync(ct);
            GitOid treeOid = await idx.WriteTreeAsync(ct);
            GitObject head = (await repo.RevparseSingleAsync("HEAD", ct))!;
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [head.Id],
                Author = Sig,
                Committer = Sig,
                Message = "add clean.txt\n",
                UpdateRef = "refs/heads/main",
            }, ct);
        }

        // Stash modifications to both files.
        await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "conflict.txt"), "stash-change\n", ct);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "clean.txt"), "stash-change\n", ct);
            await repo.StashSaveAsync(Sig, "wip", GitStashFlags.Default, ct);
        }

        // Divergent commit to conflict.txt only.
        await CommitDivergentChangeAsync(path, "conflict.txt", "divergent\n", ct);

        // Apply — conflict.txt conflicts, clean.txt should apply cleanly.
        await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
        await repo2.StashApplyAsync(0, options: null, ct);

        // conflict.txt is conflicted.
        GitIndex idx2 = await repo2.GetIndexAsync(ct);
        Assert.True(idx2.HasConflicts);

        // clean.txt was applied cleanly to the workdir.
        string cleanContent = await File.ReadAllTextAsync(Path.Combine(path, "clean.txt"), ct);
        Assert.Equal("stash-change\n", cleanContent);
    }

    // ── Workdir-level conflict (dirty workdir) ───────────────────────────

    /// <summary>
    /// A dirty workdir file that the stash also modifies causes a
    /// workdir-level conflict. The apply must throw
    /// <see cref="GitErrorCode.Conflict"/> and leave the index clean.
    /// Ports <c>test_stash_apply__conflict_workdir_with_default</c>
    /// (tests/libgit2/stash/apply.c:214-226).
    /// </summary>
    [Fact]
    public async Task Conflict_Workdir_Default_ThrowsAndLeavesIndexClean()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "file.txt", "hello\n", ct);
        try
        {
            await ModifyAndStashAsync(path, "file.txt", "stashed\n", ct);

            // Dirty the workdir with a different change.
            {
                await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
                await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), "dirty\n", ct);
            }

            // Apply must throw.
            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(() => repo2.StashApplyAsync(0, options: null, ct));
            Assert.Equal(GitErrorCode.Conflict, ex.Code);

            // Index must be clean.
            GitIndex idx = await repo2.GetIndexAsync(ct);
            Assert.False(idx.HasConflicts);

            // Workdir file is untouched (still the dirty version).
            string wdContent = await File.ReadAllTextAsync(Path.Combine(path, "file.txt"), ct);
            Assert.Equal("dirty\n", wdContent);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Untracked file conflict ─────────────────────────────────────────

    /// <summary>
    /// When the stash includes an untracked file and a blocking untracked
    /// file with the same name exists in the workdir, the checkout cannot
    /// create it. The apply must throw <see cref="GitErrorCode.Conflict"/> and
    /// leave the index clean.
    /// Ports <c>test_stash_apply__conflict_untracked_with_default</c>
    /// (tests/libgit2/stash/apply.c:180-194).
    /// </summary>
    [Fact]
    public async Task Conflict_Untracked_Default_ThrowsAndLeavesIndexClean()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "file.txt", "hello\n", ct);
        try
        {
            // Stash with an untracked file.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), "stashed\n", ct);
                await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "untracked.txt"), "stashed-untracked\n", ct);
                await repo.StashSaveAsync(Sig, "wip", GitStashFlags.IncludeUntracked, ct);
            }

            // Create a blocking untracked file with the same name.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "untracked.txt"), "blocking\n", ct);
            }

            // Apply must throw.
            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(() => repo2.StashApplyAsync(0, options: null, ct));
            Assert.Equal(GitErrorCode.Conflict, ex.Code);

            // Index must be clean.
            GitIndex idx = await repo2.GetIndexAsync(ct);
            Assert.False(idx.HasConflicts);

            // The blocking file is untouched.
            string content = await File.ReadAllTextAsync(Path.Combine(path, "untracked.txt"), ct);
            Assert.Equal("blocking\n", content);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Conflict entries survive repo reopen (persistence) ───────────────

    /// <summary>
    /// After a conflicting stash apply, the conflict entries (stages
    /// 1/2/3) must survive a full repo reopen cycle — proving they were
    /// written to the on-disk index, not just held in memory. Each stage's
    /// blob OID must resolve to the correct content.
    /// </summary>
    [Fact]
    public async Task Conflict_Entries_SurviveRepoReopen()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "f", "ancestor\n", ct);
        try
        {
            await ModifyAndStashAsync(path, "f", "theirs\n", ct);
            await CommitDivergentChangeAsync(path, "f", "ours\n", ct);

            // Apply in one session.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await repo.StashApplyAsync(0, options: null, ct);
            }

            // Verify in a fresh session — the index was read from disk.
            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitIndex idx = await repo2.GetIndexAsync(ct);

            Assert.True(idx.HasConflicts);

            // Collect stage entries from the raw entry list.
            Dictionary<int, GitOid> stages = [];
            foreach (GitIndexEntry e in idx.Entries)
            {
                if (e.Path.ToUtf8String() == "f" && e.Stage > 0)
                {
                    stages[e.Stage] = e.Id;
                }
            }

            Assert.Equal(3, stages.Count);
            Assert.True(stages.ContainsKey(1));
            Assert.True(stages.ContainsKey(2));
            Assert.True(stages.ContainsKey(3));

            Assert.Equal("ancestor\n", await ReadBlobAsync(repo2, stages[1], ct));
            Assert.Equal("ours\n", await ReadBlobAsync(repo2, stages[2], ct));
            Assert.Equal("theirs\n", await ReadBlobAsync(repo2, stages[3], ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── StashPop on conflict applies but drops the entry ─────────────────

    /// <summary>
    /// <see cref="GitRepository.StashPopAsync"/> on a conflicting apply
    /// applies the stash (writing conflict entries) and then unconditionally
    /// drops the stash entry. This matches <see cref="GitRepository.StashPopAsync"/>
    /// semantics (apply + drop). Callers needing "preserve on conflict"
    /// semantics must use <see cref="GitRepository.StashApplyAsync"/> +
    /// conditional <see cref="GitRepository.StashDropAsync"/>.
    /// </summary>
    [Fact]
    public async Task Conflict_Pop_AppliesAndDropsStashEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "f", "base\n", ct);
        try
        {
            await ModifyAndStashAsync(path, "f", "stashed\n", ct);
            await CommitDivergentChangeAsync(path, "f", "divergent\n", ct);

            // Pop — applies the stash (with conflicts) and drops the entry.
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.StashPopAsync(0, options: null, ct);

            // The stash entry is gone.
            List<GitStashEntry> entries = await repo.StashForEachAsync(ct).ToListAsync(ct);
            Assert.Empty(entries);

            // The index is conflicted.
            GitIndex idx = await repo.GetIndexAsync(ct);
            Assert.True(idx.HasConflicts);
            Assert.True(idx.ConflictGet("f") != (null, null, null));

            // The workdir has conflict markers.
            string wdContent = await File.ReadAllTextAsync(Path.Combine(path, "f"), ct);
            Assert.Contains("<<<<<<<", wdContent);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Clean apply after a prior conflicting apply ──────────────────────

    /// <summary>
    /// After a conflicting apply leaves conflict entries in the index, a
    /// subsequent clean stash apply on a different file must not clear the
    /// existing conflicts. This verifies that <c>DontUpdateIndex</c> on the
    /// clean-apply path does not clobber pre-existing conflict entries.
    /// </summary>
    [Fact]
    public async Task Conflict_ThenCleanApply_DoesNotClearExistingConflicts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "conflict.txt", "base\n", ct);

        // Add a second file.
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "clean.txt"), "base\n", ct);
            GitIndex idx = await repo.GetIndexAsync(ct);
            await idx.AddByPathAsync("clean.txt", ct);
            await idx.WriteAsync(ct);
            GitOid treeOid = await idx.WriteTreeAsync(ct);
            GitObject head = (await repo.RevparseSingleAsync("HEAD", ct))!;
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [head.Id],
                Author = Sig,
                Committer = Sig,
                Message = "add clean.txt\n",
                UpdateRef = "refs/heads/main",
            }, ct);
        }

        // Stash both files.
        await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "conflict.txt"), "stash-c\n", ct);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "clean.txt"), "stash-clean\n", ct);
            await repo.StashSaveAsync(Sig, "both\n", GitStashFlags.Default, ct);
        }

        // Divergent commit to conflict.txt.
        await CommitDivergentChangeAsync(path, "conflict.txt", "divergent\n", ct);

        // Apply — creates conflict on conflict.txt, applies clean.txt.
        await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
        await repo2.StashApplyAsync(0, options: null, ct);

        GitIndex idx2 = await repo2.GetIndexAsync(ct);
        Assert.True(idx2.HasConflicts);
        Assert.True(idx2.ConflictGet("conflict.txt") != (null, null, null));

        // clean.txt was applied.
        string cleanContent = await File.ReadAllTextAsync(Path.Combine(path, "clean.txt"), ct);
        Assert.Equal("stash-clean\n", cleanContent);
    }
}
