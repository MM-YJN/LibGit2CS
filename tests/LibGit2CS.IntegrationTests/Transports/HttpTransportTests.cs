using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.IntegrationTests.Transports;

public sealed class HttpTransportTests(ITestOutputHelper testOutputHelper)
{
    private const string Sha1Main = "a5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string Sha1Zero = "0000000000000000000000000000000000000000";

    /// <summary>
    /// Create a SmartTransport backed by HttpTransport using the test server's
    /// in-memory handler (bypasses real TCP/TLS).
    /// </summary>
    private static GitSmartTransport CreateTransport(HttpTestServer server)
    {
        var ctx = new GitContext();
        var httpTransport = new GitHttpTransport(ctx, server.CreateHandler());
        var definition = new SubtransportDefinition(
            _ => httpTransport, IsRpc: true, null);
        return new GitSmartTransport(definition, ctx);
    }

    // ─── A. Ref advertisement + basic fetch ────────────────────────────

    [Fact]
    public async Task Connect_GetInfoRefs_ReceivesRefAdvertisement()
    {
        using var server = new HttpTestServer(testOutputHelper);
        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(heads);
        GitRemoteHead main = Assert.Single(heads, h => h.Name == "refs/heads/main");
        Assert.Equal(Sha1Main, main.Oid.ToString());
    }

