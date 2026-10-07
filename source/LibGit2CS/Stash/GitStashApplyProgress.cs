// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Stash;

/// <summary>
/// Progress notification enum for stash apply. Matches
/// <c>git_stash_apply_progress_t</c> in <c>include/git2/stash.h</c>.
/// </summary>
public enum GitStashApplyProgress
{
    /// <summary>No progress yet.</summary>
    None = 0,

    /// <summary>Loading the stash commit.</summary>
    LoadingStash = 1,

    /// <summary>Analyzing the index.</summary>
    AnalyzeIndex = 2,

    /// <summary>Analyzing modified files.</summary>
    AnalyzeModified = 3,

    /// <summary>Analyzing untracked files.</summary>
    AnalyzeUntracked = 4,

    /// <summary>Checking out untracked files.</summary>
    CheckoutUntracked = 5,

    /// <summary>Checking out modified files.</summary>
    CheckoutModified = 6,

    /// <summary>Done.</summary>
    Done = 7,
}
