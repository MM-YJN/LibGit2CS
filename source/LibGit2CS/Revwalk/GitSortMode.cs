// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Revwalk;

/// <summary>
/// Sort mode for <see cref="GitRevWalker"/>. Matches <c>git_sort_t</c>
/// (<c>git2/revwalk.h:26-53</c>) with the <c>GIT_SORT_</c> prefix dropped.
/// These are bit flags; <see cref="Topological"/> | <see cref="Time"/> yields
/// <c>git log --date-order</c> behavior.
/// </summary>
[Flags]
public enum GitSortMode
{
    /// <summary>No ordering guarantee (discovery order). Matches <c>GIT_SORT_NONE</c>.</summary>
    None = 0,

    /// <summary>Topological order: children before parents. Matches <c>GIT_SORT_TOPOLOGICAL</c>.</summary>
    Topological = 1,

    /// <summary>Commit-time order (newest first). Matches <c>GIT_SORT_TIME</c>.</summary>
    Time = 2,

    /// <summary>Reverse the primary order. Matches <c>GIT_SORT_REVERSE</c>.</summary>
    Reverse = 4,
}
