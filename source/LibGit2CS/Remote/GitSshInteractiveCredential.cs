// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibSsh2CS;

namespace LibGit2CS.Remote;

/// <summary>
/// SSH keyboard-interactive credential. Maps to
/// <c>git_credential_ssh_interactive</c> /
/// <c>git_credential_ssh_interactive_new</c>
/// (<c>transports/credential.c:268-294</c>).
/// </summary>
/// <remarks>
/// Carries the username and a caller-supplied callback that answers the
/// server's challenge prompts. The transport invokes LibSsh2CS's
/// <c>SshUserAuth.AuthenticateWithKeyboardInteractiveAsync</c>, passing
/// <see cref="Callback"/> through (parity
/// <c>ssh_libssh2.c:326-346</c>, where the C code stashes the callback in
/// the session's abstract pointer).
/// <para>
/// <see cref="Callback"/> uses LibSsh2CS's <see cref="SshKeyboardInteractiveCallback"/>
/// delegate type directly — intentional coupling, since the LibGit2CS SSH
/// transport is the LibSsh2CS consumer. The <see cref="SshKeyboardInteractivePrompt"/>
/// array carries the per-prompt <c>Echo</c> flag (password prompts have
/// <c>Echo=false</c>), which a bare <c>string[]</c> would lose.
/// </para>
/// </remarks>
public sealed class GitSshInteractiveCredential : GitCredential
{
    /// <summary>
    /// Creates a new keyboard-interactive SSH credential.
    /// </summary>
    /// <param name="username">The SSH username (required, non-empty).</param>
    /// <param name="callback">
    /// The callback invoked to answer the server's challenge prompts.
    /// Receives the server's challenge name, instruction, and prompts;
    /// returns one answer string per prompt. Required.
    /// </param>
    public GitSshInteractiveCredential(string username, SshKeyboardInteractiveCallback callback)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentNullException.ThrowIfNull(callback);

        Username = username;
        Callback = callback;
    }

    /// <inheritdoc/>
    public override string? Username { get; }

    /// <summary>
    /// The keyboard-interactive callback. Uses LibSsh2CS's
    /// <see cref="SshKeyboardInteractiveCallback"/> delegate type directly —
    /// the transport passes it through to
    /// <c>SshUserAuth.AuthenticateWithKeyboardInteractiveAsync</c> unchanged.
    /// </summary>
    public SshKeyboardInteractiveCallback Callback { get; }

    /// <inheritdoc/>
    public override GitCredentialType Type => GitCredentialType.SshInteractive;
}
