using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Remote;

/// <summary>
/// Golden tests for push — ported from libgit2's
/// <c>tests/libgit2/network/remote/push.c</c> (115 lines, 2 tests) and
/// <c>tests/libgit2/network/remote/local.c</c> (4 push tests).
/// Uses <c>file://</c> URLs with <see cref="GitLocalTransport"/>.
/// </summary>
public sealed class PushTests : IDisposable
{
    private readonly string _tempDir;

    public PushTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PushTests_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> CreateSourceRepo(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });

        return repo;
    }

    private async ValueTask<GitRepository> CreateBareRepo(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
    }

    private async ValueTask<GitRepository> CreateNonBareRepo(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: false, new GitContext());
    }

    private static async ValueTask<GitRemote> CreateRemote(GitRepository clientRepo, string url)
    {
        return await clientRepo.RemoteCreateAsync("origin", url);
    }

    // ── Golden tests from push.c (2 tests) ──────────────────────────────

    [Fact]
    public async Task Delete_Notification_DeletesRefOnRemote()
    {
        // Ported from test_network_remote_push__delete_notification
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        // Create a remote pointing at the target
        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        // First push master to target
        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        // Verify the ref exists on target
        Assert.NotNull(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        // Now delete it with :refs/heads/master
        await remote.PushAsync([":refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        // Verify the ref is deleted
        Assert.Null(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Create_Notification_CreatesRefOnRemote()
    {
        // Ported from test_network_remote_push__create_notification
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        // Push a new branch
        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        // Verify the ref was created on the target
        Assert.NotNull(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
    }

    // ── Ported tests from remote/local.c ────────────────────────────────

    [Fact]
    public async Task Push_ToBareRemote_Succeeds()
    {
        // Ported from test_network_remote_local__push_to_bare_remote
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        // Verify the ref exists on the target
        Assert.NotNull(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Push_ToBareRemoteWithFileUrl_Succeeds()
    {
        // Ported from test_network_remote_local__push_to_bare_remote_with_file_url
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        // Use file:// URL
        string url = FixtureLoader.TestFileUrl(targetRepo.Path);
        GitRemote remote = await CreateRemote(sourceRepo, url);

        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Push_ToNonBareRemote_Throws()
    {
        // Ported from test_network_remote_local__push_to_non_bare_remote
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateNonBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        // Push to non-bare repo should fail with BareRepo error
        await Assert.ThrowsAsync<GitException>(async () => await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Push_Delete_RemovesRefFromRemote()
    {
        // Ported from test_network_remote_local__push_delete
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        // Push master first
        await remote.PushAsync(["refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        // Delete it
        await remote.PushAsync([":refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
    }

    // ── Additional push tests ───────────────────────────────────────────

    [Fact]
    public async Task Push_ForceRefspec_OverwritesRemoteRef()
    {
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        // Push master
        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);
        var originalOid = await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference;

        // Create a new commit in source (rewrite history)
        GitOid blobOid2 = await sourceRepo.ObjectWriteAsync(GitObjectType.Blob, "new content\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = sourceRepo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid2, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid2 = await treeBld.WriteAsync(CancellationToken.None);

        // Intentionally divergent commit — create without update_ref and move
        // the ref explicitly (C's git_commit_create refuses tip != parent).
        GitOid newCommitOid = await sourceRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid2,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "rewrite\n",
        }, cancellationToken: TestContext.Current.CancellationToken);
        await sourceRepo.ReferenceCreateAsync("refs/heads/master", newCommitOid, force: true, "commit: rewrite", cancellationToken: TestContext.Current.CancellationToken);

        // Force push (non-FF, but with +)
        await remote.PushAsync(["+refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        var updatedOid = await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference;
        Assert.NotNull(updatedOid);
        Assert.Equal(newCommitOid, updatedOid!.Target);

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Push_NonFastForward_Throws()
    {
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        // Push master
        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        // Create a divergent commit in source
        GitOid blobOid2 = await sourceRepo.ObjectWriteAsync(GitObjectType.Blob, "divergent\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = sourceRepo.NewTreeBuilder();
        await treeBld.InsertAsync("file.txt", blobOid2, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid2 = await treeBld.WriteAsync(CancellationToken.None);

        var originalCommit = await sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference;

        // Intentionally divergent commit — C's git_commit_create would refuse
        // to update the ref (tip != first parent), so create it without
        // update_ref and move the ref explicitly.
        GitOid divergent = await sourceRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid2,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "divergent\n",
        }, cancellationToken: TestContext.Current.CancellationToken);
        await sourceRepo.ReferenceCreateAsync("refs/heads/master", divergent, force: true, "commit: divergent", cancellationToken: TestContext.Current.CancellationToken);

        // Non-FF push without force should throw
        await Assert.ThrowsAsync<GitException>(async () => await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Push_MultipleRefs_AllSucceed()
    {
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        // Create a second branch in source
        var masterOid = await sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference;
        await sourceRepo.ReferenceCreateAsync("refs/heads/feature", masterOid!.Target, force: true, cancellationToken: TestContext.Current.CancellationToken);

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        await remote.PushAsync(["refs/heads/master:refs/heads/master", "refs/heads/feature:refs/heads/feature"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await targetRepo.ReferenceResolveAsync("refs/heads/feature", cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Push_PushOptions_Null_DoesNotThrow()
    {
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        // Push with no options should work
        GitPushResult result = await remote.PushAsync(["refs/heads/master:refs/heads/master"], options: null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk);
        Assert.Single(result.Status);
        Assert.True(result.Status[0].Ok);

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Push_Result_ContainsCorrectRefName()
    {
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        GitPushResult result = await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result.Status);
        Assert.Equal("refs/heads/master", result.Status[0].Ref);

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Upload_ConnectedRemote_ReturnsResult()
    {
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        // Connect manually
        await remote.ConnectAsync(GitDirection.Push, null, TestContext.Current.CancellationToken);

        GitPushResult result = await remote.UploadAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk);
        Assert.Single(result.Status);
        Assert.True(result.Status[0].Ok);

        await remote.DisconnectAsync(TestContext.Current.CancellationToken);
        await remote.DisposeAsync();
    }

    // ── Up-to-date push regression tests ────────────────────────────────

    /// <summary>
    /// Pushing a ref that is already up-to-date on the target must succeed
    /// without error. The git protocol requires sending an empty pack-file
    /// for create/update commands even when the server already has all
    /// objects (push.c:451-455). <see cref="Remote.PushCoordinator"/> must
    /// create a <see cref="Pack.GitPackWriter"/> (even empty) for non-delete
    /// specs so the transport can fulfil this requirement.
    /// </summary>
    [Fact]
    public async Task Push_AlreadyUpToDate_Succeeds()
    {
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        GitPushResult result = await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk);
        Assert.Single(result.Status);
        Assert.True(result.Status[0].Ok);

        await remote.DisposeAsync();
    }

    /// <summary>
    /// Pushing a mix of up-to-date and new refs must succeed — the
    /// <see cref="Pack.GitPackWriter"/> contains objects for the new ref
    /// and the up-to-date ref's objects are already on the target.
    /// </summary>
    [Fact]
    public async Task Push_MixedUpToDateAndNew_Succeeds()
    {
        await using GitRepository sourceRepo = await CreateSourceRepo("source");
        await using GitRepository targetRepo = await CreateBareRepo("target");

        var masterRef = await sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference;
        await sourceRepo.ReferenceCreateAsync("refs/heads/feature", masterRef!.Target, force: true, cancellationToken: TestContext.Current.CancellationToken);

        GitRemote remote = await CreateRemote(sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        GitPushResult result = await remote.PushAsync(
            ["refs/heads/master:refs/heads/master", "refs/heads/feature:refs/heads/feature"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk);
        Assert.Equal(2, result.Status.Count);
        Assert.All(result.Status, s => Assert.True(s.Ok));

        await remote.DisposeAsync();
    }
}