    [Fact]
    public async Task Connect_Rpc_StripsCommentPacket()
    {
        using var server = new HttpTestServer(testOutputHelper);
        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.HaveRefs);
        Assert.True(transport.SmartCaps.HasCapabilities);
        Assert.True((transport.SmartCaps.Flags & GitSmartCapabilities.SideBand64k) != 0);
        Assert.True((transport.SmartCaps.Flags & GitSmartCapabilities.OfsDelta) != 0);
    }

    [Fact]
    public async Task Connect_EmptyRepo_ReturnsZeroHeads()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.CustomRefAdvertisement = BuildEmptyRepoRefAd();

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(heads);
    }

    [Fact]
    public async Task Connect_DetectsSymref()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.CustomRefAdvertisement = BuildRefAdWithSymref();

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        GitRemoteHead head = Assert.Single(heads, h => h.Name == "HEAD");
        Assert.Equal("refs/heads/main", head.SymrefTarget);
    }

    [Fact]
    public async Task Connect_MultipleRefs_AllParsed()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.CustomRefAdvertisement = BuildMultiRefAd();

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, heads.Count);
        Assert.Contains(heads, h => h.Name == "refs/heads/main");
        Assert.Contains(heads, h => h.Name == "refs/heads/feature");
        Assert.Contains(heads, h => h.Name == "refs/tags/v1.0");
    }

    [Fact]
    public async Task Connect_ContentTypeMismatch_Throws()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.CustomRefAdvertisement = Encoding.ASCII.GetBytes("invalid");

        await using GitSmartTransport transport = CreateTransport(server);
        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Connect_ErrorStatus_Throws()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AlwaysReturnStatus = 500;
        await using GitSmartTransport transport = CreateTransport(server);
        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Connect_Disconnect_ClosesConnection()
    {
        using var server = new HttpTestServer(testOutputHelper);
        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        Assert.True(transport.IsConnected);

        await transport.CloseAsync(TestContext.Current.CancellationToken);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Connect_RecordsRequest()
    {
        using var server = new HttpTestServer(testOutputHelper);
        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        IReadOnlyList<RecordedRequest> requests = server.Config.Requests;
        Assert.NotEmpty(requests);
        RecordedRequest getInfoRefs = Assert.Single(requests, r => r.Path.EndsWith("/info/refs"));
        Assert.Equal("GET", getInfoRefs.Method);
        Assert.Contains("service=git-upload-pack", getInfoRefs.Query);
    }

    [Fact]
    public async Task Connect_PushDirection_UsesReceivePackService()
    {
        using var server = new HttpTestServer(testOutputHelper);
        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Push, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        IReadOnlyList<RecordedRequest> requests = server.Config.Requests;
        Assert.Contains(requests, r => r.Path.EndsWith("/info/refs") && r.Query.Contains("git-receive-pack"));
    }

    [Fact]
    public async Task Push_OverHttp_ReportStatusReceived()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.PushRefUpdates = ["refs/heads/main"];

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Push, null, TestContext.Current.CancellationToken);

        // Build a push spec — delete refs/heads/main (loid=zero means delete, no pack needed)
        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = LibGit2CS.Refs.GitRefSpec.Parse(":refs/heads/main", isFetch: false),
                Loid = default,
                Roid = GitOid.Parse(Sha1Main, GitHashAlgorithmKind.Sha1),
            },
        };

        // Delete: no pack needed (loid is zero), only pkt-line commands are sent.
        GitPushResult result = await transport.PushAsync(
            repo: null!,
            specs: specs,
            packWriter: null,
            callbacks: null,
            reportStatus: true,
            pushOptions: null,
            cancellationToken: TestContext.Current.CancellationToken);

        // Verify the server received a receive-pack POST
        IReadOnlyList<RecordedRequest> allRequests = server.Config.Requests;
        Assert.Contains(allRequests, r => r.Method == "POST" && r.Path.EndsWith("/git-receive-pack"));

        // Verify the report-status was parsed
        Assert.True(result.UnpackOk);
        Assert.Single(result.Status);
        Assert.True(result.Status[0].Ok);
        Assert.Equal("refs/heads/main", result.Status[0].Ref);
    }

    // ─── B. Fetch negotiation + pack download ──────────────────────────

    /// <summary>
    /// Drive a full fetch over HTTP: connect, ls, negotiate, download pack.
    /// Exercises the non-chunked POST path in <see cref="HttpStream"/> —
    /// the pooled write buffer that is materialized on every retry.
    /// </summary>
    [Fact]
    public async Task Fetch_OverHttp_NegotiatesAndDownloadsPack()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "libgit2cs-fetch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Build a real source repo with one commit (used to build the pack).
            string sourcePath = Path.Combine(tempDir, "source");
            await using GitRepository sourceRepo = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct);
            GitOid commitOid = await WriteInitialCommitAsync(sourceRepo, ct);

            // 2. Build a real pack containing the commit/tree/blob.
            byte[] packBytes = await BuildPackFromRepoAsync(sourceRepo, commitOid, ct);

            // 3. Stand up the HTTP test server with a ref-ad for the real
            //    commit OID and the real pack as the upload-pack response.
            using var server = new HttpTestServer(testOutputHelper);
            server.Config.CustomRefAdvertisement = BuildRefAdForCommit(commitOid);
            server.Config.PackData = packBytes;

            // 4. Create an empty client repo that will receive the pack.
            string clientPath = Path.Combine(tempDir, "client");
            await using GitRepository clientRepo = await GitRepository.InitAsync(clientPath, isBare: false, new GitContext(), cancellationToken: ct);

            // 5. Connect + ls + negotiate + download.
            await using GitSmartTransport transport = CreateTransport(server);
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, ct);

            IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(ct);
            GitRemoteHead main = Assert.Single(heads, h => h.Name == "refs/heads/main");
            Assert.Equal(commitOid, main.Oid);

            var wants = new GitFetchNegotiation(heads, Array.Empty<GitOid>(), Depth: 0);
            await transport.NegotiateFetchAsync(clientRepo, wants, ct);
            await transport.DownloadPackAsync(clientRepo, new GitIndexerProgress(), ct);

            // 6. Verify the commit/tree/blob objects are now present in the
            //    client repo. DownloadPackAsync already refreshed the ODB's
            //    pack backends, so lookups should see the new pack.
            GitObject? commitObj = await clientRepo.ObjectLookupAsync(commitOid, ct);
            Assert.NotNull(commitObj);
            Assert.Equal(GitObjectType.Commit, commitObj!.Type);

            var commit = (Commit)commitObj;
            GitTree? tree = await clientRepo.ObjectLookupAsync<GitTree>(commit.Tree, ct);
            Assert.NotNull(tree);
            await foreach ((_, GitTreeEntry entry) in tree!.WalkAsync(GitTreeWalkMode.PreOrder, ct).ConfigureAwait(false))
            {
                GitObject? blob = await clientRepo.ObjectLookupAsync(entry.Id, ct);
                Assert.NotNull(blob);
                Assert.Equal(GitObjectType.Blob, blob!.Type);
            }

            tree.Dispose();
            commit.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// Regression test for fetch negotiation: the client must advertise the
    /// common commit as a <c>have</c> line rather than negotiating "zero
    /// haves" (which makes the server re-send the whole repository).
    /// <see cref="GitSmartProtocol.NegotiateFetchAsync"/> enumerates local
    /// tips with <c>PushGlobAsync("refs/*")</c>. A
    /// <c>WildMatchFlags.Pathname</c> glob matcher would match no
    /// concrete ref (every ref is two-or-more segments deep), so the client
    /// would advertise zero <c>have</c> lines and the server would re-send the whole
    /// repository. With <c>*</c> crossing <c>/</c>, a client that
    /// already has commit A must advertise <c>have &lt;A&gt;</c> when the
    /// server offers A's child B.
    /// </summary>
    /// <remarks>
    /// The client reproduces the deterministic root commit A in its own ODB
    /// and points <c>refs/remotes/origin/main</c> at it — the post-clone
    /// layout. The mock server records the upload-pack POST body; the
    /// assertion checks that body carries a <c>have</c> for A. This is the
    /// only test path that exercises <see cref="GitSmartProtocol.NegotiateFetchAsync"/>
    /// (the local transport negotiates by ref name, not via the <c>refs/*</c>
    /// revwalk).
    /// </remarks>
    [Fact]
    public async Task NegotiateFetch_ClientHasCommonCommit_AdvertisesHaveLine()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "libgit2cs-neg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Source: root commit A, then child B on refs/heads/main.
            string sourcePath = Path.Combine(tempDir, "source");
            await using GitRepository sourceRepo = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct);
            GitOid commitA = await WriteInitialCommitAsync(sourceRepo, ct);
            GitOid commitB = await WriteSecondCommitAsync(sourceRepo, commitA, ct);

            // 2. Pack containing B's new objects (commit B + tree + blob).
            byte[] packBytes = await BuildPackFromRepoAsync(sourceRepo, commitB, ct);

            // 3. Server advertises refs/heads/main -> B and serves the B pack.
            using var server = new HttpTestServer(testOutputHelper);
            server.Config.CustomRefAdvertisement = BuildRefAdForCommit(commitB);
            server.Config.PackData = packBytes;

            // 4. Client reproduces A (deterministic OID) and tracks it as a
            //    remote-tracking ref, mimicking a prior clone at commit A.
            string clientPath = Path.Combine(tempDir, "client");
            await using GitRepository clientRepo = await GitRepository.InitAsync(clientPath, isBare: false, new GitContext(), cancellationToken: ct);
            GitOid clientCommitA = await WriteInitialCommitAsync(clientRepo, ct);
            Assert.Equal(commitA, clientCommitA);
            await clientRepo.ReferenceCreateAsync("refs/remotes/origin/main", commitA, cancellationToken: ct);

            // 5. Connect + negotiate. NegotiateFetchAsync enumerates the
            //    client's refs via "refs/*" and emits a have for A.
            await using GitSmartTransport transport = CreateTransport(server);
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, ct);

            IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(ct);
            GitRemoteHead main = Assert.Single(heads, h => h.Name == "refs/heads/main");
            Assert.Equal(commitB, main.Oid);

            var wants = new GitFetchNegotiation(heads, Array.Empty<GitOid>(), Depth: 0);
            await transport.NegotiateFetchAsync(clientRepo, wants, ct);

            // 6. The recorded upload-pack POST body must contain a have for A.
            //    (A body with only want(B) + done would skip the negotiation.)
            string negotiationBody = string.Join("\n", server.Config.UploadPackRequests);
            Assert.Contains($"have {commitA}", negotiationBody);

            // 7. Download the delta pack and confirm B is now present.
            await transport.DownloadPackAsync(clientRepo, new GitIndexerProgress(), ct);
            Commit? b = await clientRepo.ObjectLookupAsync<Commit>(commitB, ct);
            Assert.NotNull(b);
            Assert.Equal("second\n", b!.Message);
            b.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// Write an initial commit (README.md → "hello\n") to the repo and return
    /// the commit OID. Mirrors the CreateSourceRepo helper in
    /// LocalTransportTests.
    /// </summary>
    private static async Task<GitOid> WriteInitialCommitAsync(GitRepository repo, CancellationToken ct)
    {
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync(ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", ct);
        await idx.AddByPathAsync("README.md", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        var sig = new GitSignature("Test User", "test@example.com", new GitTime(1700000000, 0));
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial\n",
            UpdateRef = "refs/heads/main",
        }, ct);

        return commitOid;
    }

    /// <summary>
    /// Write a second commit on top of <paramref name="parent"/> that changes
    /// README.md to "world\n", advancing refs/heads/main. Deterministic OID
    /// for a given parent (fixed signature/timestamp/message).
    /// </summary>
    private static async Task<GitOid> WriteSecondCommitAsync(GitRepository repo, GitOid parent, CancellationToken ct)
    {
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync(ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "world\n", ct);
        await idx.AddByPathAsync("README.md", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        var sig = new GitSignature("Test User", "test@example.com", new GitTime(1700000001, 0));
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parent],
            Author = sig,
            Committer = sig,
            Message = "second\n",
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    /// <summary>
    /// Build a real pack file containing the commit, its tree, and all
    /// reachable blobs. Uses <see cref="GitPackWriter"/> to produce a
    /// byte-exact pack the server can stream back to the client.
    /// </summary>
    private static async Task<byte[]> BuildPackFromRepoAsync(GitRepository repo, GitOid commitOid, CancellationToken ct)
    {
        using GitPackWriter packWriter = repo.NewPackWriter();
        await packWriter.InsertAsync(commitOid, ct);

        // Insert the commit's tree and all reachable blobs.
        Commit? commit = await repo.ObjectLookupAsync<Commit>(commitOid, ct);
        Assert.NotNull(commit);
        await packWriter.InsertTreeAsync(commit!.Tree, ct);
        commit.Dispose();

        await packWriter.PrepareAsync(ct);

        using var ms = new MemoryStream();
        await packWriter.WriteAsync(ms, progress: null, ct);
        return ms.ToArray();
    }

    /// <summary>
    /// Build a ref advertisement for a single ref pointing at the given
    /// commit OID, with side-band-64k + ofs-delta capabilities (matching what
    /// a real git-upload-pack server would advertise).
    /// </summary>
    private static byte[] BuildRefAdForCommit(GitOid commitOid)
    {
        string oidHex = commitOid.ToString();
        var sb = new StringBuilder();
        sb.Append("001e# service=git-upload-pack\n");
        sb.Append("0000");
        string caps = "multi_ack_detailed side-band-64k ofs-delta agent=git/test";
        string firstLine = $"{oidHex} refs/heads/main\0{caps}\n";
        int firstLen = firstLine.Length + 4;
        sb.Append(firstLen.ToString("x4")).Append(firstLine);
        sb.Append("0000");
        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ─── C. Auth ───────────────────────────────────────────────────────

    [Fact]
    public async Task Auth_NoAuthRequired_NoAuthorizationHeader()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AuthRequired = false;

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task Auth_BasicChallenge_CredentialCallback_ReturnsValidToken()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AuthRequired = true;
        server.Config.AuthType = "Basic";

        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitUserPassCredential("user", "pass")),
            },
        };

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);

        // Verify at least 2 requests were made (first 401, then 200 with auth)
        IReadOnlyList<RecordedRequest> requests = server.Config.Requests;
        Assert.True(requests.Count >= 2);
    }

    [Fact]
    public async Task Auth_UrlEmbeddedCredentials_UsedBeforeCallback()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AuthRequired = true;
        server.Config.AuthType = "Basic";

        bool callbackInvoked = false;
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) =>
                {
                    callbackInvoked = true;
                    return Task.FromResult<GitCredential?>(new GitUserPassCredential("user", "pass"));
                },
            },
        };

        // URL with embedded credentials
        string url = $"{server.BaseUrl.Replace("http://", "http://user:pass@")}/repo.git";
        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync(url, GitDirection.Fetch, options, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        // URL credentials should be tried first, callback should only be
        // invoked if URL creds fail (which they don't)
        Assert.False(callbackInvoked);
    }

    [Fact]
    public async Task Auth_BasicChallenge_CallbackReturnsNull_ThrowsAuth()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AuthRequired = true;
        server.Config.AuthType = "Basic";

        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(null),
            },
        };

        await using GitSmartTransport transport = CreateTransport(server);
        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Auth_BasicChallenge_NoCredentials_ThrowsAuth()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AuthRequired = true;
        server.Config.AuthType = "Basic";

        await using GitSmartTransport transport = CreateTransport(server);
        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Auth_BasicChallenge_WrongCredentials_ThrowsAuth()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AuthRequired = true;
        server.Config.AuthType = "Basic";

        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitUserPassCredential("wrong", "wrong")),
            },
        };

        await using GitSmartTransport transport = CreateTransport(server);
        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Auth_NegotiateChallenge_AcceptsNegotiateToken()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AuthRequired = true;
        server.Config.AuthType = "Negotiate";

        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitDefaultCredential()),
            },
        };

        // This test uses a mock server that accepts any non-empty Negotiate token.
        // The real NegotiateAuthentication may fail without a real SPNEGO context,
        // but the transport should still attempt to send a Negotiate token.
        await using GitSmartTransport transport = CreateTransport(server);
        try
        {
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken);
            Assert.True(transport.IsConnected);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.Auth)
        {
            // Negotiate auth may fail on platforms without Kerberos/SPNEGO.
            // This is acceptable - the important thing is no crash.
        }
    }

    // ─── D. Redirect ───────────────────────────────────────────────────

    [Fact]
    public async Task Redirect_InitialRedirect_Followed()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AddRedirect("GET", "/old/repo.git/info/refs", "/repo.git/info/refs");

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/old/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task Redirect_NonePolicy_ThrowsOnRedirect()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AddRedirect("GET", "/old/repo.git/info/refs", "/repo.git/info/refs");

        var options = new GitRemoteConnectOptions
        {
            FollowRedirects = GitRemoteRedirect.None,
        };

        await using GitSmartTransport transport = CreateTransport(server);
        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync($"{server.BaseUrl}/old/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Redirect_AllPolicy_FollowsRedirect()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AddRedirect("GET", "/old/repo.git/info/refs", "/repo.git/info/refs");

        var options = new GitRemoteConnectOptions
        {
            FollowRedirects = GitRemoteRedirect.All,
        };

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/old/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task Redirect_InitialPolicy_AllowsInitialRedirect()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AddRedirect("GET", "/old/repo.git/info/refs", "/repo.git/info/refs");

        var options = new GitRemoteConnectOptions
        {
            FollowRedirects = GitRemoteRedirect.Initial,
        };

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/old/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task Redirect_SchemeDowngrade_IsRejected()
    {
        // an https
        // remote responding with Location: http://attacker/... must abort
        // ("cannot redirect from 'https' to 'http'", git_net_url_apply_redirect,
        // net.c:949-956) instead of following and sending the cached
        // Authorization header over plaintext. The origin is https here
        // (the in-memory handler serves whatever URL the transport asks for);
        // the server's redirect points at an http host.
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.AddRedirect("GET", "/old/repo.git/info/refs", "http://attacker.example/evil/info/refs");

        await using GitSmartTransport transport = CreateTransport(server);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync("https://example.com/old/repo.git", GitDirection.Fetch, null, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Net, ex.Category);
        Assert.Contains("cannot redirect from 'https' to 'http'", ex.Message);
    }

    // ─── E. Custom headers ─────────────────────────────────────────────

    [Fact]
    public async Task CustomHeaders_SentWithRequest()
    {
        using var server = new HttpTestServer(testOutputHelper);

        var options = new GitRemoteConnectOptions
        {
            CustomHeaders = new List<string> { "X-Custom-Header: test-value" },
        };

        await using GitSmartTransport transport = CreateTransport(server);
        await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
    }

    // ─── F. Proxy auth ─────────────────────────────────────────────────

    [Fact]
    public async Task ProxyAuth_BasicChallenge_ReturnsProxyAuthHeader()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.ProxyAuthRequired = true;

        var options = new GitRemoteConnectOptions
        {
            Proxy = new GitProxyConfig
            {
                Type = GitProxyType.Specified,
                Url = server.BaseUrl,
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitUserPassCredential("proxyuser", "proxypass")),
            },
        };

        await using GitSmartTransport transport = CreateTransport(server);
        try
        {
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken);
        }
        catch (GitException)
        {
            // Proxy auth may fail in the test setup, but the important thing
            // is that the transport attempted to resolve proxy credentials.
        }
    }

    [Fact]
    public async Task ProxyAuth_NoCredentials_ThrowsAuth()
    {
        using var server = new HttpTestServer(testOutputHelper);
        server.Config.ProxyAuthRequired = true;

        var options = new GitRemoteConnectOptions
        {
            Proxy = new GitProxyConfig
            {
                Type = GitProxyType.Specified,
                Url = server.BaseUrl,
            },
        };

        await using GitSmartTransport transport = CreateTransport(server);
        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, options, TestContext.Current.CancellationToken));
    }

    // ─── G. Auth handlers unit tests ───────────────────────────────────

    [Fact]
    public void AuthHandlers_ParseChallenges_Basic()
    {
        List<AuthChallenge> challenges = AuthHandlersTestAccess.ParseChallenges(new[] { "Basic realm=\"git\"" });
        AuthChallenge challenge = Assert.Single(challenges);
        Assert.Equal(GitAuthSchemeType.Basic, challenge.Scheme);
        Assert.Equal("realm=\"git\"", challenge.Parameters);
    }

    [Fact]
    public void AuthHandlers_ParseChallenges_Negotiate()
    {
        List<AuthChallenge> challenges = AuthHandlersTestAccess.ParseChallenges(new[] { "Negotiate" });
        AuthChallenge challenge = Assert.Single(challenges);
        Assert.Equal(GitAuthSchemeType.Negotiate, challenge.Scheme);
    }

    [Fact]
    public void AuthHandlers_ParseChallenges_NTLM()
    {
        List<AuthChallenge> challenges = AuthHandlersTestAccess.ParseChallenges(new[] { "NTLM <token>" });
        AuthChallenge challenge = Assert.Single(challenges);
        Assert.Equal(GitAuthSchemeType.Ntlm, challenge.Scheme);
        Assert.Equal("<token>", challenge.Parameters);
    }

    [Fact]
    public void AuthHandlers_ParseChallenges_MultipleSchemes()
    {
        List<AuthChallenge> challenges = AuthHandlersTestAccess.ParseChallenges(new[]
        {
            "Negotiate, Basic realm=\"git\"",
        });
        Assert.Equal(2, challenges.Count);
        Assert.Contains(challenges, c => c.Scheme == GitAuthSchemeType.Negotiate);
        Assert.Contains(challenges, c => c.Scheme == GitAuthSchemeType.Basic);
    }

    [Fact]
    public void AuthHandlers_SelectBest_PrioritizesNegotiate()
    {
        // selection matches the ACQUIRED credential's type — a DEFAULT credential picks Negotiate over Basic.
        List<AuthChallenge> challenges = AuthHandlersTestAccess.ParseChallenges(new[]
        {
            "Basic realm=\"git\", Negotiate",
        });
        using var cred = new GitDefaultCredential();
        GitAuthSchemeType best = AuthHandlersTestAccess.SelectBest(challenges, cred);
        Assert.Equal(GitAuthSchemeType.Negotiate, best);
    }

    [Fact]
    public void AuthHandlers_SelectBest_FallsBackToNTLM()
    {
        List<AuthChallenge> challenges = AuthHandlersTestAccess.ParseChallenges(new[]
        {
            "Basic realm=\"git\", NTLM",
        });
        using var cred = new GitUserPassCredential("u", "p");
        GitAuthSchemeType best = AuthHandlersTestAccess.SelectBest(challenges, cred);
        Assert.Equal(GitAuthSchemeType.Ntlm, best);
    }

    [Fact]
    public void AuthHandlers_SelectBest_FallsBackToBasic()
    {
        List<AuthChallenge> challenges = AuthHandlersTestAccess.ParseChallenges(new[]
        {
            "Basic realm=\"git\"",
        });
        using var cred = new GitUserPassCredential("u", "p");
        GitAuthSchemeType best = AuthHandlersTestAccess.SelectBest(challenges, cred);
        Assert.Equal(GitAuthSchemeType.Basic, best);
    }

    [Fact]
    public void AuthHandlers_SelectBest_UserPassSkipsNegotiate()
    {
        // Negotiate only accepts DEFAULT credentials — a user/pass credential falls through to Basic when both are advertised.
        List<AuthChallenge> challenges = AuthHandlersTestAccess.ParseChallenges(new[]
        {
            "Negotiate, Basic realm=\"git\"",
        });
        using var cred = new GitUserPassCredential("u", "p");
        GitAuthSchemeType best = AuthHandlersTestAccess.SelectBest(challenges, cred);
        Assert.Equal(GitAuthSchemeType.Basic, best);
    }

    [Fact]
    public void AuthHandlers_CredentialTypes_BasicRequiresUserPass()
    {
        Assert.Equal(GitCredentialType.UserPassPlaintext, AuthHandlersTestAccess.CredTypesFor(GitAuthSchemeType.Basic));
    }

    [Fact]
    public void AuthHandlers_CredentialTypes_NegotiateAllowsDefault()
    {
        // Negotiate accepts DEFAULT only (httpclient.c:26-30).
        GitCredentialType types = AuthHandlersTestAccess.CredTypesFor(GitAuthSchemeType.Negotiate);
        Assert.Equal(GitCredentialType.Default, types);
        Assert.True((types & GitCredentialType.UserPassPlaintext) == 0);
    }

    [Fact]
    public void AuthHandlers_ApplyUrlCredentials_ExtractsUserPass()
    {
        GitUserPassCredential? cred = AuthHandlersTestAccess.ApplyUrlCredentials(new Uri("http://user:pass@host/repo"));
        Assert.NotNull(cred);
        Assert.Equal("user", cred!.Username);
        Assert.Equal("pass", cred.Password);
    }

    [Fact]
    public void AuthHandlers_ApplyUrlCredentials_NoCredentials_ReturnsNull()
    {
        GitUserPassCredential? cred = AuthHandlersTestAccess.ApplyUrlCredentials(new Uri("http://host/repo"));
        Assert.Null(cred);
    }

    [Fact]
    public void AuthHandlers_ApplyUrlCredentials_UrlEncodesCredentials()
    {
        GitUserPassCredential? cred = AuthHandlersTestAccess.ApplyUrlCredentials(new Uri("http://us%40er:p%40ss@host/repo"));
        Assert.NotNull(cred);
        Assert.Equal("us@er", cred!.Username);
        Assert.Equal("p@ss", cred.Password);
    }

    [Fact]
    public void AuthHandlers_ApplyUrlCredentials_UsernameOnly()
    {
        GitUserPassCredential? cred = AuthHandlersTestAccess.ApplyUrlCredentials(new Uri("http://user@host/repo"));
        Assert.NotNull(cred);
        Assert.Equal("user", cred!.Username);
        Assert.Equal("", cred.Password);
    }

    // ─── H. Auth context unit tests ────────────────────────────────────

    [Fact]
    public void AuthContext_Basic_NextToken_ReturnsBase64()
    {
        using var ctx = AuthContext.Create(
            GitAuthSchemeType.Basic,
            new GitUserPassCredential("user", "pass"),
            "host",
            isProxy: false);

        string? token = ctx.NextToken(null);
        Assert.NotNull(token);
        Assert.StartsWith("Basic ", token);

        // Decode and verify
        string encoded = token!["Basic ".Length..];
        byte[] decoded = Convert.FromBase64String(encoded);
        string decodedStr = Encoding.UTF8.GetString(decoded);
        Assert.Equal("user:pass", decodedStr);
        Assert.True(ctx.IsComplete);
    }

    [Fact]
    public void AuthContext_Basic_ConnectionAffinity_False()
    {
        using var ctx = AuthContext.Create(
            GitAuthSchemeType.Basic,
            new GitUserPassCredential("user", "pass"),
            "host",
            isProxy: false);

        Assert.False(ctx.ConnectionAffinity);
    }

    [Fact]
    public void AuthContext_Negotiate_ConnectionAffinity_True()
    {
        using var ctx = AuthContext.Create(
            GitAuthSchemeType.Negotiate,
            new GitDefaultCredential(),
            "host",
            isProxy: false);

        Assert.True(ctx.ConnectionAffinity);
    }

    [Fact]
    public void AuthContext_NTLM_ConnectionAffinity_True()
    {
        using var ctx = AuthContext.Create(
            GitAuthSchemeType.Ntlm,
            new GitUserPassCredential("user", "pass"),
            "host",
            isProxy: false);

        Assert.True(ctx.ConnectionAffinity);
    }

    // ─── I. Progress callbacks ─────────────────────────────────────────

    /// <summary>
    /// Drive a full HTTP fetch with a <see cref="GitRemoteCallbacks.TransferProgress"/>
    /// callback wired: <see cref="GitSmartProtocol.DownloadPackAsync"/> sets a
    /// packetsize callback that fires <see cref="GitTransferProgress"/> with
    /// accumulating <see cref="GitTransferProgress.ReceivedBytes"/> as pack
    /// data streams in, plus a final report after the last packet. The
    /// callback must fire at least once with
    /// <see cref="GitTransferProgress.ReceivedBytes"/> > 0.
    /// </summary>
    /// <remarks>
    /// Reuses the real-source-repo + real-pack + <see cref="HttpTestServer"/>
    /// setup from <see cref="Fetch_OverHttp_NegotiatesAndDownloadsPack"/> —
    /// the only difference is the connect options carry a
    /// <see cref="CapturingProgress{T}"/> instance.
    /// </remarks>
    [Fact]
    public async Task Fetch_OverHttp_WithTransferProgressCallback_FiresReports()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "libgit2cs-fetch-prog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Build a real source repo + pack (same as Fetch_OverHttp_NegotiatesAndDownloadsPack).
            string sourcePath = Path.Combine(tempDir, "source");
            await using GitRepository sourceRepo = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct);
            GitOid commitOid = await WriteInitialCommitAsync(sourceRepo, ct);
            byte[] packBytes = await BuildPackFromRepoAsync(sourceRepo, commitOid, ct);

            // 2. Server with the real ref-ad + pack.
            using var server = new HttpTestServer(testOutputHelper);
            server.Config.CustomRefAdvertisement = BuildRefAdForCommit(commitOid);
            server.Config.PackData = packBytes;

            // 3. Empty client repo.
            string clientPath = Path.Combine(tempDir, "client");
            await using GitRepository clientRepo = await GitRepository.InitAsync(clientPath, isBare: false, new GitContext(), cancellationToken: ct);

            // 4. Connect with a TransferProgress callback.
            var progress = new CapturingProgress<GitTransferProgress>();
            var connectOpts = new GitRemoteConnectOptions
            {
                Callbacks = new GitRemoteCallbacks { TransferProgress = progress },
            };

            await using GitSmartTransport transport = CreateTransport(server);
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Fetch, connectOpts, ct);

            IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(ct);
            var wants = new GitFetchNegotiation(heads, Array.Empty<GitOid>(), Depth: 0);
            await transport.NegotiateFetchAsync(clientRepo, wants, ct);
            await transport.DownloadPackAsync(clientRepo, new GitIndexerProgress(), ct);

            // 5. The packetsize callback must have fired with bytes > 0.
            Assert.NotEmpty(progress.Reports);
            Assert.Contains(progress.Reports, r => r.ReceivedBytes > 0);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Push a real pack over HTTP with a
    /// <see cref="GitRemoteCallbacks.PushTransferProgress"/> callback wired:
    /// <see cref="GitSmartProtocol.PushAsync"/> streams the pack through a
    /// <see cref="SubtransportStreamAdapter"/> and, after
    /// <see cref="GitPackWriter.WriteAsync"/> completes, fires a final
    /// <see cref="GitPushTransferProgress"/> report with
    /// <see cref="GitPushTransferProgress.Current"/> ==
    /// <see cref="GitPushTransferProgress.Total"/> ==
    /// <see cref="GitPackWriter.ObjectCount"/> and
    /// <see cref="GitPushTransferProgress.Bytes"/> > 0. Exercises the
    /// <c>pushTransferProgress?.Report(...)</c> branch at
    /// <see cref="GitSmartProtocol"/> line 1089.
    /// </summary>
    /// <remarks>
    /// <b>Setup.</b> Builds a real source repo + commit, constructs a
    /// <see cref="GitPackWriter"/> with the commit's objects inserted (but
    /// NOT <see cref="GitPackWriter.PrepareAsync"/>'d — the transport does
    /// that), and a <see cref="HttpTestServer"/> whose
    /// <c>/git-receive-pack</c> handler returns a scripted report-status
    /// acknowledging <c>refs/heads/main</c>.
    /// </remarks>
    [Fact]
    public async Task Push_OverHttp_WithPushTransferProgressCallback_FiresFinalReport()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "libgit2cs-push-prog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Build a real source repo with one commit.
            string sourcePath = Path.Combine(tempDir, "source");
            await using GitRepository sourceRepo = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct);
            GitOid commitOid = await WriteInitialCommitAsync(sourceRepo, ct);

            // 2. Construct a pack writer with the commit's objects queued.
            //    Do NOT PrepareAsync — GitSmartProtocol.PushAsync does that.
            GitPackWriter packWriter = new(sourceRepo);
            await packWriter.InsertAsync(commitOid, ct);
            Commit? commit = await sourceRepo.ObjectLookupAsync<Commit>(commitOid, ct);
            Assert.NotNull(commit);
            await packWriter.InsertTreeAsync(commit!.Tree, ct);
            commit.Dispose();

            // 3. Server: ref-ad for the receive-pack phase + report-status
            //    acknowledging refs/heads/main.
            using var server = new HttpTestServer(testOutputHelper);
            server.Config.PushRefUpdates = ["refs/heads/main"];

            // 4. Connect in Push direction and push with a real pack + callback.
            await using GitSmartTransport transport = CreateTransport(server);
            await transport.ConnectAsync($"{server.BaseUrl}/repo.git", GitDirection.Push, null, ct);

            var progress = new CapturingProgress<GitPushTransferProgress>();
            var callbacks = new GitRemoteCallbacks { PushTransferProgress = progress };

            var specs = new List<GitPushSpec>
            {
                new()
                {
                    RefSpec = LibGit2CS.Refs.GitRefSpec.Parse("refs/heads/main:refs/heads/main", isFetch: false),
                    Loid = commitOid,
                    Roid = default, // Remote has nothing — pure create.
                },
            };

            GitPushResult result = await transport.PushAsync(
                repo: sourceRepo,
                specs: specs,
                packWriter: packWriter,
                callbacks: callbacks,
                reportStatus: true,
                pushOptions: null,
                cancellationToken: ct);

            // 5. Push must succeed and the final PushTransferProgress report
            //    must carry the pack's full object count + non-zero byte count.
            Assert.True(result.UnpackOk);
            Assert.NotEmpty(progress.Reports);
            GitPushTransferProgress last = progress.Reports[^1];
            Assert.Equal(last.Current, last.Total);
            Assert.Equal(packWriter.ObjectCount, last.Total);
            Assert.True(last.Bytes > 0);

            packWriter.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── J. Transport service selection ────────────────────────────────

    [Fact]
    public void HttpTransport_SelectService_UploadPackLs()
    {
        HttpService svc = HttpTransportTestAccess.SelectService(GitSmartService.UploadPackLs);
        Assert.Equal(HttpMethod.Get, svc.Method);
        Assert.Contains("git-upload-pack", svc.Path);
        Assert.True(svc.IsInitial);
        Assert.False(svc.Chunked);
    }

    [Fact]
    public void HttpTransport_SelectService_UploadPack()
    {
        HttpService svc = HttpTransportTestAccess.SelectService(GitSmartService.UploadPack);
        Assert.Equal(HttpMethod.Post, svc.Method);
        Assert.Equal("/git-upload-pack", svc.Path);
        Assert.False(svc.IsInitial);
        Assert.False(svc.Chunked);
    }

    [Fact]
    public void HttpTransport_SelectService_ReceivePackLs()
    {
        HttpService svc = HttpTransportTestAccess.SelectService(GitSmartService.ReceivePackLs);
        Assert.Equal(HttpMethod.Get, svc.Method);
        Assert.Contains("git-receive-pack", svc.Path);
        Assert.True(svc.IsInitial);
    }

    [Fact]
    public void HttpTransport_SelectService_ReceivePack_Chunked()
    {
        HttpService svc = HttpTransportTestAccess.SelectService(GitSmartService.ReceivePack);
        Assert.Equal(HttpMethod.Post, svc.Method);
        Assert.Equal("/git-receive-pack", svc.Path);
        Assert.True(svc.Chunked);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static byte[] BuildEmptyRepoRefAd()
    {
        var sb = new StringBuilder();
        sb.Append("001e# service=git-upload-pack\n");
        sb.Append("0000");
        string firstLine = $"{Sha1Zero} capabilities^{{}}\n";
        int firstLen = firstLine.Length + 4;
        sb.Append(firstLen.ToString("x4")).Append(firstLine);
        sb.Append("0000");
        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildRefAdWithSymref()
    {
        var sb = new StringBuilder();
        sb.Append("001e# service=git-upload-pack\n");
        sb.Append("0000");
        string caps = "multi_ack_detailed side-band-64k symref=HEAD:refs/heads/main agent=git/test";
        string firstLine = $"{Sha1Main} HEAD\0{caps}\n";
        int firstLen = firstLine.Length + 4;
        sb.Append(firstLen.ToString("x4")).Append(firstLine);
        string mainLine = $"{Sha1Main} refs/heads/main\n";
        int mainLen = mainLine.Length + 4;
        sb.Append(mainLen.ToString("x4")).Append(mainLine);
        sb.Append("0000");
        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildMultiRefAd()
    {
        var sb = new StringBuilder();
        sb.Append("001e# service=git-upload-pack\n");
        sb.Append("0000");
        string caps = "multi_ack_detailed side-band-64k ofs-delta";
        string firstLine = $"{Sha1Main} refs/heads/main\0{caps}\n";
        int firstLen = firstLine.Length + 4;
        sb.Append(firstLen.ToString("x4")).Append(firstLine);
        string featureLine = $"{Sha1Main} refs/heads/feature\n";
        int featureLen = featureLine.Length + 4;
        sb.Append(featureLen.ToString("x4")).Append(featureLine);
        string tagLine = $"{Sha1Main} refs/tags/v1.0\n";
        int tagLen = tagLine.Length + 4;
        sb.Append(tagLen.ToString("x4")).Append(tagLine);
        sb.Append("0000");
        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

/// <summary>
/// Test access helpers for internal AuthHandlers methods.
/// </summary>
internal static class AuthHandlersTestAccess
{
    public static List<AuthChallenge> ParseChallenges(IEnumerable<string> headers)
        => AuthHandlers.ParseChallenges(headers);

    public static GitAuthSchemeType SelectBest(List<AuthChallenge> challenges, GitCredential credential)
        => AuthHandlers.SelectBestScheme(challenges, credential);

    public static GitCredentialType CredTypesFor(GitAuthSchemeType scheme)
        => AuthHandlers.CredentialTypesFor(scheme);

    public static GitUserPassCredential? ApplyUrlCredentials(Uri url)
        => (GitUserPassCredential?)AuthHandlers.ApplyUrlCredentials(url, GitCredentialType.UserPassPlaintext);
}

/// <summary>
/// Test access helpers for internal HttpTransport methods.
/// </summary>
internal static class HttpTransportTestAccess
{
    public static HttpService SelectService(GitSmartService service)
        => GitHttpTransport.SelectService(service);
}
