using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Objects;

/// <summary>
/// Integration tests for <see cref="GitTag"/> exercised end-to-end against
/// locally-initialized non-bare repos. Covers the full tag lifecycle:
/// create (annotated + lightweight), lookup, peel (single + nested chain +
/// type mismatch + tree/blob targets), list with pattern filtering, delete,
/// overwrite, and enumeration with cancellation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit-test project (<c>TagTests</c>,
/// <c>TagWriteTests</c>, <c>TagListEnumerateTests</c>) covers
/// <see cref="GitTag.Parse"/>, ODB-only writes, and the ref backend in
/// isolation. The only integration test that previously touched
/// <see cref="GitTag"/> was <c>SshTransportDockerTests.Fetch_MultiBranchAndAnnotatedTag</c>,
/// which requires Docker + SSH. These tests close the gap without Docker by
/// building a local multi-commit repo and exercising the tag surface through
/// <see cref="GitRepository"/> / <see cref="GitObjectDb"/> / <see cref="GitReferences"/>.
/// </para>
/// <para>
/// Like <see cref="Transports.LocalTransportTests"/> and
/// <see cref="Checkout.CheckoutIntegrationTests"/>, these tests are not
/// Docker-gated and run on every build.
/// </para>
/// </remarks>
public sealed class TagIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    private static GitSignature TaggerSig => new("Tagger", "tagger@example.com", new GitTime(1700000001, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-tag-" + Guid.NewGuid().ToString("N"));

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
    /// Initializes a non-bare repo, writes <c>hello.txt</c>, builds a tree,
    /// creates an initial commit on <c>refs/heads/main</c>, and sets HEAD.
    /// Returns the repo path, the commit OID, and the tree OID (the tree is
    /// needed by tag-on-tree tests).
    /// </summary>
    private static async Task<(string RepoPath, GitOid CommitOid, GitOid TreeOid)> InitRepoWithCommitAsync(CancellationToken ct)
    {
        string repoPath = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: ct);

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
        using GitTreeBuilder treeBld = new(repo);
        await treeBld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);

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
        return (repoPath, commitOid, treeOid);
    }

    /// <summary>
    /// Re-opens the repo at <paramref name="repoPath"/>. Tests that need to
    /// observe on-disk state after a write (e.g. a fresh tag ref) use this to
    /// get a clean repository handle rather than reusing the init-time one.
    /// </summary>
    private static async ValueTask<GitRepository> OpenAsync(string repoPath, CancellationToken ct)
        => await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: ct);

    /// <summary>Looks up a commit by OID, throwing if missing.</summary>
    private static async Task<Commit> LookupCommitAsync(GitRepository repo, GitOid oid, CancellationToken ct)
        => (await repo.ObjectLookupAsync<Commit>(oid, ct))
            ?? throw new InvalidOperationException($"commit {oid} not found");

    // ── 1. Annotated tag round-trip ───────────────────────────────────────

    /// <summary>
    /// Create an annotated tag on the initial commit via
    /// <see cref="GitTag.CreateAsync"/> (annotated path), then look it up by
    /// OID and by ref. Asserts all tag fields (Name/Target/TargetType/Tagger/
    /// Message) round-trip through the ODB write + <see cref="GitTag.Parse"/>
    /// path, and that <c>refs/tags/v1.0</c> points at the tag object OID.
    /// </summary>
    [Fact]
    public async Task CreateAnnotated_RoundTripsThroughLookup()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repoPath = NewRepoPath();
        try
        {
            GitOid commitOid;
            GitOid tagOid;
            await using (GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: ct))
            {
                GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
                using GitTreeBuilder treeBld = new(repo);
                await treeBld.InsertAsync("hello.txt", blobOid, GitFileMode.Regular, ct);
                GitOid treeOid = await treeBld.WriteAsync(ct);
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

                Commit commit = await LookupCommitAsync(repo, commitOid, ct);
                tagOid = await repo.TagCreateAsync("v1.0", commit, TaggerSig, "release v1.0\n", cancellationToken: ct);
            }

            // Re-open to prove the tag object + ref persisted to disk.
            await using GitRepository repo2 = await OpenAsync(repoPath, ct);
            GitTag? tag = await repo2.ObjectLookupAsync<GitTag>(tagOid, ct);
            Assert.NotNull(tag);
            Assert.Equal("v1.0", tag!.Name);
            Assert.Equal(commitOid, tag.Target);
            Assert.Equal(GitObjectType.Commit, tag.TargetType);
            Assert.Equal("Tagger", tag.Tagger!.Name);
            Assert.Equal("tagger@example.com", tag.Tagger!.Email);
            Assert.Equal(1700000001, tag.Tagger!.When.Seconds);
            Assert.Equal("release v1.0\n", tag.Message);

            // The ref must point at the tag object OID (not the commit OID).
            GitReference? tagRef = await repo2.ReferenceLookupAsync("refs/tags/v1.0", ct);
            Assert.NotNull(tagRef);
            Assert.False(tagRef!.IsSymbolic);
            Assert.Equal(tagOid, ((GitDirectReference)tagRef).Target);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 2. Lightweight tag ───────────────────────────────────────────────

    /// <summary>
    /// Create a lightweight tag (null tagger + null message) via
    /// <see cref="GitTag.CreateAsync"/>. The <c>refs/tags/*</c> ref must point
    /// directly at the target commit OID, and no tag object is written to the
    /// ODB (looking up the ref's target as a <see cref="GitTag"/> returns null).
    /// </summary>
    [Fact]
    public async Task CreateLightweight_RefPointsDirectlyAtTarget()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, GitOid commitOid, _) = await InitRepoWithCommitAsync(ct);
        try
        {
            await using GitRepository repo = await OpenAsync(repoPath, ct);
            Commit commit = await LookupCommitAsync(repo, commitOid, ct);

            GitOid returnedOid = await repo.TagCreateAsync("v1.0", commit, tagger: null, message: null, cancellationToken: ct);

            // Lightweight: returned OID is the target commit OID, not a tag object.
            Assert.Equal(commitOid, returnedOid);

            GitReference? tagRef = await repo.ReferenceLookupAsync("refs/tags/v1.0", ct);
            Assert.NotNull(tagRef);
            Assert.False(tagRef!.IsSymbolic);
            Assert.Equal(commitOid, ((GitDirectReference)tagRef).Target);

            // No tag object in the ODB — the commit OID doesn't parse as a
            // GitTag, so LookupAsync<GitTag> throws NotFound (not null) per
            // the documented contract of GitObjectDb.LookupAsync<T>. C returns
            // GIT_ENOTFOUND ("the requested type does not match the type in
            // the ODB") for a type-mismatched lookup.
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.ObjectLookupAsync<GitTag>(commitOid, ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 3. Peel annotated tag to commit ──────────────────────────────────

    /// <summary>
    /// <see cref="GitTag.PeelAsync{T}"/> with <c>T = Commit</c> on an annotated
    /// tag whose target is a commit returns the target commit. Exercises the
    /// happy path of the peel loop (depth 0, <c>current is T</c> on the first
    /// resolved target).
    /// </summary>
    [Fact]
    public async Task Peel_AnnotatedTag_ResolvesToCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, GitOid commitOid, _) = await InitRepoWithCommitAsync(ct);
        try
        {
            await using GitRepository repo = await OpenAsync(repoPath, ct);
            Commit commit = await LookupCommitAsync(repo, commitOid, ct);
            GitOid tagOid = await repo.TagCreateAsync("v1.0", commit, TaggerSig, "msg\n", cancellationToken: ct);

            GitTag? tag = await repo.ObjectLookupAsync<GitTag>(tagOid, ct);
            Assert.NotNull(tag);

            Commit peeled = await tag!.PeelAsync<Commit>(ct);
            Assert.Equal(commitOid, peeled.Id);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 4. Peel nested tag chain ─────────────────────────────────────────

    /// <summary>
    /// Build a tag-on-tag chain (tag2 -> tag1 -> commit) and peel tag2 to a
    /// commit. Exercises the <see cref="GitTag.PeelAsync{T}"/> loop with
    /// <c>depth &gt; 0</c>: the first resolved target is itself a
    /// <see cref="GitTag"/> (not <c>T</c>), so the loop iterates, resolves the
    /// next target, and only matches <c>T = Commit</c> on the second step.
    /// </summary>
    [Fact]
    public async Task Peel_NestedTagChain_ResolvesToCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, GitOid commitOid, _) = await InitRepoWithCommitAsync(ct);
        try
        {
            await using GitRepository repo = await OpenAsync(repoPath, ct);
            Commit commit = await LookupCommitAsync(repo, commitOid, ct);

            // tag1 -> commit
            GitOid tag1Oid = await repo.TagCreateAnnotationAsync("v1.0", commit, TaggerSig, "inner\n", ct);
            GitTag tag1 = (await repo.ObjectLookupAsync<GitTag>(tag1Oid, ct))!;

            // tag2 -> tag1 (annotated tag targeting another annotated tag)
            GitOid tag2Oid = await repo.TagCreateAnnotationAsync("v2.0", tag1, TaggerSig, "outer\n", ct);
            GitTag tag2 = (await repo.ObjectLookupAsync<GitTag>(tag2Oid, ct))!;

            Commit peeled = await tag2.PeelAsync<Commit>(ct);
            Assert.Equal(commitOid, peeled.Id);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 5. Peel type mismatch ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitTag.PeelAsync{T}"/> throws <see cref="GitErrorCode.Peel"/>
    /// when the tag's target chain ends at a type that isn't <c>T</c>. Here a
    /// tag targets a tree, and peeling to <c>Commit</c> must fail because a
    /// tree is a leaf (not a tag, not a commit) — the loop hits the
    /// "non-tag, non-T" break and falls through to the throw.
    /// </summary>
    [Fact]
    public async Task Peel_TargetTypeMismatch_ThrowsPeelError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, _, GitOid treeOid) = await InitRepoWithCommitAsync(ct);
        try
        {
            await using GitRepository repo = await OpenAsync(repoPath, ct);
            GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, ct))!;

            GitOid tagOid = await repo.TagCreateAnnotationAsync("tree-tag", tree, TaggerSig, "tag on tree\n", ct);
            GitTag tag = (await repo.ObjectLookupAsync<GitTag>(tagOid, ct))!;

            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await tag.PeelAsync<Commit>(ct));
            Assert.Equal(GitErrorCode.Peel, ex.Code);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 6. List with mixed annotated + lightweight ───────────────────────

    /// <summary>
    /// Create three tags (two annotated: <c>v1.0</c>, <c>v2.0</c>; one
    /// lightweight: <c>release-1</c>), then list with no pattern (returns all
    /// three) and with pattern <c>"v*"</c> (returns only the two
    /// <c>v*</c> tags). Exercises <see cref="GitTag.ListAsync"/> over both
    /// annotated and lightweight refs, and the
    /// <see cref="WildMatch.IsMatch"/> filtering path inside
    /// <see cref="GitTag.ListAsync"/>.
    /// </summary>
    [Fact]
    public async Task List_WithMixedAnnotatedAndLightweight_ReturnsAll()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, GitOid commitOid, _) = await InitRepoWithCommitAsync(ct);
        try
        {
            await using GitRepository repo = await OpenAsync(repoPath, ct);
            Commit commit = await LookupCommitAsync(repo, commitOid, ct);

            await repo.TagCreateAsync("v1.0", commit, TaggerSig, "v1\n", cancellationToken: ct);
            await repo.TagCreateAsync("v2.0", commit, TaggerSig, "v2\n", cancellationToken: ct);
            await repo.TagCreateAsync("release-1", commit, tagger: null, message: null, cancellationToken: ct);

            IReadOnlyList<string> all = await repo.TagListAsync(cancellationToken: ct);
            Assert.Equal(3, all.Count);
            Assert.Contains("v1.0", all);
            Assert.Contains("v2.0", all);
            Assert.Contains("release-1", all);

            IReadOnlyList<string> filtered = await repo.TagListAsync(pattern: "v*", cancellationToken: ct);
            Assert.Equal(2, filtered.Count);
            Assert.Contains("v1.0", filtered);
            Assert.Contains("v2.0", filtered);
            Assert.DoesNotContain("release-1", filtered);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 7. Delete ────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitTag.DeleteAsync"/> removes the <c>refs/tags/*</c>
    /// reference but leaves the tag object in the ODB (matching
    /// <c>git_tag_delete</c> semantics: delete only removes the ref, the
    /// object becomes unreachable but is not garbage-collected here).
    /// Asserts the ref is gone after delete, and that looking up the tag
    /// object by its OID still returns the parsed tag.
    /// </summary>
    [Fact]
    public async Task Delete_RemovesRefAndObjectLookupStillWorks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, GitOid commitOid, _) = await InitRepoWithCommitAsync(ct);
        try
        {
            GitOid tagOid;
            await using (GitRepository repo = await OpenAsync(repoPath, ct))
            {
                Commit commit = await LookupCommitAsync(repo, commitOid, ct);
                tagOid = await repo.TagCreateAsync("v1.0", commit, TaggerSig, "msg\n", cancellationToken: ct);
            }

            await using GitRepository repo2 = await OpenAsync(repoPath, ct);
            Assert.NotNull(await repo2.ReferenceLookupAsync("refs/tags/v1.0", ct));

            await repo2.TagDeleteAsync("v1.0", ct);

            Assert.Null(await repo2.ReferenceLookupAsync("refs/tags/v1.0", ct));

            // The tag object itself is still in the ODB (delete only drops the ref).
            GitTag? tag = await repo2.ObjectLookupAsync<GitTag>(tagOid, ct);
            Assert.NotNull(tag);
            Assert.Equal("v1.0", tag!.Name);

            // Deleting a non-existent tag throws NotFound.
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo2.TagDeleteAsync("nope", ct));
            Assert.Equal(GitErrorCode.NotFound, ex.Code);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 8. Overwrite ─────────────────────────────────────────────────────

    /// <summary>
    /// Creating a tag whose name already exists throws without
    /// <c>allowOverwrite</c>, and replaces the ref when
    /// <c>allowOverwrite = true</c>. Exercises the <c>force</c> parameter of
    /// <see cref="GitReferences.CreateAsync"/> as wired by
    /// <see cref="GitTag.CreateAsync"/>.
    /// </summary>
    [Fact]
    public async Task Create_WithAllowOverwrite_ReplacesExistingTag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, GitOid commitOid, _) = await InitRepoWithCommitAsync(ct);
        try
        {
            await using GitRepository repo = await OpenAsync(repoPath, ct);
            Commit commit = await LookupCommitAsync(repo, commitOid, ct);

            GitOid firstOid = await repo.TagCreateAsync("v1.0", commit, TaggerSig, "first\n", cancellationToken: ct);

            // Re-creating without overwrite throws (ref already exists).
            await Assert.ThrowsAsync<GitException>(async () =>
                await repo.TagCreateAsync("v1.0", commit, TaggerSig, "second\n", cancellationToken: ct));

            // With allowOverwrite: the ref is replaced with the new tag object OID.
            GitOid secondOid = await repo.TagCreateAsync("v1.0", commit, TaggerSig, "second\n", allowOverwrite: true, cancellationToken: ct);
            Assert.NotEqual(firstOid, secondOid);

            GitReference? tagRef = await repo.ReferenceLookupAsync("refs/tags/v1.0", ct);
            Assert.NotNull(tagRef);
            Assert.Equal(secondOid, ((GitDirectReference)tagRef!).Target);

            GitTag? tag = await repo.ObjectLookupAsync<GitTag>(secondOid, ct);
            Assert.NotNull(tag);
            Assert.Equal("second\n", tag!.Message);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 9. Tag on tree, peel to tree ──────────────────────────────────────

    /// <summary>
    /// An annotated tag targeting a tree: <see cref="GitTag.TargetType"/> is
    /// <see cref="GitObjectType.Tree"/>, and <see cref="GitTag.PeelAsync{T}"/>
    /// with <c>T = GitTree</c> returns the tree. Exercises the
    /// <c>T = GitTree</c> branch of <see cref="GitTag.PeelAsync{T}"/> (not hit
    /// by commit-only tests) and verifies
    /// <see cref="GitObjectDb.TypeToString"/> emits <c>"tree"</c> in the tag
    /// buffer's <c>type</c> header.
    /// </summary>
    [Fact]
    public async Task CreateAnnotated_OnTree_RoundTripsAndPeelsToTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, _, GitOid treeOid) = await InitRepoWithCommitAsync(ct);
        try
        {
            await using GitRepository repo = await OpenAsync(repoPath, ct);
            GitTree tree = (await repo.ObjectLookupAsync<GitTree>(treeOid, ct))!;

            GitOid tagOid = await repo.TagCreateAnnotationAsync("tree-tag", tree, TaggerSig, "tag on tree\n", ct);
            GitTag? tag = await repo.ObjectLookupAsync<GitTag>(tagOid, ct);
            Assert.NotNull(tag);
            Assert.Equal(GitObjectType.Tree, tag!.TargetType);
            Assert.Equal(treeOid, tag.Target);

            GitTree peeled = await tag.PeelAsync<GitTree>(ct);
            Assert.Equal(treeOid, peeled.Id);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 10. Tag on blob, peel to blob ─────────────────────────────────────

    /// <summary>
    /// An annotated tag targeting a blob: <see cref="GitTag.TargetType"/> is
    /// <see cref="GitObjectType.Blob"/>, and <see cref="GitTag.PeelAsync{T}"/>
    /// with <c>T = GitBlob</c> returns the blob. Exercises the
    /// <c>T = GitBlob</c> branch of <see cref="GitTag.PeelAsync{T}"/> and
    /// verifies the <c>"blob"</c> type string in the tag buffer.
    /// </summary>
    [Fact]
    public async Task CreateAnnotated_OnBlob_RoundTripsAndPeelsToBlob()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, _, _) = await InitRepoWithCommitAsync(ct);
        try
        {
            await using GitRepository repo = await OpenAsync(repoPath, ct);
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "payload\n"u8.ToArray(), ct);
            GitBlob blob = (await repo.ObjectLookupAsync<GitBlob>(blobOid, ct))!;

            GitOid tagOid = await repo.TagCreateAnnotationAsync("blob-tag", blob, TaggerSig, "tag on blob\n", ct);
            GitTag? tag = await repo.ObjectLookupAsync<GitTag>(tagOid, ct);
            Assert.NotNull(tag);
            Assert.Equal(GitObjectType.Blob, tag!.TargetType);
            Assert.Equal(blobOid, tag.Target);

            GitBlob peeled = await tag.PeelAsync<GitBlob>(ct);
            Assert.Equal(blobOid, peeled.Id);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }

    // ── 11. EnumerateAsync with cancellation ──────────────────────────────

    /// <summary>
    /// <see cref="GitTag.EnumerateAsync"/> propagates the
    /// <see cref="CancellationToken"/> to the underlying
    /// <see cref="GitReferences.ListNamesAsync"/> async enumerable (decorated
    /// with <c>[EnumeratorCancellation]</c>). With three tags present, an
    /// already-cancelled token must throw <see cref="OperationCanceledException"/>
    /// before yielding the first entry.
    /// </summary>
    [Fact]
    public async Task EnumerateAsync_WithCancellation_StopsEarly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string repoPath, GitOid commitOid, _) = await InitRepoWithCommitAsync(ct);
        try
        {
            await using GitRepository repo = await OpenAsync(repoPath, ct);
            Commit commit = await LookupCommitAsync(repo, commitOid, ct);
            await repo.TagCreateAsync("v1.0", commit, tagger: null, message: null, cancellationToken: ct);
            await repo.TagCreateAsync("v2.0", commit, tagger: null, message: null, cancellationToken: ct);
            await repo.TagCreateAsync("v3.0", commit, tagger: null, message: null, cancellationToken: ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await cts.CancelAsync();

            var entries = new List<(string Name, GitOid Oid)>();
            // The async enumeration surfaces cancellation as either
            // OperationCanceledException or its derived TaskCanceledException
            // depending on the underlying await point — ThrowsAnyAsync accepts both.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach ((string Name, GitOid Oid) e in repo.TagEnumerateAsync(cts.Token))
                {
                    entries.Add(e);
                }
            });
            Assert.Empty(entries);
        }
        finally
        {
            Cleanup(repoPath);
        }
    }
}
