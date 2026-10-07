// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Username-only credential. Used for the first step of SSH authentication
/// (server prompts for username before key/password). Maps to
/// <c>git_credential_username</c>.
/// </summary>
public sealed class GitUsernameCredential : GitCredential
{
    /// <summary>Creates a username-only credential.</summary>
    /// <param name="username">The username.</param>
    public GitUsernameCredential(string username)
    {
        Username = username;
    }

    /// <inheritdoc/>
    public override string Username { get; }

    /// <inheritdoc/>
    public override GitCredentialType Type => GitCredentialType.Username;
}
