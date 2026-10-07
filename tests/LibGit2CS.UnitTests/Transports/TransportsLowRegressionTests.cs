using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Transports;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Transports;

// Parity cases verified against libgit2 1.9.4:
//  - C's needs_probe compares the full offered mask (http.c:461-467) — a
//    server offering "Negotiate, Basic" with a default credential must NOT
//    fire the probe.
//  - C's git_socket__connect iterates every getaddrinfo result
//    (socket.c:185-224) — an IPv6-only listener must be reachable via a
//    hostname that resolves to ::1.
//  - push report/spec/ref sorting uses C's byte-wise strcmp
//    (push_status_ref_cmp/push_spec_rref_cmp/ref_name_cmp/git__strcmp_cb):
//    non-BMP ref names (e.g. U+FDFD vs U+1F600) sort in byte order.
//  - C fires the final push-transfer-progress report unconditionally
//    (smart_protocol.c:1254-1263), including pushes that send no pack.
//  - C's ref_pkt sets seen_capabilities = 1 unconditionally (smart_pkt.c:329)
//    and rejects a caps-bearing ref after a cap-less first ref with
//    "error parsing REF pkt-line".
public sealed class TransportsLowRegressionTests
{
    private const string Sha1Main = "a5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private static readonly string s_sha1Zero = new('0', 40);

    // Non-BMP ref names whose UTF-8 byte order and UTF-16 code-unit order
    // disagree: U+FDFD (ARABIC LIGATURE) encodes as EF B7 BD, U+1F600
    // (GRINNING FACE) as F0 9F 98 80 — byte-wise U+FDFD < U+1F600 (0xEF <
    // 0xF0), but UTF-16 ordinal U+1F600 < U+FDFD (0xD83D < 0xFDFD).
    private const string LigatureRef = "refs/heads/a\uFDFD";
    private const string EmojiRef = "refs/heads/a\uD83D\uDE00";

    // ─── NeedsProbe compares the full offered mask ────────────────

