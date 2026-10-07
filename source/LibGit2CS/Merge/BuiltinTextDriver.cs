// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.Merge;
/// <summary>
/// Built-in <c>text</c> merge driver. Performs a standard three-way merge
/// via <see cref="GitRepository.MergeFileFromIndexAsync"/>, writing the merged content
/// for ODB storage by the caller. Matches <c>git_merge_driver__text</c>
/// (<c>merge_driver.c:155-163</c>) whose <c>apply</c> function is
/// <c>git_merge_driver__builtin_apply</c> (<c>merge_driver.c:70-119</c>)
/// with <c>favor = GIT_MERGE_FILE_FAVOR_NORMAL</c>.
/// </summary>
internal sealed class BuiltinTextDriver : IGitMergeDriver
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
        return await BuiltinApplyAsync(source, GitMergeFileFavor.Normal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared apply logic for text/union drivers. Matches
    /// <c>git_merge_driver__builtin_apply</c> (<c>merge_driver.c:70-119</c>).
    /// </summary>
    internal static async Task<(GitMergeDriverApplyResult Result, GitMergeDriverOutput? Output)> BuiltinApplyAsync(
        GitMergeDriverSource src,
        GitMergeFileFavor favor,
        CancellationToken cancellationToken)
    {
        // Build file options from the source's options (or default),
        // overriding the favor with the driver's favor. Matches
        // merge_driver.c:79-89.
        GitMergeFileOptions fileOpts = src.FileOptions ?? GitMergeFileOptions.Default;
        if (favor != GitMergeFileFavor.Normal)
        {
            fileOpts = fileOpts with { Favor = favor };
        }

        // Perform the file-level merge (reads blobs from ODB). Does NOT
        // write the result blob — that's the caller's job. Matches
        // merge_driver.c:91-93.
        GitMergeFileResult result = await src.Repo.MergeFileFromIndexAsync(
            src.Ancestor,
            src.Ours,
            src.Theirs,
            fileOpts,
            cancellationToken).ConfigureAwait(false);

        // If the merge produced conflicts and the caller did not request
        // AcceptConflicts, signal a conflict. Matches merge_driver.c:95-99.
        if (!result.Automergeable &&
            (fileOpts.Flags & GitMergeFileFlags.AcceptConflicts) == 0)
        {
            return (GitMergeDriverApplyResult.Conflict, null);
        }

        // Select best path and mode from the three sides. Matches
        // merge_driver.c:101-109.
        GitPath? path = GitMergeFile.BestPath(
            src.Ancestor?.Path,
            src.Ours?.Path,
            src.Theirs?.Path);

        uint mode = GitMergeFile.BestMode(
            (uint)(src.Ancestor?.Mode ?? 0),
            (uint)(src.Ours?.Mode ?? 0),
            (uint)(src.Theirs?.Mode ?? 0));

        var output = new GitMergeDriverOutput(path, mode, result.Content);
        return (GitMergeDriverApplyResult.Success, output);
    }
}
