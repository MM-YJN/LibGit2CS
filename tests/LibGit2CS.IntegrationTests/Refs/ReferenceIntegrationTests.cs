using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.TestKit;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using LibGit2CSRemote = LibGit2CS.Remote;

namespace LibGit2CS.IntegrationTests.Refs;

/// <summary>
/// Integration tests for the <see cref="LibGit2CS.Refs"/> namespace exercised
/// end-to-end through <see cref="GitRepository"/> against locally-initialized
/// non-bare repos. Covers the public ref/branch surface that no unit or
/// integration test previously drove: DWIM short-name resolution,
/// <see cref="GitReferences.Shorthand"/>, compare-and-swap
/// (<see cref="GitReferences.CreateMatchingAsync"/>), symbolic-set-target,
/// the public reflog write API (<see cref="GitReferences.EnsureLogAsync"/>/
/// <see cref="GitReferences.AppendReflogAsync"/>), the branch-management
/// methods on <see cref="GitReference"/> (<see cref="GitReference.MoveAsync"/>,
/// <see cref="GitReference.SetUpstreamAsync"/>,
/// <see cref="GitReference.IsCheckedOutAsync"/>), and the branch lookup/upstream
/// facade on <see cref="GitRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>ReferencesTests</c>/<c>RefWriteTests</c>/
/// <c>RefLogTests</c> cover the read/write/reflog primitives against a real repo
/// in isolation, and the <c>RefSpecTests</c> cover refspec parsing/transform
/// purely. None of them exercise the cross-component flows that the
/// <c>branch.c</c> entry points need (refs + config + worktrees together), the
/// DWIM search order, the CAS semantics of <see cref="GitReferences.CreateMatchingAsync"/>,
/// or the public reflog append/ensure API. These tests close that gap without
/// Docker by building a local multi-commit repo and driving the full surface.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Like <see cref="Transports.LocalTransportTests"/>
/// and <see cref="Checkout.CheckoutIntegrationTests"/>, these run on every build.
/// </para>
/// </remarks>
public sealed class ReferenceIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Inits a non-bare repo, writes an initial commit on
    /// <c>refs/heads/main</c>, sets HEAD, and returns the repo (caller
    /// disposes via <c>await using</c>).
    /// </summary>
    private static async Task<GitRepository> InitRepoWithCommitAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await bld.WriteAsync(ct);
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

    /// <summary>Writes a second commit on top of the current HEAD tip and returns its OID.</summary>
    private static async Task<GitOid> WriteSecondCommitAsync(GitRepository repo, CancellationToken ct)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "world\n"u8.ToArray(), ct);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("world.txt", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await bld.WriteAsync(ct);
        var head = (GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!;
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [head.Target],
            Author = Sig,
            Committer = Sig,
            Message = "second\n",
            UpdateRef = "refs/heads/main",
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

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-refs-" + Guid.NewGuid().ToString("N"));

    // ── 1. DWIM search order ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitReferences.DwimAsync"/> resolves a bare branch short name
    /// by walking the DWIM candidate list. With <c>refs/heads/main</c> present,
    /// the shorthand <c>"main"</c> must resolve through the
    /// <c>refs/heads/{0}</c> candidate to the direct reference. Exercises the
    /// fallback loop in <see cref="GitReferences.DwimAsync"/> (multiple
    /// candidates tried, first hit returned).
    /// </summary>
    [Fact]
    public async Task Dwim_BranchShortName_ResolvesToHeadsRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitReference? resolved = await repo.ReferenceDwimAsync("main", ct);
            Assert.NotNull(resolved);
            Assert.False(resolved!.IsSymbolic);
            Assert.Equal("refs/heads/main", resolved.Name);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// DWIM resolves a tag short name through the <c>refs/tags/{0}</c>
    /// candidate (which comes before <c>refs/heads/{0}</c> in the search
    /// order). With both <c>refs/tags/v1</c> and <c>refs/heads/v1</c> present,
    /// <c>"v1"</c> must resolve to the tag — exercising the search-order
    /// precedence of tags over heads.
    /// </summary>
    [Fact]
    public async Task Dwim_TagAndBranchSameShortName_TagCandidateWins()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;

            // Create both refs/tags/v1 and refs/heads/v1 pointing at the same commit.
            await repo.ReferenceCreateAsync("refs/tags/v1", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/v1", commitOid, cancellationToken: ct);

            GitReference? resolved = await repo.ReferenceDwimAsync("v1", ct);
            Assert.NotNull(resolved);
            // refs/tags/{0} precedes refs/heads/{0} in s_dwimFormatters.
            Assert.Equal("refs/tags/v1", resolved!.Name);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// DWIM resolves the empty shorthand to <c>HEAD</c> (the
    /// <c>shorthand.Length == 0</c> branch uses <see cref="GitReferences.HeadFile"/>
    /// and disables fallback, so only the literal <c>HEAD</c> candidate is
    /// tried). On an unborn branch HEAD is a symbolic ref pointing at
    /// <c>refs/heads/main</c> which does not yet exist, so the resolved target
    /// is null but DWIM still returns the symbolic HEAD reference itself.
    /// </summary>
    [Fact]
    public async Task Dwim_EmptyShorthand_TargetsHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            // Init without committing → unborn branch, HEAD symbolic but target missing.
            await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);

            GitReference? resolved = await repo.ReferenceDwimAsync("", ct);
            // Empty shorthand → HEAD. HEAD exists (symbolic, unborn) so DwimAsync
            // returns it; ResolveAsync on HEAD yields null because the target ref
            // doesn't exist yet. DwimAsync returns null in that case.
            Assert.Null(resolved);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// DWIM returns null when no candidate matches a nonexistent short name,
    /// and does not throw (all candidates are valid names, so
    /// <see cref="GitErrorCode.InvalidSpec"/> is not raised).
    /// </summary>
    [Fact]
    public async Task Dwim_NoCandidateMatches_ReturnsNull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitReference? resolved = await repo.ReferenceDwimAsync("does-not-exist", ct);
            Assert.Null(resolved);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 2. Shorthand ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitReferences.Shorthand"/> strips the known <c>refs/</c>
    /// prefixes: <c>refs/heads/</c>, <c>refs/tags/</c>, <c>refs/remotes/</c>,
    /// and bare <c>refs/</c>. Unknown prefixes (and <c>HEAD</c>) are returned
    /// unchanged. Exercises all four prefix branches plus the fallback.
    /// </summary>
    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("refs/tags/v1.0", "v1.0")]
    [InlineData("refs/remotes/origin/main", "origin/main")]
    [InlineData("refs/notes/default", "notes/default")]
    [InlineData("HEAD", "HEAD")]
    [InlineData("weird/ref", "weird/ref")]
    public void Shorthand_StripsKnownPrefixes(string input, string expected)
    {
        Assert.Equal(expected, GitReferences.Shorthand(input));
    }

    // ── 3. CreateMatchingAsync (compare-and-swap) ───────────────────────

    /// <summary>
    /// <see cref="GitReferences.CreateMatchingAsync"/> creates a new ref when
    /// <paramref name="expectedOldId"/> is zero and the ref does not yet exist
    /// (the CAS "must not exist" precondition). Exercises the
    /// <c>existing is null</c> branch of the CAS check.
    /// </summary>
    [Fact]
    public async Task CreateMatching_RefAbsentAndExpectedZero_CreatesRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;

            GitReference created = await repo.ReferenceCreateMatchingAsync(
                "refs/heads/cas-feature", commitOid, expectedOldId: default, cancellationToken: ct);

            Assert.Equal(commitOid, ((GitDirectReference)created).Target);
            Assert.NotNull(await repo.ReferenceLookupAsync("refs/heads/cas-feature", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReferences.CreateMatchingAsync"/> updates an existing ref
    /// when its current value matches <paramref name="expectedOldId"/> (the CAS
    /// happy path: existing direct ref, expected OID equals current target).
    /// Requires <c>force: true</c> — C's <c>reference_path_available</c>
    /// (refdb_fs.c:1112-1126) fails a non-forced write with GIT_EEXISTS before
    /// the old-value check ever runs. Mirrors
    /// <c>test_refs_races__create_matching</c>
    /// (<c>cl_git_pass(create_matching(refname, other_id, 1, id))</c>).
    /// </summary>
    [Fact]
    public async Task CreateMatching_ExpectedMatchesCurrent_UpdatesRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid first = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            GitOid second = await WriteSecondCommitAsync(repo, ct);

            // The branch now points at `second`; CAS with expectedOldId = second.
            GitReference updated = await repo.ReferenceCreateMatchingAsync(
                "refs/heads/main", first, expectedOldId: second, force: true, cancellationToken: ct);

            Assert.Equal(first, ((GitDirectReference)updated).Target);
            GitReference? looked = await repo.ReferenceLookupAsync("refs/heads/main", ct);
            Assert.Equal(first, ((GitDirectReference)looked!).Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReferences.CreateMatchingAsync"/> throws
    /// <see cref="GitErrorCode.Modified"/> when the ref exists but its current
    /// value does not match <paramref name="expectedOldId"/>. Requires
    /// <c>force: true</c> — with force false, C fails with GIT_EEXISTS in
    /// <c>reference_path_available</c> before reaching the old-value check.
    /// Mirrors <c>test_refs_races__create_matching</c>
    /// (<c>cl_git_fail_with(GIT_EMODIFIED, create_matching(refname, other_id, 1, other_id))</c>).
    /// </summary>
    [Fact]
    public async Task CreateMatching_ExpectedMismatch_ThrowsModified()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid first = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            GitOid second = await WriteSecondCommitAsync(repo, ct);

            // main now at `second`; CAS expecting `first` (stale) must fail.
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.ReferenceCreateMatchingAsync(
                    "refs/heads/main", first, expectedOldId: first, force: true, cancellationToken: ct));
            Assert.Equal(GitErrorCode.Modified, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReferences.CreateMatchingAsync"/> throws
    /// <see cref="GitErrorCode.Modified"/> when the ref exists as a symbolic ref
    /// but a direct OID was expected (the symbolic-mismatch branch of
    /// <c>cmp_old_ref</c>, refdb_fs.c:1540-1573). Requires <c>force: true</c>
    /// for the same EEXISTS-first reason as above. HEAD is symbolic here, so
    /// CAS with a non-zero expectedOldId hits the symbolic-mismatch path.
    /// Mirrors <c>test_refs_races__symbolic_create_matching</c>
    /// (<c>cl_git_fail_with(GIT_EMODIFIED, symbolic_create_matching(..., 1, ...))</c>).
    /// </summary>
    [Fact]
    public async Task CreateMatching_ExistingSymbolicExpectedDirect_ThrowsModified()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;

            // HEAD is symbolic (→ refs/heads/main). CAS expecting it to be a
            // direct ref at commitOid must report Modified.
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.ReferenceCreateMatchingAsync(
                    "HEAD", commitOid, expectedOldId: commitOid, force: true, cancellationToken: ct));
            Assert.Equal(GitErrorCode.Modified, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 4. SetSymbolicTargetAsync ────────────────────────────────────────

    /// <summary>
    /// <see cref="GitReferences.SetSymbolicTargetAsync"/> repoints an existing
    /// symbolic reference at a new target and persists the change. HEAD starts
    /// symbolic at <c>refs/heads/main</c>; repointing it to
    /// <c>refs/heads/feature</c> must update both the in-memory result and the
    /// on-disk lookup. Exercises the symbolic write path through the backend.
    /// </summary>
    [Fact]
    public async Task SetSymbolicTarget_RepointsHeadToAnotherBranch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);

            GitReference head = (await repo.ReferenceLookupAsync("HEAD", ct))!;
            GitReference updated = await repo.ReferenceSetSymbolicTargetAsync(head, "refs/heads/feature", cancellationToken: ct);

            Assert.True(updated.IsSymbolic);
            Assert.Equal("refs/heads/feature", ((GitSymbolicReference)updated).TargetName);

            // On-disk lookup reflects the repoint.
            var reread = (GitSymbolicReference)(await repo.ReferenceLookupAsync("HEAD", ct))!;
            Assert.Equal("refs/heads/feature", reread.TargetName);

            // Resolving HEAD now yields the feature branch tip.
            var resolved = (GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!;
            Assert.Equal(commitOid, resolved.Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 5. Public reflog write API: EnsureLog + AppendReflog ─────────────

    /// <summary>
    /// <see cref="GitReferences.EnsureLogAsync"/> creates an empty reflog file
    /// for a ref name that the <c>core.logallrefupdates</c> policy permits
    /// (a <c>refs/heads/*</c> name) but for which no ref yet exists — so no log
    /// was written on ref creation. After ensuring the log,
    /// <see cref="GitReferences.HasLogAsync"/> returns true and
    /// <see cref="GitReferences.ReadLogAsync"/> returns an empty (zero-entry)
    /// reflog. Exercises the ensure + has-log + read-empty path. (Tags are
    /// gated out of the default policy, so a heads name is used instead.)
    /// </summary>
    [Fact]
    public async Task EnsureLog_OnHeadsName_CreatesEmptyLog()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            // "refs/heads/never-created" passes the logallrefupdates policy
            // (heads/* is always eligible) but no ref exists there, so no log
            // file has been written yet.
            const string refName = "refs/heads/never-created";
            Assert.False(await repo.Refs.HasLogAsync(refName, ct));

            await repo.Refs.EnsureLogAsync(refName, ct);

            Assert.True(await repo.Refs.HasLogAsync(refName, ct));
            GitRefLog? log = await repo.ReferenceReadLogAsync(refName, ct);
            Assert.NotNull(log);
            Assert.Equal(0, log!.EntryCount);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReferences.AppendReflogAsync"/> writes a single entry to
    /// the reflog for a ref. After ensuring the log on a heads ref name (which
    /// passes the <c>core.logallrefupdates</c> policy), appending an entry must
    /// produce a one-entry reflog whose most-recent entry (index 0) carries the
    /// appended old/new OIDs, committer, and message. Exercises the public
    /// append + read-back path.
    /// </summary>
    [Fact]
    public async Task AppendReflog_AfterEnsureLog_AddsEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            const string refName = "refs/heads/never-created";
            await repo.Refs.EnsureLogAsync(refName, ct);

            GitSignature committer = new("reflog", "reflog@example.com", new GitTime(1700000099, 0));
            await repo.Refs.AppendReflogAsync(
                refName, oldId: default, newId: commitOid, committer, "ref created", ct);

            GitRefLog? log = await repo.ReferenceReadLogAsync(refName, ct);
            Assert.NotNull(log);
            Assert.Equal(1, log!.EntryCount);

            GitRefLogEntry entry = log[0];
            Assert.Equal(commitOid, entry.NewId);
            Assert.True(entry.OldId.IsZero);
            Assert.Equal("reflog", entry.Committer.Name);
            Assert.Equal("ref created", entry.Message);
            // The computed Algorithm getter derives from NewId when OldId is
            // zero — exercising GitRefLogEntry.Algorithm's ternary branch.
            Assert.Equal(commitOid.Algorithm, entry.Algorithm);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 6. Branch lookup + iteration ─────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.BranchLookupAsync"/> finds a local branch by
    /// short name, returns null for a missing branch, and
    /// <see cref="GitRepository.BranchForEachAsync"/> enumerates the local
    /// branches with their <see cref="GitBranchType"/>. Exercises the
    /// repo-level branch facade over <see cref="GitReferences.LookupAsync"/>.
    /// </summary>
    [Fact]
    public async Task BranchLookup_AndForEach_EnumerateLocalBranches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);

            GitReference? main = await repo.BranchLookupAsync("main", GitBranchType.Local, ct);
            Assert.NotNull(main);
            Assert.Equal("refs/heads/main", main!.Name);

            GitReference? missing = await repo.BranchLookupAsync("nope", GitBranchType.Local, ct);
            Assert.Null(missing);

            var branches = new List<(GitReference Branch, GitBranchType Type)>();
            await foreach ((GitReference b, GitBranchType t) in repo.BranchForEachAsync(GitBranchType.Local, ct))
            {
                branches.Add((b, t));
            }

            Assert.Equal(2, branches.Count);
            Assert.All(branches, x => Assert.Equal(GitBranchType.Local, x.Type));
            Assert.Contains(branches, x => x.Branch.Name == "refs/heads/main");
            Assert.Contains(branches, x => x.Branch.Name == "refs/heads/feature");
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 7. BranchCreateAsync ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.BranchCreateAsync"/> creates a branch at a
    /// commit, refuses to overwrite without <c>force</c>, and overwrites with
    /// <c>force</c> (unless the branch is the current HEAD). Exercises the
    /// repo-level branch-create facade including the
    /// <see cref="GitReference.BranchNameIsValid"/> guard and the
    /// "branch: Created from" reflog message.
    /// </summary>
    [Fact]
    public async Task BranchCreate_ThenForceOverwrite_WritesReflogMessage()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid first = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            GitOid second = await WriteSecondCommitAsync(repo, ct);

            GitReference b1 = await repo.BranchCreateAsync("topic", first, cancellationToken: ct);
            Assert.Equal("refs/heads/topic", b1.Name);
            Assert.Equal(first, ((GitDirectReference)b1).Target);

            // Re-create without force throws Exists.
            await Assert.ThrowsAsync<GitException>(async () =>
                await repo.BranchCreateAsync("topic", second, cancellationToken: ct));

            // Force overwrites (topic is not HEAD).
            GitReference b2 = await repo.BranchCreateAsync("topic", second, force: true, cancellationToken: ct);
            Assert.Equal(second, ((GitDirectReference)b2).Target);

            // The "branch: Created from" reflog message is written.
            GitRefLog? log = await repo.ReferenceReadLogAsync("refs/heads/topic", ct);
            Assert.NotNull(log);
            Assert.True(log!.EntryCount >= 1);
            Assert.Contains("branch: Created from", log[0].Message);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.BranchCreateAsync"/> throws
    /// <see cref="GitErrorCode.InvalidSpec"/> for an invalid branch name (one
    /// that does not normalize under <c>refs/heads/</c>).
    /// </summary>
    [Fact]
    public async Task BranchCreate_InvalidName_ThrowsInvalidSpec()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;

            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.BranchCreateAsync("..invalid", commitOid, cancellationToken: ct));
            Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 8. IsHead / IsCheckedOut (with linked worktree) ──────────────────

    /// <summary>
    /// <see cref="GitReference.IsHeadAsync"/> returns true for the branch
    /// HEAD points at and false for another branch. Exercises the
    /// <see cref="GitReference.Branch.cs"/> read path that resolves HEAD and
    /// compares its name.
    /// </summary>
    [Fact]
    public async Task IsHead_CurrentBranch_True_OtherBranch_False()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);

            GitReference main = (await repo.ReferenceLookupAsync("refs/heads/main", ct))!;
            GitReference feature = (await repo.ReferenceLookupAsync("refs/heads/feature", ct))!;

            Assert.True(await main.IsHeadAsync(ct));
            Assert.False(await feature.IsHeadAsync(ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReference.IsCheckedOutAsync"/> returns true for a branch
    /// checked out in a linked worktree. After adding a worktree on a new
    /// branch <c>topic</c>, that branch reports checked-out while the main
    /// branch (checked out in the main worktree) also reports true. Exercises
    /// the linked-worktree HEAD scan in
    /// <see cref="GitReference.IsCheckedOutAsync"/>.
    /// </summary>
    [Fact]
    public async Task IsCheckedOut_LinkedWorktreeBranch_ReturnsTrue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            // Add a linked worktree on a new branch "topic".
            string wtPath = Path.Combine(path, "wt-topic");
            LibGit2CS.Repository.Worktree wt = await repo.WorktreeAddAsync("topic", wtPath, cancellationToken: ct);

            GitReference topic = (await repo.ReferenceLookupAsync("refs/heads/topic", ct))!;
            Assert.True(await topic.IsCheckedOutAsync(ct));

            // The main branch is checked out in the main worktree.
            GitReference main = (await repo.ReferenceLookupAsync("refs/heads/main", ct))!;
            Assert.True(await main.IsCheckedOutAsync(ct));

            // A brand-new branch that no worktree points at is not checked out.
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/other", commitOid, cancellationToken: ct);
            GitReference other = (await repo.ReferenceLookupAsync("refs/heads/other", ct))!;
            Assert.False(await other.IsCheckedOutAsync(ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 9. Branch delete (refuses HEAD, removes config section) ─────────

    /// <summary>
    /// <see cref="GitReference.DeleteAsync"/> (the branch.c path) refuses to
    /// delete the current HEAD branch with <see cref="GitErrorCode.Invalid"/>,
    /// and successfully deletes a non-HEAD branch, also removing its
    /// <c>branch.&lt;name&gt;</c> config section. Exercises the IsHead guard
    /// and the config-section cleanup on branch delete.
    /// </summary>
    [Fact]
    public async Task BranchDelete_RefusesHead_DeletesOtherBranchAndConfig()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            // Write a branch.<name>.* config section to verify its removal.
            await repo.Config.SetStringAsync("branch.feature.remote", ".", ct);
            await repo.Config.SetStringAsync("branch.feature.merge", "refs/heads/main", ct);

            GitReference main = (await repo.ReferenceLookupAsync("refs/heads/main", ct))!;
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await main.DeleteAsync(ct));
            // C (branch.c:206-210): deleting the current HEAD branch returns
            // a bare -1 (GIT_ERROR) with class GIT_ERROR_REFERENCE.
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Reference, ex.Category);

            GitReference feature = (await repo.ReferenceLookupAsync("refs/heads/feature", ct))!;
            await feature.DeleteAsync(ct);

            Assert.Null(await repo.ReferenceLookupAsync("refs/heads/feature", ct));
            // The branch.<name>.* config entries are gone.
            Assert.Null(await repo.Config.GetStringAsync("branch.feature.remote", ct));
            Assert.Null(await repo.Config.GetStringAsync("branch.feature.merge", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 10. Branch move (rename) ────────────────────────────────────────

    /// <summary>
    /// <see cref="GitReference.MoveAsync"/> renames a local branch, carries
    /// its target over to the new name, renames the
    /// <c>branch.&lt;old&gt;</c> config section to
    /// <c>branch.&lt;new&gt;</c>, and repoints HEAD if HEAD was symbolic at
    /// the old branch. Exercises the branch.c move path plus the
    /// <see cref="GitReferences.UpdateHeadIfPointingAtAsync"/> hook.
    /// </summary>
    [Fact]
    public async Task BranchMove_RenamesRefConfigAndRepointsHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            // Create a branch "oldname" and set up a branch.<oldname>.* config.
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            GitReference oldBranch = await repo.ReferenceCreateAsync("refs/heads/oldname", commitOid, cancellationToken: ct);
            await repo.Config.SetStringAsync("branch.oldname.remote", ".", ct);
            await repo.Config.SetStringAsync("branch.oldname.merge", "refs/heads/main", ct);

            // Point HEAD at oldname so the rename must repoint HEAD too.
            await repo.SetHeadAsync("refs/heads/oldname", ct);

            GitReference renamed = await oldBranch.MoveAsync("newname", cancellationToken: ct);

            Assert.Equal("refs/heads/newname", renamed.Name);
            Assert.Equal(commitOid, ((GitDirectReference)renamed).Target);
            Assert.Null(await repo.ReferenceLookupAsync("refs/heads/oldname", ct));
            Assert.NotNull(await repo.ReferenceLookupAsync("refs/heads/newname", ct));

            // Config section renamed.
            Assert.Null(await repo.Config.GetStringAsync("branch.oldname.remote", ct));
            Assert.Equal(".", await repo.Config.GetStringAsync("branch.newname.remote", ct));

            // HEAD repointed at the new branch.
            var head = (GitSymbolicReference)(await repo.ReferenceLookupAsync("HEAD", ct))!;
            Assert.Equal("refs/heads/newname", head.TargetName);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReference.MoveAsync"/> throws
    /// <see cref="GitErrorCode.Invalid"/> when invoked on a non-branch ref
    /// (a tag). Exercises the <c>!IsBranch</c> guard.
    /// </summary>
    [Fact]
    public async Task BranchMove_OnTag_ThrowsInvalid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            GitReference tag = await repo.ReferenceCreateAsync("refs/tags/v1", commitOid, cancellationToken: ct);

            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await tag.MoveAsync("v2", cancellationToken: ct));
            // C (branch.c:48-54): not_a_local_branch returns -1 (GIT_ERROR)
            // with class GIT_ERROR_INVALID.
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 11. SetUpstream (local + remote) ─────────────────────────────────

    /// <summary>
    /// <see cref="GitReference.SetUpstreamAsync"/> with a local upstream
    /// branch writes <c>branch.&lt;name&gt;.remote = "."</c> and
    /// <c>branch.&lt;name&gt;.merge = &lt;upstream refname&gt;</c>. Exercises
    /// the local-upstream branch of <c>branch_set_upstream</c>.
    /// </summary>
    [Fact]
    public async Task SetUpstream_LocalBranch_WritesDotRemoteAndMergeRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/upstream", commitOid, cancellationToken: ct);

            GitReference feature = (await repo.ReferenceLookupAsync("refs/heads/feature", ct))!;
            await feature.SetUpstreamAsync("upstream", ct);

            Assert.Equal(".", await repo.Config.GetStringAsync("branch.feature.remote", ct));
            Assert.Equal("refs/heads/upstream", await repo.Config.GetStringAsync("branch.feature.merge", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReference.SetUpstreamAsync"/> with a remote-tracking
    /// upstream branch writes <c>branch.&lt;name&gt;.remote = &lt;remote&gt;</c>
    /// and <c>branch.&lt;name&gt;.merge</c> = the reverse-transformed refspec
    /// source (<c>refs/heads/&lt;branch&gt;</c>). Exercises the remote-upstream
    /// branch that parses the remote's fetch refspec and reverse-transforms
    /// the tracking ref name.
    /// </summary>
    [Fact]
    public async Task SetUpstream_RemoteTrackingBranch_ReverseTransformsFetchSpec()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;

            // Create the "origin" remote (writes remote.origin.url + the default
            // fetch refspec +refs/heads/*:refs/remotes/origin/*).
            await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", ct);

            // Create the local branch and the remote-tracking ref it will track.
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/remotes/origin/feature", commitOid, cancellationToken: ct);

            GitReference feature = (await repo.ReferenceLookupAsync("refs/heads/feature", ct))!;
            await feature.SetUpstreamAsync("origin/feature", ct);

            Assert.Equal("origin", await repo.Config.GetStringAsync("branch.feature.remote", ct));
            // Reverse-transform of refs/remotes/origin/feature through
            // +refs/heads/*:refs/remotes/origin/* → refs/heads/feature.
            Assert.Equal("refs/heads/feature", await repo.Config.GetStringAsync("branch.feature.merge", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReference.SetUpstreamAsync"/> with a null upstream unsets
    /// the <c>branch.&lt;name&gt;.remote</c> and <c>branch.&lt;name&gt;.merge</c>
    /// config entries. Exercises the unset branch.
    /// </summary>
    [Fact]
    public async Task SetUpstream_Null_UnsetsRemoteAndMerge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/upstream", commitOid, cancellationToken: ct);
            await repo.Config.SetStringAsync("branch.feature.remote", ".", ct);
            await repo.Config.SetStringAsync("branch.feature.merge", "refs/heads/upstream", ct);

            GitReference feature = (await repo.ReferenceLookupAsync("refs/heads/feature", ct))!;
            await feature.SetUpstreamAsync(null, ct);

            Assert.Null(await repo.Config.GetStringAsync("branch.feature.remote", ct));
            Assert.Null(await repo.Config.GetStringAsync("branch.feature.merge", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitReference.SetUpstreamAsync"/> throws
    /// <see cref="GitErrorCode.NotFound"/> when the named upstream branch
    /// cannot be found as either a local or remote branch. Exercises the
    /// missing-upstream guard.
    /// </summary>
    [Fact]
    public async Task SetUpstream_MissingUpstream_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);

            GitReference feature = (await repo.ReferenceLookupAsync("refs/heads/feature", ct))!;
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await feature.SetUpstreamAsync("no-such-branch", ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 12. BranchUpstreamName / BranchUpstream (read side) ─────────────

    /// <summary>
    /// <see cref="GitRepository.BranchUpstreamNameAsync"/> resolves the
    /// tracking ref name for a branch with a local upstream
    /// (<c>branch.&lt;name&gt;.remote = "."</c>): returns the merge value
    /// directly. <see cref="GitRepository.BranchUpstreamAsync"/> resolves it
    /// to a <see cref="GitReference"/>. Exercises the local-upstream read path
    /// (no refspec transform needed).
    /// </summary>
    [Fact]
    public async Task BranchUpstreamName_LocalUpstream_ReturnsMergeRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/upstream", commitOid, cancellationToken: ct);
            GitReference feature = (await repo.ReferenceLookupAsync("refs/heads/feature", ct))!;
            await feature.SetUpstreamAsync("upstream", ct);

            string upstreamName = await repo.BranchUpstreamNameAsync("refs/heads/feature", ct);
            Assert.Equal("refs/heads/upstream", upstreamName);

            GitReference? upstreamRef = await repo.BranchUpstreamAsync("refs/heads/feature", ct);
            Assert.NotNull(upstreamRef);
            Assert.Equal("refs/heads/upstream", upstreamRef!.Name);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.BranchUpstreamNameAsync"/> throws
    /// <see cref="GitErrorCode.NotFound"/> for a branch with no upstream
    /// configured. Exercises the missing-config guard.
    /// </summary>
    [Fact]
    public async Task BranchUpstreamName_NoUpstream_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.BranchUpstreamNameAsync("refs/heads/main", ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.BranchUpstreamRemoteAsync"/> and
    /// <see cref="GitRepository.BranchUpstreamMergeAsync"/> return the
    /// <c>branch.&lt;name&gt;.remote</c> and <c>branch.&lt;name&gt;.merge</c>
    /// config values respectively. Exercises the two config-read accessors.
    /// </summary>
    [Fact]
    public async Task BranchUpstreamRemoteAndMerge_ReadConfigValues()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/heads/upstream", commitOid, cancellationToken: ct);
            GitReference feature = (await repo.ReferenceLookupAsync("refs/heads/feature", ct))!;
            await feature.SetUpstreamAsync("upstream", ct);

            Assert.Equal(".", await repo.BranchUpstreamRemoteAsync("refs/heads/feature", ct));
            Assert.Equal("refs/heads/upstream", await repo.BranchUpstreamMergeAsync("refs/heads/feature", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 13. BranchRemoteName fallback ────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.BranchRemoteNameAsync"/> returns the
    /// configured <c>branch.&lt;name&gt;.remote</c> when set, and falls back
    /// to scanning remotes' fetch refspecs for a match when unset. Exercises
    /// both the config-hit and the refspec-scan fallback paths.
    /// </summary>
    [Fact]
    public async Task BranchRemoteName_ConfigHit_AndRefspecFallback()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", ct);
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/remotes/origin/feature", commitOid, cancellationToken: ct);

            // Config hit: branch.feature.remote is set explicitly.
            await repo.Config.SetStringAsync("branch.feature.remote", "origin", ct);
            Assert.Equal("origin", await repo.BranchRemoteNameAsync("refs/heads/feature", ct));

            // Fallback: clear the config entry; the refspec scan must still
            // match "origin" via its +refs/heads/*:refs/remotes/origin/* spec.
            await repo.Config.DeleteAsync("branch.feature.remote", ct);
            Assert.Equal("origin", await repo.BranchRemoteNameAsync("refs/heads/feature", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 14. GitTransaction (multi-ref commit) ───────────────────────────

    /// <summary>
    /// <see cref="GitReferences.BeginTransaction"/> locks multiple refs,
    /// queues <see cref="GitTransaction.SetTarget"/> operations, and
    /// <see cref="GitTransaction.CommitAsync"/> persists them atomically. Both
    /// refs must end up at their new targets. Exercises the multi-ref
    /// transaction commit path through the public
    /// <see cref="GitReferences.BeginTransaction"/> facade.
    /// </summary>
    [Fact]
    public async Task Transaction_TwoRefs_CommitsBoth()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid first = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            GitOid second = await WriteSecondCommitAsync(repo, ct);

            await using GitTransaction txn = repo.NewReferenceTransaction();
            txn.LockRef("refs/heads/alpha");
            txn.LockRef("refs/heads/beta");
            txn.SetTarget("refs/heads/alpha", second, "create alpha");
            txn.SetTarget("refs/heads/beta", first, "create beta");
            await txn.CommitAsync(ct);

            Assert.Equal(second, ((GitDirectReference)(await repo.ReferenceLookupAsync("refs/heads/alpha", ct))!).Target);
            Assert.Equal(first, ((GitDirectReference)(await repo.ReferenceLookupAsync("refs/heads/beta", ct))!).Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Disposing a <see cref="GitTransaction"/> without committing rolls back
    /// all queued operations: no ref is created. Exercises the rollback path
    /// of <see cref="GitTransaction.DisposeAsync"/>.
    /// </summary>
    [Fact]
    public async Task Transaction_DisposeWithoutCommit_RollsBack()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;

            await RunTransactionWithoutCommitAsync(repo, commitOid).DisposeAsync();

            Assert.Null(await repo.ReferenceLookupAsync("refs/heads/rolled", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Runs a transaction that locks and sets a ref but does not commit,
    /// returning the live transaction so the caller can dispose it (and
    /// observe the rollback). Isolated so the rollback test does not embed the
    /// <c>await using</c> pattern that would commit on scope exit.
    /// </summary>
    private static GitTransaction RunTransactionWithoutCommitAsync(
        GitRepository repo, GitOid commitOid)
    {
        GitTransaction txn = repo.NewReferenceTransaction();
        txn.LockRef("refs/heads/rolled");
        txn.SetTarget("refs/heads/rolled", commitOid, "would-be-created");
        return txn;
    }

    // ── 15. Symbolic reference TargetAsync ───────────────────────────────

    /// <summary>
    /// <see cref="GitSymbolicReference.TargetAsync"/> resolves the symbolic
    /// ref's target to a <see cref="GitReference"/>. With HEAD →
    /// refs/heads/main, <c>((GitSymbolicReference)head).TargetAsync</c>
    /// returns the <c>refs/heads/main</c> direct reference. Exercises the
    /// symbolic-target resolution accessor.
    /// </summary>
    [Fact]
    public async Task SymbolicTarget_ResolvesToDirectRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            var head = (GitSymbolicReference)(await repo.ReferenceLookupAsync("HEAD", ct))!;
            GitReference? target = await head.TargetAsync(ct);
            Assert.NotNull(target);
            Assert.False(target!.IsSymbolic);
            Assert.Equal("refs/heads/main", target.Name);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitSymbolicReference.TargetAsync"/> returns null when the
    /// symbolic ref points at a ref that does not exist (unborn branch). HEAD
    /// on a freshly-init repo with no commits points at refs/heads/main which
    /// does not yet exist. Exercises the dangling-target branch.
    /// </summary>
    [Fact]
    public async Task SymbolicTarget_Dangling_ReturnsNull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
            var head = (GitSymbolicReference)(await repo.ReferenceLookupAsync("HEAD", ct))!;
            GitReference? target = await head.TargetAsync(ct);
            Assert.Null(target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 16. ShortName on branch ──────────────────────────────────────────

    /// <summary>
    /// <see cref="GitReference.ShortName"/> returns the short name for a local
    /// branch (<c>refs/heads/main</c> → <c>main</c>) and for a remote-tracking
    /// branch (<c>refs/remotes/origin/main</c> → <c>origin/main</c>), and
    /// throws <see cref="GitErrorCode.InvalidSpec"/> for a non-branch ref.
    /// Exercises both the IsBranch and IsRemote branches plus the throw.
    /// </summary>
    [Fact]
    public async Task ShortName_LocalAndRemoteBranch_ReturnsShortName()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/remotes/origin/main", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/tags/v1", commitOid, cancellationToken: ct);

            GitReference local = (await repo.ReferenceLookupAsync("refs/heads/main", ct))!;
            GitReference remote = (await repo.ReferenceLookupAsync("refs/remotes/origin/main", ct))!;
            Assert.Equal("main", local.ShortName());
            Assert.Equal("origin/main", remote.ShortName());

            GitReference tag = (await repo.ReferenceLookupAsync("refs/tags/v1", ct))!;
            GitException ex = Assert.Throws<GitException>(() => tag.ShortName());
            Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 17. ListNamesAsync glob ─────────────────────────────────────────

    /// <summary>
    /// <see cref="GitReferences.ListNamesAsync"/> with a glob filters ref
    /// names by the pattern (<c>*</c> does not cross <c>/</c>). With
    /// <c>refs/heads/main</c>, <c>refs/heads/feature</c>, and
    /// <c>refs/tags/v1</c> present, the glob <c>refs/heads/*</c> returns only
    /// the two heads. Exercises the name-enumeration glob path.
    /// </summary>
    [Fact]
    public async Task ListNames_WithGlob_FiltersByPattern()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithCommitAsync(path, ct);
        try
        {
            GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: ct);
            await repo.ReferenceCreateAsync("refs/tags/v1", commitOid, cancellationToken: ct);

            var heads = new List<string>();
            await foreach (string name in repo.Refs.ListNamesAsync("refs/heads/*", ct))
            {
                heads.Add(name);
            }

            Assert.Equal(2, heads.Count);
            Assert.Contains("refs/heads/main", heads);
            Assert.Contains("refs/heads/feature", heads);
            Assert.DoesNotContain("refs/tags/v1", heads);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
