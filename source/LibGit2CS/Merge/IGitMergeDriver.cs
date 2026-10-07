// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// A merge driver — a pluggable mechanism for conflict resolution on files
/// changed in both sides of a merge. Managed port of
/// <c>git_merge_driver</c> (<c>include/git2/sys/merge.h:169-186</c>).
/// </summary>
/// <remarks>
/// Custom drivers implement this interface directly; there are no function
/// pointers (AOT-clean). Built-in drivers (<c>text</c>, <c>union</c>,
/// <c>binary</c>) are registered in the <see cref="GitMergeDriverRegistry"/>
/// static constructor.
/// </remarks>
public interface IGitMergeDriver
{
    /// <summary>
    /// Optional one-time initialization, invoked before the driver is first
    /// used. Matches <c>git_merge_driver_init_fn</c>
    /// (<c>sys/merge.h:112-111</c>). Called at most once per process
    /// lifetime.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Optional shutdown, invoked when the driver is unregistered or the
    /// process exits. Matches <c>git_merge_driver_shutdown_fn</c>
    /// (<c>sys/merge.h:126</c>). Called at most once.
    /// </summary>
    void Shutdown();

    /// <summary>
    /// Performs the merge. Matches <c>git_merge_driver_apply_fn</c>
    /// (<c>sys/merge.h:154-160</c>).
    /// </summary>
    /// <param name="filterName">The name of the driver as specified by the
    /// file's <c>merge</c> attribute (e.g. <c>"custom"</c>, <c>"text"</c>,
    /// <c>"*"</c>).</param>
    /// <param name="source">The data about the file to merge.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of the outcome (<see cref="GitMergeDriverApplyResult"/>)
    /// and the output (the merged path, mode, and content). The output is
    /// meaningful only when the result is <see cref="GitMergeDriverApplyResult.Success"/>.</returns>
    Task<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)> ApplyAsync(
        string filterName,
        GitMergeDriverSource source,
        CancellationToken cancellationToken = default);
}
