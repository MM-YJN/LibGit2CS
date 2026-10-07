using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Refs;

namespace LibGit2CS.IntegrationTests.Refs;

/// <summary>
/// End-to-end tests for the refs parity behaviors (libgit2 1.9.4)
/// against repos built from scratch with <see cref="RepoBuilder"/>:
/// delete-nonexistent error codes,
/// lock contention, logallrefupdates=always, packed-ref rename cleanup,
/// target-OID validation, SetTarget type guards, the HEAD branch-switch
/// reflog entry, symbolic-chain depth errors, and CAS-on-missing-ref.
/// </summary>
public sealed class RefsParityIntegrationTests
{
    private static GitException Throws(Func<Task> action, GitErrorCode code)
    {
        GitException ex = Assert.ThrowsAsync<GitException>(async () => await action().ConfigureAwait(false)).GetAwaiter().GetResult();
        Assert.Equal(code, ex.Code);
        return ex;
    }

    private static GitException Throws<T>(Func<Task<T>> action, GitErrorCode code)
        => Throws(async () => { _ = await action().ConfigureAwait(false); }, code);

    [Fact]
    public async Task DeleteNonexistentRef_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitException ex = Throws(
            () => bld.Repo.Refs.DeleteAsync("refs/heads/does-not-exist", ct),
            GitErrorCode.NotFound);

