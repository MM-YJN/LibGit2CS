using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Reset;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Refs;

/// <summary> Parity tests for refs-revwalk-notes: (annotated-reset reflog description), (loaded-value CAS on
/// SetTarget/SetSymbolicTarget/Delete(ref)), (zero-push walk preserves Sort/_limited), (linked-worktree per-worktree refs in enumeration). </summary>
public sealed class RefsMedParityTests2 : IDisposable
{
    private readonly string _tempDir;

    public RefsMedParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RefsMed2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private string NewDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async ValueTask<GitRepository> InitRepoAsync(string dir)
        => await GitRepository.InitAsync(dir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

    private static async Task<GitOid> CommitAllAsync(GitRepository repo, string message, GitOid? firstParent = null, string updateRef = "refs/heads/master")
    {
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        GitOid tree = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = firstParent is { } p ? [p] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message + "\n",
            UpdateRef = updateRef,
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── annotated-reset reflog carries the description ────────

    [Fact]
    public async Task ResetFromAnnotated_ReflogUsesDescription()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "v1\n", TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid c1 = await CommitAllAsync(repo, "one");

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "v2\n", TestContext.Current.CancellationToken);
        idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        _ = await CommitAllAsync(repo, "two", firstParent: c1);

        // Annotated commit with the revspec as its description.
        using GitAnnotatedCommit annotated = await GitAnnotatedCommit.FromRevspecAsync(repo, "HEAD~1", TestContext.Current.CancellationToken);
        Assert.Equal("HEAD~1", annotated.Description);

        await repo.ResetFromAnnotatedAsync(annotated, GitResetMode.Soft, cancellationToken: TestContext.Current.CancellationToken);

        // C (reset.c:147,197-204): "reset: moving to <description>" — not the OID hex.
        GitRefLog reflog = (await repo.ReferenceReadLogAsync("HEAD", TestContext.Current.CancellationToken))!;
        Assert.Contains(reflog, e => e.Message.Contains("reset: moving to HEAD~1", StringComparison.Ordinal));
        Assert.DoesNotContain(reflog, e => e.Message.Contains($"reset: moving to {c1}", StringComparison.Ordinal));
    }

    // ── loaded-value compare-and-swap ──────────────────────────

    [Fact]
    public async Task SetTarget_StaleReference_Modified()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);

        GitOid oid1 = await CommitAllAsync(repo, "one");
        GitOid oid2 = await CommitAllAsync(repo, "two", firstParent: oid1);
        GitOid oid3 = await CommitAllAsync(repo, "three", firstParent: oid2);

        _ = await repo.ReferenceCreateAsync("refs/heads/cas", oid1, logMessage: "create", cancellationToken: TestContext.Current.CancellationToken);
        GitReference stale = (await repo.ReferenceLookupAsync("refs/heads/cas", TestContext.Current.CancellationToken))!;

        // Move the ref behind the loaded object's back.
        _ = await repo.ReferenceSetTargetAsync(stale, oid2, cancellationToken: TestContext.Current.CancellationToken);

        // C (refs.c:560, git_reference_create_matching): the write is a CAS against the LOADED target → GIT_EMODIFIED.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.ReferenceSetTargetAsync(stale, oid3, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Modified, ex.Code);

        // The ref still points at oid2.
        GitReference now = (await repo.ReferenceLookupAsync("refs/heads/cas", TestContext.Current.CancellationToken))!;
        Assert.Equal(oid2, ((GitDirectReference)now).Target);
    }

    [Fact]
    public async Task Delete_StaleReference_Modified()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);

        GitOid oid1 = await CommitAllAsync(repo, "one");
        GitOid oid2 = await CommitAllAsync(repo, "two", firstParent: oid1);

        _ = await repo.ReferenceCreateAsync("refs/heads/casdel", oid1, logMessage: "create", cancellationToken: TestContext.Current.CancellationToken);
        GitReference stale = (await repo.ReferenceLookupAsync("refs/heads/casdel", TestContext.Current.CancellationToken))!;

        _ = await repo.ReferenceSetTargetAsync(stale, oid2, cancellationToken: TestContext.Current.CancellationToken);

        // C (refs.c:162-167): the delete is a CAS against the loaded value → GIT_EMODIFIED.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.Refs.DeleteAsync(stale, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Modified, ex.Code);

        Assert.NotNull(await repo.ReferenceLookupAsync("refs/heads/casdel", TestContext.Current.CancellationToken));
    }

    // ── zero-push walk preserves Sort/_limited ─────────────────

    [Fact]
    public async Task RevWalk_ZeroPushWalk_PreservesSort()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);

        GitOid c1 = await CommitAllAsync(repo, "one");
        GitOid c2 = await CommitAllAsync(repo, "two", firstParent: c1);

        using GitRevWalker walk = repo.NewRevWalker();
        walk.Sort = GitSortMode.Time;

        // Enumerate once with NO pushes (C: prepare_walk → GIT_ITEROVER WITHOUT git_revwalk_reset — sorting survives).
        List<GitOid> empty = [];
        await foreach (GitOid oid in walk.WalkAsync(TestContext.Current.CancellationToken))
        {
            empty.Add(oid);
        }

        Assert.Empty(empty);

        // Push and walk again — the SORT must still be configured.
        await walk.PushAsync(c2, TestContext.Current.CancellationToken);
        List<GitOid> walked = [];
        await foreach (GitOid oid in walk.WalkAsync(TestContext.Current.CancellationToken))
        {
            walked.Add(oid);
        }

        Assert.Equal([c2, c1], walked);
    }

    // ── linked-worktree per-worktree refs are enumerated ──────

    [Fact]
    public async Task WorktreeRefs_EnumeratedFromWorktreeRepo()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);

        GitOid c1 = await CommitAllAsync(repo, "one");
        string wtPath = Path.Combine(_tempDir, "wt-" + Guid.NewGuid().ToString("N")[..8]);
        using LibGit2CS.Repository.Worktree worktree = await repo.WorktreeAddAsync("wt", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // A per-worktree ref: refs/bisect/foo in the WORKTREE gitdir.
        string wtGitDir = Path.Combine(repo.Path, "worktrees", "wt");
        Directory.CreateDirectory(Path.Combine(wtGitDir, "refs", "bisect"));
        await File.WriteAllTextAsync(Path.Combine(wtGitDir, "refs", "bisect", "foo"), c1 + "\n", TestContext.Current.CancellationToken);

        await using GitRepository wtRepo = await GitRepository.OpenAsync(wtPath, new GitContext(), TestContext.Current.CancellationToken);

        // C (iter_load_loose_paths, refdb_fs.c:937-950): the worktree enumeration includes gitpath/refs per-worktree refs.
        var names = new HashSet<string>();
        await foreach (GitReference r in wtRepo.ReferenceListAsync(glob: null, TestContext.Current.CancellationToken))
        {
            names.Add(r.Name);
        }

        Assert.Contains("refs/bisect/foo", names);

        // The MAIN repo must NOT enumerate the worktree's refs/bisect/foo.
        var mainNames = new HashSet<string>();
        await foreach (GitReference r in repo.ReferenceListAsync(glob: null, TestContext.Current.CancellationToken))
        {
            mainNames.Add(r.Name);
        }

        Assert.DoesNotContain("refs/bisect/foo", mainNames);
    }
}
