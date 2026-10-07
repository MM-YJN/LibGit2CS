using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Repository;

/// <summary>
/// Integration tests for <see cref="Commit.CreateFromStageAsync"/> and
/// <see cref="Commit.AmendAsync"/> exercised end-to-end against
/// locally-initialized non-bare repos with a real on-disk workdir, index,
/// and HEAD symref resolved through the ref backend.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The existing unit tests
/// <c>LibGit2CS.UnitTests/Index/IndexWriterTests.cs</c>
/// (<c>CreateFromStage_*</c>) and
/// <c>LibGit2CS.UnitTests/Objects/CommitWriteTests.cs</c>
/// (<c>Amend_*</c>) cover these two APIs through minimal in-memory
/// object databases that bypass the filesystem index, the HEAD symref
/// chain, and the reflog. The integration paths — where the index is a
/// real on-disk file, HEAD is a real symref resolved through
/// <see cref="GitReferences.ResolveAsync"/>, the empty-commit rejection
/// diffs a real HEAD tree against the index, and the amended commit
/// updates HEAD with a reflog entry — were entirely cold in the default
/// (no-Docker) integration run. These tests close exactly those gaps.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/commit/create.c</c>
/// (<c>test_commit_create__from_stage_simple</c>,
/// <c>test_commit_create__from_stage_nochanges</c>,
/// <c>test_commit_create__from_stage_newrepo</c>) and
/// <c>tests/libgit2/object/commit/commitstagedfile.c</c>
/// (<c>test_object_commit_commitstagedfile__amend_commit</c>).
/// </para>
/// <para>
/// <b>Known parity gaps not covered here.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// libgit2's <c>git_commit_amend</c> rejects amending into a non-existent
/// ref with <c>GIT_ENOTFOUND</c> (the <c>lookup_resolved</c> failure at
/// <c>commit.c:370-379</c> is returned). The managed port resolves the ref
/// but proceeds when it is absent, so the ref is created by the
/// <see cref="GitReferences.CreateAsync(string, GitOid, bool, string?, CancellationToken)"/>
/// call with <c>force: true</c>. Amending a <em>stale</em> commit — an
/// existing tip that is not the commit being amended — does throw
/// <see cref="GitErrorCode.Modified"/>, matching C.
/// </description></item>
/// <item><description>
/// libgit2's <c>git_commit_create</c> with <c>update_ref = "HEAD"</c>
/// follows the HEAD symref and updates the underlying branch
/// (<c>refs/heads/main</c>). The managed port writes the direct ref
/// literally at <c>HEAD</c>, detaching it. To exercise the realistic
/// "commit on a branch" workflow, every test below passes
/// <c>UpdateRef = "refs/heads/main"</c> explicitly.
/// </description></item>
/// </list>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class CommitIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-commit-" + Guid.NewGuid().ToString("N"));

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
    /// Inits a non-bare repo, sets HEAD to <c>refs/heads/main</c> (unborn),
    /// stages <c>file.txt</c> with the given content, and returns the repo
    /// (caller disposes). No commit is written — the staged change is left
    /// in the index for <see cref="Commit.CreateFromStageAsync"/> to pick
    /// up.
    /// </summary>
    private static async Task<GitRepository> InitFreshRepoWithStagedFileAsync(string path, string content, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), content, ct);
        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("file.txt", ct);
        await idx.WriteAsync(ct);
        return repo;
    }

    /// <summary>
    /// Inits a non-bare repo, sets HEAD to <c>refs/heads/main</c>, and
    /// creates an initial root commit on <c>refs/heads/main</c> containing
    /// <c>file.txt</c> with the given content. Returns the repo (caller
    /// disposes). HEAD resolves to the new commit via the symref →
    /// <c>refs/heads/main</c>.
    /// </summary>
    private static async Task<GitRepository> InitRepoWithInitialCommitAsync(string path, string content, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), content, ct);
        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("file.txt", ct);
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
        return repo;
    }

    // ── 1. CreateFromStage ──────────────────────────────────────────────

    /// <summary>
    /// <see cref="Commit.CreateFromStageAsync"/> with a staged workdir
    /// change writes a new commit on <c>refs/heads/main</c> whose single
    /// parent is HEAD's previous tip, and HEAD (still symbolic to
    /// <c>refs/heads/main</c>) advances to the new commit. Exercises the
    /// full stage→<see cref="GitIndex.WriteTreeAsync"/>→HEAD-parent-resolve
    /// →commit pipeline against the on-disk index + ref backend.
    /// </summary>
    [Fact]
    public async Task CreateFromStage_StagedChange_CommitsWithHeadAsParent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithInitialCommitAsync(path, "base\n", ct);
        try
        {
            GitOid headBefore = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;

            // Stage a workdir change.
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), "modified\n", ct);
            GitIndex idx = await repo.GetIndexAsync(ct);
            await idx.AddByPathAsync("file.txt", ct);
            await idx.WriteAsync(ct);

            GitOid newCommit = await repo.CommitCreateFromStageAsync(new CommitCreateOptions
            {
                Author = Sig,
                Committer = Sig,
                Message = "stage\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            Assert.False(newCommit.IsZero);
            Assert.NotEqual(headBefore, newCommit);

            Commit c = (await repo.ObjectLookupAsync<Commit>(newCommit, ct))!;
            Assert.Single(c.Parents);
            Assert.Equal(headBefore, c.Parents[0]);
            Assert.Equal("stage\n", c.Message);

            // HEAD advanced to the new commit through the symref.
            GitOid headAfter = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            Assert.Equal(newCommit, headAfter);
            GitOid mainAfter = ((GitDirectReference)(await repo.ReferenceLookupAsync("refs/heads/main", ct))!).Target;
            Assert.Equal(newCommit, mainAfter);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="Commit.CreateFromStageAsync"/> on a repo whose index
    /// matches HEAD's tree (no staged changes) throws
    /// <see cref="GitErrorCode.Unchanged"/>. Exercises the empty-commit
    /// rejection path that diffs HEAD tree vs index via
    /// <see cref="LibGit2CS.Diff.GitDiff.TreeToIndexAsync"/>.
    /// </summary>
    [Fact]
    public async Task CreateFromStage_NoStagedChanges_ThrowsUnchanged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithInitialCommitAsync(path, "base\n", ct);
        try
        {
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.CommitCreateFromStageAsync(new CommitCreateOptions
                {
                    Author = Sig,
                    Committer = Sig,
                    Message = "should fail\n",
                }, ct));
            Assert.Equal(GitErrorCode.Unchanged, ex.Code);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="Commit.CreateFromStageAsync"/> with
    /// <see cref="CommitCreateOptions.AllowEmptyCommit"/> = true bypasses
    /// the empty-commit rejection and produces a commit even when the index
    /// matches HEAD's tree. Mirrors <c>git commit --allow-empty</c>. The
    /// new commit's OID differs from HEAD's previous commit because the
    /// message differs.
    /// </summary>
    [Fact]
    public async Task CreateFromStage_AllowEmptyCommit_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithInitialCommitAsync(path, "base\n", ct);
        try
        {
            GitOid headBefore = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;

            GitOid newCommit = await repo.CommitCreateFromStageAsync(new CommitCreateOptions
            {
                Author = Sig,
                Committer = Sig,
                Message = "empty\n",
                AllowEmptyCommit = true,
                UpdateRef = "refs/heads/main",
            }, ct);

            Assert.False(newCommit.IsZero);
            Assert.NotEqual(headBefore, newCommit);

            Commit c = (await repo.ObjectLookupAsync<Commit>(newCommit, ct))!;
            Assert.Equal("empty\n", c.Message);
            // Same tree as HEAD's previous commit (no staged change).
            Commit headBeforeCommit = (await repo.ObjectLookupAsync<Commit>(headBefore, ct))!;
            Assert.Equal(headBeforeCommit.Tree, c.Tree);
            Assert.Single(c.Parents);
            Assert.Equal(headBefore, c.Parents[0]);

            // HEAD advanced to the new commit.
            GitOid headAfter = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            Assert.Equal(newCommit, headAfter);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="Commit.CreateFromStageAsync"/> on a fresh repo with
    /// an unborn HEAD (symref to <c>refs/heads/main</c>, branch not yet
    /// created) produces a root commit (zero parents) and materializes
    /// <c>refs/heads/main</c>. Exercises the unborn-HEAD branch in
    /// <see cref="Commit.CreateFromStageAsync"/> where
    /// <see cref="GitReferences.ResolveAsync"/> returns a symref that does
    /// not resolve to a direct ref, leaving the parent list empty.
    /// </summary>
    [Fact]
    public async Task CreateFromStage_FreshRepo_CreatesRootCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitFreshRepoWithStagedFileAsync(path, "first\n", ct);
        try
        {
            GitOid rootOid = await repo.CommitCreateFromStageAsync(new CommitCreateOptions
            {
                Author = Sig,
                Committer = Sig,
                Message = "root\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            Assert.False(rootOid.IsZero);

            Commit root = (await repo.ObjectLookupAsync<Commit>(rootOid, ct))!;
            Assert.Empty(root.Parents);

            // refs/heads/main is now materialized and HEAD resolves through it.
            GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
            Assert.IsType<GitDirectReference>(head);
            Assert.Equal(rootOid, ((GitDirectReference)head!).Target);

            GitReference? mainRef = await repo.ReferenceLookupAsync("refs/heads/main", ct);
            Assert.NotNull(mainRef);
            Assert.Equal(rootOid, ((GitDirectReference)mainRef!).Target);

            // The reflog for refs/heads/main was birthed with the
            // "commit (initial):" prefix.
            GitRefLog? log = await repo.ReferenceReadLogAsync("refs/heads/main", ct);
            Assert.NotNull(log);
            Assert.True(log!.EntryCount >= 1);
            Assert.Equal(rootOid, log[0].NewId);
            Assert.StartsWith("commit (initial)", log[0].Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 2. Amend ────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Commit.AmendAsync"/> with a new message writes a new
    /// commit that inherits the original's tree/parents/author/committer,
    /// updates <c>refs/heads/main</c> (and HEAD through the symref) to the
    /// new commit, and appends a reflog entry whose <c>NewId</c> is the
    /// amended OID. Exercises the amend → <see cref="Commit.CreateAsync"/>
    /// → <see cref="GitReferences.CreateAsync"/> path end-to-end.
    /// </summary>
    [Fact]
    public async Task Amend_NewMessage_UpdatesHeadAndReflog()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithInitialCommitAsync(path, "base\n", ct);
        try
        {
            GitOid originalOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            Commit original = (await repo.ObjectLookupAsync<Commit>(originalOid, ct))!;

            GitOid amendedOid = await Commit.AmendAsync(
                original,
                message: "amended\n",
                updateRef: "refs/heads/main",
                cancellationToken: ct);

            Assert.False(amendedOid.IsZero);
            Assert.NotEqual(originalOid, amendedOid);

            Commit amended = (await repo.ObjectLookupAsync<Commit>(amendedOid, ct))!;
            Assert.Equal("amended\n", amended.Message);
            Assert.Equal(original.Tree, amended.Tree);
            Assert.Equal(original.Parents, amended.Parents);
            Assert.Equal(original.Author, amended.Author);
            Assert.Equal(original.Committer, amended.Committer);

            // HEAD advanced to the amended commit through the symref.
            GitOid headAfter = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            Assert.Equal(amendedOid, headAfter);

            // The reflog for refs/heads/main carries the amend with the
            // amended OID as NewId. The message starts with "commit".
            GitRefLog? log = await repo.ReferenceReadLogAsync("refs/heads/main", ct);
            Assert.NotNull(log);
            Assert.True(log!.EntryCount >= 2);
            Assert.Equal(amendedOid, log[0].NewId);
            Assert.Equal(originalOid, log[0].OldId);
            Assert.StartsWith("commit", log[0].Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="Commit.AmendAsync"/> with a new tree OID writes a
    /// commit whose tree differs from the original. Builds the new tree by
    /// adding a second file (<c>new.txt</c>) on top of the original's tree
    /// via <see cref="GitTreeBuilder"/>, then asserts the amended commit
    /// points at it and the tree carries two entries.
    /// </summary>
    [Fact]
    public async Task Amend_NewTree_UsesNewTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithInitialCommitAsync(path, "base\n", ct);
        try
        {
            GitOid originalOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            Commit original = (await repo.ObjectLookupAsync<Commit>(originalOid, ct))!;

            // Build a new tree = original tree + new.txt.
            GitTree originalTree = (await repo.ObjectLookupAsync<GitTree>(original.Tree, ct))!;
            GitOid newBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, "new\n"u8.ToArray(), ct);
            using GitTreeBuilder bld = repo.NewTreeBuilder(source: originalTree);
            await bld.InsertAsync("new.txt", newBlob, GitFileMode.Regular, ct);
            GitOid newTreeOid = await bld.WriteAsync(ct);
            Assert.NotEqual(original.Tree, newTreeOid);

            GitOid amendedOid = await Commit.AmendAsync(
                original,
                tree: newTreeOid,
                message: "amend-tree\n",
                updateRef: "refs/heads/main",
                cancellationToken: ct);

            Commit amended = (await repo.ObjectLookupAsync<Commit>(amendedOid, ct))!;
            Assert.Equal(newTreeOid, amended.Tree);

            GitTree amendedTree = (await repo.ObjectLookupAsync<GitTree>(amended.Tree, ct))!;
            Assert.Equal(2, amendedTree.EntryCount);
            Assert.NotNull(amendedTree["file.txt"]);
            Assert.NotNull(amendedTree["new.txt"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="Commit.AmendAsync"/> with new author and committer
    /// signatures writes a commit that carries the new signatures while
    /// keeping the original's tree, parents, and message. The amended
    /// commit's <see cref="Commit.Author"/> and <see cref="Commit.Committer"/>
    /// both reflect the overrides.
    /// </summary>
    [Fact]
    public async Task Amend_NewAuthorAndCommitter_RoundTrips()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await InitRepoWithInitialCommitAsync(path, "base\n", ct);
        try
        {
            GitOid originalOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", ct))!).Target;
            Commit original = (await repo.ObjectLookupAsync<Commit>(originalOid, ct))!;

            var newAuthor = new GitSignature("New Author", "new-author@example.com", new GitTime(1800000000, 0));
            var newCommitter = new GitSignature("New Committer", "new-committer@example.com", new GitTime(1800000099, 0));

            GitOid amendedOid = await Commit.AmendAsync(
                original,
                author: newAuthor,
                committer: newCommitter,
                updateRef: "refs/heads/main",
                cancellationToken: ct);

            Commit amended = (await repo.ObjectLookupAsync<Commit>(amendedOid, ct))!;
            Assert.Equal("New Author", amended.Author.Name);
            Assert.Equal("new-author@example.com", amended.Author.Email);
            Assert.Equal("New Committer", amended.Committer.Name);
            Assert.Equal("new-committer@example.com", amended.Committer.Email);

            // Tree, parents, and message inherited unchanged.
            Assert.Equal(original.Tree, amended.Tree);
            Assert.Equal(original.Parents, amended.Parents);
            Assert.Equal(original.RawMessage, amended.RawMessage);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
