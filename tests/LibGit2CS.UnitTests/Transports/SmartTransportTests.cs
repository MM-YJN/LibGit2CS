using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Pack;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

public sealed class SmartTransportTests : IDisposable
{
    private const string Sha1Zero = "0000000000000000000000000000000000000000";
    private const string Sha1Main = "a5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";

    [Fact]
    public async Task Connect_Stateful_ReceivesRefs()
    {
        // Stateful transport (git://) expects 1 flush. The first ref must
        // carry capabilities or C leaves the heads list empty.
        byte[] mockData = BuildRefAdvertisement(isRpc: false, caps: "side-band-64k ofs-delta");
        var mock = new MockSubtransport(mockData, isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        Assert.True(transport.HaveRefs);

        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(heads);
        Assert.Equal("HEAD", heads[0].Name);
    }

    [Fact]
    public async Task Connect_Rpc_ReceivesRefs()
    {
        // RPC transport (HTTP) expects 2 flushes and a leading comment.
        byte[] mockData = BuildRefAdvertisement(isRpc: true, caps: "side-band-64k ofs-delta");
        var mock = new MockSubtransport(mockData, isRpc: true);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: true, null), new GitContext());

        await transport.ConnectAsync("http://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        Assert.True(transport.HaveRefs);

        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(heads);
    }

