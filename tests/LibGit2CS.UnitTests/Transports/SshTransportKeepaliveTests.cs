using System.Net;
using System.Net.Sockets;

using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Transports;
using LibGit2CS.UnitTests.Transports.TestInfrastructure;

using Microsoft.Extensions.Time.Testing;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Unit tests for <see cref="SshTransport"/>'s keepalive pump. Uses
/// <see cref="FakeSshSession"/> + <see cref="FakeTimeProvider"/> — no real
/// SSH server, no real wall-clock waits.
/// </summary>
/// <remarks>
/// The transport owns a <see cref="PeriodicTimer"/> that drives
/// <see cref="ISshSession.SendKeepAliveAsync(CancellationToken)"/> for
/// long-lived fetch/push over idle links (parity with
/// libgit2 1.9.4; LibSsh2CS is passive; the
/// caller owns the pump). The pump is started by
/// <see cref="SshTransport.StartKeepalivePump"/> (production calls it from
/// <c>OpenServiceStreamAsync</c>; tests call it directly to avoid driving a
/// full TCP+handshake+auth flow).
/// </remarks>
public sealed class SshTransportKeepaliveTests
{
    [Fact]
    public async Task Keepalive_PumpsOnInterval()
    {
        (SshTransport? transport, FakeSshSession? session, FakeTimeProvider? timeProvider) = CreateTransport();
        transport.StartKeepalivePump(session, intervalSeconds: 5, TestContext.Current.CancellationToken);

        // No keepalive at t=0 — the timer waits for the first tick.
        Assert.Equal(0, session.SendKeepAliveCalls);

        // Advance past one interval — pump fires one keepalive.
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken); // let pump observe the tick

        Assert.Equal(1, session.SendKeepAliveCalls);

        // Advance past another interval — second keepalive.
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.Equal(2, session.SendKeepAliveCalls);

        Assert.True(session.KeepAliveConfig.HasValue);
        Assert.Equal((true, 5), session.KeepAliveConfig!.Value);

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Keepalive_DisposedInClose()
    {
        (SshTransport? transport, FakeSshSession? session, FakeTimeProvider? timeProvider) = CreateTransport();
        transport.StartKeepalivePump(session, intervalSeconds: 1, TestContext.Current.CancellationToken);

        // CloseAsync stops the pump + disposes the timer.
        await transport.CloseAsync(TestContext.Current.CancellationToken);

        // Advance the clock — pump must not fire after close.
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.Equal(0, session.SendKeepAliveCalls);
    }

    [Fact]
    public async Task Keepalive_DisabledByZeroInterval_DoesNotPump()
    {
        (SshTransport? transport, FakeSshSession? session, FakeTimeProvider _) = CreateTransport();
        transport.StartKeepalivePump(session, intervalSeconds: 0, TestContext.Current.CancellationToken);

        // Zero interval = disabled — no timer, no pump, no Config call.
        Assert.Null(session.KeepAliveConfig);
        Assert.Equal(0, session.SendKeepAliveCalls);

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Keepalive_ObservesCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        (SshTransport? transport, FakeSshSession? session, FakeTimeProvider? timeProvider) = CreateTransport();
        transport.StartKeepalivePump(session, intervalSeconds: 1, cts.Token);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.Equal(1, session.SendKeepAliveCalls);

        // Cancel — pump must exit. Subsequent time advances must not fire more
        // keepalives.
        await cts.CancelAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.Equal(1, session.SendKeepAliveCalls);

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Keepalive_SendFailure_DoesNotCrashPump()
    {
        (SshTransport? transport, FakeSshSession? session, FakeTimeProvider? timeProvider) = CreateTransport();
        // Make SendKeepAliveAsync throw on first call.
        session.SendKeepAliveThrowsOnce = true;
        transport.StartKeepalivePump(session, intervalSeconds: 1, TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        // First call threw but the pump exited cleanly (parity: a failed
        // keepalive shouldn't crash the pump; next regular I/O surfaces the
        // real error).
        Assert.Equal(1, session.SendKeepAliveCalls);

        await transport.DisposeAsync();
    }

    // ── Full OpenServiceStreamAsync wiring ──────────────────────────────

    [Fact]
    public async Task OpenServiceStream_DefaultOptions_DoesNotStartKeepalivePump()
    {
        // Wire parity: the reference libgit2 + libssh2 never send SSH
        // keepalive packets, so the default
        // (KeepaliveIntervalSeconds == 0) must not configure or pump.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var listener = new LoopbackSshListener();
        int port = listener.Start();

        var session = new FakeSshSession();
        var transport = new SshTransport(new GitContext(), new SingleSessionFactory(session), timeProvider: null);
        await using (transport)
        {
            IGitSubtransportStream stream = await transport.ActionAsync(
                $"ssh://user@127.0.0.1:{port}/home/user/repo.git",
                GitSmartService.UploadPackLs,
                BuildConnectOptions(),
                cancellationToken: ct);

            Assert.Null(session.KeepAliveConfig);
            Assert.Equal(0, session.SendKeepAliveCalls);

            await stream.DisposeAsync();
            await transport.CloseAsync(ct);
        }
    }

    [Fact]
    public async Task OpenServiceStream_OptInInterval_StartsKeepalivePump()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var listener = new LoopbackSshListener();
        int port = listener.Start();

        var session = new FakeSshSession();
        var transport = new SshTransport(new GitContext(), new SingleSessionFactory(session), timeProvider: null);
        await using (transport)
        {
            GitRemoteConnectOptions options = BuildConnectOptions(keepaliveIntervalSeconds: 7);

            IGitSubtransportStream stream = await transport.ActionAsync(
                $"ssh://user@127.0.0.1:{port}/home/user/repo.git",
                GitSmartService.UploadPackLs,
                options,
                cancellationToken: ct);

            Assert.True(session.KeepAliveConfig.HasValue);
            Assert.Equal((true, 7), session.KeepAliveConfig!.Value);

            await stream.DisposeAsync();
            await transport.CloseAsync(ct);
        }
    }

    // ── Factory ────────────────────────────────────────────────────────

    private static (SshTransport Transport, FakeSshSession Session, FakeTimeProvider TimeProvider) CreateTransport()
    {
        var session = new FakeSshSession();
        var factory = new FakeSessionFactory(session);
        var timeProvider = new FakeTimeProvider();
        var transport = new SshTransport(new GitContext(), factory, timeProvider);
        return (transport, session, timeProvider);
    }

    private sealed class FakeSessionFactory : ISshSessionFactory
    {
        private readonly FakeSshSession _session;

        public FakeSessionFactory(FakeSshSession session) => _session = session;

        public ISshSession Create() => _session;
    }

    private static GitRemoteConnectOptions BuildConnectOptions(int keepaliveIntervalSeconds = 0) => new()
    {
        Callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential("user", "fakepassword")),
            CertificateCheck = _ => true,
            KeepaliveIntervalSeconds = keepaliveIntervalSeconds,
        },
    };

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
                    break;   // listener stopped
                }

                _accepted.Add(client);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
                _cts.Dispose();
            }

            _listener?.Dispose();
            try
            {
                if (_acceptLoop is not null)
                {
                    await _acceptLoop.ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Accept loop teardown noise is not test-relevant.
            }

            foreach (Socket socket in _accepted)
            {
                socket.Dispose();
            }
        }
    }
}
