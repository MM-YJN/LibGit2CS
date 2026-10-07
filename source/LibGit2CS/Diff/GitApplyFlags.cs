// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Flags for patch application. Maps 1:1 to <c>git_apply_flags_t</c>
/// (<c>include/git2/apply.h:72-78</c>).
/// </summary>
[Flags]
public enum GitApplyFlags
{
    /// <summary>No flags (normal apply). (<c>GIT_APPLY_CHECK</c> absent)</summary>
    None = 0,

    /// <summary>Validate-only (dry run): parse + apply in-memory, no writes. (<c>GIT_APPLY_CHECK</c>; <c>1u &lt;&lt; 0</c>)</summary>
    Check = 1 << 0,
}
