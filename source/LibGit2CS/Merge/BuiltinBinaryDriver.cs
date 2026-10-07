// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// Built-in <c>binary</c> merge driver. Always produces a conflict — binary
/// files cannot be merged. Matches <c>git_merge_driver__binary</c>
/// (<c>merge_driver.c:175-180</c>) whose <c>apply</c> is
/// <c>merge_driver_binary_apply</c> (<c>merge_driver.c:121-137</c>).
/// </summary>
internal sealed class BuiltinBinaryDriver : IGitMergeDriver
{
    /// <inheritdoc/>
    public void Initialize() { }

    /// <inheritdoc/>
    public void Shutdown() { }

    /// <inheritdoc/>
    public Task<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)> ApplyAsync(
        string filterName,
        GitMergeDriverSource source,
        CancellationToken cancellationToken)
    {
        return Task.FromResult((GitMergeDriverApplyResult.Conflict, (GitMergeDriverOutput?)null));
    }
}
