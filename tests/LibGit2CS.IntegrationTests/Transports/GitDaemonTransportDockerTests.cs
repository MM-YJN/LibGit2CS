using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Docker-based integration tests for the <c>git://</c> transport
/// (<see cref="GitTransport"/> +
/// <see cref="GitStream"/> +
/// <see cref="GitSocket"/>): a real
/// <c>git daemon</c> in an Alpine container with a seeded server-side
/// repo. Exercises the wire protocol end-to-end: TCP connect, lazy
/// command send (<c>git-upload-pack</c>/<c>git-receive-pack</c>), ref
/// advertisement, clone, fetch, push, push-delete, and disconnect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests exist.</b> The <c>git://</c> transport had
/// <c>0%</c> integration coverage — no fixture stood up a
/// <c>git daemon</c>, so every code path in
/// <see cref="GitSocket"/> (TCP connect, NetworkStream
/// read/write, keepalive, disposal),
/// <see cref="GitStream"/> (lazy command send,
/// <c>GenerateProtocol</c> packet construction), and
/// <see cref="GitTransport"/> (URL host/port/path
/// parsing, stateful stream reuse across <c>Ls</c>+<c>UploadPack</c>
/// service calls, <c>CloseAsync</c>/<c>DisposeAsync</c>) was entirely
/// cold. The unit-test project covers URL parsing in isolation but never
/// drives the full smart-protocol negotiation + pack exchange over a real
/// socket. These tests close that gap.
/// </para>
/// <para>
/// <b>Gating.</b> All tests no-op when Docker is not reachable (socket on
/// Unix, named pipe on Windows) —
/// <see cref="GitDaemonImageFixture.StartContainerAsync"/> calls
/// <see cref="GitDaemonDockerFixture.SkipIfDockerNotAvailable"/> internally.
/// </para>
/// <para>
/// <b>No auth.</b> The <c>git://</c> protocol has no authentication
/// mechanism; the daemon serves any connection (subject to
/// <c>--export-all</c> and <c>--enable=receive-pack</c> in the image CMD).
/// Tests therefore omit <see cref="GitRemoteCallbacks"/> entirely — the
/// transport never invokes credential or certificate callbacks.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/online/clone.c</c> (clone over <c>git://</c>),
/// <c>tests/libgit2/network/remote/local.c</c>
/// (<c>test_network_remote_local__fetch</c>,
/// <c>test_network_remote_local__push_to_bare_remote</c>,
/// <c>test_network_remote_local__push_delete</c>), adapted to drive the
/// transport via <see cref="GitRemote"/> against a containerized
/// <c>git daemon</c> rather than an in-process local repo.
/// </para>
/// </remarks>
public sealed class GitDaemonTransportDockerTests : IDisposable
{
    private readonly LoggerFactory _loggerFactory;
    private readonly GitDaemonImageFixture _daemon;

