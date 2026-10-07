// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Index;

namespace LibGit2CS.Merge;

/// <summary>
/// Accumulator for 3-way merge results: staged entries (resolved), conflicts
/// (unresolved), and resolved conflicts (for REUC). Matches
/// <c>git_merge_diff_list</c> in <c>merge.h:76-97</c>.
/// </summary>
internal sealed class MergeDiffList
{
    /// <summary>
    /// Entries that have been staged (either only one side changed, or the
    /// two changes were non-conflicting and mergeable). Written as stage-0
    /// entries in the output index.
    /// </summary>
    public List<GitIndexEntry> Staged { get; } = [];

    /// <summary>
    /// Conflicts that have not been automerged. Written as high-stage entries
    /// (stages 1/2/3) in the output index.
    /// </summary>
    public List<MergeDiff> Conflicts { get; } = [];

    /// <summary>
    /// Conflicts that have been automerged. Written to the REUC extension
    /// in the output index.
    /// </summary>
    public List<MergeDiff> Resolved { get; } = [];
}
