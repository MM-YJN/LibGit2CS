// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibSsh2CS;

namespace LibGit2CS.Transports;

/// <summary>
/// Internal seam over the subset of <see cref="SshSession"/> + <c>SshUserAuth.*</c>
/// extension methods that <see cref="SshTransport"/> consumes. The production
/// implementation <see cref="SshSessionAdapter"/> delegates to LibSsh2CS's
/// public API; tests inject fakes that script handshake/auth/channel outcomes
/// without driving a real SSH server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The test strategy uses unit tests with
/// scripted fakes plus Docker sshd tests. A mock SSH server that stops at
/// KEX + SERVICE_ACCEPT cannot exercise auth, channel, or exec — exactly what
/// the transport tests need to cover. Rather than grow such a mock into a full
/// server-side implementation (substantial protocol work) or hand-roll a TCP
/// SSH server, the transport depends on this narrow interface and tests inject
/// fakes. Real wire-level interop is verified by the Docker suite.
/// </para>
/// <para>
/// <b>Hostkey verification</b> is intentionally NOT on this interface: the
/// <see cref="SshSession.HandshakeAsync(Stream, LibSsh2CS.HostKeyVerificationCallback, CancellationToken)"/>
/// verify callback is a <see cref="Func{T1, T2, T3, TResult}"/> passed at call
/// time, so <see cref="SshTransport"/> builds it inline and binds
/// <c>KnownHosts</c> + the host name + the <c>CertificateCheck</c> callback
/// through a closure. This keeps the seam surface small.
/// </para>
/// </remarks>
internal interface ISshSession
{
    /// <summary>
    /// Runs the SSH-2 transport handshake (banner → KEX → NEWKEYS →
    /// SERVICE_REQUEST). The caller owns <paramref name="stream"/>; the
    /// session does not dispose it.
    /// </summary>
    /// <param name="stream">The encrypted transport stream.</param>
    /// <param name="verifyHostKeyAsync">Required hostkey verification callback
    /// (parity with <see cref="SshSession.HandshakeAsync(Stream, LibSsh2CS.HostKeyVerificationCallback, CancellationToken)"/>).</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    Task HandshakeAsync(
        Stream stream,
        Func<byte[], byte[], CancellationToken, Task<bool>> verifyHostKeyAsync,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends the <c>"none"</c> userauth probe and returns the methods the
    /// server advertises in the FAILURE response. Parity with
    /// <see cref="SshUserAuth.GetAuthMethodsAsync(SshSession, string, CancellationToken)"/>.
    /// </summary>
    Task<string[]> GetAuthMethodsAsync(string username, CancellationToken cancellationToken);

