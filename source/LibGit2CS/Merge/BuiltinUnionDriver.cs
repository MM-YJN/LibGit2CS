// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// Built-in <c>union</c> merge driver. Like <c>text</c> but resolves all
/// content conflicts by union (ours + theirs). Matches
/// <c>git_merge_driver__union</c> (<c>merge_driver.c:165-173</c>) whose
/// <c>apply</c> is <c>git_merge_driver__builtin_apply</c> with
/// <c>favor = GIT_MERGE_FILE_FAVOR_UNION</c>.
/// </summary>
internal sealed class BuiltinUnionDriver : IGitMergeDriver
{
    /// <inheritdoc/>
    public void Initialize() { }

    /// <inheritdoc/>
    public void Shutdown() { }

    /// <inheritdoc/>
    public async Task<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)> ApplyAsync(
        string filterName,
        GitMergeDriverSource source,
        CancellationToken cancellationToken)
    {
        return await BuiltinTextDriver.BuiltinApplyAsync(source, GitMergeFileFavor.Union, cancellationToken).ConfigureAwait(false);
    }
}
