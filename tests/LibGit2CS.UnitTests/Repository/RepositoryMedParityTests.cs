using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

/// <summary> Regression tests for the repository-core parity behaviors (libgit2 1.9.4): (open_bare fast path), (SetBare in-memory state),
/// (unparseable extensions.worktreeconfig), (empty core.worktree), (commondir ASCII rtrim), (gitdir pointer ASCII rtrim), (set_head
/// checked-out-branch guard), (reinit formatversion parse), (MERGE_HEAD corruption handling). </summary>
public sealed class RepositoryMedParityTests : IAsyncLifetime
{
    private readonly string _tempDir;

    public RepositoryMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepositoryMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitRepository> InitRepoAsync(string name, bool bare, bool withCommit = false)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, bare, new GitContext());
        if (withCommit)
        {
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "data\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder treeBld = repo.NewTreeBuilder();
            await treeBld.InsertAsync("f.txt", blobOid, GitFileMode.Regular);
            GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = TestSig(),
                Committer = TestSig(),
                Message = "m\n",
                UpdateRef = "refs/heads/master",
            });
        }

        return repo;
    }

    /// <summary>Creates a minimal fake gitdir (HEAD + config + objects + refs).</summary>
    private string CreateFakeGitdir(string name)
    {
        string gitdir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(gitdir, "HEAD"), "ref: refs/heads/master\n");
        File.WriteAllText(Path.Combine(gitdir, "config"), "[core]\n");
        Directory.CreateDirectory(Path.Combine(gitdir, "objects"));
        Directory.CreateDirectory(Path.Combine(gitdir, "refs"));
        return gitdir;
    }

    // ── OpenBareAsync fast path ───────────────────────────────

    [Fact]
    public async Task OpenBare_SkipsGraftsLoading()
    {
        // C (repository.c:1021-1064): git_repository_open_bare never calls
        // load_grafts — a shallow bare repo has NO grafts loaded, while the
        // open_ext path loads them.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("open-bare-grafts", bare: true);

        string oidHex = new('a', 40);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "shallow"), oidHex + "\n", ct);

        await using GitRepository opened = await GitRepository.OpenBareAsync(repo.Path, new GitContext(), ct);
        Assert.Null(opened.ShallowGrafts);
    }

    // ── SetBareAsync stale in-memory state ────────────────────

    [Fact]
    public async Task SetBare_UpdatesInMemoryState()
    {
        // C (repository.c:3321-3345): after writing core.bare=true and
        // deleting core.worktree, repo->workdir = NULL and repo->is_bare = 1
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("setbare-state", bare: false);

        Assert.False(repo.IsBare);
        Assert.NotNull(repo.Workdir);

        await repo.SetBareAsync(ct);

        Assert.True(repo.IsBare);
        Assert.Null(repo.Workdir);
        Assert.True(await repo.Config.GetBoolAsync("core.bare", cancellationToken: ct));

        // C: an already-bare repo returns early (no-op).
        await repo.SetBareAsync(ct);
        Assert.True(repo.IsBare);
    }

    // ── unparseable extensions.worktreeconfig ─────────────────

    [Fact]
    public async Task Open_UnparseableWorktreeConfig_Fails()
    {
        // C (repository.c:1261-1277, has_config_worktree): the extension is
        // read UNCONDITIONALLY and a parse error fails the open with
        // "failed to parse 'banana' as a boolean".
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = Path.Combine(_tempDir, "worktree-config");
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), ct);
        await repo.Config.SetStringAsync("extensions.worktreeconfig", "banana", ct);
        await repo.DisposeAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitRepository.OpenAsync(repoPath, new GitContext(), ct));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
        Assert.Contains("failed to parse 'banana' as a boolean", ex.Message);
    }

    // ── empty core.worktree ───────────────────────────────────

    [Fact]
    public async Task Open_EmptyCoreWorktree_Throws()
    {
        // C (repository.c:429-434, load_workdir): a present core.worktree
        // with an empty value → GIT_ERROR_NET "working directory cannot be
        // set to empty path".
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = Path.Combine(_tempDir, "empty-core-worktree");
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), ct);
        await repo.Config.SetStringAsync("core.worktree", "", ct);
        await repo.DisposeAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitRepository.OpenAsync(repoPath, new GitContext(), ct));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Net, ex.Category);
        Assert.Contains("working directory cannot be set to empty path", ex.Message);
    }

    // ── commondir pointer file ASCII rtrim ────────────────────

    [Fact]
    public async Task Open_CommondirLeadingSpace_FailsToResolve()
    {
        // C (repository.c:230-243, lookup_commondir): git_str_rtrim is ASCII
        // trailing-only, so a leading-space commondir entry stays relative
        // and prettify fails.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string realCommon = Path.Combine(_tempDir, "commondir-space-common");
        Directory.CreateDirectory(realCommon);
        string gitdir = CreateFakeGitdir("commondir-space-gitdir");
        await File.WriteAllTextAsync(Path.Combine(gitdir, "commondir"), $" {realCommon}\n", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitRepository.OpenAsync(gitdir, new GitContext(), ct));

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("failed to resolve path", ex.Message);
    }

    // ── worktree gitdir pointer ASCII rtrim ───────────────────

    [Fact]
    public async Task Open_WorktreeGitdirFileLeadingSpace_NotResolved()
    {
        // C (repository.c:413-428, load_workdir): git_worktree__read_link
        // rtrims only — leading whitespace is preserved, so the worktree
        // root is NOT derived from the space-prefixed path.
        CancellationToken ct = TestContext.Current.CancellationToken;
        // The commondir must itself be a valid repository (objects/refs).
        string common = Path.Combine(_tempDir, "gitdir-space-common");
        Directory.CreateDirectory(Path.Combine(common, "objects"));
        Directory.CreateDirectory(Path.Combine(common, "refs"));
        string realWorkRoot = Path.Combine(_tempDir, "gitdir-space-real");
        Directory.CreateDirectory(realWorkRoot);
        string gitdir = CreateFakeGitdir("gitdir-space-gitdir");
        await File.WriteAllTextAsync(Path.Combine(gitdir, "commondir"), common + "\n", ct);
        await File.WriteAllTextAsync(Path.Combine(gitdir, "gitdir"), $" {Path.Combine(realWorkRoot, ".git")}\n", ct);

        await using GitRepository opened = await GitRepository.OpenAsync(gitdir, new GitContext(), ct);

        // The space-prefixed gitdir file is not an existing path — the
        // workdir falls back to the parent of the gitdir (C keeps the raw
        // dirname).
        Assert.Equal(PathHelpers.PrettifyDir(PathHelpers.Dirname(gitdir)), opened.Workdir);
    }

    // ── SetHeadAsync checked-out-branch guard ─────────────────

    [Fact]
    public async Task SetHead_BranchCheckedOutInLinkedWorktree_Throws()
    {
        // C (repository.c:3603-3616): setting HEAD to a branch that is the
        // current HEAD of a LINKED repository fails with
        // GIT_ERROR_REPOSITORY "cannot set HEAD to reference '%s' as it is
        // the current HEAD of a linked repository."
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("sethead-linked", bare: false, withCommit: true);
        GitReference master = (await repo.ReferenceLookupAsync("refs/heads/master", ct))!;
        GitOid masterOid = ((GitDirectReference)master).Target;
        await repo.BranchCreateAsync("dev", masterOid, force: false, ct);

        // Check out dev in a linked worktree.
        string wtPath = Path.Combine(_tempDir, "sethead-linked-wt");
        GitReference dev = (await repo.ReferenceLookupAsync("refs/heads/dev", ct))!;
        await repo.WorktreeAddAsync("sethead-linked-wt2", wtPath, new WorktreeAddOptions { Ref = dev }, ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.SetHeadAsync("refs/heads/dev", ct));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Repository, ex.Category);
        Assert.Contains("as it is the current HEAD of a linked repository", ex.Message);
    }

    // ── reinit swallows unparseable formatversion ─────────────

    [Fact]
    public async Task Reinit_UnparseableRepositoryFormatVersion_Throws()
    {
        // C (repository.c:2330-2332 → check_repositoryformatversion,
        // 1836-1862): the reinit path fails on an unparseable value (a C probe:
        // "failed to parse 'banana' as a 32-bit integer").
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = Path.Combine(_tempDir, "reinit-format");
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), ct);
        await repo.Config.SetStringAsync("core.repositoryformatversion", "banana", ct);
        await repo.DisposeAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), ct));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("failed to parse 'banana' as a 32-bit integer", ex.Message);
    }

    // ── MergeHeadForEachAsync corrupt MERGE_HEAD ──────────────

    [Fact]
    public async Task MergeHead_EmptyLine_ThrowsInvalidLength()
    {
        // C (merge.c:588-641): an empty line is strlen 0 ≠ hexsize →
        // GIT_ERROR_INVALID "unable to parse OID - invalid length". The
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("mergehead-empty", bare: true);
        string oidHex = new('a', 40);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "MERGE_HEAD"), $"{oidHex}\n\n", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (GitOid _ in repo.MergeHeadForEachAsync(ct))
            {
            }
        });

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Contains("unable to parse OID - invalid length", ex.Message);
    }

    [Fact]
    public async Task MergeHead_NoTrailingEol_ThrowsNoEol()
    {
        // C: leftover bytes without a trailing newline abort with
        // GIT_ERROR_MERGE "no EOL at line N".
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("mergehead-noeol", bare: true);
        string oidHex = new('a', 40);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "MERGE_HEAD"), oidHex, ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (GitOid _ in repo.MergeHeadForEachAsync(ct))
            {
            }
        });

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Merge, ex.Category);
        Assert.Contains("no EOL at line 1", ex.Message);
    }

    [Fact]
    public async Task MergeHead_CrlfLine_ThrowsInvalidLength()
    {
        // C: a CRLF line is 41 bytes ≠ 40 → invalid length.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("mergehead-crlf", bare: true);
        string oidHex = new('a', 40);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "MERGE_HEAD"), $"{oidHex}\r\n", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (GitOid _ in repo.MergeHeadForEachAsync(ct))
            {
            }
        });

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Contains("unable to parse OID - invalid length", ex.Message);
    }

    [Fact]
    public async Task MergeHead_InvalidHex_ThrowsInvalidChars()
    {
        // C: a full-length line with non-hex chars fails git_oid__fromstr →
        // GIT_ERROR_INVALID "unable to parse OID - contains invalid
        // characters".
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("mergehead-invalid", bare: true);
        string badHex = new('z', 40);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "MERGE_HEAD"), badHex + "\n", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (GitOid _ in repo.MergeHeadForEachAsync(ct))
            {
            }
        });

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Contains("unable to parse OID - contains invalid characters", ex.Message);
    }

    [Fact]
    public async Task MergeHead_ValidLines_YieldsOids()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("mergehead-valid", bare: true);
        string oid1 = new('a', 40);
        string oid2 = new('b', 40);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "MERGE_HEAD"), $"{oid1}\n{oid2}\n", ct);

        var yielded = new List<GitOid>();
        await foreach (GitOid oid in repo.MergeHeadForEachAsync(ct))
        {
            yielded.Add(oid);
        }

        Assert.Equal(2, yielded.Count);
        Assert.Equal(GitOid.Parse(oid1.AsSpan(), LibGit2CS.Core.Hashing.GitHashAlgorithmKind.Sha1), yielded[0]);
        Assert.Equal(GitOid.Parse(oid2.AsSpan(), LibGit2CS.Core.Hashing.GitHashAlgorithmKind.Sha1), yielded[1]);
    }
}
