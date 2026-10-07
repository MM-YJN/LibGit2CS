using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.IntegrationTests.Checkout;

/// <summary>
/// Integration tests for the checkout engine
/// (<see cref="CheckoutContext"/>/<see cref="WorkdirWriter"/>/
/// <see cref="MergeFileFromIndex"/>/<see cref="GitCheckoutOptions"/>/
/// <see cref="GitCheckoutStrategy"/>/<see cref="GitCheckoutNotifyFlags"/>)
/// exercised end-to-end against locally-initialized non-bare repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The <see cref="LibGit2CS.Checkout"/> namespace
/// had <c>22-27%</c> integration coverage — the unit-test project covers
/// checkout against locally-initialized repos for the basic
/// <c>Safe</c>/<c>Force</c> strategies, but the broader surface (branch
/// switching with directory pruning, <c>RemoveUntracked</c>/
/// <c>RemoveIgnored</c>/<c>UpdateOnly</c>/<c>DryRun</c>/<c>DontWriteIndex</c>,
/// pathspec + <c>DisablePathSpecMatch</c>, notify cancellation, perfdata,
/// <c>TargetDirectory</c> from bare and non-bare, the
/// <c>AllowConflicts</c>/<c>UseOurs</c>/<c>UseTheirs</c>/
/// <c>ConflictStyleDiff3</c> conflict family, the <c>Baseline</c>
/// override, and the platform-specific <c>core.filemode</c>/
/// <c>core.symlinks</c>/<c>core.autocrlf</c> paths) was never exercised
/// end-to-end. The Docker-based reset tests cover only the
/// <see cref="GitRepository.ResetAsync"/> hard path and require an SSH
/// clone. These tests close that gap without Docker by building repos
/// locally with <see cref="GitRepository.InitAsync"/> +
/// <see cref="Commit.CreateAsync"/> + <see cref="GitTreeBuilder"/>.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Ports scenarios from
/// <c>tests/libgit2/checkout/head.c</c>, <c>index.c</c>, <c>tree.c</c>, and
/// <c>conflict.c</c>, adapted to build the sandbox from scratch (no fixture
/// repo).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build. Platform-specific tests
/// (filemode/symlinks) early-return on non-POSIX platforms.
/// </para>
/// </remarks>
public sealed class CheckoutIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>
    /// A synchronous <see cref="IProgress{T}"/> implementation that invokes
    /// the callback directly on the calling thread (no threadpool hop).
    /// Used for checkout progress/perfdata callbacks in tests where
    /// <see cref="Progress{T}"/> would race with the assertion (the BCL
    /// <see cref="Progress{T}"/> posts to the captured sync context, which
    /// is null under xUnit, scheduling to the threadpool).
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        internal SynchronousProgress(Action<T> callback) => _callback = callback;
        void IProgress<T>.Report(T value) => _callback(value);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-checkout-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// Writes a file to the workdir and stages it into the index WITHOUT
    /// committing. Returns the blob OID.
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
    /// Initializes a non-bare repo at <paramref name="path"/>, creates an
    /// initial commit on <c>refs/heads/main</c> with <c>README.md</c> and
    /// <c>fileA.txt</c>, and sets HEAD. Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> InitRepoWithInitialCommitAsync(string path, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "readme\n", ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nline2\nline3\n", ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("README.md", ct);
        await idx.AddByPathAsync("fileA.txt", ct);
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
    /// modifies <c>fileA.txt</c> to the given content. Returns the new commit OID.
    /// </summary>
    private static async Task<GitOid> AddCommitModifyingFileAAsync(string repoPath, GitOid parentOid, string newContent, string message, string refName, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), newContent, ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("fileA.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        // C (commit.c:109-117): with update_ref, parent[0] must equal the
        // ref tip — chain onto the CURRENT tip. The explicit parentOid can
        // lag the ref (e.g. a manual commit between calls), which the C
        // tip check would reject with GIT_EMODIFIED.
        GitReference? tip = await repo.ReferenceLookupAsync(refName, ct);
        GitOid[] parents = tip is GitDirectReference direct ? [direct.Target] : [parentOid];

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct);
    }

    /// <summary>
    /// Adds a second commit on top of <paramref name="parentOid"/> that adds
    /// a new file <c>fileB.txt</c> with the given content. Returns the new
    /// commit OID.
    /// </summary>
    private static async Task<GitOid> AddCommitAddingFileBAsync(string repoPath, GitOid parentOid, string content, string message, string refName, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "fileB.txt"), content, ct);

        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("fileB.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parentOid],
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
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

    /// <summary>
    /// Commits a set of (possibly nested) files in one commit on
    /// <paramref name="refName"/>, creating parent directories as needed.
    /// Returns the commit OID. Used by the nested-workdir checkout tests.
    /// </summary>
    private static async Task<GitOid> CommitNestedFilesAsync(
        GitRepository repo, string workdir,
        (string Path, string Content)[] files,
        string message, string refName, GitOid[] parents, CancellationToken ct)
    {
        GitIndex idx = await repo.GetIndexAsync(ct);
        foreach ((string relPath, string content) in files)
        {
            string full = Path.Combine(workdir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, ct);
            await idx.AddByPathAsync(relPath, ct);
        }

        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = refName,
        }, ct);
    }

    /// <summary>
    /// Builds a 3-stage conflict in the repo's index for the given path by
    /// writing three blobs to the ODB and calling
    /// <see cref="GitIndex.ConflictAdd"/>. Returns the three blob OIDs.
    /// </summary>
    private static async Task<(GitOid Ancestor, GitOid Ours, GitOid Theirs)> BuildThreeStageConflictAsync(
        GitRepository repo, string path, string ancestorContent, string oursContent, string theirsContent, CancellationToken ct)
    {
        GitOid ancestorOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(ancestorContent), ct);
        GitOid oursOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(oursContent), ct);
        GitOid theirsOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(theirsContent), ct);

        GitIndex index = await repo.GetIndexAsync(ct);
        var ancestor = new GitIndexEntry(path, ancestorOid, GitFileMode.Regular);
        var ours = new GitIndexEntry(path, oursOid, GitFileMode.Regular);
        var theirs = new GitIndexEntry(path, theirsOid, GitFileMode.Regular);
        index.ConflictAdd(ancestor, ours, theirs);
        await index.WriteAsync(ct);
        return (ancestorOid, oursOid, theirsOid);
    }

    // ── HEAD checkout basics ────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.CheckoutHeadAsync"/> on an unborn repo
    /// (HEAD points at a branch with no commits) throws
    /// <see cref="GitErrorCode.UnbornBranch"/> — matches libgit2's
    /// <c>git_checkout_head</c> which propagates <c>GIT_EUNBORNBRANCH</c>
    /// from <c>git_repository_head</c> via
    /// <c>checkout_lookup_head_tree</c> (checkout.c:1928-1941, 2777-2783).
    /// </summary>
    [Fact]
    public async Task Head_UnbornBranch_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Fresh init: HEAD points at refs/heads/main (or master) with no
            // commits — unborn branch. libgit2 throws GIT_EUNBORNBRANCH.
            GitException ex = await Assert.ThrowsAsync<GitException>(
                async () => await repo.CheckoutHeadAsync(cancellationToken: ct));
            Assert.Equal(GitErrorCode.UnbornBranch, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutHeadAsync"/> with
    /// <see cref="GitCheckoutStrategy.Force"/>|
    /// <see cref="GitCheckoutStrategy.RemoveUntracked"/> removes a staged
    /// file and its containing directory when the file is not in HEAD.
    /// Matches <c>test_checkout_head__with_index_only_tree</c>.
    /// </summary>
    [Fact]
    public async Task Head_ForceRemoveUntracked_RemovesStagedFileAndDir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;

            // Stage a new file in a new directory.
            Directory.CreateDirectory(Path.Combine(workdir, "newdir"));
            await File.WriteAllTextAsync(Path.Combine(workdir, "newdir", "newfile.txt"), "new file\n", ct);
            GitIndex index = await repo.GetIndexAsync(ct);
            await index.AddByPathAsync("newdir/newfile.txt", ct);
            await index.WriteAsync(ct);

            // Verify staged.
            Assert.NotNull(index.EntryByPath("newdir/newfile.txt", 0));

            // Force + RemoveUntracked checkout of HEAD removes the staged file.
            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RemoveUntracked,
            };
            await repo.CheckoutHeadAsync(opts, ct);

            // The staged file and its directory should be gone from the workdir.
            Assert.False(File.Exists(Path.Combine(workdir, "newdir", "newfile.txt")));
            // And no longer in the index.
            GitIndex refreshed = await repo.GetIndexAsync(ct);
            Assert.Null(refreshed.EntryByPath("newdir/newfile.txt", 0));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutHeadAsync"/> with
    /// <see cref="GitCheckoutStrategy.Force"/> removes a tracked file but
    /// preserves an untracked sibling in the same directory. Matches
    /// <c>test_checkout_head__do_not_remove_untracked_file</c>.
    /// </summary>
    [Fact]
    public async Task Head_Force_PreservesUntrackedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;

            // Create a tracked dir with a tracked file and an untracked file.
            Directory.CreateDirectory(Path.Combine(workdir, "tracked"));
            await File.WriteAllTextAsync(Path.Combine(workdir, "tracked", "tracked.txt"), "tracked\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir, "tracked", "untracked.txt"), "untracked\n", ct);
            GitIndex index = await repo.GetIndexAsync(ct);
            await index.AddByPathAsync("tracked/tracked.txt", ct);
            await index.WriteAsync(ct);

            // Force checkout HEAD — tracked file is NOT in HEAD, so removed.
            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo.CheckoutHeadAsync(opts, ct);

            Assert.False(File.Exists(Path.Combine(workdir, "tracked", "tracked.txt")));
            Assert.True(File.Exists(Path.Combine(workdir, "tracked", "untracked.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutHeadAsync"/> with
    /// <see cref="GitCheckoutStrategy.Force"/> preserves untracked files in
    /// nested subdirectories while removing tracked ones. Matches
    /// <c>test_checkout_head__do_not_remove_untracked_file_in_subdir</c>.
    /// </summary>
    [Fact]
    public async Task Head_Force_PreservesUntrackedInSubdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;

            Directory.CreateDirectory(Path.Combine(workdir, "tracked", "subdir"));
            await File.WriteAllTextAsync(Path.Combine(workdir, "tracked", "tracked.txt"), "tracked\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir, "tracked", "subdir", "tracked.txt"), "tracked\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir, "tracked", "subdir", "untracked.txt"), "untracked\n", ct);
            GitIndex index = await repo.GetIndexAsync(ct);
            await index.AddByPathAsync("tracked/tracked.txt", ct);
            await index.AddByPathAsync("tracked/subdir/tracked.txt", ct);
            await index.WriteAsync(ct);

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo.CheckoutHeadAsync(opts, ct);

            Assert.False(File.Exists(Path.Combine(workdir, "tracked", "tracked.txt")));
            Assert.False(File.Exists(Path.Combine(workdir, "tracked", "subdir", "tracked.txt")));
            Assert.True(File.Exists(Path.Combine(workdir, "tracked", "subdir", "untracked.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutHeadAsync"/> with
    /// <see cref="GitCheckoutStrategy.Force"/>|
    /// <see cref="GitCheckoutStrategy.RemoveUntracked"/> and a pathspec
    /// removes only the untracked file matching the pathspec. Matches
    /// <c>test_checkout_head__do_remove_untracked_paths</c>.
    /// </summary>
    [Fact]
    public async Task Head_ForceRemoveUntracked_PathSpec_RemovesOnlyMatched()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;

            Directory.CreateDirectory(Path.Combine(workdir, "tracked"));
            await File.WriteAllTextAsync(Path.Combine(workdir, "tracked", "tracked.txt"), "tracked\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir, "tracked", "untracked.txt"), "untracked\n", ct);
            GitIndex index = await repo.GetIndexAsync(ct);
            await index.AddByPathAsync("tracked/tracked.txt", ct);
            await index.WriteAsync(ct);

            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.RemoveUntracked,
                PathsStrings = ["tracked/untracked.txt"],
            };
            await repo.CheckoutHeadAsync(opts, ct);

            // The pathspec limits RemoveUntracked to the matching untracked
            // file. The tracked file (not in HEAD, but not matching the
            // pathspec) is NOT removed. Matches test_checkout_head__do_remove_untracked_paths.
            Assert.True(File.Exists(Path.Combine(workdir, "tracked", "tracked.txt")));
            Assert.False(File.Exists(Path.Combine(workdir, "tracked", "untracked.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Tree checkout & branch switching ────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> with
    /// <see cref="GitCheckoutStrategy.Force"/> writes all files from the
    /// tree into the workdir and updates the index to match.
    /// </summary>
    [Fact]
    public async Task Tree_Force_ChecksOutAllFilesAndSetsIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            Assert.NotNull(firstCommit);
            GitTree? tree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);
            Assert.NotNull(tree);

            // Wipe the workdir to prove checkout recreates the files.
            string workdir = repo.Workdir!;
            File.Delete(Path.Combine(workdir, "README.md"));
            File.Delete(Path.Combine(workdir, "fileA.txt"));

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo.CheckoutTreeAsync(tree, opts, ct);

            Assert.True(File.Exists(Path.Combine(workdir, "README.md")));
            Assert.True(File.Exists(Path.Combine(workdir, "fileA.txt")));
            Assert.Equal("readme\n", await File.ReadAllTextAsync(Path.Combine(workdir, "README.md"), ct));

            // Index should now contain both files.
            GitIndex index = await repo.GetIndexAsync(ct);
            Assert.NotNull(index.EntryByPath("README.md", 0));
            Assert.NotNull(index.EntryByPath("fileA.txt", 0));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> switching between
    /// two commits with different file sets removes files absent from the
    /// new tree and prunes their empty parent directories. Matches
    /// <c>test_checkout_tree__can_switch_branches</c>.
    /// </summary>
    [Fact]
    public async Task Tree_Force_SwitchesBranches_RemovesAbsentFilesAndDirs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Branch main: README.md, fileA.txt
            GitOid mainCommit = await InitRepoWithInitialCommitAsync(path, ct);

            // Branch feature: README.md, fileA.txt (modified), sub/deep.txt
            // Build on a second branch.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                Directory.CreateDirectory(Path.Combine(workdir, "sub"));
                await File.WriteAllTextAsync(Path.Combine(workdir, "sub", "deep.txt"), "deep\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nCHANGED\nline3\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("sub/deep.txt", ct);
                await idx.AddByPathAsync("fileA.txt", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [mainCommit],
                    Author = Sig,
                    Committer = Sig,
                    Message = "feature\n",
                    UpdateRef = "refs/heads/feature",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            // First, force-checkout HEAD to get a clean workdir matching main.
            (Commit mainCommitObj, GitTree mainTree) = await GetHeadCommitAndTreeAsync(repo2, ct);
            await repo2.CheckoutTreeAsync(mainTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct);
            await repo2.SetHeadAsync("refs/heads/main", ct);

            string workdir2 = repo2.Workdir!;
            Assert.True(File.Exists(Path.Combine(workdir2, "fileA.txt")));
            Assert.False(Directory.Exists(Path.Combine(workdir2, "sub")));

            // Now check out the feature branch tree.
            GitReference? featureRef = await repo2.ReferenceLookupAsync("refs/heads/feature", ct);
            Assert.NotNull(featureRef);
            GitOid featureOid = Assert.IsType<GitDirectReference>(featureRef).Target;
            Commit? featureCommit = await repo2.ObjectLookupAsync<Commit>(featureOid, ct);
            Assert.NotNull(featureCommit);
            GitTree? featureTree = await repo2.ObjectLookupAsync<GitTree>(featureCommit!.Tree, ct);
            Assert.NotNull(featureTree);

            await repo2.CheckoutTreeAsync(featureTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct);
            await repo2.SetHeadAsync("refs/heads/feature", ct);

            Assert.True(File.Exists(Path.Combine(workdir2, "sub", "deep.txt")));
            Assert.Equal("line1\nCHANGED\nline3\n", await File.ReadAllTextAsync(Path.Combine(workdir2, "fileA.txt"), ct));

            // Switch back to main: sub/deep.txt removed, sub/ pruned.
            await repo2.CheckoutTreeAsync(mainTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct);
            await repo2.SetHeadAsync("refs/heads/main", ct);
            Assert.False(File.Exists(Path.Combine(workdir2, "sub", "deep.txt")));
            Assert.False(Directory.Exists(Path.Combine(workdir2, "sub")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> with
    /// <see cref="GitCheckoutOptions.Paths"/> restricts the checkout to the
    /// matching subtree. Matches
    /// <c>test_checkout_tree__can_checkout_a_subdirectory_from_a_commit</c>.
    /// </summary>
    [Fact]
    public async Task Tree_Force_PathSpec_ChecksOutSubdirectoryOnly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Build a commit with sub/a.txt and sub/b.txt and root.txt.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "root.txt"), "root\n", ct);
                Directory.CreateDirectory(Path.Combine(workdir, "sub"));
                await File.WriteAllTextAsync(Path.Combine(workdir, "sub", "a.txt"), "a\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "sub", "b.txt"), "b\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("root.txt", ct);
                await idx.AddByPathAsync("sub/a.txt", ct);
                await idx.AddByPathAsync("sub/b.txt", ct);
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
            string workdir2 = repo2.Workdir!;
            // Wipe workdir.
            File.Delete(Path.Combine(workdir2, "root.txt"));
            Directory.Delete(Path.Combine(workdir2, "sub"), recursive: true);

            (_, GitTree headTree) = await GetHeadCommitAndTreeAsync(repo2, ct);
            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force,
                PathsStrings = ["sub/"],
            };
            await repo2.CheckoutTreeAsync(headTree, opts, ct);

            // Only sub/* written; root.txt NOT written.
            Assert.True(File.Exists(Path.Combine(workdir2, "sub", "a.txt")));
            Assert.True(File.Exists(Path.Combine(workdir2, "sub", "b.txt")));
            Assert.False(File.Exists(Path.Combine(workdir2, "root.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> on a subtree
    /// (subdirectory) object with a pathspec checks out only the matching
    /// entries within that subtree. Matches
    /// <c>test_checkout_tree__can_checkout_a_subdirectory_from_a_subtree</c>.
    /// </summary>
    [Fact]
    public async Task Tree_Force_PathSpec_ChecksOutFromSubtree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Build commit with ab/de/1.txt and ab/de/2.txt.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                Directory.CreateDirectory(Path.Combine(workdir, "ab", "de", "fgh"));
                await File.WriteAllTextAsync(Path.Combine(workdir, "ab", "de", "1.txt"), "1\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "ab", "de", "2.txt"), "2\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "ab", "de", "fgh", "1.txt"), "3\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("ab/de/1.txt", ct);
                await idx.AddByPathAsync("ab/de/2.txt", ct);
                await idx.AddByPathAsync("ab/de/fgh/1.txt", ct);
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
            string workdir2 = repo2.Workdir!;

            (_, GitTree headTree) = await GetHeadCommitAndTreeAsync(repo2, ct);
            // Look up the ab subtree (contains de/ which in turn contains
            // 1.txt, 2.txt, fgh/1.txt).
            GitTreeEntry? abEntry = headTree.EntryByName("ab");
            Assert.True(abEntry.HasValue, "ab entry should exist");
            GitTree? abTree = await repo2.ObjectLookupAsync<GitTree>(abEntry.Value.Id, ct);
            Assert.NotNull(abTree);

            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force,
                PathsStrings = ["de/"],
            };
            // Check out the ab subtree with a "de/" pathspec — only the de/
            // entries within ab are written, landing under de/ in the workdir.
            // Matches test_checkout_tree__can_checkout_a_subdirectory_from_a_subtree.
            await repo2.CheckoutTreeAsync(abTree, opts, ct);

            Assert.True(File.Exists(Path.Combine(workdir2, "de", "2.txt")));
            Assert.True(File.Exists(Path.Combine(workdir2, "de", "fgh", "1.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> removes a directory
    /// that exists in the current tree but not the target tree after a
    /// branch switch. Matches
    /// <c>test_checkout_tree__can_checkout_and_remove_directory</c>.
    /// </summary>
    [Fact]
    public async Task Tree_Force_RemovesDirectoryAfterSwitch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // main: README.md
            // feature: README.md + dir/inside.txt
            GitOid mainCommit;
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "readme\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("README.md", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                mainCommit = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "init\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await repo.SetHeadAsync("refs/heads/main", ct);

                // Create feature branch with dir/inside.txt.
                Directory.CreateDirectory(Path.Combine(workdir, "dir"));
                await File.WriteAllTextAsync(Path.Combine(workdir, "dir", "inside.txt"), "inside\n", ct);
                await idx.AddByPathAsync("dir/inside.txt", ct);
                await idx.WriteAsync(ct);
                GitOid featureTreeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = featureTreeOid,
                    Parents = [mainCommit],
                    Author = Sig,
                    Committer = Sig,
                    Message = "feature\n",
                    UpdateRef = "refs/heads/feature",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            // Check out feature, then switch back to main — dir/ should be pruned.
            GitReference? featureRef = await repo2.ReferenceLookupAsync("refs/heads/feature", ct);
            GitOid featureOid = Assert.IsType<GitDirectReference>(featureRef).Target;
            Commit? featureCommit = await repo2.ObjectLookupAsync<Commit>(featureOid, ct);
            GitTree? featureTree = await repo2.ObjectLookupAsync<GitTree>(featureCommit!.Tree, ct);

            await repo2.CheckoutTreeAsync(featureTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct);
            await repo2.SetHeadAsync("refs/heads/feature", ct);
            Assert.True(Directory.Exists(Path.Combine(repo2.Workdir!, "dir")));

            // Switch back to main.
            Commit? mainCommitObj = await repo2.ObjectLookupAsync<Commit>(mainCommit, ct);
            GitTree? mainTree = await repo2.ObjectLookupAsync<GitTree>(mainCommitObj!.Tree, ct);
            await repo2.CheckoutTreeAsync(mainTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct);
            await repo2.SetHeadAsync("refs/heads/main", ct);

            Assert.False(Directory.Exists(Path.Combine(repo2.Workdir!, "dir")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> with
    /// <see cref="GitCheckoutStrategy.None"/> does not write any files to
    /// the workdir (used by clone to disable the post-fetch checkout).
    /// Matches <c>test_checkout_tree__doesnt_write_unrequested_files_to_worktree</c>.
    /// </summary>
    [Fact]
    public async Task Tree_NoneStrategy_DoesNotWriteWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            // Wipe workdir.
            File.Delete(Path.Combine(workdir, "README.md"));
            File.Delete(Path.Combine(workdir, "fileA.txt"));

            (_, GitTree headTree) = await GetHeadCommitAndTreeAsync(repo, ct);
            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.None };
            await repo.CheckoutTreeAsync(headTree, opts, ct);

            // Nothing should have been written.
            Assert.False(File.Exists(Path.Combine(workdir, "README.md")));
            Assert.False(File.Exists(Path.Combine(workdir, "fileA.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Strategy flags ──────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> with the default
    /// <see cref="GitCheckoutStrategy.Safe"/> strategy aborts checkout with
    /// <see cref="GitErrorCode.Conflict"/> when a workdir file has been
    /// modified relative to the baseline AND the baseline→target delta would
    /// overwrite it (GIT_DELTA_MODIFIED, checkout.c:524-529). The dirty file
    /// is left untouched (no write happens before the throw).
    /// <b>Baseline model:</b> with no explicit baseline, C uses the HEAD tree
    /// (checkout.c:2476-2484), so the checkout target must differ from HEAD —
    /// here we check out the FIRST tree while HEAD is at the second commit.
    /// Matches libgit2's <c>checkout_get_actions</c> (checkout.c:1372-1380).
    /// See the unit test <c>Tree_Safe_ThrowsConflict_OnModified</c>.
    /// </summary>
    [Fact]
    public async Task Tree_Safe_ThrowsConflict_OnModifiedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify\n", "refs/heads/main", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            // Make a dirty workdir change distinct from both the baseline and target.
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nDIRTY\nline3\n", ct);

            // Target = the FIRST tree; baseline = HEAD tree (second commit's),
            // so fileA.txt is a Modified delta that would overwrite the dirty file.
            Commit? firstCommit = await repo.ObjectLookupAsync<Commit>(first, ct);
            GitTree? firstTree = await repo.ObjectLookupAsync<GitTree>(firstCommit!.Tree, ct);

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe };
            GitException ex = await Assert.ThrowsAsync<GitException>(
                async () => await repo.CheckoutTreeAsync(firstTree, opts, ct));
            Assert.Equal(GitErrorCode.Conflict, ex.Code);

            // Safe did NOT overwrite — the dirty content remains (no write
            // happened before the ECONFLICT throw).
            Assert.Equal("line1\nDIRTY\nline3\n", await File.ReadAllTextAsync(Path.Combine(workdir, "fileA.txt"), ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> with
    /// <see cref="GitCheckoutStrategy.Force"/> overwrites a modified workdir
    /// file with the target tree's content. Matches
    /// <c>test_checkout_index__can_overwrite_modified_file</c>.
    /// </summary>
    [Fact]
    public async Task Tree_Force_OverwritesModifiedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify\n", "refs/heads/main", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nDIRTY\nline3\n", ct);

            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            GitTree? secondTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo.CheckoutTreeAsync(secondTree, opts, ct);

            Assert.Equal("line1\nCHANGED\nline3\n", await File.ReadAllTextAsync(Path.Combine(workdir, "fileA.txt"), ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> with
    /// <see cref="GitCheckoutStrategy.RecreateMissing"/> recreates a file
    /// that was deleted from the workdir. Matches
    /// <c>test_checkout_index__can_create_missing_files</c>.
    /// </summary>
    [Fact]
    public async Task Tree_RecreateMissing_CreatesDeletedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            File.Delete(Path.Combine(workdir, "fileA.txt"));

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.RecreateMissing };
            await repo.CheckoutHeadAsync(opts, ct);

            Assert.True(File.Exists(Path.Combine(workdir, "fileA.txt")));
            Assert.Equal("line1\nline2\nline3\n", await File.ReadAllTextAsync(Path.Combine(workdir, "fileA.txt"), ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutHeadAsync"/> with
    /// <see cref="GitCheckoutStrategy.RemoveUntracked"/> removes untracked
    /// files from the workdir. Matches
    /// <c>test_checkout_tree__can_remove_untracked</c>.
    /// </summary>
    [Fact]
    public async Task Tree_RemoveUntracked_RemovesUntrackedFiles()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            await File.WriteAllTextAsync(Path.Combine(workdir, "untracked.txt"), "as you wish\n", ct);
            Assert.True(File.Exists(Path.Combine(workdir, "untracked.txt")));

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.RemoveUntracked };
            await repo.CheckoutHeadAsync(opts, ct);

            Assert.False(File.Exists(Path.Combine(workdir, "untracked.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutHeadAsync"/> with
    /// <see cref="GitCheckoutStrategy.RemoveIgnored"/> removes ignored
    /// files from the workdir. Matches
    /// <c>test_checkout_tree__can_remove_ignored</c>.
    /// </summary>
    [Fact]
    public async Task Tree_RemoveIgnored_RemovesIgnoredFiles()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            await repo.IgnoreAddRuleAsync("ignored_file\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir, "ignored_file"), "as you wish\n", ct);
            Assert.True(File.Exists(Path.Combine(workdir, "ignored_file")));

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.RemoveIgnored };
            await repo.CheckoutHeadAsync(opts, ct);

            Assert.False(File.Exists(Path.Combine(workdir, "ignored_file")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> with the default
    /// <see cref="GitCheckoutStrategy.Safe"/> strategy on a clean workdir
    /// succeeds when tracked files live in (possibly nested) subdirectories.
    /// End-to-end pin for the descend branch of
    /// <c>CheckoutContext.HandleWorkdirOnlyAsync</c>: an exact-match index
    /// lookup (<c>GitIndex.Find</c>) for the trailing-'/' workdir tree entry
    /// plus a spurious post-prefix '/' boundary check would make the descend
    /// branch unreachable — nested files would appear "missing from the
    /// workdir" and a Modified delta with no workdir entry would classify as
    /// Conflict under Safe (no <c>RecreateMissing</c>). Mirrors libgit2's
    /// <c>checkout_action_wd_only</c> (checkout.c:404-413) which descends
    /// whenever <c>git_index__find_pos</c>'s insertion-position entry
    /// prefix-matches the tree path via <c>pfxcomp</c>. Covers a direct child
    /// (src/top.txt) and a deeper descendant (src/nested/inner.txt).
    /// <b>Baseline model:</b> with no explicit baseline, C uses the HEAD tree
    /// (checkout.c:2476-2484), so the target must differ from HEAD — we check
    /// out branch B's tree (v1) while HEAD stays on main at C2 (v2). The
    /// workdir matches the baseline (clean), so Safe must succeed and revert
    /// both nested files to v1. (A SetHead-then-CheckoutHead sequence would
    /// diff HEAD against itself and do nothing — that is not a branch switch.)
    /// </summary>
    [Fact]
    public async Task Tree_Safe_CleanNestedWorkdir_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // C1 on main: src/top.txt=v1, src/nested/inner.txt=v1.
            GitOid c1;
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                c1 = await CommitNestedFilesAsync(
                    repo, repo.Workdir!,
                    [("src/top.txt", "top-v1\n"), ("src/nested/inner.txt", "inner-v1\n")],
                    "C1: nested v1\n", "refs/heads/main", [], ct);
                await repo.SetHeadAsync("refs/heads/main", ct);
            }

            // Branch B at C1 (the v1 point).
            await using (GitRepository repoB = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await repoB.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = (await repoB.ObjectLookupAsync<Commit>(c1, ct))!.Tree,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "B at C1\n",
                    UpdateRef = "refs/heads/B",
                }, ct);
            }

            // C2 on main: advance both nested files to v2. After this the
            // workdir and index match C2 (content v2).
            await using (GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await CommitNestedFilesAsync(
                    repo2, repo2.Workdir!,
                    [("src/top.txt", "top-v2\n"), ("src/nested/inner.txt", "inner-v2\n")],
                    "C2: nested v2\n", "refs/heads/main", [c1], ct);
            }

            // HEAD stays on main (C2). CheckoutTreeAsync(B's tree = C1):
            // baseline (HEAD tree = C2) → target (C1): both nested files are
            // Modified deltas. The workdir is clean (matches the index and
            // baseline), so Safe must succeed and revert both files to v1.
            await using GitRepository repo3 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo3.Workdir!;
            Commit? c1Commit = await repo3.ObjectLookupAsync<Commit>(c1, ct);
            GitTree? bTree = await repo3.ObjectLookupAsync<GitTree>(c1Commit!.Tree, ct);
            await repo3.CheckoutTreeAsync(
                bTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe }, ct);

            Assert.Equal("top-v1\n", await File.ReadAllTextAsync(Path.Combine(workdir, "src/top.txt"), ct));
            Assert.Equal("inner-v1\n", await File.ReadAllTextAsync(Path.Combine(workdir, "src/nested/inner.txt"), ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Negative counterpart to <see cref="Tree_Safe_CleanNestedWorkdir_Succeeds"/>:
    /// a genuinely dirty nested workdir file must still conflict under Safe.
    /// Guards against a descend branch that is too permissive (e.g.
    /// always treating nested files as unmodified). Tampers one nested file
    /// after the v2 commit so the workdir no longer matches the baseline; Safe
    /// checkout that would overwrite it must refuse with
    /// <see cref="GitErrorCode.Conflict"/> and leave the dirty file untouched.
    /// Same baseline model as <see cref="Tree_Safe_CleanNestedWorkdir_Succeeds"/>:
    /// checkout B's tree (v1) with HEAD on main (v2).
    /// </summary>
    [Fact]
    public async Task Tree_Safe_DirtyNestedWorkdir_Conflicts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid c1;
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                c1 = await CommitNestedFilesAsync(
                    repo, repo.Workdir!,
                    [("src/top.txt", "top-v1\n"), ("src/nested/inner.txt", "inner-v1\n")],
                    "C1: nested v1\n", "refs/heads/main", [], ct);
                await repo.SetHeadAsync("refs/heads/main", ct);
            }

            await using (GitRepository repoB = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await repoB.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = (await repoB.ObjectLookupAsync<Commit>(c1, ct))!.Tree,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "B at C1\n",
                    UpdateRef = "refs/heads/B",
                }, ct);
            }

            await using (GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await CommitNestedFilesAsync(
                    repo2, repo2.Workdir!,
                    [("src/top.txt", "top-v2\n"), ("src/nested/inner.txt", "inner-v2\n")],
                    "C2: nested v2\n", "refs/heads/main", [c1], ct);
            }

            await using GitRepository repo3 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo3.Workdir!;
            // Tamper a nested workdir file WITHOUT staging — workdir now differs
            // from the baseline (dirty), so Safe checkout that would overwrite
            // it must refuse with Conflict.
            await File.WriteAllTextAsync(Path.Combine(workdir, "src/nested/inner.txt"), "tampered\n", ct);

            Commit? c1Commit = await repo3.ObjectLookupAsync<Commit>(c1, ct);
            GitTree? bTree = await repo3.ObjectLookupAsync<GitTree>(c1Commit!.Tree, ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(
                async () => await repo3.CheckoutTreeAsync(
                    bTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe }, ct));
            Assert.Equal(GitErrorCode.Conflict, ex.Code);

            // Safe did NOT overwrite — the dirty content remains.
            Assert.Equal("tampered\n", await File.ReadAllTextAsync(Path.Combine(workdir, "src/nested/inner.txt"), ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> with
    /// <see cref="GitCheckoutStrategy.UpdateOnly"/> updates existing files
    /// in place but does not create files that are absent from the workdir
    /// (and does not remove files absent from the target tree). Matches
    /// libgit2's <c>checkout_safe_for_update_only</c> (checkout.c:1727-1749).
    /// </summary>
    [Fact]
    public async Task Tree_UpdateOnly_UpdatesExistingSkipsAbsent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // main: README.md
            // feature: README.md (modified) + new.txt
            GitOid mainCommit;
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "readme\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("README.md", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                mainCommit = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "init\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await repo.SetHeadAsync("refs/heads/main", ct);

                // feature branch: README.md modified + new.txt added.
                await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "changed\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "new.txt"), "new\n", ct);
                await idx.AddByPathAsync("README.md", ct);
                await idx.AddByPathAsync("new.txt", ct);
                await idx.WriteAsync(ct);
                GitOid featureTreeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = featureTreeOid,
                    Parents = [mainCommit],
                    Author = Sig,
                    Committer = Sig,
                    Message = "feature\n",
                    UpdateRef = "refs/heads/feature",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            // Force-checkout main to get a clean workdir.
            (_, GitTree mainTree) = await GetHeadCommitAndTreeAsync(repo2, ct);
            await repo2.CheckoutTreeAsync(mainTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct);
            await repo2.SetHeadAsync("refs/heads/main", ct);

            string workdir2 = repo2.Workdir!;
            Assert.False(File.Exists(Path.Combine(workdir2, "new.txt")));

            // UpdateOnly checkout of feature: README.md updated (existing file),
            // new.txt NOT created (workdir file absent — checkout_safe_for_update_only
            // returns 0 → skip).
            GitReference? featureRef = await repo2.ReferenceLookupAsync("refs/heads/feature", ct);
            GitOid featureOid = Assert.IsType<GitDirectReference>(featureRef).Target;
            Commit? featureCommit = await repo2.ObjectLookupAsync<Commit>(featureOid, ct);
            GitTree? featureTree = await repo2.ObjectLookupAsync<GitTree>(featureCommit!.Tree, ct);
            await repo2.CheckoutTreeAsync(featureTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.UpdateOnly }, ct);

            Assert.Equal("changed\n", await File.ReadAllTextAsync(Path.Combine(workdir2, "README.md"), ct));
            Assert.False(File.Exists(Path.Combine(workdir2, "new.txt")),
                "new.txt should NOT be created under UpdateOnly (workdir file absent)");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutTreeAsync"/> with
    /// <see cref="GitCheckoutStrategy.DryRun"/> determines actions but does
    /// not modify the workdir or index. Matches
    /// <c>test_checkout_tree__dry_run</c>.
    /// </summary>
    [Fact]
    public async Task Tree_DryRun_DoesNotModifyWorkdirOrIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify\n", "refs/heads/main", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            // Workdir matches first commit (unchanged).
            string originalContent = await File.ReadAllTextAsync(Path.Combine(workdir, "fileA.txt"), ct);

            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            GitTree? secondTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.DryRun };
            await repo.CheckoutTreeAsync(secondTree, opts, ct);

            // Workdir unchanged.
            Assert.Equal(originalContent, await File.ReadAllTextAsync(Path.Combine(workdir, "fileA.txt"), ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Index checkout & pathspec ───────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.CheckoutIndexAsync"/> on a bare repo
    /// throws <see cref="GitException"/> with
    /// <see cref="GitErrorCode.BareRepo"/>. Matches
    /// <c>test_checkout_index__cannot_checkout_a_bare_repository</c>.
    /// </summary>
    [Fact]
    public async Task Index_BareRepo_ThrowsBareRepo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-checkout-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository bare = await GitRepository.InitAsync(path, isBare: true, new GitContext(), cancellationToken: ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(
                async () => await bare.CheckoutIndexAsync(cancellationToken: ct));
            Assert.Equal(GitErrorCode.BareRepo, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutIndexAsync"/> with
    /// <see cref="GitCheckoutStrategy.Force"/> writes all index entries to
    /// the workdir. Matches <c>test_checkout_index__can_create_missing_files</c>.
    /// </summary>
    [Fact]
    public async Task Index_Force_WritesAllIndexEntriesToWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            // Wipe workdir.
            File.Delete(Path.Combine(workdir, "README.md"));
            File.Delete(Path.Combine(workdir, "fileA.txt"));

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo.CheckoutIndexAsync(opts, ct);

            Assert.True(File.Exists(Path.Combine(workdir, "README.md")));
            Assert.True(File.Exists(Path.Combine(workdir, "fileA.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutIndexAsync"/> with
    /// <see cref="GitCheckoutOptions.Paths"/> only checks out paths
    /// matching the pathspec. Matches
    /// <c>test_checkout_index__honor_the_specified_pathspecs</c>.
    /// </summary>
    [Fact]
    public async Task Index_PathSpec_OnlyChecksOutMatchingPaths()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Build a commit with README.md, fileA.txt, fileB.txt.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "readme\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "a\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileB.txt"), "b\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("README.md", ct);
                await idx.AddByPathAsync("fileA.txt", ct);
                await idx.AddByPathAsync("fileB.txt", ct);
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
            string workdir2 = repo2.Workdir!;
            // Wipe workdir.
            File.Delete(Path.Combine(workdir2, "README.md"));
            File.Delete(Path.Combine(workdir2, "fileA.txt"));
            File.Delete(Path.Combine(workdir2, "fileB.txt"));

            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force,
                PathsStrings = ["*.txt"],
            };
            await repo2.CheckoutIndexAsync(opts, ct);

            Assert.False(File.Exists(Path.Combine(workdir2, "README.md")));
            Assert.True(File.Exists(Path.Combine(workdir2, "fileA.txt")));
            Assert.True(File.Exists(Path.Combine(workdir2, "fileB.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutIndexAsync"/> with
    /// <see cref="GitCheckoutStrategy.DisablePathSpecMatch"/> treats the
    /// pathspec as a literal file list, not a glob. Matches
    /// <c>test_checkout_index__can_disable_pathspec_match</c>.
    /// </summary>
    [Fact]
    public async Task Index_DisablePathSpecMatch_TreatsPathsAsLiteral()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Build a commit with test9.txt, test10.txt, test11.txt, test12.txt.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "test9.txt"), "original\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "test10.txt"), "original\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "test11.txt"), "original\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "test12.txt"), "original\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("test9.txt", ct);
                await idx.AddByPathAsync("test10.txt", ct);
                await idx.AddByPathAsync("test11.txt", ct);
                await idx.AddByPathAsync("test12.txt", ct);
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
            string workdir2 = repo2.Workdir!;
            // Modify all 4 files in the workdir.
            await File.WriteAllTextAsync(Path.Combine(workdir2, "test9.txt"), "modified\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir2, "test10.txt"), "modified\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir2, "test11.txt"), "modified\n", ct);
            await File.WriteAllTextAsync(Path.Combine(workdir2, "test12.txt"), "modified\n", ct);

            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.DisablePathSpecMatch,
                PathsStrings = ["test10.txt", "test11.txt"],
            };
            await repo2.CheckoutIndexAsync(opts, ct);

            // Only test10 and test11 reverted; test9 and test12 stay modified.
            Assert.Equal("modified\n", await File.ReadAllTextAsync(Path.Combine(workdir2, "test9.txt"), ct));
            Assert.Equal("original\n", await File.ReadAllTextAsync(Path.Combine(workdir2, "test10.txt"), ct));
            Assert.Equal("original\n", await File.ReadAllTextAsync(Path.Combine(workdir2, "test11.txt"), ct));
            Assert.Equal("modified\n", await File.ReadAllTextAsync(Path.Combine(workdir2, "test12.txt"), ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.CheckoutIndexAsync"/> with an explicit
    /// in-memory <see cref="GitIndex"/> (built via <see cref="GitIndex.New"/>
    /// + <c>ReadTreeAsync</c>) writes the provided index's entries to the
    /// workdir. The index need not be the repo's own on-disk index.
    /// </summary>
    [Fact]
    public async Task Index_ExplicitIndex_WritesProvidedIndexToWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;

            // Build an ephemeral in-memory index seeded from a tree that
            // contains "explicit.txt".
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "explicit\n"u8.ToArray(), ct);
            using GitTreeBuilder tb = repo.NewTreeBuilder();
            await tb.InsertAsync("explicit.txt", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await tb.WriteAsync(ct);
            GitTree? tree = await repo.ObjectLookupAsync<GitTree>(treeOid, ct);
            Assert.NotNull(tree);

            var ephemeral = GitIndex.New(repo.ObjectFormat);
            await ephemeral.ReadTreeAsync(tree, ct);

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo.CheckoutIndexAsync(ephemeral, opts, ct);

            Assert.True(File.Exists(Path.Combine(workdir, "explicit.txt")));
            Assert.Equal("explicit\n", await File.ReadAllTextAsync(Path.Combine(workdir, "explicit.txt"), ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Notify, Progress, Perfdata, TargetDirectory ─────────────────────

    /// <summary>
    /// <see cref="GitCheckoutOptions.Notify"/> returning <c>true</c>
    /// aborts the checkout by throwing <see cref="GitException"/> with
    /// <see cref="GitErrorCode.User"/>. The notification is delivered for
    /// the dirty workdir file (<see cref="GitCheckoutNotifyFlags.Dirty"/>).
    /// Matches <c>test_checkout_tree__can_cancel_checkout_from_notify</c>.
    /// </summary>
    [Fact]
    public async Task Tree_Notify_AbortsCheckoutWhenCallbackReturnsTrue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify\n", "refs/heads/main", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            // Make the workdir dirty so a notification fires.
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nDIRTY\nline3\n", ct);

            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            GitTree? secondTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);

            var notifications = new List<GitCheckoutNotification>();
            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Safe,
                NotifyFlags = GitCheckoutNotifyFlags.Conflict | GitCheckoutNotifyFlags.Dirty,
                Notify = n =>
                {
                    notifications.Add(n);
                    return true; // abort
                },
            };
            GitException ex = await Assert.ThrowsAsync<GitException>(
                async () => await repo.CheckoutTreeAsync(secondTree, opts, ct));
            Assert.Equal(GitErrorCode.User, ex.Code);
            Assert.NotEmpty(notifications);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitCheckoutOptions.Progress"/> receives progress
    /// events during checkout; the final report has
    /// <see cref="GitCheckoutProgress.CompletedSteps"/> ==
    /// <see cref="GitCheckoutProgress.TotalSteps"/>. Matches
    /// <c>test_checkout_tree__calls_progress_callback</c>.
    /// </summary>
    [Fact]
    public async Task Tree_Progress_ReportsSteps()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            File.Delete(Path.Combine(workdir, "README.md"));
            File.Delete(Path.Combine(workdir, "fileA.txt"));

            (_, GitTree headTree) = await GetHeadCommitAndTreeAsync(repo, ct);

            var events = new List<GitCheckoutProgress>();
            var progress = new SynchronousProgress<GitCheckoutProgress>(events.Add);
            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force,
                Progress = progress,
            };
            await repo.CheckoutTreeAsync(headTree, opts, ct);

            Assert.NotEmpty(events);
            GitCheckoutProgress last = events[^1];
            Assert.True(last.TotalSteps > 0, "TotalSteps should be > 0 for a non-trivial checkout");
            Assert.Equal(last.TotalSteps, last.CompletedSteps);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitCheckoutOptions.Perfdata"/> receives a performance
    /// counter snapshot after checkout completes. Matches libgit2's
    /// <c>test_checkout_tree__can_collect_perfdata</c>: the
    /// <see cref="GitCheckoutPerformance.MkdirCalls"/> counter is non-zero
    /// when checkout creates a nested directory in the workdir.
    /// </summary>
    [Fact]
    public async Task Tree_Perfdata_ReportsMkdirStatChmod()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Build a commit with a nested directory to exercise mkdir.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                Directory.CreateDirectory(Path.Combine(workdir, "sub", "deep"));
                await File.WriteAllTextAsync(Path.Combine(workdir, "sub", "deep", "x.txt"), "x\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("sub/deep/x.txt", ct);
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
            (_, GitTree headTree) = await GetHeadCommitAndTreeAsync(repo2, ct);
            // Wipe the workdir to force a full recreate.
            Directory.Delete(Path.Combine(repo2.Workdir!, "sub"), recursive: true);

            GitCheckoutPerformance? captured = null;
            var perfdata = new SynchronousProgress<GitCheckoutPerformance>(p => captured = p);
            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force,
                Perfdata = perfdata,
            };
            await repo2.CheckoutTreeAsync(headTree, opts, ct);

            // The perfdata callback fires with non-zero counters.
            Assert.NotNull(captured);
            Assert.True(captured.Value.MkdirCalls > 0,
                $"MkdirCalls should be > 0 after recreating sub/deep/, got {captured.Value.MkdirCalls}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitCheckoutOptions.TargetDirectory"/> redirects
    /// checkout writes to an alternative directory instead of the
    /// repository workdir. Matches libgit2's
    /// <c>test_checkout_index__target_directory</c>.
    /// </summary>
    [Fact]
    public async Task Tree_TargetDirectory_WritesToAltDir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            string altDir = Path.Combine(path, "alternative");
            Directory.CreateDirectory(altDir);

            // Wipe workdir.
            File.Delete(Path.Combine(workdir, "README.md"));
            File.Delete(Path.Combine(workdir, "fileA.txt"));

            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force,
                TargetDirectory = altDir,
            };
            await repo.CheckoutIndexAsync(opts, ct);

            // Files land in altDir, NOT in the workdir.
            Assert.True(File.Exists(Path.Combine(altDir, "README.md")),
                "README.md should be written to TargetDirectory");
            Assert.True(File.Exists(Path.Combine(altDir, "fileA.txt")),
                "fileA.txt should be written to TargetDirectory");
            Assert.False(File.Exists(Path.Combine(workdir, "README.md")),
                "workdir should not receive the file when TargetDirectory is set");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitCheckoutOptions.TargetDirectory"/> allows checkout
    /// from a bare repository (which has no workdir) when an alternative
    /// directory is provided. Matches libgit2's
    /// <c>test_checkout_tree__target_directory_from_bare</c>.
    /// </summary>
    [Fact]
    public async Task Tree_TargetDirectory_FromBare_WritesToAltDir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "libgit2cs-checkout-bare-td-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Init a bare repo and write a commit + tree directly via the ODB.
            await using GitRepository bare = await GitRepository.InitAsync(path, isBare: true, new GitContext(), cancellationToken: ct);
            GitOid blobOid = await bare.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
            using GitTreeBuilder tb = bare.NewTreeBuilder();
            await tb.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await tb.WriteAsync(ct);
            await bare.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = Sig,
                Committer = Sig,
                Message = "init\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            string altDir = Path.Combine(path, "alternative");
            Directory.CreateDirectory(altDir);

            GitTree? tree = await bare.ObjectLookupAsync<GitTree>(treeOid, ct);
            Assert.NotNull(tree);
            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force,
                TargetDirectory = altDir,
            };

            // With TargetDirectory set, checkout from a bare repo succeeds
            // and writes the file to altDir.
            await bare.CheckoutTreeAsync(tree, opts, ct);
            Assert.True(File.Exists(Path.Combine(altDir, "hello.txt")),
                "TargetDirectory file should exist in altDir from a bare repo");
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Conflicts (ours/theirs/diff3/automerge) ─────────────────────────

    /// <summary>
    /// <see cref="GitCheckoutStrategy.UseOurs"/> writes the "ours"
    /// (stage 2) content to the workdir for a conflicted path, leaving the
    /// index conflict entries in place. Matches
    /// <c>test_checkout_conflict__ours</c>.
    /// </summary>
    [Fact]
    public async Task Conflict_UseOurs_WritesOursContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Make an initial commit so HEAD has a tree (checkout needs a baseline).
            await CommitFileAsync(repo, repo.Workdir!, "base.txt", "base\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await BuildThreeStageConflictAsync(repo, "conflict.txt",
                ancestorContent: "ancestor line\n",
                oursContent: "ours line\n",
                theirsContent: "theirs line\n", ct);

            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.AllowConflicts | GitCheckoutStrategy.UseOurs,
            };
            await repo.CheckoutIndexAsync(opts, ct);

            string content = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "conflict.txt"), ct);
            Assert.Equal("ours line\n", content);

            // UseOurs/UseTheirs PRESERVE the index conflict entries (stages
            // 1/2/3) — the path remains conflicted in the index until the
            // user explicitly resolves it. Matches libgit2's
            // checkout_conflict_update_index (checkout.c:2167-2179, 2264-2265)
            // which re-adds the stage 1/2/3 entries via git_index_add.
            GitIndex idx = await repo.GetIndexAsync(ct);
            Assert.True(idx.HasConflicts,
                "UseOurs should preserve index conflict entries (libgit2 leaves stages 1/2/3 in the index)");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitCheckoutStrategy.UseTheirs"/> writes the "theirs"
    /// (stage 3) content to the workdir for a conflicted path, and
    /// preserves the index conflict entries. Matches
    /// <c>test_checkout_conflict__theirs</c>.
    /// </summary>
    [Fact]
    public async Task Conflict_UseTheirs_WritesTheirsContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, repo.Workdir!, "base.txt", "base\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await BuildThreeStageConflictAsync(repo, "conflict.txt",
                ancestorContent: "ancestor line\n",
                oursContent: "ours line\n",
                theirsContent: "theirs line\n", ct);

            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.AllowConflicts | GitCheckoutStrategy.UseTheirs,
            };
            await repo.CheckoutIndexAsync(opts, ct);

            string content = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "conflict.txt"), ct);
            Assert.Equal("theirs line\n", content);

            // Index conflict entries preserved (see UseOurs test comment).
            GitIndex idx = await repo.GetIndexAsync(ct);
            Assert.True(idx.HasConflicts,
                "UseTheirs should preserve index conflict entries");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitCheckoutStrategy.ConflictStyleDiff3"/> writes
    /// diff3-style conflict markers (including the
    /// <c>||||||| ancestor</c> section) to the workdir for a conflicted
    /// path. Matches <c>test_checkout_conflict__diff3</c>.
    /// </summary>
    [Fact]
    public async Task Conflict_Diff3_WritesAncestorMarkers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, repo.Workdir!, "base.txt", "base\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            await BuildThreeStageConflictAsync(repo, "conflict.txt",
                ancestorContent: "ancestor line\n",
                oursContent: "ours line\n",
                theirsContent: "theirs line\n", ct);

            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.AllowConflicts | GitCheckoutStrategy.ConflictStyleDiff3,
            };
            await repo.CheckoutIndexAsync(opts, ct);

            string content = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "conflict.txt"), ct);
            Assert.Contains("<<<<<<<", content);
            Assert.Contains("|||||||", content);
            Assert.Contains("=======", content);
            Assert.Contains(">>>>>>>", content);
            Assert.Contains("ours line", content);
            Assert.Contains("theirs line", content);
            Assert.Contains("ancestor line", content);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitCheckoutStrategy.AllowConflicts"/> performs a 3-way
    /// merge of conflicted entries; non-conflicting regions are auto-merged
    /// and only the conflicting region carries conflict markers. Matches
    /// <c>test_checkout_conflict__automerge</c>.
    /// </summary>
    [Fact]
    public async Task Conflict_Automerge_ResolvesNonConflictingLines()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await CommitFileAsync(repo, repo.Workdir!, "base.txt", "base\n", "refs/heads/main", ct);
            await repo.SetHeadAsync("refs/heads/main", ct);

            // Ancestor has 3 unique lines; ours adds one, theirs adds a
            // different one — non-conflicting additions auto-merge.
            await BuildThreeStageConflictAsync(repo, "conflict.txt",
                ancestorContent: "line1\nline2\nline3\n",
                oursContent: "line1\nline2-ours\nline3\nline4-ours\n",
                theirsContent: "line1\nline2-theirs\nline3\nline5-theirs\n", ct);

            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.AllowConflicts,
            };
            await repo.CheckoutIndexAsync(opts, ct);

            string content = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "conflict.txt"), ct);
            // The non-conflicting unique additions should be present.
            Assert.Contains("line1", content);
            Assert.Contains("line3", content);
            // The conflicting line2 region carries markers.
            Assert.Contains("<<<<<<<", content);
            Assert.Contains(">>>>>>>", content);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Platform-specific (filemode/symlinks/CRLF) ──────────────────────

    /// <summary>
    /// <see cref="GitCheckoutStrategy.Force"/> checkout reverts the
    /// executable bit on a workdir file when the target tree's mode is
    /// non-executable but the existing workdir file is executable. Matches
    /// libgit2's <c>test_checkout_head__typechange_workdir</c> and the
    /// remove-and-recreate path in <c>checkout_action_common</c>
    /// (checkout.c:274-277). POSIX-only.
    /// </summary>
    [Fact]
    public async Task FileMode_Force_RevertsExecBit()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // chmod exec semantics are POSIX-only
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await InitRepoWithInitialCommitAsync(path, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            string fileA = Path.Combine(workdir, "fileA.txt");

            // Make the workdir file executable.
            File.SetUnixFileMode(fileA, UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.UserRead);

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo.CheckoutHeadAsync(opts, ct);

            // The exec bit is reverted to non-exec by Force checkout.
            UnixFileMode mode = File.GetUnixFileMode(fileA);
            Assert.True((mode & UnixFileMode.UserExecute) == 0,
                $"fileA.txt should be reverted to non-exec by Force checkout, but stayed {mode}");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitCheckoutStrategy.Safe"/> ignores a mode-only change
    /// (chmod 0666 vs 0644) when both modes simplify to the same git mode
    /// (non-executable). Matches
    /// <c>test_checkout_head__workdir_filemode_is_simplified</c>. POSIX-only.
    /// </summary>
    [Fact]
    public async Task FileMode_Safe_IgnoresModeOnlyChange()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // chmod exec semantics are POSIX-only
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            GitOid first = await InitRepoWithInitialCommitAsync(path, ct);
            GitOid second = await AddCommitModifyingFileAAsync(path, first, "line1\nCHANGED\nline3\n", "modify\n", "refs/heads/main", ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo.Workdir!;
            string fileA = Path.Combine(workdir, "fileA.txt");

            // chmod 0666 — different on-disk mode, but both 0666 and 0644
            // simplify to git's "Regular" (non-exec) mode, so Safe checkout
            // should NOT treat this as a conflict.
            File.SetUnixFileMode(fileA, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

            Commit? secondCommit = await repo.ObjectLookupAsync<Commit>(second, ct);
            GitTree? secondTree = await repo.ObjectLookupAsync<GitTree>(secondCommit!.Tree, ct);

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe };
            // Should succeed (no conflict) — the mode change is simplified away.
            await repo.CheckoutTreeAsync(secondTree, opts, ct);

            Assert.Equal("line1\nCHANGED\nline3\n", await File.ReadAllTextAsync(fileA, ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// With <c>core.symlinks=true</c>, checking out a tree containing a
    /// symlink entry creates a real symbolic link in the workdir. Matches
    /// <c>test_checkout_index__honor_coresymlinks_setting_set_to_true</c>.
    /// POSIX-only (Windows symlink creation requires admin).
    /// </summary>
    [Fact]
    public async Task Symlinks_Force_CreatesSymlinkWhenCoreSymlinksTrue()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // symlinks require POSIX
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Build a commit with a regular file and a symlink pointing at it.
            GitOid commitOid;
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "target.txt"), "target\n", ct);
                GitOid targetBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, "target.txt"u8.ToArray(), ct);

                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("target.txt", ct);
                // Add the symlink as an index entry with Symlink mode.
                idx.Add(new GitIndexEntry("link.txt", targetBlob, GitFileMode.Symlink));
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "init\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await repo.SetHeadAsync("refs/heads/main", ct);
                await repo.Config.SetBoolAsync("core.symlinks", true, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir2 = repo2.Workdir!;
            // Wipe workdir.
            File.Delete(Path.Combine(workdir2, "target.txt"));
            File.Delete(Path.Combine(workdir2, "link.txt"));

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo2.CheckoutHeadAsync(opts, ct);

            string linkPath = Path.Combine(workdir2, "link.txt");
            Assert.True(File.Exists(linkPath), "link.txt should exist");
            // It should be a real symlink pointing at target.txt.
            Assert.True(File.Exists(linkPath));
            string? linkTarget = new FileInfo(linkPath).LinkTarget;
            Assert.NotNull(linkTarget);
            // The link target should be "target.txt" (the stored symlink path).
            Assert.Equal("target.txt", linkTarget);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// With <c>core.symlinks=false</c>, checking out a tree containing a
    /// symlink entry writes a regular file containing the link target path
    /// as its content. Matches
    /// <c>test_checkout_index__honor_coresymlinks_setting_set_to_false</c>.
    /// Runs on all platforms (Windows default is symlinks=false).
    /// </summary>
    [Fact]
    public async Task Symlinks_Force_CreatesRegularFileWhenCoreSymlinksFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Build a commit with a regular file and a symlink entry.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "target.txt"), "target\n", ct);
                GitOid targetBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, "target.txt"u8.ToArray(), ct);

                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("target.txt", ct);
                idx.Add(new GitIndexEntry("link.txt", targetBlob, GitFileMode.Symlink));
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
                await repo.Config.SetBoolAsync("core.symlinks", false, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir2 = repo2.Workdir!;
            File.Delete(Path.Combine(workdir2, "target.txt"));
            if (File.Exists(Path.Combine(workdir2, "link.txt")))
            {
                File.Delete(Path.Combine(workdir2, "link.txt"));
            }

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo2.CheckoutHeadAsync(opts, ct);

            string linkPath = Path.Combine(workdir2, "link.txt");
            Assert.True(File.Exists(linkPath));
            // With symlinks=false, the link is written as a regular file
            // containing the target path as text.
            string content = await File.ReadAllTextAsync(linkPath, ct);
            Assert.Equal("target.txt", content);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// With <c>core.autocrlf=true</c>, checking out a blob with LF line
    /// endings writes CRLF line endings to the workdir file. Matches
    /// <c>test_checkout_index__honor_coreautocrlf_setting_set_to_true</c>
    /// and <c>test_checkout_crlf__detect_crlf_autocrlf_true</c>.
    /// </summary>
    [Fact]
    public async Task CRLF_AutoCrlfTrue_WritesCrlfLineEndings()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Build a commit with a file containing LF line endings.
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nline2\nline3\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("fileA.txt", ct);
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
                await repo.Config.SetBoolAsync("core.autocrlf", true, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir2 = repo2.Workdir!;
            File.Delete(Path.Combine(workdir2, "fileA.txt"));

            var opts = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            await repo2.CheckoutHeadAsync(opts, ct);

            byte[] content = await File.ReadAllBytesAsync(Path.Combine(workdir2, "fileA.txt"), ct);
            // With autocrlf=true, LF in the blob becomes CRLF in the workdir.
            Assert.Contains((byte)'\r', content);
            // The content should contain "\r\n" sequences.
            string text = Encoding.UTF8.GetString(content);
            Assert.Contains("\r\n", text);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Advanced options ───────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitCheckoutStrategy.DontWriteIndex"/> updates the
    /// in-memory index (so the caller sees the staged changes) but does NOT
    /// write the index to disk — a second handle opening the same repo sees
    /// the pre-checkout on-disk index. Matches
    /// <c>test_checkout_tree__can_update_but_not_write_index</c>.
    /// </summary>
    [Fact]
    public async Task Tree_DontWriteIndex_LeavesOnDiskIndexUnchanged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // main: README.md
            // feature: README.md (modified)
            GitOid mainCommit;
            await using (GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "v1\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("README.md", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                mainCommit = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig,
                    Committer = Sig,
                    Message = "init\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await repo.SetHeadAsync("refs/heads/main", ct);

                // feature branch modifies README.md.
                await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "v2\n", ct);
                await idx.AddByPathAsync("README.md", ct);
                await idx.WriteAsync(ct);
                GitOid featureTreeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = featureTreeOid,
                    Parents = [mainCommit],
                    Author = Sig,
                    Committer = Sig,
                    Message = "feature\n",
                    UpdateRef = "refs/heads/feature",
                }, ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            // Force-checkout main to get clean state.
            (_, GitTree mainTree) = await GetHeadCommitAndTreeAsync(repo2, ct);
            await repo2.CheckoutTreeAsync(mainTree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, ct);
            await repo2.SetHeadAsync("refs/heads/main", ct);

            // Capture the on-disk index state (the v1 blob OID for README.md).
            GitIndex indexBefore = await repo2.GetIndexAsync(ct);
            GitIndexEntry? entryBefore = indexBefore.EntryByPath("README.md", 0);
            Assert.True(entryBefore.HasValue, "README.md should be in the index before checkout");
            GitOid oidBefore = entryBefore.Value.Id;

            // Check out feature with DontWriteIndex — workdir updated, index NOT written to disk.
            GitReference? featureRef = await repo2.ReferenceLookupAsync("refs/heads/feature", ct);
            GitOid featureOid = Assert.IsType<GitDirectReference>(featureRef).Target;
            Commit? featureCommit = await repo2.ObjectLookupAsync<Commit>(featureOid, ct);
            GitTree? featureTree = await repo2.ObjectLookupAsync<GitTree>(featureCommit!.Tree, ct);
            await repo2.CheckoutTreeAsync(featureTree, new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force | GitCheckoutStrategy.DontWriteIndex,
            }, ct);

            // Workdir has the new content.
            Assert.Equal("v2\n", await File.ReadAllTextAsync(Path.Combine(repo2.Workdir!, "README.md"), ct));

            // A fresh repo handle sees the on-disk index (still v1 — not written).
            await using GitRepository repo3 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            GitIndex indexAfter = await repo3.GetIndexAsync(ct);
            GitIndexEntry? entryAfter = indexAfter.EntryByPath("README.md", 0);
            Assert.True(entryAfter.HasValue, "README.md should still be in the on-disk index");
            Assert.Equal(oidBefore, entryAfter.Value.Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitCheckoutOptions.Baseline"/> overrides the HEAD tree
    /// used as the diff baseline. A workdir file that matches the baseline
    /// but differs from the target tree is treated as a clean update (no
    /// conflict), while without the override it would conflict.
    /// </summary>
    [Fact]
    public async Task Tree_BaselineOverride_UsesProvidedTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Build three commits on main:
            //   c1: fileA.txt = v1
            //   c2: fileA.txt = v2  (this is the baseline)
            //   c3: fileA.txt = v3  (this is HEAD / target)
            GitOid c1 = await InitRepoWithInitialCommitAsync(path, ct);
            // InitRepo wrote fileA.txt = "line1\nline2\nline3\n"; rewrite as v1.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                string workdirInner = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdirInner, "fileA.txt"), "v1\n", ct);
                GitIndex idx = await repo.GetIndexAsync(ct);
                await idx.AddByPathAsync("fileA.txt", ct);
                await idx.WriteAsync(ct);
                GitOid treeOid = await idx.WriteTreeAsync(ct);
                await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [c1],
                    Author = Sig,
                    Committer = Sig,
                    Message = "v1\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            GitOid c2 = await AddCommitModifyingFileAAsync(path, c1, "v2\n", "v2\n", "refs/heads/main", ct);
            GitOid c3 = await AddCommitModifyingFileAAsync(path, c2, "v3\n", "v3\n", "refs/heads/main", ct);

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            string workdir = repo2.Workdir!;

            // Put the workdir at v2 (matches the baseline we'll pass).
            await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "v2\n", ct);

            // Look up the c2 tree to use as the baseline override.
            Commit? c2Commit = await repo2.ObjectLookupAsync<Commit>(c2, ct);
            Assert.NotNull(c2Commit);
            GitTree? baselineTree = await repo2.ObjectLookupAsync<GitTree>(c2Commit!.Tree, ct);
            Assert.NotNull(baselineTree);

            // Target = c3 tree.
            Commit? c3Commit = await repo2.ObjectLookupAsync<Commit>(c3, ct);
            Assert.NotNull(c3Commit);
            GitTree? targetTree = await repo2.ObjectLookupAsync<GitTree>(c3Commit!.Tree, ct);
            Assert.NotNull(targetTree);

            // With Baseline = c2 tree, the workdir (v2) matches the baseline,
            // so a Safe checkout to c3 (v3) should succeed cleanly — the
            // workdir is "clean" relative to the provided baseline.
            var opts = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Safe,
                Baseline = baselineTree,
            };
            await repo2.CheckoutTreeAsync(targetTree, opts, ct);

            Assert.Equal("v3\n", await File.ReadAllTextAsync(Path.Combine(workdir, "fileA.txt"), ct));
        }
        finally
        {
            Cleanup(path);
        }
    }
}
