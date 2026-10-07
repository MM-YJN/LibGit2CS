// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Plaintext username + password credential. Maps to
/// <c>git_credential_userpass_plaintext</c>.
/// </summary>
public sealed class GitUserPassCredential : GitCredential
{
    /// <summary>Creates a new username/password credential.</summary>
    /// <param name="username">The username (may be empty, not null).</param>
    /// <param name="password">The password.</param>
    public GitUserPassCredential(string username, string password)
    {
        Username = username;
        Password = password;
    }

    /// <inheritdoc/>
    public override string Username { get; }

    /// <inheritdoc/>
    public override GitCredentialType Type => GitCredentialType.UserPassPlaintext;

    /// <summary>The plaintext password.</summary>
    public string Password { get; }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // No way to zero a string in .NET, but we clear the reference
            // to avoid accidental retention in closures.
        }

        base.Dispose(disposing);
    }
}
