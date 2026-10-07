// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Config;

/// <summary>
/// <c>core.safecrlf</c> config values. Maps to <c>GIT_SAFE_CRLF_*</c>
/// in <c>src/libgit2/repository.h:73-75</c>. The configmap table is in
/// <c>src/libgit2/config_cache.c:56-58</c>.
/// </summary>
public enum GitSafeCrlf
{
    /// <summary><c>core.safecrlf = false</c>. No safety check. (<c>GIT_SAFE_CRLF_FALSE</c>)</summary>
    False = 0,

    /// <summary><c>core.safecrlf = true</c>. Reject destructive conversions. (<c>GIT_SAFE_CRLF_FAIL</c>)</summary>
    Fail = 1,

    /// <summary><c>core.safecrlf = warn</c>. Warn on destructive conversions. (<c>GIT_SAFE_CRLF_WARN</c>)</summary>
    Warn = 2,
}
