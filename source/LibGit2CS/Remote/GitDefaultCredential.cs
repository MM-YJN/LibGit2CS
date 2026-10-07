// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Default credential — let the transport choose (Negotiate/NTLM/Kerberos).
/// Maps to <c>git_credential_default</c>.
/// </summary>
public sealed class GitDefaultCredential : GitCredential
{
    /// <summary>Creates a default credential.</summary>
    public GitDefaultCredential()
    {
    }

    /// <inheritdoc/>
    public override string? Username => null;

    /// <inheritdoc/>
    public override GitCredentialType Type => GitCredentialType.Default;
}
