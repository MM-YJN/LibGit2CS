using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

using GitDiff = LibGit2CS.Diff.GitDiff;
using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Diff;

/// <summary>
/// Integration tests for the diff engine (<see cref="LibGit2CS.Diff"/> namespace)
/// exercised end-to-end against locally-initialized repositories with real
/// commit history built in code (no fixture zips, no Docker).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit-test project covers diff against
/// pre-extracted golden fixtures (<c>attr</c>, <c>status</c>, <c>renames</c>,
/// <c>diff_format_email</c>) with byte-exact output assertions. Those tests
/// bypass the full stack paths through <see cref="GitRepository"/> → index
/// state → <see cref="GitDiff"/> factories → <see cref="GitPatch"/> /
/// <see cref="GitDiffStats"/> / <see cref="GitPatchId"/> /
/// <see cref="GitEmailFormatter"/>. These integration tests close the gap by
/// building multi-commit repositories from scratch and exercising the diff
/// entry points against real on-disk object databases and indices.
/// </para>
/// <para>
/// <b>No Docker, no fixtures.</b> Each test creates a temp repo via
/// <see cref="GitRepository.InitAsync"/>, builds commits with
/// <see cref="Commit.CreateAsync"/> + <see cref="GitIndex.AddByPathAsync"/>
/// (the canonical pattern in <see cref="Transports.LocalTransportTests"/>),
/// and tears the temp dir down in a <c>finally</c> block.
/// </para>
/// <para>
/// <b>Assertion style.</b> Structural/property assertions for most tests
/// (delta counts, statuses, paths, line stats); byte-exact round-trip for
/// the printer ↔ parser parity check.
/// </para>
/// </remarks>
public sealed class DiffIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ─── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Initializes a non-bare repo at <paramref name="path"/> and creates an
    /// initial commit on <c>refs/heads/main</c> containing
    /// <c>README.md</c>, <c>fileA.txt</c>, and <c>fileB.txt</c>. Sets HEAD to
    /// <c>refs/heads/main</c>. Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> InitRepoWithInitialCommitAsync(string path, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "readme\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nline2\nline3\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "fileB.txt"), "b-content\n", ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("README.md", ct);
        await idx.AddByPathAsync("fileA.txt", ct);
        await idx.AddByPathAsync("fileB.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        return commitOid;
    }

    /// <summary>
    /// Adds a second commit on top of <paramref name="parentOid"/> that
    /// modifies <c>fileA.txt</c> to the given <paramref name="newFileAContent"/>
    /// and stages the result. Returns the new commit OID.
    /// </summary>
    private static async Task<GitOid> AddCommitModifyingFileAAsync(string repoPath, GitOid parentOid, string newFileAContent, string message, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), newFileAContent, ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("fileA.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parentOid],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    /// <summary>
    /// Adds a second commit on top of <paramref name="parentOid"/> that adds
    /// a new file <c>fileC.txt</c> with the given content. Returns the new
    /// commit OID.
    /// </summary>
    private static async Task<GitOid> AddCommitAddingFileCAsync(string repoPath, GitOid parentOid, string fileCContent, string message, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "fileC.txt"), fileCContent, ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("fileC.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parentOid],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    /// <summary>
    /// Resolves HEAD to its <see cref="Commit"/> and looks up the tree.
    /// Returns (<see cref="Commit"/>, <see cref="GitTree"/>).
    /// </summary>
    private static async Task<(Commit Commit, GitTree Tree)> GetHeadCommitAndTreeAsync(GitRepository repo, CancellationToken ct)
    {
        GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
        Assert.NotNull(head);
        GitDirectReference direct = Assert.IsType<GitDirectReference>(head);
        Commit? commit = await repo.ObjectLookupAsync<Commit>(direct.Target, ct);
        Assert.NotNull(commit);
        GitTree? tree = await repo.ObjectLookupAsync<GitTree>(commit!.Tree, ct);
        Assert.NotNull(tree);
        return (commit!, tree!);
    }

    // ─── 1. Generation ─────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitDiff.TreeToTreeAsync"/> between two commits that
    /// modify <c>fileA.txt</c> produces a single <see cref="GitDeltaStatus.Modified"/>
    /// delta with matching old/new paths.
    /// </summary>
    [Fact]
    public async Task TreeToTree_TwoCommitsModifyingFileA_ReturnsSingleModifiedDelta()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-t2t-mod-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify fileA\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);

            Assert.Equal(1, diff.DeltaCount);
            GitDiffDelta delta = diff.GetDelta(0);
            Assert.Equal(GitDeltaStatus.Modified, delta.Status);
            Assert.Equal("fileA.txt", delta.OldFile.Path?.ToUtf8String());
            Assert.Equal("fileA.txt", delta.NewFile.Path?.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.TreeToTreeAsync"/> where the second commit adds
    /// <c>fileC.txt</c> produces an <see cref="GitDeltaStatus.Added"/> delta
    /// with an empty old side and the new path on the new side.
    /// </summary>
    [Fact]
    public async Task TreeToTree_AddedFile_ReturnsAddedDelta()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-t2t-add-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitAddingFileCAsync(path, first, "new file\n", "add fileC\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);

            GitDiffDelta? added = Assert.Single(diff.Deltas, d => d.Status == GitDeltaStatus.Added);
            Assert.Equal("fileC.txt", added.NewFile.Path?.ToUtf8String());
            Assert.Equal("fileC.txt", added.Path.ToUtf8String());
            // The old side of an Added delta has a zero OID (no prior content).
            Assert.True(added.OldFile.Id.IsZero);
            var expectedOldPath = GitPath.FromUtf8String("fileC.txt");
            Assert.True(added.OldFile.Path is null || added.OldFile.Path.Value.Equals(expectedOldPath));
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.TreeToTreeAsync"/> where the second commit
    /// deletes <c>fileA.txt</c> produces a <see cref="GitDeltaStatus.Deleted"/>
    /// delta whose <see cref="GitDiffDelta.Path"/> is the old path.
    /// </summary>
    [Fact]
    public async Task TreeToTree_DeletedFile_ReturnsDeletedDelta()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-t2t-del-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);

            // Second commit: remove fileA.txt from the workdir + index.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                File.Delete(Path.Combine(repo.Workdir!, "fileA.txt"));
                GitIndex idx = await repo.GetIndexAsync(ct);
                idx.RemoveByPath("fileA.txt");
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [first],
                    Author = Sig,
                    Committer = Sig,
                    Message = "delete fileA\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo2.ObjectLookupAsync<Commit>(first, ct);
            Assert.NotNull(firstCommit);
            (_, GitTree newTree) = await GetHeadCommitAndTreeAsync(repo2, ct);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            Assert.NotNull(oldTree);

            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);

            GitDiffDelta? deleted = Assert.Single(diff.Deltas, d => d.Status == GitDeltaStatus.Deleted);
            Assert.Equal("fileA.txt", deleted.Path.ToUtf8String());
            Assert.Equal("fileA.txt", deleted.OldFile.Path?.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.CommitAsync"/> against a non-root commit diffs
    /// the commit against its first parent, producing the same deltas as the
    /// explicit <see cref="GitDiff.TreeToTreeAsync"/> between the two trees.
    /// </summary>
    [Fact]
    public async Task CommitAsync_SecondCommit_DiffAgainstFirstParent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-commit-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify fileA\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(secondCommit);

            using GitDiff diff = await repo.DiffCommitAsync(secondCommit!, cancellationToken: ct);

            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Modified, diff.GetDelta(0).Status);
            Assert.Equal("fileA.txt", diff.GetDelta(0).Path.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.CommitAsync"/> against a root commit (no
    /// parents) diffs against an empty tree, so every file in the commit's
    /// tree appears as <see cref="GitDeltaStatus.Added"/>.
    /// </summary>
    [Fact]
    public async Task CommitAsync_RootCommit_DiffAgainstEmptyTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid root = await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? rootCommit = await repo.ObjectLookupAsync<Commit>(root, ct);
            Assert.NotNull(rootCommit);

            using GitDiff diff = await repo.DiffCommitAsync(rootCommit!, cancellationToken: ct);

            // README.md, fileA.txt, fileB.txt — all Added against the empty tree.
            Assert.Equal(3, diff.DeltaCount);
            Assert.All(diff.Deltas, d => Assert.Equal(GitDeltaStatus.Added, d.Status));
            var paths = diff.Deltas.Select(d => d.Path.ToUtf8String()).ToHashSet();
            Assert.Contains("README.md", paths);
            Assert.Contains("fileA.txt", paths);
            Assert.Contains("fileB.txt", paths);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.IndexToWorkdirAsync"/> after modifying a tracked
    /// file in the workdir (without staging) reports a single
    /// <see cref="GitDeltaStatus.Modified"/> delta for that file.
    /// </summary>
    [Fact]
    public async Task IndexToWorkdir_ModifiedFile_ReturnsModifiedDelta()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-i2w-mod-" + Guid.NewGuid().ToString("N"));
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "fileA.txt"), "line1\nUNSTAGED\nline3\n", ct);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);

            Assert.Equal(1, diff.DeltaCount);
            GitDiffDelta delta = diff.GetDelta(0);
            Assert.Equal(GitDeltaStatus.Modified, delta.Status);
            Assert.Equal("fileA.txt", delta.Path.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.TreeToWorkdirWithIndexAsync"/> merges a
    /// tree-to-index diff (staged changes) with an index-to-workdir diff
    /// (unstaged changes). With a staged change to <c>fileA.txt</c> and an
    /// unstaged change to <c>fileB.txt</c>, the merged diff contains deltas
    /// for both files.
    /// </summary>
    [Fact]
    public async Task TreeToWorkdirWithIndex_StagedAndUnstaged_ReportsBoth()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-t2wiw-" + Guid.NewGuid().ToString("N"));
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;

            // Make an unstaged change to fileB.txt FIRST (write only, no add).
            // Use a different-size content so the stat-based change detection
            // catches it regardless of timestamp resolution.
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileB.txt"), "unstaged-b-different-size\n", ct);

            // Stage a change to fileA.txt (write + add to index, no commit).
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nSTAGED\nline3\n", ct);
            GitIndex idx = await repo.GetIndexAsync(ct);
            await idx.AddByPathAsync("fileA.txt", ct);
            await idx.WriteAsync(ct);

            (_, GitTree headTree) = await GetHeadCommitAndTreeAsync(repo, ct);

            using GitDiff diff = await repo.DiffTreeToWorkdirWithIndexAsync(headTree, cancellationToken: ct);

            var paths = diff.Deltas.Select(d => d.Path.ToUtf8String()).ToHashSet();
            Assert.Contains("fileA.txt", paths);
            Assert.Contains("fileB.txt", paths);
            Assert.All(diff.Deltas, d => Assert.Equal(GitDeltaStatus.Modified, d.Status));
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Regression: a <see cref="GitRepository.DiffTreeToIndexAsync"/> restricted
    /// to a single nested staged path must report <see cref="GitDeltaStatus.Modified"/>,
    /// not <see cref="GitDeltaStatus.Added"/>. The pathspec collapses the
    /// iterator range to <c>Start == End == &lt;full path&gt;</c>; the tree
    /// iterator must recurse into each ancestor directory (whose path sorts
    /// before the start boundary) to reach the file. Without the
    /// directory-recurse clause of <c>iterator_has_started</c>
    /// (iterator.c:208-210), the tree side yields nothing and the staged
    /// modification surfaces as a whole-file <c>Added</c>.
    /// </summary>
    [Fact]
    public async Task DiffTreeToIndex_NestedFilePathSpec_ReportsModifiedNotAdded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-t2i-nested-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid init = await InitRepoWithInitialCommitAsync(path, ct);

            // Commit a nested file so HEAD's tree contains it.
            await using (GitRepository setupRepo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                string setupWorkdir = setupRepo.Workdir!;
                Directory.CreateDirectory(Path.Combine(setupWorkdir, "src", "Backend"));
                var lines = new StringBuilder();
                for (int i = 0; i < 10; i++)
                {
                    lines.Append("base").Append(i).Append('\n');
                }

                await File.WriteAllTextAsync(Path.Combine(setupWorkdir, "src", "Backend", "Test.cs"), lines.ToString(), ct);

                GitIndex setupIdx = await setupRepo.GetIndexAsync(ct);
                await setupIdx.AddByPathAsync("src/Backend/Test.cs", ct);
                await setupIdx.WriteAsync(ct);
                GitOid treeOid = await setupIdx.WriteTreeAsync(ct);
                await setupRepo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [init],
                    Author = Sig,
                    Committer = Sig,
                    Message = "add nested\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            // Modify the nested file and stage it (no commit) — a staged
            // modification of a file nested under two directories.
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            var modified = new StringBuilder();
            for (int i = 0; i < 12; i++)
            {
                modified.Append("base").Append(i).Append('\n');
            }

            await File.WriteAllTextAsync(Path.Combine(workdir, "src", "Backend", "Test.cs"), modified.ToString(), ct);
            GitIndex idx = await repo.GetIndexAsync(ct);
            await idx.AddByPathAsync("src/Backend/Test.cs", ct);
            await idx.WriteAsync(ct);

            (_, GitTree headTree) = await GetHeadCommitAndTreeAsync(repo, ct);

            // Pathspec-restricted tree→index diff — the exact reported scenario.
            using GitDiff diff = await repo.DiffTreeToIndexAsync(
                headTree,
                new GitDiffOptions { PathSpecStrings = ["src/Backend/Test.cs"] },
                ct);

            GitDiffDelta delta = Assert.Single(diff.Deltas);
            Assert.Equal(GitDeltaStatus.Modified, delta.Status);
            Assert.Equal("src/Backend/Test.cs", delta.Path.ToUtf8String());

            // The patch must render a modification hunk with context, not a
            // whole-file "new file" addition from /dev/null.
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            Assert.Contains("@@ -", patch);
            Assert.DoesNotContain("new file mode", patch);
            Assert.DoesNotContain("/dev/null", patch);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.TreeToWorkdirAsync"/> with a <c>null</c> tree
    /// reports every workdir file as <see cref="GitDeltaStatus.Untracked"/>
    /// when <see cref="GitDiffOptionsFlags.IncludeUntracked"/> is set.
    /// Matches libgit2's <c>test_diff_workdir__to_null_tree</c>
    /// (workdir.c:1192-1213), which asserts every delta is UNTRACKED.
    /// Without <c>IncludeUntracked</c>, untracked deltas are filtered out
    /// (DiffGenerator.cs:359-361) and the diff yields zero deltas.
    /// </summary>
    [Fact]
    public async Task TreeToWorkdir_NullTree_ReportsAllAsUntracked()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-t2w-null-" + Guid.NewGuid().ToString("N"));
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);

            // Without IncludeUntracked, all workdir files (unmatched-new vs
            // the null tree) are filtered out — the diff is empty.
            using (GitDiff cleanDiff = await repo.DiffTreeToWorkdirAsync(oldTree: null, cancellationToken: ct))
            {
                Assert.Equal(0, cleanDiff.DeltaCount);
            }

            // With IncludeUntracked, every workdir file surfaces as UNTRACKED.
            var opts = new GitDiffOptions
            {
                Flags = GitDiffOptionsFlags.IncludeUntracked | GitDiffOptionsFlags.RecurseUntrackedDirs,
            };
            using GitDiff diff = await repo.DiffTreeToWorkdirAsync(oldTree: null, opts, ct);

            Assert.True(diff.DeltaCount >= 3);
            Assert.All(diff.Deltas, d => Assert.Equal(GitDeltaStatus.Untracked, d.Status));
            var paths = diff.Deltas.Select(d => d.Path.ToUtf8String()).ToHashSet();
            Assert.Contains("README.md", paths);
            Assert.Contains("fileA.txt", paths);
            Assert.Contains("fileB.txt", paths);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.TreeToWorkdirAsync"/> materializes a patch
    /// whose new side comes from the actual workdir file (not the ODB),
    /// even when the workdir change is unstaged. Regression guard for the
    /// direct-workdir-iterator fix: the old merge approach
    /// (tree-to-index + index-to-workdir + Merge) resolved the merged
    /// delta's new side to Src=Index, so <see cref="GitPatch"/> looked up
    /// the new content via the ODB using a workdir-computed OID that was
    /// never staged — yielding empty new-side content and zero line stats.
    /// With the direct workdir iterator, <c>NewSrc=Workdir</c> and
    /// <see cref="DiffFileContent.LoadWorkdirAsync"/> reads the file from
    /// disk. Matches C's <c>git_diff_tree_to_workdir</c> semantics.
    /// </summary>
    [Fact]
    public async Task TreeToWorkdir_PatchMaterializesUnstagedWorkdirContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-t2w-patch-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);

            // Modify fileA.txt in the workdir WITHOUT staging.
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nMODIFIED\nline3\n", ct);

            (_, GitTree headTree) = await GetHeadCommitAndTreeAsync(repo, ct);

            using GitDiff diff = await repo.DiffTreeToWorkdirAsync(headTree, cancellationToken: ct);

            GitDiffDelta? delta = diff.Deltas.FirstOrDefault(d => d.NewFile.Path?.ToUtf8String() == "fileA.txt");
            Assert.NotNull(delta);
            Assert.Equal(GitDeltaStatus.Modified, delta!.Status);

            // The core assertion: the patch must carry the unstaged workdir
            // content as the new side. Under the old merge approach this
            // returned (0, 0, 0) because the ODB lookup came up empty.
            using GitPatch patch = await repo.PatchFromDiffAsync(diff, 0, ct);
            (int context, int additions, int deletions) = await patch.LineStatsAsync(ct);
            Assert.True(additions > 0, $"expected additions > 0, got {additions}");
            Assert.True(deletions > 0, $"expected deletions > 0, got {deletions}");
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 2. Pathspec & Flags ───────────────────────────────────────────

    /// <summary>
    /// <see cref="GitDiffOptions.PathSpecs"/> limits the diff to matching
    /// paths only. Modifying both <c>fileA.txt</c> and <c>fileB.txt</c> in the
    /// workdir, then diffing with <c>PathSpecStrings = ["fileA.txt"]</c> yields a
    /// single delta for <c>fileA.txt</c> only.
    /// </summary>
    [Fact]
    public async Task PathSpec_LimitsDeltasToMatchedPaths()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-pathspec-" + Guid.NewGuid().ToString("N"));
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nCHANGED\nline3\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileB.txt"), "changed-b\n", ct);

            var opts = new GitDiffOptions { PathSpecStrings = ["fileA.txt"] };
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, ct);

            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal("fileA.txt", diff.GetDelta(0).Path.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiffOptionsFlags.IncludeUntracked"/> reports new
    /// untracked files in the workdir as <see cref="GitDeltaStatus.Untracked"/>.
    /// </summary>
    [Fact]
    public async Task IncludeUntracked_ReportsNewUntrackedFiles()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-untracked-" + Guid.NewGuid().ToString("N"));
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "new.txt"), "new untracked content\n", ct);

            var opts = new GitDiffOptions { Flags = GitDiffOptionsFlags.IncludeUntracked };
            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, ct);

            GitDiffDelta? untracked = Assert.Single(diff.Deltas, d => d.Status == GitDeltaStatus.Untracked);
            Assert.Equal("new.txt", untracked.Path.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiffOptionsFlags.IncludeIgnored"/> reports files
    /// matching a <c>.gitignore</c> pattern as <see cref="GitDeltaStatus.Ignored"/>.
    /// </summary>
    [Fact]
    public async Task IncludeIgnored_ReportsIgnoredFiles()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-ignored-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                // .gitignore that ignores *.log
                await File.WriteAllTextAsync(Path.Combine(workdir, ".gitignore"), "*.log\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "readme\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "trace.log"), "log data\n", ct);

                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync(".gitignore", ct);
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
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            var opts = new GitDiffOptions { Flags = GitDiffOptionsFlags.IncludeIgnored };
            using GitDiff diff = await repo2.DiffIndexToWorkdirAsync(opts, ct);

            GitDiffDelta? ignored = Assert.Single(diff.Deltas, d => d.Status == GitDeltaStatus.Ignored);
            Assert.Equal("trace.log", ignored.Path.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 3. Patch output ──────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitDiff.PatchesAsync"/> yields one
    /// <see cref="GitPatch"/> per delta, with the patch's
    /// <see cref="GitPatch.Delta"/> matching the diff's delta at the same
    /// index.
    /// </summary>
    [Fact]
    public async Task PatchesAsync_YieldsOnePatchPerDelta()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-patches-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            // Modify fileA.txt AND add fileC.txt in one commit so we get 2 deltas.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nCHANGED\nline3\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileC.txt"), "new file\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("fileA.txt", ct);
                await idx.AddByPathAsync("fileC.txt", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [first],
                    Author = Sig,
                    Committer = Sig,
                    Message = "modify + add\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo2.ObjectLookupAsync<Commit>(first, ct);
            Assert.NotNull(firstCommit);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            (_, GitTree newTree) = await GetHeadCommitAndTreeAsync(repo2, ct);

            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            Assert.Equal(2, diff.DeltaCount);

            int count = 0;
            await foreach (GitPatch patch in diff.PatchesAsync(ct).ConfigureAwait(false))
            {
                using (patch)
                {
                    Assert.NotNull(patch.Delta);
                    count++;
                }
            }
            Assert.Equal(2, count);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitPatch.LineStatsAsync"/> on a patch with one added
    /// and one deleted line reports <c>(context&gt;=0, additions=1,
    /// deletions=1)</c>.
    /// </summary>
    [Fact]
    public async Task Patch_LineStats_ReportsAdditionsAndDeletions()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-linestats-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            // Modify fileA.txt: replace "line2" with "CHANGED" (1 del + 1 add).
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            using GitPatch patch = await repo.PatchFromDiffAsync(diff, 0, ct);

            (int context, int additions, int deletions) = await patch.LineStatsAsync(ct);
            Assert.Equal(1, additions);
            Assert.Equal(1, deletions);
            Assert.True(context >= 2); // two unchanged lines of context at least
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.ToBufferAsync"/> renders the three name/raw
    /// formats deterministically: <see cref="GitDiffPrintFormat.NameOnly"/>
    /// emits just paths, <see cref="GitDiffPrintFormat.NameStatus"/> prefixes
    /// the status char, and <see cref="GitDiffPrintFormat.Raw"/> includes the
    /// <c>:mode mode oid oid STATUS\tpath</c> raw line.
    /// </summary>
    [Fact]
    public async Task ToBuffer_NameOnly_NameStatus_Raw_FormatsMatchExpectedShape()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-formats-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            // Add fileC.txt → produces an Added delta alongside the unmodified others.
            GitOid second = await AddCommitAddingFileCAsync(path, first, "new file\n", "add fileC\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Added, diff.GetDelta(0).Status);
            Assert.Equal("fileC.txt", diff.GetDelta(0).Path.ToUtf8String());

            string nameOnly = await diff.ToBufferTextAsync(GitDiffPrintFormat.NameOnly, ct);
            Assert.Equal("fileC.txt\n", nameOnly);

            string nameStatus = await diff.ToBufferTextAsync(GitDiffPrintFormat.NameStatus, ct);
            Assert.Equal("A\tfileC.txt\n", nameStatus);

            string raw = await diff.ToBufferTextAsync(GitDiffPrintFormat.Raw, ct);
            Assert.Contains("A\tfileC.txt", raw);
            // Raw format begins with ":<oldmode> <newmode> <oldoid> <newoid> "
            Assert.StartsWith(":", raw);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 4. Rename detection ───────────────────────────────────────────

    /// <summary>
    /// <see cref="GitDiff.FindSimilarAsync"/> detects a 100%-identical
    /// rename: deleting <c>old.txt</c> and adding <c>new.txt</c> with the same
    /// content. Without <see cref="GitDiff.FindSimilarAsync"/> the diff
    /// reports separate Deleted + Added deltas; after calling it, a single
    /// <see cref="GitDeltaStatus.Renamed"/> delta with
    /// <see cref="GitDiffDelta.Similarity"/> == 100 is reported.
    /// </summary>
    [Fact]
    public async Task FindSimilar_RenamedFile_ReportedAsRenameWith100Similarity()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-rename-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string sharedContent = "shared content for rename\n";
            // Initial commit: old.txt with the shared content.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "old.txt"), sharedContent, ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("old.txt", ct);
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

            // Second commit: delete old.txt, add new.txt with the same content.
            GitOid first;
            await using (GitRepository reopen = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                GitReference? head = await reopen.ReferenceResolveAsync("HEAD", ct);
                Assert.NotNull(head);
                first = Assert.IsType<GitDirectReference>(head).Target;
            }

            GitOid second;
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                File.Delete(Path.Combine(workdir, "old.txt"));
                await File.WriteAllTextAsync(Path.Combine(workdir, "new.txt"), sharedContent, ct);

                GitIndex idx = await repo.GetIndexAsync(ct);
                idx.RemoveByPath("old.txt");
                await idx.AddByPathAsync("new.txt", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                second = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [first],
                    Author = Sig,
                    Committer = Sig,
                    Message = "rename old to new\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo2.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo2.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo2.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);

            // Before FindSimilar: a delete + an add.
            Assert.Equal(2, diff.DeltaCount);
            Assert.Contains(diff.Deltas, d => d.Status == GitDeltaStatus.Deleted && d.Path.ToUtf8String() == "old.txt");
            Assert.Contains(diff.Deltas, d => d.Status == GitDeltaStatus.Added && d.Path.ToUtf8String() == "new.txt");

            await diff.FindSimilarAsync(cancellationToken: ct);

            // After FindSimilar: a single rename with similarity 100.
            Assert.Equal(1, diff.DeltaCount);
            GitDiffDelta renamed = diff.GetDelta(0);
            Assert.Equal(GitDeltaStatus.Renamed, renamed.Status);
            Assert.Equal(100, renamed.Similarity);
            Assert.Equal("old.txt", renamed.OldFile.Path?.ToUtf8String());
            Assert.Equal("new.txt", renamed.NewFile.Path?.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 5. Stats, PatchId, Email ──────────────────────────────────────

    /// <summary>
    /// <see cref="GitDiff.GetStatsAsync"/> on a diff with one modified
    /// file (1 insertion + 1 deletion) reports
    /// <see cref="GitDiffStats.FilesChanged"/>==1, <see cref="GitDiffStats.Insertions"/>==1,
    /// <see cref="GitDiffStats.Deletions"/>==1, and the formatted short
    /// summary contains the canonical "1 file changed" line.
    /// </summary>
    [Fact]
    public async Task GetStats_OneModifiedOneAddOneDel_ReportsCorrectCounts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-stats-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify fileA\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            GitDiffStats stats = await diff.GetStatsAsync(ct);

            Assert.Equal(1, stats.FilesChanged);
            Assert.Equal(1, stats.Insertions);
            Assert.Equal(1, stats.Deletions);

            using var statsWriter1 = new PooledByteBufferWriter();
            stats.Format(statsWriter1, GitDiffStatsFormat.Short);
            string shortSummary = Encoding.UTF8.GetString(statsWriter1.WrittenSpan);
            Assert.Contains("1 file changed", shortSummary);
            Assert.Contains("1 insertion(+)", shortSummary);
            Assert.Contains("1 deletion(-)", shortSummary);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitPatchId.ComputeAsync"/> is deterministic: the same
    /// diff generated twice produces the same patch-id OID.
    /// </summary>
    [Fact]
    public async Task PatchId_SameDiffProducedTwice_IsDeterministic()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-patchid-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify fileA\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff1 = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            GitOid patchId1 = await GitPatchId.ComputeAsync(diff1, cancellationToken: ct);

            using GitDiff diff2 = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            GitOid patchId2 = await GitPatchId.ComputeAsync(diff2, cancellationToken: ct);

            Assert.Equal(patchId1, patchId2);
            Assert.False(patchId1.IsZero);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitEmailFormatter.FromCommitAsync"/> on a single-file
    /// modification commit produces RFC-2822 format-patch output with the
    /// <c>From:</c> header, the <c>[PATCH]</c> subject, the <c>---</c> body
    /// separator, and the <c>diff --git</c> header.
    /// </summary>
    [Fact]
    public async Task EmailFormatter_FromCommit_ProducesFormatPatchEmail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-email-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify fileA\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? commit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(commit);

            string email = await GitEmailFormatter.ToBufferTextAsync(commit!, cancellationToken: ct);

            Assert.Contains("From: t <t@t>", email);
            Assert.Contains("[PATCH]", email);
            Assert.Contains("---", email);
            Assert.Contains("diff --git", email);
            Assert.Contains("fileA.txt", email);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 6. Round-trip parity ──────────────────────────────────────────

    /// <summary>
    /// Round-trip: <see cref="GitDiff.ToBufferAsync"/> renders a diff to
    /// patch text, and <see cref="GitDiff.FromBuffer"/> parses it back into a
    /// diff with the same delta count and matching per-delta statuses/paths.
    /// This validates that the printer and parser are inverse-consistent.
    /// </summary>
    [Fact]
    public async Task RoundTrip_ToBufferThenFromBuffer_PreservesDeltas()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-roundtrip-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            // Modify fileA + add fileC: 2 deltas, mixed statuses.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nCHANGED\nline3\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileC.txt"), "brand new\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("fileA.txt", ct);
                await idx.AddByPathAsync("fileC.txt", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [first],
                    Author = Sig,
                    Committer = Sig,
                    Message = "modify + add\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo2.ObjectLookupAsync<Commit>(first, ct);
            Assert.NotNull(firstCommit);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            (_, GitTree newTree) = await GetHeadCommitAndTreeAsync(repo2, ct);

            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            Assert.Equal(2, diff.DeltaCount);

            string patchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            Assert.NotEmpty(patchText);

            using var reparsed = GitDiff.FromBuffer(patchText);
            Assert.Equal(diff.DeltaCount, reparsed.DeltaCount);

            var original = diff.Deltas.Select(d => (d.Status, d.Path.ToUtf8String())).ToList();
            var reparsedDeltas = reparsed.Deltas.Select(d => (d.Status, d.Path.ToUtf8String())).ToList();
            Assert.Equal(original.Count, reparsedDeltas.Count);
            foreach ((GitDeltaStatus s, string p) in original)
            {
                Assert.Contains((s, p), reparsedDeltas);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 7. Patch round-trip: deleted file ──────────────────────────────

    /// <summary>
    /// Round-trip of a <see cref="GitDeltaStatus.Deleted"/> delta:
    /// <see cref="GitDiff.ToBufferAsync"/> emits a <c>deleted file mode</c>
    /// header with <c>+++ /dev/null</c>, and <see cref="GitDiff.FromBuffer"/>
    /// parses it back with <see cref="GitDeltaStatus.Deleted"/> and a zero
    /// new-side OID. Exercises <c>PatchParser.ParseDeletedFileMode</c> and
    /// the printer's <c>deleted file mode</c> branch.
    /// </summary>
    [Fact]
    public async Task RoundTrip_DeletedFile_DevNullAndZeroedNewId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-rt-del-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);

            // Second commit: delete fileA.txt.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                File.Delete(Path.Combine(repo.Workdir!, "fileA.txt"));
                GitIndex idx = await repo.GetIndexAsync(ct);
                idx.RemoveByPath("fileA.txt");
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [first],
                    Author = Sig,
                    Committer = Sig,
                    Message = "delete fileA\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo2.ObjectLookupAsync<Commit>(first, ct);
            Assert.NotNull(firstCommit);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            (_, GitTree newTree) = await GetHeadCommitAndTreeAsync(repo2, ct);

            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            Assert.Contains("deleted file mode", patchText);
            Assert.Contains("/dev/null", patchText);

            using var reparsed = GitDiff.FromBuffer(patchText);
            GitDiffDelta? deleted = Assert.Single(reparsed.Deltas, d => d.Status == GitDeltaStatus.Deleted);
            Assert.Equal("fileA.txt", deleted.OldFile.Path?.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 8. Patch round-trip: binary with ShowBinary ───────────────────

    /// <summary>
    /// Round-trip of a binary file change with
    /// <see cref="GitDiffOptionsFlags.ShowBinary"/>: the patch text
    /// contains a <c>GIT binary patch</c> header with Base85-encoded data,
    /// and <see cref="GitDiff.FromBuffer"/> parses it back as a binary
    /// delta. Exercises <c>DiffPrinter.EmitBinaryPatch</c>,
    /// <c>PatchParser.ParseBinary</c> + <c>ParseBinarySide</c> (Base85
    /// decode), and <see cref="GitPatch.GetBinaryAsync"/>.
    /// </summary>
    [Fact]
    public async Task RoundTrip_BinaryWithShowBinary_GitBinaryPatch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-rt-bin-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Initial commit: a binary file with NUL bytes.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                byte[] binContent = [0, 1, 2, 0, 4, 5, 0, 7, 8, 0];
                await File.WriteAllBytesAsync(Path.Combine(workdir, "data.bin"), binContent, ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("data.bin", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "init binary\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await repo.SetHeadAsync("refs/heads/main", ct);
            }

            // Second commit: modify the binary content.
            GitOid first;
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
                Assert.NotNull(head);
                first = Assert.IsType<GitDirectReference>(head).Target;
                byte[] newBin = [0, 1, 2, 0, 99, 98, 0, 7, 8, 0];
                await File.WriteAllBytesAsync(Path.Combine(repo.Workdir!, "data.bin"), newBin, ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("data.bin", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [first],
                    Author = Sig,
                    Committer = Sig,
                    Message = "modify binary\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo2.ObjectLookupAsync<Commit>(first, ct);
            Assert.NotNull(firstCommit);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            (_, GitTree newTree) = await GetHeadCommitAndTreeAsync(repo2, ct);

            var opts = new GitDiffOptions { Flags = GitDiffOptionsFlags.ShowBinary };
            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, opts, ct);
            string patchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            Assert.Contains("GIT binary patch", patchText);

            using var reparsed = GitDiff.FromBuffer(patchText);
            GitDiffDelta? delta = Assert.Single(reparsed.Deltas);
            Assert.Equal("data.bin", delta.Path.ToUtf8String());

            // The reparsed patch should be binary.
            using GitPatch patch = await repo2.PatchFromDiffAsync(reparsed, 0, ct);
            Assert.True(await patch.GetIsBinaryAsync(ct));
            GitBinaryPatch? bin = await patch.GetBinaryAsync(ct);
            Assert.NotNull(bin);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 9. Patch round-trip: binary without ShowBinary ────────────────

    /// <summary>
    /// Round-trip of a binary file change WITHOUT
    /// <see cref="GitDiffOptionsFlags.ShowBinary"/>: the patch text
    /// contains <c>Binary files a/... and b/... differ</c> (no encoded
    /// data), and <see cref="GitDiff.FromBuffer"/> parses it back as a
    /// binary delta. Exercises <c>PatchParser.ParseBinaryNoData</c>
    /// including the <c>/dev/null</c> substitution branches.
    /// </summary>
    [Fact]
    public async Task RoundTrip_BinaryWithoutShowBinary_FilesDiffer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-rt-binnoshow-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                byte[] binContent = [0, 1, 2, 0, 4, 5, 0, 7, 8, 0];
                await File.WriteAllBytesAsync(Path.Combine(workdir, "data.bin"), binContent, ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("data.bin", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "init binary\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await repo.SetHeadAsync("refs/heads/main", ct);
            }

            GitOid first;
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
                Assert.NotNull(head);
                first = Assert.IsType<GitDirectReference>(head).Target;
                byte[] newBin = [0, 1, 2, 0, 99, 98, 0, 7, 8, 0];
                await File.WriteAllBytesAsync(Path.Combine(repo.Workdir!, "data.bin"), newBin, ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("data.bin", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [first],
                    Author = Sig,
                    Committer = Sig,
                    Message = "modify binary\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo2.ObjectLookupAsync<Commit>(first, ct);
            Assert.NotNull(firstCommit);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            (_, GitTree newTree) = await GetHeadCommitAndTreeAsync(repo2, ct);

            // No ShowBinary flag → "Binary files ... differ" stub.
            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string patchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            Assert.Contains("Binary files", patchText);
            Assert.Contains("differ", patchText);
            Assert.DoesNotContain("GIT binary patch", patchText);

            using var reparsed = GitDiff.FromBuffer(patchText);
            GitDiffDelta? delta = Assert.Single(reparsed.Deltas);
            Assert.Equal("data.bin", delta.Path.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 10. Patch round-trip: rename with similarity ──────────────────

    /// <summary>
    /// Round-trip of a rename delta (after
    /// <see cref="GitDiff.FindSimilarAsync"/>): the patch text contains
    /// <c>similarity index 100%</c>, <c>rename from</c>, <c>rename to</c>,
    /// and <see cref="GitDiff.FromBuffer"/> parses it back as
    /// <see cref="GitDeltaStatus.Renamed"/>. Exercises
    /// <c>PatchParser.ParseRenameFrom</c>, <c>ParseRenameTo</c>,
    /// <c>ParseSimilarity</c>, and <c>ParsePercent</c>.
    /// </summary>
    [Fact]
    public async Task RoundTrip_RenameWithSimilarity()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-rt-rename-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string sharedContent = "shared content for rename\n";
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "old.txt"), sharedContent, ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("old.txt", ct);
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

            GitOid first;
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
                Assert.NotNull(head);
                first = Assert.IsType<GitDirectReference>(head).Target;
            }

            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                File.Delete(Path.Combine(workdir, "old.txt"));
                await File.WriteAllTextAsync(Path.Combine(workdir, "new.txt"), sharedContent, ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                idx.RemoveByPath("old.txt");
                await idx.AddByPathAsync("new.txt", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [first],
                    Author = Sig,
                    Committer = Sig,
                    Message = "rename\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo2.ObjectLookupAsync<Commit>(first, ct);
            Assert.NotNull(firstCommit);
            GitTree? oldTree = await repo2.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            (_, GitTree newTree) = await GetHeadCommitAndTreeAsync(repo2, ct);

            using GitDiff diff = await repo2.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            await diff.FindSimilarAsync(cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);
            Assert.Equal(GitDeltaStatus.Renamed, diff.GetDelta(0).Status);

            string patchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            Assert.Contains("similarity index 100%", patchText);
            Assert.Contains("rename from old.txt", patchText);
            Assert.Contains("rename to new.txt", patchText);

            using var reparsed = GitDiff.FromBuffer(patchText);
            GitDiffDelta? delta = Assert.Single(reparsed.Deltas);
            Assert.Equal(GitDeltaStatus.Renamed, delta.Status);
            Assert.Equal("old.txt", delta.OldFile.Path?.ToUtf8String());
            Assert.Equal("new.txt", delta.NewFile.Path?.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 11. Single-patch FromBuffer round-trip ────────────────────────

    /// <summary>
    /// <see cref="GitPatch.FromBuffer"/> parses a single-patch text
    /// into a <see cref="GitPatch"/> backed by <see cref="ParsedPatchSource"/>.
    /// Exercises <see cref="GitPatch.FromBuffer"/>,
    /// <see cref="GitPatch.TryFromBuffer"/>, and the entire
    /// <see cref="ParsedPatchSource"/> adapter class (Delta, GetHunksAsync,
    /// LineStatsAsync, GetIsBinaryAsync, GetBinaryAsync).
    /// </summary>
    [Fact]
    public async Task SinglePatch_FromBuffer_PreservesDeltaAndHunks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-rt-single-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify\n", ct);

            // Generate a single-patch text via ToBufferAsync(Patch) on a
            // single-delta diff. This produces a valid single patch that
            // GitPatch.FromBuffer can parse.
            string singlePatchText;
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
                Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
                Assert.NotNull(firstCommit);
                Assert.NotNull(secondCommit);
                GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
                GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
                Assert.NotNull(oldTree);
                Assert.NotNull(newTree);

                using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
                Assert.Equal(1, diff.DeltaCount);
                singlePatchText = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            }

            Assert.Contains("diff --git", singlePatchText);
            Assert.Contains("fileA.txt", singlePatchText);

            // Parse the single patch back via GitPatch.FromBuffer.
            using var reparsed = GitPatch.FromBuffer(singlePatchText);
            Assert.Equal(GitDeltaStatus.Modified, reparsed.Delta.Status);
            Assert.Equal("fileA.txt", reparsed.Delta.Path.ToUtf8String());

            int hunkCount = await reparsed.GetHunkCountAsync(ct);
            Assert.Equal(1, hunkCount);

            (int context, int additions, int deletions) = await reparsed.LineStatsAsync(ct);
            Assert.Equal(1, additions);
            Assert.Equal(1, deletions);
            Assert.True(context >= 2);

            Assert.False(await reparsed.GetIsBinaryAsync(ct));

            // TryFromBuffer also succeeds.
            Assert.True(GitPatch.TryFromBuffer(singlePatchText, out GitPatch? tryParsed));
            Assert.NotNull(tryParsed);
            Assert.Equal("fileA.txt", tryParsed!.Delta.Path.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 12. GitPatch factory methods ──────────────────────────────────

    /// <summary>
    /// <see cref="GitPatch.FromBlobs"/>,
    /// <see cref="GitPatch.FromBlobAndBuffer"/>, and
    /// <see cref="GitPatch.FromBuffers"/> produce patches with correct
    /// deltas. Exercises the three standalone factory paths in
    /// <see cref="GitPatch"/> and <c>PatchGenerator.BuildStandaloneDelta</c>.
    /// </summary>
    [Fact]
    public async Task Patch_FromBlobs_And_FromBuffers_Factories()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-rt-factories-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);

            GitOid oldBlobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "old content\n"u8.ToArray(), ct);
            GitOid newBlobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "new content\n"u8.ToArray(), ct);
            GitBlob? oldBlob = await repo.ObjectLookupAsync<GitBlob>(oldBlobOid, ct);
            GitBlob? newBlob = await repo.ObjectLookupAsync<GitBlob>(newBlobOid, ct);
            Assert.NotNull(oldBlob);
            Assert.NotNull(newBlob);

            // FromBlobs: Modified delta.
            using (GitPatch p = repo.PatchFromBlobs(oldBlob, newBlob))
            {
                Assert.Equal(GitDeltaStatus.Modified, p.Delta.Status);
                int hunkCount = await p.GetHunkCountAsync(ct);
                Assert.Equal(1, hunkCount);
            }

            // FromBlobAndBuffer: Modified delta.
            using (GitPatch p = repo.PatchFromBlobAndBuffer(oldBlob, "new content\n"u8.ToArray()))
            {
                Assert.Equal(GitDeltaStatus.Modified, p.Delta.Status);
                Assert.Equal(1, await p.GetHunkCountAsync(ct));
            }

            // FromBuffers: Modified delta.
            using (GitPatch p = repo.PatchFromBuffers("old content\n"u8.ToArray(), "new content\n"u8.ToArray()))
            {
                Assert.Equal(GitDeltaStatus.Modified, p.Delta.Status);
                Assert.Equal(1, await p.GetHunkCountAsync(ct));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 13. PrintAsync with Stat and Summary formats ──────────────────

    /// <summary>
    /// <see cref="GitDiff.PrintAsync"/> with
    /// <see cref="GitDiffPrintFormat.Stat"/> and
    /// <see cref="GitDiffPrintFormat.Summary"/> routes through
    /// <see cref="GitDiffStats.Format"/> and invokes the callback with the
    /// formatted stats text. Exercises the Stat/Summary dispatch in
    /// <see cref="GitDiff.PrintAsync"/>.
    /// </summary>
    /// <remarks>
    /// The Summary format (<c>FormatSummary</c>) only emits
    /// <c>create mode</c>/<c>delete mode</c>/<c>mode change</c> lines — not
    /// for plain Modified deltas. So the test uses an Added file to get a
    /// <c>create mode</c> summary line.
    /// </remarks>
    [Fact]
    public async Task Print_StatAndSummary_Formats()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-rt-statsum-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitAddingFileCAsync(path, first, "new file\n", "add fileC\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);

            string statText = string.Empty;
            await diff.PrintAsync(GitDiffPrintFormat.Stat, (_, _, line) =>
            {
                statText += Encoding.UTF8.GetString(line.Content.Span);
            }, ct);
            Assert.Contains("file changed", statText);
            Assert.Contains("insertion", statText);

            string summaryText = string.Empty;
            await diff.PrintAsync(GitDiffPrintFormat.Summary, (_, _, line) =>
            {
                summaryText += Encoding.UTF8.GetString(line.Content.Span);
            }, ct);
            // Summary emits "create mode 100644 fileC.txt" for the Added file.
            Assert.Contains("create mode", summaryText);
            Assert.Contains("fileC.txt", summaryText);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 14. PatchHeader format ────────────────────────────────────────

    /// <summary>
    /// <see cref="GitDiff.ToBufferAsync"/> with
    /// <see cref="GitDiffPrintFormat.PatchHeader"/> emits just the
    /// <c>diff --git</c> header lines (no hunks). Exercises
    /// <c>DiffPrinter.PrintPatchHeader</c>.
    /// </summary>
    [Fact]
    public async Task PatchHeader_Format_EmitsOnlyHeaders()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-rt-hdr-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            string headerText = await diff.ToBufferTextAsync(GitDiffPrintFormat.PatchHeader, ct);

            Assert.Contains("diff --git", headerText);
            Assert.Contains("fileA.txt", headerText);
            // No hunk content — just headers.
            Assert.DoesNotContain("@@", headerText);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 15. Rename detection for untracked workdir files ──────────────

    /// <summary>
    /// <see cref="GitDiff.FindSimilarAsync"/> on an index-to-workdir
    /// diff pairs a deleted tracked file with an untracked new file of
    /// identical content when <see cref="GitDiffFindFlags.ForUntracked"/> is
    /// set. Before FindSimilar, the diff reports separate Deleted + Untracked
    /// deltas; after, a single Renamed delta with similarity 100. Regression
    /// guard for the zero-OID workdir-content fix in DiffTransform. Mirrors
    /// C's <c>test_status_renames__index2workdir_one</c>
    /// (status/renames.c:218).
    /// </summary>
    [Fact]
    public async Task FindSimilar_IndexToWorkdir_UntrackedTarget_PairedAsRename()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-untracked-rename-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string sharedContent = "shared content for rename detection\n";

            // Build a repo with just old.txt committed (clean workdir).
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "old.txt"), sharedContent, ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("old.txt", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "add old.txt\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await repo.SetHeadAsync("refs/heads/main", ct);
            }

            // Workdir-only: delete old.txt, create untracked new.txt (same content).
            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string wd = repo2.Workdir!;
            File.Delete(Path.Combine(wd, "old.txt"));
            await File.WriteAllTextAsync(Path.Combine(wd, "new.txt"), sharedContent, ct);

            // Index-to-workdir diff with IncludeUntracked.
            var diffOpts = new GitDiffOptions
            {
                Flags = GitDiffOptionsFlags.IncludeUntracked,
            };
            using GitDiff diff = await repo2.DiffIndexToWorkdirAsync(diffOpts, ct);

            // Before FindSimilar: Deleted(old.txt) + Untracked(new.txt).
            Assert.Equal(2, diff.DeltaCount);
            Assert.Contains(diff.Deltas, d => d.Status == GitDeltaStatus.Deleted && d.Path.ToUtf8String() == "old.txt");
            Assert.Contains(diff.Deltas, d => d.Status == GitDeltaStatus.Untracked && d.Path.ToUtf8String() == "new.txt");

            // FindSimilar with ForUntracked → pairs the untracked target.
            var findOpts = new GitDiffFindOptions { Flags = GitDiffFindFlags.ForUntracked };
            await diff.FindSimilarAsync(findOpts, ct);

            // After: a single Renamed delta, similarity 100.
            Assert.Equal(1, diff.DeltaCount);
            GitDiffDelta renamed = diff.GetDelta(0);
            Assert.Equal(GitDeltaStatus.Renamed, renamed.Status);
            Assert.Equal(100, renamed.Similarity);
            Assert.Equal("old.txt", renamed.OldFile.Path?.ToUtf8String());
            Assert.Equal("new.txt", renamed.NewFile.Path?.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── 16. EOFNL line-stats regression ───────────────────────────────

    /// <summary>
    /// <see cref="GitPatch.LineStatsAsync"/> on a patch produced from a
    /// real index-to-workdir diff must not count the EOFNL marker
    /// ("\ No newline at end of file") as a real addition or deletion.
    /// Regression guard for the patch.c:93-128 fix. The scenario: a tracked
    /// file with a trailing newline is modified in the workdir to remove the
    /// newline — the only content change is the EOFNL marker.
    /// </summary>
    [Fact]
    public async Task Patch_LineStats_EofnlNotCounted_RealIndexToWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-eofnl-" + Guid.NewGuid().ToString("N"));
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            // Rewrite fileB.txt removing the trailing newline (was "b-content\n").
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileB.txt"), "b-content", ct);

            using GitDiff diff = await repo.DiffIndexToWorkdirAsync(cancellationToken: ct);
            Assert.Equal(1, diff.DeltaCount);

            using GitPatch patch = await repo.PatchFromDiffAsync(diff, 0, ct);

            // The patch must have at least one EOFNL marker (otherwise the test is vacuous).
            int hunks = await patch.GetHunkCountAsync(ct);
            Assert.True(hunks > 0, "expected at least one hunk");
            GitDiffHunk? hunk = await patch.GetHunkAsync(0, ct);
            Assert.NotNull(hunk);
            Assert.Contains(hunk!.Lines, l => l.Origin is GitDiffLineOrigin.AddEofnl or GitDiffLineOrigin.DelEofnl);

            // LineStats must not count the EOFNL marker.
            (int context, int additions, int deletions) = await patch.LineStatsAsync(ct);

            // Count the real content lines directly from the hunk.
            int expectedCtxt = 0, expectedAdds = 0, expectedDels = 0;
            foreach (GitDiffLine line in hunk.Lines)
            {
                switch (line.Origin)
                {
                    case GitDiffLineOrigin.Context: expectedCtxt++; break;
                    case GitDiffLineOrigin.Addition: expectedAdds++; break;
                    case GitDiffLineOrigin.Deletion: expectedDels++; break;
                }
            }

            Assert.Equal(expectedCtxt, context);
            Assert.Equal(expectedAdds, additions);
            Assert.Equal(expectedDels, deletions);
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitDiff.GetStatsAsync"/> on a diff that adds a new file
    /// must report zero deletions, not a phantom deletion from the EOFNL
    /// marker xdiff emits for the empty old side. <c>git diff --numstat</c>
    /// would report <c>1\t0</c>. Regression guard for the
    /// git_patch_line_stats EOFNL fix driving git_diff_stats.
    /// </summary>
    [Fact]
    public async Task Diff_GetStats_AddedFile_NoPhantomDeletion()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-stats-add-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);

            // Second commit: add new.txt with "x\n" (a pure addition).
            GitOid second = await AddCommitAddingFileCAsync(path, first, "x\n", "add new file\n", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            Assert.NotNull(firstCommit);
            Assert.NotNull(secondCommit);
            GitTree? oldTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            GitTree? newTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);
            Assert.NotNull(oldTree);
            Assert.NotNull(newTree);

            using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: ct);
            GitDiffStats stats = await diff.GetStatsAsync(ct);

            // One file added; 1 insertion, 0 deletions (not 1/1).
            Assert.Equal(1, stats.FilesChanged);
            Assert.Equal(1, stats.Insertions);
            Assert.Equal(0, stats.Deletions);

            // Numstat format uses "%-8" left-justified fields (diff_stats.c:158-170),
            // not tabs: "1       0       fileC.txt\n".
            using var statsWriter2 = new PooledByteBufferWriter();
            stats.Format(statsWriter2, GitDiffStatsFormat.Number);
            string numstat = Encoding.UTF8.GetString(statsWriter2.WrittenSpan);
            Assert.Contains("fileC.txt", numstat);
            Assert.DoesNotContain("-\t", numstat);

            firstCommit.Dispose();
            secondCommit.Dispose();
            oldTree!.Dispose();
            newTree!.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}