    /// <summary>
    /// Authenticates with a plaintext password. Parity with
    /// <see cref="SshUserAuth.AuthenticateWithPasswordAsync(SshSession, string, string, SshPasswordChangeCallback?, CancellationToken)"/>.
    /// </summary>
    Task AuthenticateWithPasswordAsync(string username, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Authenticates with a public key from in-memory key bytes. Parity with
    /// <see cref="SshUserAuth.AuthenticateWithPublicKeyAsync(SshSession, string, byte[], byte[], string?, CancellationToken)"/>
    /// — the caller reads the private key file; this method parses it via
    /// <see cref="SshPemParser.ParseOpenSshPrivateKey"/> and runs
    /// the two-step publickey flow.
    /// </summary>
    /// <param name="username">The SSH username.</param>
    /// <param name="publicKeyBlob">
    /// The raw public-key blob, or <c>null</c> to derive it from
    /// <paramref name="privateKeyData"/> (parity with the C API accepting a
    /// NULL publickey — see <c>credential.c:254-257</c>).
    /// </param>
    /// <param name="privateKeyData">The raw private-key file bytes (PEM armor included).</param>
    /// <param name="passphrase">The passphrase, or <c>null</c> for unencrypted keys.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    Task AuthenticateWithPublicKeyAsync(string username, byte[]? publicKeyBlob, byte[] privateKeyData, string? passphrase, CancellationToken cancellationToken);

    /// <summary>
    /// Authenticates via keyboard-interactive (RFC 4252 §5.4). Parity with
    /// <see cref="SshUserAuth.AuthenticateWithKeyboardInteractiveAsync(SshSession, string, SshKeyboardInteractiveCallback, CancellationToken)"/>.
    /// </summary>
    Task AuthenticateWithKeyboardInteractiveAsync(string username, SshKeyboardInteractiveCallback callback, CancellationToken cancellationToken);

    /// <summary>
    /// Authenticates via the SSH agent. Connects to
    /// <c>$SSH_AUTH_SOCK</c>, lists loaded identities, and tries each one
    /// in turn until one succeeds or all fail. Parity with
    /// <see cref="LibSsh2CS.Agent.SshAgentExtensions.AuthenticateWithIdentityAsync(LibSsh2CS.Agent.SshAgent, SshSession, string, LibSsh2CS.Agent.SshAgentIdentity, CancellationToken)"/>
    /// called per identity.
    /// </summary>
    /// <param name="username">The SSH username.</param>
    /// <param name="agentSocketPath">
    /// When non-null, connect to the SSH agent at this explicit Unix socket
    /// path instead of resolving <c>$SSH_AUTH_SOCK</c>. The caller resolves
    /// this from <see cref="LibGit2CS.Core.GitSettings.AgentSocketPathOverride"/> on the
    /// transport's context; <c>null</c> means use the env-var default.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <exception cref="SshException">
    /// Thrown with <see cref="SshErrorCode.AuthenticationFailed"/> if no
    /// loaded identity authenticates successfully.
    /// </exception>
    Task AuthenticateWithAgentAsync(string username, string? agentSocketPath, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a <c>"session"</c> channel. Parity with
    /// <see cref="SshSession.OpenSessionAsync(CancellationToken)"/>.
    /// </summary>
    Task<ISshChannel> OpenSessionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets the host-key preference string for the upcoming handshake (the
    /// <c>session[MethodType.HostKey]</c> indexer on
    /// <see cref="SshSession"/>). Must be called BEFORE
    /// <see cref="HandshakeAsync"/>. Parity with
    /// <c>find_hostkey_preference</c> (<c>ssh_libssh2.c:523-587</c>).
    /// </summary>
    /// <param name="preference">
    /// Comma-separated wire names in priority order (e.g.
    /// <c>"ssh-ed25519,rsa-sha2-512,rsa-sha2-256,ssh-rsa"</c>), or the empty
    /// string to clear.
    /// </param>
    void SetHostKeyPreference(string preference);

    /// <summary>
    /// Sends a keepalive request. Parity with
    /// <see cref="SshSession.SendKeepAliveAsync(CancellationToken)"/>.
    /// </summary>
    ValueTask<int> SendKeepAliveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Configures the keepalive interval. Parity with
    /// <see cref="SshSession.ConfigureKeepAlive(bool, int)"/>.
    /// </summary>
    /// <param name="wantReply">Whether to request a reply from the server.</param>
    /// <param name="intervalSeconds">Seconds between keepalive sends.</param>
    void ConfigureKeepAlive(bool wantReply, int intervalSeconds);

    /// <summary>
    /// Disconnects the session. Parity with
    /// <see cref="SshSession.DisconnectAsync(SshDisconnectReason, string, string, CancellationToken)"/>.
    /// </summary>
    Task DisconnectAsync(SshDisconnectReason reason, string description, string lang, CancellationToken cancellationToken);

    /// <summary>
    /// Disposes the session. Parity with <see cref="SshSession.DisposeAsync()"/>.
    /// Does NOT dispose the caller-owned transport pipe.
    /// </summary>
    ValueTask DisposeAsync();
}
