using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Remote;

public sealed class FetchCoordinatorTests
{
    private const string Sha1Main = "a5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string Sha1Tag = "c1c2c3c4c5c6c7c8c9d0e1f2a3b4c5d6e7f8a9b0";

    private static GitRemoteHead Head(string oid, string name, bool local = false)
        => new(local, GitOid.Parse(oid, GitHashAlgorithmKind.Sha1), default, name, null);

    private static GitRemoteHead Head(GitOid oid, string name, bool local = false)
        => new(local, oid, default, name, null);

    [Fact]
    public void MaybeWant_MatchingRefspec_ReturnsTrue()
    {
        GitRemoteHead head = Head(Sha1Main, "refs/heads/main");
        GitRefSpec[] refspecs = [GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true)];
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);

        bool want = FetchCoordinator.MaybeWant(head, refspecs, tagSpec, GitAutoTagOption.Auto);

        Assert.True(want);
    }

    [Fact]
    public void MaybeWant_NonMatchingRefspec_ReturnsFalse()
    {
        GitRemoteHead head = Head(Sha1Main, "refs/heads/main");
        GitRefSpec[] refspecs = [GitRefSpec.Parse("refs/heads/other:refs/remotes/origin/other", isFetch: true)];
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);

        bool want = FetchCoordinator.MaybeWant(head, refspecs, tagSpec, GitAutoTagOption.Auto);

        Assert.False(want);
    }

    [Fact]
    public void MaybeWant_TagWithAllPolicy_ReturnsTrue()
    {
        GitRemoteHead head = Head(Sha1Tag, "refs/tags/v1.0");
        GitRefSpec[] refspecs = Array.Empty<GitRefSpec>();
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);

        bool want = FetchCoordinator.MaybeWant(head, refspecs, tagSpec, GitAutoTagOption.All);

        Assert.True(want);
    }

    [Fact]
    public void MaybeWant_TagWithAutoPolicy_NoMatchingRefspec_ReturnsFalse()
    {
        GitRemoteHead head = Head(Sha1Tag, "refs/tags/v1.0");
        GitRefSpec[] refspecs = Array.Empty<GitRefSpec>();
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);

        bool want = FetchCoordinator.MaybeWant(head, refspecs, tagSpec, GitAutoTagOption.Auto);

        Assert.False(want);
    }

    [Fact]
    public void MaybeWant_EmptyName_ReturnsFalse()
    {
        GitRemoteHead head = Head(Sha1Main, "");
        GitRefSpec[] refspecs = [GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true)];
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);

        bool want = FetchCoordinator.MaybeWant(head, refspecs, tagSpec, GitAutoTagOption.Auto);

        Assert.False(want);
    }

    [Fact]
    public async Task FilterWants_AllHeadsLocal_LeavesNeedPackFalse()
    {
        await using TempRepo t = await TempRepoWithCommitAsync();
        GitRemoteHead head = Head(t.CommitOid, "refs/heads/main");
        GitRefSpec[] refspecs = [GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true)];

        var remote = new GitRemote(t.Repo, "origin", "file:///dummy");
        List<GitRemoteHead> wants = await FetchCoordinator.FilterWantsAsync(
            new[] { head }, refspecs, GitAutoTagOption.Auto, remote, depth: 0, GitRemoteCapability.None, TestContext.Current.CancellationToken);

        Assert.False(remote.NeedPack);
        Assert.Single(wants);
        Assert.True(wants[0].Local);
    }

    [Fact]
    public async Task FilterWants_HeadNotLocal_SetsNeedPackTrue()
    {
        await using TempRepo t = await TempRepoWithCommitAsync();
        // A remote head advertising an OID we do not have locally.
        var unknownOid = GitOid.Parse("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", GitHashAlgorithmKind.Sha1);
        GitRemoteHead head = Head(unknownOid, "refs/heads/main");
        GitRefSpec[] refspecs = [GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true)];

        var remote = new GitRemote(t.Repo, "origin", "file:///dummy");
        List<GitRemoteHead> wants = await FetchCoordinator.FilterWantsAsync(
            new[] { head }, refspecs, GitAutoTagOption.Auto, remote, depth: 0, GitRemoteCapability.None, TestContext.Current.CancellationToken);

        Assert.True(remote.NeedPack);
        Assert.Single(wants);
        Assert.False(wants[0].Local);
    }

    [Fact]
    public async Task FilterWants_ShallowFetch_AlwaysSetsNeedPackTrue()
    {
        await using TempRepo t = await TempRepoWithCommitAsync();
        // Even though we have the object locally, a shallow fetch (depth != 0)
        // must request the pack — mirrors mark_local's depth guard (fetch.c:66-70).
        GitRemoteHead head = Head(t.CommitOid, "refs/heads/main");
        GitRefSpec[] refspecs = [GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true)];

        var remote = new GitRemote(t.Repo, "origin", "file:///dummy");
        await FetchCoordinator.FilterWantsAsync(
            new[] { head }, refspecs, GitAutoTagOption.Auto, remote, depth: 1, GitRemoteCapability.None, TestContext.Current.CancellationToken);

        Assert.True(remote.NeedPack);
    }

    [Fact]
    public async Task Negotiate_AllLocal_DoesNotCallTransportNegotiate()
    {
        await using TempRepo t = await TempRepoWithCommitAsync();
        GitRemoteHead head = Head(t.CommitOid, "refs/heads/main");
        GitRefSpec[] refspecs = [GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true)];
        var transport = new TrackingTransport();
        var remote = new GitRemote(t.Repo, "origin", "file:///dummy");

        await FetchCoordinator.NegotiateAsync(
            transport, remote, new[] { head }, refspecs, GitAutoTagOption.Auto,
            depth: 0, Array.Empty<GitOid>(), TestContext.Current.CancellationToken);

        Assert.False(remote.NeedPack);
        Assert.False(transport.NegotiateFetchCalled);
    }

    [Fact]
    public async Task Negotiate_HeadNotLocal_CallsTransportNegotiate()
    {
        await using TempRepo t = await TempRepoWithCommitAsync();
        var unknownOid = GitOid.Parse("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", GitHashAlgorithmKind.Sha1);
        GitRemoteHead head = Head(unknownOid, "refs/heads/main");
        GitRefSpec[] refspecs = [GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true)];
        var transport = new TrackingTransport();
        var remote = new GitRemote(t.Repo, "origin", "file:///dummy");

        await FetchCoordinator.NegotiateAsync(
            transport, remote, new[] { head }, refspecs, GitAutoTagOption.Auto,
            depth: 0, Array.Empty<GitOid>(), TestContext.Current.CancellationToken);

        Assert.True(remote.NeedPack);
        Assert.True(transport.NegotiateFetchCalled);
    }

    [Fact]
    public async Task DownloadPack_NeedPackFalse_DoesNotCallTransportDownload()
    {
        await using TempRepo t = await TempRepoWithCommitAsync();
        var transport = new TrackingTransport();
        var remote = new GitRemote(t.Repo, "origin", "file:///dummy") { NeedPack = false };

        await FetchCoordinator.DownloadPackAsync(
            transport, remote, new GitIndexerProgress(), TestContext.Current.CancellationToken);

        Assert.False(transport.DownloadPackCalled);
    }

    [Fact]
    public async Task DownloadPack_NeedPackTrue_CallsTransportDownload()
    {
        await using TempRepo t = await TempRepoWithCommitAsync();
        var transport = new TrackingTransport();
        var remote = new GitRemote(t.Repo, "origin", "file:///dummy") { NeedPack = true };

        await FetchCoordinator.DownloadPackAsync(
            transport, remote, new GitIndexerProgress(), TestContext.Current.CancellationToken);

        Assert.True(transport.DownloadPackCalled);
    }

    [Fact]
    public void IWritePack_Interface_Exists()
    {
        // Verify the interface is available
        Assert.True(typeof(IGitWritePack).IsInterface);
    }

    [Fact]
    public void IndexerProgress_RecordsFields()
    {
        var progress = new GitIndexerProgress
        {
            ReceivedBytes = 1024,
            ReceivedObjects = 5,
            IndexedObjects = 3,
            LocalObjects = 1,
            TotalDeltas = 2,
            TotalObjects = 7,
            IndexedDeltas = 4,
        };

        Assert.Equal(1024, progress.ReceivedBytes);
        Assert.Equal(5, progress.ReceivedObjects);
        Assert.Equal(3, progress.IndexedObjects);
        Assert.Equal(1, progress.LocalObjects);
        Assert.Equal(2, progress.TotalDeltas);
        Assert.Equal(7, progress.TotalObjects);
        Assert.Equal(4, progress.IndexedDeltas);
    }

    [Fact]
    public void IndexerProgress_DefaultsToZero()
    {
        var progress = new GitIndexerProgress();

        Assert.Equal(0, progress.TotalObjects);
        Assert.Equal(0, progress.IndexedObjects);
        Assert.Equal(0, progress.ReceivedObjects);
        Assert.Equal(0, progress.LocalObjects);
        Assert.Equal(0, progress.TotalDeltas);
        Assert.Equal(0, progress.IndexedDeltas);
        Assert.Equal(0L, progress.ReceivedBytes);
    }

    [Fact]
    public void PackIndexer_ImplementsIWritePack()
    {
        Assert.True(typeof(IGitWritePack).IsAssignableFrom(typeof(GitPackIndexer)));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a temp repo with a single commit and returns the repo
    /// (disposable via <c>await using</c>) plus the commit OID.
    /// </summary>
    private static async ValueTask<TempRepo> TempRepoWithCommitAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_FC_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        GitRepository repo = await GitRepository.InitAsync(tempDir, isBare: false, new GitContext());
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync();

        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("README.md");
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();

        var sig = new GitSignature("Test User", "test@example.com", new GitTime(1700000000, 0));
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });

        return new TempRepo(repo, tempDir, commitOid);
    }

    private sealed class TempRepo : IAsyncDisposable
    {
        private readonly string _tempDir;

        public TempRepo(GitRepository repo, string tempDir, GitOid commitOid)
        {
            Repo = repo;
            _tempDir = tempDir;
            CommitOid = commitOid;
        }

        public GitRepository Repo { get; }
        public GitOid CommitOid { get; }

        public async ValueTask DisposeAsync()
        {
            await Repo.DisposeAsync();
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Minimal <see cref="IGitTransport"/> stub that records whether
    /// <see cref="NegotiateFetchAsync"/> / <see cref="DownloadPackAsync"/>
    /// were invoked, so tests can assert the up-to-date short-circuit.
    /// </summary>
    private sealed class TrackingTransport : IGitTransport
    {
        public bool NegotiateFetchCalled { get; private set; }
        public bool DownloadPackCalled { get; private set; }

        public GitRemoteCapability Capabilities => GitRemoteCapability.None;
        public GitHashAlgorithmKind OidType => GitHashAlgorithmKind.Sha1;
        public bool IsConnected => true;

        public Task ConnectAsync(string url, GitDirection direction, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void SetConnectOptions(GitRemoteConnectOptions? options) { }

        public Task<IReadOnlyList<GitRemoteHead>> LsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GitRemoteHead>>([]);

        public Task NegotiateFetchAsync(GitRepository repo, GitFetchNegotiation wants, CancellationToken cancellationToken)
        {
            NegotiateFetchCalled = true;
            return Task.CompletedTask;
        }

        public Task DownloadPackAsync(GitRepository repo, GitIndexerProgress stats, CancellationToken cancellationToken)
        {
            DownloadPackCalled = true;
            return Task.CompletedTask;
        }

        public ValueTask<IReadOnlyList<GitOid>> ShallowRootsAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<GitOid>>([]);

        public Task<GitPushResult> PushAsync(
            GitRepository repo, IReadOnlyList<GitPushSpec> specs, GitPackWriter? packWriter,
            GitRemoteCallbacks? callbacks, bool reportStatus, IReadOnlyList<string>? pushOptions,
            CancellationToken cancellationToken)
            => Task.FromResult(new GitPushResult { UnpackOk = true, Status = [] });

        public void Cancel() { }

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
