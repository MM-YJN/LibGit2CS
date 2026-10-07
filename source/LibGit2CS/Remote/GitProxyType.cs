// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Proxy type for remote connections. Maps to <c>git_proxy_t</c> in
/// <c>include/git2/proxy.h</c>.
/// </summary>
public enum GitProxyType
{
    /// <summary>No proxy — direct connection.</summary>
    None = 0,

    /// <summary>Auto-detect proxy from environment/system settings.</summary>
    Auto = 1,

    /// <summary>Use the proxy specified by <see cref="GitProxyConfig.Url"/>.</summary>
    Specified = 2,
}
