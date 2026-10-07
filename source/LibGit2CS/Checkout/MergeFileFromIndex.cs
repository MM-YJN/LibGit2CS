// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using Xdiff;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;
using GitMergeFile = LibGit2CS.Merge.GitMergeFile;

namespace LibGit2CS.Checkout;

/// <summary>
/// Performs a 3-way merge of index conflict entries, producing merged content
/// with conflict markers. Matches <c>git_merge_file_from_index</c> as used by
/// <c>checkout_write_merge</c> (checkout.c:2070-2165).
/// </summary>
/// <remarks>
/// The inputs are the RAW ODB blob contents (no worktree filters) — filters
/// are applied by the caller to the MERGED output, matching C's
/// <c>checkout_write_merge</c> which loads the filter list for the merged
/// buffer (checkout.c:2130-2145).
/// </remarks>
internal static class MergeFileFromIndex
{
    /// <param name="path">The conflicted path (byte-faithful GitPath).</param>
    /// <param name="repo">The repository used by this operation.</param>
    /// <param name="index">The index to read.</param>
    /// <param name="opts">Options controlling this operation.</param>
    /// <param name="conflictStyle">The conflict marker style.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<(byte[] Content, int ConflictCount, string? ResultPath, GitFileMode ResultMode)> MergeAsync(
        GitRepository repo,
        GitIndex index,
        GitPath path,
        GitCheckoutOptions opts,
        GitConflictStyle conflictStyle,
        CancellationToken cancellationToken = default)
    {
        // ConflictGet has an internal GitPath overload — call it directly, no string roundtrip.
        (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = index.ConflictGet(path);
        return await MergeFromEntriesAsync(repo, ancestor, ours, theirs, opts, conflictStyle, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<(byte[] Content, int ConflictCount, string? ResultPath, GitFileMode ResultMode)> MergeFromEntriesAsync(
        GitRepository repo,
        GitIndexEntry? ancestor,
        GitIndexEntry? ours,
        GitIndexEntry? theirs,
        GitCheckoutOptions opts,
        GitConflictStyle conflictStyle,
        CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> ancestorContent = await LoadBlobAsync(repo, ancestor?.Id, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> oursContent = await LoadBlobAsync(repo, ours?.Id, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> theirsContent = await LoadBlobAsync(repo, theirs?.Id, cancellationToken).ConfigureAwait(false);

        MergeStyle mergeStyle = conflictStyle switch
        {
            GitConflictStyle.Diff3 => MergeStyle.Diff3,
            GitConflictStyle.ZealousDiff3 => MergeStyle.ZealousDiff3,
            _ => MergeStyle.Merge,
        };

        string ourLabel = opts.OurLabel ?? "ours";
        string theirLabel = opts.TheirLabel ?? "theirs";

        // C (checkout.c:2097-2111): for a 2-to-1 rename conflict (ours->path != theirs->path) the merge labels are decorated as "side:path" via
        // conflict_entry_name, not left as bare `ours`/`theirs`.
        if (ours is { } o && theirs is { } t && !o.Path.Equals(t.Path))
        {
            ourLabel = $"{ourLabel}:{o.Path.ToUtf8String()}";
            theirLabel = $"{theirLabel}:{t.Path.ToUtf8String()}";
        }

        var mergeOpts = new MergeOptions
        {
            Style = mergeStyle,
            AncestorLabel = opts.AncestorLabel ?? "ancestor",
            OurLabel = ourLabel,
            TheirLabel = theirLabel,
        };

        MergeResult result = Merger.Merge(
            ancestorContent,
            oursContent,
            theirsContent,
            mergeOpts);

        // Compute the result path via BestPath (matches checkout_merge_path which uses result->path from git_merge_file_from_index). For rename 2-to-1
        // conflicts, ours.Path != theirs.Path, and BestPath selects which one the merged content should be written to. ancestor/ours/theirs.Path and BestPath
        // are byte-faithful GitPath.
        GitPath? resultPath = GitMergeFile.BestPath(
            ancestor?.Path,
            ours?.Path,
            theirs?.Path);

        // Compute the result mode via BestMode (matches
        // git_merge_file__best_mode, merge.h:184-206 — the merged file is
        // written with this mode by checkout_write_merge,
        // checkout.c:2147-2151).
        var resultMode = (GitFileMode)GitMergeFile.BestMode(
            ancestor is { } am ? (uint)am.Mode : 0,
            ours is { } om ? (uint)om.Mode : 0,
            theirs is { } tm ? (uint)tm.Mode : 0);

        return (result.Content, result.ConflictCount, resultPath?.ToUtf8String(), resultMode);
    }

    private static async Task<ReadOnlyMemory<byte>> LoadBlobAsync(GitRepository repo, GitOid? oid, CancellationToken cancellationToken)
    {
        if (oid is null || oid.Value.IsZero)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        // C's
        // merge_file_input_from_index propagates git_odb_read failures
        // (merge_file.c:29-44) — a missing conflict side aborts the merge
        // instead of silently merging empty content.
        GitBlob? blob = await repo.Objects.LookupAsync<GitBlob>(oid.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"conflict blob {oid.Value} is not in the database",
                GitErrorCategory.Odb);

        using GitBlob _ = blob;
        return blob.Content;
    }
}
