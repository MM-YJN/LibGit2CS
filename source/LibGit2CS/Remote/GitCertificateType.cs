// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Type of host certificate. Maps to <c>git_cert_t</c>.
/// </summary>
public enum GitCertificateType
{
    /// <summary>No certificate information available.</summary>
    None = 0,

    /// <summary>X509 DER-encoded certificate.</summary>
    X509 = 1,

    /// <summary>SSH host key.</summary>
    HostkeySsh = 2,

    /// <summary>String array certificate info.</summary>
    StringArray = 3,
}
