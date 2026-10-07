// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// Which side to favor when resolving content conflicts. Matches
/// <c>git_merge_file_favor_t</c> in <c>include/git2/merge.h</c>.
/// </summary>
public enum GitMergeFileFavor
{
    /// <summary>Leave conflicts marked (default, normal merge).</summary>
    Normal = 0,

    /// <summary>Resolve all conflicts by taking the ours side.</summary>
    Ours = 1,

    /// <summary>Resolve all conflicts by taking the theirs side.</summary>
    Theirs = 2,

    /// <summary>Resolve all conflicts by union (ours + theirs).</summary>
    Union = 3,
}
