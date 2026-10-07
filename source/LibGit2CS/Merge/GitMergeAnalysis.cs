// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// Merge analysis result flags. Matches <c>git_merge_analysis_t</c> in
/// <c>include/git2/merge.h</c>.
/// </summary>
[Flags]
public enum GitMergeAnalysis
{
    /// <summary>No merge possible.</summary>
    None = 0,

    /// <summary>
    /// Both sides have diverged; a normal merge commit is required.
    /// </summary>
    Normal = 1 << 0,

    /// <summary>
    /// Their heads are already reachable from HEAD; nothing to do.
    /// </summary>
    UpToDate = 1 << 1,

    /// <summary>
    /// HEAD is an ancestor of their heads; a fast-forward is possible.
    /// Always combined with <see cref="Normal"/>.
    /// </summary>
    FastForward = 1 << 2,

    /// <summary>
    /// HEAD is unborn (no commits yet); a fast-forward will create the
    /// initial commit. Always combined with <see cref="FastForward"/>.
    /// </summary>
    Unborn = 1 << 3,
}