    public GitDaemonTransportDockerTests(
        GitDaemonImageFixture daemon,
        ITestOutputHelper testOutputHelper)
    {
        _daemon = daemon;
        _loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);
    }

    public void Dispose() => _loggerFactory.Dispose();

    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>Allocates a fresh temp dir path for a cloned/init repo.</summary>
    private static string NewTempPath(string purpose)
        => Path.Combine(Path.GetTempPath(), "libgit2cs-gitd-" + purpose + "-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>The <c>git://</c> URL for the daemon's seeded repo, with the fixture's mapped port.</summary>
    private static string GitUrl(GitDaemonDockerContainer fixture)
        => $"git://{GitDaemonDockerFixture.Host}:{fixture.Port}/test.git";

    /// <summary>
    /// Adds a second commit (<c>second.txt</c>) to the server-side repo via
    /// <c>git</c> inside the container. Returns the new <c>refs/heads/main</c>
    /// OID as printed by <c>git rev-parse</c>.
    /// </summary>
    private static async Task<string> AdvanceServerRepoAsync(GitDaemonDockerContainer fixture, CancellationToken ct)
    {
        string oid = await fixture.ExecAsync(
            "cd " + GitDaemonDockerFixture.RepoPath + " && " +
            "git config user.email t@t && git config user.name T && " +
            "echo second > second.txt && git add second.txt && " +
            "git commit -m second && git rev-parse refs/heads/main", ct);
        return oid.Trim();
    }

    // ── Ls (ref advertisement) ─────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRemote.ConnectAsync"/> + <see cref="GitRemote.LsAsync"/>
    /// over <c>git://</c> returns the seeded <c>refs/heads/main</c> ref.
    /// Exercises <see cref="GitTransport.ActionAsync"/>
    /// with <see cref="GitSmartService.UploadPackLs"/>,
    /// the URL host/port parser (<see cref="GitTransport.OpenServiceStreamAsync"/>),
    /// <see cref="GitSocket"/> TCP connect, and the
    /// lazy command-send in <see cref="GitStream.EnsureCommandSentAsync"/>
    /// + <see cref="GitStream.GenerateProtocol"/>.
    /// </summary>
    [Fact]
    public async Task Ls_ListsMainBranch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitDaemonDockerContainer fixture = await _daemon.StartContainerAsync(_loggerFactory, ct);

        string url = GitUrl(fixture);
        string localPath = NewTempPath("ls");
        await using GitRepository repo = await GitRepository.InitAsync(localPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            await remote.ConnectAsync(GitDirection.Fetch, cancellationToken: ct);
            IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);

            // Assert BEFORE DisconnectAsync — disconnect clears the cached
            // heads (parity with libgit2's ResetStreamAsync clearing _refs).
            Assert.Contains(heads, h => h.Name == "refs/heads/main");
            Assert.Contains(heads, h => h.Name == "HEAD" && h.SymrefTarget == "refs/heads/main");

            await remote.DisconnectAsync(ct);
            Assert.False(remote.IsConnected);
        }
        finally
        {
            Cleanup(localPath);
        }
    }

    // ── Clone ─────────────────────────────────────────────────────────

    /// <summary>
    /// Clone via <c>git://host:port/test.git</c> end-to-end. Exercises
    /// the full smart-protocol fetch path: ref advertisement, capability
    /// negotiation, pack download via the reused
    /// <see cref="GitStream"/> (the
    /// <see cref="GitSmartService.UploadPack"/>
    /// service call returned by
    /// <see cref="GitTransport.ReuseStream"/>),
    /// pack indexing, ref update, and workdir checkout.
    /// </summary>
    [Fact]
    public async Task Clone_GitUrl_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitDaemonDockerContainer fixture = await _daemon.StartContainerAsync(_loggerFactory, ct);

        string url = GitUrl(fixture);
        string targetPath = NewTempPath("clone");
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, new GitCloneOptions(), new GitContext(), ct);

            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            Assert.IsType<GitDirectReference>(head);

            GitOid commitOid = ((GitDirectReference)head).Target;
            Assert.False(commitOid.IsZero);

            GitObject? obj = await cloned.ObjectLookupAsync(commitOid, ct);
            Assert.NotNull(obj);

            // The seeded repo has README.md on refs/heads/main; a non-bare
            // clone checks out the workdir.
            Assert.True(File.Exists(Path.Combine(targetPath, "README.md")));
            Assert.Equal("hello\n", await File.ReadAllTextAsync(Path.Combine(targetPath, "README.md"), ct));

            GitReference? tracking = await cloned.ReferenceResolveAsync("refs/remotes/origin/main", ct);
            Assert.NotNull(tracking);
            Assert.Equal(commitOid, ((GitDirectReference)tracking).Target);
        }
        finally
        {
            Cleanup(targetPath);
        }
    }

    // ── Fetch after server advance ────────────────────────────────────

    /// <summary>
    /// Clone once, advance the server-side repo by one commit (via
    /// <see cref="GitDaemonDockerContainer.ExecAsync"/>), then reconnect
    /// and <see cref="GitRemote.FetchAsync"/>. The local
    /// <c>refs/remotes/origin/main</c> must move to the new commit OID,
    /// and the new commit must be retrievable from the local object store.
    /// Exercises a fresh <c>git-upload-pack</c> negotiation on a second
    /// connection over the same daemon.
    /// </summary>
    [Fact]
    public async Task Fetch_AfterServerAdvance_UpdatesTrackingRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitDaemonDockerContainer fixture = await _daemon.StartContainerAsync(_loggerFactory, ct);

        string url = GitUrl(fixture);
        string targetPath = NewTempPath("fetch");
        try
        {
            // 1. Clone the seeded repo (one commit on main).
            GitOid originalTip;
            await using (GitRepository cloned = await GitClone.RunAsync(url, targetPath, new GitCloneOptions(), new GitContext(), ct))
            {
                GitReference? head = await cloned.ReferenceResolveAsync("refs/remotes/origin/main", ct);
                originalTip = ((GitDirectReference)head!).Target;
            }

            // 2. Server-side: add a second commit.
            string serverOid = await AdvanceServerRepoAsync(fixture, ct);

            // 3. Reopen and fetch.
            await using GitRepository repo = await GitRepository.OpenAsync(targetPath, new GitContext(), cancellationToken: ct);
            GitRemote remote = await repo.RemoteLookupAsync("origin", ct);
            await remote.FetchAsync(cancellationToken: ct);

            GitReference? tracking = await repo.ReferenceResolveAsync("refs/remotes/origin/main", ct);
            Assert.NotNull(tracking);
            GitOid newTip = ((GitDirectReference)tracking).Target;
            Assert.NotEqual(originalTip, newTip);

            // The fetched commit must be present in the local ODB and carry
            // the expected message.
            Commit? fetched = await repo.ObjectLookupAsync<Commit>(newTip, ct);
            Assert.NotNull(fetched);
            Assert.Equal("second\n", fetched!.Message);

            await remote.DisconnectAsync(ct);
        }
        finally
        {
            Cleanup(targetPath);
        }
    }

    // ── Push over git:// ──────────────────────────────────────────────

    /// <summary>
    /// Push over <c>git://</c>: clone, add a local commit, push to
    /// <c>refs/heads/main</c> on the daemon, then verify the server-side
    /// ref moved. Exercises <see cref="GitTransport.ActionAsync"/>
    /// with <see cref="GitSmartService.ReceivePackLs"/>
    /// + <see cref="GitSmartService.ReceivePack"/>
    /// (the daemon has <c>--enable=receive-pack</c> in its CMD) and the
    /// full pack-upload path.
    /// </summary>
    [Fact]
    public async Task Push_OverGitProtocol_UpdatesRemoteRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitDaemonDockerContainer fixture = await _daemon.StartContainerAsync(_loggerFactory, ct);

        string url = GitUrl(fixture);
        string targetPath = NewTempPath("push");
        try
        {
            // 1. Clone.
            GitOid originalTip;
            await using (GitRepository cloned = await GitClone.RunAsync(url, targetPath, new GitCloneOptions(), new GitContext(), ct))
            {
                GitReference? head = await cloned.ReferenceResolveAsync("refs/remotes/origin/main", ct);
                originalTip = ((GitDirectReference)head!).Target;
            }

            // 2. Reopen, add a second local commit on main.
            await using GitRepository repo = await GitRepository.OpenAsync(targetPath, new GitContext(), cancellationToken: ct);
            await File.WriteAllTextAsync(Path.Combine(targetPath, "pushed.txt"), "pushed\n", ct);

            GitIndex idx = await repo.GetIndexAsync(ct);
            await idx.AddByPathAsync("pushed.txt", ct);
            await idx.WriteAsync(ct);
            GitOid treeOid = await idx.WriteTreeAsync(ct);

            GitReference? mainHead = await repo.ReferenceResolveAsync("refs/heads/main", ct);
            GitOid parentOid = ((GitDirectReference)mainHead!).Target;
            GitOid pushedOid = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [parentOid],
                Author = Sig,
                Committer = Sig,
                Message = "pushed\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            // 3. Push.
            GitRemote remote = await repo.RemoteLookupAsync("origin", ct);
            GitPushResult result = await remote.PushAsync(["refs/heads/main:refs/heads/main"], new GitPushOptions(), reflogMessage: null, ct);

            Assert.True(result.UnpackOk,
                $"push UnpackOk=False; status=[{string.Join(", ", result.Status.Select(s => $"{(s.Ok ? "ok" : "ng")} {s.Ref}"))}]");
            Assert.Single(result.Status);
            Assert.True(result.Status[0].Ok);
            Assert.Equal("refs/heads/main", result.Status[0].Ref);

            await remote.DisconnectAsync(ct);

            // 4. Verify server-side: git rev-parse refs/heads/main must
            //    match the pushed OID.
            string serverOid = await fixture.ExecAsync(
                "cd " + GitDaemonDockerFixture.RepoPath + " && git rev-parse refs/heads/main", ct);
            Assert.Equal(pushedOid.ToString(), serverOid.Trim());
            Assert.NotEqual(originalTip.ToString(), serverOid.Trim());
        }
        finally
        {
            Cleanup(targetPath);
        }
    }

    // ── Push-delete ───────────────────────────────────────────────────

    /// <summary>
    /// Push-delete over <c>git://</c>: seed
    /// <c>refs/heads/feature</c> on the server by pushing it from the
    /// client, then delete it via the <c>:refs/heads/feature</c> refspec.
    /// Exercises <see cref="GitTransport.ActionAsync"/>
    /// with the receive-pack service and the delete-refspec dispatch in
    /// <see cref="PushCoordinator"/>.
    /// </summary>
    [Fact]
    public async Task Push_DeleteRemoteRef_RemovesRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitDaemonDockerContainer fixture = await _daemon.StartContainerAsync(_loggerFactory, ct);

        string url = GitUrl(fixture);
        string targetPath = NewTempPath("pushdel");
        try
        {
            // 1. Clone (gives us a local main + the origin remote).
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, new GitCloneOptions(), new GitContext(), ct);

            GitReference? mainRef = await cloned.ReferenceResolveAsync("refs/heads/main", ct);
            GitOid mainOid = ((GitDirectReference)mainRef!).Target;

            // 2. Push main as refs/heads/feature on the server.
            GitRemote seedRemote = await cloned.RemoteLookupAsync("origin", ct);
            await seedRemote.PushAsync(["refs/heads/main:refs/heads/feature"], new GitPushOptions(), reflogMessage: null, ct);
            await seedRemote.DisconnectAsync(ct);

            // 3. Verify the feature ref exists server-side.
            string before = await fixture.ExecAsync(
                "cd " + GitDaemonDockerFixture.RepoPath + " && git rev-parse refs/heads/feature", ct);
            Assert.Equal(mainOid.ToString(), before.Trim());

            // 4. Delete refs/heads/feature via an empty-source refspec.
            GitRemote remote = await cloned.RemoteLookupAsync("origin", ct);
            GitPushResult result = await remote.PushAsync([":refs/heads/feature"], new GitPushOptions(), reflogMessage: null, ct);

            Assert.True(result.UnpackOk);
            Assert.Single(result.Status);
            Assert.True(result.Status[0].Ok);
            Assert.Equal("refs/heads/feature", result.Status[0].Ref);

            await remote.DisconnectAsync(ct);

            // 5. Server-side: refs/heads/feature must be gone.
            string after = await fixture.ExecAsync(
                "cd " + GitDaemonDockerFixture.RepoPath + " && " +
                "if git rev-parse --verify refs/heads/feature >/dev/null 2>&1; then echo present; else echo gone; fi", ct);
            Assert.Equal("gone", after.Trim());
        }
        finally
        {
            Cleanup(targetPath);
        }
    }

    // ── Connect then disconnect ───────────────────────────────────────

    /// <summary>
    /// <see cref="GitRemote.ConnectAsync"/> transitions
    /// <see cref="GitRemote.IsConnected"/> to <c>true</c>;
    /// <see cref="GitRemote.DisconnectAsync"/> transitions it back to
    /// <c>false</c>. Exercises the
    /// <see cref="GitTransport.CloseAsync"/> +
    /// <see cref="GitTransport.DisposeAsync"/> chain
    /// (which disposes the underlying <see cref="GitStream"/>
    /// + <see cref="GitSocket"/>).
    /// </summary>
    [Fact]
    public async Task Connect_ThenDisconnect_TransitionsIsConnected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitDaemonDockerContainer fixture = await _daemon.StartContainerAsync(_loggerFactory, ct);

        string url = GitUrl(fixture);
        string localPath = NewTempPath("disc");
        await using GitRepository repo = await GitRepository.InitAsync(localPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);

            Assert.False(remote.IsConnected);
            await remote.ConnectAsync(GitDirection.Fetch, cancellationToken: ct);
            Assert.True(remote.IsConnected);

            // Drain the advertised refs so the connection is fully
            // exercised before close (the smart-protocol negotiation is
            // what populates the cached heads).
            IReadOnlyList<GitRemoteHead> _ = await remote.LsAsync(ct);

            await remote.DisconnectAsync(ct);
            Assert.False(remote.IsConnected);
        }
        finally
        {
            Cleanup(localPath);
        }
    }
}
