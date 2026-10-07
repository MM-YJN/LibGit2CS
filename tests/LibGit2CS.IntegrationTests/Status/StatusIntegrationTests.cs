using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;
using GitStatusEntry = LibGit2CS.Status.GitStatusEntry;
using GitStatusFlags = LibGit2CS.Status.GitStatusFlags;
using GitStatusList = LibGit2CS.Status.GitStatusList;
using GitStatusOptions = LibGit2CS.Status.GitStatusOptions;
using GitStatusShow = LibGit2CS.Status.GitStatusShow;

namespace LibGit2CS.IntegrationTests.Status;

/// <summary>
/// Integration tests for the working-tree status engine
/// (<see cref="Status.GitStatusList"/>/<see cref="Status.GitStatusEntry"/>/
/// <see cref="Status.GitStatusOptions"/>/<see cref="Status.GitStatusShow"/>)
/// exercised end-to-end against locally-initialized non-bare repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The <see cref="LibGit2CS.Status"/> namespace
/// had <c>0%</c> integration coverage — the unit-test project covers
/// <c>UPDATE_INDEX</c> against locally-initialized repos, but the broader
/// status surface (show modes, per-flag scenarios, pathspec/recurse, sort,
/// <see cref="GitRepository.StatusFileAsync"/>, bare-repo / invalid-flag
/// error paths, and the <c>Baseline</c> override) was never exercised
/// end-to-end. The Docker-based <c>Clone_NonBare_StatusDetectsEdits</c> test
/// covers only the <c>WorkdirModified</c> + <c>WorkdirNew</c> paths and
/// requires an SSH clone. These tests close that gap without Docker by
/// building repos locally with <see cref="GitRepository.InitAsync"/> +
/// <see cref="Commit.CreateAsync"/> + <see cref="GitTreeBuilder"/>.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Ports scenarios from
/// <c>tests/libgit2/status/worktree.c</c> and <c>tests/libgit2/status/single.c</c>,
/// adapted to build the sandbox from scratch (no fixture repo).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class StatusIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-status-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Writes a file to the workdir, stages it, writes the tree, and creates
    /// a commit updating <paramref name="refName"/>. Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> CommitFileAsync(GitRepository repo, string workdir, string path, string content, string refName, CancellationToken ct)
    {
        string fullPath = Path.Combine(workdir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);
        GitIndex index = await repo.GetIndexAsync(ct);
        await index.AddByPathAsync(path, ct);
        await index.WriteAsync(ct);
        GitOid treeOid = await index.WriteTreeAsync(ct);
        // C (commit.c:109-117): with update_ref, parent[0] must equal the
        // ref tip — chain onto the current tip so repeated calls on the
        // same ref create linear history.
        GitReference? tip = await repo.ReferenceLookupAsync(refName, ct);
        GitOid[] parents = tip is GitDirectReference direct ? [direct.Target] : [];
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = Sig,
            Committer = Sig,
            Message = $"add {path}\n",
            UpdateRef = refName,
        }, ct);
    }

    /// <summary>
    /// Writes a file to the workdir and stages it into the index WITHOUT
    /// committing. Used to produce <c>IndexNew</c>/<c>IndexModified</c>
    /// states. Returns the blob OID.
    /// </summary>
    private static async Task<GitOid> StageFileAsync(GitRepository repo, string workdir, string path, string content, CancellationToken ct)
    {
        string fullPath = Path.Combine(workdir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);
        GitIndex index = await repo.GetIndexAsync(ct);
        await index.AddByPathAsync(path, ct);
        await index.WriteAsync(ct);
        return await index.WriteTreeAsync(ct);
    }

    /// <summary>
    /// Re-hashes a workdir file's content into the ODB and returns its blob
    /// OID. Matches <c>LocalTransportPushTests.LookupBlobOidAsync</c>.
    /// </summary>
    private static async Task<GitOid> LookupBlobOidAsync(GitRepository repo, string workdir, string fileName, CancellationToken ct)
    {
        byte[] content = await File.ReadAllBytesAsync(Path.Combine(workdir, fileName), ct);
        return await repo.ObjectWriteAsync(GitObjectType.Blob, content, ct);
    }

    /// <summary>Finds the first status entry whose <c>Path</c> matches, or null.</summary>
    private static GitStatusEntry? FindEntry(GitStatusList list, string path)
    {
        for (int i = 0; i < list.EntryCount; i++)
        {
            GitStatusEntry e = list.GetEntry(i);
            if (e.Path.ToUtf8String() == path)
            {
                return e;
            }
        }

        return null;
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

    // ── Show modes ───────────────────────────────────────────────────────

    /// <summary>
    /// A clean repo (one committed file, no workdir changes) reports
    /// zero status entries with default options. Matches
    /// <c>test_status_worktree__empty_repository</c>.
    /// </summary>
    [Fact]
    public async Task Status_CleanRepo_ReportsNoEntries()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            using GitStatusList list = await repo.StatusNewAsync(cancellationToken: ct);
            Assert.Equal(0, list.EntryCount);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Regression: a tracked non-ASCII path stored identically in
    /// the HEAD tree and the index must yield a clean status — no phantom staged
    /// rename. A tree entry name decoded as Latin-1 while the
    /// index path decoded as UTF-8 would turn the same on-disk bytes into two
    /// unequal strings, and the HEAD→Index diff would emit a delete+add that
    /// rename detection (<see cref="GitStatusFlags.RenamesHeadToIndex"/>)
    /// pairs into a spurious <see cref="GitStatusFlags.IndexRenamed"/> entry.
    /// <c>git status</c> / <c>git diff-index HEAD</c> report a clean tree here
    /// because git compares raw bytes.
    /// </summary>
    [Fact]
    public async Task Status_NonAsciiTrackedPath_WithRenames_ReportsClean()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Nested non-ASCII path exercises both the leaf name decode and the
            // '/'-splitting path lookup.
            await CommitFileAsync(repo, path, "üm/汉语.txt", "content\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.RenamesHeadToIndex
                      | GitStatusFlags.RenamesIndexToWorkdir,
            };
            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            Assert.Equal(0, list.EntryCount);

            // Direct HEAD-tree → index diff must also be empty.
            GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
            GitDirectReference direct = Assert.IsType<GitDirectReference>(head);
            Commit? commit = await repo.ObjectLookupAsync<Commit>(direct.Target, ct);
            GitTree? tree = await repo.ObjectLookupAsync<GitTree>(commit!.Tree, ct);
            Assert.NotNull(tree);
            using GitDiff diff = await repo.DiffTreeToIndexAsync(tree, cancellationToken: ct);
            Assert.Equal(0, diff.DeltaCount);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitStatusShow.IndexOnly"/> reports only staged changes
    /// (newly staged file B) and excludes workdir-only changes (untracked C,
    /// modified A). Matches <c>test_status_worktree__show_index_only</c>.
    /// </summary>
    [Fact]
    public async Task Status_ShowIndexOnly_ReportsStagedChangesOnly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Stage a new file (IndexNew) — do not commit.
            await StageFileAsync(repo, path, "b.txt", "staged\n", ct);

            // Modify the tracked file in the workdir only (WorkdirModified).
            await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "hello\nMODIFIED\n", ct);

            // Add an untracked file (WorkdirNew).
            await File.WriteAllTextAsync(Path.Combine(path, "c.txt"), "untracked\n", ct);

            var opts = new GitStatusOptions
            {
                Show = GitStatusShow.IndexOnly,
                Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            // IndexOnly → only the staged b.txt (IndexNew).
            Assert.Equal(1, list.EntryCount);
            GitStatusEntry? b = FindEntry(list, "b.txt");
            Assert.NotNull(b);
            Assert.True((b!.Status & GitStatusFlags.IndexNew) != 0, $"b.txt should be IndexNew, got {b.Status}");

            // a.txt (WorkdirModified) and c.txt (WorkdirNew) must NOT appear.
            Assert.Null(FindEntry(list, "a.txt"));
            Assert.Null(FindEntry(list, "c.txt"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitStatusShow.WorkdirOnly"/> reports only workdir
    /// changes (modified A, untracked C) and excludes index-only changes
    /// (staged B). Matches <c>test_status_worktree__show_workdir_only</c>.
    /// </summary>
    [Fact]
    public async Task Status_ShowWorkdirOnly_ReportsUnstagedOnly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await StageFileAsync(repo, path, "b.txt", "staged\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "hello\nMODIFIED\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "c.txt"), "untracked\n", ct);

            var opts = new GitStatusOptions
            {
                Show = GitStatusShow.WorkdirOnly,
                Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            // WorkdirOnly → a.txt (WorkdirModified) + c.txt (WorkdirNew).
            Assert.Equal(2, list.EntryCount);

            GitStatusEntry? a = FindEntry(list, "a.txt");
            Assert.NotNull(a);
            Assert.True((a!.Status & GitStatusFlags.WorkdirModified) != 0, $"a.txt should be WorkdirModified, got {a.Status}");

            GitStatusEntry? c = FindEntry(list, "c.txt");
            Assert.NotNull(c);
            Assert.True((c!.Status & GitStatusFlags.WorkdirNew) != 0, $"c.txt should be WorkdirNew, got {c.Status}");

            // b.txt (staged, IndexNew) must NOT appear.
            Assert.Null(FindEntry(list, "b.txt"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitStatusShow.IndexAndWorkdir"/> (the default) reports
    /// both staged and workdir-only changes. Matches
    /// <c>test_status_worktree__show_index_and_workdir</c>.
    /// </summary>
    [Fact]
    public async Task Status_ShowIndexAndWorkdir_ReportsBothSides()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await StageFileAsync(repo, path, "b.txt", "staged\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "hello\nMODIFIED\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "c.txt"), "untracked\n", ct);

            var opts = new GitStatusOptions
            {
                Show = GitStatusShow.IndexAndWorkdir,
                Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            // All three: b.txt (IndexNew), a.txt (WorkdirModified), c.txt (WorkdirNew).
            Assert.Equal(3, list.EntryCount);

            GitStatusEntry? b = FindEntry(list, "b.txt");
            Assert.NotNull(b);
            Assert.True((b!.Status & GitStatusFlags.IndexNew) != 0);

            GitStatusEntry? a = FindEntry(list, "a.txt");
            Assert.NotNull(a);
            Assert.True((a!.Status & GitStatusFlags.WorkdirModified) != 0);

            GitStatusEntry? c = FindEntry(list, "c.txt");
            Assert.NotNull(c);
            Assert.True((c!.Status & GitStatusFlags.WorkdirNew) != 0);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Per-status-flag scenarios ────────────────────────────────────────

    /// <summary>
    /// Editing a tracked file in the workdir (without staging) reports
    /// <see cref="GitStatusFlags.WorkdirModified"/>. Ports the
    /// <c>Clone_NonBare_StatusDetectsEdits</c> Docker test to a local repo.
    /// </summary>
    [Fact]
    public async Task Status_WorkdirModified_WhenTrackedFileEdited()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "hello\nMODIFIED\n", ct);

            using GitStatusList list = await repo.StatusNewAsync(cancellationToken: ct);
            Assert.Equal(1, list.EntryCount);

            GitStatusEntry? e = FindEntry(list, "a.txt");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.WorkdirModified) != 0,
                $"a.txt should be WorkdirModified, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Creating a new file in the workdir (untracked) reports
    /// <see cref="GitStatusFlags.WorkdirNew"/>.
    /// </summary>
    [Fact]
    public async Task Status_WorkdirNew_WhenUntrackedFileAdded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await File.WriteAllTextAsync(Path.Combine(path, "b.txt"), "new\n", ct);

            using GitStatusList list = await repo.StatusNewAsync(cancellationToken: ct);
            GitStatusEntry? e = FindEntry(list, "b.txt");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.WorkdirNew) != 0,
                $"b.txt should be WorkdirNew, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Deleting a tracked file from the workdir (without staging the
    /// removal) reports <see cref="GitStatusFlags.WorkdirDeleted"/>. Matches
    /// <c>test_status_worktree__simple_delete</c>.
    /// </summary>
    [Fact]
    public async Task Status_WorkdirDeleted_WhenTrackedFileRemoved()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            File.Delete(Path.Combine(path, "a.txt"));

            using GitStatusList list = await repo.StatusNewAsync(cancellationToken: ct);
            GitStatusEntry? e = FindEntry(list, "a.txt");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.WorkdirDeleted) != 0,
                $"a.txt should be WorkdirDeleted, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// In an unborn repo (no commits) with a staged file, the file
    /// reports as <see cref="GitStatusFlags.IndexNew"/> — the entire index
    /// is "new" relative to the empty HEAD.
    /// </summary>
    [Fact]
    public async Task Status_IndexNew_WhenFileStagedInCleanRepo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Stage a file without committing — HEAD is unborn.
            await StageFileAsync(repo, path, "a.txt", "staged\n", ct);

            using GitStatusList list = await repo.StatusNewAsync(cancellationToken: ct);
            GitStatusEntry? e = FindEntry(list, "a.txt");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.IndexNew) != 0,
                $"a.txt should be IndexNew, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Staging content that differs from HEAD reports
    /// <see cref="GitStatusFlags.IndexModified"/>.
    /// </summary>
    [Fact]
    public async Task Status_IndexModified_WhenStagedContentDiffersFromHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Overwrite workdir + stage new content.
            await StageFileAsync(repo, path, "a.txt", "hello\nMODIFIED\n", ct);

            using GitStatusList list = await repo.StatusNewAsync(cancellationToken: ct);
            GitStatusEntry? e = FindEntry(list, "a.txt");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.IndexModified) != 0,
                $"a.txt should be IndexModified, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Staging the removal of a tracked file (index.RemoveByPath +
    /// Write) reports <see cref="GitStatusFlags.IndexDeleted"/>. Matches
    /// <c>test_status_worktree__simple_delete_indexed</c>.
    /// </summary>
    [Fact]
    public async Task Status_IndexDeleted_WhenStagedRemoval()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitIndex index = await repo.GetIndexAsync(ct);
            index.RemoveByPath("a.txt");
            await index.WriteAsync(ct);

            using GitStatusList list = await repo.StatusNewAsync(cancellationToken: ct);
            GitStatusEntry? e = FindEntry(list, "a.txt");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.IndexDeleted) != 0,
                $"a.txt should be IndexDeleted, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// When a file is both staged (differs from HEAD) AND further
    /// modified in the workdir, status reports
    /// <see cref="GitStatusFlags.IndexModified"/> |
    /// <see cref="GitStatusFlags.WorkdirModified"/>.
    /// </summary>
    [Fact]
    public async Task Status_BothIndexAndWorkdirModified_WhenStagedAndWorkdirEdited()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "v1\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Stage v2 (IndexModified relative to HEAD).
            await StageFileAsync(repo, path, "a.txt", "v2\n", ct);

            // Further modify the workdir (WorkdirModified relative to index).
            await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "v3\n", ct);

            using GitStatusList list = await repo.StatusNewAsync(cancellationToken: ct);
            GitStatusEntry? e = FindEntry(list, "a.txt");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.IndexModified) != 0,
                $"a.txt should be IndexModified, got {e.Status}");
            Assert.True((e.Status & GitStatusFlags.WorkdirModified) != 0,
                $"a.txt should be WorkdirModified, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitStatusFlags.IncludeUnmodified"/> causes clean files
    /// to appear in the status list with <see cref="GitStatusFlags.Current"/>.
    /// </summary>
    [Fact]
    public async Task Status_IncludeUnmodified_ReportsCleanFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.IncludeUnmodified,
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            GitStatusEntry? e = FindEntry(list, "a.txt");
            Assert.NotNull(e);
            Assert.Equal(GitStatusFlags.Current, e!.Status & ~(GitStatusFlags.IncludeUntracked
                                                              | GitStatusFlags.RecurseUntrackedDirs
                                                              | GitStatusFlags.IncludeUnmodified));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Pathspec & recurse flags ────────────────────────────────────────

    /// <summary>
    /// <see cref="GitStatusOptions.PathSpecs"/> restricts status to a
    /// subtree. Matches <c>test_status_worktree__swap_subdir_with_recurse_and_pathspec</c>.
    /// </summary>
    [Fact]
    public async Task Status_PathSpec_FiltersToFileSubtree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Commit dir/a.txt, dir/b.txt, root.txt.
            await CommitFileAsync(repo, path, "dir/a.txt", "a\n", "refs/heads/main", ct);
            await CommitFileAsync(repo, path, "dir/b.txt", "b\n", "refs/heads/main", ct);
            // Note: second commit re-adds dir/a.txt to preserve it in the tree.
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Add untracked files: dir/c.txt (in subtree) and root_new.txt (outside).
            Directory.CreateDirectory(Path.Combine(path, "dir"));
            await File.WriteAllTextAsync(Path.Combine(path, "dir", "c.txt"), "c\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "root_new.txt"), "root\n", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
                PathSpecStrings = ["dir"],
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            // Only dir/c.txt should appear (root_new.txt is filtered out).
            GitStatusEntry? dirC = FindEntry(list, "dir/c.txt");
            Assert.NotNull(dirC);
            Assert.True((dirC!.Status & GitStatusFlags.WorkdirNew) != 0);
            Assert.Null(FindEntry(list, "root_new.txt"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitStatusFlags.RecurseUntrackedDirs"/> reports the
    /// full path of a deeply-nested untracked file rather than stopping at
    /// the top-level directory. Matches the
    /// <c>RECURSE_UNTRACKED_DIRS</c> behavior in libgit2's status engine.
    /// </summary>
    [Fact]
    public async Task Status_RecurseUntrackedDirs_ReportsNestedUntracked()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Create nested untracked: sub/deep/x.txt
            Directory.CreateDirectory(Path.Combine(path, "sub", "deep"));
            await File.WriteAllTextAsync(Path.Combine(path, "sub", "deep", "x.txt"), "x\n", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
            };
            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            GitStatusEntry? nested = FindEntry(list, "sub/deep/x.txt");
            Assert.NotNull(nested);
            Assert.True((nested!.Status & GitStatusFlags.WorkdirNew) != 0,
                $"sub/deep/x.txt should be WorkdirNew, got {nested.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitStatusFlags.DisablePathspecMatch"/> treats the
    /// pathspec as a literal path prefix, not a glob. Matches
    /// <c>test_status_worktree__within_subdir</c>.
    /// </summary>
    [Fact]
    public async Task Status_DisablePathspecMatch_TreatsPathSpecAsLiteral()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "dir/a.txt", "a\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Create untracked file inside dir/.
            Directory.CreateDirectory(Path.Combine(path, "dir"));
            await File.WriteAllTextAsync(Path.Combine(path, "dir", "new.txt"), "new\n", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.DisablePathspecMatch,
                PathSpecStrings = ["dir"],
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            // With DISABLE_PATHSPEC_MATCH, "dir" is a literal prefix —
            // dir/new.txt should be reported.
            GitStatusEntry? e = FindEntry(list, "dir/new.txt");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.WorkdirNew) != 0);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Sort flags ───────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitStatusFlags.SortCaseSensitively"/> orders entries
    /// by ASCII path. Matches <c>test_status_worktree__sorting_by_case</c>.
    /// </summary>
    [Fact]
    public async Task Status_SortCaseSensitively_OrderedByPath()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "a\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Create untracked files with mixed-case names.
            await File.WriteAllTextAsync(Path.Combine(path, "Banana"), "1\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "apple"), "2\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "Cherry"), "3\n", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.SortCaseSensitively,
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            var paths = new List<string>();
            for (int i = 0; i < list.EntryCount; i++)
            {
                paths.Add(list.GetEntry(i).Path.ToUtf8String());
            }

            // ASCII order: Banana, Cherry, apple (uppercase before lowercase).
            int idxBanana = paths.IndexOf("Banana");
            int idxCherry = paths.IndexOf("Cherry");
            int idxApple = paths.IndexOf("apple");
            Assert.True(idxBanana >= 0 && idxCherry >= 0 && idxApple >= 0,
                $"missing entries: {string.Join(", ", paths)}");
            Assert.True(idxBanana < idxCherry, $"Banana should precede Cherry (got {idxBanana} vs {idxCherry})");
            Assert.True(idxCherry < idxApple, $"Cherry should precede apple (got {idxCherry} vs {idxApple})");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitStatusFlags.SortCaseInsenzively"/> orders entries
    /// alphabetically regardless of case. Matches
    /// <c>test_status_worktree__sorting_by_case</c>.
    /// </summary>
    [Fact]
    public async Task Status_SortCaseInsensitively_OrderedAlphabetically()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "a\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await File.WriteAllTextAsync(Path.Combine(path, "Banana"), "1\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "apple"), "2\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "Cherry"), "3\n", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.SortCaseInsenzively,
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            var paths = new List<string>();
            for (int i = 0; i < list.EntryCount; i++)
            {
                paths.Add(list.GetEntry(i).Path.ToUtf8String());
            }

            // Case-insensitive order: apple, Banana, Cherry.
            int idxBanana = paths.IndexOf("Banana");
            int idxCherry = paths.IndexOf("Cherry");
            int idxApple = paths.IndexOf("apple");
            Assert.True(idxBanana >= 0 && idxCherry >= 0 && idxApple >= 0,
                $"missing entries: {string.Join(", ", paths)}");
            Assert.True(idxApple < idxBanana, $"apple should precede Banana (got {idxApple} vs {idxBanana})");
            Assert.True(idxBanana < idxCherry, $"Banana should precede Cherry (got {idxBanana} vs {idxCherry})");
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── StatusFileAsync ──────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.StatusFileAsync"/> on a clean tracked
    /// file returns <see cref="GitStatusFlags.Current"/>. Matches
    /// <c>test_status_single__hash_single_file</c>.
    /// </summary>
    [Fact]
    public async Task StatusFile_CleanTrackedFile_ReturnsCurrent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitStatusFlags status = await repo.StatusFileAsync("a.txt", ct);
            Assert.Equal(GitStatusFlags.Current, status);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.StatusFileAsync"/> on a modified
    /// workdir file returns <see cref="GitStatusFlags.WorkdirModified"/>.
    /// </summary>
    [Fact]
    public async Task StatusFile_ModifiedFile_ReturnsWorkdirModified()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "hello\nMOD\n", ct);

            GitStatusFlags status = await repo.StatusFileAsync("a.txt", ct);
            Assert.True((status & GitStatusFlags.WorkdirModified) != 0,
                $"a.txt should be WorkdirModified, got {status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.StatusFileAsync"/> on a nonexistent path
    /// throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.NotFound"/>. Matches
    /// <c>test_status_single__nonexistent_file</c>.
    /// </summary>
    [Fact]
    public async Task StatusFile_Nonexistent_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            GitException ex = await Assert.ThrowsAsync<GitException>(
                async () => await repo.StatusFileAsync("nope.txt", ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.StatusFileAsync"/> on a new untracked
    /// file in an empty repo (no commits) returns
    /// <see cref="GitStatusFlags.WorkdirNew"/>. Matches
    /// <c>test_status_single__file_empty_repo</c>.
    /// </summary>
    [Fact]
    public async Task StatusFile_UntrackedInEmptyRepo_ReturnsWorkdirNew()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // No commit — HEAD is unborn.
            await File.WriteAllTextAsync(Path.Combine(path, "new.txt"), "new\n", ct);

            GitStatusFlags status = await repo.StatusFileAsync("new.txt", ct);
            Assert.True((status & GitStatusFlags.WorkdirNew) != 0,
                $"new.txt should be WorkdirNew, got {status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Error paths & bare repo ──────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.StatusNewAsync"/> on a bare repo throws
    /// <see cref="GitException"/> with <see cref="GitErrorCode.BareRepo"/>.
    /// </summary>
    [Fact]
    public async Task Status_BareRepo_ThrowsBareRepo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string barePath = Path.Combine(Path.GetTempPath(), "libgit2cs-status-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository bare = await GitRepository.InitAsync(barePath, isBare: true, new GitContext(), cancellationToken: ct);

            GitException ex = await Assert.ThrowsAsync<GitException>(
                async () => await bare.StatusNewAsync(cancellationToken: ct));
            Assert.Equal(GitErrorCode.BareRepo, ex.Code);
        }
        finally
        {
            Cleanup(barePath);
        }
    }

    /// <summary>
    /// Combining <see cref="GitStatusFlags.NoRefresh"/> with
    /// <see cref="GitStatusFlags.UpdateIndex"/> throws
    /// <see cref="GitException"/> with <see cref="GitErrorCode.Invalid"/>.
    /// Matches the validation in <see cref="GitStatusList.NewAsync"/>.
    /// </summary>
    [Fact]
    public async Task Status_NoRefreshWithUpdateIndex_ThrowsInvalid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.NoRefresh
                      | GitStatusFlags.UpdateIndex,
            };

            GitException ex = await Assert.ThrowsAsync<GitException>(
                async () => await repo.StatusNewAsync(opts, ct));
            Assert.Equal(GitErrorCode.Invalid, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Baseline override ────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitStatusOptions.Baseline"/> overrides the HEAD tree
    /// used for the HEAD→index diff. Commit v1 as HEAD, stage v2 in the
    /// index, then pass <c>Baseline = v1Tree</c> — the file should report
    /// <see cref="GitStatusFlags.IndexModified"/> because the index (v2)
    /// differs from the baseline (v1).
    /// </summary>
    [Fact]
    public async Task Status_BaselineTree_OverridesHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Commit 1: a.txt = v1 (this is the baseline tree).
            GitOid commit1 = await CommitFileAsync(repo, path, "a.txt", "v1\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Resolve the v1 tree.
            Commit? c1 = await repo.ObjectLookupAsync<Commit>(commit1, ct);
            Assert.NotNull(c1);
            GitTree? v1Tree = await repo.ObjectLookupAsync<GitTree>(c1!.Tree, ct);
            Assert.NotNull(v1Tree);

            // Commit 2: a.txt = v2 (now HEAD points at v2).
            // Stage v2 content, write tree, commit.
            await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "v2\n", ct);
            GitIndex index = await repo.GetIndexAsync(ct);
            await index.AddByPathAsync("a.txt", ct);
            await index.WriteAsync(ct);
            GitOid tree2 = await index.WriteTreeAsync(ct);
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = tree2,
                Parents = [commit1],
                Author = Sig,
                Committer = Sig,
                Message = "v2\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            // Now stage v3 (index = v3, HEAD = v2, baseline = v1).
            await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "v3\n", ct);
            await index.AddByPathAsync("a.txt", ct);
            await index.WriteAsync(ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
                Baseline = v1Tree,
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            GitStatusEntry? e = FindEntry(list, "a.txt");
            Assert.NotNull(e);
            // Baseline=v1, index=v3 → IndexModified (content differs from baseline).
            Assert.True((e!.Status & GitStatusFlags.IndexModified) != 0,
                $"a.txt should be IndexModified relative to baseline, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Conflict & rename regression guards ─────────────────────────────

    /// <summary>
    /// A conflicted index path (stages 1/2/3, no stage 0) must surface as
    /// <see cref="GitStatusFlags.Conflicted"/> in the status list. Regression
    /// guard for the GIT_ITERATOR_INCLUDE_CONFLICTS fix: without it, the
    /// tree-to-index diff iterator skipped unmerged entries and the conflicted
    /// path appeared clean.
    /// </summary>
    [Fact]
    public async Task Status_ConflictedIndex_ReportsConflicted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "base\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Build a conflict: three different blob OIDs at stages 1/2/3.
            GitIndex index = await repo.GetIndexAsync(ct);
            GitOid ancestorOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), ct);
            GitOid oursOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), ct);
            GitOid theirsOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), ct);
            index.ConflictAdd(
                new GitIndexEntry("a.txt", ancestorOid, GitFileMode.Regular),
                new GitIndexEntry("a.txt", oursOid, GitFileMode.Regular),
                new GitIndexEntry("a.txt", theirsOid, GitFileMode.Regular));
            await index.WriteAsync(ct);

            using GitStatusList list = await repo.StatusNewAsync(cancellationToken: ct);
            GitStatusEntry? entry = FindEntry(list, "a.txt");
            Assert.NotNull(entry);
            Assert.True((entry!.Status & GitStatusFlags.Conflicted) != 0,
                $"a.txt should be Conflicted, got {entry.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Workdir-rename detection must pair a deleted tracked file with an
    /// untracked new file of identical content when
    /// <see cref="GitStatusFlags.RenamesIndexToWorkdir"/> is set. Regression
    /// guard for the zero-OID workdir-content fix in DiffTransform: without it,
    /// the untracked file's similarity hash was computed from empty content and
    /// could never match the deleted source, so status reported separate
    /// WorkdirDeleted + WorkdirNew entries instead of a WorkdirRenamed.
    /// </summary>
    [Fact]
    public async Task Status_WorkdirRename_UntrackedTarget_Paired()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            const string shared = "shared content for rename detection\n";
            await CommitFileAsync(repo, path, "old.txt", shared, "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Workdir-only: delete old.txt, create untracked new.txt with same content.
            File.Delete(Path.Combine(path, "old.txt"));
            await File.WriteAllTextAsync(Path.Combine(path, "new.txt"), shared, ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RenamesIndexToWorkdir,
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);

            // There should be exactly one entry: a workdir rename old.txt → new.txt.
            GitStatusEntry? renameEntry = null;
            for (int i = 0; i < list.EntryCount; i++)
            {
                GitStatusEntry e = list.GetEntry(i);
                if ((e.Status & GitStatusFlags.WorkdirRenamed) != 0)
                {
                    renameEntry = e;
                }
            }

            Assert.NotNull(renameEntry);
            Assert.Equal("old.txt", renameEntry!.Path.ToUtf8String());
            Assert.Equal("new.txt", renameEntry.NewPath.ToUtf8String());
        }
        finally
        {
            Cleanup(path);
        }
    }
}
