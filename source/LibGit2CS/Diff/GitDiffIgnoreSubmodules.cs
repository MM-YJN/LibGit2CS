// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Submodule-ignore behavior for diff. Maps to <c>git_submodule_ignore_t</c>
/// in <c>include/git2/types.h:347-352</c>.
/// </summary>
public enum GitDiffIgnoreSubmodules
{
    /// <summary>Use the submodule's configured ignore value. (<c>GIT_SUBMODULE_IGNORE_UNSPECIFIED</c> = -1)</summary>
    Unspecified = -1,

    /// <summary>Any change or untracked files == dirty. (<c>GIT_SUBMODULE_IGNORE_NONE</c>)</summary>
    None = 1,

    /// <summary>Dirty only if tracked files change. (<c>GIT_SUBMODULE_IGNORE_UNTRACKED</c>)</summary>
    Untracked = 2,

    /// <summary>Ignore working-directory changes; dirty on HEAD change only. (<c>GIT_SUBMODULE_IGNORE_DIRTY</c>)</summary>
    Dirty = 3,

    /// <summary>Never check if the submodule is dirty. (<c>GIT_SUBMODULE_IGNORE_ALL</c>)</summary>
    All = 4,
}
