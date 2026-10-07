// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// SSH agent-backed credential. Maps to
/// <c>git_credential_ssh_key_from_agent</c>
/// (<c>transports/credential.c:296-315</c>).
/// </summary>
/// <remarks>
/// Carries the username only — the actual key material is held by the
/// running <c>ssh-agent</c> (located via the <c>$SSH_AUTH_SOCK</c>
/// environment variable). The transport instantiates a LibSsh2CS
/// <c>SshAgent</c>, connects to the agent, enumerates identities, and
/// attempts <c>AuthenticateWithIdentityAsync</c> for each one until one
/// succeeds (parity <c>ssh_agent_auth</c> at <c>ssh_libssh2.c:236-290</c>).
/// <para>
/// <see cref="Type"/> returns <see cref="GitCredentialType.SshKey"/>
/// (NOT a separate agent bit) — the C credential struct reuses
/// <c>git_credential_ssh_key</c> with <c>privatekey = NULL</c>, and the
/// dispatch in <c>_git_ssh_authenticate_session</c>
/// (<c>ssh_libssh2.c:306-317</c>) branches on <c>c->privatekey == NULL</c>
/// to decide agent-vs-file. The C# transport does the equivalent by
/// checking <c>is GitSshAgentCredential</c> after matching the
/// <see cref="GitCredentialType.SshKey"/> bit.
/// </para>
/// </remarks>
public sealed class GitSshAgentCredential : GitCredential
{
    /// <summary>
    /// Creates a new SSH agent-backed credential.
    /// </summary>
    /// <param name="username">The SSH username (required, non-empty).</param>
    public GitSshAgentCredential(string username)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        Username = username;
    }

    /// <inheritdoc/>
    public override string? Username { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns <see cref="GitCredentialType.SshKey"/> (the agent-backed
    /// variant reuses the SshKey bit; the transport distinguishes agent
    /// from file-based keys via <c>is GitSshAgentCredential</c>).
    /// </remarks>
    public override GitCredentialType Type => GitCredentialType.SshKey;
}
