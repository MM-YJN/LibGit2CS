using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Regression tests for the transports/remote parity behaviors in
/// libgit2 1.9.4:
///
/// <list type="bullet">
/// <item>push-options pkt-lines carry no trailing newline.</item>
/// <item>packet-parse length errors use -1 (GIT_ERROR) with C's
/// messages ("bad packet length", "unexpected pack file", "Invalid empty
/// packet").</item>
/// <item>packet-type dispatch uses C's shorter prefixes ("ERR",
/// "ok", "ng"), routing malformed prefixes to the specific parsers.</item>
/// <item>shallow/unshallow packets require an exact OID length.</item>
/// <item>a malformed symref= capability aborts the connect
/// ("remote sent invalid symref" / "invalid response").</item>
/// <item>no-capabilities refs leave the heads list empty (C
/// git_smart__connect only calls update_heads when caps were found).</item>
/// <item>push report-status is validated against the specs
/// ("report-status: protocol error").</item>
/// <item>leftover partial inner pkt-lines at the outer flush are
/// "incomplete pack data pkt-line"; the inner flush does not end the read.</item>
/// <item>local push deleting a nonexistent ref is success.</item>
/// </list>
/// </summary>
public sealed class TransportsMedParityTests2 : IDisposable
{
    private const string Sha1Zero = "0000000000000000000000000000000000000000";
    private const string Sha1Main = "a5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private readonly string _tempDir;

    public TransportsMedParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_TransportsMed2_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── packet-parse length errors (code -1, C messages) ──────

    [Fact]
    public void Parse_PackPrefix_ThrowsUnexpectedPackFile()
    {
        // C (smart_pkt.c:594-597): a raw "PACK" length prefix is
        // "unexpected pack file" (GIT_ERROR, -1).
        byte[] buffer = "PACKabcdef"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("unexpected pack file", ex.Message);
        Assert.Equal(GitErrorCategory.Net, ex.Category);
    }

    [Fact]
    public void Parse_BadHexLength_ThrowsBadPacketLength()
    {
        // C (smart_pkt.c:594-597): any non-PACK length-prefix parse failure
        // is "bad packet length" (the parse_len "invalid hex digit" message
        // is overwritten by git_pkt_parse_line).
        byte[] buffer = "GGGGthis is not hex"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("bad packet length", ex.Message);
    }

    [Fact]
    public void Parse_ShortLength_ThrowsGenericError()
    {
        // C (smart_pkt.c:613-615): a length in 1..3 is a bare GIT_ERROR (-1)
        // with no message set.
        byte[] buffer = "0001x"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public void Parse_EmptyPacket_ThrowsInvalidEmptyPacket()
    {
        // C (smart_pkt.c:617-621): "Invalid empty packet" (capital I),
        // GIT_ERROR (-1).
        byte[] buffer = "0004"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("Invalid empty packet", ex.Message);
    }

    [Fact]
    public void TryParse_BadHexLength_ReportsInvalidHexLength()
    {
        byte[] buffer = "00xy"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        bool result = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out _, out GitPacketParseError error, ref state);

        Assert.False(result);
        Assert.Equal(GitPacketParseError.InvalidHexLength, error);
        Assert.Null(pkt);
    }

    [Fact]
    public void TryParse_EmptyPacket_ReportsInvalidEmptyPacket()
    {
        byte[] buffer = "0004"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        bool result = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out _, out GitPacketParseError error, ref state);

