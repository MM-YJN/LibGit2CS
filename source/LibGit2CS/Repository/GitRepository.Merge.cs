// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;

using LibGit2CS.Checkout;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Revwalk;

using Xdiff;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.Repository;

/// <content> Merge operations. Managed port of libgit2's <c>src/libgit2/merge.c</c>, <c>merge_file.c</c>, <c>cherrypick.c</c>, and <c>revert.c</c> public entry
/// points. </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    private const string CherryPickHeadFile = "CHERRY_PICK_HEAD";
    private const string RevertHeadFile = "REVERT_HEAD";

    // ── Merge option normalization ────────────────────────────────────────

    /// <summary>
    /// Normalizes merge options, filling in defaults from config. Matches
    /// <c>merge_normalize_opts</c> (<c>merge.c:1875-1941</c>). Reads
    /// <c>merge.default</c> (default driver) and <c>merge.renamelimit</c> /
    /// <c>diff.renamelimit</c> (target limit). Does NOT read custom merge
    /// driver config (<c>merge.&lt;name&gt;.driver</c>).
    /// </summary>
    /// <param name="given">Caller-supplied options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized options with all fields resolved.</returns>
    internal async ValueTask<GitMergeOptions> NormalizeMergeOptionsAsync(GitMergeOptions? given, CancellationToken cancellationToken)
    {
        GitMergeOptions opts = given ?? GitMergeOptions.Default;

        int renameThreshold = opts.RenameThreshold;
        if ((opts.Flags & GitMergeFlags.FindRenames) != 0 && renameThreshold == 0)
        {
            renameThreshold = 50; // GIT_MERGE_DEFAULT_RENAME_THRESHOLD
        }

        string? defaultDriver = opts.DefaultDriver;
        if (defaultDriver is null)
        {
            GitConfigEntry? entry = await Config.GetEntryAsync("merge.default", cancellationToken).ConfigureAwait(false);
            if (entry is { } ce)
            {
                defaultDriver = ce.Value;
            }
        }

        int targetLimit = opts.TargetLimit;
        if (targetLimit == 0)
        {
            int limit = await Config.GetIntAsync("merge.renamelimit", 0, cancellationToken).ConfigureAwait(false);
            if (limit == 0)
            {
                limit = await Config.GetIntAsync("diff.renamelimit", 0, cancellationToken).ConfigureAwait(false);
            }

            targetLimit = limit <= 0 ? 200 : limit; // GIT_MERGE_DEFAULT_TARGET_LIMIT
        }

        return opts with
        {
            RenameThreshold = renameThreshold,
            DefaultDriver = defaultDriver,
            TargetLimit = targetLimit,
        };
    }

    // ── Public: 3-way tree/index merge (git_merge__iterators family) ─────

    /// <summary>
    /// Merges three iterators into a new index. Matches
    /// <c>git_merge__iterators</c> (merge.c:2096-2186).
    /// </summary>
    /// <param name="ancestor">Ancestor (merge base) iterator, or null for empty.</param>
    /// <param name="ours">Our side iterator.</param>
    /// <param name="theirs">Their side iterator.</param>
    /// <param name="opts">Merge options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new in-memory index containing the merge result.</returns>
    private async Task<GitIndex> MergeIteratorsAsync(
        IIterator? ancestor,
        IIterator ours,
        IIterator theirs,
        GitMergeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(theirs);

        GitMergeOptions options = await NormalizeMergeOptionsAsync(opts, cancellationToken).ConfigureAwait(false);

        IIterator ancestorIter = ancestor ?? new EmptyIterator();
        try
        {
            var diffList = new MergeDiffList();

            // Walk three iterators in lock-step, classifying each entry.
            await MergeFindDifferencesAsync(diffList, ancestorIter, ours, theirs, cancellationToken).ConfigureAwait(false);

            // Find renames (exact OID + inexact similarity via SimilarityHash).
            if ((options.Flags & GitMergeFlags.FindRenames) != 0)
            {
                await MergeFindRenamesAsync(diffList, options, cancellationToken).ConfigureAwait(false);
            }

            // Attempt to resolve each conflict.
            var conflicts = diffList.Conflicts.ToList();
            diffList.Conflicts.Clear();

            foreach (MergeDiff? conflict in conflicts)
            {
                bool resolved = false;

                resolved = MergeResolveTrivial(diffList, conflict);
                if (!resolved)
                {
                    resolved = MergeResolveOneRemoved(diffList, conflict);
                }

                if (!resolved)
                {
                    resolved = MergeResolveOneRenamed(diffList, conflict);
                }

                if (!resolved)
                {
                    resolved = await MergeResolveContentsAsync(diffList, conflict, options, cancellationToken).ConfigureAwait(false);
                }

                if (!resolved)
                {
                    if ((options.Flags & GitMergeFlags.FailOnConflict) != 0)
                    {
                        throw new GitException(
                            GitErrorCode.MergeConflict,
                            "merge conflicts exist",
                            GitErrorCategory.Merge);
                    }

                    diffList.Conflicts.Add(conflict);
                }
            }

            // Build the output index from the diff list.
            return MergeIndexFromDiffList(
                diffList,
                ObjectFormat,
                (options.Flags & GitMergeFlags.SkipReuc) != 0);
        }
        finally
        {
            if (ancestor is null)
            {
                ancestorIter.Dispose();
            }
        }
    }

    /// <summary>
    /// Merges three trees into a new index. Matches <c>git_merge_trees</c>
    /// (merge.c:2188-2240).
    /// </summary>
    public async Task<GitIndex> MergeTreesAsync(
        GitTree? ancestor,
        GitTree? ours,
        GitTree? theirs,
        GitMergeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        var iterOpts = new IteratorOptions
        {
            Flags = IteratorFlags.DontIgnoreCase,
        };

        using IIterator ancestorIter = TreeIterator.ForTree(ancestor, this, iterOpts);
        using IIterator ourIter = TreeIterator.ForTree(ours, this, iterOpts);
        using IIterator theirIter = TreeIterator.ForTree(theirs, this, iterOpts);

        return await MergeIteratorsAsync(ancestorIter, ourIter, theirIter, opts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges an index against a tree, using an ancestor tree as the merge
    /// base. Matches <c>merge_index_and_tree</c> (stash.c:894-919).
    /// </summary>
    /// <param name="ancestorTree">The ancestor (merge base) tree, or null.</param>
    /// <param name="oursIndex">Our index.</param>
    /// <param name="theirsTree">Their tree.</param>
    /// <param name="opts">Merge options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new index with the merge result.</returns>
    public async Task<GitIndex> MergeIndexAndTreeAsync(
        GitTree? ancestorTree,
        GitIndex oursIndex,
        GitTree theirsTree,
        GitMergeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oursIndex);
        ArgumentNullException.ThrowIfNull(theirsTree);

        var iterOpts = new IteratorOptions
        {
            Flags = IteratorFlags.DontIgnoreCase,
        };

        using IIterator ancestorIter = TreeIterator.ForTree(ancestorTree, this, iterOpts);
        using IIterator ourIter = IndexIterator.ForIndex(oursIndex, this, iterOpts);
        using IIterator theirIter = TreeIterator.ForTree(theirsTree, this, iterOpts);

        return await MergeIteratorsAsync(ancestorIter, ourIter, theirIter, opts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges two indexes using an ancestor tree as the merge base. Matches
    /// <c>merge_indexes</c> (stash.c:867-892).
    /// </summary>
    /// <param name="ancestorTree">The ancestor (merge base) tree, or null.</param>
    /// <param name="oursIndex">Our index.</param>
    /// <param name="theirsIndex">Their index.</param>
    /// <param name="opts">Merge options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new index with the merge result.</returns>
    public async Task<GitIndex> MergeIndexesAsync(
        GitTree? ancestorTree,
        GitIndex oursIndex,
        GitIndex theirsIndex,
        GitMergeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oursIndex);
        ArgumentNullException.ThrowIfNull(theirsIndex);

        var iterOpts = new IteratorOptions
        {
            Flags = IteratorFlags.DontIgnoreCase,
        };

        using IIterator ancestorIter = TreeIterator.ForTree(ancestorTree, this, iterOpts);
        using IIterator ourIter = IndexIterator.ForIndex(oursIndex, this, iterOpts);
        using IIterator theirIter = IndexIterator.ForIndex(theirsIndex, this, iterOpts);

        return await MergeIteratorsAsync(ancestorIter, ourIter, theirIter, opts, cancellationToken).ConfigureAwait(false);
    }

    // ── Public: merge base ───────────────────────────────────────────────

    /// <summary>
    /// Finds a single merge base of two commits. Returns the best (most
    /// recent) merge base, or <c>null</c> if none exists. Matches
    /// <c>git_merge_base</c> (<c>merge.c:265-279</c>).
    /// </summary>
    public async Task<GitOid?> MergeBaseFindAsync(GitOid one, GitOid two, CancellationToken cancellationToken = default)
    {
        List<GitOid>? bases = await MergeBaseBasesManyAsync(one, [two], cancellationToken).ConfigureAwait(false);
        if (bases is null || bases.Count == 0)
        {
            return null;
        }

        return bases[0];
    }

    /// <summary>
    /// Finds all merge bases of two commits, sorted by date (newest first).
    /// Returns an empty list if none exist. Matches <c>git_merge_bases</c>
    /// (<c>merge.c:281-313</c>).
    /// </summary>
    public async Task<IReadOnlyList<GitOid>> MergeBaseFindAllAsync(GitOid one, GitOid two, CancellationToken cancellationToken = default)
    {
        List<GitOid>? bases = await MergeBaseBasesManyAsync(one, [two], cancellationToken).ConfigureAwait(false);
        return bases ?? [];
    }

    /// <summary>
    /// Finds a single merge base among N commits. Matches
    /// <c>git_merge_base_many</c> (<c>merge.c:135-154</c>).
    /// </summary>
    /// <param name="commits">At least 2 commit OIDs.</param>
    /// <returns>The best merge base, or <c>null</c> if none exists.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitOid?> MergeBaseFindManyAsync(IReadOnlyList<GitOid> commits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commits);

        if (commits.Count < 2)
        {
            // C: merge.c:91-94/203-206 — "at least two commits are required
            // to find an ancestor" with class GIT_ERROR_INVALID and a bare
            // -1 (GIT_ERROR) return.
            throw new GitException(
                GitErrorCode.Error,
                "at least two commits are required to find an ancestor",
                GitErrorCategory.Invalid);
        }

        GitOid first = commits[0];
        var rest = new List<GitOid>(commits.Count - 1);
        for (int i = 1; i < commits.Count; i++)
        {
            rest.Add(commits[i]);
        }

        List<GitOid>? bases = await MergeBaseBasesManyAsync(first, rest, cancellationToken).ConfigureAwait(false);
        if (bases is null || bases.Count == 0)
        {
            return null;
        }

        return bases[0];
    }

    /// <summary>
    /// Finds all merge bases among N commits. Matches
    /// <c>git_merge_bases_many</c> (<c>merge.c:156-191</c>).
    /// </summary>
    public async Task<IReadOnlyList<GitOid>> MergeBaseFindAllManyAsync(IReadOnlyList<GitOid> commits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commits);

        if (commits.Count < 2)
        {
            // C: merge.c:91-94/203-206 — "at least two commits are required
            // to find an ancestor" with class GIT_ERROR_INVALID and a bare
            // -1 (GIT_ERROR) return.
            throw new GitException(
                GitErrorCode.Error,
                "at least two commits are required to find an ancestor",
                GitErrorCategory.Invalid);
        }

        GitOid first = commits[0];
        var rest = new List<GitOid>(commits.Count - 1);
        for (int i = 1; i < commits.Count; i++)
        {
            rest.Add(commits[i]);
        }

        List<GitOid>? bases = await MergeBaseBasesManyAsync(first, rest, cancellationToken).ConfigureAwait(false);
        return bases ?? [];
    }

    /// <summary>
    /// Finds the common ancestor of ALL input commits by iteratively
    /// computing pairwise merge bases. Matches <c>git_merge_base_octopus</c>
    /// (<c>merge.c:193-218</c>).
    /// </summary>
    /// <param name="commits">At least 2 commit OIDs.</param>
    /// <returns>The octopus merge base, or <c>null</c> if any pair has no
    /// common ancestor.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitOid?> MergeBaseOctopusAsync(IReadOnlyList<GitOid> commits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commits);

        if (commits.Count < 2)
        {
            // C: merge.c:91-94/203-206 — "at least two commits are required
            // to find an ancestor" with class GIT_ERROR_INVALID and a bare
            // -1 (GIT_ERROR) return.
            throw new GitException(
                GitErrorCode.Error,
                "at least two commits are required to find an ancestor",
                GitErrorCategory.Invalid);
        }

        GitOid result = commits[0];
        for (int i = 1; i < commits.Count; i++)
        {
            GitOid? mb = await MergeBaseFindAsync(result, commits[i], cancellationToken).ConfigureAwait(false);
            if (mb is null)
            {
                return null;
            }

            result = mb.Value;
        }

        return result;
    }

    // ── Public: merge analysis ───────────────────────────────────────────

    /// <summary>
    /// Analyzes a merge of <paramref name="theirHeads"/> into the current
    /// HEAD. Matches <c>git_merge_analysis</c> (<c>merge.c:3306-3326</c>).
    /// </summary>
    /// <param name="theirHeads">The annotated commits to merge in. Must
    /// contain exactly one entry.</param>
    /// <returns>The merge analysis result.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitMergeAnalysisResult> MergeAnalyzeAsync(IReadOnlyList<GitAnnotatedCommit> theirHeads, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(theirHeads);

        GitReference headRef = await Refs.LookupAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "failed to lookup HEAD reference",
                GitErrorCategory.Merge); // C (merge.c:3316-3319): GIT_ERROR_MERGE class

        return await MergeAnalyzeForRefAsync(headRef, theirHeads, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Analyzes a merge of <paramref name="theirHeads"/> into the given
    /// <paramref name="ourRef"/>. Matches <c>git_merge_analysis_for_ref</c>
    /// (<c>merge.c:3246-3304</c>).
    /// </summary>
    /// <param name="ourRef">The reference to merge into (typically HEAD).</param>
    /// <param name="theirHeads">The annotated commits to merge in. Must
    /// contain exactly one entry.</param>
    /// <returns>The merge analysis result.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitMergeAnalysisResult> MergeAnalyzeForRefAsync(
        GitReference ourRef,
        IReadOnlyList<GitAnnotatedCommit> theirHeads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ourRef);
        ArgumentNullException.ThrowIfNull(theirHeads);

        if (theirHeads.Count != 1)
        {
            // C (merge.c:3263-3267): git_error_set(GIT_ERROR_MERGE,
            // "can only merge a single branch"); error = -1 (GIT_ERROR).
            throw new GitException(
                GitErrorCode.Error,
                "can only merge a single branch",
                GitErrorCategory.Merge);
        }

        GitMergePreference preference = await MergeAnalyzeReadPreferenceAsync(cancellationToken).ConfigureAwait(false);

        // Check if HEAD is unborn (symbolic HEAD pointing at a branch that
        // doesn't exist yet). Matches git_reference__is_unborn_head.
        if (await MergeAnalyzeIsUnbornHeadAsync(ourRef, cancellationToken).ConfigureAwait(false))
        {
            return new GitMergeAnalysisResult(
                GitMergeAnalysis.FastForward | GitMergeAnalysis.Unborn,
                preference);
        }

        // Resolve ancestor and our head.
        (GitAnnotatedCommit? ancestor, GitAnnotatedCommit ourHead) = await MergeAnalyzeResolveAncestorAsync(ourRef, theirHeads, cancellationToken).ConfigureAwait(false);

        try
        {
            // If ancestor was found, compare OIDs.
            if (ancestor is not null)
            {
                GitOid ancestorId = ancestor.Id;
                GitOid theirId = theirHeads[0].Id;
                GitOid ourId = ourHead.Id;

                // Up-to-date: ancestor == their → they are already in our history.
                if (ancestorId.Equals(theirId))
                {
                    return new GitMergeAnalysisResult(GitMergeAnalysis.UpToDate, preference);
                }

                // Fast-forward: ancestor == our → we are ancestor, they are ahead.
                if (ancestorId.Equals(ourId))
                {
                    return new GitMergeAnalysisResult(
                        GitMergeAnalysis.FastForward | GitMergeAnalysis.Normal,
                        preference);
                }
            }

            // Otherwise: normal merge.
            return new GitMergeAnalysisResult(GitMergeAnalysis.Normal, preference);
        }
        finally
        {
            ancestor?.Dispose();
            ourHead.Dispose();
        }
    }

    // ── Public: merge commits ────────────────────────────────────────────

    /// <summary>
    /// Merges two commits, producing a merged index. Matches
    /// <c>git_merge_commits</c> (merge.c:2456-2477). A thin wrapper around
    /// <see cref="MergeAnnotatedCommitsAsync"/> that converts commits to annotated
    /// commits and uses recursion level 0 (top-level).
    /// </summary>
    /// <param name="ourCommit">Our commit (HEAD side).</param>
    /// <param name="theirCommit">Their commit (being merged in).</param>
    /// <param name="opts">Merge options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new in-memory index containing the merge result.</returns>
    public async Task<GitIndex> MergeCommitsAsync(
        Commit ourCommit,
        Commit theirCommit,
        GitMergeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ourCommit);
        ArgumentNullException.ThrowIfNull(theirCommit);

        using var ours = GitAnnotatedCommit.FromCommit(ourCommit);
        using var theirs = GitAnnotatedCommit.FromCommit(theirCommit);
        return await MergeAnnotatedCommitsAsync(ours, theirs, 0, opts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges two annotated commits, producing a merged index. Matches
    /// <c>merge_annotated_commits</c> (merge.c:2413-2453). Computes the merge
    /// base (possibly recursive), creates iterators for the base/ours/theirs,
    /// and calls the internal iterator merge.
    /// </summary>
    /// <param name="ours">Our annotated commit.</param>
    /// <param name="theirs">Their annotated commit.</param>
    /// <param name="recursionLevel">Current recursion depth (0 = top-level).</param>
    /// <param name="opts">Merge options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new in-memory index containing the merge result.</returns>
    public async Task<GitIndex> MergeAnnotatedCommitsAsync(
        GitAnnotatedCommit ours,
        GitAnnotatedCommit theirs,
        int recursionLevel,
        GitMergeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(theirs);

        // Compute the merge base (possibly recursive). If the merge base
        // is not found (no common ancestor), that's OK — base remains null.
        GitAnnotatedCommit? baseCommit = null;
        try
        {
            try
            {
                baseCommit = await MergeComputeBaseAsync(ours, theirs, opts, recursionLevel, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
            {
                // No common ancestor — proceed with null base (empty ancestor).
                // Matches merge.c:2429-2432.
            }

            // Create iterators for base/ours/theirs. Matches
            // iterator_for_annotated_commit (merge.c:2388-2411).
            var iterOpts = new IteratorOptions
            {
                Flags = IteratorFlags.DontIgnoreCase,
            };

            using IIterator baseIter = await MergeIteratorForAnnotatedCommitAsync(baseCommit, iterOpts, cancellationToken).ConfigureAwait(false);
            using IIterator ourIter = await MergeIteratorForAnnotatedCommitAsync(ours, iterOpts, cancellationToken).ConfigureAwait(false);
            using IIterator theirIter = await MergeIteratorForAnnotatedCommitAsync(theirs, iterOpts, cancellationToken).ConfigureAwait(false);

            // Perform the three-way merge. Matches merge.c:2438-2439.
            return await MergeIteratorsAsync(baseIter, ourIter, theirIter, opts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            baseCommit?.Dispose();
        }
    }

    /// <summary>
    /// Computes the merge base of two annotated commits, recursively merging
    /// multiple bases if needed (criss-cross merge). Matches
    /// <c>compute_base</c> (merge.c:2315-2386).
    /// </summary>
    /// <param name="one">First annotated commit (may be virtual).</param>
    /// <param name="two">Second annotated commit (always a real commit at top level).</param>
    /// <param name="opts">Merge options (for recursion_limit and NoRecursive flag).</param>
    /// <param name="recursionLevel">Current recursion depth.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The merge base as an annotated commit, or null if no common ancestor.</returns>
    public async Task<GitAnnotatedCommit?> MergeComputeBaseAsync(
        GitAnnotatedCommit one,
        GitAnnotatedCommit two,
        GitMergeOptions? opts,
        int recursionLevel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(two);

        GitMergeOptions options = await NormalizeMergeOptionsAsync(opts, cancellationToken).ConfigureAwait(false);

        // Collect ancestor OIDs for merge base computation. The order matters:
        // "two" is added first (always a single real commit at top level), then
        // "one" (which may be virtual with multiple ancestors). This matches
        // merge.c:2341-2342 (insert_head_ids(two) then insert_head_ids(one)).
        var headIds = new List<GitOid>();
        headIds.AddRange(MergeInsertHeadIds(two));
        headIds.AddRange(MergeInsertHeadIds(one));

        // Find all merge bases. Matches merge.c:2343-2345.
        IReadOnlyList<GitOid> basesResult = await MergeBaseFindAllManyAsync(headIds, cancellationToken).ConfigureAwait(false);
        if (basesResult.Count == 0)
        {
            return null;
        }

        // With GIT_MERGE_NO_RECURSIVE, only use the first base (like git-merge-resolve).
        // Matches merge.c:2347.
        int baseCount = (options.Flags & GitMergeFlags.NoRecursive) != 0
            ? 0 : basesResult.Count;

        // Reverse the bases so they go from oldest to newest.
        // Matches merge.c:2349-2350.
        List<GitOid> bases;
        if (baseCount > 0)
        {
            bases = [.. basesResult];
            bases.Reverse(); // List<T>.Reverse() — reverses in-place
        }
        else
        {
            bases = [.. basesResult];
        }

        // Look up the first base. Matches merge.c:2352.
        GitAnnotatedCommit @base = await GitAnnotatedCommit.LookupAsync(this, bases[0], cancellationToken).ConfigureAwait(false);

        // Recursively merge additional bases to produce a single virtual ancestor.
        // Matches merge.c:2355-2373.
        for (int i = 1; i < baseCount; i++)
        {
            recursionLevel++;

            // Check recursion limit. 0 = unlimited. Matches merge.c:2358-2359.
            if (options.RecursionLimit > 0 && recursionLevel > options.RecursionLimit)
            {
                break;
            }

            GitAnnotatedCommit other = await GitAnnotatedCommit.LookupAsync(this, bases[i], cancellationToken).ConfigureAwait(false);
            try
            {
                GitAnnotatedCommit newBase = await MergeCreateVirtualBaseAsync(@base, other, options, recursionLevel, cancellationToken).ConfigureAwait(false);
                @base.Dispose();
                @base = newBase;
            }
            finally
            {
                other.Dispose();
            }
        }

        return @base;
    }

    /// <summary>
    /// Merges two merge-base candidates to produce a virtual ancestor.
    /// Matches <c>create_virtual_base</c> (merge.c:2275-2313). Clears
    /// <see cref="GitMergeFlags.FailOnConflict"/>, sets
    /// <see cref="GitMergeFlags.VirtualBase"/>, and recursively calls
    /// <see cref="MergeAnnotatedCommitsAsync"/>. The merged index becomes the
    /// virtual commit's "tree".
    /// </summary>
    public async Task<GitAnnotatedCommit> MergeCreateVirtualBaseAsync(
        GitAnnotatedCommit one,
        GitAnnotatedCommit two,
        GitMergeOptions opts,
        int recursionLevel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(two);
        ArgumentNullException.ThrowIfNull(opts);

        // Clear FailOnConflict and set VirtualBase. Matches merge.c:2293-2294.
        GitMergeOptions virtualOpts = opts with
        {
            Flags = (opts.Flags & ~GitMergeFlags.FailOnConflict) | GitMergeFlags.VirtualBase,
        };

        // Recursively merge. Matches merge.c:2296-2298.
        GitIndex index = await MergeAnnotatedCommitsAsync(one, two, recursionLevel + 1, virtualOpts, cancellationToken).ConfigureAwait(false);

        // Collect parent OIDs from both sides. Matches merge.c:2305-2306.
        var parents = new List<GitOid>();
        parents.AddRange(MergeInsertHeadIds(one));
        parents.AddRange(MergeInsertHeadIds(two));

        return GitAnnotatedCommit.CreateVirtual(index, parents);
    }

    // ── Public: full merge ───────────────────────────────────────────────

    /// <summary> Performs a full merge: writes state files, computes the merge, checks the result, appends conflict info to <c>MERGE_MSG</c>, checks out the
    /// result, and writes the index. Matches <c>git_merge</c> (merge.c:3328-3401). This is the top-level orchestrator entry point. Named <c>MergeAsync</c> (not
    /// <c>MergeRunAsync</c>) because the domain name is itself a verb, so the redundant <c>Run</c> verb is dropped. </summary>
    /// <param name="theirHeads">The annotated commits being merged in. Only one head is supported (octopus is not implemented).</param> <param
    /// name="mergeOpts">Merge options, or null for defaults.</param> <param name="checkoutOpts">Checkout options, or null for defaults.</param> <param
    /// name="cancellationToken">Cancellation token.</param>
    public async Task MergeAsync(
        IReadOnlyList<GitAnnotatedCommit> theirHeads,
        GitMergeOptions? mergeOpts = null,
        GitCheckoutOptions? checkoutOpts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(theirHeads);
        if (theirHeads.Count == 0)
        {
            throw new ArgumentException("at least one head is required", nameof(theirHeads));
        }

        // Only one head supported. Matches merge.c:3346-3349.
        if (theirHeads.Count != 1)
        {
            throw new GitException(
                GitErrorCode.Error,
                "can only merge a single branch",
                GitErrorCategory.Merge);
        }

        // Bare repo check. Matches merge.c:3351
        // (git_repository__ensure_not_bare, repository.h:216-229).
        if (IsBare)
        {
            throw new GitException(
                GitErrorCode.BareRepo,
                "cannot merge. This operation is not allowed against bare repositories.",
                GitErrorCategory.Repository);
        }

        // Default checkout strategy: SAFE (0). Matches merge.c:3354-3355
        // (checkout_strategy = given_checkout_opts ? ... : 0) followed by
        // git_indexwriter_init_for_operation (index.c:3885-3893) which ORs in
        // DONT_WRITE_INDEX — the effective strategy is SAFE|DONT_WRITE_INDEX,
        // NOT AllowConflicts (the DONT_WRITE_INDEX bit is added below).
        GitCheckoutStrategy strategy = checkoutOpts?.Strategy ?? GitCheckoutStrategy.Safe;

        // C (merge.c:3357-3361): git_indexwriter_init_for_operation locks the
        // index BEFORE the merge state files are written — a concurrent index
        // lock fails the merge early with GIT_ELOCKED and no
        // MERGE_HEAD is created. should_write is false when the caller passed
        // GIT_CHECKOUT_DONT_WRITE_INDEX (index.c:3891). The index is then
        // re-read from disk (git_index_read, merge.c:3359-3360).
        GitIndex repoIndex = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var indexWriter = IndexWriter.Init(repoIndex);
        bool shouldWrite = (strategy & GitCheckoutStrategy.DontWriteIndex) == 0;
        await repoIndex.ReadFromDiskAsync(force: false, cancellationToken).ConfigureAwait(false);

        GitIndex? index = null;
        GitAnnotatedCommit? baseCommit = null;
        try
        {
            // Write merge state files. Matches merge.c:3365-3369.
            using GitAnnotatedCommit ourHead = await GitAnnotatedCommit.FromHeadAsync(this, cancellationToken).ConfigureAwait(false);
            await MergeState.SetupAsync(this, ourHead, theirHeads, cancellationToken).ConfigureAwait(false);

            // Perform the merge. Matches merge.c:3373-3374.
            baseCommit = await MergeComputeBaseAsync(ourHead, theirHeads[0], mergeOpts, 0, cancellationToken).ConfigureAwait(false);

            var iterOpts = new IteratorOptions
            {
                Flags = IteratorFlags.DontIgnoreCase,
            };

            using IIterator baseIter = await MergeIteratorForAnnotatedCommitAsync(baseCommit, iterOpts, cancellationToken).ConfigureAwait(false);
            using IIterator ourIter = await MergeIteratorForAnnotatedCommitAsync(ourHead, iterOpts, cancellationToken).ConfigureAwait(false);
            using IIterator theirIter = await MergeIteratorForAnnotatedCommitAsync(theirHeads[0], iterOpts, cancellationToken).ConfigureAwait(false);

            index = await MergeIteratorsAsync(baseIter, ourIter, theirIter, mergeOpts, cancellationToken).ConfigureAwait(false);

            // Post-merge safety check. Matches merge.c:3375.
            await MergeCheckResultAsync(index, cancellationToken).ConfigureAwait(false);

            // Append conflict paths to MERGE_MSG. Matches merge.c:3376.
            await MergeState.AppendConflictsToMergeMsgAsync(this, index, cancellationToken).ConfigureAwait(false);

            // Checkout the merge result. Matches merge.c:3381-3385. The
            // merged index is NOT written to disk before checkout: like C,
            // the repo index is only committed after checkout succeeds
            // (git_indexwriter_commit, merge.c:3387), so a failed checkout
            // leaves the on-disk index untouched.
            GitCheckoutOptions normalizedCheckout = MergeNormalizeCheckoutOpts(
                strategy,
                baseCommit,
                ourHead,
                theirHeads,
                checkoutOpts);

            // Use DontWriteIndex so checkout doesn't write the repo index
            // itself (the commit happens below, after a successful checkout).
            normalizedCheckout = normalizedCheckout with
            {
                Strategy = normalizedCheckout.Strategy | GitCheckoutStrategy.DontWriteIndex,
            };

            await CheckoutIndexAsync(index, normalizedCheckout, cancellationToken).ConfigureAwait(false);

            // Commit the repo index to disk. Matches git_indexwriter_commit
            // (merge.c:3387): the checkout populated the repo's in-memory
            // index (stage-0 updates, removes, conflict stages 1/2/3, REUC/
            // NAME extensions); the pre-opened writer commits it now that
            // checkout succeeded — unless the caller passed DONT_WRITE_INDEX
            // (should_write=false, index.c:3891).
            if (shouldWrite)
            {
                await indexWriter.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // On error, clean up the MERGE state files only (C merge.c:3165-3174,
            // git_merge__cleanup) - never another operation's state.
            StateCleanup(["MERGE_HEAD", "MERGE_MODE", "MERGE_MSG"], []);
            throw;
        }
        finally
        {
            index?.Dispose();
            baseCommit?.Dispose();
            indexWriter.Dispose();
        }
    }

    // ── Public: post-merge validation ─────────────────────────────────────

    /// <summary>
    /// Validates that the index and workdir are clean enough to receive the
    /// merge result. Matches <c>git_merge__check_result</c> (merge.c:3064-3121).
    /// Throws <see cref="GitException"/> with <see cref="GitErrorCode.Conflict"/>
    /// if staged or workdir changes would be overwritten.
    /// </summary>
    public async Task MergeCheckResultAsync(GitIndex indexNew, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(indexNew);

        // Build a diff of HEAD tree vs merge result index to collect all
        // changed paths. Matches merge.c:3079-3083.
        GitTree? headTree = await MergeResolveHeadTreeAsync(cancellationToken).ConfigureAwait(false);
        if (headTree is null)
        {
            // C (merge.c:3079-3083): git_repository_head_tree fails on an
            // unborn HEAD (GIT_EUNBORNBRANCH) and git_merge__check_result
            // propagates it.
            throw new GitException(
                GitErrorCode.UnbornBranch,
                "reference 'HEAD' not found",
                GitErrorCategory.Reference);
        }

        var iterOpts = new IteratorOptions
        {
            Flags = IteratorFlags.DontIgnoreCase,
        };

        using IIterator headIter = TreeIterator.ForTree(headTree, this, iterOpts);
        using IIterator newIter = IndexIterator.ForIndex(indexNew, this, iterOpts);

        DiffGenerator mergedDiff = await DiffGenerator.GenerateAsync(this, headIter, newIter, new GitDiffOptions(), cancellationToken).ConfigureAwait(false);

        // Collect all changed paths from the diff. Matches merge.c:3085-3088.
        var paths = new List<GitPath>();
        foreach (GitDiffDelta delta in mergedDiff.Deltas)
        {
            paths.Add(delta.DeltaPath ?? default);
        }

        // Add conflict paths from the merge index (deduplicated).
        // Matches merge.c:3090-3100.
        foreach (GitIndexEntry entry in indexNew.Entries)
        {
            if (!entry.IsConflict)
            {
                continue;
            }

            if (paths.Count == 0 || paths[^1] != entry.Path)
            {
                paths.Add(entry.Path);
            }
        }

        // Run safety checks. Matches merge.c:3103-3105.
        int indexConflicts = await MergeCheckIndexAsync(indexNew, paths, cancellationToken).ConfigureAwait(false);
        int wdConflicts = await MergeCheckWorkdirAsync(indexNew, paths, cancellationToken).ConfigureAwait(false);

        // Throw if any conflicts. Matches merge.c:3107-3111.
        int conflicts = indexConflicts + wdConflicts;
        if (conflicts > 0)
        {
            string suffix = conflicts != 1 ? "s" : "";
            throw new GitException(
                GitErrorCode.Conflict,
                $"{conflicts} uncommitted change{suffix} would be overwritten by merge",
                GitErrorCategory.Merge);
        }
    }

    // ── Public: cherry-pick ───────────────────────────────────────────────

    /// <summary>
    /// Cherry-picks a commit onto another commit, producing a merged index
    /// without touching the working directory. Matches
    /// <c>git_cherrypick_commit</c> (cherrypick.c:116-165).
    /// </summary>
    /// <param name="cherrypickCommit">The commit being cherry-picked.</param>
    /// <param name="ourCommit">Our commit (HEAD side, the target).</param>
    /// <param name="mainline">Parent number (1-indexed) for merge commits.
    /// Must be 0 for non-merge commits.</param>
    /// <param name="mergeOpts">Merge options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The merged index (may have conflicts).</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> with <see cref="GitErrorCategory.CherryPick"/>
    /// if <paramref name="mainline"/> is inconsistent with the commit's
    /// parent count.
    /// </exception>
    public async Task<GitIndex> CherryPickCommitAsync(
        Commit cherrypickCommit,
        Commit ourCommit,
        uint mainline,
        GitMergeOptions? mergeOpts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cherrypickCommit);
        ArgumentNullException.ThrowIfNull(ourCommit);

        // Resolve the parent to diff against. Matches cherrypick.c:133-145.
        int parentIndex = MergeResolveParentIndex(cherrypickCommit, mainline, GitErrorCategory.CherryPick);

        // Get the parent tree.
        GitTree? parentTree = null;
        if (parentIndex >= 0)
        {
            GitOid parentId = cherrypickCommit.ParentId(parentIndex);
            Commit parentCommit = await Objects.LookupAsync<Commit>(parentId, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    $"parent {parentId} of commit {cherrypickCommit.Id} not found",
                    GitErrorCategory.Object);
            parentTree = await Objects.LookupAsync<GitTree>(parentCommit.Tree, cancellationToken).ConfigureAwait(false);
        }

        // Get our tree and the cherry-pick tree.
        GitTree ourTree = await Objects.LookupAsync<GitTree>(ourCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"tree {ourCommit.Tree} not found",
                GitErrorCategory.Object);
        GitTree cherrypickTree = await Objects.LookupAsync<GitTree>(cherrypickCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"tree {cherrypickCommit.Tree} not found",
                GitErrorCategory.Object);

        // 3-way merge: ancestor=parentTree, ours=ourTree, theirs=cherrypickTree.
        // Matches cherrypick.c:156.
        return await MergeTreesAsync(parentTree, ourTree, cherrypickTree, mergeOpts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cherry-picks a commit onto the current HEAD, updating the working
    /// directory and index. Matches <c>git_cherrypick</c> (cherrypick.c:167-225).
    /// </summary>
    /// <param name="commit">The commit to cherry-pick.</param>
    /// <param name="opts">Cherry-pick options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.BareRepo"/> if the repository is bare.
    /// </exception>
    public async Task CherryPickAsync(
        Commit commit,
        GitCherryPickOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);

        opts ??= GitCherryPickOptions.Default;

        // Bare repo check. Matches cherrypick.c:189
        // (git_repository__ensure_not_bare, repository.h:216-229).
        if (IsBare)
        {
            throw new GitException(
                GitErrorCode.BareRepo,
                "cannot cherry-pick. This operation is not allowed against bare repositories.",
                GitErrorCategory.Repository);
        }

        // Build the "their" label: "<7-char-oid>... <summary>".
        // Matches cherrypick.c:194-196.
        string theirLabel = MergeBuildCherryPickLabel(commit);

        // Normalize options (set default checkout strategy + labels).
        (GitMergeOptions? mergeOpts, GitCheckoutOptions? checkoutOpts) = MergeNormalizeCherryPickOpts(opts, theirLabel);

        // Write MERGE_MSG first. Matches cherrypick.c:197 — it happens
        // BEFORE the index lock.
        string msg = commit.Message;
        await MergeState.WriteMergeMsgAsync(this, msg, cancellationToken).ConfigureAwait(false);

        // C (cherrypick.c:201-203): git_indexwriter_init_for_operation locks
        // the index BEFORE CHERRY_PICK_HEAD is written — a concurrent index
        // lock fails the cherry-pick early with GIT_ELOCKED and no
        // state file is created. should_write is false when the caller passed
        // GIT_CHECKOUT_DONT_WRITE_INDEX (index.c:3891).
        GitIndex repoIndex = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var indexWriter = IndexWriter.Init(repoIndex);
        bool shouldWrite = (checkoutOpts.Strategy & GitCheckoutStrategy.DontWriteIndex) == 0;
        await MergeWriteOperationHeadAsync(CherryPickHeadFile, commit.Id, cancellationToken).ConfigureAwait(false);

        GitIndex? index = null;
        try
        {
            // Resolve HEAD to our commit.
            using GitAnnotatedCommit ourHead = await GitAnnotatedCommit.FromHeadAsync(this, cancellationToken).ConfigureAwait(false);
            Commit? headCommit = ourHead.Commit;
            Debug.Assert(headCommit is not null, "Annotated commit from HEAD has a non-null Commit");
            Commit ourCommit = headCommit;

            // Perform the in-memory merge.
            index = await CherryPickCommitAsync(commit, ourCommit, opts.Mainline, mergeOpts, cancellationToken).ConfigureAwait(false);

            // Post-merge safety check. Matches cherrypick.c:206.
            await MergeCheckResultAsync(index, cancellationToken).ConfigureAwait(false);

            // Append conflict paths to MERGE_MSG. Matches cherrypick.c:207.
            await MergeState.AppendConflictsToMergeMsgAsync(this, index, cancellationToken).ConfigureAwait(false);

            // Checkout the cherry-pick result. Matches cherrypick.c:208. The
            // merged index is NOT written before checkout: like C, the repo
            // index is only committed after checkout succeeds
            // (git_indexwriter_commit, cherrypick.c:209), so a failed
            // checkout leaves the on-disk index untouched.
            GitCheckoutOptions normalizedCheckout = checkoutOpts with
            {
                Strategy = checkoutOpts.Strategy | GitCheckoutStrategy.DontWriteIndex,
            };
            await CheckoutIndexAsync(index, normalizedCheckout, cancellationToken).ConfigureAwait(false);

            // Commit the repo index to disk (git_indexwriter_commit,
            // cherrypick.c:209) — unless the caller passed DONT_WRITE_INDEX.
            if (shouldWrite)
            {
                await indexWriter.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // On error, clean up the CHERRY_PICK state files only (C
            // cherrypick.c:99-104, git_cherrypick__cleanup).
            StateCleanup(["CHERRY_PICK_HEAD", "MERGE_MSG"], []);
            throw;
        }
        finally
        {
            index?.Dispose();
            indexWriter.Dispose();
        }
    }

    // ── Public: revert ────────────────────────────────────────────────────

    /// <summary>
    /// Reverts a commit against another commit, producing a merged index
    /// without touching the working directory. Matches
    /// <c>git_revert_commit</c> (revert.c:117-166).
    /// </summary>
    /// <param name="revertCommit">The commit being reverted.</param>
    /// <param name="ourCommit">Our commit (HEAD side, the target).</param>
    /// <param name="mainline">Parent number (1-indexed) for merge commits.
    /// Must be 0 for non-merge commits.</param>
    /// <param name="mergeOpts">Merge options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The merged index (may have conflicts).</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> with <see cref="GitErrorCategory.Revert"/>
    /// if <paramref name="mainline"/> is inconsistent with the commit's
    /// parent count.
    /// </exception>
    public async Task<GitIndex> RevertCommitAsync(
        Commit revertCommit,
        Commit ourCommit,
        uint mainline,
        GitMergeOptions? mergeOpts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revertCommit);
        ArgumentNullException.ThrowIfNull(ourCommit);

        // Resolve the parent to diff against. Matches revert.c:134-146.
        int parentIndex = MergeResolveParentIndex(revertCommit, mainline, GitErrorCategory.Revert);

        // Get the parent tree.
        GitTree? parentTree = null;
        if (parentIndex >= 0)
        {
            GitOid parentId = revertCommit.ParentId(parentIndex);
            Commit parentCommit = await Objects.LookupAsync<Commit>(parentId, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    $"parent {parentId} of commit {revertCommit.Id} not found",
                    GitErrorCategory.Object);
            parentTree = await Objects.LookupAsync<GitTree>(parentCommit.Tree, cancellationToken).ConfigureAwait(false);
        }

        // Get our tree and the revert commit's tree.
        GitTree ourTree = await Objects.LookupAsync<GitTree>(ourCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"tree {ourCommit.Tree} not found",
                GitErrorCategory.Object);
        GitTree revertTree = await Objects.LookupAsync<GitTree>(revertCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"tree {revertCommit.Tree} not found",
                GitErrorCategory.Object);

        // 3-way merge: ancestor=revertTree, ours=ourTree, theirs=parentTree.
        // This is the SWAPPED ordering vs cherry-pick. Matches revert.c:157.
        return await MergeTreesAsync(revertTree, ourTree, parentTree, mergeOpts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts a commit against the current HEAD, updating the working
    /// directory and index. Matches <c>git_revert</c> (revert.c:168-225).
    /// </summary>
    /// <param name="commit">The commit to revert.</param>
    /// <param name="opts">Revert options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.BareRepo"/> if the repository is bare.
    /// </exception>
    public async Task RevertAsync(
        Commit commit,
        GitRevertOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);

        opts ??= GitRevertOptions.Default;

        // Bare repo check. Matches revert.c:189
        // (git_repository__ensure_not_bare, repository.h:216-229).
        if (IsBare)
        {
            throw new GitException(
                GitErrorCode.BareRepo,
                "cannot revert. This operation is not allowed against bare repositories.",
                GitErrorCategory.Repository);
        }

        // Build the "their" label: "parent of <7-char-oid>... <summary>".
        // Matches revert.c:194-196.
        string theirLabel = MergeBuildRevertLabel(commit);

        // Normalize options (set default checkout strategy + labels).
        (GitMergeOptions? mergeOpts, GitCheckoutOptions? checkoutOpts) = MergeNormalizeRevertOpts(opts, theirLabel);

        // Write MERGE_MSG first. Matches revert.c:197 — it happens BEFORE
        // the index lock.
        string msg = $"Revert \"{commit.Summary}\"\n\nThis reverts commit {commit.Id}.\n";
        await MergeState.WriteMergeMsgAsync(this, msg, cancellationToken).ConfigureAwait(false);

        // C (revert.c:201-203): git_indexwriter_init_for_operation locks the
        // index BEFORE REVERT_HEAD is written; a concurrent index lock fails
        // the revert early with GIT_ELOCKED. should_write is false when the
        // caller passed GIT_CHECKOUT_DONT_WRITE_INDEX (index.c:3891).
        GitIndex repoIndex = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var indexWriter = IndexWriter.Init(repoIndex);
        bool shouldWrite = (checkoutOpts.Strategy & GitCheckoutStrategy.DontWriteIndex) == 0;
        await MergeWriteOperationHeadAsync(RevertHeadFile, commit.Id, cancellationToken).ConfigureAwait(false);

        GitIndex? index = null;
        try
        {
            // Resolve HEAD to our commit.
            using GitAnnotatedCommit ourHead = await GitAnnotatedCommit.FromHeadAsync(this, cancellationToken).ConfigureAwait(false);
            Commit? headCommit = ourHead.Commit;
            Debug.Assert(headCommit is not null, "Annotated commit from HEAD has a non-null Commit");
            Commit ourCommit = headCommit;

            // Perform the in-memory merge.
            index = await RevertCommitAsync(commit, ourCommit, opts.Mainline, mergeOpts, cancellationToken).ConfigureAwait(false);

            // Post-merge safety check. Matches revert.c:206.
            await MergeCheckResultAsync(index, cancellationToken).ConfigureAwait(false);

            // Append conflict paths to MERGE_MSG. Matches revert.c:207.
            await MergeState.AppendConflictsToMergeMsgAsync(this, index, cancellationToken).ConfigureAwait(false);

            // Checkout the revert result. Matches revert.c:208. The merged
            // index is NOT written before checkout: like C, the repo index is
            // only committed after checkout succeeds (git_indexwriter_commit,
            // revert.c:209), so a failed checkout leaves the on-disk index
            // untouched.
            GitCheckoutOptions normalizedCheckout = checkoutOpts with
            {
                Strategy = checkoutOpts.Strategy | GitCheckoutStrategy.DontWriteIndex,
            };
            await CheckoutIndexAsync(index, normalizedCheckout, cancellationToken).ConfigureAwait(false);

            // Commit the repo index to disk (git_indexwriter_commit,
            // revert.c:209) — unless the caller passed DONT_WRITE_INDEX.
            if (shouldWrite)
            {
                await indexWriter.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // On error, clean up the REVERT state files only (C revert.c:100-105,
            // git_revert__cleanup).
            StateCleanup(["REVERT_HEAD", "MERGE_MSG"], []);
            throw;
        }
        finally
        {
            index?.Dispose();
            indexWriter.Dispose();
        }
    }

    // ── Public: file-level merge from index ──────────────────────────────

    /// <summary>
    /// Merges three files referenced by index entries, reading their content
    /// from the repository's object database. Matches
    /// <c>git_merge_file_from_index</c> (<c>merge_file.c:277-329</c>).
    /// </summary>
    /// <param name="ancestor">The ancestor index entry, or <c>null</c>.</param>
    /// <param name="ours">Our index entry, or <c>null</c>.</param>
    /// <param name="theirs">Their index entry, or <c>null</c>.</param>
    /// <param name="options">Optional merge file options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The merge result. The result blob is NOT written to ODB;
    /// the caller is responsible for writing if needed.</returns>
    /// <exception cref="GitException">If a blob cannot be read from ODB.</exception>
    public async Task<GitMergeFileResult> MergeFileFromIndexAsync(
        GitIndexEntry? ancestor,
        GitIndexEntry? ours,
        GitIndexEntry? theirs,
        GitMergeFileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (ancestor is null && ours is null && theirs is null)
        {
            throw new ArgumentException(
                "At least one of ancestor, ours, or theirs must be non-null.",
                nameof(ancestor));
        }

        GitMergeFileInput? ancestorInput = await MergeFileInputFromIndexAsync(ancestor, cancellationToken).ConfigureAwait(false);
        GitMergeFileInput? ourInput = await MergeFileInputFromIndexAsync(ours, cancellationToken).ConfigureAwait(false);
        GitMergeFileInput? theirInput = await MergeFileInputFromIndexAsync(theirs, cancellationToken).ConfigureAwait(false);

        return GitMergeFile.FromInputs(ancestorInput, ourInput, theirInput, options);
    }

    // ── Private: merge base computation ───────────────────────────────────

    private const CommitListGraphFlags MergeBaseAllFlags =
        CommitListGraphFlags.Parent1 | CommitListGraphFlags.Parent2 |
        CommitListGraphFlags.Result | CommitListGraphFlags.Stale;

    /// <summary>
    /// Core merge-base computation for one commit vs. a list of others.
    /// Matches <c>git_merge__bases_many</c> (<c>merge.c:517-586</c>).
    /// Returns merge-base OIDs sorted by date (newest first), or <c>null</c>
    /// if no merge base was found.
    /// </summary>
    private async Task<List<GitOid>?> MergeBaseBasesManyAsync(GitOid oneOid, List<GitOid> twosOids, CancellationToken cancellationToken)
    {
        if (twosOids.Count == 0)
        {
            return null;
        }

        using var walk = new GitRevWalker(this);

        var twos = new List<CommitListNode>(twosOids.Count);
        foreach (GitOid twoOid in twosOids)
        {
            CommitListNode node = walk.LookupCommit(twoOid);
            twos.Add(node);
        }

        CommitListNode one = walk.LookupCommit(oneOid);

        // If the commit is repeated, we have our merge base already.
        foreach (CommitListNode two in twos)
        {
            if (ReferenceEquals(one, two))
            {
                return [one.Oid];
            }
        }

        // Matches git_commit_list_parse(walk, one) (merge.c:541-542): a
        // missing first commit is an error, not a "no merge base" answer.
        // C's merge_bases/many on_error return a bare -1 (GIT_ERROR) with
        // the ODB's "object not found - no match for id (...)" message.
        await ParseForGraphAsync(walk, one, cancellationToken).ConfigureAwait(false);

        List<CommitListNode> result = await RevwalkPaintDownToCommonAsync(walk, one, twos, minimumGeneration: 0, cancellationToken).ConfigureAwait(false);

        // Filter out stale commits (those flagged STALE are ancestors of a
        // merge base and not themselves a merge base).
        List<CommitListNode?> filtered = [];
        foreach (CommitListNode node in result)
        {
            if ((node.GraphFlags & CommitListGraphFlags.Stale) == 0)
            {
                filtered.Add(node);
            }
        }

        // More than one merge base — prune redundant bases.
        if (filtered.Count > 1)
        {
            // Clear ALL_FLAGS from the entire commit graph that was traversed.
            // This is critical: the merge base candidates (filtered) still carry
            // PARENT1|PARENT2 from the first PaintDownToCommon. If not cleared,
            // RemoveRedundant's check for PARENT2/PARENT1 on the candidates will
            // see stale flags and incorrectly mark all candidates as redundant.
            // Matches merge.c:569-570 (clear_commit_marks(one, ALL_FLAGS) +
            // clear_commit_marks_many(twos, ALL_FLAGS) which walk the graph
            // and clear ALL reachable commits, including the merge bases).
            MergeBaseClearCommitMarks(one, MergeBaseAllFlags);
            MergeBaseClearCommitMarksMany(twos, MergeBaseAllFlags);

            await MergeBaseRemoveRedundantAsync(walk, filtered, minimumGeneration: 0, cancellationToken).ConfigureAwait(false);

            // Rebuild the list with only non-null (non-redundant) entries.
            var pruned = new List<CommitListNode?>(filtered.Count);
            foreach (CommitListNode? node in filtered)
            {
                if (node is not null)
                {
                    pruned.Add(node);
                }
            }

            filtered = pruned;
        }

        if (filtered.Count == 0)
        {
            return null;
        }

        // Sort by date (newest first), matching git_commit_list_insert_by_date.
        // TimSort is stable: for equal timestamps the paint/pop order is
        // preserved, so bases[0] (the chosen merge base) is deterministic
        // like C's insert_by_date (commit_list.c:62-75). List<T>.Sort is
        // introsort (unstable) and would pick an arbitrary tie order.
        TimSort.Sort(filtered, static (a, b) =>
        {
            Debug.Assert(a is not null && b is not null, "Redundant entries pruned before sort.");
            return CommitList.TimeCompare(a, b);
        });

        var oids = new List<GitOid>(filtered.Count);
        foreach (CommitListNode? node in filtered)
        {
            if (node is not null)
            {
                oids.Add(node.Oid);
            }
        }

        return oids;
    }

    /// <summary>
    /// Prunes redundant merge bases from <paramref name="commits"/>. A
    /// commit is redundant if it is reachable from another candidate (i.e.
    /// it is an ancestor of another merge base). Redundant entries are set
    /// to <c>null</c> in the list (the caller skips nulls).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For each candidate <c>i</c> (not already redundant), paint commit
    /// <c>i</c> (PARENT1) against all other non-redundant candidates
    /// (PARENT2) via <see cref="RevwalkPaintDownToCommonAsync"/>.
    /// If <c>i</c> gets PARENT2, it is reachable from another candidate →
    /// redundant. If candidate <c>j</c> gets PARENT1, it is reachable from
    /// <c>i</c> → redundant.
    /// </para>
    /// <para>
    /// ALL_FLAGS are cleared from all touched commits between iterations to
    /// avoid cross-contamination.
    /// </para>
    /// </remarks>
    private static async Task MergeBaseRemoveRedundantAsync(GitRevWalker walk, List<CommitListNode?> commits, uint minimumGeneration, CancellationToken cancellationToken)
    {
        int count = commits.Count;
        bool[] redundant = new bool[count];
        int[] filledIndex = new int[count - 1];

        // Parse all commits first (matches merge.c:462-465).
        foreach (CommitListNode? commit in commits)
        {
            if (commit is not null)
            {
                await CommitList.ParseAsync(commit, walk.Repository, oid => walk.LookupCommit(oid), cancellationToken).ConfigureAwait(false);
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (redundant[i])
            {
                continue;
            }

            if (commits[i] is not { } commit)
            {
                continue;
            }

            // Build the work list: all other non-redundant candidates.
            var work = new List<CommitListNode>(count - 1);
            for (int j = 0; j < count; j++)
            {
                if (i == j || redundant[j])
                {
                    continue;
                }

                filledIndex[work.Count] = j;
                if (commits[j] is { } other)
                {
                    work.Add(other);
                }
            }

            // Paint commit i (PARENT1) vs. work (PARENT2).
            await RevwalkPaintDownToCommonAsync(walk, commit, work, minimumGeneration, cancellationToken).ConfigureAwait(false);

            // If commit i got PARENT2, it's reachable from another candidate.
            if ((commit.GraphFlags & CommitListGraphFlags.Parent2) != 0)
            {
                redundant[i] = true;
            }

            // If any work[j] got PARENT1, it's reachable from commit i.
            for (int j = 0; j < work.Count; j++)
            {
                if ((work[j].GraphFlags & CommitListGraphFlags.Parent1) != 0)
                {
                    redundant[filledIndex[j]] = true;
                }
            }

            // Clear ALL_FLAGS from commit i and all work entries.
            MergeBaseClearCommitMarks(commit, MergeBaseAllFlags);
            MergeBaseClearCommitMarksMany(work, MergeBaseAllFlags);
        }

        // Null out redundant entries (matches merge.c:505-508).
        for (int i = 0; i < count; i++)
        {
            if (redundant[i])
            {
                commits[i] = null;
            }
        }
    }

    /// <summary>
    /// Clears <paramref name="mark"/> from <paramref name="commit"/> and all
    /// of its ancestors. Matches <c>clear_commit_marks</c>
    /// (<c>merge.c:368-377</c>).
    /// </summary>
    /// <remarks>
    /// Walks the first-parent chain clearing the mark. Other parents are
    /// pushed to a list for breadth-first clearing. Stops when a commit no
    /// longer has the mark (already cleared).
    /// </remarks>
    private static void MergeBaseClearCommitMarks(CommitListNode commit, CommitListGraphFlags mark)
    {
        CommitList? list = null;
        list = CommitList.Insert(commit, list);
        while (list is not null)
        {
            (CommitListNode? node, CommitList? rest) = CommitList.Pop(list);
            list = rest;
            Debug.Assert(node is not null, "CommitList.Pop on non-null head yields non-null node");
            MergeBaseClearCommitMarks1(ref list, node, mark);
        }
    }

    /// <summary>
    /// Clears <paramref name="mark"/> from multiple commits and their
    /// ancestors. Matches <c>clear_commit_marks_many</c>
    /// (<c>merge.c:351-366</c>).
    /// </summary>
    private static void MergeBaseClearCommitMarksMany(List<CommitListNode> commits, CommitListGraphFlags mark)
    {
        CommitList? list = null;
        foreach (CommitListNode? commit in commits)
        {
            if (commit is not null)
            {
                list = CommitList.Insert(commit, list);
            }
        }

        while (list is not null)
        {
            (CommitListNode? node, CommitList? rest) = CommitList.Pop(list);
            list = rest;
            Debug.Assert(node is not null, "CommitList.Pop on non-null head yields non-null node");
            MergeBaseClearCommitMarks1(ref list, node, mark);
        }
    }

    /// <summary>
    /// Walks the first-parent chain clearing <paramref name="mark"/>. Other
    /// parents are pushed to <paramref name="plist"/> for later processing.
    /// Matches <c>clear_commit_marks_1</c> (<c>merge.c:328-349</c>).
    /// </summary>
    private static void MergeBaseClearCommitMarks1(ref CommitList? plist, CommitListNode commit, CommitListGraphFlags mark)
    {
        CommitListNode? current = commit;
        while (current is not null)
        {
            if ((current.GraphFlags & mark) == 0)
            {
                return;
            }

            current.GraphFlags &= ~mark;

            // Push parents 1..N to the list (parent 0 is walked inline).
            CommitListNode[]? parents = current.Parents;
            for (int i = 1; i < current.OutDegree; i++)
            {
                Debug.Assert(parents is not null, "Parents is set (with OutDegree) during ParseAsync");
                CommitListNode p = parents[i];
                plist = CommitList.Insert(p, plist);
            }

            if (current.OutDegree > 0)
            {
                Debug.Assert(parents is not null, "Parents is set (with OutDegree) during ParseAsync");
                current = parents[0];
            }
            else
            {
                current = null;
            }
        }
    }

    // ── Private: merge analysis helpers ──────────────────────────────────

    /// <summary>
    /// Reads <c>merge.ff</c> config and converts to a preference. If
    /// parseable as bool and <c>false</c> → <see cref="GitMergePreference.NoFastForward"/>.
    /// If string value is <c>"only"</c> → <see cref="GitMergePreference.FastForwardOnly"/>.
    /// Otherwise → <see cref="GitMergePreference.None"/>.
    /// </summary>
    private async ValueTask<GitMergePreference> MergeAnalyzeReadPreferenceAsync(CancellationToken cancellationToken)
    {
        GitConfigEntry? entry = await Config.GetEntryAsync("merge.ff", cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return GitMergePreference.None;
        }

        // byte-domain compare — C (merge.c:3233-3238) runs git_config_parse_bool over the raw value bytes, then strcasecmp against "only". A lone variable (no
        // value) is treated as bool true → no preference.
        if (entry.Value.ValueBytes is not { } valueBytes)
        {
            return GitMergePreference.None;
        }

        if (ConfigurationValueParser.TryParseBool(valueBytes.Span, out bool boolValue))
        {
            return boolValue ? GitMergePreference.None : GitMergePreference.NoFastForward;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(valueBytes.Span, "only"))
        {
            return GitMergePreference.FastForwardOnly;
        }

        return GitMergePreference.None;
    }

    /// <summary>
    /// Resolves our HEAD to an annotated commit and computes the merge
    /// ancestor between our HEAD and their heads. Matches <c>merge_heads</c>
    /// (<c>merge.c:3176-3211</c>) + <c>merge_ancestor_head</c>
    /// (<c>merge.c:2867-2899</c>).
    /// </summary>
    /// <returns>The ancestor (or <c>null</c> if none exists) and our head.</returns>
    private async Task<(GitAnnotatedCommit? ancestor, GitAnnotatedCommit ourHead)> MergeAnalyzeResolveAncestorAsync(
        GitReference ourRef,
        IReadOnlyList<GitAnnotatedCommit> theirHeads,
        CancellationToken cancellationToken)
    {
        // Resolve our head from the ref.
        GitAnnotatedCommit ourHead = await GitAnnotatedCommit.FromRefAsync(this, ourRef, cancellationToken).ConfigureAwait(false);

        // Compute merge base of [ourHead.Id, theirHeads[0].Id, ...].
        var oids = new List<GitOid>(theirHeads.Count + 1) { ourHead.Id };
        foreach (GitAnnotatedCommit their in theirHeads)
        {
            oids.Add(their.Id);
        }

        GitOid? ancestorOid = await MergeBaseFindManyAsync(oids, cancellationToken).ConfigureAwait(false);

        GitAnnotatedCommit? ancestor = null;
        if (ancestorOid is { } oid)
        {
            ancestor = await GitAnnotatedCommit.LookupAsync(this, oid, cancellationToken).ConfigureAwait(false);
        }

        return (ancestor, ourHead);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="ref"/> is a symbolic HEAD
    /// pointing at a branch that doesn't exist yet (unborn). Matches
    /// <c>git_reference__is_unborn_head</c>.
    /// </summary>
    private async Task<bool> MergeAnalyzeIsUnbornHeadAsync(GitReference @ref, CancellationToken cancellationToken)
    {
        // Direct ref → not unborn.
        if (!@ref.IsSymbolic)
        {
            return false;
        }

        // Try to resolve the symbolic chain. If the target doesn't resolve
        // and the ref is HEAD, it's unborn.
        GitReference? resolved = await Refs.ResolveAsync(@ref.Name, cancellationToken).ConfigureAwait(false);
        return resolved is null && @ref.Name == GitReferences.HeadFile;
    }

    // ── Private: merge orchestration helpers ─────────────────────────────

    /// <summary>
    /// Returns the ancestor OIDs of an annotated commit for merge-base
    /// computation. REAL commits return <c>[Id]</c>; VIRTUAL commits return
    /// their accumulated parent list. Matches <c>insert_head_ids</c>
    /// (merge.c:2251-2273).
    /// </summary>
    private static IReadOnlyList<GitOid> MergeInsertHeadIds(GitAnnotatedCommit commit)
    {
        return commit.ParentOids;
    }

    /// <summary>
    /// Creates an iterator for an annotated commit. NULL → empty iterator;
    /// VIRTUAL → index iterator; REAL → tree iterator. Matches
    /// <c>iterator_for_annotated_commit</c> (merge.c:2388-2411).
    /// </summary>
    private async Task<IIterator> MergeIteratorForAnnotatedCommitAsync(
        GitAnnotatedCommit? commit,
        IteratorOptions iterOpts,
        CancellationToken cancellationToken)
    {
        if (commit is null)
        {
            return new EmptyIterator();
        }

        if (commit.Type == AnnotatedCommitType.Virtual)
        {
            return IndexIterator.ForIndex(commit.VirtualIndex, this, iterOpts);
        }

        // REAL: lazy-load the tree (matches merge.c:2402-2404).
        Commit? realCommit = commit.Commit;
        Debug.Assert(realCommit is not null, "REAL annotated commit has a non-null Commit");
        GitTree? tree = await Objects.LookupAsync<GitTree>(realCommit.Tree, cancellationToken).ConfigureAwait(false);
        return TreeIterator.ForTree(tree, this, iterOpts);
    }

    /// <summary>
    /// Normalizes checkout options for merge, setting ancestor/our/their
    /// labels from the annotated commit metadata. Matches
    /// <c>merge_normalize_checkout_opts</c> (merge.c:2914-2962).
    /// </summary>
    private static GitCheckoutOptions MergeNormalizeCheckoutOpts(
        GitCheckoutStrategy strategy,
        GitAnnotatedCommit? ancestor,
        GitAnnotatedCommit ourHead,
        IReadOnlyList<GitAnnotatedCommit> theirHeads,
        GitCheckoutOptions? givenCheckoutOpts)
    {
        ArgumentNullException.ThrowIfNull(ourHead);
        ArgumentNullException.ThrowIfNull(theirHeads);
        GitCheckoutOptions opts = givenCheckoutOpts ?? new GitCheckoutOptions();
        opts = opts with { Strategy = strategy };

        // Ancestor label. Matches merge.c:2936-2943.
        string? ancestorLabel;
        if (ancestor is not null && ancestor.Type == AnnotatedCommitType.Real && ancestor.Commit is not null)
        {
            ancestorLabel = ancestor.Commit.Summary;
        }
        else if (ancestor is not null && ancestor.Type == AnnotatedCommitType.Virtual)
        {
            ancestorLabel = "merged common ancestors";
        }
        else
        {
            ancestorLabel = "empty base";
        }

        // Our label. Matches merge.c:2945-2950.
        string? ourLabel = ourHead.Ref ?? "ours";

        // Their label. Matches merge.c:2952-2999.
        string? theirLabel;
        if (theirHeads.Count == 1 && theirHeads[0].Ref is not null)
        {
            theirLabel = MergeTheirLabel(theirHeads[0].Ref!);
        }
        else if (theirHeads.Count == 1)
        {
            theirLabel = theirHeads[0].Id.ToString();
        }
        else
        {
            theirLabel = "theirs";
        }

        return opts with
        {
            AncestorLabel = ancestorLabel,
            OurLabel = ourLabel,
            TheirLabel = theirLabel,
        };
    }

    /// <summary>
    /// Extracts the last path component of a branch ref for the "their" label.
    /// Matches <c>merge_their_label</c> (merge.c:2901-2912).
    /// <c>"refs/heads/feature"</c> → <c>"feature"</c>.
    /// <c>"refs/heads/"</c> → <c>"theirs"</c>.
    /// <c>"branch"</c> → <c>"branch"</c>.
    /// </summary>
    private static string MergeTheirLabel(string branchName)
    {
        int slash = branchName.LastIndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return branchName;
        }

        if (slash + 1 >= branchName.Length)
        {
            return "theirs";
        }

        return branchName[(slash + 1)..];
    }

    /// <summary>
    /// Checks that no staged changes exist that differ from the merge result.
    /// Matches <c>merge_check_index</c> (merge.c:2964-3020). Returns the number
    /// of conflicting staged entries.
    /// </summary>
    private async Task<int> MergeCheckIndexAsync(
        GitIndex indexNew,
        List<GitPath> _,
        CancellationToken cancellationToken)
    {
        // Diff HEAD tree vs repo index to find staged changes.
        // Matches merge.c:2986-2989.
        GitTree? headTree = await MergeResolveHeadTreeAsync(cancellationToken).ConfigureAwait(false);
        if (headTree is null)
        {
            // C (merge.c:2986-2989): merge_check_index fails on an unborn
            // HEAD (git_repository_head_tree error).
            throw new GitException(
                GitErrorCode.UnbornBranch,
                "reference 'HEAD' not found",
                GitErrorCategory.Reference);
        }

        DiffGenerator stagedDiff = await DiffGenerator.TreeToIndexAsync(this, headTree, new GitDiffOptions(), cancellationToken).ConfigureAwait(false);
        if (stagedDiff.Deltas.Count == 0)
        {
            return 0;
        }

        // Collect staged paths. Matches merge.c:2994-2997.
        var stagedPaths = new List<GitPath>();
        foreach (GitDiffDelta delta in stagedDiff.Deltas)
        {
            stagedPaths.Add(delta.DeltaPath ?? default);
        }

        // Diff repo index vs merge result index, constrained to staged paths.
        // Matches merge.c:2999-3006.
        var diffOpts = new GitDiffOptions
        {
            PathSpecs = [.. stagedPaths],
        };

        var iterOpts = new IteratorOptions
        {
            Flags = IteratorFlags.DontIgnoreCase,
        };

        GitIndex repoIndex = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        using IIterator repoIter = IndexIterator.ForIndex(repoIndex, this, iterOpts);
        using IIterator newIter = IndexIterator.ForIndex(indexNew, this, iterOpts);

        DiffGenerator indexDiff = await DiffGenerator.GenerateAsync(this, repoIter, newIter, diffOpts, cancellationToken).ConfigureAwait(false);
        return indexDiff.Deltas.Count;
    }

    /// <summary>
    /// Checks that no workdir changes exist in files that the merge would
    /// modify. Matches <c>merge_check_workdir</c> (merge.c:3022-3062). Returns
    /// the number of conflicting workdir entries.
    /// </summary>
    private async Task<int> MergeCheckWorkdirAsync(
        GitIndex _,
        List<GitPath> mergedPaths,
        CancellationToken cancellationToken)
    {
        // If no files were merged, no workdir conflict is possible.
        // Matches merge.c:3039-3040.
        if (mergedPaths.Count == 0)
        {
            return 0;
        }

        // Diff index vs workdir with IncludeUntracked + DisablePathspecMatch +
        // pathspec=mergedPaths. Matches merge.c:3042-3053.
        var diffOpts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUntracked | GitDiffOptionsFlags.DisablePathspecMatch,
            PathSpecs = [.. mergedPaths],
            IgnoreSubmodules = GitDiffIgnoreSubmodules.All,
        };

        DiffGenerator wdDiff = await DiffGenerator.IndexToWorkdirAsync(this, diffOpts, cancellationToken).ConfigureAwait(false);
        return wdDiff.Deltas.Count;
    }

    /// <summary>
    /// Resolves HEAD to its tree. Returns null if HEAD is unborn or
    /// points at a non-commit object. Matches the HEAD-tree lookup used by
    /// <c>git_merge__check_result</c>.
    /// </summary>
    private async Task<GitTree?> MergeResolveHeadTreeAsync(CancellationToken cancellationToken)
    {
        GitReference? head = await Refs.ResolveAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false);
        if (head is not GitDirectReference dr)
        {
            return null;
        }

        GitOid targetOid = dr.Target;

        if (targetOid.IsZero)
        {
            return null;
        }

        Commit? commit = await Objects.LookupAsync<Commit>(targetOid, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            // HEAD resolved to a non-zero OID, but the commit object is absent.
            // Inconsistent-DB state — not unborn (the unborn case returns
            // earlier on the null/symref/zero-OID paths). C's
            // checkout_lookup_head_tree propagates this as GIT_ENOTFOUND.
            throw new GitException(
                GitErrorCode.NotFound,
                $"HEAD points to commit {targetOid} but the object is not in the database",
                GitErrorCategory.Reference);
        }

        return await Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
    }

    // ── Private: cherry-pick / revert shared helpers ──────────────────────

    /// <summary>
    /// Resolves the parent index (0-indexed) to diff against. Returns -1 if
    /// the commit has no parents (root commit). Matches cherrypick.c:133-145
    /// / revert.c:134-146.
    /// </summary>
    private static int MergeResolveParentIndex(Commit commit, uint mainline, GitErrorCategory errorCategory)
    {
        int parentCount = commit.Parents.Count;

        if (parentCount > 1 && mainline == 0)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"mainline branch is not specified but {commit.Id} is a merge commit",
                errorCategory);
        }

        if (parentCount <= 1 && mainline != 0)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"mainline branch specified but {commit.Id} is not a merge commit",
                errorCategory);
        }

        if (parentCount == 0)
        {
            return -1;
        }

        // mainline is 1-indexed; for non-merge commits, parent index is 0.
        // For merge commits, parent index is (mainline - 1).
        return mainline > 0 ? (int)(mainline - 1) : 0;
    }

    /// <summary>
    /// Builds the cherry-pick "their" conflict label:
    /// "&lt;7-char-oid&gt;... &lt;summary&gt;". Matches cherrypick.c:194-196.
    /// </summary>
    private static string MergeBuildCherryPickLabel(Commit commit)
    {
        string shortOid = commit.Id.ToString()[..7];
        string summary = commit.Summary;
        return $"{shortOid}... {summary}";
    }

    /// <summary>
    /// Builds the revert "their" conflict label:
    /// "parent of &lt;7-char-oid&gt;... &lt;summary&gt;". Matches revert.c:194-196.
    /// </summary>
    private static string MergeBuildRevertLabel(Commit commit)
    {
        string shortOid = commit.Id.ToString()[..7];
        string summary = commit.Summary;
        return $"parent of {shortOid}... {summary}";
    }

    /// <summary>
    /// Normalizes cherry-pick options: sets the default checkout strategy
    /// (AllowConflicts if the strategy is 0) and default conflict labels
    /// (ours="HEAD", theirs=<paramref name="theirLabel"/>) — ONLY when the
    /// corresponding field is unset; user-provided strategies and labels are
    /// preserved. Matches <c>cherrypick_normalize_opts</c>
    /// (cherrypick.c:69-97).
    /// </summary>
    private static (GitMergeOptions? mergeOpts, GitCheckoutOptions checkoutOpts) MergeNormalizeCherryPickOpts(
        GitCherryPickOptions opts,
        string theirLabel)
    {
        // Default checkout strategy: AllowConflicts, only when the strategy
        // is 0 (checkout_strategy defaults to SAFE=0). Matches
        // cherrypick.c:87-88.
        GitCheckoutOptions checkoutOpts = opts.CheckoutOptions ?? new GitCheckoutOptions();
        if (checkoutOpts.Strategy == GitCheckoutStrategy.Safe)
        {
            checkoutOpts = checkoutOpts with
            {
                Strategy = GitCheckoutStrategy.AllowConflicts,
            };
        }

        // Default conflict labels, only when unset. Matches cherrypick.c:90-94.
        if (checkoutOpts.OurLabel is null)
        {
            checkoutOpts = checkoutOpts with { OurLabel = "HEAD" };
        }

        if (checkoutOpts.TheirLabel is null)
        {
            checkoutOpts = checkoutOpts with { TheirLabel = theirLabel };
        }

        return (opts.MergeOptions, checkoutOpts);
    }

    /// <summary>
    /// Normalizes revert options: sets the default checkout strategy
    /// (AllowConflicts if the strategy is 0) and default conflict labels
    /// (ours="HEAD", theirs=<paramref name="theirLabel"/>) — ONLY when the
    /// corresponding field is unset; user-provided strategies and labels are
    /// preserved. Matches <c>revert_normalize_opts</c> (revert.c:70-98).
    /// </summary>
    private static (GitMergeOptions? mergeOpts, GitCheckoutOptions checkoutOpts) MergeNormalizeRevertOpts(
        GitRevertOptions opts,
        string theirLabel)
    {
        // Default checkout strategy: AllowConflicts, only when the strategy
        // is 0. Matches revert.c:88-89.
        GitCheckoutOptions checkoutOpts = opts.CheckoutOptions ?? new GitCheckoutOptions();
        if (checkoutOpts.Strategy == GitCheckoutStrategy.Safe)
        {
            checkoutOpts = checkoutOpts with
            {
                Strategy = GitCheckoutStrategy.AllowConflicts,
            };
        }

        // Default conflict labels, only when unset. Matches revert.c:91-95.
        if (checkoutOpts.OurLabel is null)
        {
            checkoutOpts = checkoutOpts with { OurLabel = "HEAD" };
        }

        if (checkoutOpts.TheirLabel is null)
        {
            checkoutOpts = checkoutOpts with { TheirLabel = theirLabel };
        }

        return (opts.MergeOptions, checkoutOpts);
    }

    /// <summary>
    /// Writes an operation HEAD file (<c>CHERRY_PICK_HEAD</c> or
    /// <c>REVERT_HEAD</c>) — the full OID of the commit being operated on.
    /// Matches <c>write_cherrypick_head</c> (cherrypick.c:24-43) /
    /// <c>write_revert_head</c> (revert.c:23-42). Consolidated from the two
    /// near-identical helpers on the cherry-pick and revert facades.
    /// </summary>
    private async Task MergeWriteOperationHeadAsync(string headFile, GitOid commitId, CancellationToken cancellationToken)
    {
        string content = commitId.ToString() + "\n";
        await MergeState.WriteAtomicAsync(this, headFile, content, cancellationToken).ConfigureAwait(false);
    }

    // ── Private: file-level merge helper ──────────────────────────────────

    /// <summary>
    /// Reads a blob from the ODB for the given index entry, building a
    /// <see cref="GitMergeFileInput"/>. Matches <c>merge_file_input_from_index</c>
    /// (<c>merge_file.c:29-52</c>).
    /// </summary>
    private async Task<GitMergeFileInput?> MergeFileInputFromIndexAsync(GitIndexEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is null || entry.Value.Id.IsZero)
        {
            return null;
        }

        GitBlob blob = await Objects.LookupAsync<GitBlob>(entry.Value.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"blob {entry.Value.Id} not found",
                GitErrorCategory.Object);

        using GitBlob _ = blob;

        return new GitMergeFileInput(
            Path: entry.Value.Path,
            Mode: (uint)entry.Value.Mode,
            Contents: blob.Content);
    }

    // ── Private: 3-way merge engine (find differences) ───────────────────

    /// <summary>
    /// Per-conflict similarity tracking. Matches
    /// <c>struct merge_diff_similarity</c> (<c>merge.c</c>).
    /// </summary>
    private struct MergeDiffSimilarity
    {
        public int Similarity;
        public int OtherIdx;
    }

    private static bool MergeIndexEntryEqual(in GitIndexEntry a, in GitIndexEntry b)
    {
        return a.Id.Equals(b.Id) && a.Mode == b.Mode;
    }

    private static async Task MergeFindDifferencesAsync(
        MergeDiffList diffList,
        IIterator ancestorIter,
        IIterator ourIter,
        IIterator theirIter,
        CancellationToken cancellationToken)
    {
        // Track D/F conflict state across consecutive entries.
        GitPath? dfPath = null;
        GitPath? prevPath = null;
        MergeDiff? prevConflict = null;

        IIterator[] iterators = [ancestorIter, ourIter, theirIter];
        await IteratorWalker.WalkAsync(iterators, async (entries, ct) =>
        {
            bool itemModified = false;

            GitIndexEntry? ancestorEntry = entries[0];
            GitIndexEntry? ourEntry = entries[1];
            GitIndexEntry? theirEntry = entries[2];

            if (ancestorEntry is null || ourEntry is null || theirEntry is null)
            {
                itemModified = true;
            }
            else
            {
                // Compare ancestor vs ours and ancestor vs theirs.
                if (!MergeIndexEntryEqual(ancestorEntry.Value, ourEntry.Value) ||
                    !MergeIndexEntryEqual(ancestorEntry.Value, theirEntry.Value))
                {
                    itemModified = true;
                }
            }

            if (itemModified)
            {
                MergeDiff conflict = MergeDiffFromIndexEntries(entries);
                MergeDetectType(conflict);
                MergeDetectDfConflict(
                    conflict, ref dfPath, ref prevPath, ref prevConflict);
                diffList.Conflicts.Add(conflict);
            }
            else
            {
                // All three match → unmodified, add to staged.
                Debug.Assert(ancestorEntry is not null, "ancestorEntry is non-null when !itemModified");
                diffList.Staged.Add(ancestorEntry.Value);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static MergeDiff MergeDiffFromIndexEntries(GitIndexEntry?[] entries)
    {
        var diff = new MergeDiff
        {
            AncestorEntry = entries[0] ?? default,
            OurEntry = entries[1] ?? default,
            TheirEntry = entries[2] ?? default,
            OurStatus = MergeDeltaTypeFromIndexEntries(
                entries[0].HasValue ? entries[0] : null,
                entries[1].HasValue ? entries[1] : null),
            TheirStatus = MergeDeltaTypeFromIndexEntries(
                entries[0].HasValue ? entries[0] : null,
                entries[2].HasValue ? entries[2] : null)
        };

        return diff;
    }

    private static GitDeltaStatus MergeDeltaTypeFromIndexEntries(
        GitIndexEntry? ancestor, GitIndexEntry? other)
    {
        if (ancestor is null && other is null)
        {
            return GitDeltaStatus.Unmodified;
        }

        if (ancestor is null && other is not null)
        {
            return GitDeltaStatus.Added;
        }

        if (ancestor is not null && other is null)
        {
            return GitDeltaStatus.Deleted;
        }

        // Both exist — check for typechange or modification.
        Debug.Assert(ancestor is not null && other is not null, "Both ancestor and other are non-null after early returns");
        GitIndexEntry a = ancestor.Value;
        GitIndexEntry o = other.Value;

        bool aIsDir = (a.Mode == GitFileMode.Tree);
        bool oIsDir = (o.Mode == GitFileMode.Tree);
        if (aIsDir ^ oIsDir)
        {
            return GitDeltaStatus.Typechange;
        }

        bool aIsLink = (a.Mode == GitFileMode.Symlink);
        bool oIsLink = (o.Mode == GitFileMode.Symlink);
        if (aIsLink ^ oIsLink)
        {
            return GitDeltaStatus.Typechange;
        }

        if (!a.Id.Equals(o.Id) || a.Mode != o.Mode)
        {
            return GitDeltaStatus.Modified;
        }

        return GitDeltaStatus.Unmodified;
    }

    private static void MergeDetectType(MergeDiff conflict)
    {
        if (conflict.OurStatus == GitDeltaStatus.Added &&
            conflict.TheirStatus == GitDeltaStatus.Added)
        {
            conflict.Type = MergeDiffType.BothAdded;
        }
        else if (conflict.OurStatus == GitDeltaStatus.Modified &&
                 conflict.TheirStatus == GitDeltaStatus.Modified)
        {
            conflict.Type = MergeDiffType.BothModified;
        }
        else if (conflict.OurStatus == GitDeltaStatus.Deleted &&
                 conflict.TheirStatus == GitDeltaStatus.Deleted)
        {
            conflict.Type = MergeDiffType.BothDeleted;
        }
        else if (conflict.OurStatus == GitDeltaStatus.Modified &&
                 conflict.TheirStatus == GitDeltaStatus.Deleted)
        {
            conflict.Type = MergeDiffType.ModifiedDeleted;
        }
        else if (conflict.OurStatus == GitDeltaStatus.Deleted &&
                 conflict.TheirStatus == GitDeltaStatus.Modified)
        {
            conflict.Type = MergeDiffType.ModifiedDeleted;
        }
        else
        {
            conflict.Type = MergeDiffType.None;
        }
    }

    private static void MergeDetectDfConflict(
        MergeDiff conflict,
        ref GitPath? dfPath,
        ref GitPath? prevPath,
        ref MergeDiff? prevConflict)
    {
        GitPath curPath = MergeDiffPath(conflict);

        // Check if this is a child of an existing D/F conflict.
        if (dfPath is not null && MergePathIsPrefixed(dfPath.Value, curPath))
        {
            conflict.Type = MergeDiffType.DfChild;
        }
        else if (dfPath is not null)
        {
            dfPath = null;
        }
        else if (prevPath is not null &&
                 MergeAnySideAddedOrModified(prevConflict) &&
                 MergeAnySideAddedOrModified(conflict) &&
                 MergePathIsPrefixed(prevPath.Value, curPath))
        {
            conflict.Type = MergeDiffType.DfChild;
            Debug.Assert(prevConflict is not null, "prevConflict is non-null when AnySideAddedOrModified returned true");
            prevConflict.Type = MergeDiffType.DirectoryFile;
            dfPath = prevPath;
        }

        prevPath = curPath;
        prevConflict = conflict;
    }

    private static GitPath MergeDiffPath(MergeDiff conflict)
    {
        if (MergeEntryExists(conflict.AncestorEntry))
        {
            return conflict.AncestorEntry.Path;
        }

        if (MergeEntryExists(conflict.OurEntry))
        {
            return conflict.OurEntry.Path;
        }

        if (MergeEntryExists(conflict.TheirEntry))
        {
            return conflict.TheirEntry.Path;
        }

        return default;
    }

    private static bool MergeAnySideAddedOrModified(MergeDiff? conflict)
    {
        if (conflict is null)
        {
            return false;
        }

        return conflict.OurStatus == GitDeltaStatus.Added ||
               conflict.OurStatus == GitDeltaStatus.Modified ||
               conflict.TheirStatus == GitDeltaStatus.Added ||
               conflict.TheirStatus == GitDeltaStatus.Modified;
    }

    private static bool MergePathIsPrefixed(GitPath parent, GitPath child)
    {
        ReadOnlySpan<byte> ps = parent.Span;
        ReadOnlySpan<byte> cs = child.Span;
        if (cs.Length < ps.Length ||
            !cs.StartsWith(ps))
        {
            return false;
        }

        return cs[ps.Length] == (byte)'/';
    }

    private static bool MergeEntryExists(in GitIndexEntry entry) => entry.Mode != 0;

    private static GitOid MergeGetEmptyBlobOid(GitHashAlgorithmKind kind)
        => kind == GitHashAlgorithmKind.Sha256
            ? GitOid.EmptyBlobSha256
            : GitOid.EmptyBlobSha1;

    // ── Private: rename detection ────────────────────────────────────────

    /// <summary>
    /// Finds renames in the conflict list: exact OID match first, then
    /// inexact content similarity via <see cref="SimilarityHash"/>. Coalesces
    /// rename targets into their sources and classifies the conflict type.
    /// Matches <c>git_merge_diff_list__find_renames</c>
    /// (<c>merge.c:1545-1615</c>).
    /// </summary>
    private async Task MergeFindRenamesAsync(
        MergeDiffList diffList,
        GitMergeOptions opts,
        CancellationToken cancellationToken)
    {
        List<MergeDiff> conflicts = diffList.Conflicts;
        if (conflicts.Count == 0)
        {
            return;
        }

        var simOurs = new MergeDiffSimilarity[conflicts.Count];
        var simTheirs = new MergeDiffSimilarity[conflicts.Count];

        // Phase 1: exact OID match (merge.c:1574).
        MergeMarkSimilarityExact(diffList, simOurs, simTheirs);

        // Phase 2: inexact similarity (merge.c:1577-1591). Only if
        // threshold < 100 (100 means exact-only) and the number of
        // conflicts is within the target limit.
        if (opts.RenameThreshold < 100 && conflicts.Count <= opts.TargetLimit)
        {
            await MergeMarkSimilarityInexactAsync(diffList, simOurs, simTheirs, opts, cancellationToken).ConfigureAwait(false);
        }

        // Phase 3: coalesce renames + classify conflict type
        // (merge.c:1596-1599).
        MergeCoalesceRenames(diffList, simOurs, simTheirs, opts);

        // Phase 4: remove fully-coalesced (empty) conflicts
        // (merge.c:1599, merge_diff_empty:1513-1522).
        diffList.Conflicts.RemoveAll(c =>
            !MergeEntryExists(c.AncestorEntry) &&
            !MergeEntryExists(c.OurEntry) &&
            !MergeEntryExists(c.TheirEntry));
    }

    /// <summary>
    /// Exact OID rename detection. Matches
    /// <c>merge_diff_mark_similarity_exact</c> (<c>merge.c:1209-1277</c>).
    /// Builds OID→conflict queues for deleted-on-ours and deleted-on-theirs
    /// entries, then matches added entries by OID.
    /// </summary>
    private static void MergeMarkSimilarityExact(
        MergeDiffList diffList,
        MergeDiffSimilarity[] simOurs,
        MergeDiffSimilarity[] simTheirs)
    {
        List<MergeDiff> conflicts = diffList.Conflicts;
        GitOid emptyBlob = diffList.Conflicts.Count > 0
            ? MergeGetEmptyBlobOid(conflicts[0].AncestorEntry.Id.Algorithm)
            : GitOid.EmptyBlobSha1;

        // Build OID → queue of source indices for ours-deletes and
        // theirs-deletes. Skip empty-blob OIDs (always match → false
        // matches). Matches merge.c:1220-1245.
        var oursDeletesByOid = new Dictionary<GitOid, Queue<int>>();
        var theirsDeletesByOid = new Dictionary<GitOid, Queue<int>>();

        for (int i = 0; i < conflicts.Count; i++)
        {
            MergeDiff src = conflicts[i];

            // Source must have an ancestor entry.
            if (!MergeEntryExists(src.AncestorEntry))
            {
                continue;
            }

            // Skip empty blobs (merge.c:1231-1232).
            if (src.AncestorEntry.Id == emptyBlob)
            {
                continue;
            }

            if (!MergeEntryExists(src.OurEntry))
            {
                MergeEnqueueDelete(oursDeletesByOid, src.AncestorEntry.Id, i);
            }

            if (!MergeEntryExists(src.TheirEntry))
            {
                MergeEnqueueDelete(theirsDeletesByOid, src.AncestorEntry.Id, i);
            }
        }

        // Match added entries to deleted-by-OID (merge.c:1247-1270).
        for (int j = 0; j < conflicts.Count; j++)
        {
            MergeDiff tgt = conflicts[j];

            // Target must NOT have an ancestor entry.
            if (MergeEntryExists(tgt.AncestorEntry))
            {
                continue;
            }

            if (MergeEntryExists(tgt.OurEntry))
            {
                if (MergeDequeueDelete(oursDeletesByOid, tgt.OurEntry.Id, out int i))
                {
                    simOurs[i].Similarity = 100;
                    simOurs[i].OtherIdx = j;
                    simOurs[j].Similarity = 100;
                    simOurs[j].OtherIdx = i;
                }
            }

            if (MergeEntryExists(tgt.TheirEntry))
            {
                if (MergeDequeueDelete(theirsDeletesByOid, tgt.TheirEntry.Id, out int i))
                {
                    simTheirs[i].Similarity = 100;
                    simTheirs[i].OtherIdx = j;
                    simTheirs[j].Similarity = 100;
                    simTheirs[j].OtherIdx = i;
                }
            }
        }
    }

    private static void MergeEnqueueDelete(
        Dictionary<GitOid, Queue<int>> map, GitOid oid, int idx)
    {
        if (!map.TryGetValue(oid, out Queue<int>? queue))
        {
            queue = new Queue<int>();
            queue.Enqueue(idx);
            map[oid] = queue;
        }
        else
        {
            queue.Enqueue(idx);
        }
    }

    private static bool MergeDequeueDelete(
        Dictionary<GitOid, Queue<int>> map, GitOid oid, out int idx)
    {
        if (map.TryGetValue(oid, out Queue<int>? queue) && queue.Count > 0)
        {
            idx = queue.Dequeue();
            return true;
        }

        idx = -1;
        return false;
    }

    /// <summary>
    /// Inexact content-similarity rename detection. Matches
    /// <c>merge_diff_mark_similarity_inexact</c> (<c>merge.c:1279-1356</c>).
    /// O(n²) comparison of source (ancestor exists, one side missing) vs
    /// target (no ancestor, that side present) using
    /// <see cref="SimilarityHash"/>.
    /// </summary>
    private async Task MergeMarkSimilarityInexactAsync(
        MergeDiffList diffList,
        MergeDiffSimilarity[] simOurs,
        MergeDiffSimilarity[] simTheirs,
        GitMergeOptions opts,
        CancellationToken cancellationToken)
    {
        List<MergeDiff> conflicts = diffList.Conflicts;
        int n = conflicts.Count;

        // Signature cache: 3 slots per conflict (ancestor, ours, theirs).
        // Matches merge.c:1578-1580 cache layout.
        var cache = new SimilarityHash?[n * 3];

        for (int i = 0; i < n; i++)
        {
            MergeDiff src = conflicts[i];

            // Source must have ancestor and be missing on at least one side
            // (merge.c:1294-1297).
            if (!MergeEntryExists(src.AncestorEntry))
            {
                continue;
            }

            if (MergeEntryExists(src.OurEntry) && MergeEntryExists(src.TheirEntry))
            {
                continue;
            }

            for (int j = 0; j < n; j++)
            {
                MergeDiff tgt = conflicts[j];

                // Target must not have an ancestor (merge.c:1303-1304).
                if (MergeEntryExists(tgt.AncestorEntry))
                {
                    continue;
                }

                int ourIdx = n + j;
                int theirIdx = (n * 2) + j;

                // Ours side: source missing ours, target has ours
                // (merge.c:1306-1329).
                if (MergeEntryExists(tgt.OurEntry) && !MergeEntryExists(src.OurEntry))
                {
                    int sim = await MergeIndexEntrySimilarityInexactAsync(
                        src.AncestorEntry, i, tgt.OurEntry, ourIdx,
                        cache, opts, cancellationToken).ConfigureAwait(false);

                    if (sim > simOurs[i].Similarity &&
                        sim > simOurs[j].Similarity)
                    {
                        // Clear previous best (merge.c:1318-1322).
                        if (simOurs[i].Similarity > 0)
                        {
                            simOurs[simOurs[i].OtherIdx].Similarity = 0;
                        }

                        if (simOurs[j].Similarity > 0)
                        {
                            simOurs[simOurs[j].OtherIdx].Similarity = 0;
                        }

                        simOurs[i].Similarity = sim;
                        simOurs[i].OtherIdx = j;
                        simOurs[j].Similarity = sim;
                        simOurs[j].OtherIdx = i;
                    }
                }

                // Theirs side: source missing theirs, target has theirs
                // (merge.c:1332-1351).
                if (MergeEntryExists(tgt.TheirEntry) && !MergeEntryExists(src.TheirEntry))
                {
                    int sim = await MergeIndexEntrySimilarityInexactAsync(
                        src.AncestorEntry, i, tgt.TheirEntry, theirIdx,
                        cache, opts, cancellationToken).ConfigureAwait(false);

                    if (sim > simTheirs[i].Similarity &&
                        sim > simTheirs[j].Similarity)
                    {
                        if (simTheirs[i].Similarity > 0)
                        {
                            simTheirs[simTheirs[i].OtherIdx].Similarity = 0;
                        }

                        if (simTheirs[j].Similarity > 0)
                        {
                            simTheirs[simTheirs[j].OtherIdx].Similarity = 0;
                        }

                        simTheirs[i].Similarity = sim;
                        simTheirs[i].OtherIdx = j;
                        simTheirs[j].Similarity = sim;
                        simTheirs[j].OtherIdx = i;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Computes the content similarity score (0-100) between two index
    /// entries using <see cref="SimilarityHash"/>. Matches
    /// <c>index_entry_similarity_inexact</c> + <c>index_entry_similarity_calc</c>
    /// (<c>merge.c:1056-1135</c>).
    /// </summary>
    private async Task<int> MergeIndexEntrySimilarityInexactAsync(
        GitIndexEntry a,
        int aIdx,
        GitIndexEntry b,
        int bIdx,
        SimilarityHash?[] cache,
        GitMergeOptions _,
        CancellationToken cancellationToken)
    {
        // Skip non-blob modes (merge.c:1112-1113).
        if (!GitFileModeUtils.IsBlob(a.Mode) || !GitFileModeUtils.IsBlob(b.Mode))
        {
            return 0;
        }

        // Compute/cache signatures (merge.c:1116-1118).
        SimilarityHash? sigA = await MergeComputeSimilarityHashAsync(a, aIdx, cache, cancellationToken).ConfigureAwait(false);
        SimilarityHash? sigB = await MergeComputeSimilarityHashAsync(b, bIdx, cache, cancellationToken).ConfigureAwait(false);

        // "Invalid" (too big/small) → score 0 (merge.c:1121-1122).
        if (sigA is null || sigB is null)
        {
            return 0;
        }

        // Compare signatures (merge.c:1125).
        int score = SimilarityHash.Compare(sigA, sigB);

        // Clip score (merge.c:1129-1132).
        return score < 0 ? 0 : score > 100 ? 100 : score;
    }

    /// <summary>
    /// Loads (or retrieves from cache) the <see cref="SimilarityHash"/> for
    /// an index entry's blob content. Matches <c>index_entry_similarity_calc</c>
    /// (<c>merge.c:1056-1098</c>).
    /// </summary>
    private async Task<SimilarityHash?> MergeComputeSimilarityHashAsync(
        GitIndexEntry entry,
        int idx,
        SimilarityHash?[] cache,
        CancellationToken cancellationToken)
    {
        if (cache[idx] is not null)
        {
            return cache[idx];
        }

        // Load the blob (merge.c:1074-1075).
        GitBlob? blob = await Objects.LookupAsync<GitBlob>(entry.Id, cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            return null;
        }

        using GitBlob _ = blob;
        ReadOnlyMemory<byte> content = blob.Content;

        // C (merge.c:1935): the internal metric payload is GIT_HASHSIG_SMART_WHITESPACE ONLY — the diff path adds ALLOW_SMALL_FILES (diff_tform.c:348), the
        // merge path does NOT. Signatures with < 4 buckets yield null → score 0 ("invalid marker"), so tiny files never match as renames.
        SimilarityHashOptions hashsigOpts = SimilarityHashOptions.SmartWhitespace;

        var sig = SimilarityHash.Create(content.Span, hashsigOpts);
        cache[idx] = sig;

        // null result (too small) → "invalid marker" → score 0.
        return sig;
    }

    /// <summary>
    /// Coalesces rename targets into their sources and classifies the conflict
    /// type. Matches <c>merge_diff_list_coalesce_renames</c>
    /// (<c>merge.c:1452-1511</c>) + <c>merge_diff_mark_rename_conflict</c>
    /// (<c>merge.c:1377-1436</c>).
    /// </summary>
    private static void MergeCoalesceRenames(
        MergeDiffList diffList,
        MergeDiffSimilarity[] simOurs,
        MergeDiffSimilarity[] simTheirs,
        GitMergeOptions opts)
    {
        List<MergeDiff> conflicts = diffList.Conflicts;

        for (int i = 0; i < conflicts.Count; i++)
        {
            MergeDiff target = conflicts[i];

            bool oursRenamed = false;
            bool theirsRenamed = false;
            int oursSourceIdx = 0;
            int theirsSourceIdx = 0;

            // Coalesce ours (merge.c:1469-1485).
            if (MergeEntryExists(target.OurEntry) &&
                simOurs[i].Similarity >= opts.RenameThreshold)
            {
                oursSourceIdx = simOurs[i].OtherIdx;
                MergeDiff source = conflicts[oursSourceIdx];

                MergeCoalesceRename(source, target, oursSide: true);

                simOurs[oursSourceIdx].Similarity = 0;
                simOurs[i].Similarity = 0;

                oursRenamed = true;
            }

            // Coalesce theirs (merge.c:1488-1504).
            if (MergeEntryExists(target.TheirEntry) &&
                simTheirs[i].Similarity >= opts.RenameThreshold)
            {
                theirsSourceIdx = simTheirs[i].OtherIdx;
                MergeDiff source = conflicts[theirsSourceIdx];

                MergeCoalesceRename(source, target, oursSide: false);

                simTheirs[theirsSourceIdx].Similarity = 0;
                simTheirs[i].Similarity = 0;

                theirsRenamed = true;
            }

            // Classify the rename conflict type
            // (merge.c:1506-1509, merge_diff_mark_rename_conflict).
            MergeMarkRenameConflict(
                diffList, simOurs, oursRenamed, oursSourceIdx,
                simTheirs, theirsRenamed, theirsSourceIdx,
                target, opts);
        }
    }

    /// <summary>
    /// Coalesces a rename target into the source: copies the target's entry
    /// into the source, marks source as Renamed, clears the target.
    /// Matches <c>merge_diff_coalesce_rename</c> (<c>merge.c:1438-1450</c>).
    /// </summary>
    private static void MergeCoalesceRename(
        MergeDiff source,
        MergeDiff target,
        bool oursSide)
    {
        if (oursSide)
        {
            source.OurEntry = target.OurEntry;
            source.OurStatus = GitDeltaStatus.Renamed;
            target.OurEntry = default;
            target.OurStatus = GitDeltaStatus.Unmodified;
        }
        else
        {
            source.TheirEntry = target.TheirEntry;
            source.TheirStatus = GitDeltaStatus.Renamed;
            target.TheirEntry = default;
            target.TheirStatus = GitDeltaStatus.Unmodified;
        }
    }

    /// <summary>
    /// Classifies a rename conflict's type. Matches
    /// <c>merge_diff_mark_rename_conflict</c> (<c>merge.c:1377-1436</c>).
    /// </summary>
    private static void MergeMarkRenameConflict(
        MergeDiffList diffList,
        MergeDiffSimilarity[] simOurs,
        bool oursRenamed,
        int oursSourceIdx,
        MergeDiffSimilarity[] simTheirs,
        bool theirsRenamed,
        int theirsSourceIdx,
        MergeDiff target,
        GitMergeOptions opts)
    {
        List<MergeDiff> conflicts = diffList.Conflicts;

        if (oursRenamed && theirsRenamed)
        {
            // Both renamed (merge.c:1397-1404).
            if (oursSourceIdx == theirsSourceIdx)
            {
                conflicts[oursSourceIdx].Type = MergeDiffType.BothRenamed;
            }
            else
            {
                conflicts[oursSourceIdx].Type = MergeDiffType.BothRenamed2To1;
                conflicts[theirsSourceIdx].Type = MergeDiffType.BothRenamed2To1;
            }
        }
        else if (oursRenamed)
        {
            MergeDiff oursSource = conflicts[oursSourceIdx];

            // If our source was also renamed in theirs → 1-to-2
            // (merge.c:1407-1408).
            if (simTheirs[oursSourceIdx].Similarity >= opts.RenameThreshold)
            {
                oursSource.Type = MergeDiffType.BothRenamed1To2;
            }
            else if (MergeEntryExists(target.TheirEntry))
            {
                // Their side has an entry at this path → rename+add
                // (merge.c:1410-1413).
                oursSource.Type = MergeDiffType.RenamedAdded;
                target.Type = MergeDiffType.RenamedAdded;
            }
            else if (!MergeEntryExists(oursSource.TheirEntry))
            {
                // Source has no their entry → rename+delete
                // (merge.c:1415-1416).
                oursSource.Type = MergeDiffType.RenamedDeleted;
            }
            else if (oursSource.Type == MergeDiffType.ModifiedDeleted)
            {
                // Was modify-delete → now rename+modify
                // (merge.c:1418-1419).
                oursSource.Type = MergeDiffType.RenamedModified;
            }
        }
        else if (theirsRenamed)
        {
            MergeDiff theirsSource = conflicts[theirsSourceIdx];

            // If their source was also renamed in ours → 1-to-2
            // (merge.c:1421-1423).
            if (simOurs[theirsSourceIdx].Similarity >= opts.RenameThreshold)
            {
                theirsSource.Type = MergeDiffType.BothRenamed1To2;
            }
            else if (MergeEntryExists(target.OurEntry))
            {
                // Our side has an entry at this path → rename+add
                // (merge.c:1425-1428).
                theirsSource.Type = MergeDiffType.RenamedAdded;
                target.Type = MergeDiffType.RenamedAdded;
            }
            else if (!MergeEntryExists(theirsSource.OurEntry))
            {
                // Source has no our entry → rename+delete
                // (merge.c:1430-1431).
                theirsSource.Type = MergeDiffType.RenamedDeleted;
            }
            else if (theirsSource.Type == MergeDiffType.ModifiedDeleted)
            {
                // Was modify-delete → now rename+modify
                // (merge.c:1433-1434).
                theirsSource.Type = MergeDiffType.RenamedModified;
            }
        }
    }

    // ── Private: conflict resolution ─────────────────────────────────────

    private static bool MergeResolveTrivial(MergeDiffList diffList, MergeDiff conflict)
    {
        // Matches merge_conflict_resolve_trivial (merge.c:659-742).
        if (conflict.Type is MergeDiffType.DirectoryFile or
            MergeDiffType.RenamedAdded)
        {
            return false;
        }

        if (conflict.OurStatus == GitDeltaStatus.Renamed ||
            conflict.TheirStatus == GitDeltaStatus.Renamed)
        {
            return false;
        }

        bool oursEmpty = !MergeEntryExists(conflict.OurEntry);
        bool theirsEmpty = !MergeEntryExists(conflict.TheirEntry);
        bool oursChanged = conflict.OurStatus != GitDeltaStatus.Unmodified;
        bool theirsChanged = conflict.TheirStatus != GitDeltaStatus.Unmodified;
        bool oursTheirsDiffer = oursChanged && theirsChanged &&
            !MergeIndexEntryEqual(conflict.OurEntry, conflict.TheirEntry);

        GitIndexEntry result;

        // 5ALT: ancest:*, head:head, remote:head = result:head
        if (oursChanged && !oursEmpty && !oursTheirsDiffer)
        {
            result = conflict.OurEntry;
        }
        // 6: ancest:ancest+, head:(empty), remote:(empty) = no merge
        else if (oursChanged && oursEmpty && theirsEmpty)
        {
            return false;
        }
        // 8: ancest:ancest^, head:(empty), remote:ancest = no merge
        else if (oursEmpty && !theirsChanged)
        {
            return false;
        }
        // 10: ancest:ancest^, head:ancest, remote:(empty) = no merge
        else if (!oursChanged && theirsEmpty)
        {
            return false;
        }
        // 13: ancest:ancest+, head:head, remote:ancest = result:head
        else if (oursChanged && !theirsChanged)
        {
            result = conflict.OurEntry;
        }
        // 14: ancest:ancest+, head:ancest, remote:remote = result:remote
        else if (!oursChanged && theirsChanged)
        {
            result = conflict.TheirEntry;
        }
        else
        {
            return false;
        }

        if (MergeEntryExists(result))
        {
            diffList.Staged.Add(result);
            return true;
        }

        return false;
    }

    private static bool MergeResolveOneRemoved(MergeDiffList diffList, MergeDiff conflict)
    {
        // Matches merge_conflict_resolve_one_removed (merge.c:745-784).
        if (conflict.Type is MergeDiffType.DirectoryFile or
            MergeDiffType.RenamedAdded)
        {
            return false;
        }

        bool oursEmpty = !MergeEntryExists(conflict.OurEntry);
        bool theirsEmpty = !MergeEntryExists(conflict.TheirEntry);
        bool oursChanged = conflict.OurStatus != GitDeltaStatus.Unmodified;
        bool theirsChanged = conflict.TheirStatus != GitDeltaStatus.Unmodified;

        // Removed in both
        if (oursChanged && oursEmpty && theirsEmpty)
        {
            diffList.Resolved.Add(conflict);
            return true;
        }

        // Removed in ours
        if (oursEmpty && !theirsChanged)
        {
            diffList.Resolved.Add(conflict);
            return true;
        }

        // Removed in theirs
        if (!oursChanged && theirsEmpty)
        {
            diffList.Resolved.Add(conflict);
            return true;
        }

        return false;
    }

    private static bool MergeResolveOneRenamed(MergeDiffList diffList, MergeDiff conflict)
    {
        // Matches merge_conflict_resolve_one_renamed (merge.c:786-848).
        if (!MergeEntryExists(conflict.OurEntry) || !MergeEntryExists(conflict.TheirEntry))
        {
            return false;
        }

        bool oursRenamed = conflict.OurStatus == GitDeltaStatus.Renamed;
        bool theirsRenamed = conflict.TheirStatus == GitDeltaStatus.Renamed;

        if (!oursRenamed && !theirsRenamed)
        {
            return false;
        }

        if (conflict.Type is MergeDiffType.BothRenamed2To1 or
            MergeDiffType.BothRenamed1To2 or
            MergeDiffType.RenamedAdded)
        {
            return false;
        }

        bool oursChanged = !conflict.AncestorEntry.Id.Equals(conflict.OurEntry.Id) ||
            conflict.AncestorEntry.Mode != conflict.OurEntry.Mode;
        bool theirsChanged = !conflict.AncestorEntry.Id.Equals(conflict.TheirEntry.Id) ||
            conflict.AncestorEntry.Mode != conflict.TheirEntry.Mode;

        // If both modified (and not to a common target) require a merge.
        if (oursChanged && theirsChanged &&
            !conflict.OurEntry.Id.Equals(conflict.TheirEntry.Id))
        {
            return false;
        }

        GitIndexEntry merged = oursChanged ? conflict.OurEntry : conflict.TheirEntry;
        merged = merged with { Path = oursRenamed ? conflict.OurEntry.Path : conflict.TheirEntry.Path };

        diffList.Staged.Add(merged);
        diffList.Resolved.Add(conflict);
        return true;
    }

    private async Task<bool> MergeResolveContentsAsync(
        MergeDiffList diffList,
        MergeDiff conflict,
        GitMergeOptions options,
        CancellationToken cancellationToken)
    {
        // Matches merge_conflict_resolve_contents (merge.c:928-1013) +
        // merge_conflict_invoke_driver (merge.c:887-926).
        if (!MergeCanResolveContents(conflict))
        {
            return false;
        }

        GitIndexEntry? ancestor = MergeEntryExists(conflict.AncestorEntry)
            ? conflict.AncestorEntry : null;
        GitIndexEntry? ours = MergeEntryExists(conflict.OurEntry)
            ? conflict.OurEntry : null;
        GitIndexEntry? theirs = MergeEntryExists(conflict.TheirEntry)
            ? conflict.TheirEntry : null;

        // Build the file-level merge options from the tree-level merge options.
        // Matches merge.c:2126-2127 (file_opts.favor = opts.file_favor,
        // file_opts.flags = opts.file_flags).
        GitMergeFileFlags fileFlags = options.FileFlags;
        string? ancestorLabel = null;
        string? ourLabel = null;
        string? theirLabel = null;
        ushort markerSize = 7; // GIT_MERGE_CONFLICT_MARKER_SIZE

        // When building a virtual base (recursive merge), set the git-inspired
        // labels and accept conflicts in the result. Matches merge.c:2130-2136.
        if ((options.Flags & GitMergeFlags.VirtualBase) != 0)
        {
            ancestorLabel = "merged common ancestors";
            ourLabel = "Temporary merge branch 1";
            theirLabel = "Temporary merge branch 2";
            fileFlags |= GitMergeFileFlags.AcceptConflicts;
            markerSize = 9; // GIT_MERGE_CONFLICT_MARKER_SIZE + 2
        }

        var fileOpts = new GitMergeFileOptions
        {
            Favor = options.Favor,
            Flags = fileFlags,
            AncestorLabel = ancestorLabel,
            OurLabel = ourLabel,
            TheirLabel = theirLabel,
            MarkerSize = markerSize,
        };

        // Build the driver source.
        var source = new GitMergeDriverSource(
            this,
            options.DefaultDriver,
            fileOpts,
            ancestor,
            ours,
            theirs);

        // If the user requested a particular favor (Ours/Theirs/Union), bypass
        // the gitattributes lookup and use the builtin text driver directly.
        // Matches merge.c:964-973.
        string driverName;
        IGitMergeDriver driver;
        if (options.Favor != GitMergeFileFavor.Normal)
        {
            driverName = GitMergeDriverRegistry.TextName;
            driver = Context.MergeDrivers.TextInstance;
        }
        else
        {
            // Find the merge driver for this file via gitattributes.
            // Matches merge.c:976 (git_merge_driver_for_source).
            (string? name, IGitMergeDriver? drv) = await Context.MergeDrivers.ForSourceAsync(source, cancellationToken).ConfigureAwait(false);
            driverName = name;

            // If no driver was found (neither named nor wildcard), fall back to
            // the text driver. Matches merge.c:979-980, 991-994 (fallback=true).
            driver = drv ?? Context.MergeDrivers.TextInstance;
        }

        // Invoke the driver. Matches merge.c:984 (merge_conflict_invoke_driver).
        (GitMergeDriverApplyResult applyResult, GitMergeDriverOutput? output) = await driver.ApplyAsync(driverName, source, cancellationToken).ConfigureAwait(false);

        // GIT_PASSTHROUGH → fall back to the text driver.
        // Matches merge.c:987-988.
        if (applyResult == GitMergeDriverApplyResult.PassThrough)
        {
            (applyResult, output) = await Context.MergeDrivers.TextInstance.ApplyAsync(
                GitMergeDriverRegistry.TextName, source, cancellationToken).ConfigureAwait(false);
        }

        // GIT_EMERGECONFLICT → leave as unresolved conflict.
        // Matches merge.c:996-1001 (error == GIT_EMERGECONFLICT → error = 0,
        // *resolved stays 0).
        if (applyResult == GitMergeDriverApplyResult.Conflict)
        {
            return false;
        }

        // Driver contract: a non-Conflict result always carries a valid output.
        // Matches merge.c:906 (the driver writes `merged` only on success).
        if (output is null)
        {
            throw new GitException(GitErrorCode.Error, "merge driver returned success but produced no output", GitErrorCategory.Merge);
        }

        // Write the merged blob to ODB. Matches merge.c:906
        // (git_odb_write in merge_conflict_invoke_driver).
        GitOid oid = await Objects.WriteAsync(GitObjectType.Blob, output.Content, cancellationToken).ConfigureAwait(false);

        // Determine the path and mode for the merged entry. The driver
        // already computed best-path/best-mode; fall back to the existing
        // MergeBestMode if the driver returned mode 0.
        GitPath path = output.Path ?? ours?.Path ?? theirs?.Path ?? ancestor?.Path ?? default;
        var mode = (GitFileMode)output.Mode;
        if (mode == 0)
        {
            mode = MergeBestMode(
                ancestor?.Mode ?? 0,
                ours?.Mode ?? 0,
                theirs?.Mode ?? 0);
        }

        var mergedEntry = new GitIndexEntry(path, oid, mode);
        diffList.Staged.Add(mergedEntry);
        diffList.Resolved.Add(conflict);
        return true;
    }

    private static bool MergeCanResolveContents(MergeDiff conflict)
    {
        // Matches merge_conflict_can_resolve_contents (merge.c:850-884).
        if (!MergeEntryExists(conflict.OurEntry) || !MergeEntryExists(conflict.TheirEntry))
        {
            return false;
        }

        // Reject D/F conflicts.
        if (conflict.Type == MergeDiffType.DirectoryFile)
        {
            return false;
        }

        // Reject submodules.
        if (conflict.AncestorEntry.Mode == GitFileMode.GitLink ||
            conflict.OurEntry.Mode == GitFileMode.GitLink ||
            conflict.TheirEntry.Mode == GitFileMode.GitLink)
        {
            return false;
        }

        // Reject link/file conflicts.
        bool ancestorIsLink = conflict.AncestorEntry.Mode == GitFileMode.Symlink;
        bool oursIsLink = conflict.OurEntry.Mode == GitFileMode.Symlink;
        bool theirsIsLink = conflict.TheirEntry.Mode == GitFileMode.Symlink;
        if ((ancestorIsLink ^ oursIsLink) || (ancestorIsLink ^ theirsIsLink))
        {
            return false;
        }

        // Reject name conflicts.
        if (conflict.Type is MergeDiffType.BothRenamed2To1 or
            MergeDiffType.RenamedAdded)
        {
            return false;
        }

        // Reject 1-to-2 renames: both sides renamed, but to different targets.
        // Matches merge.c:879-882.
        if (conflict.OurStatus == GitDeltaStatus.Renamed &&
            conflict.TheirStatus == GitDeltaStatus.Renamed &&
            conflict.AncestorEntry.Path != conflict.TheirEntry.Path)
        {
            return false;
        }

        return true;
    }

    private static GitFileMode MergeBestMode(GitFileMode ancestor, GitFileMode ours, GitFileMode theirs)
    {
        // Matches git_merge_file__best_mode (merge.h:184-206).
        if (ancestor == 0)
        {
            if (ours == GitFileMode.Executable || theirs == GitFileMode.Executable)
            {
                return GitFileMode.Executable;
            }

            return GitFileMode.Regular;
        }

        if (ours != 0 && theirs != 0)
        {
            if (ancestor == ours)
            {
                return theirs;
            }

            return ours;
        }

        return 0;
    }

    // ── Private: build output index ──────────────────────────────────────

    private static GitIndex MergeIndexFromDiffList(
        MergeDiffList diffList,
        GitHashAlgorithmKind oidType,
        bool skipReuc)
    {
        var index = GitIndex.New(oidType);

        // Fill staged entries (stage 0).
        foreach (GitIndexEntry entry in diffList.Staged)
        {
            index.Add(entry);
        }

        // Add conflicts (stages 1/2/3).
        foreach (MergeDiff conflict in diffList.Conflicts)
        {
            GitIndexEntry? ancestor = MergeEntryExists(conflict.AncestorEntry)
                ? conflict.AncestorEntry : null;
            GitIndexEntry? ours = MergeEntryExists(conflict.OurEntry)
                ? conflict.OurEntry : null;
            GitIndexEntry? theirs = MergeEntryExists(conflict.TheirEntry)
                ? conflict.TheirEntry : null;

            index.ConflictAdd(ancestor, ours, theirs);
        }

        // Add rename name entries. byte-domain end to end — C strcmps the raw index path bytes (merge.c:2061) and git_index_name_add copies them verbatim
        // (index.c:2164-2185); the GitPath overload of NameAdd keeps the bytes (the string bridge would decode-then-re-encode, corrupting non-UTF-8 paths).
        foreach (MergeDiff conflict in diffList.Conflicts)
        {
            if (!MergeEntryExists(conflict.AncestorEntry))
            {
                continue;
            }

            GitPath? ancestorPath = conflict.AncestorEntry.Path;
            GitPath? ourPath = MergeEntryExists(conflict.OurEntry) ? conflict.OurEntry.Path : null;
            GitPath? theirPath = MergeEntryExists(conflict.TheirEntry) ? conflict.TheirEntry.Path : null;

            if ((ourPath is not null && ancestorPath != ourPath) ||
                (theirPath is not null && ancestorPath != theirPath))
            {
                index.NameAdd(ancestorPath, ourPath, theirPath);
            }
        }

        // Add REUC entries for resolved conflicts.
        if (!skipReuc)
        {
            MergeUpdateReuc(index, diffList);
        }

        return index;
    }

    private static void MergeUpdateReuc(GitIndex index, MergeDiffList diffList)
    {
        // Matches index_update_reuc (merge.c:1971-2006).
        foreach (MergeDiff conflict in diffList.Resolved)
        {
            GitIndexEntry? ancestor = MergeEntryExists(conflict.AncestorEntry)
                ? conflict.AncestorEntry : null;
            GitIndexEntry? ours = MergeEntryExists(conflict.OurEntry)
                ? conflict.OurEntry : null;
            GitIndexEntry? theirs = MergeEntryExists(conflict.TheirEntry)
                ? conflict.TheirEntry : null;

            if (ancestor is not null)
            {
                MergeInsertReuc(index, 0, ancestor.Value);
            }

            if (ours is not null)
            {
                MergeInsertReuc(index, 1, ours.Value);
            }

            if (theirs is not null)
            {
                MergeInsertReuc(index, 2, theirs.Value);
            }
        }
    }

    private static void MergeInsertReuc(GitIndex index, int stage, in GitIndexEntry entry)
    {
        // Matches merge_index_insert_reuc (merge.c:1944-1969).
        GitIndexReucEntry? existing = index.ReucByPath(entry.Path);
        uint[] modes = new uint[3];
        var oids = new GitOid[3];

        if (existing is not null)
        {
            modes[0] = existing.Modes[0];
            modes[1] = existing.Modes[1];
            modes[2] = existing.Modes[2];
            oids[0] = existing.Oids[0];
            oids[1] = existing.Oids[1];
            oids[2] = existing.Oids[2];

            // Find and remove the existing REUC entry by path.
            for (int i = 0; i < index.ReucCount; i++)
            {
                if (index.ReucByIndex(i)?.Path == entry.Path)
                {
                    index.ReucRemove(i);
                    break;
                }
            }
        }

        modes[stage] = (uint)entry.Mode;
        oids[stage] = entry.Id;

        index.ReucAdd(
            entry.Path,
            modes[0], oids[0],
            modes[1], oids[1],
            modes[2], oids[2]);
    }
}
