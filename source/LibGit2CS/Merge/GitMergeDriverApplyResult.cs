// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// The outcome of a merge driver apply attempt. Replaces the integer return
/// value of <c>git_merge_driver_apply_fn</c> (<c>sys/merge.h:154-160</c>):
/// <c>0</c> → <see cref="Success"/>, <c>GIT_EMERGECONFLICT</c> →
/// <see cref="Conflict"/>, <c>GIT_PASSTHROUGH</c> → <see cref="PassThrough"/>.
/// </summary>
public enum GitMergeDriverApplyResult
{
    /// <summary>
    /// The driver successfully merged the file. The output record contains
    /// the merged path, mode, and content. Matches C return <c>0</c>.
    /// </summary>
    Success,

    /// <summary>
    /// The driver could not produce a merge; the file should remain
    /// conflicted. Matches <c>GIT_EMERGECONFLICT</c>.
    /// </summary>
    Conflict,

    /// <summary>
    /// The driver deferred; the default <c>text</c> merge driver should be
    /// invoked instead. Matches <c>GIT_PASSTHROUGH</c>.
    /// </summary>
    PassThrough,
}
