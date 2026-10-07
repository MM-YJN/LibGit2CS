// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Repository;

/// <content> Patch application (diff apply) operations. Managed port of libgit2's <c>src/libgit2/apply.c</c> public entry points. The pure parse core <see
/// cref="GitPatchApplier.ApplyPatchAsync"/> stays as a <c>public static</c> on <see cref="GitPatchApplier"/>. </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    // ── Apply (diff) ────────────────────────────────────────────────────

    /// <summary>
    /// Applies a <see cref="GitDiff"/> to a tree entirely in memory, returning a
    /// new ephemeral <see cref="GitIndex"/> with the result. Matches
    /// <c>git_apply_to_tree</c> (apply.c:615-680). No disk writes — the
    /// returned index is in-memory only.
    /// </summary>
    /// <param name="preimage">The baseline tree to apply the diff to.</param>
    /// <param name="diff">The diff to apply.</param>
    /// <param name="options">Apply options (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A new in-memory <see cref="GitIndex"/> containing the postimage.
    /// The caller owns disposal. No repo index is mutated. Postimage blobs
    /// are written to the ODB (matching C's <c>git_blob_create_from_buffer</c>).
    /// </returns>
    public async ValueTask<GitIndex> ApplyToTreeAsync(
        GitTree preimage, GitDiff diff, GitApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preimage);
        ArgumentNullException.ThrowIfNull(diff);

        GitApplyOptions opts = options ?? new GitApplyOptions();

        var preReader = new TreeReader(preimage, this);
        GitIndex? postimage = null;

        try
        {
            // Seed the postimage with all tree entries, then apply the diff
            // to replace/add/remove entries. The diff's old paths are removed
            // before applying deltas (to handle rename situations).
            postimage = GitIndex.New(ObjectFormat);
            await postimage.ReadTreeAsync(preimage, cancellationToken).ConfigureAwait(false);

            var postReader = new IndexReader(postimage, this);

            // First pass: remove old paths for deleted/renamed deltas
            // (apply.c:656-665). Must happen before apply_deltas to handle
            // rename situations where the old path must be gone before the
            // new path is added.
            for (int i = 0; i < diff.DeltaCount; i++)
            {
                GitDiffDelta delta = diff.GetDelta(i);
                if (delta.Status is GitDeltaStatus.Deleted or
                    GitDeltaStatus.Renamed)
                {
                    postimage.Remove(delta.OldFile.Path ?? default, stage: 0);
                }
            }

            // Apply deltas (preimage=null per apply.c:667 — the preimage
            // index is not needed for tree application).
            await ApplyDeltasAsync(preReader, null, postReader, postimage, diff, opts, cancellationToken).ConfigureAwait(false);

            return postimage;
        }
        catch
        {
            postimage?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies a diff to the repository. Matches <c>git_apply</c>
    /// (apply.c:802-896). In CHECK mode (<see cref="GitApplyFlags.Check"/>),
    /// validates in memory without writes. In execute mode (no Check flag),
    /// writes the postimage to the workdir and/or index depending on
    /// <paramref name="location"/>.
    /// </summary>
    /// <param name="diff">The diff to apply.</param>
    /// <param name="location">
    /// Where to apply the diff: <see cref="GitApplyLocation.Workdir"/>,
    /// <see cref="GitApplyLocation.Index"/>, or <see cref="GitApplyLocation.Both"/>.
    /// </param>
    /// <param name="options">Apply options (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// Thrown with <see cref="GitErrorCode.ApplyFail"/> if the patch does not
    /// apply cleanly (preimage not found, or workdir does not match index).
    /// </exception>
    public async ValueTask ApplyAsync(
        GitDiff diff, GitApplyLocation location, GitApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);

        GitApplyOptions opts = options ?? new GitApplyOptions();

        // Build the preimage reader by location (apply.c:828-840).
        GitIndex repoIndex = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        IObjectReader preReader = location switch
        {
            GitApplyLocation.Both => new WorkdirReader(this, validateIndex: true),
            GitApplyLocation.Index => new IndexReader(repoIndex, this),
            GitApplyLocation.Workdir => new WorkdirReader(this, validateIndex: false),
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };

        var preimage = GitIndex.New(ObjectFormat);
        var postimage = GitIndex.New(ObjectFormat);

        try
        {
            var postReader = new IndexReader(postimage, this);

            // Build the preimage + postimage (differences). This only
            // contains files affected by the patch — apply_deltas populates
            // both ephemeral indexes in memory. (apply.c:862)
            await ApplyDeltasAsync(preReader, preimage, postReader, postimage, diff, opts, cancellationToken).ConfigureAwait(false);

            // CHECK path: we're done. No writes. (apply.c:865-866: if
            // GIT_APPLY_CHECK → goto done, skipping execute.)
            if (opts.Flags.HasFlag(GitApplyFlags.Check))
            {
                return;
            }

            // Execute path (apply.c:868-881). Wrap in an IndexWriter for
            // atomic index write (matching git_indexwriter_init/commit).
            IndexWriter? writer = null;
            try
            {
                if (location is GitApplyLocation.Both or
                    GitApplyLocation.Index)
                {
                    writer = IndexWriter.Init(repoIndex);
                }

                switch (location)
                {
                    case GitApplyLocation.Both:
                        await ApplyToWorkdirAsync(diff, preimage, postimage, location, cancellationToken).ConfigureAwait(false);
                        await ApplyToIndexAsync(diff, postimage, cancellationToken).ConfigureAwait(false);
                        break;
                    case GitApplyLocation.Workdir:
                        await ApplyToWorkdirAsync(diff, preimage, postimage, location, cancellationToken).ConfigureAwait(false);
                        break;
                    case GitApplyLocation.Index:
                        await ApplyToIndexAsync(diff, postimage, cancellationToken).ConfigureAwait(false);
                        break;
                }

                if (writer is not null)
                {
                    await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                writer?.Dispose();
            }
        }
        finally
        {
            preimage.Dispose();
            postimage.Dispose();
        }
    }

    /// <summary>
    /// Applies all deltas in a diff. Matches <c>apply_deltas</c>
    /// (apply.c:592-613). Iterates over the diff's deltas and calls
    /// <see cref="ApplyOneAsync"/> for each.
    /// </summary>
    private async Task ApplyDeltasAsync(
        IObjectReader preReader,
        GitIndex? preimage,
        IObjectReader postReader,
        GitIndex postimage,
        GitDiff diff,
        GitApplyOptions opts,
        CancellationToken cancellationToken)
    {
        var removedPaths = new HashSet<GitPath>();

        for (int i = 0; i < diff.DeltaCount; i++)
        {
            await ApplyOneAsync(preReader, preimage, postReader, postimage,
                diff, removedPaths, i, opts, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies a diff to the working directory. Matches
    /// <c>git_apply__to_workdir</c> (apply.c:682-732). Uses checkout with the
    /// preimage index as <see cref="GitCheckoutOptions.BaselineIndex"/> to limit
    /// writes to only paths affected by the diff.
    /// </summary>
    private async Task ApplyToWorkdirAsync(
        GitDiff diff,
        GitIndex preimage,
        GitIndex postimage,
        GitApplyLocation location,
        CancellationToken cancellationToken)
    {
        // Collect all paths affected by the diff (both old and new paths for
        // renames) to limit checkout to only those files. (apply.c:692-706)
        var paths = new List<GitPath>(diff.DeltaCount * 2);
        for (int i = 0; i < diff.DeltaCount; i++)
        {
            GitDiffDelta delta = diff.GetDelta(i);
            paths.Add(delta.OldFile.Path ?? default);
            if (delta.OldFile.Path != delta.NewFile.Path)
            {
                paths.Add(delta.NewFile.Path ?? default);
            }
        }

        // Configure checkout: limit to affected paths, use preimage as
        // baseline, don't write the index. (apply.c:714-722)
        GitCheckoutStrategy strategy = GitCheckoutStrategy.DisablePathSpecMatch |
            GitCheckoutStrategy.DontWriteIndex;

        // Workdir-only: don't update the index at all. BOTH mode lets
        // checkout update the index (but DontWriteIndex prevents the disk
        // write — IndexWriter.Commit in Apply handles the write).
        if (location == GitApplyLocation.Workdir)
        {
            strategy |= GitCheckoutStrategy.DontUpdateIndex;
        }

        var checkoutOpts = new GitCheckoutOptions
        {
            Strategy = strategy,
            Paths = [.. paths],
            BaselineIndex = preimage,
        };

        await CheckoutIndexAsync(postimage, checkoutOpts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a diff to the repository index. Matches
    /// <c>git_apply__to_index</c> (apply.c:734-775). Removes deleted/renamed
    /// old paths from the repo index, then adds all postimage entries.
    /// </summary>
    private async ValueTask ApplyToIndexAsync(
        GitDiff diff,
        GitIndex postimage,
        CancellationToken cancellationToken)
    {
        GitIndex index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);

        // Remove deleted/renamed old paths from the repo index. (apply.c:748-758)
        for (int i = 0; i < diff.DeltaCount; i++)
        {
            GitDiffDelta delta = diff.GetDelta(i);
            if (delta.Status is GitDeltaStatus.Deleted or
                GitDeltaStatus.Renamed)
            {
                index.Remove(delta.OldFile.Path ?? default, stage: 0);
            }
        }

        // Add all postimage entries to the repo index. (apply.c:763-771)
        // Validate paths — git_index_add rejects paths like ".git/..." that
        // are invalid for the index.
        for (int i = 0; i < postimage.EntryCount; i++)
        {
            GitIndexEntry entry = postimage.EntryByIndex(i);
            if (!await GitPathValidator.IsValidAsync(entry.Path,
                GitPathRejectFlags.IndexDefaults | PathRejectPresets.FilesystemDefaults, this, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                throw new GitException(GitErrorCode.Invalid,
                    $"path '{entry.Path.ToUtf8String()}' is not valid",
                    GitErrorCategory.Index);
            }
            index.Add(entry);
        }
    }

    /// <summary>
    /// Applies a single delta. Matches <c>apply_one</c> (apply.c:449-590).
    /// Reads the preimage (postimage-first for chained deltas, then
    /// preimage reader), applies the patch, creates the postimage blob,
    /// and adds the result to the postimage index.
    /// </summary>
    private async Task ApplyOneAsync(
        IObjectReader preimageReader,
        GitIndex? preimage,
        IObjectReader postimageReader,
        GitIndex postimage,
        GitDiff diff,
        HashSet<GitPath> removedPaths,
        int i,
        GitApplyOptions opts,
        CancellationToken cancellationToken)
    {
        using GitPatch patch = await GitPatch.FromDiffAsync(diff, i, cancellationToken).ConfigureAwait(false);
        GitDiffDelta delta = patch.Delta;

        // Delta callback (apply.c:476-485): <0 abort, >0 skip.
        if (opts.DeltaCallback is { } deltaCb)
        {
            int cbResult = deltaCb(delta);
            if (cbResult < 0)
            {
                throw new GitException(GitErrorCode.ApplyFail,
                    $"delta callback aborted (return {cbResult})", GitErrorCategory.Patch);
            }

            if (cbResult > 0)
            {
                return;
            }
        }

        // Ensure the file has not been deleted or renamed if we're applying
        // a modification delta (apply.c:491-497).
        if (delta.Status is not GitDeltaStatus.Renamed and
            not GitDeltaStatus.Added)
        {
            if (removedPaths.Contains(delta.OldFile.Path ?? default))
            {
                throw new GitException(GitErrorCode.ApplyFail,
                    $"path '{(delta.OldFile.Path ?? default).ToUtf8String()}' has been renamed or deleted",
                    GitErrorCategory.Patch);
            }
        }

        // We may be applying a second delta to an already seen file. If so,
        // use the already modified data in the postimage instead of the
        // content from the index or working directory. (Don't do this for
        // renames, which must be specified before additional deltas since we
        // apply deltas to the target filename.) (apply.c:506-516)
        bool skipPreimage = false;
        ReadOnlyMemory<byte> preContents = default;
        GitOid preId = default;
        GitFileMode preFilemode = default;

        if (delta.Status != GitDeltaStatus.Renamed)
        {
            ReaderReadResult postRead = await postimageReader.ReadAsync(delta.OldFile.Path ?? default, cancellationToken).ConfigureAwait(false);
            if (postRead.Status == ReadStatus.Found && postRead.Result is { } postResult)
            {
                skipPreimage = true;
                preContents = postResult.Content;
                preId = postResult.Oid;
                preFilemode = postResult.Mode;
            }
            else if (postRead.Status == ReadStatus.Mismatch)
            {
                throw new GitException(GitErrorCode.ApplyFail,
                    $"{(delta.OldFile.Path ?? default).ToUtf8String()}: does not match index",
                    GitErrorCategory.Patch);
            }
            // NotFound: fall through to preimage read.
        }

        if (!skipPreimage && delta.Status != GitDeltaStatus.Added)
        {
            ReaderReadResult preRead = await preimageReader.ReadAsync(delta.OldFile.Path ?? default, cancellationToken).ConfigureAwait(false);
            ReaderResult? result = preRead.Result;

            if (preRead.Status == ReadStatus.NotFound)
            {
                // ENOTFOUND → EAPPLYFAIL (apply.c:523-524).
                throw new GitException(GitErrorCode.ApplyFail,
                    $"could not find preimage for '{(delta.OldFile.Path ?? default).ToUtf8String()}'",
                    GitErrorCategory.Patch);
            }

            if (preRead.Status == ReadStatus.Mismatch)
            {
                // When applying to BOTH, the index did not match the workdir
                // (apply.c:527-528).
                throw new GitException(GitErrorCode.ApplyFail,
                    $"{(delta.OldFile.Path ?? default).ToUtf8String()}: does not match index",
                    GitErrorCategory.Patch);
            }

            if (result is null)
            {
                throw new GitException(GitErrorCode.ApplyFail,
                    $"could not read preimage for '{(delta.OldFile.Path ?? default).ToUtf8String()}'",
                    GitErrorCategory.Patch);
            }

            preContents = result.Content;
            preId = result.Oid;
            preFilemode = result.Mode;

            // Populate the preimage index with the contents we're using as
            // the preimage for this file (apply.c:548-556). This allows
            // checkout to use this expected preimage as the baseline.
            if (preimage is not null)
            {
                GitFileMode preMode = delta.OldFile.Mode != 0
                    ? delta.OldFile.Mode
                    : preFilemode;
                preimage.Add(new GitIndexEntry(delta.OldFile.Path ?? default, preId, preMode));
            }
        }

        // Apply the patch and create the postimage blob (apply.c:559-573).
        if (delta.Status != GitDeltaStatus.Deleted)
        {
            GitApplyResult applyResult = await GitPatchApplier.ApplyPatchAsync(preContents, patch, opts, cancellationToken).ConfigureAwait(false);

            // Write the postimage blob to the ODB. Matches
            // git_blob_create_from_buffer (apply.c:562-563). The blob must
            // exist in the ODB so checkout can read it when writing to the
            // workdir. Write computes the hash and persists if not present.
            GitOid postId = await Objects.WriteAsync(
                GitObjectType.Blob, applyResult.Content, cancellationToken).ConfigureAwait(false);

            postimage.Add(new GitIndexEntry(applyResult.Filename ?? default, postId, applyResult.Mode));
        }

        // Track removed/added paths for the rename/delete guard
        // (apply.c:575-581).
        if (delta.Status is GitDeltaStatus.Renamed or
            GitDeltaStatus.Deleted)
        {
            removedPaths.Add(delta.OldFile.Path ?? default);
        }

        if (delta.Status is GitDeltaStatus.Renamed or
            GitDeltaStatus.Added)
        {
            removedPaths.Remove(delta.NewFile.Path ?? default);
        }
    }
}
