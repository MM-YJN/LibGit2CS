using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Worktree;

/// <summary>
/// Integration tests for linked worktrees
/// (<see cref="LibGit2CS.Repository.Worktree"/>) exercised end-to-end
/// against locally-initialized repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>WorktreeTests</c> cover the
/// individual worktree operations (Add/List/Lookup/Lock/Unlock/Prune) against
/// a real repo, but each test exercises one operation in isolation. These
/// integration tests cover the <b>end-to-end linked-worktree workflow</b>:
/// add a worktree, open it as a separate <see cref="GitRepository"/>, commit
/// in it, and verify the new commit is reachable from the parent repo through
/// the shared object database — the reason linked worktrees exist. This
/// drives the full <see cref="GitRepository.OpenAsync"/> → worktree HEAD
/// resolution → ODB-sharing → <see cref="GitRepository.SetHeadAsync"/> path
/// that the unit tests don't chain together.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/worktree/worktree.c</c> (<c>test_worktree__open</c>,
/// <c>test_worktree__repository_path</c>) and the linked-worktree commit
/// workflow, adapted to build the sandbox from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class WorktreeIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-worktree-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Inits a non-bare repo at <paramref name="path"/>, creates an initial
    /// commit on <c>refs/heads/main</c> containing <c>README.md</c>, sets
    /// HEAD to <c>refs/heads/main</c>. Returns the parent repository (caller
    /// disposes via <c>await using</c>).
    /// </summary>
    private static async Task<GitRepository> InitRepoWithCommitAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("README.md", ct);
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
        return repo;
    }

    /// <summary>
    /// Opens the worktree at <paramref name="wtPath"/> as a fresh
    /// <see cref="GitRepository"/>, writes a new file, stages and commits it
    /// on the worktree's HEAD branch, and returns the new commit OID.
    /// </summary>
    private static async Task<GitOid> CommitInWorktreeAsync(
        string wtPath, GitContext context, string fileName, string content, CancellationToken ct)
    {
        await using GitRepository wtRepo = await GitRepository.OpenAsync(wtPath, context, cancellationToken: ct);
        string workdir = wtRepo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, ct);

        GitIndex idx = await wtRepo.GetIndexAsync(ct);
        await idx.AddByPathAsync(fileName, ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        // Resolve HEAD to get the parent.
        var head = (GitDirectReference)(await wtRepo.ReferenceResolveAsync("HEAD", ct))!;
        return await wtRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [head.Target],
            Author = Sig,
            Committer = Sig,
            Message = $"add {fileName}\n",
            UpdateRef = head.Name,
        }, ct);
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

    // ── Add + open + commit + cross-repo visibility ─────────────────────

    /// <summary>
    /// Adding a linked worktree creates a separate working directory
    /// with its own HEAD pointing at a new branch, and commits made in the
    /// worktree are visible from the parent repo's object database — the
    /// defining feature of linked worktrees. Mirrors the linked-worktree
    /// commit workflow described in <c>test_worktree__open</c> (worktree.c).
    /// </summary>
    [Fact]
    public async Task Add_OpenInWorktree_CommitVisibleFromParentRepo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            // Add a linked worktree named "topic" at a sibling path.
            string wtPath = Path.Combine(path, "wt-topic");
            LibGit2CS.Repository.Worktree wt = await repo.WorktreeAddAsync("topic", wtPath, cancellationToken: ct);

            Assert.Equal("topic", wt.Name);
            Assert.True(Directory.Exists(wtPath));
            Assert.True(File.Exists(Path.Combine(wtPath, ".git")));

            // The worktree's branch exists and points at the initial commit.
            GitReference? branch = await repo.BranchLookupAsync("topic", GitBranchType.Local, ct);
            Assert.NotNull(branch);

            // Commit in the worktree via a fresh repo handle.
            GitOid newCommit = await CommitInWorktreeAsync(
                wtPath, repo.Context, "feature.txt", "feature content\n", ct);

            // The new commit must be visible from the parent repo's ODB
            // (linked worktrees share the parent's object database).
            Commit? lookedUp = await repo.ObjectLookupAsync<Commit>(newCommit, ct);
            Assert.NotNull(lookedUp);
            Assert.Equal("add feature.txt\n", lookedUp!.Message);

            // The worktree's branch now points at the new commit.
            GitReference? updatedBranch = await repo.BranchLookupAsync("topic", GitBranchType.Local, ct);
            Assert.NotNull(updatedBranch);
            Assert.Equal(newCommit, ((GitDirectReference)updatedBranch!).Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="LibGit2CS.Repository.Worktree.FromRepositoryAsync"/>
    /// returns the <see cref="Worktree"/> metadata when given a
    /// <see cref="GitRepository"/> that is itself a linked worktree (opened
    /// from a worktree path). The returned worktree's name and path match
    /// the original add. Mirrors <c>test_worktree__open_from_repository</c>
    /// (worktree.c).
    /// </summary>
    [Fact]
    public async Task FromRepository_OnLinkedWorktree_ReturnsWorktreeMetadata()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            string wtPath = Path.Combine(path, "wt-from-repo");
            LibGit2CS.Repository.Worktree wt = await repo.WorktreeAddAsync("fromrepo", wtPath, cancellationToken: ct);

            // Open the worktree path as a fresh repository and ask for its
            // worktree metadata.
            await using GitRepository wtRepo = await GitRepository.OpenAsync(wtPath, repo.Context, cancellationToken: ct);
            Assert.True(wtRepo.IsWorktree);

            LibGit2CS.Repository.Worktree? fromRepo = await wtRepo.WorktreeFromRepositoryAsync(ct);
            Assert.NotNull(fromRepo);
            Assert.Equal("fromrepo", fromRepo!.Name);
            // Stored worktree paths use forward slashes and resolve parent
            // symlinks, including macOS's /var -> /private/var temporary root.
            Assert.Equal(PathHelpers.PrettifyDir(wtPath).TrimEnd('/'), fromRepo.Path);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="LibGit2CS.Repository.Worktree.FromRepositoryAsync"/>
    /// throws <see cref="GitException"/> when called on a non-worktree
    /// repository (a normal main repo). Mirrors the
    /// <c>git_worktree_open_from_repository</c> error path (worktree.c).
    /// </summary>
    [Fact]
    public async Task FromRepository_OnMainRepo_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            Assert.False(repo.IsWorktree);
            GitException ex = await Assert.ThrowsAsync<GitException>(
                () => repo.WorktreeFromRepositoryAsync(ct));
            Assert.Equal(GitErrorCategory.Worktree, ex.Category);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Lock / Unlock round-trip ────────────────────────────────────────

    /// <summary>
    /// <see cref="LibGit2CS.Repository.Worktree.LockAsync"/> writes a
    /// <c>locked</c> admin file with the given reason, and
    /// <see cref="LibGit2CS.Repository.Worktree.GetLockReasonAsync"/> returns
    /// it. <see cref="LibGit2CS.Repository.Worktree.Unlock"/> deletes the
    /// file and clears <see cref="LibGit2CS.Repository.Worktree.IsLocked"/>.
    /// Mirrors <c>test_worktree__lock_unlock</c> (worktree.c).
    /// </summary>
    [Fact]
    public async Task Lock_WithReason_GetLockReasonReturnsIt_UnlockClears()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            string wtPath = Path.Combine(path, "wt-lock");
            LibGit2CS.Repository.Worktree wt = await repo.WorktreeAddAsync("lockme", wtPath, cancellationToken: ct);
            Assert.False(wt.IsLocked);

            // Lock with a reason. The reason is the RAW `locked` file
            // contents (worktree.c:489-511) — a trailing newline is NOT
            // trimmed, matching `git worktree lock --reason` behavior.
            await wt.LockAsync("manual rebase in progress\n", ct);
            Assert.True(wt.IsLocked);
            Assert.Equal("manual rebase in progress\n", await wt.GetLockReasonAsync(ct));

            // Re-locking throws.
            await Assert.ThrowsAsync<GitException>(() => wt.LockAsync(null, ct));

            // Unlock removes the `locked` file and returns true.
            Assert.True(wt.Unlock());
            Assert.False(wt.IsLocked);
            Assert.Null(await wt.GetLockReasonAsync(ct));

            // Unlocking again returns false — C (worktree.c:462-487) returns
            // 1 (not an error) when the worktree is not locked.
            Assert.False(wt.Unlock());
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Validate + Prune ────────────────────────────────────────────────

    /// <summary>
    /// <see cref="LibGit2CS.Repository.Worktree.Validate"/> succeeds on
    /// a freshly-added worktree, but <see cref="LibGit2CS.Repository.Worktree.Prune"/>
    /// refuses to prune it (without the <see cref="WorktreePruneFlags.Valid"/>
    /// flag). After deleting the worktree's working directory, validate
    /// throws and prune (with <see cref="WorktreePruneFlags.Valid"/>) removes
    /// the admin dir. Mirrors <c>test_worktree__prune</c> (worktree.c).
    /// </summary>
    [Fact]
    public async Task Validate_OnValidWorktree_Succeeds_PruneRefusesValidWorktree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            string wtPath = Path.Combine(path, "wt-validate");
            LibGit2CS.Repository.Worktree wt = await repo.WorktreeAddAsync("validate", wtPath, cancellationToken: ct);

            // A freshly-added worktree is valid.
            wt.Validate();

            // IsPrunable without Valid flag → false (valid worktree is not prunable).
            Assert.False(wt.IsPrunable());

            // Prune without Valid flag → throws (not prunable).
            Assert.Throws<GitException>(() => wt.Prune());

            // Delete the worktree's working directory → validate now throws.
            Directory.Delete(wtPath, recursive: true);
            Assert.Throws<GitException>(() => wt.Validate());

            // Now it's prunable (invalid worktree).
            Assert.True(wt.IsPrunable());

            // Prune (without WorkingTree flag) removes the admin dir only.
            wt.Prune();
            Assert.False(Directory.Exists(wt.GitdirPath));

            // The worktree is no longer listed.
            Assert.Empty(await repo.WorktreeListAsync(ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="LibGit2CS.Repository.Worktree.Prune"/> with
    /// <see cref="WorktreePruneFlags.WorkingTree"/> also deletes the worktree's
    /// working directory. Mirrors <c>test_worktree__prune_working_tree</c>
    /// (worktree.c).
    /// </summary>
    [Fact]
    public async Task Prune_WithWorkingTreeFlag_DeletesWorkingDirectory()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            string wtPath = Path.Combine(path, "wt-prune-wt");
            LibGit2CS.Repository.Worktree wt = await repo.WorktreeAddAsync("prunewt", wtPath, cancellationToken: ct);
            wt.Validate();

            // Prune with both Valid + WorkingTree flags — the worktree is
            // valid, so we need the Valid flag to make it prunable.
            wt.Prune(new WorktreePruneOptions
            {
                Flags = WorktreePruneFlags.Valid | WorktreePruneFlags.WorkingTree,
            });

            // Both the admin dir and the working directory are gone.
            Assert.False(Directory.Exists(wt.GitdirPath));
            Assert.False(Directory.Exists(wtPath));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// A locked worktree is not prunable without the
    /// <see cref="WorktreePruneFlags.Locked"/> flag, even if it's invalid.
    /// With the Locked flag, prune succeeds. Mirrors
    /// <c>test_worktree__prune_locked</c> (worktree.c).
    /// </summary>
    [Fact]
    public async Task Prune_LockedWorktree_RequiresLockedFlag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            string wtPath = Path.Combine(path, "wt-locked");
            LibGit2CS.Repository.Worktree wt = await repo.WorktreeAddAsync("locked", wtPath, new WorktreeAddOptions { Lock = true }, ct);
            Assert.True(wt.IsLocked);

            // Make the worktree invalid (delete its working dir).
            Directory.Delete(wtPath, recursive: true);

            // Without the Locked flag, IsPrunable returns false (locked).
            Assert.False(wt.IsPrunable());

            // With the Locked flag, it's prunable.
            Assert.True(wt.IsPrunable(new WorktreePruneOptions { Flags = WorktreePruneFlags.Locked }));

            // Prune with Locked flag succeeds.
            wt.Prune(new WorktreePruneOptions { Flags = WorktreePruneFlags.Locked });
            Assert.False(Directory.Exists(wt.GitdirPath));
        }
        finally
        {
            Cleanup(path);
        }
    }
}
