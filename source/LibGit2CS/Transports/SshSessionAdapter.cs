// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibSsh2CS;
using LibSsh2CS.Agent;

namespace LibGit2CS.Transports;

/// <summary>
/// Production <see cref="ISshSession"/> over a real <see cref="SshSession"/>.
/// Delegates handshake/keepalive/disconnect directly to
/// <see cref="SshSession"/>; delegates auth to <see cref="SshUserAuth"/>
/// extensions; folds the agent connect-list-iterate-sign flow into a single
/// <see cref="AuthenticateWithAgentAsync"/> call.
/// </summary>
internal sealed class SshSessionAdapter : ISshSession
{
    private readonly SshSession _session;

    public SshSessionAdapter(SshSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>The underlying LibSsh2CS session (exposed for hostkey-hash access).</summary>
    public SshSession Session => _session;

    /// <inheritdoc/>
    public Task HandshakeAsync(
        Stream stream,
        Func<byte[], byte[], CancellationToken, Task<bool>> verifyHostKeyAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifyHostKeyAsync);
        return _session.HandshakeAsync(
            stream,
            (hostKey, signature, token) => verifyHostKeyAsync(hostKey.ToArray(), signature.ToArray(), token),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<string[]> GetAuthMethodsAsync(string username, CancellationToken cancellationToken)
        => _session.GetAuthMethodsAsync(username, cancellationToken);

    /// <inheritdoc/>
    public Task AuthenticateWithPasswordAsync(string username, string password, CancellationToken cancellationToken)
        => _session.AuthenticateWithPasswordAsync(username, password, cancellationToken: cancellationToken);

    /// <inheritdoc/>
    public Task AuthenticateWithPublicKeyAsync(
        string username,
        byte[]? publicKeyBlob,
        byte[] privateKeyData,
        string? passphrase,
        CancellationToken cancellationToken)
        => _session.AuthenticateWithPublicKeyAsync(
            username,
            publicKeyBlob!,
            privateKeyData,
            passphrase,
            cancellationToken);

    /// <inheritdoc/>
    public Task AuthenticateWithKeyboardInteractiveAsync(string username, SshKeyboardInteractiveCallback callback, CancellationToken cancellationToken)
        => _session.AuthenticateWithKeyboardInteractiveAsync(username, callback, cancellationToken);

    /// <inheritdoc/>
    public async Task AuthenticateWithAgentAsync(string username, string? agentSocketPath, CancellationToken cancellationToken)
    {
        // Honor a per-context agent-socket override (ctx.Settings.AgentSocketPathOverride)
        // when the caller resolved one; otherwise fall back to $SSH_AUTH_SOCK
        // auto-discovery. Mirrors GitSettings.KnownHostsPathOverride's resolution.
        SshAgent agent = agentSocketPath is null ? new SshAgent() : new SshAgent(agentSocketPath);
        try
        {
            try
            {
                await agent.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshException ex)
            {
                // Parity ssh_libssh2.c:246-251: an agent-connect failure is
                // converted to LIBSSH2_ERROR_AUTHENTICATION_FAILED so the
                // transport's EAUTH retry loop re-invokes the credentials
                // callback (the user can fall back to password/keys). The raw
                // AgentProtocol code is NOT retryable, so the conversion keeps
                // the retry loop alive when no ssh-agent is running.
                throw new SshException(SshErrorCode.AuthenticationFailed,
                    $"Failed to connect to ssh-agent: {ex.Message}", ex);
            }

            IReadOnlyList<SshAgentIdentity> identities = await agent.ListIdentitiesAsync(cancellationToken)
                .ConfigureAwait(false);

            if (identities.Count == 0)
            {
                throw new SshException(SshErrorCode.AuthenticationFailed, "No identities loaded in ssh-agent");
            }

            // Try each identity in turn; the first success short-circuits. Per
            // libssh2's agent flow, a key rejected by the server advances to the
            // next identity; only when ALL are exhausted do we surface the last
            // failure.
            SshException? lastError = null;
            foreach (SshAgentIdentity identity in identities)
            {
                try
                {
                    await agent.AuthenticateWithIdentityAsync(_session, username, identity, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (SshException ex)
                {
                    lastError = ex;
                }
            }

            throw lastError ?? new SshException(SshErrorCode.AuthenticationFailed, "SSH agent auth failed");
        }
        finally
        {
            await agent.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<ISshChannel> OpenSessionAsync(CancellationToken cancellationToken)
    {
        SshChannel channel = await _session.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        return new SshChannelAdapter(channel);
    }

    /// <inheritdoc/>
    public void SetHostKeyPreference(string preference)
    {
        _session[SshMethodType.HostKey] = preference ?? string.Empty;
    }

    /// <inheritdoc/>
    public ValueTask<int> SendKeepAliveAsync(CancellationToken cancellationToken)
        => _session.SendKeepAliveAsync(cancellationToken);

    /// <inheritdoc/>
    public void ConfigureKeepAlive(bool wantReply, int intervalSeconds)
        => _session.ConfigureKeepAlive(wantReply, intervalSeconds);

    /// <inheritdoc/>
    public Task DisconnectAsync(SshDisconnectReason reason, string description, string lang, CancellationToken cancellationToken)
        => _session.DisconnectAsync(reason, description, lang, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
        => _session.DisposeAsync();
}