        Assert.Equal("reference 'refs/heads/does-not-exist' not found", ex.Message);
    }

    [Fact]
    public async Task LockContention_ThrowsLocked()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        string lockPath = Path.Combine(bld.Path, ".git", "refs", "heads", "main.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, "stale", ct);

        Throws(
            () => bld.Repo.ReferenceCreateAsync("refs/heads/main", history[0], force: true, cancellationToken: ct),
            GitErrorCode.Locked);
    }

    [Fact]
    public async Task LogAllRefUpdatesAlways_WritesReflogForArbitraryRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        await bld.Repo.Config.SetStringAsync("core.logallrefupdates", "always", ct);

        _ = await bld.Repo.ReferenceCreateAsync("refs/foo/bar", history[0], logMessage: "create", cancellationToken: ct);

        Assert.True(File.Exists(Path.Combine(bld.Path, ".git", "logs", "refs", "foo", "bar")));
    }

    [Fact]
    public async Task RenamePackedRef_RemovesOldName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        string packedPath = Path.Combine(bld.Path, ".git", "packed-refs");
        await File.WriteAllTextAsync(packedPath, "# pack-refs with: peeled fully-peeled sorted \n" + history[0] + " refs/heads/packed1\n", ct);

        GitReference? packed = await bld.Repo.ReferenceLookupAsync("refs/heads/packed1", ct);
        Assert.NotNull(packed);

        await bld.Repo.ReferenceRenameAsync(packed!, "refs/heads/packed2", cancellationToken: ct);

        Assert.Null(await bld.Repo.ReferenceLookupAsync("refs/heads/packed1", ct));
        GitReference? renamed = await bld.Repo.ReferenceLookupAsync("refs/heads/packed2", ct);
        Assert.NotNull(renamed);
        Assert.Equal(history[0], ((GitDirectReference)renamed!).Target);
    }

    [Fact]
    public async Task CreateRefWithMissingTarget_ThrowsError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        _ = await bld.BuildLinearHistoryAsync(1, ct: ct);
        var missing = GitOid.Parse("0123456789012345678901234567890123456789".AsSpan(), GitHashAlgorithmKind.Sha1);

        GitException ex = Throws(
            () => bld.Repo.ReferenceCreateAsync("refs/heads/dangling", missing, cancellationToken: ct),
            GitErrorCode.Error);

        Assert.Equal("target OID for the reference doesn't exist on the repository", ex.Message);
    }

    [Fact]
    public async Task SetTargetOnSymbolicRef_ThrowsError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        GitReference? head = await bld.Repo.ReferenceLookupAsync("HEAD", ct);
        Assert.NotNull(head);

        GitException ex = Throws(
            () => bld.Repo.ReferenceSetTargetAsync(head!, history[0], cancellationToken: ct),
            GitErrorCode.Error);

        Assert.Equal("cannot set OID on symbolic reference", ex.Message);
    }

    [Fact]
    public async Task BranchSwitch_WritesHeadReflogEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(2, ct: ct);
        // Create a branch without switching HEAD to it (creating a
        // non-current branch does not touch the HEAD reflog).
        await bld.CreateBranchAsync("other", history[1], ct);

        GitRefLog? before = await bld.Repo.ReferenceReadLogAsync("HEAD", ct);
        int countBefore = before?.EntryCount ?? 0;

        // C (refdb_fs.c:2318-2332): switching HEAD to an EXISTING branch
        // resolves the new target's OID and appends a HEAD reflog entry.
        await bld.Repo.SetHeadAsync("refs/heads/other", ct);

        GitRefLog? after = await bld.Repo.ReferenceReadLogAsync("HEAD", ct);
        Assert.NotNull(after);
        Assert.Equal(countBefore + 1, after!.EntryCount);
        Assert.Equal(history[1], after[0].NewId);
    }

    [Fact]
    public async Task SymbolicChainTooDeep_ThrowsError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        await bld.Repo.ReferenceCreateSymbolicAsync("refs/a", "refs/b", cancellationToken: ct);
        await bld.Repo.ReferenceCreateSymbolicAsync("refs/b", "refs/c", cancellationToken: ct);
        await bld.Repo.ReferenceCreateSymbolicAsync("refs/c", "refs/d", cancellationToken: ct);
        await bld.Repo.ReferenceCreateSymbolicAsync("refs/d", "refs/e", cancellationToken: ct);
        await bld.Repo.ReferenceCreateSymbolicAsync("refs/e", "refs/f", cancellationToken: ct);
        await bld.Repo.ReferenceCreateSymbolicAsync("refs/f", "refs/g", cancellationToken: ct);
        GitOid gTree = await bld.Repo.RevparseSingleAsync("HEAD^{tree}", ct) is GitTree t ? t.Id : throw new InvalidOperationException("no HEAD tree");
        _ = await bld.Repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = gTree,
            Parents = [history[0]],
            Author = new GitSignature("t", "t@t", new GitTime(1700000001, 0)),
            Committer = new GitSignature("t", "t@t", new GitTime(1700000001, 0)),
            Message = "g\n",
            UpdateRef = "refs/g",
        }, ct);

        GitException ex = Throws(
            () => bld.Repo.ReferenceResolveAsync("refs/a", ct),
            GitErrorCode.Error);

        Assert.Equal("cannot resolve reference (>5 levels deep)", ex.Message);
    }

    [Fact]
    public async Task CreateMatchingOnMissingRef_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        var expectedOld = GitOid.Parse("1111111111111111111111111111111111111111".AsSpan(), GitHashAlgorithmKind.Sha1);

        GitException ex = Throws(
            () => bld.Repo.ReferenceCreateMatchingAsync("refs/heads/cas", history[0], expectedOld, cancellationToken: ct),
            GitErrorCode.NotFound);

        Assert.Equal("reference 'refs/heads/cas' not found", ex.Message);
    }

    // ── HeadAsync: git_repository_head ──

    [Fact]
    public async Task HeadAsync_SymbolicHead_ReturnsBranchRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);

        GitReference? head = await bld.Repo.HeadAsync(ct);

        Assert.NotNull(head);
        Assert.Equal("refs/heads/main", head!.Name);
        GitDirectReference direct = Assert.IsType<GitDirectReference>(head);
        Assert.Equal(history[0], direct.Target);
    }

    [Fact]
    public async Task HeadAsync_DetachedHead_ReturnsHeadDirectRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();
        GitOid[] history = await bld.BuildLinearHistoryAsync(1, ct: ct);
        await bld.DetachHeadAsync(history[0], ct);

        GitReference? head = await bld.Repo.HeadAsync(ct);

        Assert.NotNull(head);
        Assert.Equal("HEAD", head!.Name);
        GitDirectReference direct = Assert.IsType<GitDirectReference>(head);
        Assert.Equal(history[0], direct.Target);
    }

    [Fact]
    public async Task HeadAsync_UnbornOrMissingHead_ReturnsNull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var bld = new RepoBuilder();

        // Fresh InitAsync: HEAD symbolic to refs/heads/main, no main ref yet.
        Assert.Null(await bld.Repo.HeadAsync(ct));

        // Missing HEAD file entirely (empty/unborn repo).
        File.Delete(Path.Combine(bld.Path, ".git", "HEAD"));
        Assert.Null(await bld.Repo.HeadAsync(ct));
    }
}
