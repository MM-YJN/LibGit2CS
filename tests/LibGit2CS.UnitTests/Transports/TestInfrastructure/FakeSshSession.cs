using LibGit2CS.Transports;

using LibSsh2CS;

namespace LibGit2CS.UnitTests.Transports.TestInfrastructure;

/// <summary>
/// Scripts <see cref="ISshSession"/> behavior for unit tests. Queues
/// per-method results that tests dequeue in call order; records all calls.
/// </summary>
/// <remarks>
/// Used by <see cref="SshTransportAuthTests"/> and
/// <see cref="SshTransportKeepaliveTests"/> to drive the transport's auth
/// retry loop and keepalive pump without a real SSH server.
/// </remarks>
internal sealed class FakeSshSession : ISshSession
{
    // ── Result queues (each call dequeues one) ──────────────────────────

    public Queue<SshException?> PasswordAuthResults { get; } = new();
    public Queue<SshException?> PublicKeyAuthResults { get; } = new();
    public Queue<SshException?> KeyboardInteractiveAuthResults { get; } = new();
    public Queue<SshException?> AgentAuthResults { get; } = new();

    // ── Call counts / last-arg capture ──────────────────────────────────

    public int HandshakeCalls { get; private set; }
    public int GetAuthMethodsCalls { get; private set; }
    public int PasswordAuthCalls { get; private set; }
    public int PublicKeyAuthCalls { get; private set; }
    public int KeyboardInteractiveCalls { get; private set; }
    public int AgentAuthCalls { get; private set; }
    public int OpenSessionCalls { get; private set; }
    public int SendKeepAliveCalls { get; private set; }
    public int DisconnectCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    public string? LastPasswordUser { get; private set; }
    public string? LastPassword { get; private set; }
    public (string User, string Password) LastPasswordAuth => (LastPasswordUser!, LastPassword!);

    public byte[]? LastPublicKeyBlob { get; private set; }
    public byte[]? LastPublicKeyPrivateKey { get; private set; }

    public string? LastAgentUsername { get; private set; }

    /// <summary>The last agent-socket path passed to AuthenticateWithAgentAsync (null = $SSH_AUTH_SOCK default).</summary>
    public string? LastAgentSocketPath { get; private set; }

    public List<string> HostKeyPreferences { get; } = [];
    public (bool WantReply, int Interval)? KeepAliveConfig { get; private set; }

    /// <summary>When true, the next SendKeepAliveAsync call throws an OCE.</summary>
    public bool SendKeepAliveThrowsOnce { get; set; }

    /// <summary>When set, HandshakeAsync throws this error (e.g. to simulate a
    /// hostkey-rejection KeyExchangeFailure).</summary>
    public SshException? HandshakeError { get; set; }

    public Func<Stream, Task<bool>?>? HandshakeVerifier { get; private set; }

    // ── ISshSession ────────────────────────────────────────────────────

    public Task HandshakeAsync(Stream stream, Func<byte[], byte[], CancellationToken, Task<bool>> verifyHostKeyAsync, CancellationToken cancellationToken)
    {
        HandshakeCalls++;
        HandshakeVerifier = _ => Task.FromResult(true);
        if (HandshakeError is not null)
        {
            return Task.FromException(HandshakeError);
        }

        return Task.CompletedTask;
    }

    public Task<string[]> GetAuthMethodsAsync(string username, CancellationToken cancellationToken)
    {
        GetAuthMethodsCalls++;
        return Task.FromResult<string[]>(["publickey", "password", "keyboard-interactive"]);
    }

    public async Task AuthenticateWithPasswordAsync(string username, string password, CancellationToken cancellationToken)
    {
        PasswordAuthCalls++;
        LastPasswordUser = username;
        LastPassword = password;
        await DequeueAndThrowIfNeeded(PasswordAuthResults).ConfigureAwait(false);
    }

    public async Task AuthenticateWithPublicKeyAsync(string username, byte[]? publicKeyBlob, byte[] privateKeyData, string? passphrase, CancellationToken cancellationToken)
    {
        PublicKeyAuthCalls++;
        LastPublicKeyBlob = publicKeyBlob;
        LastPublicKeyPrivateKey = privateKeyData;
        await DequeueAndThrowIfNeeded(PublicKeyAuthResults).ConfigureAwait(false);
    }

    public async Task AuthenticateWithKeyboardInteractiveAsync(string username, SshKeyboardInteractiveCallback callback, CancellationToken cancellationToken)
    {
        KeyboardInteractiveCalls++;
        await DequeueAndThrowIfNeeded(KeyboardInteractiveAuthResults).ConfigureAwait(false);
    }

