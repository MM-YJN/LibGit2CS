// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// Flags controlling 3-way merge behavior. Matches <c>git_merge_flag_t</c>
/// in <c>include/git2/merge.h</c>.
/// </summary>
[Flags]
public enum GitMergeFlags
{
    /// <summary>No flags (default).</summary>
    None = 0,

    /// <summary>Detect renames during merge (exact OID + inexact similarity).</summary>
    FindRenames = 1 << 0,

    /// <summary>Fail immediately if any conflict is found.</summary>
    FailOnConflict = 1 << 1,

    /// <summary>Skip writing the REUC extension.</summary>
    SkipReuc = 1 << 2,

    /// <summary>No recursive merge base computation.</summary>
    NoRecursive = 1 << 3,

    /// <summary>Virtual base building (for recursive merge).</summary>
    VirtualBase = 1 << 4,
}