    /// <summary>
    /// A server offering "Negotiate, Basic" with a DEFAULT credential selects
    /// Negotiate (priority), but C's needs_probe (http.c:461-467) compares
    /// the full offered mask — Negotiate|Basic is neither NTLM nor Negotiate
    /// alone, so no probe fires.
    /// and sent a spurious "0000" probe POST.
    /// </summary>
    [Fact]
    public async Task ServerAuth_NegotiatePlusBasic_DefaultCredential_NoProbe()
    {
        var transport = new GitHttpTransport(new GitContext());
        await transport.ActionAsync("https://example.com/repo", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken);

        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Negotiate"));
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"test\""));

        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitDefaultCredential()),
            },
        };

        bool ok = await transport.HandleServerAuthAsync(response, options, TestContext.Current.CancellationToken);
        Assert.True(ok);

        // Negotiate is selected (priority), but the OFFERED mask is
        // Negotiate|Basic — C's needs_probe compares the full mask, so no
        // probe.
        Assert.Equal(GitAuthSchemeType.Negotiate, transport.ServerAuthSchemes);
        Assert.False(transport.NeedsProbe);
    }

    /// <summary>
    /// Positive control: a server offering ONLY Negotiate (with a default
    /// credential) still needs the probe — the offered mask IS Negotiate.
    /// </summary>
    [Fact]
    public async Task ServerAuth_NegotiateOnly_DefaultCredential_Probe()
    {
        var transport = new GitHttpTransport(new GitContext());
        await transport.ActionAsync("https://example.com/repo", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken);

        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Negotiate"));

        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitDefaultCredential()),
            },
        };

        bool ok = await transport.HandleServerAuthAsync(response, options, TestContext.Current.CancellationToken);
        Assert.True(ok);
        Assert.True(transport.NeedsProbe);
    }

    /// <summary>
    /// Positive control: a server offering ONLY NTLM (with a user/pass
    /// credential) still needs the probe — the offered mask IS NTLM.
    /// </summary>
    [Fact]
    public async Task ServerAuth_NtlmOnly_UserPassCredential_Probe()
    {
        var transport = new GitHttpTransport(new GitContext());
        await transport.ActionAsync("https://example.com/repo", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken);

        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("NTLM"));

        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitUserPassCredential("user", "pass")),
            },
        };

        bool ok = await transport.HandleServerAuthAsync(response, options, TestContext.Current.CancellationToken);
        Assert.True(ok);
        Assert.True(transport.NeedsProbe);
    }

    // ─── GitSocket iterates every resolved address ────────────────

    /// <summary>
    /// A hostname that resolves to ::1 must be able to reach a listener bound
    /// to the IPv6 loopback ONLY. C's git_socket__connect iterates every
    /// getaddrinfo result (socket.c:185-224), so an IPv6-only listener is
    /// reachable by hostname, not just "Connection refused" against
    /// 127.0.0.1.
    /// </summary>
    [Fact]
    public async Task Connect_HostnameResolvingToIPv6_ConnectsToIPv6OnlyListener()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // "localhost" must resolve to the IPv6 loopback for this test to be
        // meaningful (the listener is IPv6-only).
        IPAddress[] localAddrs = await Dns.GetHostAddressesAsync("localhost", ct);
        if (!localAddrs.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6))
        {
            Assert.Skip("localhost does not resolve to IPv6; skipping the regression test.");
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
        }
        catch (SocketException)
        {
            listener?.Dispose();
            Assert.Skip("IPv6 loopback unavailable; skipping the regression test.");
        }

        using (listener)
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var echoTask = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);
                    await using NetworkStream ns = client.GetStream();
                    byte[] buf = new byte[64];
                    int n = await ns.ReadAsync(buf, cts.Token);
                    await ns.WriteAsync(buf.AsMemory(0, n), cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }, ct);

            try
            {
                // "localhost" resolves to ::1 (and 127.0.0.1); only the
                // address-iteration fix can reach the IPv6-only listener.
                var socket = new GitSocket("localhost", port);
                await using (socket.ConfigureAwait(false))
                {
                    await socket.ConnectAsync(connectTimeoutMs: 5000, ct);
                    await socket.WriteAsync("ping"u8.ToArray(), ct);
                    byte[] reply = new byte[4];
                    int read = await socket.ReadAsync(reply, ct);
                    Assert.Equal(4, read);
                    Assert.Equal("ping"u8, reply);
                }
            }
            finally
            {
                await cts.CancelAsync();
                try
                {
                    await echoTask;
                }
                catch (Exception)
                {
                    // The echo task may have been cancelled mid-accept.
                }
            }
        }
    }

    // ─── byte-wise (strcmp) ordering for ref names ────────────────

    /// <summary>
    /// The core ordering divergence: for U+FDFD vs U+1F600, C's strcmp
    /// (byte order over UTF-8) sorts U+FDFD first, while UTF-16 ordinal
    /// sorts U+1F600 first.
    /// </summary>
    [Fact]
    public void BytewiseCompare_NonBmpRefNames_MatchesStrcmpOrder()
    {
        // UTF-8: U+FDFD = EF B7 BD < F0 9F 98 80 = U+1F600 (byte order).
        Assert.True(AsciiText.BytewiseCompare(LigatureRef, EmojiRef) < 0);
        // UTF-16 code units: U+1F600 = D83D DE00 < FDFD = U+FDFD — this is
        // the order string.CompareOrdinal returns, which differs from C.
        Assert.True(string.CompareOrdinal(LigatureRef, EmojiRef) > 0);
    }

    /// <summary>
    /// A push whose specs/report carry non-BMP ref names must surface the
    /// status list in C's byte-wise order (ValidatePushReport sorts the
    /// status list IN PLACE with push_status_ref_cmp, smart_protocol.c:1088),
    /// matching strcmp byte order rather than UTF-16 ordinal order.
    /// </summary>
    [Fact]
    public async Task Push_NonBmpRefNames_StatusSortedByteWise()
    {
        byte[] refAd = BuildRefAdvertisementUtf8(caps: "report-status", refs: [LigatureRef, EmojiRef]);
        byte[] report = BuildPushReportUtf8(refs: [LigatureRef, EmojiRef]);
        var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);
        Assert.True(transport.IsConnected);

        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = GitRefSpec.Parse($":{LigatureRef}", isFetch: false),
                Loid = default, // delete — no pack
                Roid = GitOid.Parse(Sha1Main, GitHashAlgorithmKind.Sha1),
            },
            new()
            {
                RefSpec = GitRefSpec.Parse($":{EmojiRef}", isFetch: false),
                Loid = default,
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
        Assert.Equal(2, result.Status.Count);

        // C sorts the status list in place with push_status_ref_cmp (strcmp
        // byte order): U+FDFD < U+1F600.
        Assert.Equal(LigatureRef, result.Status[0].Ref);
        Assert.Equal(EmojiRef, result.Status[1].Ref);
    }

    /// <summary>
    /// UpdateRefsFromPush's merge join must compare with the same byte-wise
    /// order used by the sorts (C: strcmp in update_refs_from_report,
    /// smart_protocol.c:1112-1132). With a byte-wise-sorted ref list and a
    /// UTF-16 join comparison, a spec whose name sorts before the ref it
    /// matches is misclassified as an "add" — the ref keeps its old OID and
    /// a duplicate is appended.
    /// </summary>
    [Fact]
    public async Task UpdateRefsFromPush_NonBmpRefNames_MergeJoinMatchesByteWise()
    {
        var oldOid1 = GitOid.Parse("1111111111111111111111111111111111111111", GitHashAlgorithmKind.Sha1);
        var oldOid2 = GitOid.Parse("2222222222222222222222222222222222222222", GitHashAlgorithmKind.Sha1);
        var newOid2 = GitOid.Parse("4444444444444444444444444444444444444444", GitHashAlgorithmKind.Sha1);

        // Advertise HEAD (oid = oldOid1) + both non-BMP refs.
        byte[] refAd = BuildRefAdvertisementUtf8(caps: "report-status", refs: [LigatureRef, EmojiRef], headOid: oldOid1, refOids: [oldOid1, oldOid2]);
        var mock = new PhasedMockSubtransport([refAd], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        // Push ONLY the emoji ref (its name sorts AFTER the ligature ref
        // byte-wise but BEFORE it in UTF-16 — the discriminating case).
        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = GitRefSpec.Parse($"{EmojiRef}:{EmojiRef}", isFetch: false),
                Loid = newOid2,
                Roid = oldOid2,
            },
        };
        var status = new List<GitPushStatus>
        {
            new() { Ref = EmojiRef, Ok = true, Message = null },
        };

        transport.UpdateRefsFromPush(specs, status);

        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);

        // HEAD + ligature (unchanged) + emoji (updated) — in byte-wise order.
        Assert.Equal(3, heads.Count);
        Assert.Equal("HEAD", heads[0].Name);
        Assert.Equal(LigatureRef, heads[1].Name);
        Assert.Equal(oldOid1, heads[1].Oid);
        Assert.Equal(EmojiRef, heads[2].Name);
        Assert.Equal(newOid2, heads[2].Oid);
    }

    // ─── final push-transfer-progress fires unconditionally ────────

    /// <summary>
    /// A delete-only push (all specs zero-Loid, no pack) must still fire the
    /// final push-transfer-progress report with 0/0/0 — C fires it
    /// unconditionally (smart_protocol.c:1254-1263), outside the needPack
    /// block.
    /// </summary>
    [Fact]
    public async Task Push_DeleteOnly_FiresFinalZeroProgressReport()
    {
        byte[] refAd = BuildRefAdvertisement(caps: "report-status side-band-64k");
        byte[] report = BuildPushReport(refs: ["refs/heads/main"]);
        var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        var progress = new CapturingProgress<GitPushTransferProgress>();
        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = GitRefSpec.Parse(":refs/heads/main", isFetch: false),
                Loid = default, // delete — no pack
                Roid = GitOid.Parse(Sha1Main, GitHashAlgorithmKind.Sha1),
            },
        };

        GitPushResult result = await transport.PushAsync(
            repo: null!,
            specs: specs,
            packWriter: null,
            callbacks: new GitRemoteCallbacks { PushTransferProgress = progress },
            reportStatus: true,
            pushOptions: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk);
        Assert.Single(result.Status);

        // The terminal 0/0/0 report must fire even though no pack was sent.
        GitPushTransferProgress final = Assert.Single(progress.Reports);
        Assert.Equal(0, final.Current);
        Assert.Equal(0, final.Total);
        Assert.Equal(0, final.Bytes);
    }

    /// <summary>
    /// A zero-spec push (the early-return path) must also fire the final
    /// push-transfer-progress report with 0/0/0 — C's git_smart__push fires
    /// it unconditionally after the (empty) command phase.
    /// </summary>
    [Fact]
    public async Task Push_ZeroSpecs_FiresFinalZeroProgressReport()
    {
        byte[] refAd = BuildRefAdvertisement(caps: "report-status");
        var mock = new PhasedMockSubtransport([refAd], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        var progress = new CapturingProgress<GitPushTransferProgress>();

        GitPushResult result = await transport.PushAsync(
            repo: null!,
            specs: [],
            packWriter: null,
            callbacks: new GitRemoteCallbacks { PushTransferProgress = progress },
            reportStatus: true,
            pushOptions: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk);
        Assert.Empty(result.Status);

        GitPushTransferProgress final = Assert.Single(progress.Reports);
        Assert.Equal(0, final.Current);
        Assert.Equal(0, final.Total);
        Assert.Equal(0, final.Bytes);
    }

    // ─── cap-less first ref marks capabilities as seen ───────────

    /// <summary>
    /// C's ref_pkt sets seen_capabilities = 1 unconditionally at the end of
    /// every ref parse (smart_pkt.c:329), so a caps-bearing ref AFTER a
    /// cap-less first ref hits "else goto out_err" — "error parsing REF
    /// pkt-line".
    /// </summary>
    [Fact]
    public void Parse_RefPacket_CapLessFirstThenCapsBearing_Throws()
    {
        // First ref: no capabilities.
        byte[] firstBuf = BuildPkt($"{s_sha1Zero} HEAD\n");
        var state = new GitPacketParseState();
        GitPacket pkt = GitPacketReader.Parse(firstBuf, out _, ref state);
        Assert.IsType<GitRefPacket>(pkt);

        // even a cap-less ref marks capabilities as seen.
        Assert.True(state.SeenCapabilities);

        // Second ref: caps-bearing → C: goto out_err.
        byte[] secondBuf = BuildPkt($"{Sha1Main} {EmojiRef}\0cap\n");
        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(secondBuf, out _, ref state));
        Assert.Equal("error parsing REF pkt-line", ex.Message);
    }

    /// <summary>
    /// Positive control: a cap-less first ref followed by a cap-less second
    /// ref parses fine (both are accepted, capabilities stay seen).
    /// </summary>
    [Fact]
    public void Parse_RefPacket_CapLessFirstThenCapLessSecond_Ok()
    {
        byte[] firstBuf = BuildPkt($"{s_sha1Zero} HEAD\n");
        var state = new GitPacketParseState();
        GitPacketReader.Parse(firstBuf, out _, ref state);
        Assert.True(state.SeenCapabilities);

        byte[] secondBuf = BuildPkt($"{Sha1Main} {LigatureRef}\n");
        GitPacket pkt2 = GitPacketReader.Parse(secondBuf, out _, ref state);
        GitRefPacket refPkt = Assert.IsType<GitRefPacket>(pkt2);
        Assert.Equal(LigatureRef, refPkt.Head.Name);
        Assert.Null(refPkt.Capabilities);
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Build a stateful (non-RPC) ref advertisement: HEAD with capabilities,
    /// then one ref per entry in <paramref name="refs"/> (UTF-8 encoded —
    /// non-BMP ref names must survive the wire), then a flush.
    /// </summary>
    private static byte[] BuildRefAdvertisementUtf8(string caps, IReadOnlyList<string> refs, GitOid? headOid = null, IReadOnlyList<GitOid>? refOids = null)
    {
        var result = new List<byte>();
        string headHex = headOid?.ToString() ?? s_sha1Zero;
        AppendPkt(result, $"{headHex} HEAD\0{caps}\n");
        for (int i = 0; i < refs.Count; i++)
        {
            string oidHex = refOids is not null && i < refOids.Count ? refOids[i].ToString() : Sha1Main;
            AppendPkt(result, $"{oidHex} {refs[i]}\n");
        }

        result.AddRange("0000"u8.ToArray());
        return [.. result];
    }

    /// <summary>
    /// Build a stateful ref advertisement for ASCII ref names (HEAD with
    /// capabilities + refs/heads/main + flush).
    /// </summary>
    private static byte[] BuildRefAdvertisement(string caps)
    {
        var sb = new StringBuilder();
        string firstRef = $"{s_sha1Zero} HEAD\0{caps}\n";
        int refLen = 4 + firstRef.Length;
        sb.Append(refLen.ToString("x4"));
        sb.Append(firstRef);
        string secondRef = $"{Sha1Main} refs/heads/main\n";
        int ref2Len = 4 + secondRef.Length;
        sb.Append(ref2Len.ToString("x4"));
        sb.Append(secondRef);
        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Build a push report-status response: <c>unpack ok</c>, one
    /// <c>ok &lt;ref&gt;</c> line per ref, then a flush (UTF-8 encoded).
    /// </summary>
    private static byte[] BuildPushReportUtf8(IReadOnlyList<string> refs)
    {
        var result = new List<byte>();
        AppendPkt(result, "unpack ok\n");
        foreach (string refName in refs)
        {
            AppendPkt(result, $"ok {refName}\n");
        }

        result.AddRange("0000"u8.ToArray());
        return [.. result];
    }

    /// <summary>
    /// Build a push report-status response for ASCII ref names.
    /// </summary>
    private static byte[] BuildPushReport(IReadOnlyList<string> refs)
    {
        var sb = new StringBuilder();
        string unpackLine = "unpack ok\n";
        int unpackLen = 4 + unpackLine.Length;
        sb.Append(unpackLen.ToString("x4")).Append(unpackLine);
        foreach (string refName in refs)
        {
            string okLine = $"ok {refName}\n";
            int okLen = 4 + okLine.Length;
            sb.Append(okLen.ToString("x4")).Append(okLine);
        }

        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Append a pkt-line whose length prefix is computed from the UTF-8 byte
    /// count (not the UTF-16 string length).
    /// </summary>
    private static void AppendPkt(List<byte> target, string content)
    {
        byte[] contentBytes = Encoding.UTF8.GetBytes(content);
        int len = 4 + contentBytes.Length;
        target.AddRange(Encoding.ASCII.GetBytes(len.ToString("x4")));
        target.AddRange(contentBytes);
    }

    /// <summary>Build a single pkt-line buffer (UTF-8).</summary>
    private static byte[] BuildPkt(string content)
    {
        var result = new List<byte>();
        AppendPkt(result, content);
        return [.. result];
    }

    /// <summary>
    /// Stateful mock subtransport that returns the SAME stream for every
    /// service call. The stream serves a scripted sequence of byte chunks
    /// across reads.
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

        public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IGitSubtransportStream>(_stream);
        }

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Mock stream that serves a scripted sequence of byte chunks. Each
    /// <see cref="ReadAsync"/> call returns up to <paramref name="buffer"/>
    /// bytes from the current chunk; a chunk larger than the read buffer is
    /// served across multiple reads. Returns EOF only after all chunks are
    /// fully consumed.
    /// </summary>
    private sealed class PhasedMockStream : IGitSubtransportStream
    {
        private readonly byte[][] _chunks;
        private int _phase;
        private int _phaseOffset;

        public PhasedMockStream(IReadOnlyList<byte[]> chunks)
        {
            _chunks = [.. chunks];
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (_phase < _chunks.Length && _phaseOffset >= _chunks[_phase].Length)
            {
                _phase++;
                _phaseOffset = 0;
            }

            if (_phase >= _chunks.Length)
            {
                return Task.FromResult<int>(0);
            }

            byte[] chunk = _chunks[_phase];
            int toRead = Math.Min(buffer.Length, chunk.Length - _phaseOffset);
            chunk.AsSpan(_phaseOffset, toRead).CopyTo(buffer.Span);
            _phaseOffset += toRead;
            return Task.FromResult<int>(toRead);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> capture: invokes the callback
    /// inline on the reporting thread.
    /// </summary>
    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }
}
