using System.Reflection;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;
using LibGit2CS.UnitTests.Transports.TestInfrastructure;

using LibSsh2CS;

using MockNegotiationTransport = LibGit2CS.UnitTests.Transports.MockNegotiationTransport;
using MockTransportBuilder = LibGit2CS.UnitTests.Transports.MockTransportBuilder;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Regression tests for the Transports parity behaviors:
/// (non-hex OID in ACK/REF/shallow packets surfaced as GitException),
/// (unsupported SSH credential type re-prompting the callback),
/// (remote object-format mismatch with the local repo rejected),
/// (hostkey-blob length decode not overflowing int).
/// </summary>
public sealed class TransportMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public TransportMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_TransportMedium_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── non-hex OID in ACK/REF/shallow packets ────────────────────

    [Fact]
    public void Parse_Ack_NonHexOid_ThrowsGitExceptionNotFormatException()
    {
        // C's ack_pkt fails with GIT_ERROR_NET "error parsing ACK
        // pkt-line" (smart_pkt.c:91) on a non-hex OID, so
        // GitOid.Parse's FormatException must not escape as anything but a
        // GitException (GitPacketReader.cs:214-225).
        byte[] buffer = Encoding.ASCII.GetBytes($"0031ACK {new string('Z', 40)}\n");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal("error parsing ACK pkt-line", ex.Message);
        Assert.Equal(GitErrorCategory.Net, ex.Category);
    }

    [Fact]
    public void TryParse_Ack_NonHexOid_ReportsInvalidPayload()
    {
        // The TryParse contract: payload corruption is surfaced as a value,
        // never as a FormatException.
        byte[] buffer = Encoding.ASCII.GetBytes($"0031ACK {new string('Z', 40)}\n");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        Assert.False(GitPacketReader.TryParse(buffer, out _, out _, out GitPacketParseError error, ref state));
        Assert.Equal(GitPacketParseError.InvalidPayload, error);
    }

    [Fact]
    public void Parse_Ref_NonHexOid_ThrowsGitExceptionNotFormatException()
    {
        // C's ref_pkt fails with "error parsing REF pkt-line"
        // (smart_pkt.c:335).
        string line = $"{new string('Z', 40)} refs/heads/main\n";
        byte[] buffer = Encoding.ASCII.GetBytes($"{line.Length + 4:x4}{line}");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal("error parsing REF pkt-line", ex.Message);
        Assert.Equal(GitErrorCategory.Net, ex.Category);
    }

    [Fact]
    public void Parse_Shallow_NonHexOid_AcceptsWithZeroedOid()
    {
        // (parity nuance): C's shallow_pkt IGNORES git_oid__fromstr's
        // failure and keeps the ZEROED oid (smart_pkt.c:478;
        // git_oid__fromstrn zeroes the oid before failing, oid.c:50) — a
        // non-hex shallow OID is accepted, not an error.
        string content = $"shallow {new string('Z', 40)}";
        byte[] buffer = Encoding.ASCII.GetBytes($"{content.Length + 4:x4}{content}");
        var state = new GitPacketParseState { OidType = GitHashAlgorithmKind.Sha1 };

        GitPacket pkt = GitPacketReader.Parse(buffer, out _, ref state);
        GitShallowPacket shallow = Assert.IsType<GitShallowPacket>(pkt);
        Assert.True(shallow.Oid.IsZero);
    }

    // ── unsupported SSH credential type must fail hard ───────────

    [Fact]
    public async Task Authenticate_UnsupportedCredentialType_ThrowsAuthOnce()
    {
        // a credential type the server did not advertise is FATAL —
        // C's request_creds returns GIT_EAUTH "authentication callback
        // returned unsupported credentials type" (ssh_libssh2.c:415-419) and
        // the caller treats every request_creds failure as fatal
        // (ssh_libssh2.c:873).
        // callback forever. The FakeSshSession advertises publickey +
        // password + keyboard-interactive; a GitDefaultCredential (Type =
        // Default) is in none of them.
        var session = new FakeSshSession();
        int callbackCalls = 0;
        GitRemoteConnectOptions options = new()
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) =>
                {
                    callbackCalls++;
                    return Task.FromResult<GitCredential?>(new GitDefaultCredential());
                },
            },
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SshTransportTestAccess.AuthenticateAsync(session, "user", options, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Auth, ex.Code);
        Assert.Equal(GitErrorCategory.Ssh, ex.Category);
        Assert.Contains("unsupported credentials type", ex.Message);
        Assert.Equal(1, callbackCalls); // C fails hard — no re-prompt
    }

    // ── remote object-format mismatch must be rejected ───────────

    [Fact]
    public async Task Fetch_RemoteAdvertisesSha256IntoSha1Repo_RejectsWithMismatch()
    {
        // C's recv_pkt seeds the packet-parse state with the LOCAL
        // repository's object format and set_data rejects a mismatch with
        // "the local object format '%s' does not match the remote object
        // format '%s'" (smart_pkt.c:261-269, smart_protocol.c:275), not
        // storing incompatible OIDs parsed from the 64-hex refs.
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitContext ctx = new();
        string clientPath = Path.Combine(_tempDir, "client");
        await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: false, ctx, cancellationToken: ct);

        await client.Config.SetStringAsync("remote.origin.url", "m05://host/repo", ct);
        await client.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/main:refs/remotes/origin/main", ct);

        string oid64 = new('a', 64);
        byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
            [(oid64, "HEAD"), (oid64, "refs/heads/main")],
            capabilities: "object-format=sha256 side-band-64k ofs-delta");
        byte[] downloadResponse = [.. "0008NAK\n"u8.ToArray(), .. MockTransportBuilder.BuildSidebandData([])];
        var mock = new MockNegotiationTransport(refAd, downloadResponse, packData: [], isRpc: false);
        ctx.Transports.Register("m05", _ => new GitSmartTransport(
            new SubtransportDefinition(_ => mock, IsRpc: false, null), ctx));

        GitRemote remote = await client.RemoteLookupAsync("origin", ct);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await remote.FetchAsync(
                refspecs: ["+refs/heads/main:refs/remotes/origin/main"],
                options: new GitFetchOptions(),
                cancellationToken: ct));

        Assert.Contains("the local object format 'sha1' does not match the remote object format 'sha256'", ex.Message);
    }

    // ── hostkey-blob length decode must not overflow int ─────────

    [Fact]
    public void DetectKnownHostKeyType_HugeLength_ReturnsUnknownInsteadOfOverflowing()
    {
        // for a length in [0x7FFFFFFD, int.MaxValue] "4 + len" must not
        // overflow to a negative int and pass the bounds test, letting
        // Encoding.UTF8.GetString throw instead of returning Unknown.
        byte[] blob = [0x7F, 0xFF, 0xFF, 0xFD, 0x00, 0x01, 0x02, 0x03];
        Assert.Equal(SshKnownHostKeyType.Unknown, InvokeDetectKnownHostKeyType(blob));
    }

    [Fact]
    public void DetectHostKeyTypeName_HugeLength_ReturnsNullInsteadOfOverflowing()
    {
        byte[] blob = [0x7F, 0xFF, 0xFF, 0xFF, 0x00, 0x01, 0x02, 0x03];
        Assert.Null(InvokeDetectHostKeyTypeName(blob));
    }

    [Fact]
    public void DetectKnownHostKeyType_ValidBlob_StillRecognizes()
    {
        // Sanity: the overflow-safe bounds test must not break the happy path.
        byte[] blob = [0x00, 0x00, 0x00, 0x0B, .. "ssh-ed25519"u8.ToArray()];
        Assert.Equal(SshKnownHostKeyType.Ed25519, InvokeDetectKnownHostKeyType(blob));
    }

    private static SshKnownHostKeyType InvokeDetectKnownHostKeyType(byte[] hostKey)
    {
        MethodInfo method = typeof(SshTransport).GetMethod(
            "DetectKnownHostKeyType",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DetectKnownHostKeyType not found");
        return (SshKnownHostKeyType)method.Invoke(null, [hostKey])!;
    }

    private static string? InvokeDetectHostKeyTypeName(byte[] hostKey)
    {
        MethodInfo method = typeof(SshTransport).GetMethod(
            "DetectHostKeyTypeName",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DetectHostKeyTypeName not found");
        return (string?)method.Invoke(null, [hostKey]);
    }
}
