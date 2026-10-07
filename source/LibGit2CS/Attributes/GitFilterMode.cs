// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// Filter direction. Maps to <c>git_filter_mode_t</c> in
/// <c>include/git2/filter.h:37-42</c>.
/// </summary>
public enum GitFilterMode
{
    /// <summary>ODB → worktree (smudge). (<c>GIT_FILTER_TO_WORKTREE</c>)</summary>
    ToWorktree = 0,

    /// <summary>Workdir → ODB (clean). (<c>GIT_FILTER_TO_ODB</c>)</summary>
    ToOdb = 1,
}