    [Fact]
    public async Task Connect_DetectsCapabilities()
    {
        byte[] mockData = BuildRefAdvertisement(isRpc: false, caps: "multi_ack_detailed side-band-64k ofs-delta");
        var mock = new MockSubtransport(mockData, isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        GitSmartCapabilitySet caps = transport.SmartCaps;
        Assert.True((caps.Flags & GitSmartCapabilities.MultiAckDetailed) != 0);
        Assert.True((caps.Flags & GitSmartCapabilities.SideBand64k) != 0);
        Assert.True((caps.Flags & GitSmartCapabilities.OfsDelta) != 0);
    }

    [Fact]
    public async Task Connect_EmptyRepo_RemovesCapabilitiesRef()
    {
        // Empty repo: only ref is "capabilities^{}" with zero OID (with
        // capabilities — the first ref always carries them in C).
        var sb = new StringBuilder();
        string content = $"{Sha1Zero} capabilities^{{}}\0side-band-64k\n";
        int len = 4 + content.Length;
        sb.Append(len.ToString("x4"));
        sb.Append(content);
        sb.Append("0000"); // flush

        var mock = new MockSubtransport(Encoding.ASCII.GetBytes(sb.ToString()), isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(heads);
    }

    [Fact]
    public async Task Ls_BeforeConnect_Throws()
    {
        var mock = new MockSubtransport([], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await Assert.ThrowsAsync<GitException>(async () => await transport.LsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancel_SetsCancelledFlag()
    {
        var mock = new MockSubtransport([], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        Assert.False(transport.IsCancelled);
        transport.Cancel();
        Assert.True(transport.IsCancelled);
    }

    [Fact]
    public async Task Close_SetsDisconnected()
    {
        byte[] mockData = BuildRefAdvertisement(isRpc: false);
        var mock = new MockSubtransport(mockData, isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        Assert.True(transport.IsConnected);

        await transport.CloseAsync(TestContext.Current.CancellationToken);
        Assert.False(transport.IsConnected);
    }

    /// <summary>
    /// C's
    /// git_smart__close does NOT clear t->refs or t->heads (smart.c:370-411)
    /// — the advertised heads survive close so a subsequent LsAsync (e.g.
    /// clone's checkout_branch, clone.c:214-223) sees them without
    /// reconnecting.
    /// </summary>
    [Fact]
    public async Task Close_RetainsHeadsForLs()
    {
        byte[] mockData = BuildRefAdvertisement(isRpc: false, caps: "symref=HEAD:refs/heads/main");
        var mock = new MockSubtransport(mockData, isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        IReadOnlyList<GitRemoteHead> before = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, before.Count);

        await transport.CloseAsync(TestContext.Current.CancellationToken);
        Assert.False(transport.IsConnected);

        // The advertised heads must survive close (C: git_smart__close).
        IReadOnlyList<GitRemoteHead> after = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, after.Count);
        Assert.Equal("HEAD", after[0].Name);
        Assert.Equal("refs/heads/main", after[1].Name);
    }

    [Fact]
    public async Task OidType_DefaultsToSha1()
    {
        byte[] mockData = BuildRefAdvertisement(isRpc: false);
        var mock = new MockSubtransport(mockData, isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.Equal(GitHashAlgorithmKind.Sha1, transport.OidType);
    }

    [Fact]
    public async Task OidType_Sha256_FromCapabilities()
    {
        byte[] mockData = BuildRefAdvertisement(isRpc: false, caps: "object-format=sha256", useSha256: true);
        var mock = new MockSubtransport(mockData, isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.Equal(GitHashAlgorithmKind.Sha256, transport.OidType);
    }

    [Fact]
    public async Task Symref_DetectedFromCapabilities()
    {
        byte[] mockData = BuildRefAdvertisement(isRpc: false, caps: "symref=HEAD:refs/heads/main");
        var mock = new MockSubtransport(mockData, isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        // HEAD should have symref target resolved
        GitRemoteHead head = Assert.Single(heads, h => h.Name == "HEAD");
        Assert.Equal("refs/heads/main", head.SymrefTarget);
    }

    // ── Push (stateful) ──────────────────────────────────────────────────

    /// <summary>
    /// Stateful (non-RPC) push: ConnectAsync(Push) consumes the ref
    /// advertisement, then PushAsync sends commands + reads the report-status.
    /// This closes a coverage gap — only RPC (HTTP) push was unit-tested
    /// before (see <c>HttpTransportTests.Push_OverHttp_ReportStatusReceived</c>);
    /// stateful push was only exercised by the Docker SSH integration test.
    /// </summary>
    /// <remarks>
    /// The mock scripts the read sequence: the ref advertisement + flush is
    /// served first (consumed by <c>StoreRefsAsync</c>), then the push report
    /// (<c>unpack ok</c> / <c>ok refs/heads/main</c> / flush) is served for
    /// <c>ParsePushReportAsync</c>. A delete spec (loid=zero) is used so no
    /// pack is required. Uses <see cref="PhasedMockSubtransport"/> which
    /// returns the SAME stream for both service calls (parity with stateful
    /// SSH/git transports where <c>ActionAsync</c> reuses one stream).
    /// </remarks>
    [Fact]
    public async Task Push_Stateful_ParsesReport()
    {
        byte[] refAd = BuildRefAdvertisement(isRpc: false, caps: "report-status side-band-64k");
        byte[] report = BuildPushReport(refs: ["refs/heads/main"]);
        var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);
        Assert.True(transport.IsConnected);

        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = LibGit2CS.Refs.GitRefSpec.Parse(":refs/heads/main", isFetch: false),
                Loid = default, // delete — no pack
                Roid = GitOid.Parse(Sha1Main, GitHashAlgorithmKind.Sha1),
            },
        };

        GitPushResult result = await transport.PushAsync(
            repo: null!,
            specs: specs,
            packWriter: null,
            callbacks: null,
            reportStatus: true,
            pushOptions: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk);
        Assert.Single(result.Status);
        Assert.True(result.Status[0].Ok);
        Assert.Equal("refs/heads/main", result.Status[0].Ref);
    }

    /// <summary>
    /// Stateful push where the server wraps its report-status in a side-band
    /// data channel (band 0x01). This is what <c>git-receive-pack</c> actually
    /// does when the client advertises <c>side-band-64k</c> (which
    /// <c>GeneratePushPktline</c> always requests). Reproduces and guards the
    /// double-band-byte bug: <c>ParsePushReportAsync</c> parses the inner
    /// pkt-lines directly from <c>GitDataPacket.Data</c> and must not re-check
    /// <c>dataPkt.Data[0]</c> as a band ID, since <c>GitPacketReader</c> has
    /// already stripped the 0x01 band byte (the inner pkt-line's first
    /// length hex digit '0' = 0x30 matches no band, which would silently drop
    /// the report and yield <c>UnpackOk=False, Status=empty</c>).
    /// </summary>
    /// <remarks>
    /// The mock serves the report as a single side-band data packet.
    /// </remarks>
    [Fact]
    public async Task Push_Stateful_SidebandReport_ParsesReport()
    {
        byte[] refAd = BuildRefAdvertisement(isRpc: false, caps: "report-status side-band-64k");
        byte[] report = BuildPushReportSideband(refs: ["refs/heads/main"]);
        var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);
        Assert.True(transport.IsConnected);

        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = LibGit2CS.Refs.GitRefSpec.Parse(":refs/heads/main", isFetch: false),
                Loid = default, // delete — no pack
                Roid = GitOid.Parse(Sha1Main, GitHashAlgorithmKind.Sha1),
            },
        };

        GitPushResult result = await transport.PushAsync(
            repo: null!,
            specs: specs,
            packWriter: null,
            callbacks: null,
            reportStatus: true,
            pushOptions: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk, $"expected UnpackOk=True, got False; status=[{string.Join(", ", result.Status.Select(s => $"{(s.Ok ? "ok" : "ng")} {s.Ref}"))}]");
        Assert.Single(result.Status);
        Assert.True(result.Status[0].Ok);
        Assert.Equal("refs/heads/main", result.Status[0].Ref);
    }

    /// <summary>
    /// <see cref="GitSmartTransport.GetPushStreamAsync"/> must clear the
    /// receive buffer for stateful (non-RPC) transports, not just for RPC.
    /// Reproduces the push buffer pollution: after ConnectAsync(Push), the
    /// buffer may hold leftover bytes (the SSH channel reads ahead past the
    /// ref-advertisement flush). Without the clear, ParsePushReportAsync
    /// reads the stale flush and returns UnpackOk=False immediately. Here we
    /// simulate the read-ahead by appending a stray flush to the ref-ad
    /// chunk and assert the buffer is cleared by GetPushStreamAsync.
    /// </summary>
    [Fact]
    public async Task GetPushStreamAsync_Stateful_ClearsLeftoverBuffer()
    {
        // Ref advertisement + flush, then a STRAY flush that simulates the
        // SSH channel reading ahead past the ref-ad flush. StoreRefsAsync
        // consumes the ref-ad + first flush (flushes=1) and leaves the stray
        // flush in the smart-transport buffer.
        byte[] refAd = BuildRefAdvertisement(isRpc: false, caps: "report-status");
        byte[] strayFlush = "0000"u8.ToArray();
        byte[] combined = new byte[refAd.Length + strayFlush.Length];
        refAd.CopyTo(combined, 0);
        strayFlush.CopyTo(combined, refAd.Length);

        var mock = new PhasedMockSubtransport([combined, []], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        // The stray flush survived StoreRefsAsync (which only consumes one
        // flush) — confirm the precondition: the buffer is non-empty.
        Assert.True(transport.BufferData.Length > 0,
            $"expected leftover bytes in buffer after ConnectAsync(Push), got {transport.BufferData.Length}");

        // GetPushStreamAsync must clear the buffer so the push-report parse
        // isn't polluted by the stray flush.
        await transport.GetPushStreamAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.BufferData.Length);
    }

    /// <summary>
    /// Pushing a ref that is already up-to-date (loid == roid, both non-zero)
    /// must still send a pack-file on the wire — the git protocol spec
    /// requires it ("A pack-file MUST be sent … even if the server already
    /// has all the necessary objects", push.c:451-455). With the
    /// PushCoordinator fix, <paramref name="packWriter"/> is non-null for
    /// non-delete pushes, and <see cref="GitSmartProtocol.PushAsync"/> sends
    /// the (empty) pack because <c>needPack</c> is true whenever
    /// <c>loid != zero</c>. This test captures the bytes written to the mock
    /// subtransport stream and verifies the PACK magic + zero-object header
    /// appears after the pkt-line commands.
    /// </summary>
    [Fact]
    public async Task Push_UpToDateRef_SendsEmptyPack()
    {
        // GitPackWriter needs a real repo (accesses Objects, ObjectFormat).
        string tempPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_EmptyPack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using GitRepository packRepo = await GitRepository.InitAsync(tempPath, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            GitPackWriter packWriter = packRepo.NewPackWriter();

            byte[] refAd = BuildRefAdvertisement(isRpc: false, caps: "report-status");
            byte[] report = BuildPushReport(refs: ["refs/heads/main"]);
            var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
            await using var transport = new GitSmartTransport(new SubtransportDefinition(
                _ => mock, IsRpc: false, null), new GitContext());

            await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);
            Assert.True(transport.IsConnected);

            var oid = GitOid.Parse(Sha1Main, GitHashAlgorithmKind.Sha1);
            var specs = new List<GitPushSpec>
            {
                new()
                {
                    RefSpec = LibGit2CS.Refs.GitRefSpec.Parse("refs/heads/main:refs/heads/main", isFetch: false),
                    Loid = oid, // non-zero → needPack = true
                    Roid = oid, // same → up-to-date
                },
            };

            GitPushResult result = await transport.PushAsync(
                repo: null!,
                specs: specs,
                packWriter: packWriter,
                callbacks: null,
                reportStatus: true,
                pushOptions: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.UnpackOk);
            Assert.Single(result.Status);
            Assert.True(result.Status[0].Ok);

            // Verify the empty pack was sent on the wire. The captured writes
            // contain: pkt-line commands (ASCII), then the pack header +
            // trailer (binary). Find the PACK magic to locate the pack.
            byte[] captured = [.. mock.CapturedWrites];

            int packOffset = -1;
            for (int i = 0; i < captured.Length - 3; i++)
            {
                if (captured[i] == (byte)'P' && captured[i + 1] == (byte)'A' &&
                    captured[i + 2] == (byte)'C' && captured[i + 3] == (byte)'K')
                {
                    packOffset = i;
                    break;
                }
            }

            Assert.True(packOffset >= 0, "PACK magic not found in captured wire writes");

            // Verify the 12-byte pack header: PACK + version 2 + 0 objects.
            Assert.Equal("PACK"u8, captured.AsSpan(packOffset, 4).ToArray());
            Assert.Equal(0, captured[packOffset + 4]); // version MSB
            Assert.Equal(0, captured[packOffset + 5]);
            Assert.Equal(0, captured[packOffset + 6]);
            Assert.Equal(2, captured[packOffset + 7]); // version 2
            Assert.Equal(0, captured[packOffset + 8]); // object count MSB
            Assert.Equal(0, captured[packOffset + 9]);
            Assert.Equal(0, captured[packOffset + 10]);
            Assert.Equal(0, captured[packOffset + 11]); // 0 objects

            // After the 12-byte header comes the 20-byte SHA-1 trailer (no
            // object data in between).
            Assert.Equal(packOffset + 12 + 20, captured.Length);

            packWriter.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(tempPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Build a ref advertisement that the SmartTransport can parse.
    /// </summary>
    private static byte[] BuildRefAdvertisement(bool isRpc, string? caps = null, bool useSha256 = false)
    {
        var sb = new StringBuilder();
        int oidLen = useSha256 ? 64 : 40;
        string oidZero = new('0', oidLen);
        string oidMain = useSha256 ? new('a', oidLen) : Sha1Main;

        if (isRpc)
        {
            // RPC: leading comment packet
            string comment = "# service=git-upload-pack\n";
            int cmtLen = 4 + comment.Length;
            sb.Append(cmtLen.ToString("x4"));
            sb.Append(comment);
            sb.Append("0000"); // first flush for RPC
        }

        // First ref: HEAD with capabilities
        string firstRef = caps is null
            ? $"{oidZero} HEAD\n"
            : $"{oidZero} HEAD\0{caps}\n";
        int refLen = 4 + firstRef.Length;
        sb.Append(refLen.ToString("x4"));
        sb.Append(firstRef);

        // Second ref: refs/heads/main
        string secondRef = $"{oidMain} refs/heads/main\n";
        int ref2Len = 4 + secondRef.Length;
        sb.Append(ref2Len.ToString("x4"));
        sb.Append(secondRef);

        // Flush
        sb.Append("0000");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Build a push report-status response: <c>unpack ok</c>, one
    /// <c>ok &lt;ref&gt;</c> line per ref, then a flush. Matches the format
    /// the real <c>git-receive-pack</c> sends (see
    /// <c>HttpTestServer</c>'s receive-pack handler).
    /// </summary>
    private static byte[] BuildPushReport(IReadOnlyList<string> refs)
    {
        var sb = new StringBuilder();

        // unpack ok
        string unpackLine = "unpack ok\n";
        int unpackLen = 4 + unpackLine.Length;
        sb.Append(unpackLen.ToString("x4")).Append(unpackLine);

        // ok <ref> for each ref
        foreach (string refName in refs)
        {
            string okLine = $"ok {refName}\n";
            int okLen = 4 + okLine.Length;
            sb.Append(okLen.ToString("x4")).Append(okLine);
        }

        // Flush
        sb.Append("0000");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Build a push report-status response wrapped in a side-band data
    /// channel (band 0x01), the way <c>git-receive-pack</c> sends it when the
    /// client advertises <c>side-band-64k</c>. The outer pkt-line payload is
    /// <c>0x01</c> (the band byte, which <c>GitPacketReader</c> strips to
    /// produce a <c>GitDataPacket</c>) followed by the inner report pkt-lines
    /// (<c>unpack ok</c> / <c>ok &lt;ref&gt;</c> / inner flush). The outer
    /// stream ends with a flush pkt-line after the data packet.
    /// </summary>
    private static byte[] BuildPushReportSideband(IReadOnlyList<string> refs)
    {
        // Build the inner report pkt-line stream (what the data band carries).
        var inner = new StringBuilder();
        string unpackLine = "unpack ok\n";
        int unpackLen = 4 + unpackLine.Length;
        inner.Append(unpackLen.ToString("x4")).Append(unpackLine);
        foreach (string refName in refs)
        {
            string okLine = $"ok {refName}\n";
            int okLen = 4 + okLine.Length;
            inner.Append(okLen.ToString("x4")).Append(okLine);
        }
        inner.Append("0000"); // inner flush — ends the report-status stream

        // Wrap the inner stream in a side-band data pkt-line: the payload is
        // 0x01 (band=data) + the inner stream bytes. The pkt-line length
        // prefix covers both.
        byte[] innerBytes = Encoding.ASCII.GetBytes(inner.ToString());
        byte[] payload = new byte[1 + innerBytes.Length];
        payload[0] = GitPacketReader.SideBandData; // 0x01
        innerBytes.CopyTo(payload, 1);

        var sb = new StringBuilder();
        int outerLen = 4 + payload.Length;
        sb.Append(outerLen.ToString("x4"));
        // Append the payload bytes (the 0x01 band byte is not printable, so
        // build the outer packet via byte concat, not the StringBuilder).
        byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
        byte[] flush = "0000"u8.ToArray();

        byte[] result = new byte[header.Length + payload.Length + flush.Length];
        header.CopyTo(result, 0);
        payload.CopyTo(result, header.Length);
        flush.CopyTo(result, header.Length + payload.Length);
        return result;
    }

    /// <summary>
    /// Mock subtransport that feeds pre-built data and collects writes.
    /// </summary>
    private sealed class MockSubtransport : IGitSubtransport
    {
        private readonly byte[] _data;
        private readonly SharedOffset _offset = new();

        public MockSubtransport(byte[] data, bool isRpc)
        {
            _data = data;
            IsRpc = isRpc;
        }

        public bool IsRpc { get; }

        public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IGitSubtransportStream>(new MockStream(_data, _offset));
        }

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Shared mutable offset for mock stream reads.</summary>
    private sealed class SharedOffset
    {
        public int Value;
    }

    /// <summary>
    /// Mock stream that reads from a pre-built byte array.
    /// </summary>
    private sealed class MockStream : IGitSubtransportStream
    {
        private readonly byte[] _data;
        private readonly SharedOffset _offset;

        public MockStream(byte[] data, SharedOffset offset)
        {
            _data = data;
            _offset = offset;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_offset.Value >= _data.Length)
            {
                return Task.FromResult<int>(0); // EOF
            }

            int toRead = Math.Min(buffer.Length, _data.Length - _offset.Value);
            _data.AsSpan(_offset.Value, toRead).CopyTo(buffer.Span);
            _offset.Value += toRead;
            return Task.FromResult<int>(toRead);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            // Collect writes for testing
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stateful mock subtransport that returns the SAME stream for both the
    /// <c>*Ls</c> and the <c>ReceivePack</c>/<c>UploadPack</c> service calls
    /// (parity with SSH/git transports where one channel is reused). The
    /// stream serves a scripted sequence of byte chunks across reads:
    /// chunk 0 is returned by the first <see cref="IGitSubtransportStream.ReadAsync"/>
    /// call, chunk 1 by the next, etc. This lets a test serve the ref
    /// advertisement in the first read and the push report in a later read,
    /// reproducing the stateful push flow without a real server.
    /// </summary>
    private sealed class PhasedMockSubtransport : IGitSubtransport
    {
        private readonly PhasedMockStream _stream;

        public PhasedMockSubtransport(IReadOnlyList<byte[]> chunks, bool isRpc)
        {
            _stream = new PhasedMockStream(chunks);
            IsRpc = isRpc;
        }

        public bool IsRpc { get; }

        /// <summary>All bytes written to the stream across all <see cref="WriteAsync"/> calls.</summary>
        public IReadOnlyList<byte> CapturedWrites => _stream.Writes;

        public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
        {
            // Stateful: always return the same stream instance.
            return Task.FromResult<IGitSubtransportStream>(_stream);
        }

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Mock stream that serves a scripted sequence of byte chunks. Each
    /// <see cref="ReadAsync"/> call returns the next chunk in full (or EOF
    /// once all chunks are exhausted). A chunk may be empty (returns EOF
    /// immediately for that phase).
    /// </summary>
    private sealed class PhasedMockStream : IGitSubtransportStream
    {
        private readonly byte[][] _chunks;
        private int _phase;
        private readonly List<byte> _writes = [];

        public PhasedMockStream(IReadOnlyList<byte[]> chunks)
        {
            _chunks = [.. chunks];
        }

        /// <summary>All bytes written to this stream across all <see cref="WriteAsync"/> calls.</summary>
        public IReadOnlyList<byte> Writes => _writes;

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_phase >= _chunks.Length)
            {
                return Task.FromResult<int>(0); // EOF
            }

            byte[] chunk = _chunks[_phase++];
            int toRead = Math.Min(buffer.Length, chunk.Length);
            chunk.AsSpan(0, toRead).CopyTo(buffer.Span);
            return Task.FromResult<int>(toRead);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            _writes.AddRange(data.Span);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
    }
}
