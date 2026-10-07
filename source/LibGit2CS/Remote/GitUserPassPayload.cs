// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// A username/password credential payload. Pass this to
/// <see cref="GitCredentialHelpers.UserPass"/> as the payload. Managed equivalent of
/// <c>git_credential_userpass_payload</c>.
/// </summary>
public sealed class GitUserPassPayload
{
    /// <summary>The username (may be null to use the URL's username).</summary>
    public string? Username { get; init; }

    /// <summary>The password (required).</summary>
    public string Password { get; init; } = string.Empty;
}
