// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Credential helper functions. Managed port of
/// <c>src/libgit2/transports/credential_helpers.c</c> (68 lines).
/// </summary>
/// <remarks>
/// <b>Scope:</b> This file ports <c>git_credential_userpass</c> — the
/// stock username/password credential callback. <c>.netrc</c> file
/// parsing and credential helper subprocess execution are git CLI
/// features (not in libgit2) and are intentionally not ported.
/// </remarks>
public static class GitCredentialHelpers
{
    /// <summary>
    /// Stock credential callback that creates a <see cref="GitUserPassCredential"/>
    /// or <see cref="GitUsernameCredential"/> from a
    /// <see cref="GitUserPassPayload"/>. Matches <c>git_credential_userpass</c>
    /// (credential_helpers.c:12-53).
    /// </summary>
    /// <param name="payload">The payload containing username/password.</param>
    /// <param name="userFromUrl">The username extracted from the URL, if any.</param>
    /// <param name="allowedTypes">Bitmask of acceptable credential types.</param>
    /// <returns>A credential, or null if the payload is invalid or the
    /// allowed types don't match.</returns>
    public static GitCredential? UserPass(
        GitUserPassPayload payload,
        string? userFromUrl,
        GitCredentialType allowedTypes)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // C (credential_helpers.c:24-52): POINTER-presence semantics - a
        // NULL password fails, but an EMPTY-STRING password is accepted; an
        // empty-string payload username is used as-is and WINS over the URL
        // username.
        if (payload.Password is null)
        {
            return null;
        }

        // Username resolution:
        //   payload has username (even "") -> use payload's
        //   payload has no username, URL has -> use URL's
        //   neither -> fail
        string? effectiveUsername = payload.Username is not null ? payload.Username : userFromUrl;
        if (effectiveUsername is null)
        {
            return null;
        }

        // If only USERNAME is allowed, return a username-only credential.
        if ((allowedTypes & GitCredentialType.Username) != 0)
        {
            return new GitUsernameCredential(effectiveUsername);
        }

        // Otherwise, try USERPASS_PLAINTEXT.
        if ((allowedTypes & GitCredentialType.UserPassPlaintext) == 0)
        {
            return null;
        }

        return new GitUserPassCredential(effectiveUsername, payload.Password);
    }
}
