// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Stash;

/// <summary>
/// Flags controlling stash apply behavior. Matches
/// <c>git_stash_apply_flags</c> in <c>include/git2/stash.h</c>.
/// </summary>
[Flags]
public enum GitStashApplyFlags
{
    /// <summary>Default behavior.</summary>
    Default = 0,

    /// <summary>Restore the index to the stashed state.</summary>
    ReinstateIndex = 1 << 0,
}
