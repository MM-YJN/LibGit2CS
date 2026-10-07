// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Where to apply a patch. Maps 1:1 to <c>git_apply_location_t</c>
/// (<c>include/git2/apply.h:148-166</c>).
/// </summary>
public enum GitApplyLocation
{
    /// <summary>Apply to the working directory. (<c>GIT_APPLY_LOCATION_WORKDIR</c>)</summary>
    Workdir = 0,

    /// <summary>Apply to the index. (<c>GIT_APPLY_LOCATION_INDEX</c>)</summary>
    Index = 1,

    /// <summary>Apply to both workdir and index. (<c>GIT_APPLY_LOCATION_BOTH</c>)</summary>
    Both = 2,
}
