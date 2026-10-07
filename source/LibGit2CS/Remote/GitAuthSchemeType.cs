// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// HTTP authentication scheme bitmask. Maps to <c>git_http_auth_t</c> in
/// <c>src/libgit2/transports/auth.h</c>.
/// </summary>
[Flags]
public enum GitAuthSchemeType
{
    /// <summary>No auth scheme.</summary>
    None = 0,

    /// <summary>Basic auth (base64-encoded username:password). (<c>GIT_HTTP_AUTH_BASIC = 1</c>)</summary>
    Basic = 1,

    /// <summary>Negotiate / SPNEGO (Kerberos via <see cref="System.Net.Security.NegotiateAuthentication"/>). (<c>GIT_HTTP_AUTH_NEGOTIATE = 2</c>)</summary>
    Negotiate = 2,

    /// <summary>NTLM (challenge-response via <see cref="System.Net.Security.NegotiateAuthentication"/>). (<c>GIT_HTTP_AUTH_NTLM = 4</c>)</summary>
    Ntlm = 4,
}
