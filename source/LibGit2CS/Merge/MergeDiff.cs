// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Diff;
using LibGit2CS.Index;

namespace LibGit2CS.Merge;

/// <summary>
/// Description of changes to one file across three trees during a 3-way
/// merge. Matches <c>git_merge_diff</c> in <c>merge.h:102-113</c>.
/// </summary>
internal sealed class MergeDiff
{
    /// <summary>The conflict classification type.</summary>
    public MergeDiffType Type { get; set; } = MergeDiffType.None;

    /// <summary>The ancestor (merge base) entry, or default if no ancestor.</summary>
    public GitIndexEntry AncestorEntry { get; set; }

    /// <summary>Our side's entry, or default if absent.</summary>
    public GitIndexEntry OurEntry { get; set; }

    /// <summary>The delta status of ours vs ancestor.</summary>
    public GitDeltaStatus OurStatus { get; set; }

    /// <summary>Their side's entry, or default if absent.</summary>
    public GitIndexEntry TheirEntry { get; set; }

    /// <summary>The delta status of theirs vs ancestor.</summary>
    public GitDeltaStatus TheirStatus { get; set; }

    /// <summary>True if the entry exists (mode != 0).</summary>
    public static bool EntryExists(in GitIndexEntry entry) => entry.Mode != 0;
}
