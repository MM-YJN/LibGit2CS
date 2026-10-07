using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Golden tests for local transport — ported from libgit2's
/// <c>tests/libgit2/network/remote/local.c</c> (524 lines, ~19 tests).
/// Uses <c>file://</c> URLs with <see cref="GitLocalTransport"/>.
/// </summary>
public sealed class RemoteLocalTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository? _sourceRepo;

    public RemoteLocalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RemoteLocal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_sourceRepo is not null)
        {
            await _sourceRepo.DisposeAsync();
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> CreateSourceRepo(string name, int branches = 1)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitSignature sig = TestSig();
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });

        for (int i = 1; i < branches; i++)
        {
            await repo.ReferenceCreateAsync($"refs/heads/branch{i}", commitOid, force: true);
        }

        return repo;
    }

    private async ValueTask<GitRepository> CreateBareRepo(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
    }

    private static async ValueTask<GitRemote> CreateRemote(GitRepository clientRepo, string url)
    {
        return await clientRepo.RemoteCreateAsync("origin", url);
    }

    // ── connected ──────────────────────────────────────────────────────

    [Fact]
    public async Task Connected_ReturnsTrueAfterConnect()
    {
        _sourceRepo = await CreateSourceRepo("source");
        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        Assert.True(transport.IsConnected);
    }

    // ── retrieve_advertised_references ──────────────────────────────────

    [Fact]
    public async Task RetrieveAdvertisedReferences_ReturnsAllRefs()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 3);
        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.Contains(refs, r => r.Name == "HEAD");
        Assert.Contains(refs, r => r.Name == "refs/heads/master");
        Assert.Contains(refs, r => r.Name == "refs/heads/branch1");
        Assert.Contains(refs, r => r.Name == "refs/heads/branch2");
    }

    // ── retrieve_advertised_before_connect ─────────────────────────────

    [Fact]
    public async Task RetrieveAdvertisedBeforeConnect_Throws()
    {
        await using var transport = new GitLocalTransport(new GitContext());
        await Assert.ThrowsAsync<GitException>(async () => await transport.LsAsync(TestContext.Current.CancellationToken));
    }

    // ── retrieve_advertised_references_after_disconnect ───────────────

    [Fact]
    public async Task RetrieveAdvertisedReferencesAfterDisconnect_ReturnsCachedRefs()
    {
        _sourceRepo = await CreateSourceRepo("source");
        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        IReadOnlyList<GitRemoteHead> refsBefore = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(refsBefore);

        await transport.CloseAsync(TestContext.Current.CancellationToken);
        Assert.False(transport.IsConnected);

        // After disconnect, Ls() should still return cached refs
        IReadOnlyList<GitRemoteHead> refsAfter = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(refsAfter);
    }

    // ── nested_tags_are_completely_peeled ─────────────────────────────

    [Fact]
    public async Task NestedTagsAreCompletelyPeeled()
    {
        _sourceRepo = await CreateSourceRepo("source");

        // Create a nested tag (tag pointing to tag)
        GitOid commitOid = (await _sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference)!.Target;
        Commit commit = (await _sourceRepo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitOid tag1Oid = await _sourceRepo.TagCreateAnnotationAsync("v1.0", commit, TestSig(), "tag1\n", TestContext.Current.CancellationToken);

        // Create a tag pointing to the tag object
        GitTag tag1 = (await _sourceRepo.ObjectLookupAsync<GitTag>(tag1Oid, TestContext.Current.CancellationToken))!;
        await _sourceRepo.ReferenceCreateAsync("refs/tags/nested", tag1Oid, force: true, cancellationToken: TestContext.Current.CancellationToken);

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        // Should have refs/tags/nested
        Assert.Contains(refs, r => r.Name == "refs/tags/nested");
        // Should also have the peeled entry refs/tags/nested^{}
        Assert.Contains(refs, r => r.Name == "refs/tags/nested^{}");
    }

    // ── shorthand_fetch_refspec ───────────────────────────────────────

    [Fact]
    public async Task ShorthandFetchRefspec0_FetchesSpecificBranch()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 2);
        GitRepository clientRepo = await CreateBareRepo("client");

        GitRemote remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(refspecs: ["refs/heads/master:refs/remotes/origin/master"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");
        Assert.DoesNotContain(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");

        await clientRepo.DisposeAsync();
    }

    [Fact]
    public async Task ShorthandFetchRefspec1_FetchesAllBranches()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 2);
        GitRepository clientRepo = await CreateBareRepo("client");

        GitRemote remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(refspecs: ["refs/heads/*:refs/remotes/origin/*"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");
        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");

        await clientRepo.DisposeAsync();
    }

    // ── tagopt ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TagOpt_None_DoesNotFetchTags()
    {
        _sourceRepo = await CreateSourceRepo("source");

        GitOid commitOid = (await _sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference)!.Target;
        Commit commit = (await _sourceRepo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        await _sourceRepo.TagCreateAsync("v1.0", commit, TestSig(), "tag\n", cancellationToken: TestContext.Current.CancellationToken);

        GitRepository clientRepo = await CreateBareRepo("client");
        GitRemote remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(refspecs: ["+refs/heads/*:refs/remotes/origin/*"],
                      options: new GitFetchOptions { DownloadTags = GitAutoTagOption.None }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/tags/v1.0");

        await clientRepo.DisposeAsync();
    }

    // ── fetch ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_CopiesObjectsAndUpdatesRefs()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateBareRepo("client");

        GitRemote remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);
        await remote.DisposeAsync();

        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");

        // The commit object is visible in the client ODB WITHOUT a reopen —
        // DownloadPackAsync refreshed the pack backends. A missed lookup here
        // would leave the fetched commits invisible until reopen.
        GitOid sourceCommitOid = (await _sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference)!.Target;
        Assert.NotNull(await clientRepo.ObjectLookupAsync(sourceCommitOid, TestContext.Current.CancellationToken));

        await clientRepo.DisposeAsync();
    }

    // ── reflog ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reflog_WrittenOnFetch()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateBareRepo("client");

        // Enable reflog for bare repo
        await clientRepo.Config.SetStringAsync("core.logAllRefUpdates", "true", cancellationToken: TestContext.Current.CancellationToken);

        GitRemote remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(reflogMessage: "test fetch", cancellationToken: TestContext.Current.CancellationToken);

        // Check that a reflog entry was written for refs/remotes/origin/master
        GitRefLog? reflog = await clientRepo.ReferenceReadLogAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(reflog);
        var entries = reflog!.ToList();
        Assert.Single(entries);
        Assert.Contains("test fetch", entries[0].Message);

        await clientRepo.DisposeAsync();
    }

    // ── fetch_default_reflog_message ───────────────────────────────────

    [Fact]
    public async Task FetchDefaultReflogMessage_UsesFetchUrl()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateBareRepo("client");

        // Enable reflog for bare repo
        await clientRepo.Config.SetStringAsync("core.logAllRefUpdates", "true", cancellationToken: TestContext.Current.CancellationToken);

        string url = FixtureLoader.TestFileUrl(_sourceRepo.Path);
        GitRemote remote = await CreateRemote(clientRepo, url);
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitRefLog? reflog = await clientRepo.ReferenceReadLogAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(reflog);
        var entries = reflog!.ToList();
        Assert.Single(entries);
        Assert.Contains("fetch", entries[0].Message, StringComparison.OrdinalIgnoreCase);

        await clientRepo.DisposeAsync();
    }

    // ── opportunistic_update ───────────────────────────────────────────

    [Fact]
    public async Task OpportunisticUpdate_UpdatesExistingRef()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateBareRepo("client");

        // First fetch
        GitRemote remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid oldOid = (await clientRepo.ReferenceResolveAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference)!.Target;

        // Create a second commit on the source AFTER the first fetch
        GitOid blobOid = await _sourceRepo.ObjectWriteAsync(GitObjectType.Blob, "v2\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = _sourceRepo.NewTreeBuilder();
        await treeBld.InsertAsync("file2.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        GitOid parentOid = (await _sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference)!.Target;
        GitOid commit2 = await _sourceRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parentOid],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "second\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Second fetch — should update
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid newOid = (await clientRepo.ReferenceResolveAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference)!.Target;

        Assert.NotEqual(oldOid, newOid);
        Assert.Equal(commit2, newOid);

        await clientRepo.DisposeAsync();
    }

    // ── update_tips_for_new_remote ─────────────────────────────────────

    [Fact]
    public async Task UpdateTipsForNewRemote_CreatesTrackingRefs()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 2);
        GitRepository clientRepo = await CreateBareRepo("client");

        GitRemote remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");
        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");

        await clientRepo.DisposeAsync();
    }

    // ── sha256_oid_type ───────────────────────────────────────────────

    [Fact]
    public async Task OidType_FromSourceRepo_Sha1()
    {
        _sourceRepo = await CreateSourceRepo("source");

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.Equal(GitHashAlgorithmKind.Sha1, transport.OidType);
    }

    // ── anonymous_remote_inmemory_repo ────────────────────────────────

    [Fact]
    public async Task AnonymousRemote_WorksInMemory()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateBareRepo("client");

        // Create an anonymous remote (no name, no config)
        var remote = new GitRemote(clientRepo, null, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(refspecs: ["refs/heads/*:refs/remotes/origin/*"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");

        await remote.DisposeAsync();
        await clientRepo.DisposeAsync();
    }

    // ── push golden tests (ported from remote/local.c) ───────────────────

    [Fact]
    public async Task Push_ToBareRemote_Succeeds()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(_sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));
        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
        await targetRepo.DisposeAsync();
    }

    [Fact]
    public async Task Push_ToNonBareRemote_Throws()
    {
        _sourceRepo = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "nonbare_target");
        GitRepository targetRepo = await GitRepository.InitAsync(targetPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitRemote remote = await CreateRemote(_sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        await Assert.ThrowsAsync<GitException>(async () =>
            await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
        await targetRepo.DisposeAsync();
    }

    [Fact]
    public async Task Push_Delete_RemovesRefFromRemote()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository targetRepo = await CreateBareRepo("target");

        GitRemote remote = await CreateRemote(_sourceRepo, FixtureLoader.TestFileUrl(targetRepo.Path));

        // Push first
        await remote.PushAsync(["refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        // Delete
        await remote.PushAsync([":refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await targetRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        await remote.DisposeAsync();
        await targetRepo.DisposeAsync();
    }
}
