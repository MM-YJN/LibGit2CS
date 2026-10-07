using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitStatusEntry = LibGit2CS.Status.GitStatusEntry;
using GitStatusFlags = LibGit2CS.Status.GitStatusFlags;
using GitStatusList = LibGit2CS.Status.GitStatusList;
using GitStatusOptions = LibGit2CS.Status.GitStatusOptions;

namespace LibGit2CS.IntegrationTests.Ignore;

/// <summary>
/// Integration tests for the git ignore engine
/// (<see cref="Status.IgnoreContext"/>/<see cref="Status.IgnoreFile"/>/
/// <see cref="Status.IgnoreRule"/>/<see cref="Status.IgnoreState"/>)
/// exercised end-to-end against locally-initialized non-bare repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The <see cref="LibGit2CS.Status"/> ignore
/// machinery (<see cref="Status.IgnoreContext"/>,
/// <see cref="Status.IgnoreFile"/>, <see cref="Status.IgnoreRule"/>,
/// <see cref="Status.IgnoreState"/>) had <c>25-51%</c> integration coverage
/// — exercised only incidentally by the status path in the Docker-based
/// <c>Clone_NonBare_StatusDetectsEdits</c> test. These tests drive the
/// public entry points (<see cref="GitRepository.IsIgnoredAsync"/>,
/// <see cref="GitRepository.IgnoreAddRuleAsync"/>,
/// <see cref="GitRepository.IgnoreClearInternalRulesAsync"/>,
/// <see cref="GitRepository.StatusShouldIgnoreAsync"/>, and the
/// <see cref="GitStatusFlags.IncludeIgnored"/> status flag) without Docker
/// by building repos locally and writing <c>.gitignore</c> files directly.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Ports scenarios from
/// <c>tests/libgit2/status/worktree.c</c> (<c>test_status_worktree__ignores</c>
/// and related), adapted to build the sandbox from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class IgnoreIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-ignore-" + Guid.NewGuid().ToString("N"));

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
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = $"add {path}\n",
            UpdateRef = refName,
        }, ct);
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

    // ── Ignore rules ─────────────────────────────────────────────────────

    /// <summary>
    /// A file matching a <c>.gitignore</c> pattern is reported as
    /// <see cref="GitStatusFlags.Ignored"/> when
    /// <see cref="GitStatusFlags.IncludeIgnored"/> is set. Matches
    /// <c>test_status_worktree__ignores</c>.
    /// </summary>
    [Fact]
    public async Task Status_IgnoredFile_ReportsIgnored_WhenIncludeIgnored()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Write a .gitignore that ignores *.log files.
            await File.WriteAllTextAsync(Path.Combine(path, ".gitignore"), "*.log\n", ct);
            // Stage the .gitignore so it does not show as untracked.
            GitIndex index = await repo.GetIndexAsync(ct);
            await index.AddByPathAsync(".gitignore", ct);
            await index.WriteAsync(ct);

            // Create an ignored file in the workdir.
            await File.WriteAllTextAsync(Path.Combine(path, "foo.log"), "log\n", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.IncludeIgnored,
            };

            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            GitStatusEntry? e = FindEntry(list, "foo.log");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.Ignored) != 0,
                $"foo.log should be Ignored, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.IsIgnoredAsync"/> returns true for a
    /// path matching a <c>.gitignore</c> pattern and false for a tracked
    /// file. Matches the <c>git_status_should_ignore</c> assertions in
    /// <c>test_status_worktree__ignores</c>.
    /// </summary>
    [Fact]
    public async Task IsIgnored_ReturnsTrue_ForGitignoreMatch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await File.WriteAllTextAsync(Path.Combine(path, ".gitignore"), "*.log\n", ct);
            GitIndex index = await repo.GetIndexAsync(ct);
            await index.AddByPathAsync(".gitignore", ct);
            await index.WriteAsync(ct);

            await File.WriteAllTextAsync(Path.Combine(path, "foo.log"), "log\n", ct);

            Assert.True(await repo.IsIgnoredAsync("foo.log", ct), "foo.log should be ignored");
            Assert.False(await repo.IsIgnoredAsync("a.txt", ct), "a.txt should not be ignored");

            // StatusShouldIgnoreAsync is the same as IsIgnoredAsync.
            Assert.True(await repo.StatusShouldIgnoreAsync("foo.log", ct), "foo.log should be ignored via StatusShouldIgnoreAsync");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.IgnoreAddRuleAsync"/> adds in-memory
    /// ignore rules at the highest priority (without writing to disk). A
    /// file matching the added rule is reported as
    /// <see cref="GitStatusFlags.Ignored"/> by the status engine.
    /// </summary>
    [Fact]
    public async Task IgnoreAddRule_AddsInMemoryRule_HighestPriority()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // No .gitignore on disk — add an in-memory rule.
            await repo.IgnoreAddRuleAsync("*.tmp\n", ct);

            await File.WriteAllTextAsync(Path.Combine(path, "x.tmp"), "temp\n", ct);

            Assert.True(await repo.IsIgnoredAsync("x.tmp", ct), "x.tmp should be ignored by in-memory rule");

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.IncludeIgnored,
            };
            using GitStatusList list = await repo.StatusNewAsync(opts, ct);
            GitStatusEntry? e = FindEntry(list, "x.tmp");
            Assert.NotNull(e);
            Assert.True((e!.Status & GitStatusFlags.Ignored) != 0,
                $"x.tmp should be Ignored, got {e.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.IgnoreClearInternalRulesAsync"/> removes
    /// all in-memory rules added via <see cref="GitRepository.IgnoreAddRuleAsync"/>
    /// and re-seeds the defaults (<c>.</c>, <c>..</c>, <c>.git</c>). After
    /// clearing, a file that matched a previously-added rule is no longer
    /// ignored.
    /// </summary>
    [Fact]
    public async Task IgnoreClearInternalRules_RemovesInMemoryRules()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await repo.IgnoreAddRuleAsync("*.tmp\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "x.tmp"), "temp\n", ct);
            Assert.True(await repo.IsIgnoredAsync("x.tmp", ct), "x.tmp should be ignored before clear");

            await repo.IgnoreClearInternalRulesAsync(ct);

            Assert.False(await repo.IsIgnoredAsync("x.tmp", ct), "x.tmp should not be ignored after clear");

            // The default rules (. , .., .git) must still be present.
            Assert.True(await repo.IsIgnoredAsync(".git", ct), ".git should still be ignored by default rules");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// A <c>.gitignore</c> in a subdirectory only matches paths within
    /// that subdirectory (context scoping). A <c>.log</c> file inside the
    /// subdirectory is ignored; a file outside is not. Matches the context
    /// behavior in <c>ignore.c</c> (<c>parse_ignore_file</c> sets context
    /// from the file's relative path).
    /// </summary>
    [Fact]
    public async Task Status_NestedGitignore_RespectsSubdirectoryScope()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Create a subdirectory with its own .gitignore that ignores *.log.
            Directory.CreateDirectory(Path.Combine(path, "sub"));
            await File.WriteAllTextAsync(Path.Combine(path, "sub", ".gitignore"), "*.log\n", ct);

            // Create files: sub/c.log (should be ignored by sub/.gitignore),
            // sub/b.txt (not ignored), root.log (not ignored — only sub/.gitignore exists).
            await File.WriteAllTextAsync(Path.Combine(path, "sub", "c.log"), "log\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "sub", "b.txt"), "b\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "root.log"), "root\n", ct);

            Assert.True(await repo.IsIgnoredAsync("sub/c.log", ct), "sub/c.log should be ignored by sub/.gitignore");
            Assert.False(await repo.IsIgnoredAsync("sub/b.txt", ct), "sub/b.txt should not be ignored");
            Assert.False(await repo.IsIgnoredAsync("root.log", ct), "root.log should not be ignored (sub/.gitignore is scoped)");

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.IncludeIgnored,
            };
            using GitStatusList list = await repo.StatusNewAsync(opts, ct);

            GitStatusEntry? subC = FindEntry(list, "sub/c.log");
            Assert.NotNull(subC);
            Assert.True((subC!.Status & GitStatusFlags.Ignored) != 0,
                $"sub/c.log should be Ignored, got {subC.Status}");

            GitStatusEntry? subB = FindEntry(list, "sub/b.txt");
            Assert.NotNull(subB);
            Assert.True((subB!.Status & GitStatusFlags.WorkdirNew) != 0,
                $"sub/b.txt should be WorkdirNew, got {subB.Status}");

            GitStatusEntry? rootLog = FindEntry(list, "root.log");
            Assert.NotNull(rootLog);
            Assert.True((rootLog!.Status & GitStatusFlags.WorkdirNew) != 0,
                $"root.log should be WorkdirNew (not ignored), got {rootLog.Status}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// The ignored-directory leak regression: files inside a directory
    /// matched by a <c>.gitignore</c> rule — but whose own basenames do not
    /// match any rule — must be classified <c>Ignored</c>, never
    /// <c>Untracked</c>. <c>sub/.gitignore</c> with <c>node_modules</c> must
    /// collapse <c>sub/node_modules/**</c> to a single ignored entry for the
    /// directory (libgit2 semantics: the directory delta, not git CLI's
    /// collapsed-to-ancestor <c>!! sub/</c>). Ports
    /// <c>filesystem_iterator_frame_push_ignores</c> (iterator.c:1146-1178)
    /// + <c>GIT_ITERATOR_DONT_AUTOEXPAND</c> workdir diffs
    /// (diff_generate.c:1487).
    /// </summary>
    [Fact]
    public async Task Status_IgnoredDirectory_ContentsNotReportedAsUntracked()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // sub/.gitignore ignores node_modules; the file inside does not
            // itself match any rule.
            Directory.CreateDirectory(Path.Combine(path, "sub"));
            await File.WriteAllTextAsync(Path.Combine(path, "sub", ".gitignore"), "node_modules\n", ct);
            Directory.CreateDirectory(Path.Combine(path, "sub", "node_modules", "pkg"));
            await File.WriteAllTextAsync(Path.Combine(path, "sub", "node_modules", "pkg", "index.js"), "content\n", ct);

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.IncludeIgnored,
            };
            using GitStatusList list = await repo.StatusNewAsync(opts, ct);

            // The ignored directory collapses to a single IGNORED entry.
            GitStatusEntry? dir = FindEntry(list, "sub/node_modules/");
            Assert.NotNull(dir);
            Assert.True((dir!.Status & GitStatusFlags.Ignored) != 0,
                $"sub/node_modules/ should be Ignored, got {dir.Status}");

            // Nothing under node_modules/** leaks as untracked.
            Assert.Null(FindEntry(list, "sub/node_modules/pkg/index.js"));

            // Without INCLUDE_IGNORED, the directory is omitted entirely.
            var noIgnoredOpts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
            };
            using GitStatusList list2 = await repo.StatusNewAsync(noIgnoredOpts, ct);
            Assert.Null(FindEntry(list2, "sub/node_modules/"));
            Assert.Null(FindEntry(list2, "sub/node_modules/pkg/index.js"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Regression: a <c>.gitignore</c> nested at depth 2 is loaded by the
    /// status walk and its rules take effect.
    /// <c>FilesystemIterator.PushFrameAsync</c> must pass a single component
    /// to <c>IgnoreContext.PushDirAsync</c> (which appends its argument); the
    /// full workdir-relative path would
    /// double-count the parent prefix at depth >= 2 and silently drop
    /// the nested <c>.gitignore</c>. The walk-up <see cref="GitRepository.IsIgnoredAsync"/>
    /// path is unaffected — the tell-tale signature. Mirrors
    /// <c>filesystem_iterator_frame_push_ignores</c> (iterator.c:1146-1178),
    /// which passes <c>frame_entry->path + previous_frame->path_len</c> (a
    /// single component) to <c>git_ignore__push_dir</c>.
    /// </summary>
    [Fact]
    public async Task Status_NestedGitignoreAtDepthTwo_HonoredByStatusWalk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Seed a commit so HEAD has a tree (tree->index->workdir diff path,
            // distinct from the unborn-HEAD unit-test fixture).
            await CommitFileAsync(repo, path, "a.txt", "hello\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Depth-2 nested .gitignore with a basename rule not covered by any parent.
            Directory.CreateDirectory(Path.Combine(path, "nested", "deep"));
            await File.WriteAllTextAsync(Path.Combine(path, "nested", "deep", ".gitignore"), "*.tmp\n", ct);

            await File.WriteAllTextAsync(Path.Combine(path, "nested", "deep", "foo.tmp"), "x\n", ct);
            await File.WriteAllTextAsync(Path.Combine(path, "nested", "deep", "keep.txt"), "y\n", ct);

            // Depth-3 file under the same rule — confirms the rule applies at
            // deeper file levels (corruption would otherwise accumulate).
            Directory.CreateDirectory(Path.Combine(path, "nested", "deep", "inner"));
            await File.WriteAllTextAsync(Path.Combine(path, "nested", "deep", "inner", "bar.tmp"), "z\n", ct);

            // Walk-up path agrees (and agrees with real git).
            Assert.True(await repo.IsIgnoredAsync("nested/deep/foo.tmp", ct), "foo.tmp should be ignored (walk-up)");
            Assert.True(await repo.IsIgnoredAsync("nested/deep/inner/bar.tmp", ct), "bar.tmp should be ignored (walk-up)");

            var opts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked
                      | GitStatusFlags.RecurseUntrackedDirs
                      | GitStatusFlags.IncludeIgnored,
            };
            using GitStatusList list = await repo.StatusNewAsync(opts, ct);

            // Status walk must agree with the walk-up path.
            GitStatusEntry? foo = FindEntry(list, "nested/deep/foo.tmp");
            Assert.NotNull(foo);
            Assert.True((foo!.Status & GitStatusFlags.Ignored) != 0,
                $"nested/deep/foo.tmp should be Ignored, got {foo.Status}");

            GitStatusEntry? bar = FindEntry(list, "nested/deep/inner/bar.tmp");
            Assert.NotNull(bar);
            Assert.True((bar!.Status & GitStatusFlags.Ignored) != 0,
                $"nested/deep/inner/bar.tmp should be Ignored, got {bar.Status}");

            GitStatusEntry? keep = FindEntry(list, "nested/deep/keep.txt");
            Assert.NotNull(keep);
            Assert.True((keep!.Status & GitStatusFlags.WorkdirNew) != 0,
                $"nested/deep/keep.txt should be WorkdirNew, got {keep.Status}");

            // The nested .gitignore itself surfaces as untracked.
            GitStatusEntry? ignoreFile = FindEntry(list, "nested/deep/.gitignore");
            Assert.NotNull(ignoreFile);
            Assert.True((ignoreFile!.Status & GitStatusFlags.WorkdirNew) != 0,
                $"nested/deep/.gitignore should be WorkdirNew, got {ignoreFile.Status}");

            // Without IncludeIgnored, the ignored files are omitted entirely.
            var noIgnoredOpts = new GitStatusOptions
            {
                Flags = GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
            };
            using GitStatusList list2 = await repo.StatusNewAsync(noIgnoredOpts, ct);
            Assert.Null(FindEntry(list2, "nested/deep/foo.tmp"));
            Assert.Null(FindEntry(list2, "nested/deep/inner/bar.tmp"));
        }
        finally
        {
            Cleanup(path);
        }
    }
}
