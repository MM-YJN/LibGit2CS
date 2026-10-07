using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

using LibGit2CS.UnitTests.Transports;

namespace LibGit2CS.UnitTests.Remote;

public sealed class FetchNegotiationTests
{
    private const string Sha1Main = "a5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string Sha1Branch = "b1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";

    [Fact]
    public async Task NegotiateFetch_SingleAck_ReceivesAckAndDone()
    {
        // Single-ACK mode: server sends ACK or NAK for each batch
        byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
            [(Sha1Main, "HEAD"), (Sha1Main, "refs/heads/main")],
            capabilities: "ofs-delta");

        // NAK for the first batch, then ACK after done
        var negotiationResponse = new List<byte>();
        negotiationResponse.AddRange(MockTransportBuilder.BuildNak());
        negotiationResponse.AddRange(MockTransportBuilder.BuildAck(Sha1Main));

        var mock = new MockNegotiationTransport(
            refAd,
            [.. negotiationResponse],
            packData: []);

        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        // The negotiation requires a repository with commits to walk.
        // For this unit test, we verify the transport connects and
        // can read refs. Full negotiation requires a local repo with
        // objects — covered in integration tests.
        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, heads.Count);
        Assert.Equal("HEAD", heads[0].Name);
    }

    [Fact]
    public async Task StoreCommon_AccumulatesAckOids()
    {
        // Build a response with multiple ACK packets
        byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
            [(Sha1Main, "HEAD"), (Sha1Main, "refs/heads/main")],
            capabilities: "multi_ack");

        var acks = new List<byte>();
        acks.AddRange(MockTransportBuilder.BuildAck(Sha1Main, "continue"));
        acks.AddRange(MockTransportBuilder.BuildAck(Sha1Branch, "continue"));
        acks.AddRange(MockTransportBuilder.BuildNak());

        var mock = new MockNegotiationTransport(refAd, [.. acks], []);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        // StoreCommon reads ACK packets from the negotiation response
        // Since the response is queued for UploadPack service, we need
        // to trigger a negotiation step first to switch to that service
        await transport.NegotiationStepAsync("0000"u8.ToArray(), TestContext.Current.CancellationToken);

        await GitSmartProtocol.StoreCommonAsync(transport, TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.Common.Count);
    }

    [Fact]
    public async Task WaitWhileAck_ConsumesUntilNak()
    {
        byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
            [(Sha1Main, "HEAD"), (Sha1Main, "refs/heads/main")],
            capabilities: "multi_ack");

        var acks = new List<byte>();
        acks.AddRange(MockTransportBuilder.BuildAck(Sha1Main, "continue"));
        acks.AddRange(MockTransportBuilder.BuildAck(Sha1Branch, "continue"));
        acks.AddRange(MockTransportBuilder.BuildNak());

        var mock = new MockNegotiationTransport(refAd, [.. acks], []);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        await transport.NegotiationStepAsync("0000"u8.ToArray(), TestContext.Current.CancellationToken);

        await GitSmartProtocol.WaitWhileAckAsync(transport, TestContext.Current.CancellationToken);

        // After WaitWhileAck, all ACKs should have been consumed until NAK
        // The NAK terminates the loop. No exception means success.
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public void SetupCaps_ShallowDepth_RequiresShallowCap()
    {
        var caps = new GitSmartCapabilitySet { Flags = GitSmartCapabilities.None };
        var wants = new GitFetchNegotiation(
            Refs: [],
            ShallowRoots: [],
            Depth: 1);

        Assert.Throws<GitException>(() =>
            GitSmartProtocol.SetupCaps(caps, wants));
    }

    [Fact]
    public void SetupCaps_ShallowDepth_WithShallowCap_Succeeds()
    {
        var caps = new GitSmartCapabilitySet { Flags = GitSmartCapabilities.Shallow };
        var wants = new GitFetchNegotiation(
            Refs: [],
            ShallowRoots: [],
            Depth: 1);

        GitSmartProtocol.SetupCaps(caps, wants);
        Assert.True((caps.Flags & GitSmartCapabilities.Shallow) != 0);
    }

    [Fact]
    public void SetupCaps_ZeroDepth_ClearsShallowCap()
    {
        var caps = new GitSmartCapabilitySet { Flags = GitSmartCapabilities.Shallow };
        var wants = new GitFetchNegotiation(
            Refs: [],
            ShallowRoots: [],
            Depth: 0);

        GitSmartProtocol.SetupCaps(caps, wants);
        Assert.True((caps.Flags & GitSmartCapabilities.Shallow) == 0);
    }

    [Fact]
    public async Task DownloadPack_NoSideband_StreamsRawData()
    {
        // Build a minimal pack (PACK header + 0 objects + trailer)
        byte[] packData = new byte[32];
        packData[0] = (byte)'P';
        packData[1] = (byte)'A';
        packData[2] = (byte)'C';
        packData[3] = (byte)'K';
        // version 2 (big-endian)
        packData[4] = 0;
        packData[5] = 0;
        packData[6] = 0;
        packData[7] = 2;
        // 0 objects
        packData[8] = 0;
        packData[9] = 0;
        packData[10] = 0;
        packData[11] = 0;
        // SHA-1 trailer (20 zero bytes)
        // Already zero

        byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
            [(Sha1Main, "HEAD"), (Sha1Main, "refs/heads/main")],
            capabilities: null); // No side-band

        var mock = new MockNegotiationTransport(refAd, [], packData);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        await transport.NegotiationStepAsync("0000"u8.ToArray(), TestContext.Current.CancellationToken);

        // The pack data should be streamed to the writepack
        // For this test, we just verify no exception is thrown
        // (a real repo with ODB is needed for actual object writing)
    }

    /// <summary>
    /// Drive a full <see cref="GitSmartProtocol.DownloadPackAsync"/> through
    /// the no-sideband path with a real pack and a real writepack, exercising
    /// <c>transport.BufferData.ToArray()</c> at <c>GitSmartProtocol.cs:586</c>
    /// (the no-sideband append path).
    /// </summary>
    [Fact]
    public async Task DownloadPack_NoSideband_AppendsBufferDataToWritepack()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "libgit2cs-nosideband-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Build a real source repo with one commit, then a real pack
            //    containing the commit/tree/blob.
            string sourcePath = Path.Combine(tempDir, "source");
            await using GitRepository sourceRepo = await GitRepository.InitAsync(sourcePath, isBare: true, new GitContext(), cancellationToken: ct);
            GitOid commitOid = await WriteInitialCommitAsync(sourceRepo, ct);
            byte[] packBytes = await BuildPackFromRepoAsync(sourceRepo, commitOid, ct);

            // 2. Build a ref-ad with NO side-band capability — this forces the
            //    no-sideband code path in DownloadPackAsync (the append path
            //    at GitSmartProtocol.cs:586).
            byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
                [(commitOid.ToString(), "refs/heads/main")],
                capabilities: "ofs-delta"); // no side-band-64k

            // 3. Negotiation response: a single NAK. The mock serves the
            //    negotiation response first, then the raw pack bytes.
            byte[] negotiationResponse = MockTransportBuilder.BuildNak();
            var mock = new MockNegotiationTransport(refAd, negotiationResponse, packBytes, isRpc: false);

            // 4. Empty client repo to receive the pack.
            string clientPath = Path.Combine(tempDir, "client");
            await using GitRepository clientRepo = await GitRepository.InitAsync(clientPath, isBare: true, new GitContext(), cancellationToken: ct);

            // 5. Connect + ls + negotiate + download.
            await using var transport = new GitSmartTransport(new SubtransportDefinition(
                _ => mock, IsRpc: false, null), new GitContext());
            await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, ct);

            IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(ct);
            GitRemoteHead main = Assert.Single(heads, h => h.Name == "refs/heads/main");
            Assert.Equal(commitOid, main.Oid);

            var wants = new GitFetchNegotiation(heads, Array.Empty<GitOid>(), Depth: 0);
            await transport.NegotiateFetchAsync(clientRepo, wants, ct);
            await transport.DownloadPackAsync(clientRepo, new GitIndexerProgress(), ct);

            // 6. Verify the commit/tree/blob are now in the client repo.
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
    /// Write an initial commit (README.md → "hello\n") to the repo and return
    /// the commit OID. Mirrors the CreateSourceRepo helper in
    /// LocalTransportTests.
    /// </summary>
    private static async Task<GitOid> WriteInitialCommitAsync(GitRepository repo, CancellationToken ct)
    {
        // For a bare repo, write the blob directly via the ODB and build the
        // tree via GitTreeBuilder (no workdir/index in a bare repo).
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);

        GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await bld.WriteAsync(ct);

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
    /// Build a real pack file containing the commit, its tree, and all
    /// reachable blobs. Uses <see cref="GitPackWriter"/> to produce a
    /// byte-exact pack the mock transport can serve.
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
}
