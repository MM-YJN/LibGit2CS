// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Config;

/// <summary>
/// <c>core.eol</c> config values. Maps to <c>GIT_EOL_*</c>
/// in <c>src/libgit2/repository.h:84-92</c>. The configmap table is in
/// <c>src/libgit2/config_cache.c:33-36</c>.
/// </summary>
public enum GitEol
{
    /// <summary><c>core.eol</c> unset / false. Uses native EOL. (<c>GIT_EOL_UNSET</c>)</summary>
    Unset = 0,

    /// <summary><c>core.eol = crlf</c>. (<c>GIT_EOL_CRLF</c>)</summary>
    Crlf = 1,

    /// <summary><c>core.eol = lf</c>. (<c>GIT_EOL_LF</c>)</summary>
    Lf = 2,
}
