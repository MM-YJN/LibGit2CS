// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Reset;

/// <summary>
/// Reset mode. Maps 1:1 to <c>git_reset_t</c> in
/// <c>include/git2/reset.h:14-24</c>.
/// </summary>
public enum GitResetMode
{
    /// <summary>
    /// Move HEAD to the target commit. Index and working directory are
    /// unchanged. (<c>GIT_RESET_SOFT = 1</c>)
    /// </summary>
    Soft = 1,

    /// <summary>
    /// SOFT + replace the index with the target commit's tree. Working
    /// directory is unchanged. (<c>GIT_RESET_MIXED = 2</c>)
    /// </summary>
    Mixed = 2,

    /// <summary>
    /// MIXED + replace the working directory with the target commit's tree
    /// (via checkout with <c>FORCE</c> strategy). (<c>GIT_RESET_HARD = 3</c>)
    /// </summary>
    Hard = 3,
}
