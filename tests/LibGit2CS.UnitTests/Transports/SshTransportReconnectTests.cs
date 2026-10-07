using System.Net;
using System.Net.Sockets;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Transports;
using LibGit2CS.UnitTests.Transports.TestInfrastructure;

using LibSsh2CS;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Regression tests for the SSH re-connect lifecycle: a fresh
/// <see cref="SshTransport"/> must open, read a ref advertisement, and close
/// cleanly after a PREVIOUS <see cref="SshTransport"/> on the same process has
/// gone through its full <c>OpenServiceStreamAsync</c> + <c>CloseAsync</c>
/// cycle. Pins the <see cref="SshTransport.CloseAsync"/> teardown (await
/// the keepalive pump + <see cref="Socket.Shutdown(SocketShutdown.Both)"/>
/// before <see cref="Socket.Dispose"/>) that prevents the first transport's
/// TCP teardown from racing the second's connect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Test level.</b> These tests drive the real <see cref="SshTransport"/>
/// TCP + session lifecycle (real <c>Dns.GetHostAddressesAsync</c>, real
/// <see cref="Socket.ConnectAsync"/>, real <see cref="SshTransport.CloseAsync"/>
/// teardown) with the SSH layer faked via <see cref="FakeSshSession"/> +
/// <see cref="FakeSshChannel"/>. A loopback <see cref="TcpListener"/> accepts
/// the raw TCP connections so <c>ConnectAsync</c> succeeds; no SSH bytes
/// actually flow — the fake session's handshake/auth/channel are no-ops.
/// </para>
/// <para>
/// <b>What this catches.</b> Regressions where <see cref="SshTransport.CloseAsync"/>
/// throws, hangs, or leaves the process in a state where a second transport
/// cannot connect (e.g. port exhaustion, a stuck keepalive pump, an unhandled
/// <see cref="Socket.Shutdown(SocketShutdown.Both)"/> exception). The
/// real-wire TCP-vs-sshd-child-teardown race is
/// verified by the Docker integration test
/// <c>SshTransportDockerTests.Push_OverSsh_UpdatesRemoteRefs</c>; this
/// fake-backed test provides a Docker-free regression baseline.
/// </para>
/// </remarks>
public sealed class SshTransportReconnectTests
{
    /// <summary>
    /// Two sequential <see cref="SshTransport"/> instances against the same
    /// loopback listener: the first opens a ReceivePackLs stream, reads a
    /// scripted ref advertisement, and closes; the second opens an
    /// UploadPackLs stream, reads a scripted ref advertisement, and closes.
    /// Both must complete without error, and the second must return the
    /// scripted ref bytes — confirming a fresh transport works after a
    /// previous one's full lifecycle.
    /// </summary>
    [Fact]
    public async Task SecondTransport_WorksAfter_FirstTransport_CloseAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var listener = new LoopbackSshListener();
        int port = listener.Start();

        string url = $"ssh://user@127.0.0.1:{port}/home/user/repo.git";

        // ── Transport #1 (push side) ──────────────────────────────────────
        byte[] pushRefAd = BuildRefAdvertisement("refs/heads/main", new('1', 40));
        var session1 = new FakeSshSession
        {
            ChannelFactory = () =>
            {
                var ch = new FakeSshChannel();
                ch.ReadResults.Enqueue(pushRefAd);
                return ch;
            }
        };
        var factory1 = new SingleSessionFactory(session1);

        GitRemoteConnectOptions opts = BuildConnectOptions();
        await using var transport1 = new SshTransport(new GitContext(), factory1, timeProvider: null);
        IGitSubtransportStream stream1 = await transport1.ActionAsync(
            url, GitSmartService.ReceivePackLs, opts, cancellationToken: ct);
        byte[] readBuf1 = new byte[4096];
        int n1 = await stream1.ReadAsync(readBuf1.AsMemory(), ct);
        Assert.True(n1 > 0, "transport #1 read returned no bytes");
        await stream1.DisposeAsync();
        await transport1.CloseAsync(ct);

