// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Reset;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.Repository;

/// <content> Reset operations. Managed port of libgit2's <c>src/libgit2/reset.c</c>. </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>
    /// Resets the repository to a target commit. Matches
    /// <c>git_reset</c> (reset.c:185-195).
    /// </summary>
    /// <param name="target">The target commit to reset to.</param>
    /// <param name="mode">The reset mode (Soft/Mixed/Hard).</param>
    /// <param name="checkoutOpts">Checkout options for Hard mode. If null, uses Force strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ResetAsync(Commit target, GitResetMode mode, GitCheckoutOptions? checkoutOpts = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await ResetAsync(target, description: target.Id.ToString(), mode, checkoutOpts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Internal overload carrying the reflog "to" description — C's <c>reset</c> (reset.c:147) formats <c>"reset: moving to %s"</c> from the
    /// caller-supplied string; <c>git_reset_from_annotated</c> passes the annotated commit's description. </summary>
    private async ValueTask ResetAsync(Commit target, string description, GitResetMode mode, GitCheckoutOptions? checkoutOpts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Validation: Mixed/Hard require a non-bare repo.
        if (mode != GitResetMode.Soft && IsBare)
        {
            throw new GitException(
                GitErrorCode.BareRepo,
                "cannot perform a mixed or hard reset on a bare repository",
                GitErrorCategory.Repository);
        }

        // Soft reset guard: rejected while a merge is in progress OR the
        // index has unmerged entries. C (reset.c:138-145): GIT_RESET_SOFT is
        // refused when git_repository_state(repo) ==
        // GIT_REPOSITORY_STATE_MERGE || git_index_has_conflicts(index) —
        // GIT_EUNMERGED, GIT_ERROR_OBJECT, "Cannot perform reset (soft) in
        // the middle of a merge".
        GitIndex index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (mode == GitResetMode.Soft && (State == RepositoryState.Merge || index.HasConflicts))
        {
            throw new GitException(
                GitErrorCode.Unmerged,
                "Cannot perform reset (soft) in the middle of a merge",
                GitErrorCategory.Object);
        }

        GitTree tree = await Objects.LookupAsync<GitTree>(target.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "target tree not found", GitErrorCategory.Object);

        // Build reflog message (reset.c:147): "reset: moving to %s".
        string logMessage = $"reset: moving to {description}";

        // Hard: update working directory via checkout.
        if (mode == GitResetMode.Hard)
        {
            GitCheckoutOptions opts = checkoutOpts ?? new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force };
            // C (reset.c:150-156): opts.checkout_strategy = GIT_CHECKOUT_FORCE
            // — the caller's strategy bits are REPLACED, not OR'd in.
            opts = opts with { Strategy = GitCheckoutStrategy.Force };
            await CheckoutTreeAsync(tree, opts, cancellationToken).ConfigureAwait(false);
        }

        // Update HEAD (all modes).
        await UpdateHeadForResetAsync(target.Id, logMessage, cancellationToken).ConfigureAwait(false);

        // Update index (Mixed and Hard).
        if (mode != GitResetMode.Soft)
        {
            await index.ReadTreeAsync(tree, cancellationToken).ConfigureAwait(false);
            await index.WriteAsync(cancellationToken).ConfigureAwait(false);
            StateCleanup();
        }
    }

    /// <summary>
    /// Resets the repository to an annotated commit. Matches
    /// <c>git_reset_from_annotated</c> (reset.c:197-204). Uses the annotated
    /// commit's description for the reflog message.
    /// </summary>
    /// <param name="target">The target annotated commit to reset to.</param>
    /// <param name="mode">The reset mode (Soft/Mixed/Hard).</param>
    /// <param name="checkoutOpts">Checkout options for Hard mode. If null, uses Force strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ResetFromAnnotatedAsync(GitAnnotatedCommit target, GitResetMode mode, GitCheckoutOptions? checkoutOpts = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Use the description for a richer reflog message.
        // Matches git_reset_from_annotated (reset.c:197-204): the annotated
        // commit must have a target commit; C dereferences annotated->commit
        // directly, so a NULL here is an invalid-state error.
        Commit? commit = target.Commit;
        if (commit is null)
        {
            throw new GitException(GitErrorCode.Invalid, "annotated commit has no target commit", GitErrorCategory.Reference);
        }

        await ResetAsync(commit, target.Description, mode, checkoutOpts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets specific paths in the index to match the target commit.
    /// Matches <c>git_reset_default</c> (reset.c:22-100). Does NOT touch
    /// HEAD or the working directory.
    /// </summary>
    /// <param name="target">The target commit (or null to remove the paths from the index).</param>
    /// <param name="paths">The pathspec of files to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ResetDefaultAsync(Commit? target, string[] paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        GitPath[] pathSpecs = Array.ConvertAll(paths, GitPath.FromUtf8String);
        await ResetDefaultAsync(target, pathSpecs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-faithful overload of <see cref="ResetDefaultAsync(Commit?, string[], CancellationToken)"/>. </summary>
    public async Task ResetDefaultAsync(Commit? target, GitPath[] paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // C's
        // git_reset_default begins with GIT_ASSERT_ARG(pathspecs &&
        // pathspecs->count > 0) (reset.c:33) — an empty/absent pathspec
        // errors ("invalid argument: 'pathspecs && pathspecs->count > 0'",
        // GIT_ERROR_INVALID, release build), never proceeding. An empty array
        // stays an error instead of becoming null PathSpecs that would reset
        // the WHOLE index to the target tree.
        if (paths.Length == 0)
        {
            throw new GitException(
                GitErrorCode.Error,
                "invalid argument: 'pathspecs && pathspecs->count > 0'",
                GitErrorCategory.Invalid);
        }

        GitTree? tree = null;
        if (target is not null)
        {
            tree = await Objects.LookupAsync<GitTree>(target.Tree, cancellationToken).ConfigureAwait(false);
        }

        // Generate a reversed diff (index → tree) for the pathspec. C (reset.c:55-58): opts.flags = GIT_DIFF_REVERSE — the sides are swapped so that DELETED
        // means "in index, not in tree" (remove) and ADDED means "in tree, not in index" (add from tree). C does NOT set INCLUDE_UNMODIFIED.
        var diffOpts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.Reverse,
            PathSpecs = paths.Length > 0 ? paths : null,
        };

        DiffGenerator diff = await DiffGenerator.TreeToIndexAsync(this, tree, diffOpts, cancellationToken).ConfigureAwait(false);

        GitIndex index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);

        // Apply the diff to the index: for each delta, update or remove
        // the index entry.
        foreach (GitDiffDelta delta in diff.Deltas)
        {
            // Remove conflict entries first.
            if (index.HasConflicts)
            {
                index.ConflictRemove(delta.Path);
            }

            switch (delta.Status)
            {
                case GitDeltaStatus.Deleted:
                    // The file was in the tree but not in the index →
                    // actually this means the tree doesn't have it,
                    // so remove it from the index.
                    index.Remove(delta.Path, 0);
                    break;

                case GitDeltaStatus.Added:
                case GitDeltaStatus.Modified:
                case GitDeltaStatus.Conflicted:
                    // The file is in the tree → add/update the index entry
                    // from the tree's content.
                    if (tree is not null)
                    {
                        GitTreeEntry? entry = await tree.EntryByPathAsync(delta.Path, cancellationToken).ConfigureAwait(false);
                        if (entry is not null)
                        {
                            var indexEntry = new GitIndexEntry(delta.Path, entry.Value.Id, entry.Value.Mode);
                            index.Add(indexEntry);
                        }
                    }
                    else
                    {
                        // No target → remove from index.
                        index.Remove(delta.Path, 0);
                    }
                    break;

                case GitDeltaStatus.Unmodified:
                    // Nothing to do.
                    break;
            }
        }

        await index.WriteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates HEAD to point at the target commit OID. If HEAD is symbolic
    /// (pointing at a branch), updates the branch ref. If HEAD is detached,
    /// updates HEAD directly. Matches <c>git_reference__update_terminal</c>
    /// with <c>GIT_HEAD_FILE</c> (reset.c:140-160).
    /// </summary>
    private async Task UpdateHeadForResetAsync(GitOid targetOid, string logMessage, CancellationToken cancellationToken)
    {
        GitReference? head = await Refs.LookupAsync("HEAD", cancellationToken).ConfigureAwait(false);

        if (head is GitSymbolicReference symRef)
        {
            // HEAD points at a branch → update the branch to the target OID.
            // The branch ref is the symbolic target, not HEAD itself.
            GitReference branchRef = await Refs.LookupAsync(symRef.TargetNameKey, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    $"reference '{symRef.TargetName}' not found",
                    GitErrorCategory.Reference);
            await Refs.SetTargetAsync(branchRef, targetOid, logMessage, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // C's
            // reset() writes HEAD via git_reference__update_terminal
            // (reset.c:171 → refs.c:1116-1150), which for a direct HEAD
            // writes the 'reset: moving to %s' message VERBATIM (reset.c:146-148).
            // Write HEAD
            // directly with the reset message (the ref-backend validates
            // the target commit exists).
            await Refs.CreateAsync("HEAD", targetOid, force: true, logMessage, cancellationToken).ConfigureAwait(false);
        }
    }
}