    public async Task AuthenticateWithAgentAsync(string username, string? agentSocketPath, CancellationToken cancellationToken)
    {
        AgentAuthCalls++;
        LastAgentUsername = username;
        LastAgentSocketPath = agentSocketPath;
        await DequeueAndThrowIfNeeded(AgentAuthResults).ConfigureAwait(false);
    }

    /// <summary>
    /// When non-null, <see cref="OpenSessionAsync"/> invokes this supplier to
    /// produce the next <see cref="ISshChannel"/> instead of constructing a
    /// plain <see cref="FakeSshChannel"/>. Tests that need to script the
    /// channel's read sequence (e.g. preload <see cref="FakeSshChannel.ReadResults"/>)
    /// set this to a function that returns their pre-configured channel.
    /// Defaults to <c>null</c> — a fresh empty <see cref="FakeSshChannel"/>
    /// is returned per call, matching the original behavior used by auth/
    /// keepalive tests.
    /// </summary>
    public Func<FakeSshChannel>? ChannelFactory { get; set; }

    public Task<ISshChannel> OpenSessionAsync(CancellationToken cancellationToken)
    {
        OpenSessionCalls++;
        FakeSshChannel channel = ChannelFactory is { } factory ? factory() : new FakeSshChannel();
        return Task.FromResult<ISshChannel>(channel);
    }

    public void SetHostKeyPreference(string preference)
    {
        HostKeyPreferences.Add(preference);
    }

    public ValueTask<int> SendKeepAliveAsync(CancellationToken cancellationToken)
    {
        SendKeepAliveCalls++;
        if (SendKeepAliveThrowsOnce)
        {
            SendKeepAliveThrowsOnce = false;
            return ValueTask.FromException<int>(new SshException(LibSsh2CS.SshErrorCode.SocketDisconnect, "fake keepalive failure"));
        }

        return ValueTask.FromResult(0);
    }

    public void ConfigureKeepAlive(bool wantReply, int intervalSeconds)
    {
        KeepAliveConfig = (wantReply, intervalSeconds);
    }

    public Task DisconnectAsync(SshDisconnectReason reason, string description, string lang, CancellationToken cancellationToken)
    {
        DisconnectCalls++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }

    private static async Task DequeueAndThrowIfNeeded(Queue<SshException?> queue)
    {
        if (queue.Count > 0)
        {
            SshException? ex = queue.Dequeue();
            if (ex is not null)
            {
                throw ex;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// Minimal fake channel for tests that need to complete
/// <see cref="ISshSession.OpenSessionAsync(CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// By default <see cref="ReadAsync"/> returns 0 (EOF) immediately, matching
/// the original behavior used by auth/keepalive tests that only need the
/// channel to exist. Tests that need to script the read sequence (e.g. the
/// push-then-reconnect lifecycle test in <c>SshTransportReconnectTests</c>)
/// pre-populate <see cref="ReadResults"/> with byte chunks; each
/// <c>ReadAsync</c> call dequeues one chunk and copies it into the caller's
/// buffer. An empty queue falls back to the EOF behavior.
/// </para>
/// <para>
/// <see cref="Writes"/> captures every <see cref="WriteAsync"/> payload in
/// order, for assertions on the bytes sent to the "server".
/// </para>
/// </remarks>
internal sealed class FakeSshChannel : ISshChannel
{
    /// <summary>Pre-built byte chunks returned by <see cref="ReadAsync"/>, in order.</summary>
    public Queue<byte[]> ReadResults { get; } = new();

    /// <summary>Every write payload captured in call order.</summary>
    public List<byte[]> Writes { get; } = [];

    /// <summary>Number of times <see cref="ExecAsync"/> was called.</summary>
    public int ExecCalls { get; private set; }

    /// <summary>The last command passed to <see cref="ExecAsync"/>.</summary>
    public string? LastExecCommand { get; private set; }

    /// <summary>Number of times <see cref="SendEofAsync"/> was called.</summary>
    public int SendEofCalls { get; private set; }

    /// <summary>Number of times <see cref="DisposeAsync"/> was called.</summary>
    public int DisposeCalls { get; private set; }

    public Task ExecAsync(string command, CancellationToken cancellationToken)
    {
        ExecCalls++;
        LastExecCommand = command;
        return Task.CompletedTask;
    }

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (ReadResults.Count == 0)
        {
            return Task.FromResult(0);
        }

        byte[] chunk = ReadResults.Dequeue();
        int toCopy = Math.Min(buffer.Length, chunk.Length);
        chunk.AsSpan(0, toCopy).CopyTo(buffer.Span);
        return Task.FromResult(toCopy);
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        Writes.Add(data.ToArray());
        return Task.CompletedTask;
    }

    public Task SendEofAsync(CancellationToken cancellationToken)
    {
        SendEofCalls++;
        return Task.CompletedTask;
    }

    public Task<int> ReadStderrAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => Task.FromResult(0);

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }
}