        // ── Transport #2 (fetch / re-connect side) ────────────────────────
        byte[] fetchRefAd = BuildRefAdvertisement("refs/heads/main", new('2', 40));
        var session2 = new FakeSshSession
        {
            ChannelFactory = () =>
            {
                var ch = new FakeSshChannel();
                ch.ReadResults.Enqueue(fetchRefAd);
                return ch;
            }
        };
        var factory2 = new SingleSessionFactory(session2);

        await using var transport2 = new SshTransport(new GitContext(), factory2, timeProvider: null);
        IGitSubtransportStream stream2 = await transport2.ActionAsync(
            url, GitSmartService.UploadPackLs, opts, cancellationToken: ct);
        byte[] readBuf2 = new byte[4096];
        int n2 = await stream2.ReadAsync(readBuf2.AsMemory(), ct);
        Assert.True(n2 > 0, "transport #2 read returned no bytes — re-connect failed after transport #1 CloseAsync");

        // The bytes read must be the scripted ref-ad, not stale data from
        // transport #1.
        byte[] received = readBuf2.AsSpan(0, n2).ToArray();
        Assert.Equal(fetchRefAd, received);

        await stream2.DisposeAsync();
        await transport2.CloseAsync(ct);
    }

    /// <summary>
    /// Three sequential transports to exercise repeated connect/close cycles
    /// (the pattern a long-lived remote uses for fetch-after-push-after-fetch).
    /// Each must open, read its scripted ref-ad, and close without error.
    /// </summary>
    [Fact]
    public async Task ThreeSequentialTransports_EachConnectAndClose()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var listener = new LoopbackSshListener();
        int port = listener.Start();

        string url = $"ssh://user@127.0.0.1:{port}/home/user/repo.git";
        GitRemoteConnectOptions opts = BuildConnectOptions();

        for (int i = 0; i < 3; i++)
        {
            byte[] refAd = BuildRefAdvertisement("refs/heads/main", new((char)('a' + i), 40));
            var session = new FakeSshSession
            {
                ChannelFactory = () =>
                {
                    var ch = new FakeSshChannel();
                    ch.ReadResults.Enqueue(refAd);
                    return ch;
                }
            };
            var factory = new SingleSessionFactory(session);

            await using var transport = new SshTransport(new GitContext(), factory, timeProvider: null);
            IGitSubtransportStream stream = await transport.ActionAsync(
                url, GitSmartService.UploadPackLs, opts, cancellationToken: ct);
            byte[] buf = new byte[4096];
            int n = await stream.ReadAsync(buf.AsMemory(), ct);
            Assert.True(n > 0, $"transport #{i + 1} read returned no bytes");

            byte[] received = buf.AsSpan(0, n).ToArray();
            Assert.Equal(refAd, received);

            await stream.DisposeAsync();
            await transport.CloseAsync(ct);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Build <see cref="GitRemoteConnectOptions"/> with a credentials
    /// callback that returns a placeholder password. The SSH layer is faked
    /// so the password is never actually used; the callback just has to be
    /// non-null so <see cref="SshTransport.AuthenticateAsync"/> doesn't throw
    /// <c>"no credentials callback provided"</c> before the fake session's
    /// no-op auth runs.
    /// </summary>
    private static GitRemoteConnectOptions BuildConnectOptions() => new()
    {
        Callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential("user", "fakepassword")),
            CertificateCheck = _ => true,
        },
    };

    /// <summary>
    /// A hostkey rejection during the handshake (the verify callback returning
    /// false surfaces as <see cref="SshErrorCode.KeyExchangeFailure"/>) must be
    /// mapped to <see cref="GitErrorCode.Certificate"/> with libgit2's exact
    /// message — parity ssh_libssh2.c:758-761.
    /// </summary>
    [Fact]
    public async Task HandshakeKeyExchangeFailure_MapsToCertificateError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var listener = new LoopbackSshListener();
        int port = listener.Start();

        string url = $"ssh://user@127.0.0.1:{port}/home/user/repo.git";

        var session = new FakeSshSession
        {
            HandshakeError = new SshException(SshErrorCode.KeyExchangeFailure, "hostkey signature verification failed."),
        };
        var factory = new SingleSessionFactory(session);

        GitRemoteConnectOptions opts = BuildConnectOptions();
        await using var transport = new SshTransport(new GitContext(), factory, timeProvider: null);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ActionAsync(url, GitSmartService.UploadPackLs, opts, cancellationToken: ct));

        Assert.Equal(GitErrorCode.Certificate, ex.Code);
        Assert.Equal(GitErrorCategory.Ssh, ex.Category);
        Assert.Equal("invalid or unknown remote ssh hostkey", ex.Message);
    }

    /// <summary>
    /// A non-hostkey handshake failure (e.g. a banner error) must NOT be
    /// mislabeled as a certificate error.
    /// </summary>
    [Fact]
    public async Task HandshakeOtherFailure_NotMappedToCertificate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var listener = new LoopbackSshListener();
        int port = listener.Start();

        string url = $"ssh://user@127.0.0.1:{port}/home/user/repo.git";

        var session = new FakeSshSession
        {
            HandshakeError = new SshException(SshErrorCode.BannerRecv, "EOF reading server banner"),
        };
        var factory = new SingleSessionFactory(session);

        GitRemoteConnectOptions opts = BuildConnectOptions();
        await using var transport = new SshTransport(new GitContext(), factory, timeProvider: null);

        SshException ex = await Assert.ThrowsAsync<SshException>(async () =>
            await transport.ActionAsync(url, GitSmartService.UploadPackLs, opts, cancellationToken: ct));

        Assert.Equal(SshErrorCode.BannerRecv, ex.ErrorCode);
    }

    /// <summary>
    /// Build a minimal v0 ref advertisement with one ref + a flush, the
    /// format <c>git-upload-pack</c> emits and <see cref="GitSmartProtocol.StoreRefsAsync"/>
    /// parses. The OID is 40 ASCII chars (SHA-1). No capabilities are
    /// advertised — the smart protocol tolerates their absence.
    /// </summary>
    private static byte[] BuildRefAdvertisement(string refName, string oid40)
    {
        var sb = new StringBuilder();
        string refLine = $"{oid40} {refName}\n";
        int refLen = 4 + refLine.Length;
        sb.Append(refLen.ToString("x4"));
        sb.Append(refLine);
        sb.Append("0000"); // flush
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// <see cref="ISshSessionFactory"/> that returns a single pre-built
    /// <see cref="ISshSession"/> for every <c>Create</c> call. Each
    /// <see cref="SshTransport"/> gets its own factory wrapping its own
    /// session — mirroring production where <see cref="SshSessionFactory"/>
    /// builds a fresh <see cref="SshSessionAdapter"/> per transport.
    /// </summary>
    private sealed class SingleSessionFactory : ISshSessionFactory
    {
        private readonly ISshSession _session;
        public SingleSessionFactory(ISshSession session) => _session = session;
        public ISshSession Create() => _session;
    }

    /// <summary>
    /// Loopback TCP listener that accepts and holds connections so
    /// <see cref="Socket.ConnectAsync"/> succeeds. No SSH bytes are exchanged;
    /// the SSH layer is faked by <see cref="FakeSshSession"/>. Held sockets
    /// are disposed on <see cref="DisposeAsync"/>.
    /// </summary>
    private sealed class LoopbackSshListener : IAsyncDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private readonly List<Socket> _accepted = [];

        public int Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _acceptLoop = AcceptLoopAsync(_cts.Token);
            return ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Socket? client;
                try
                {
                    client = await _listener!.AcceptSocketAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // Listener stopped — exit.
                    break;
                }

                _accepted.Add(client);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }

            _listener?.Dispose();
            try
            {
                if (_acceptLoop is not null)
                {
                    await _acceptLoop.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            _cts?.Dispose();
            foreach (Socket s in _accepted)
            {
                try
                {
                    s.Shutdown(SocketShutdown.Both);
                }
                catch { /* best-effort */ }
                s.Dispose();
            }

            _accepted.Clear();
        }
    }
}
