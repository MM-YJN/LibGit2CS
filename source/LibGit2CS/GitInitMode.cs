// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS;

/// <summary>
/// Shared repository mode. Maps to <c>git_repository_init_mode_t</c>.
/// </summary>
public enum GitInitMode
{
    /// <summary>Use the process umask (default). (<c>GIT_REPOSITORY_INIT_SHARED_UMASK = 0</c>)</summary>
    SharedUmask = 0,

    /// <summary>Group-shared (setgid bit, octal 02775). (<c>GIT_REPOSITORY_INIT_SHARED_GROUP</c>)</summary>
    SharedGroup = 1533, // 0o2775

    /// <summary>All-shared (setgid bit, octal 02777). (<c>GIT_REPOSITORY_INIT_SHARED_ALL</c>)</summary>
    SharedAll = 1535, // 0o2777
}
