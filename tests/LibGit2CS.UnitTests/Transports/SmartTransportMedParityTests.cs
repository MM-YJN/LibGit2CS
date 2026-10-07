using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

/// <summary> Regression tests for the transport parity behaviors (libgit2 1.9.4): (push status sorted by ref name), (ACK/ng/shallow space
/// checks), (post-push transport refs/heads cache update). </summary>
public sealed class SmartTransportMedParityTests : IDisposable
{
    private const string Sha1Main = "a5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string Sha1New = "c5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string Sha1Old = "d5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";

    public void Dispose()
    {
    }

    // ── push status iteration/callback order ──────────────────

    /// <summary>
    /// C (smart_protocol.c:1067-1088, update_refs_from_report) sorts
    /// <c>push-&gt;status</c> IN PLACE, so <c>git_push_status_foreach</c>
    /// (and thus <c>push_update_reference</c> and <c>git_remote_push</c>'s
    /// result) fire in ref-name order.
    /// </summary>
    [Fact]
    public async Task Push_Status_SortedByRefName()
    {
        byte[] refAd = BuildRefAdvertisement(isRpc: false, caps: "report-status side-band-64k");
        // The server reports zzz before aaa (not sorted).
        byte[] report = BuildPushReport(refs: ["refs/heads/zzz", "refs/heads/aaa"]);
        var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);
        Assert.True(transport.IsConnected);

        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = GitRefSpec.Parse(":refs/heads/aaa", isFetch: false),
                Loid = default, // delete — no pack
                Roid = GitOid.Parse(Sha1Main, GitHashAlgorithmKind.Sha1),
            },
            new()
            {
                RefSpec = GitRefSpec.Parse(":refs/heads/zzz", isFetch: false),
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
        Assert.Equal(["refs/heads/aaa", "refs/heads/zzz"], result.Status.Select(s => s.Ref));
    }

    // ── post-push transport refs/heads cache ──────────────────

    /// <summary>
    /// C (smart_protocol.c:1265-1271): after a successful push,
    /// <c>update_refs_from_report</c> merge-joins the push specs against
    /// <c>t-&gt;refs</c> (add/update/delete zero-OID) and rebuilds
    /// <c>t-&gt;heads</c>. A still-connected transport's <c>ls</c> must
    /// reflect the pushed state.
    /// </summary>
    [Fact]
    public async Task Push_RefsCache_UpdatedAfterPush()
    {
        // Advertisement: HEAD (real OID + caps), refs/heads/main, refs/heads/old.
        var sb = new StringBuilder();
        string headRef = $"{Sha1Main} HEAD\0report-status side-band-64k\n";
        sb.Append((4 + headRef.Length).ToString("x4")).Append(headRef);
        string mainRef = $"{Sha1Main} refs/heads/main\n";
        sb.Append((4 + mainRef.Length).ToString("x4")).Append(mainRef);
        string oldRef = $"{Sha1Old} refs/heads/old\n";
        sb.Append((4 + oldRef.Length).ToString("x4")).Append(oldRef);
        sb.Append("0000");
        byte[] refAd = Encoding.ASCII.GetBytes(sb.ToString());

        byte[] report = BuildPushReport(refs: ["refs/heads/main", "refs/heads/old"]);
        var mock = new PhasedMockSubtransport([refAd, report], isRpc: false);
        await using var transport = new GitSmartTransport(new SubtransportDefinition(
            _ => mock, IsRpc: false, null), new GitContext());

        await transport.ConnectAsync("git://host/repo", GitDirection.Push, null, TestContext.Current.CancellationToken);

        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = GitRefSpec.Parse("refs/heads/main:refs/heads/main", isFetch: false),
                Loid = GitOid.Parse(Sha1New, GitHashAlgorithmKind.Sha1), // update
                Roid = GitOid.Parse(Sha1Main, GitHashAlgorithmKind.Sha1),
            },
            new()
            {
                RefSpec = GitRefSpec.Parse(":refs/heads/old", isFetch: false),
                Loid = default, // delete
                Roid = GitOid.Parse(Sha1Old, GitHashAlgorithmKind.Sha1),
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

        // The cached advertisement must now show the pushed state.
        IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, heads.Count);
        Assert.Equal("HEAD", heads[0].Name);
        Assert.Equal("refs/heads/main", heads[1].Name);
        Assert.Equal(GitOid.Parse(Sha1New, GitHashAlgorithmKind.Sha1), heads[1].Oid);
        Assert.DoesNotContain(heads, h => h.Name == "refs/heads/old");
    }

    // ── ParseAck/Ng/Shallow space checks ──────────────────────

    [Theory]
    [InlineData("ACK")]
    [InlineData("ACKX")]
    [InlineData("ng")]
    [InlineData("ngX")]
    [InlineData("shallow")]
    [InlineData("shallowX")]
    [InlineData("unshallow")]
    [InlineData("unshallowX")]
    public void TryParse_MissingSpaceAfterPrefix_InvalidPayload(string prefix)
    {
        // C (smart_pkt.c:61-64, 389-391, 462-466): "ACK "/"ng "/"shallow "/
        // "unshallow " are REQUIRED before slicing — a truncated or
        // space-less prefix is "error parsing ACK pkt-line"/"invalid packet
        // line".
        int payloadLen = prefix.Length;
        int len = GitPacketReader.PktLenSize + payloadLen;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(prefix).CopyTo(buffer, GitPacketReader.PktLenSize);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1, SeenCapabilities = true };
        bool ok = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out _, out GitPacketParseError error, ref state);

        Assert.False(ok);
        Assert.Equal(GitPacketParseError.InvalidPayload, error);
        Assert.Null(pkt);
    }

    /// <summary>
    /// A space-less "ACKX&lt;oid&gt;" must NOT be accepted as an ACK — C's
    /// ack_pkt requires the trailing space (smart_pkt.c:61-64), so slicing
    /// payload[4..] must not accept it.
    /// </summary>
    [Fact]
    public void TryParse_AckWithoutSpace_InvalidPayload()
    {
        string payload = "ACKX" + Sha1Main;
        int len = GitPacketReader.PktLenSize + payload.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(payload).CopyTo(buffer, GitPacketReader.PktLenSize);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1, SeenCapabilities = true };
        bool ok = GitPacketReader.TryParse(buffer, out GitPacket? pkt, out _, out GitPacketParseError error, ref state);

        Assert.False(ok);
        Assert.Equal(GitPacketParseError.InvalidPayload, error);
        Assert.Null(pkt);
    }

    /// <summary>Well-formed ACK/ng/shallow/unshallow still parse.</summary>
    [Fact]
    public void TryParse_WithSpace_ValidPackets()
    {
        ParseOk("ACK " + Sha1Main, out GitPacket? ack);
        Assert.IsType<GitAckPacket>(ack);

        ParseOk("ng refs/heads/x oops\n", out GitPacket? ng);
        Assert.IsType<GitNgPacket>(ng);

        ParseOk("shallow " + Sha1Main, out GitPacket? shallow);
        Assert.IsType<GitShallowPacket>(shallow);

        ParseOk("unshallow " + Sha1Main, out GitPacket? unshallow);
        Assert.IsType<GitUnshallowPacket>(unshallow);
    }

    private static void ParseOk(string payload, out GitPacket? pkt)
    {
        int len = GitPacketReader.PktLenSize + payload.Length;
        byte[] buffer = new byte[len];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes(payload).CopyTo(buffer, GitPacketReader.PktLenSize);

        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1, SeenCapabilities = true };
        Assert.True(GitPacketReader.TryParse(buffer, out pkt, out _, out GitPacketParseError error, ref state), $"expected parse success, got {error}");
    }

    // ── Helpers (mirror SmartTransportTests) ─────────────────────────────

    private static byte[] BuildRefAdvertisement(bool isRpc, string? caps = null)
    {
        var sb = new StringBuilder();
        string oidZero = new('0', 40);

        if (isRpc)
        {
            string comment = "# service=git-upload-pack\n";
            int cmtLen = 4 + comment.Length;
            sb.Append(cmtLen.ToString("x4"));
            sb.Append(comment);
            sb.Append("0000");
        }

        string firstRef = caps is null
            ? $"{oidZero} HEAD\n"
            : $"{oidZero} HEAD\0{caps}\n";
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
        {
            return Task.FromResult<IGitSubtransportStream>(_stream);
        }

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
