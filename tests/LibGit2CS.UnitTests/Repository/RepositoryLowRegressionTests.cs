using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

// Parity cases verified against libgit2 1.9.4:
// - IsHeadDetachedAsync must include C's ODB-existence check for a direct
//    HEAD (repository.c:2940-2943) — a dangling detached HEAD is not detached.
// - IsHeadUnbornAsync must treat a MISSING HEAD file as an error, not 'unborn',
//    like C's GIT_ENOTFOUND (repository.c:3068-3081).
// - SetWorkdirAsync with an empty string must fail with GIT_ENOTFOUND like
//    C's realpath("") (repository.c:3262-3264), not set Workdir="/".
// - ResetDefaultAsync with an empty pathspec must not reset the entire index;
//    C's git_reset_default asserts pathspecs->count > 0 (reset.c:33).
// - directory-discovered worktrees must set RepoPaths.Gitlink so
//    ownership validation checks the path C checks (repository.c:823-826).
// - CRLF graft/shallow lines must report 'invalid parent OID' like C
//    (grafts.c:142-181), not 'invalid graft OID'.
public sealed class RepositoryLowRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryLowRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepoLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GitSignature Sig() => new("t", "t@t", new GitTime(1700000000, 0));

    private async Task<GitRepository> InitRepoWithCommitAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig(),
            Committer = Sig(),
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        return repo;
    }

    // ─── detached-HEAD ODB-existence check ──────────────────────────

    /// <summary>
    /// A direct HEAD whose target object is missing (dangling detached HEAD)
    /// is NOT detached — C returns git_odb_exists(odb, target)
    /// (repository.c:2940-2943).
    /// </summary>
    [Fact]
    public async Task IsHeadDetached_DanglingDirectHead_ReturnsFalse()
    {
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "n07a"), isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Write a direct HEAD pointing at a nonexistent object.
        string dangling = new('d', 40);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "HEAD"), dangling + "\n", TestContext.Current.CancellationToken);

        Assert.False(await repo.IsHeadDetachedAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Positive control: a direct HEAD whose target EXISTS is detached.
    /// </summary>
    [Fact]
    public async Task IsHeadDetached_ValidDirectHead_ReturnsTrue()
    {
        await using GitRepository repo = await InitRepoWithCommitAsync("n07b");
        GitOid head = ((GitDirectReference)(await repo.ReferenceResolveAsync("refs/heads/main", TestContext.Current.CancellationToken))!).Target;

        await repo.SetHeadDetachedAsync(head, TestContext.Current.CancellationToken);
        Assert.True(await repo.IsHeadDetachedAsync(TestContext.Current.CancellationToken));
    }

    // ─── missing HEAD file is an error, not 'unborn' ───────────────

    /// <summary>
    /// A repository whose HEAD file is absent entirely fails the lookup with
    /// GIT_ENOTFOUND in C (repository.c:3068-3081 via git_repository_head);
    /// only a symbolic HEAD to a nonexistent branch is 'unborn'.
    /// </summary>
    [Fact]
    public async Task IsHeadUnborn_MissingHeadFile_ThrowsNotFound()
    {
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "n08a"), isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(repo.Path, "HEAD"));

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.IsHeadUnbornAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("reference 'HEAD' not found", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Positive control: a symbolic HEAD whose target branch does not exist
    /// (fresh repo) is unborn.
    /// </summary>
    [Fact]
    public async Task IsHeadUnborn_SymbolicToMissingBranch_ReturnsTrue()
    {
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "n08b"), isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await repo.IsHeadUnbornAsync(TestContext.Current.CancellationToken));
    }

    // ─── empty workdir rejected like C's realpath ─────────────────

    /// <summary>
    /// C's git_repository_set_workdir calls git_fs_path_prettify_dir first
    /// (repository.c:3262-3264) — realpath("") fails with ENOENT →
    /// GIT_ENOTFOUND "failed to resolve path ''".
    /// </summary>
    [Fact]
    public async Task SetWorkdir_EmptyString_ThrowsNotFound()
    {
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "n09a"), isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        string original = repo.Workdir!;

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.SetWorkdirAsync("", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("failed to resolve path ''", ex.Message, StringComparison.Ordinal);

        // The workdir must be untouched.
        Assert.Equal(original, repo.Workdir);
    }

    /// <summary>
    /// Guard: a nonexistent-but-nonempty path is also rejected (Prettify's
    /// realpath check) — matching C's GIT_ENOTFOUND.
    /// </summary>
    [Fact]
    public async Task SetWorkdir_NonexistentPath_ThrowsNotFound()
    {
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "n09b"), isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.SetWorkdirAsync(Path.Combine(_tempDir, "does-not-exist"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ─── empty pathspec rejected like C's assert ──────────────────

    /// <summary>
    /// C's git_reset_default begins with GIT_ASSERT_ARG(pathspecs &&
    /// pathspecs->count > 0) (reset.c:33) — an empty pathspec errors
    /// ("invalid argument: 'pathspecs && pathspecs->count > 0'") and never
    /// proceeds, so the whole index is not reset to the target tree.
    /// </summary>
    [Fact]
    public async Task ResetDefault_EmptyPathspec_ThrowsInvalidArgument()
    {
        await using GitRepository repo = await InitRepoWithCommitAsync("n10a");
        GitOid head = ((GitDirectReference)(await repo.ReferenceResolveAsync("refs/heads/main", TestContext.Current.CancellationToken))!).Target;
        Commit? commit = await repo.ObjectLookupAsync<Commit>(head, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        using (commit)
        {
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.ResetDefaultAsync(commit, Array.Empty<string>(), TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Contains("invalid argument: 'pathspecs && pathspecs->count > 0'", ex.Message, StringComparison.Ordinal);
        }

        // The index must be untouched — the throw happens before any mutation.
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(index.EntryByPath("f.txt"));
    }

    // ─── directory-discovered worktree sets the gitlink ────────────

    /// <summary>
    /// C's find_repo_traverse directory branch attaches
    /// git_worktree__read_link(path, GIT_GITDIR_FILE) — the content of the
    /// gitdir file, i.e. the worktree's .git file path — to out->gitlink
    /// (repository.c:823-826), so validate_ownership checks it.
    /// </summary>
    [Fact]
    public async Task FindRepo_DirectoryDiscoveredWorktree_SetsGitlink()
    {
        await using GitRepository repo = await InitRepoWithCommitAsync("n11a");
        string wtPath = Path.Combine(_tempDir, "wt_n11");
        await LibGit2CS.Repository.Worktree.AddAsync(repo, "wt1", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // Discover the linked worktree via its admin gitdir (the directory
        // branch of the walk).
        string adminGitDir = Path.Combine(repo.CommonDir, "worktrees", "wt1");
        RepoPaths paths = await RepositoryOpener.FindRepoAsync(
            adminGitDir,
            RepositoryOpenFlags.NoDotgit,
            ceilingDirs: null,
            repo.Context,
            TestContext.Current.CancellationToken);

        // The gitlink is the content of the admin gitdir's "gitdir" file —
        // the worktree root's .git file path.
        string expectedGitlink = PathHelpers.Join(PathHelpers.PrettifyDir(wtPath), ".git");
        Assert.Equal(expectedGitlink, paths.Gitlink);
    }

    // ─── CRLF shallow/graft lines report 'invalid parent OID' ─────

    /// <summary>
    /// C's git_grafts_parse (grafts.c:142-181) first advances the 40-hex OID
    /// (git_parse_advance_oid requires no trailing separator), then the
    /// parent loop's git_parse_advance_expected(" ") fails on the '\r' →
    /// "invalid parent OID at line N".
    /// </summary>
    [Fact]
    public async Task Grafts_CrlfShallowLine_InvalidParentOidMessage()
    {
        string path = Path.Combine(_tempDir, "n12a");
        string oid = new('a', 40);
        await File.WriteAllTextAsync(path, oid + "\r\n", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("invalid parent OID at line 1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guard: a CRLF graft line (OID + parent + CRLF) also reports
    /// "invalid parent OID" — the '\r' after the parent fails the next space
    /// expectation, exactly as in C.
    /// </summary>
    [Fact]
    public async Task Grafts_CrlfGraftLine_InvalidParentOidMessage()
    {
        string path = Path.Combine(_tempDir, "n12b");
        string oid = new('a', 40);
        await File.WriteAllTextAsync(path, oid + " " + oid + "\r\n", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("invalid parent OID at line 1", ex.Message, StringComparison.Ordinal);
    }
}
