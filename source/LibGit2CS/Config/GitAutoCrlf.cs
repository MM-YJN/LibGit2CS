// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Config;

/// <summary>
/// <c>core.autocrlf</c> config values. Maps to <c>GIT_AUTO_CRLF_*</c>
/// in <c>src/libgit2/repository.h:78-81</c>. The configmap table is in
/// <c>src/libgit2/config_cache.c:50-52</c>.
/// </summary>
public enum GitAutoCrlf
{
    /// <summary><c>core.autocrlf = false</c>. No automatic conversion. (<c>GIT_AUTO_CRLF_FALSE</c>)</summary>
    False = 0,

    /// <summary><c>core.autocrlf = true</c>. CRLF→LF on clean, LF→CRLF on smudge. (<c>GIT_AUTO_CRLF_TRUE</c>)</summary>
    True = 1,

    /// <summary><c>core.autocrlf = input</c>. CRLF→LF on clean, no smudge. (<c>GIT_AUTO_CRLF_INPUT</c>)</summary>
    Input = 2,
}