        Assert.False(result);
        Assert.Equal(GitPacketParseError.InvalidEmptyPacket, error);
        Assert.Null(pkt);
    }

    [Fact]
    public async Task Connect_BadHexLength_SmartTransportThrowsBadPacketLength()
    {
        // The smart transport's receive loop surfaces the same error.
        var mock = new PhasedMockSubtransport(["GGGGthis is not hex"u8.ToArray(), []], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("bad packet length", ex.Message);
    }

    [Fact]
    public async Task Connect_PackPrefix_SmartTransportThrowsUnexpectedPackFile()
    {
        var mock = new PhasedMockSubtransport(["PACKabcdef"u8.ToArray(), []], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("unexpected pack file", ex.Message);
    }

    // ── dispatch on C's shorter prefixes ──────────────────────

    [Fact]
    public void Parse_ErrWithoutSpace_RoutesToErrParser()
    {
        // C (smart_pkt.c:645): "ERRfoo" dispatches on the 3-char "ERR"
        // prefix; err_pkt then requires "ERR " and fails with
        // "error parsing ERR pkt-line".
        byte[] buffer = "000bERRfoo\n"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("error parsing ERR pkt-line", ex.Message);
    }

    [Fact]
    public void Parse_OkWithoutSpace_RoutesToOkParser()
    {
        // C (smart_pkt.c:650): "okfoo" → ok_pkt requires "ok " →
        // "error parsing OK pkt-line".
        byte[] buffer = "000aokfoo\n"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("error parsing OK pkt-line", ex.Message);
    }

    [Fact]
    public void Parse_NgWithoutSpace_RoutesToNgParser()
    {
        // C (smart_pkt.c:653): "ngfoo" → ng_pkt requires "ng " →
        // "invalid packet line".
        byte[] buffer = "000angfoo\n"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid packet line", ex.Message);
    }

    [Fact]
    public void Parse_ShallowWithoutSpace_RoutesToShallowParser()
    {
        // C (smart_pkt.c:655): "shallowfoo" → shallow_pkt requires
        // "shallow " → "invalid packet line".
        byte[] buffer = "000fshallowfoo\n"u8.ToArray();
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid packet line", ex.Message);
    }

    // ── shallow/unshallow exact OID length ───────────────────

    [Fact]
    public void Parse_ShallowWithTrailingBytes_ThrowsInvalidPacketLine()
    {
        // C (smart_pkt.c:446-522): the payload after "shallow " must be
        // exactly the OID hex length; trailing bytes are an error.
        byte[] buffer = Encoding.ASCII.GetBytes($"003ashallow {Sha1Main}extra\n");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid packet line", ex.Message);
    }

    [Fact]
    public void Parse_UnshallowWithTrailingBytes_ThrowsInvalidPacketLine()
    {
        byte[] buffer = Encoding.ASCII.GetBytes($"003cunshallow {Sha1Main}extra\n");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid packet line", ex.Message);
    }

    // ── malformed symref capability ──────────────────────────

    [Fact]
    public void DetectCapabilities_InvalidSymrefName_ThrowsRemoteSentInvalidSymref()
    {
        // C (smart_protocol.c:89-135): the symref value is parsed as a fetch
        // refspec; an unparseable value sets GIT_ERROR_NET
        // "remote sent invalid symref".
        var refPkt = new GitRefPacket(
            new GitRemoteHead(false, default, default, "HEAD", null),
            "symref=HEAD:refs/heads/bad~name");
        var set = new GitSmartCapabilitySet();

        GitException ex = Assert.Throws<GitException>(() => GitSmartProtocol.DetectCapabilities(refPkt, set));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("remote sent invalid symref", ex.Message);
    }

    [Fact]
    public void DetectCapabilities_SymrefNoColon_AddsRefspecMapping()
    {
        // C: "symref=HEAD" parses as the fetch refspec "HEAD" (src HEAD,
        // dst empty) — it is NOT an error, and the mapping is added.
        var refPkt = new GitRefPacket(
            new GitRemoteHead(false, default, default, "HEAD", null),
            "symref=HEAD");
        var set = new GitSmartCapabilitySet();

        bool found = GitSmartProtocol.DetectCapabilities(refPkt, set);

        Assert.True(found);
        Assert.Single(set.Symrefs);
        Assert.Equal(("HEAD", string.Empty), set.Symrefs[0]);
    }

    [Fact]
    public async Task Connect_InvalidSymref_ThrowsInvalidResponse()
    {
        // C (smart.c:210-216): a detect_caps failure other than ENOTFOUND
        // aborts the connect with GIT_ERROR_NET "invalid response" (the
        // append_symref message is overwritten by the caller).
        byte[] refAd = BuildRefAdvertisement(caps: "symref=HEAD:refs/heads/bad~name");
        var mock = new PhasedMockSubtransport([refAd], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid response", ex.Message);
    }

    // ── no-capabilities refs leave heads empty ───────────────

    [Fact]
    public void DetectCapabilities_UnknownCapsOnly_ReturnsTrue()
    {
        // C (smart_protocol.c:265-267): detect_caps succeeds whenever the
        // first ref carries a capabilities string, even all-unknown.
        var refPkt = new GitRefPacket(
            new GitRemoteHead(false, default, default, "HEAD", null),
            "unknown-cap foo bar");
        var set = new GitSmartCapabilitySet();

        bool found = GitSmartProtocol.DetectCapabilities(refPkt, set);

        Assert.True(found);
        Assert.Equal(GitSmartCapabilities.None, set.Flags);
    }

    [Fact]
    public async Task Connect_NoCapabilities_LsReturnsEmpty()
    {
        // C (smart.c:202-218): without a capabilities string on the first
        // ref, git_smart__connect leaves heads empty — git_remote_ls returns
        // nothing even though refs were advertised.
        // update_heads unconditionally.
        byte[] refAd = BuildRefAdvertisement(caps: null);
        var mock = new PhasedMockSubtransport([refAd], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(heads);
    }

    // ── push-options pkt-lines have no trailing newline ───────

    [Fact]
    public async Task Push_PushOptions_WrittenWithoutTrailingNewline()
    {
        // C (smart_protocol.c:853-858): each push option is written as
        // "%04x%s" with strlen(option)+4 — no trailing newline.
        byte[] refAd = BuildRefAdvertisement(caps: "report-status side-band-64k push-options");
        byte[] report = BuildPushReport(["refs/heads/main"]);
        var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = GitRefSpec.Parse(":refs/heads/main", isFetch: false),
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
            pushOptions: ["opt1"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk);

        byte[] captured = [.. mock.CapturedWrites];
        string wire = Encoding.ASCII.GetString(captured);
        // "opt1" is 4 bytes: pkt length 4+4 = 8 → "0008opt1".
        Assert.Contains("0008opt1", wire);
        Assert.DoesNotContain("0009opt1", wire);
    }

    // ── report-status validated against specs ────────────────

    [Fact]
    public async Task Push_ReportRefMismatch_ThrowsProtocolError()
    {
        // C (smart_protocol.c:1067-1102, update_refs_from_report): each
        // report ref must match the spec's dst, else "report-status:
        // protocol error".
        byte[] refAd = BuildRefAdvertisement(caps: "report-status side-band-64k");
        byte[] report = BuildPushReport(["refs/heads/OTHER"]);
        var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = GitRefSpec.Parse(":refs/heads/main", isFetch: false),
                Loid = default,
                Roid = GitOid.Parse(Sha1Main, GitHashAlgorithmKind.Sha1),
            },
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.PushAsync(
                repo: null!,
                specs: specs,
                packWriter: null,
                callbacks: null,
                reportStatus: true,
                pushOptions: null,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("report-status: protocol error", ex.Message);
    }

    [Fact]
    public async Task Push_ReportStatusCountMismatch_ThrowsProtocolError()
    {
        // C: push_specs->length != push_report->length → protocol error.
        byte[] refAd = BuildRefAdvertisement(caps: "report-status side-band-64k");
        byte[] report = BuildPushReport(["refs/heads/a"]);
        var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        var specs = new List<GitPushSpec>
        {
            new() { RefSpec = GitRefSpec.Parse(":refs/heads/a", isFetch: false), Loid = default, Roid = default },
            new() { RefSpec = GitRefSpec.Parse(":refs/heads/b", isFetch: false), Loid = default, Roid = default },
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.PushAsync(
                repo: null!,
                specs: specs,
                packWriter: null,
                callbacks: null,
                reportStatus: true,
                pushOptions: null,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("report-status: protocol error", ex.Message);
    }

    // ── leftover partial pkt-line at the outer flush ─────────

    [Fact]
    public async Task Push_ReportPartialInnerPktline_ThrowsIncompletePackDataPktline()
    {
        // C (smart_protocol.c:1028-1038): leftover bytes in the side-band
        // data buffer at the outer flush are "incomplete pack data pkt-line".
        // Serve a side-band data packet whose inner stream ends with a
        // truncated pkt-line length prefix ("000"), then the outer flush.
        byte[] refAd = BuildRefAdvertisement(caps: "report-status side-band-64k");
        byte[] dataPkt = BuildSidebandDataPacket("000eunpack ok\n"u8.ToArray().Concat("000"u8.ToArray()).ToArray());
        var mock = new PhasedMockSubtransport([refAd, dataPkt, "0000"u8.ToArray()], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        var specs = new List<GitPushSpec>
        {
            new() { RefSpec = GitRefSpec.Parse(":refs/heads/main", isFetch: false), Loid = default, Roid = default },
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.PushAsync(
                repo: null!,
                specs: specs,
                packWriter: null,
                callbacks: null,
                reportStatus: true,
                pushOptions: null,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("incomplete pack data pkt-line", ex.Message);
    }

    [Fact]
    public async Task Push_SidebandInnerFlush_ConsumesOuterFlush()
    {
        // C: the inner flush ("0000" inside the band-1 data) ends only the
        // report-status stream; parse_report keeps reading outer packets
        // until the OUTER flush.
        // leaving the outer flush unread in the transport buffer.
        byte[] refAd = BuildRefAdvertisement(caps: "report-status side-band-64k");
        byte[] dataPkt = BuildSidebandDataPacket("000eunpack ok\n0017ok refs/heads/main\n0000"u8.ToArray());
        // Serve the data packet and the OUTER flush in the same read so the
        // outer flush sits in the transport buffer when the inner flush is
        // parsed (the report loop must not return at the inner flush and
        // leave it unread).
        var mock = new PhasedMockSubtransport([refAd, Combine(dataPkt, "0000"u8.ToArray())], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        var specs = new List<GitPushSpec>
        {
            new() { RefSpec = GitRefSpec.Parse(":refs/heads/main", isFetch: false), Loid = default, Roid = default },
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
        Assert.Equal("refs/heads/main", result.Status[0].Ref);
        // The outer flush must have been consumed.
        Assert.Equal(0, transport.BufferData.Length);
    }

    // ── local push delete of a nonexistent ref is success ────

    [Fact]
    public async Task LocalPush_DeleteNonexistentRef_ReportsOk()
    {
        // C (local.c:356-366): deleting a ref that does not exist locally is
        // success (GIT_ENOTFOUND swallowed) — the status is "ok".
        string sourcePath = Path.Combine(_tempDir, "lpd-source");
        string targetPath = Path.Combine(_tempDir, "lpd-target");
        await using GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await using GitRepository target = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitRemote remote = await source.RemoteCreateAsync("origin", FixtureLoader.TestFileUrl(targetPath), TestContext.Current.CancellationToken);

        // Push a deletion for a ref that was never created on the target.
        GitPushResult result = await remote.PushAsync([":refs/heads/master"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.UnpackOk);
        Assert.Single(result.Status);
        Assert.True(result.Status[0].Ok, "deleting a nonexistent ref must succeed (C swallows GIT_ENOTFOUND)");
        Assert.Equal("refs/heads/master", result.Status[0].Ref);

        await remote.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static byte[] BuildRefAdvertisement(string? caps)
    {
        var sb = new StringBuilder();

        string firstRef = caps is null
            ? $"{Sha1Zero} HEAD\n"
            : $"{Sha1Zero} HEAD\0{caps}\n";
        sb.Append((4 + firstRef.Length).ToString("x4")).Append(firstRef);

        string secondRef = $"{Sha1Main} refs/heads/main\n";
        sb.Append((4 + secondRef.Length).ToString("x4")).Append(secondRef);

        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPushReport(IReadOnlyList<string> refs)
    {
        var sb = new StringBuilder();
        string unpackLine = "unpack ok\n";
        sb.Append((4 + unpackLine.Length).ToString("x4")).Append(unpackLine);
        foreach (string refName in refs)
        {
            string okLine = $"ok {refName}\n";
            sb.Append((4 + okLine.Length).ToString("x4")).Append(okLine);
        }

        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildSidebandDataPacket(byte[] inner)
    {
        byte[] payload = new byte[1 + inner.Length];
        payload[0] = GitPacketReader.SideBandData;
        inner.CopyTo(payload, 1);

        byte[] header = Encoding.ASCII.GetBytes((4 + payload.Length).ToString("x4"));
        byte[] result = new byte[header.Length + payload.Length];
        header.CopyTo(result, 0);
        payload.CopyTo(result, header.Length);
        return result;
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        int total = arrays.Sum(a => a.Length);
        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] a in arrays)
        {
            a.CopyTo(result, offset);
            offset += a.Length;
        }

        return result;
    }

    private sealed class PhasedMockSubtransport : IGitSubtransport
    {
        private readonly PhasedMockStream _stream;

        public PhasedMockSubtransport(IReadOnlyList<byte[]> chunks, bool isRpc)
        {
            _stream = new PhasedMockStream(chunks);
            IsRpc = isRpc;
        }

        public bool IsRpc { get; }

        public IReadOnlyList<byte> CapturedWrites => _stream.Writes;

        public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
            => Task.FromResult<IGitSubtransportStream>(_stream);

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PhasedMockStream : IGitSubtransportStream
    {
        private readonly byte[][] _chunks;
        private int _phase;
        private readonly List<byte> _writes = [];

        public PhasedMockStream(IReadOnlyList<byte[]> chunks)
        {
            _chunks = [.. chunks];
        }

        public IReadOnlyList<byte> Writes => _writes;

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_phase >= _chunks.Length)
            {
                return Task.FromResult<int>(0);
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
}
