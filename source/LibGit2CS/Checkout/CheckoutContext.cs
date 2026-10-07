// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;
using IndexTime = LibGit2CS.Index.IndexTime;

namespace LibGit2CS.Checkout;

/// <summary>
/// Internal checkout context. Holds all state for a single checkout
/// operation. Matches <c>checkout_data</c> in <c>checkout.c:51-77</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's checkout engine (checkout.c); coupling is inherent to the broad checkout surface, and GitIndexEntry.Path is a byte-faithful GitPath (a permanent type coupling).")]
internal sealed class CheckoutContext : IDisposable
{
    private readonly GitRepository _repo;
    private readonly GitCheckoutOptions _opts;
    // The repository's own index — the write target for checkout results
    // (stage-0 entries, conflict stages 1/2/3, removes). Mirrors libgit2's
    // data->index (checkout.c:2404: git_repository_index(&data->index, repo)).
    private readonly GitIndex _index;
    // The checkout TARGET — the index whose contents are diffed against the
    // baseline and written to the workdir. For git_checkout_index without an
    // explicit index, this IS _index. For stash/merge callers that pass a
    // standalone merged index, this differs from _index. Mirrors libgit2's
    // target iterator index (git_iterator_index(data->target)).
    private readonly GitIndex _targetIndex;
    private readonly WorkdirWriter _writer;
    private GitCheckoutPerformance _perf;
    private readonly List<CheckoutEntryAction> _actions = [];
    // Paths whose conflict entries ExecuteRemoveConflicts drops. Mirror of
    // libgit2's remove_conflicts vector (checkout_data.remove_conflicts,
    // checkout.c:60), populated by checkout_get_remove_conflicts
    // (checkout.c:1277-1286). For tree-target checkouts this holds EVERY
    // conflict path (libgit2 removes them all unconditionally — see
    // checkout_remove_conflicts, checkout.c:2279-2289 — because
    // update_conflicts is empty and nothing re-adds). For index-target
    // checkouts this stays empty: both-deleted conflicts are removed
    // per-record via the _updateConflicts pass below (matching the C flow
    // where checkout_conflict_update_index re-adds only the non-deleted
    // stages after checkout_remove_conflicts clears them).
    private readonly List<GitPath> _removeConflicts = [];
    // the conflict
    // passes share the compiled checkout pathspec (C passes the same vector
    // to checkout_conflicts_foreach from both get_remove_conflicts and
    // get_update_conflicts, checkout.c:1383-1384). Null when no pathspec was
    // given — git_pathspec__match treats an empty vector as matching
    // everything (pathspec.c).
    private GitPathSpec? _compiledPathspec;
    private bool _disablePathspecMatch;
    private bool _conflictIgnoreCase;
    // Full conflict records that drive marker / side writing in
    // ExecuteCreateConflictsAsync. Mirror of libgit2's update_conflicts vector
    // (checkout_data.update_conflicts, checkout.c:62), populated by
    // checkout_get_update_conflicts (checkout.c:1233-1250). The load is gated
    // on the checkout target being an index — the direct port of
    // checkout_conflicts_load (checkout.c:974-991: "Only write conflicts from
    // sources that have them: indexes" via git_iterator_index(data->target)==NULL).
    // Empty for tree targets (git_checkout_tree / git_checkout_head / hard
    // reset), so conflict markers are never written to the workdir during a
    // tree checkout.
    private readonly List<CheckoutConflictData> _updateConflicts = [];
    private readonly List<GitPath> _removes = [];
    private long _totalSteps;
    private long _completedSteps;
    private readonly GitCheckoutStrategy _strategy;
    private readonly bool _canSymlink;
    private readonly bool _respectFilemode;
    private readonly GitConflictStyle _conflictStyle;
    // should_remove_existing (core.ignorecase && !DONT_REMOVE_EXISTING,
    // checkout.c:1407-1417), shared with the WorkdirWriter and used by
    // the submodule pass's mkdir.
    private readonly bool _shouldRemoveExisting;
    // True when the checkout target is an index (git_checkout_index — used by
    // merge/cherry-pick/revert/stash); false for tree targets
    // (git_checkout_tree / git_checkout_head / hard reset). Set at the top of
    // RunIndexAsync / RunTreeAsync respectively.
    private bool _targetIsIndex;

    private CheckoutContext(GitRepository repo, GitCheckoutOptions opts, GitIndex index, GitIndex targetIndex, bool canSymlink, bool respectFilemode, bool shouldRemoveExisting, GitConflictStyle conflictStyle)
    {
        _repo = repo;
        _opts = opts;
        _index = index;
        _targetIndex = targetIndex;
        _strategy = opts.Strategy;
        _perf = new GitCheckoutPerformance();
        _shouldRemoveExisting = shouldRemoveExisting;

        // If FORCE is set, also set RECREATE_MISSING.
        if ((_strategy & GitCheckoutStrategy.Force) != 0)
        {
            _strategy |= GitCheckoutStrategy.RecreateMissing;
        }

        // C sets
        // RECREATE_MISSING when the repo index is not on disk — an initial
        // checkout (checkout.c:2451-2454: `if (!data->index->on_disk)`). Without
        // this, a repo with a live HEAD but a deleted .git/index would classify
        // every file as DELETED and remove the workdir on CheckoutIndexAsync.
        if (index.IndexPath is null || !File.Exists(index.IndexPath))
        {
            _strategy |= GitCheckoutStrategy.RecreateMissing;
        }

        _canSymlink = canSymlink;
        _respectFilemode = respectFilemode;
        _conflictStyle = conflictStyle;

        // Checkout strategy flags override the config conflict style.
        // Matches git_checkout_options.checkout_strategy which includes
        // GIT_CHECKOUT_CONFLICT_STYLE_DIFF3 / _ZDIFF3.
        if ((_strategy & GitCheckoutStrategy.ConflictStyleZdiff3) != 0)
        {
            _conflictStyle = GitConflictStyle.ZealousDiff3;
        }
        else if ((_strategy & GitCheckoutStrategy.ConflictStyleDiff3) != 0)
        {
            _conflictStyle = GitConflictStyle.Diff3;
        }

        _writer = new WorkdirWriter(repo, opts, respectFilemode, shouldRemoveExisting, (opts.Strategy & GitCheckoutStrategy.SkipLockedDirectories) != 0, ref _perf);
    }

    /// <summary>
    /// Creates a checkout context, loading the index and reading config
    /// asynchronously. Matches the C <c>checkout_data_init</c> which reads
    /// <c>core.symlinks</c>, <c>core.filemode</c>, and <c>merge.conflictstyle</c>
    /// from the repository config.
    /// </summary>
    internal static async ValueTask<CheckoutContext> CreateAsync(GitRepository repo, GitCheckoutOptions opts, CancellationToken cancellationToken = default)
    {
        GitIndex index = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        // C (checkout.c:2468-2473, checkout_data_init): the SYMLINKS/FILEMODE
        // configmap lookups are plain bool parses whose defaults are
        // GIT_SYMLINKS_DEFAULT/GIT_FILEMODE_DEFAULT = GIT_CONFIGMAP_TRUE
        // (repository.h:95,99); an unparseable value FAILS the checkout.
        bool canSymlink = await repo.Config.GetConfigmapBoolOrThrowAsync("core.symlinks", true, cancellationToken).ConfigureAwait(false);
        bool respectFilemode = await repo.Config.GetConfigmapBoolOrThrowAsync("core.filemode", true, cancellationToken).ConfigureAwait(false);
        bool shouldRemoveExisting = await ResolveShouldRemoveExistingAsync(repo, opts, cancellationToken).ConfigureAwait(false);
        GitConflictStyle conflictStyle = await ResolveConflictStyleAsync(repo, opts, cancellationToken).ConfigureAwait(false);
        return new CheckoutContext(repo, opts, index, index, canSymlink, respectFilemode, shouldRemoveExisting, conflictStyle);
    }

    /// <summary>
    /// Creates a checkout context using an explicit target index (e.g. for
    /// <c>Checkout.Index(repo, index, opts)</c>). The explicit index is the
    /// diff/checkout TARGET; the repo's own index (loaded separately) is the
    /// write target for results. Mirrors libgit2's <c>git_checkout_index</c>
    /// where <c>data->index</c> (repo's index) is distinct from the target
    /// iterator's index.
    /// </summary>
    internal static async ValueTask<CheckoutContext> CreateAsync(GitRepository repo, GitCheckoutOptions opts, GitIndex index, CancellationToken cancellationToken = default)
    {
        GitIndex repoIndex = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        // C (checkout.c:2468-2473): bool configmap lookups with
        // GIT_CONFIGMAP_TRUE defaults; unparseable values fail the checkout.
        bool canSymlink = await repo.Config.GetConfigmapBoolOrThrowAsync("core.symlinks", true, cancellationToken).ConfigureAwait(false);
        bool respectFilemode = await repo.Config.GetConfigmapBoolOrThrowAsync("core.filemode", true, cancellationToken).ConfigureAwait(false);
        bool shouldRemoveExisting = await ResolveShouldRemoveExistingAsync(repo, opts, cancellationToken).ConfigureAwait(false);
        GitConflictStyle conflictStyle = await ResolveConflictStyleAsync(repo, opts, cancellationToken).ConfigureAwait(false);
        return new CheckoutContext(repo, opts, repoIndex, index, canSymlink, respectFilemode, shouldRemoveExisting, conflictStyle);
    }

    /// <summary>
    /// Port of C's <c>should_remove_existing</c> (checkout.c:1407-1417):
    /// <c>core.ignorecase</c> enabled and <c>GIT_CHECKOUT_DONT_REMOVE_EXISTING</c>
    /// not set. On lookup failure C defaults ignorecase to 0 (the lookup is
    /// not error-propagating), matching <see cref="LibGit2CS.Config.GitConfiguration.TryGetConfigmapBoolAsync"/>
    /// returning null → false, as in
    /// libgit2 1.9.4.
    /// </summary>
    private static async ValueTask<bool> ResolveShouldRemoveExistingAsync(GitRepository repo, GitCheckoutOptions opts, CancellationToken cancellationToken)
    {
        bool? ignorecase = await repo.Config.TryGetConfigmapBoolAsync("core.ignorecase", false, cancellationToken).ConfigureAwait(false);
        return (ignorecase ?? false) && (opts.Strategy & GitCheckoutStrategy.DontRemoveExisting) == 0;
    }

    /// <summary>
    /// Resolves the conflict marker style from <c>merge.conflictstyle</c>.
    /// Matches <c>checkout_data_init</c> (checkout.c:2495-2520): the config
    /// value is read ONLY when neither <c>GIT_CHECKOUT_CONFLICT_STYLE_MERGE</c>
    /// nor <c>GIT_CHECKOUT_CONFLICT_STYLE_DIFF3</c> is set in the strategy —
    /// an explicit strategy bit wins over the config (the ctor applies the
    /// ZDIFF3/DIFF3 strategy overrides afterwards).
    /// </summary>
    private static async ValueTask<GitConflictStyle> ResolveConflictStyleAsync(
        GitRepository repo, GitCheckoutOptions opts, CancellationToken cancellationToken)
    {
        if ((opts.Strategy & (GitCheckoutStrategy.ConflictStyleMerge | GitCheckoutStrategy.ConflictStyleDiff3)) != 0)
        {
            return GitConflictStyle.Merge;
        }

        // C (checkout.c:2500-2527): the value is matched with plain strcmp — case-SENSITIVE — and an unknown style fails the checkout with "unknown style '%s'
        // given for 'merge.conflictstyle'" (GIT_ERROR_CHECKOUT, -1). The compare is byte-domain (C's strcmp over the raw value bytes).
        GitConfigEntry? entry = await repo.Config.GetEntryAsync("merge.conflictstyle", cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.Value.ValueBytes is not { } valueBytes)
        {
            return GitConflictStyle.Merge;
        }

        if (ConfigKeyName.AsciiEquals(valueBytes.Span, "merge"))
        {
            return GitConflictStyle.Merge;
        }

        if (ConfigKeyName.AsciiEquals(valueBytes.Span, "diff3"))
        {
            return GitConflictStyle.Diff3;
        }

        if (ConfigKeyName.AsciiEquals(valueBytes.Span, "zdiff3"))
        {
            return GitConflictStyle.ZealousDiff3;
        }

        throw new GitException(
            GitErrorCode.Error,
            $"unknown style '{Encoding.UTF8.GetString(valueBytes.Span)}' given for 'merge.conflictstyle'",
            GitErrorCategory.Checkout);
    }

    internal GitCheckoutPerformance Performance => _perf;

    /// <summary>
    /// The filesystem root all workdir writes are constructed against.
    /// Matches libgit2's <c>data->opts.target_directory</c> (defaults to
    /// <c>repo->workdir</c> when null — checkout.c:2399-2401). Exposed so
    /// dirty-detection and the workdir iterator use the same root as the
    /// writer.
    /// </summary>
    internal string? WorkdirRoot => _writer.WorkdirRoot;

    /// <summary>
    /// Runs the full checkout pipeline against a target tree.
    /// Matches <c>git_checkout_iterator</c> (checkout.c:2555-2696).
    /// </summary>
    internal async Task RunTreeAsync(GitTree? targetTree, CancellationToken cancellationToken = default)
    {
        // Tree-target checkout (git_checkout_tree / git_checkout_head / hard
        // reset): update_conflicts must stay empty (checkout_conflicts_load,
        // checkout.c:974-991, returns early when git_iterator_index(target) is
        // NULL), so no conflict markers are written. _removeConflicts takes
        // every conflict path instead.
        _targetIsIndex = false;

        // Matches checkout_data_init (checkout.c:2406-2442): unless
        // NO_REFRESH, the repo index is refreshed from disk for TREE-target
        // checkouts. Non-FORCE first aborts on unresolved conflicts
        // (GIT_ECONFLICT), then both paths run git_index_read_safely — with
        // the unsaved-safety flag off (index.c:123) that is a checksum-gated
        // re-read that keeps in-memory entries when the file is unchanged —
        // followed by NAME/REUC clears (checkout.c:2440-2441).
        if ((_strategy & GitCheckoutStrategy.NoRefresh) == 0)
        {
            if ((_strategy & GitCheckoutStrategy.Force) == 0
                && _index.HasConflicts)
            {
                throw new GitException(
                    GitErrorCode.Conflict,
                    "unresolved conflicts exist in the index",
                    GitErrorCategory.Checkout);
            }

            await _repo.RefreshIndexAsync(cancellationToken).ConfigureAwait(false);
            _index.NameClear();
            _index.ReucClear();
        }

        // Resolve baseline (default: HEAD tree, or user-provided).
        // Matches checkout.c:2476-2493: if there is no index file on disk,
        // this is an initial checkout — use an empty baseline so that all
        // target entries are treated as new and created in the workdir.
        // C (checkout.c:2476-2484): with no explicit baseline/baseline_index,
        // the baseline is the HEAD tree whenever the index is on disk (or an
        // empty baseline for an initial checkout). It is NEVER the working
        // index — a staged change must not distort the checkout diff.
        GitTree? baseline = _opts.Baseline;
        if (baseline is null && _opts.BaselineIndex is null)
        {
            bool indexOnDisk = !string.IsNullOrEmpty(_index.IndexPath)
                && File.Exists(_index.IndexPath);
            if (indexOnDisk)
            {
                baseline = await ResolveHeadTreeAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Generate baseline→target diff with INCLUDE_UNMODIFIED.
        // C (checkout.c:2574-2583): the checkout diff includes IGNORED,
        // TYPECHANGE_TREES, SKIP_BINARY_CHECK and CASECHANGE on top of the
        // baseline/untracked/typechange set (UNREADABLE is the remaining
        // gap — the port's workdir iterator does not produce UNREADABLE
        // deltas yet).
        var diffOpts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUnmodified
                  | GitDiffOptionsFlags.IncludeUntracked
                  | GitDiffOptionsFlags.IncludeTypechange
                  | GitDiffOptionsFlags.RecurseUntrackedDirs
                  | GitDiffOptionsFlags.IncludeIgnored
                  | GitDiffOptionsFlags.IncludeTypechangeTrees
                  | GitDiffOptionsFlags.SkipBinaryCheck
                  | GitDiffOptionsFlags.IncludeCasechange,
        };

        // C (checkout.c:2584-2585): DISABLE_PATHSPEC_MATCH is propagated to the checkout diff flags — a glob-looking pathspec is matched literally in delta
        // generation.
        if ((_strategy & GitCheckoutStrategy.DisablePathSpecMatch) != 0)
        {
            diffOpts = diffOpts with { Flags = diffOpts.Flags | GitDiffOptionsFlags.DisablePathspecMatch };
        }

        if (_opts.Paths is not null && _opts.Paths.Length > 0)
        {
            diffOpts = diffOpts with { PathSpecs = _opts.Paths };
        }

        DiffGenerator diff;
        var iterOpts = new IteratorOptions();

        if (_opts.BaselineIndex is not null)
        {
            // Baseline is an explicit index (e.g. stash apply).
            using IIterator baselineIter = IndexIterator.ForIndex(_opts.BaselineIndex, _repo, iterOpts);
            using IIterator targetIter = TreeIterator.ForTree(targetTree, _repo, iterOpts);
            diff = await DiffGenerator.GenerateAsync(_repo, baselineIter, targetIter, diffOpts, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Baseline is the resolved HEAD tree, an explicit baseline tree,
            // or an EMPTY baseline (initial checkout / unborn HEAD) — exactly
            // like C's git_checkout_iterator (checkout.c:2607-2630):
            // git_iterator_for_tree(NULL) yields an empty iterator.
            using IIterator baselineIter = TreeIterator.ForTree(baseline, _repo, iterOpts);
            using IIterator targetIter = TreeIterator.ForTree(targetTree, _repo, iterOpts);
            diff = await DiffGenerator.GenerateAsync(_repo, baselineIter, targetIter, diffOpts, cancellationToken).ConfigureAwait(false);
        }

        // Build a workdir iterator for the lockstep walk (matches
        // git_checkout_iterator at checkout.c:2595-2605 which builds a workdir
        // iterator alongside the baseline→target diff). This is what lets
        // checkout find and remove untracked/ignored files that are not in the
        // baseline→target diff. DontAutoexpand matches the C flag so directory
        // entries surface as trees (we descend explicitly via AdvanceInto).
        // The iterator is rooted at the same root the writer writes to —
        // either TargetDirectory (when set, matches checkout.c:2601) or the
        // repo workdir.
        IIterator? workdirIter = null;
        if (WorkdirRoot is not null)
        {
            workdirIter = await FilesystemIterator.ForWorkdirAsync(_repo, _index, targetTree, WorkdirRoot, new IteratorOptions
            {
                Flags = IteratorFlags.DontAutoexpand,
            }, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Walk diff + workdir to determine actions.
            await DetermineActionsAsync(diff, workdirIter, cancellationToken).ConfigureAwait(false);

            if (_opts.IsDryRun)
            {
                return;
            }

            // SAFE strategy (default): if any action was classified as a
            // conflict and AllowConflicts is not set, abort with
            // GIT_ECONFLICT before any filesystem mutation. Matches
            // libgit2's checkout_get_actions (checkout.c:1372-1380):
            //   if (counts[CHECKOUT_ACTION__CONFLICT] > 0 &&
            //       (data->strategy & GIT_CHECKOUT_ALLOW_CONFLICTS) == 0) {
            //       error = GIT_ECONFLICT; goto fail; }
            ThrowIfConflicts();

            // Count total steps. Matches checkout.c:1356-1364 (per-delta
            // counts: REMOVE and UPDATE_BLOB are incremented separately, so a
            // REMOVE_AND_UPDATE action contributes 2; workdir-only removals
            // add to REMOVE) and checkout.c:2643-2647 (the total also
            // includes the REMOVE_CONFLICT and UPDATE_CONFLICT passes).
            _totalSteps = CountTotalSteps();

            // Establish the 0 baseline (matches report_progress(&data, NULL),
            // checkout.c:2649).
            ReportProgress(default);

            // Execute in passes: remove, remove conflicts, create, create conflicts.
            ExecuteRemoves();
            ExecuteRemoveConflicts();
            await ExecuteCreatesAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteCreateConflictsAsync(cancellationToken).ConfigureAwait(false);

            // Update index extensions from target index (if applicable).
            // Write the index unless DontWriteIndex (or it's an in-memory index).
            if (!_opts.DontWriteIndex && _index.Owner is not null)
            {
                await _index.WriteAsync(cancellationToken).ConfigureAwait(false);
            }

            // Propagate the writer's accumulated perf counters back to the
            // context (the writer holds its own copy due to struct value
            // semantics — the `ref` param to the ctor only seeds it).
            _perf = _writer.Performance;

            // Report final perfdata.
            _opts.Perfdata?.Report(_perf);
        }
        finally
        {
            workdirIter?.Dispose();
        }
    }

    /// <summary>
    /// Runs checkout against the current index (checkout_index).
    /// </summary>
    internal async Task RunIndexAsync(CancellationToken cancellationToken = default)
    {
        // Index-target checkout (git_checkout_index — merge/cherry-pick/
        // revert/stash): the target carries an index, so update_conflicts is
        // populated (checkout_conflicts_load, checkout.c:974-991) and conflict
        // markers may be written to the workdir. _removeConflicts stays empty;
        // removal is derived from _updateConflicts' both-deleted entries.
        _targetIsIndex = true;

        // Matches checkout_data_init (checkout.c:2406-2442): when the checkout
        // target is an index different from the repository's own, the repo
        // index is refreshed unless NO_REFRESH — a conflicted repo index
        // aborts with GIT_ECONFLICT first (non-FORCE), then
        // git_index_read_safely (checksum-gated re-read; keeps in-memory
        // entries when the file is unchanged — unsaved-safety is off by
        // default, index.c:123) followed by NAME/REUC clears
        // (checkout.c:2440-2441).
        if (_index != _targetIndex
            && (_strategy & GitCheckoutStrategy.NoRefresh) == 0)
        {
            if ((_strategy & GitCheckoutStrategy.Force) == 0
                && _index.HasConflicts)
            {
                throw new GitException(
                    GitErrorCode.Conflict,
                    "unresolved conflicts exist in the index",
                    GitErrorCategory.Checkout);
            }

            await _repo.RefreshIndexAsync(cancellationToken).ConfigureAwait(false);
            _index.NameClear();
            _index.ReucClear();
        }

        // For checkout_index, the target is the index itself.
        // The baseline is HEAD tree (or BaselineIndex if provided).
        // We diff baseline→index and then apply the resulting entries to the workdir.
        GitTree? baseline = _opts.Baseline;
        if (baseline is null && _opts.BaselineIndex is null)
        {
            // C resolves the HEAD baseline only when the repo index is
            // on disk (checkout.c:2476-2483, `if (data->index->on_disk)`) —
            // a missing index file means initial checkout against an EMPTY
            // baseline. Resolving HEAD unconditionally would diff HEAD→empty
            // index for a deleted .git/index with a live HEAD, classify every
            // file DELETED, and REMOVE the workdir files.
            string? indexPath = _index.IndexPath;
            if (indexPath is not null && File.Exists(indexPath))
            {
                baseline = await ResolveHeadTreeAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // C (checkout.c:2574-2583): the checkout diff flags (see
        // RunTreeAsync for the full set).
        var diffOpts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUnmodified
                  | GitDiffOptionsFlags.IncludeUntracked
                  | GitDiffOptionsFlags.IncludeTypechange
                  | GitDiffOptionsFlags.RecurseUntrackedDirs
                  | GitDiffOptionsFlags.IncludeIgnored
                  | GitDiffOptionsFlags.IncludeTypechangeTrees
                  | GitDiffOptionsFlags.SkipBinaryCheck
                  | GitDiffOptionsFlags.IncludeCasechange,
        };

        // C (checkout.c:2584-2585): DISABLE_PATHSPEC_MATCH is propagated to the checkout diff flags — a glob-looking pathspec is matched literally in delta
        // generation.
        if ((_strategy & GitCheckoutStrategy.DisablePathSpecMatch) != 0)
        {
            diffOpts = diffOpts with { Flags = diffOpts.Flags | GitDiffOptionsFlags.DisablePathspecMatch };
        }

        if (_opts.Paths is not null && _opts.Paths.Length > 0)
        {
            diffOpts = diffOpts with { PathSpecs = _opts.Paths };
        }

        DiffGenerator diff;
        var iterOpts = new IteratorOptions();

        if (_opts.BaselineIndex is not null)
        {
            // Baseline is an explicit index (e.g. stash apply).
            using IIterator baselineIter = IndexIterator.ForIndex(_opts.BaselineIndex, _repo, iterOpts);
            using IIterator targetIter = IndexIterator.ForIndex(_targetIndex, _repo, iterOpts);
            diff = await DiffGenerator.GenerateAsync(_repo, baselineIter, targetIter, diffOpts, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Build the diff with checkout-local iterator options (no
            // INCLUDE_CONFLICTS) — matches C's git_checkout_index, which
            // creates its own iterators without GIT_ITERATOR_INCLUDE_CONFLICTS
            // (checkout.c:2605-2630). DiffGenerator.TreeToIndexAsync hardcodes
            // IncludeConflicts for status/diff consumers; using it here would
            // make a conflicted index produce CONFLICTED deltas that trigger
            // ThrowIfConflicts during rebase merge checkout.
            using IIterator baselineIter = TreeIterator.ForTree(baseline, _repo, iterOpts);
            using IIterator targetIter = IndexIterator.ForIndex(_targetIndex, _repo, iterOpts);
            diff = await DiffGenerator.GenerateAsync(_repo, baselineIter, targetIter, diffOpts, cancellationToken).ConfigureAwait(false);
        }

        // Build a workdir iterator for the lockstep walk (matches RunTree),
        // rooted at the writer's workdir root (TargetDirectory when set).
        IIterator? workdirIter = null;
        if (WorkdirRoot is not null)
        {
            workdirIter = await FilesystemIterator.ForWorkdirAsync(_repo, _index, baseline, WorkdirRoot, new IteratorOptions
            {
                Flags = IteratorFlags.DontAutoexpand,
            }, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await DetermineActionsAsync(diff, workdirIter, cancellationToken).ConfigureAwait(false);

            if (_opts.IsDryRun)
            {
                return;
            }

            // SAFE: abort on conflicts before any write (see RunTreeAsync).
            ThrowIfConflicts();

            // Count total steps (see RunTreeAsync).
            _totalSteps = CountTotalSteps();

            // Establish the 0 baseline (matches report_progress(&data, NULL),
            // checkout.c:2649).
            ReportProgress(default);

            ExecuteRemoves();
            ExecuteRemoveConflicts();
            await ExecuteCreatesAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteCreateConflictsAsync(cancellationToken).ConfigureAwait(false);

            // Copy REUC and NAME extensions from the target index. Matches
            // checkout_extensions_update_index (checkout.c:2294-2324), called
            // from git_checkout_iterator when the target is a different index
            // (checkout.c:2674-2676), gated on !UPDATE_ONLY.
            if ((_strategy & GitCheckoutStrategy.UpdateOnly) == 0 && _index != _targetIndex)
            {
                foreach (GitIndexReucEntry reuc in _targetIndex.ReucEntries)
                {
                    _index.ReucAdd(reuc.Path, reuc.Modes[0], reuc.Oids[0], reuc.Modes[1], reuc.Oids[1], reuc.Modes[2], reuc.Oids[2]);
                }

                foreach (GitIndexNameEntry name in _targetIndex.NameEntries)
                {
                    _index.NameAdd(name.Ancestor, name.Ours, name.Theirs);
                }
            }

            if (!_opts.DontWriteIndex && _index.Owner is not null)
            {
                await _index.WriteAsync(cancellationToken).ConfigureAwait(false);
            }

            // Propagate the writer's accumulated perf counters back to the
            // context (the writer holds its own copy due to struct value
            // semantics — the `ref` param to the ctor only seeds it).
            _perf = _writer.Performance;

            _opts.Perfdata?.Report(_perf);
        }
        finally
        {
            workdirIter?.Dispose();
        }
    }

    private async Task<GitTree?> ResolveHeadTreeAsync(CancellationToken cancellationToken)
    {
        GitReference? head = await _repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        GitOid targetOid;
        if (head is GitDirectReference dr)
        {
            targetOid = dr.Target;
        }
        else if (head is GitSymbolicReference sr)
        {
            GitReference? resolved = await sr.TargetAsync(cancellationToken).ConfigureAwait(false);
            if (resolved is GitDirectReference dt)
            {
                targetOid = dt.Target;
            }
            else
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        if (targetOid.IsZero)
        {
            return null;
        }

        Commit? commit = await _repo.Objects.LookupAsync<Commit>(targetOid, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            // HEAD resolved to a non-zero OID, but the commit object is absent
            // from the ODB. This is an inconsistent-DB state, not an unborn
            // branch (the unborn case returns earlier on the zero OID / null
            // HEAD / unresolved-symref paths). C's checkout_lookup_head_tree
            // (checkout.c:1928) propagates this as GIT_ENOTFOUND; only
            // GIT_EUNBORNBRANCH is swallowed by the caller (checkout.c:2486).
            throw new GitException(
                GitErrorCode.NotFound,
                $"HEAD points to commit {targetOid} but the object is not in the database",
                GitErrorCategory.Reference);
        }

        return await _repo.Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
    }

    // ── Action determination ────────────────────────────────────────────

    private async Task DetermineActionsAsync(DiffGenerator diff, IIterator? workdirIter, CancellationToken cancellationToken)
    {
        _actions.Clear();
        _removeConflicts.Clear();
        _updateConflicts.Clear();
        _removes.Clear();

        // Compile the checkout pathspec once (for the workdir-only walk).
        GitPathSpec? pathspec = null;
        bool disablePathspecMatch = (_strategy & GitCheckoutStrategy.DisablePathSpecMatch) != 0;
        if (_opts.Paths is { Length: > 0 } paths)
        {
            pathspec = GitPathSpec.New(paths);
        }

        bool ignoreCase = workdirIter?.IgnoreCase ?? false;

        // the conflict passes share the compiled pathspec (C passes
        // the same vector to checkout_conflicts_foreach via
        // checkout_get_remove_conflicts / checkout_get_update_conflicts,
        // checkout.c:1383-1384).
        _compiledPathspec = pathspec;
        _disablePathspecMatch = disablePathspecMatch;
        _conflictIgnoreCase = ignoreCase;

        // Pull the path comparators from the diff — faithful to libgit2's
        // checkout.c:678-679 (strcomp = data->diff->strcomp; pfxcomp =
        // data->diff->pfxcomp). The DiffGenerator slots are byte-wise
        // GitPath comparators (set once in SetIgnoreCase from core.ignorecase).
        // Reading the diff's slots (rather than re-deriving from
        // workdirIter.IgnoreCase) matches C — the `diff` parameter is the
        // baseline→target diff; its slots reflect the same core.ignorecase.
        Func<GitPath, GitPath, int> strcomp = diff._strcomp;
        Func<GitPath, GitPath, int> pfxcomp = diff._pfxcomp;

        // Prime the workdir iterator (fetch the first entry without advancing
        // past it — CurrentAsync does the initial Advance on first access).
        GitIndexEntry? wdEntry = workdirIter is not null
            ? await workdirIter.CurrentAsync(cancellationToken).ConfigureAwait(false)
            : null;

        foreach (GitDiffDelta delta in diff.Deltas)
        {
            GitPath path = delta.DeltaPath ?? default;
            // delta.OldFile.Path is byte-faithful GitPath?; use directly — no UTF-8 decode.
            GitPath oldPath = delta.OldFile.Path ?? default;

            // Mirrors libgit2's checkout_action loop (checkout.c:684-765) with
            // its six cases:
            //   1. wd before delta ("a/a" before "a/b")
            //   2. wd prefixes delta & should expand ("a/" before "a/b")
            //   3. wd prefixes delta & cannot expand ("a/b" before "a/b/c")
            //   4. wd equals delta ("a/b" and "a/b")
            //   5. wd after delta & delta prefixes wd ("a/b/c" after "a/b/" or "a/b")
            //   6. wd after delta ("a/c" after "a/b")
            // The loop breaks once the action for this delta is determined;
            // wdEntry/wdPath hold the iterator's current entry for the next
            // delta (advanced only where C advances).
            CheckoutEntryAction action;
            while (true)
            {
                if (wdEntry is null)
                {
                    // C: return checkout_action_no_wd(action, data, delta);
                    // (checkout.c:687-688) — the no_wd decision table is
                    // followed by checkout_action_common (checkout.c:324).
                    action = new CheckoutEntryAction { Path = path, Delta = delta };
                    DetermineActionNoWd(action, delta);
                    ApplyCommonAdjustments(action, delta, null);
                    break;
                }

                GitPath wdPath = wdEntry.Value.Path;
                int cmp = strcomp(wdPath, oldPath);

                if (cmp < 0)
                {
                    int pref = pfxcomp(oldPath, wdPath);
                    if (pref == 0)
                    {
                        if (wdEntry.Value.Mode == GitFileMode.Tree)
                        {
                            // case 2 — entry prefixed by workdir tree
                            // (checkout.c:703-710).
                            Debug.Assert(workdirIter is not null, "workdirIter is non-null when wdEntry is non-null");
                            await workdirIter.AdvanceIntoAsync(cancellationToken).ConfigureAwait(false);
                            wdEntry = await workdirIter.CurrentAsync(cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        // case 3 — wd contains non-dir where dir expected
                        // (checkout.c:712-718).
                        if (oldPath.Length > wdPath.Length && oldPath.Span[wdPath.Length] == (byte)'/')
                        {
                            action = new CheckoutEntryAction { Path = path, Delta = delta };
                            DetermineActionWithWdBlocker(action, delta, wdEntry.Value);
                            Debug.Assert(workdirIter is not null, "workdirIter is non-null when wdEntry is non-null");
                            wdEntry = await workdirIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        }
                    }

                    // case 1 — handle wd item (if it matches pathspec); the
                    // helper advances the iterator itself.
                    Debug.Assert(workdirIter is not null, "workdirIter is non-null when wdEntry is non-null");
                    await HandleWorkdirOnlyAsync(wdEntry.Value, workdirIter, pathspec, disablePathspecMatch, ignoreCase, pfxcomp, cancellationToken).ConfigureAwait(false);
                    wdEntry = await workdirIter.CurrentAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (cmp == 0)
                {
                    // case 4 — wd equals delta (checkout.c:728-733).
                    action = await DetermineActionForDeltaAsync(delta, path, wdEntry.Value, workdirIter, cancellationToken).ConfigureAwait(false);
                    Debug.Assert(workdirIter is not null, "workdirIter is non-null when wdEntry is non-null");
                    wdEntry = await workdirIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

                int pref5 = pfxcomp(wdPath, oldPath);
                if (pref5 == 0)
                {
                    // case 5 — delta path is a prefix of the wd entry
                    // (checkout.c:735-761).
                    if (wdPath.Length <= oldPath.Length || wdPath.Span[oldPath.Length] != (byte)'/')
                    {
                        // C: return checkout_action_no_wd(action, data, delta)
                        // (checkout.c:738-739).
                        action = new CheckoutEntryAction { Path = path, Delta = delta };
                        DetermineActionNoWd(action, delta);
                        ApplyCommonAdjustments(action, delta, null);
                        break;
                    }

                    if (delta.Status == GitDeltaStatus.Typechange)
                    {
                        if (delta.OldFile.Mode == GitFileMode.Tree)
                        {
                            action = await DetermineActionForDeltaAsync(delta, path, wdEntry.Value, workdirIter, cancellationToken).ConfigureAwait(false);
                            Debug.Assert(workdirIter is not null, "workdirIter is non-null when wdEntry is non-null");
                            wdEntry = await workdirIter.AdvanceIntoAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        if (delta.NewFile.Mode == GitFileMode.Tree
                            || delta.NewFile.Mode == GitFileMode.GitLink
                            || delta.OldFile.Mode == GitFileMode.GitLink)
                        {
                            action = await DetermineActionForDeltaAsync(delta, path, wdEntry.Value, workdirIter, cancellationToken).ConfigureAwait(false);
                            Debug.Assert(workdirIter is not null, "workdirIter is non-null when wdEntry is non-null");
                            wdEntry = await workdirIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        }
                    }

                    action = new CheckoutEntryAction { Path = path, Delta = delta };
                    if (IsEmptyDir(wdEntry.Value.Path))
                    {
                        // checkout_action_with_wd_dir_empty (checkout.c:655-667):
                        // run the no_wd action (which applies the common
                        // adjustments), then force REMOVE — an empty directory
                        // is always safe to remove.
                        DetermineActionNoWd(action, delta);
                        ApplyCommonAdjustments(action, delta, null);
                        if (action.Action != CheckoutAction.None)
                        {
                            action.Action |= CheckoutAction.Remove;
                        }
                    }
                    else
                    {
                        DetermineActionWithWdDir(action, delta, wdEntry.Value, workdirIter);
                    }

                    break;
                }

                // case 6 — wd is after delta (checkout.c:763-764).
                action = new CheckoutEntryAction { Path = path, Delta = delta };
                DetermineActionNoWd(action, delta);
                ApplyCommonAdjustments(action, delta, null);
                break;
            }

            // C checkout_verify_paths (checkout.c:1288-1310): every action's
            // path is validated with GIT_PATH_REJECT_WORKDIR_DEFAULTS before
            // it is queued — REMOVE validates the old path, everything else
            // validates the new path, so a crafted tree entry like "../evil"
            // or ".git" cannot escape the workdir.
            if (action.Action != CheckoutAction.None)
            {
                await VerifyActionPathsAsync(delta, action.Action, cancellationToken).ConfigureAwait(false);
            }

            if (action.Action != CheckoutAction.None)
            {
                _actions.Add(action);
            }
        }

        // Drain remaining workdir entries (all sort after the last delta).
        while (wdEntry is not null && workdirIter is not null)
        {
            await HandleWorkdirOnlyAsync(wdEntry.Value, workdirIter, pathspec, disablePathspecMatch, ignoreCase, pfxcomp, cancellationToken).ConfigureAwait(false);
            wdEntry = await workdirIter.CurrentAsync(cancellationToken).ConfigureAwait(false);
        }

        // Load conflict data: _removeConflicts (paths to drop, always —
        // tree targets) and _updateConflicts (records for marker writing,
        // index targets only — the port of checkout.c:979). Both empty for a
        // clean index.
        LoadRemoveConflicts();
        await LoadUpdateConflictsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of <c>checkout_verify_paths</c> (checkout.c:1288-1310): validates
    /// the delta paths an action will touch, with
    /// <c>GIT_PATH_REJECT_WORKDIR_DEFAULTS</c> (TRAVERSAL | BACKSLASH on
    /// Windows | DOT_GIT). REMOVE actions validate the old path; all other
    /// actions validate the new path, as in
    /// libgit2 1.9.4.
    /// </summary>
    private async ValueTask VerifyActionPathsAsync(GitDiffDelta delta, CheckoutAction action, CancellationToken cancellationToken)
    {
        if ((action & CheckoutAction.Remove) != 0)
        {
            GitPath oldPath = delta.OldFile.Path ?? default;
            if (!await GitPathValidator.IsValidAsync(oldPath, PathRejectPresets.WorkdirDefaults, _repo, (ushort)delta.OldFile.Mode, cancellationToken).ConfigureAwait(false))
            {
                throw new GitException(GitErrorCode.Error, $"cannot remove invalid path '{oldPath.ToUtf8String()}'", GitErrorCategory.Checkout);
            }
        }

        if ((action & ~CheckoutAction.Remove) != CheckoutAction.None)
        {
            GitPath newPath = delta.NewFile.Path ?? default;
            if (!await GitPathValidator.IsValidAsync(newPath, PathRejectPresets.WorkdirDefaults, _repo, (ushort)delta.NewFile.Mode, cancellationToken).ConfigureAwait(false))
            {
                throw new GitException(GitErrorCode.Error, $"cannot checkout to invalid path '{newPath.ToUtf8String()}'", GitErrorCategory.Checkout);
            }
        }
    }

    private async Task<CheckoutEntryAction> DetermineActionForDeltaAsync(GitDiffDelta delta, GitPath path, GitIndexEntry? wd, IIterator? workdirIter, CancellationToken cancellationToken)
    {
        var action = new CheckoutEntryAction { Path = path, Delta = delta };

        // The workdir iterator is the source of truth for workdir state, matching
        // libgit2's checkout_action (checkout.c:680-725) which dispatches to
        // checkout_action_no_wd when the iterator has no matching entry. When wd
        // is non-null, it carries the iterator's pre-stat'd mode/mtime/size.
        bool wdExists = wd is not null;

        // Check if workdir is modified (only when a workdir entry exists).
        bool wdModified = wdExists && await IsWorkdirModifiedAsync(delta, wd!.Value, cancellationToken).ConfigureAwait(false);

        if (!wdExists)
        {
            // No working directory entry.
            DetermineActionNoWd(action, delta);
        }
        else
        {
            // Working directory entry exists.
            await DetermineActionWithWdAsync(action, delta, wdModified, wd!.Value, workdirIter, cancellationToken).ConfigureAwait(false);
        }

        // Apply common action adjustments.
        ApplyCommonAdjustments(action, delta, wd);

        return action;
    }

    private void DetermineActionNoWd(CheckoutEntryAction action, GitDiffDelta delta)
    {
        // C short-
        // circuits every decision table on GIT_CHECKOUT_NONE (checkout.c:297,
        // 500, 572, 609) — a None-strategy checkout queues no actions and
        // writes nothing.
        if ((_strategy & GitCheckoutStrategy.None) != 0)
        {
            return;
        }

        switch (delta.Status)
        {
            case GitDeltaStatus.Unmodified:
                // File is missing from workdir but should be there.
                if (HasRecreateMissing())
                {
                    action.Action = CheckoutAction.UpdateBlob;
                }
                else
                {
                    action.Action = CheckoutAction.None;
                }
                Notify(GitCheckoutNotifyFlags.Dirty, delta.DeltaPath ?? default, delta.OldFile, delta.NewFile, null);
                break;

            case GitDeltaStatus.Added:
                action.Action = CheckoutAction.UpdateBlob;
                break;

            case GitDeltaStatus.Modified:
                action.Action = HasRecreateMissing()
                    ? CheckoutAction.UpdateBlob
                    : CheckoutAction.Conflict;
                break;

            case GitDeltaStatus.Typechange:
                if (delta.NewFile.Mode == GitFileMode.Tree)
                {
                    action.Action = CheckoutAction.UpdateBlob;
                }
                else
                {
                    action.Action = CheckoutAction.None;
                }
                break;

            case GitDeltaStatus.Deleted:
                action.Action = CheckoutAction.Remove;
                break;

            default:
                action.Action = CheckoutAction.None;
                break;
        }
    }

    private async Task DetermineActionWithWdAsync(CheckoutEntryAction action, GitDiffDelta delta, bool wdModified, GitIndexEntry wd, IIterator? workdirIter, CancellationToken cancellationToken)
    {
        // GIT_CHECKOUT_NONE short-circuit (checkout.c:500).
        if ((_strategy & GitCheckoutStrategy.None) != 0)
        {
            return;
        }

        switch (delta.Status)
        {
            case GitDeltaStatus.Unmodified:
                if (wdModified)
                {
                    // C (checkout.c:105-116): the notify workdir file comes from the workdir entry when one is available.
                    Notify(GitCheckoutNotifyFlags.Dirty, delta.DeltaPath ?? default, delta.OldFile, delta.NewFile, ToDiffFile(wd));
                    // C (checkout.c:503-510): a dirty workdir file whose
                    // baseline→target delta is UNMODIFIED is left alone —
                    // CHECKOUT_ACTION_IF(FORCE, UPDATE_BLOB, NONE). The SAFE
                    // checkout proceeds and preserves local edits.
                    action.Action = IsForce()
                        ? CheckoutAction.UpdateBlob
                        : CheckoutAction.None;
                }
                else
                {
                    action.Action = CheckoutAction.None;
                }
                break;

            case GitDeltaStatus.Added:
                // C (checkout.c:511-516): an IGNORED workdir file at an ADDED
                // path is overwritten (UPDATE_BLOB) unless
                // DONT_OVERWRITE_IGNORED is set — the default SAFE silently
                // overwrites it. Non-ignored files conflict unless FORCE.
                if (CurrentIsIgnored(workdirIter))
                {
                    action.Action = (_strategy & GitCheckoutStrategy.DontOverwriteIgnored) != 0
                        ? CheckoutAction.Conflict
                        : CheckoutAction.UpdateBlob;
                }
                else
                {
                    action.Action = IsForce()
                        ? CheckoutAction.UpdateBlob
                        : CheckoutAction.Conflict;
                }
                break;

            case GitDeltaStatus.Deleted:
                if (wdModified)
                {
                    action.Action = IsForce()
                        ? CheckoutAction.Remove
                        : CheckoutAction.Conflict;
                }
                else
                {
                    action.Action = CheckoutAction.Remove;
                }
                break;

            case GitDeltaStatus.Modified:
                // C (checkout.c:523-529): the workdir-modified test is gated on wd->mode != GIT_FILEMODE_COMMIT — a dirty submodule workdir must NOT conflict a
                // gitlink move.
                if (wd.Mode != GitFileMode.GitLink && wdModified)
                {
                    action.Action = IsForce()
                        ? CheckoutAction.UpdateBlob
                        : CheckoutAction.Conflict;
                }
                else
                {
                    action.Action = CheckoutAction.UpdateBlob;
                }
                break;

            case GitDeltaStatus.Typechange:
                // C (checkout.c:530-556).
                if (delta.OldFile.Mode == GitFileMode.Tree)
                {
                    if (wd.Mode == GitFileMode.Tree)
                    {
                        action.Action = CheckoutAction.UpdateBlob;
                    }
                    else if (wd.Mode == GitFileMode.GitLink)
                    {
                        action.Action = await IsSubmoduleConfigOnlyAsync(wd.Path, cancellationToken).ConfigureAwait(false)
                            ? CheckoutAction.UpdateBlob
                            : IsForce()
                                ? CheckoutAction.RemoveAndUpdate
                                : CheckoutAction.Conflict;
                    }
                    else
                    {
                        action.Action = IsForce()
                            ? CheckoutAction.Remove
                            : CheckoutAction.Conflict;
                    }
                }
                else if (wdModified)
                {
                    action.Action = IsForce()
                        ? CheckoutAction.RemoveAndUpdate
                        : CheckoutAction.Conflict;
                }
                else
                {
                    action.Action = CheckoutAction.RemoveAndUpdate;
                }

                // C (checkout.c:553-555): don't update if the typechange lands on a tree — the entry is removed and the child entries create the directory.
                // Without this strip, UPDATE_BLOB on a tree-mode target looks the tree OID up as a blob and fails.
                if (delta.NewFile.Mode == GitFileMode.Tree)
                {
                    action.Action &= ~CheckoutAction.UpdateBlob;
                }

                break;

            case GitDeltaStatus.Conflicted:
                action.Action = CheckoutAction.Conflict;
                break;

            case GitDeltaStatus.Untracked:
                if ((_strategy & GitCheckoutStrategy.RemoveUntracked) != 0)
                {
                    action.Action = CheckoutAction.Remove;
                }
                else
                {
                    action.Action = CheckoutAction.None;
                }
                break;

            case GitDeltaStatus.Ignored:
                if ((_strategy & GitCheckoutStrategy.RemoveIgnored) != 0)
                {
                    action.Action = CheckoutAction.Remove;
                }
                else
                {
                    action.Action = CheckoutAction.None;
                }
                break;

            default:
                action.Action = CheckoutAction.None;
                break;
        }
    }

    /// <summary>
    /// Decision-table case 3: the workdir contains a NON-directory where a
    /// directory is expected (a file "a/b" blocks delta "a/b/c"). Port of
    /// <c>checkout_action_with_wd_blocker</c> (checkout.c:564-598).
    /// </summary>
    private void DetermineActionWithWdBlocker(CheckoutEntryAction action, GitDiffDelta delta, GitIndexEntry wd)
    {
        // GIT_CHECKOUT_NONE short-circuit (checkout.c:572).
        if ((_strategy & GitCheckoutStrategy.None) != 0)
        {
            return;
        }

        switch (delta.Status)
        {
            case GitDeltaStatus.Unmodified:
                // Should show delta as dirty / deleted (checkout.c:576-580).
                Notify(GitCheckoutNotifyFlags.Dirty, delta.DeltaPath ?? default, delta.OldFile, delta.NewFile, ToDiffFile(wd));
                action.Action = IsForce()
                    ? CheckoutAction.RemoveAndUpdate
                    : CheckoutAction.None;
                break;

            case GitDeltaStatus.Added:
            case GitDeltaStatus.Modified:
                action.Action = IsForce()
                    ? CheckoutAction.RemoveAndUpdate
                    : CheckoutAction.Conflict;
                break;

            case GitDeltaStatus.Deleted:
                action.Action = IsForce()
                    ? CheckoutAction.Remove
                    : CheckoutAction.Conflict;
                break;

            case GitDeltaStatus.Typechange:
                action.Action = IsForce()
                    ? CheckoutAction.RemoveAndUpdate
                    : CheckoutAction.Conflict;
                break;

            default:
                action.Action = CheckoutAction.None;
                break;
        }

        ApplyCommonAdjustments(action, delta, wd);
    }

    /// <summary>
    /// Decision-table case 5: the workdir has a DIRECTORY at the delta's path
    /// (delta "a/b" with wd dir "a/b/"). Port of
    /// <c>checkout_action_with_wd_dir</c> (checkout.c:600-653).
    /// </summary>
    private void DetermineActionWithWdDir(CheckoutEntryAction action, GitDiffDelta delta, GitIndexEntry wd, IIterator? workdirIter)
    {
        // GIT_CHECKOUT_NONE short-circuit (checkout.c:609).
        if ((_strategy & GitCheckoutStrategy.None) != 0)
        {
            return;
        }

        switch (delta.Status)
        {
            case GitDeltaStatus.Unmodified: /* case 19 or 24 (or 34 but not really) */
                // C (checkout.c:613-617): the DIRTY notify passes NULL as the
                // workdir file;
                // only the UNTRACKED notify carries the workdir entry.
                Notify(GitCheckoutNotifyFlags.Dirty, delta.DeltaPath ?? default, delta.OldFile, delta.NewFile, null);
                Notify(GitCheckoutNotifyFlags.Untracked, wd.Path, null, null, ToDiffFile(wd));
                action.Action = IsForce()
                    ? CheckoutAction.RemoveAndUpdate
                    : CheckoutAction.None;
                break;

            case GitDeltaStatus.Added: /* case 4 (and 7 for dir) */
            case GitDeltaStatus.Modified: /* case 20 (or 37 but not really) */
                if (delta.OldFile.Mode != GitFileMode.GitLink
                    && delta.NewFile.Mode != GitFileMode.Tree)
                {
                    action.Action = CurrentIsIgnored(workdirIter)
                        ? ((_strategy & GitCheckoutStrategy.DontOverwriteIgnored) != 0
                            ? CheckoutAction.Conflict
                            : CheckoutAction.RemoveAndUpdate)
                        : (IsForce()
                            ? CheckoutAction.RemoveAndUpdate
                            : CheckoutAction.Conflict);
                }
                break;

            case GitDeltaStatus.Deleted: /* case 11 (and 27 for dir) */
                if (delta.OldFile.Mode != GitFileMode.Tree)
                {
                    Notify(GitCheckoutNotifyFlags.Untracked, wd.Path, null, null, ToDiffFile(wd));
                }
                break;

            case GitDeltaStatus.Typechange: /* case 24 or 31 */
                if (delta.OldFile.Mode == GitFileMode.Tree)
                {
                    // Typechange from dir: remove dir and add blob, deferring
                    // the dir removal (empty parents are pruned). Matches
                    // checkout.c:635-643.
                    action.Action = CheckoutAction.UpdateBlob;
                }
                else if (delta.NewFile.Mode != GitFileMode.Tree)
                {
                    // Typechange to dir: the dir is already created — no action
                    // unless FORCE removes it. Matches checkout.c:644-646.
                    action.Action = IsForce()
                        ? CheckoutAction.RemoveAndUpdate
                        : CheckoutAction.Conflict;
                }
                break;

            default:
                action.Action = CheckoutAction.None;
                break;
        }

        ApplyCommonAdjustments(action, delta, wd);
    }

    /// <summary>
    /// Returns true when the given workdir path is an EMPTY directory. Port of
    /// <c>checkout_is_empty_dir</c> (checkout.c:481-489) →
    /// <c>git_fs_path_is_empty_dir</c>.
    /// </summary>
    private bool IsEmptyDir(GitPath path)
    {
        if (WorkdirRoot is null)
        {
            return false;
        }

        string fullPath = Path.Join(WorkdirRoot, path.ToFileSystemString());
        return Directory.Exists(fullPath) && Directory.GetFileSystemEntries(fullPath).Length == 0;
    }

    /// <summary>
    /// Returns true when the workdir iterator's CURRENT entry is ignored.
    /// Port of <c>git_iterator_current_is_ignored</c> (iterator.c) used by
    /// checkout.c:512 and checkout.c:625.
    /// </summary>
    private static bool CurrentIsIgnored(IIterator? workdirIter)
        => workdirIter is FilesystemIterator fs && fs.CurrentIsIgnored();

    /// <summary>
    /// Computes the total checkout step count. Matches checkout.c:1356-1364
    /// (per-delta: REMOVE and UPDATE_BLOB counted separately, so a
    /// REMOVE_AND_UPDATE action contributes 2; workdir-only removals add to
    /// REMOVE at checkout.c:1370) and checkout.c:2643-2647 (the total sums
    /// REMOVE + REMOVE_CONFLICT + UPDATE_BLOB + UPDATE_SUBMODULE +
    /// UPDATE_CONFLICT, where REMOVE_CONFLICT/UPDATE_CONFLICT are the lengths
    /// of the remove/update conflict vectors, checkout.c:1387-1388).
    /// </summary>
    private long CountTotalSteps()
    {
        long removeCount = _actions.Count(a => (a.Action & CheckoutAction.Remove) != 0)
            + (long)_removes.Count;
        long updateBlobCount = _actions.Count(a => (a.Action & CheckoutAction.UpdateBlob) != 0);
        long updateSubmoduleCount = _actions.Count(a => (a.Action & CheckoutAction.UpdateSubmodule) != 0);
        return removeCount + updateBlobCount + updateSubmoduleCount
            + _removeConflicts.Count + _updateConflicts.Count;
    }

    private void ApplyCommonAdjustments(CheckoutEntryAction action, GitDiffDelta delta, GitIndexEntry? wd)
    {
        // UPDATE_ONLY: don't create or remove.
        if ((_strategy & GitCheckoutStrategy.UpdateOnly) != 0)
        {
            action.Action &= ~CheckoutAction.Remove;
        }

        // Gitlink → submodule update.
        if ((action.Action & CheckoutAction.UpdateBlob) != 0)
        {
            if (delta.NewFile.Mode == GitFileMode.GitLink)
            {
                action.Action = (action.Action & ~CheckoutAction.UpdateBlob) | CheckoutAction.UpdateSubmodule;
            }

            // Symlink update: remove old first.
            if (delta.NewFile.Mode == GitFileMode.Symlink && wd is not null)
            {
                action.Action |= CheckoutAction.Remove;
            }

            // C (checkout.c:274-277): "if the file is on disk and doesn't
            // match our mode, force update" — the exec-mismatch REMOVE is
            // applied UNCONDITIONALLY when a workdir entry exists (no
            // core.filemode gate). The workdir entry's mode is the iterator's
            // canonicalized mode (regular vs executable), matching C's
            // GIT_PERMS_IS_EXEC(wd->mode).
            if (wd is { } wdEntry)
            {
                bool wdExec = wdEntry.Mode == GitFileMode.Executable;
                bool targetExec = delta.NewFile.Mode == GitFileMode.Executable;
                if (wdExec != targetExec)
                {
                    action.Action |= CheckoutAction.Remove;
                }
            }
        }

        // Notify.
        GitCheckoutNotifyFlags notify = GitCheckoutNotifyFlags.None;
        if ((action.Action & CheckoutAction.UpdateBlob) != 0)
        {
            notify = GitCheckoutNotifyFlags.Updated;
        }
        if ((action.Action & CheckoutAction.Conflict) != 0)
        {
            notify = GitCheckoutNotifyFlags.Conflict;
        }

        if (notify != GitCheckoutNotifyFlags.None)
        {
            // C (checkout.c:105-116): the notify workdir file comes from the workdir entry when one is available.
            Notify(notify, delta.DeltaPath ?? default, delta.OldFile, delta.NewFile, wd is { } w ? ToDiffFile(w) : null);
        }
    }

    /// <summary>
    /// Handles a workdir-only entry (one that has no corresponding delta in
    /// the baseline→target diff). Matches <c>checkout_action_wd_only</c>
    /// (checkout.c:365-459). Classifies the entry as tracked-in-index (dirty),
    /// untracked, or ignored, and queues it for removal if the corresponding
    /// strategy flag is set and <see cref="WdItemIsRemovable"/> returns true.
    /// </summary>
    private async Task HandleWorkdirOnlyAsync(
        GitIndexEntry wd,
        IIterator workdirIter,
        GitPathSpec? pathspec,
        bool disablePathspecMatch,
        bool ignoreCase,
        Func<GitPath, GitPath, int> pfxcomp,
        CancellationToken cancellationToken)
    {
        // Pathspec filtering — matches git_pathspec__match at checkout.c:377. The lossy UTF-8 decode is safe for ASCII structural matching.
        if (pathspec is not null && !pathspec.IsEmpty)
        {
            var matchFlags = (GitPathSpec.MatchFlags)0;
            if (disablePathspecMatch)
            {
                matchFlags |= GitPathSpec.MatchFlags.NoGlob;
            }

            if (ignoreCase)
            {
                matchFlags |= GitPathSpec.MatchFlags.IgnoreCase;
            }

            if (pathspec.MatchPathspec(wd.Path, matchFlags).IsEmpty)
            {
                // Doesn't match pathspec — advance over (or into for trees).
                if (wd.Mode == GitFileMode.Tree)
                {
                    await workdirIter.AdvanceIntoAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await workdirIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }
        }

        // If the entry is tracked in the index, it's "dirty" (not untracked). Matches checkout.c:388-403: git_index__find_pos → if found, notify DIRTY and
        // remove only if FORCE. _index.Find has an internal GitPath overload.
        if (wd.Mode != GitFileMode.Tree)
        {
            int pos = _index.Find(wd.Path);
            if (pos >= 0)
            {
                // Tracked in index — dirty, not untracked.
                Notify(GitCheckoutNotifyFlags.Dirty, wd.Path, null, null, ToDiffFile(wd));
                if (IsForce() && WdItemIsRemovable(wd))
                {
                    _removes.Add(wd.Path);
                }

                await workdirIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            // Tree entry — check if any index entries are contained inside.
            // Faithful port of checkout.c:404-413: git_index__find_pos always
            // reports the insertion position (even on GIT_ENOTFOUND), so we
            // read the entry at that position and gate the descend solely on
            // the diff's pfxcomp (icase-aware) — no extra boundary check on
            // the byte after the prefix. wd.Path already carries the trailing
            // '/' appended by the filesystem iterator (FilesystemIterator.cs:646,
            // mirroring iterator.c:1330), so pfxcomp("src/top.txt", "src/") == 0
            // succeeds for direct children and deeper descendants alike.
            int pos = _index.FindInsertionPos(wd.Path);
            if (pos < _index.EntryCount)
            {
                GitIndexEntry entry = _index.EntryByIndex(pos);
                if (pfxcomp(entry.Path, wd.Path) == 0)
                {
                    // Index has entries under this directory — descend.
                    await workdirIter.AdvanceIntoAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }

        // Untracked or ignored — advance over to classify.
        GitIndexEntry savedWd = wd;
        (_, IteratorStatus status) = await workdirIter.AdvanceOverAsync(cancellationToken).ConfigureAwait(false);

        // Determine untracked vs ignored.
        bool isIgnored = status == IteratorStatus.Ignored;
        bool remove;
        GitCheckoutNotifyFlags notifyFlag;

        if (isIgnored)
        {
            notifyFlag = GitCheckoutNotifyFlags.Ignored;
            remove = (_strategy & GitCheckoutStrategy.RemoveIgnored) != 0;
        }
        else
        {
            notifyFlag = GitCheckoutNotifyFlags.Untracked;
            remove = (_strategy & GitCheckoutStrategy.RemoveUntracked) != 0;
        }

        Notify(notifyFlag, savedWd.Path, null, null, ToDiffFile(savedWd));

        if (remove && WdItemIsRemovable(savedWd))
        {
            _removes.Add(savedWd.Path);
        }
    }

    /// <summary>
    /// Converts an <see cref="GitIndexEntry"/> to a <see cref="GitDiffFile"/> for
    /// notification callbacks. Matches the C <c>checkout_notify</c> which
    /// fills a <c>git_diff_file</c> from the workdir <c>git_index_entry</c>.
    /// </summary>
    private static GitDiffFile ToDiffFile(GitIndexEntry entry)
        => new(entry.Id, entry.Path, entry.FileSize, entry.Mode);

    /// <summary>
    /// Checks whether a workdir item is safe to remove. Matches
    /// <c>wd_item_is_removable</c> (checkout.c:343-356). Non-tree entries are
    /// always removable. Tree entries are removable unless they contain a
    /// <c>.git</c> directory (which would indicate a submodule or linked
    /// worktree).
    /// </summary>
    private bool WdItemIsRemovable(GitIndexEntry wd)
    {
        if (wd.Mode != GitFileMode.Tree)
        {
            return true;
        }

        if (WorkdirRoot is null)
        {
            return false;
        }

        string fullPath = Path.Join(WorkdirRoot, wd.Path.ToFileSystemString());
        string gitPath = Path.Join(fullPath, ".git");
        return !File.Exists(gitPath) && !Directory.Exists(gitPath);
    }

    /// <summary>
    /// Checks if an update is safe under <see cref="GitCheckoutStrategy.UpdateOnly"/>.
    /// Matches libgit2's <c>checkout_safe_for_update_only</c>
    /// (checkout.c:1727-1749): stat the workdir path; if missing (ENOENT)
    /// or the on-disk type doesn't match the expected mode, return false
    /// (skip the write). Returns true when the workdir file exists with the
    /// same type as the target mode. Increments
    /// <see cref="GitCheckoutPerformance.StatCalls"/>.
    /// </summary>
    private bool SafeForUpdateOnly(GitPath path, GitFileMode expectedMode)
    {
        if (WorkdirRoot is null)
        {
            return false;
        }

        string fullPath = Path.Join(WorkdirRoot, path.ToFileSystemString());
        _perf = _perf with { StatCalls = _perf.StatCalls + 1 };

        // Match the C lstat + st_mode check: file must exist and its type
        // (regular vs symlink vs directory) must match the expected mode.
        if (expectedMode == GitFileMode.Symlink)
        {
            // C compares
            // (st.st_mode & ~0777) == (expected_mode & ~0777)
            // (checkout.c:1727-1749) — a fake symlink (regular file) on a
            // symlink-unsupported platform does NOT match 0120000, so the
            // UPDATE_ONLY write is skipped — existing regular files are never
            // treated as safe when symlinks are unsupported.
            if (!File.Exists(fullPath))
            {
                return false;
            }

            return File.ResolveLinkTarget(fullPath, returnFinalTarget: false) is not null;
        }

        if (expectedMode == GitFileMode.Tree)
        {
            return Directory.Exists(fullPath);
        }

        // Regular / Executable: the workdir file must be a regular file
        // (not a directory or symlink).
        if (!File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return false;
        }

        return !_canSymlink || File.ResolveLinkTarget(fullPath, returnFinalTarget: false) is null;
    }

    /// <summary>
    /// Checks if the workdir content differs from the baseline (old file).
    /// 1:1 port of <c>checkout_is_workdir_modified</c> (checkout.c:176-249).
    /// Takes the workdir iterator's pre-stat'd entry <c>wd</c>
    /// (the managed equivalent of libgit2's <c>wditem</c>), avoiding a second
    /// stat syscall on the hot path: the iterator already populated
    /// <c>wd.Mode</c>/<c>Mtime</c>/<c>FileSize</c> via lstat/stat at iteration
    /// time (<c>FilesystemIterator.GetStatInfo</c>).
    /// </summary>
    /// <remarks>
    /// Branch order matches the C reference exactly:
    /// <list type="number">
    /// <item>Submodule (GitLink) → status short-circuit (checkout.c:185-207).</item>
    /// <item>Stat-cache hit on the index entry (checkout.c:216-227): when the
    /// workdir file's stat matches the index entry's stat, the workdir content
    /// equals the index content, so the question reduces to whether the index
    /// entry's OID is the baseline or the target.</item>
    /// <item>Baseline-size pre-check (checkout.c:232): cheap size mismatch.</item>
    /// <item>Directory cannot be a modified file (checkout.c:236).</item>
    /// <item>Baseline-vs-workdir mode change (checkout.c:239).</item>
    /// <item>Content hash via <see cref="DiffFileContent.HashWorkdirEntryAsync"/>
    /// (checkout.c:242-248): the symlink-aware path that branches on mode —
    /// gitlink → submodule OID, symlink → hash of link-target string
    /// (<c>git_odb__hashlink</c>), regular → read + clean-filter + hash.</item>
    /// </list>
    /// The racy-git read-side guard <c>!git_index_entry_newer_than_index</c>
    /// (checkout.c:219) is applied inside the stat-cache block: when the file's
    /// mtime is at or after the index file's own mtime, the stat cache is
    /// untrusted and the code falls through to the content hash. This is the
    /// correctness defense against the same-second modify-then-revert race.
    /// </remarks>
    /// <summary>
    /// Returns true if the submodule at <paramref name="path"/> exists only in
    /// the config (never checked out / not in HEAD or the index). Matches
    /// <c>submodule_is_config_only</c> (checkout.c:461-479): a failed lookup
    /// or a config-only location counts as config-only.
    /// </summary>
    private async Task<bool> IsSubmoduleConfigOnlyAsync(GitPath path, CancellationToken cancellationToken)
    {
        try
        {
            // GitPath lookup — C's git_submodule_lookup takes the raw path bytes (submodule.c:308-433); no UTF-8 round-trip.
            using GitSubmodule sm = await GitSubmodule.LookupAsync(_repo, path, cancellationToken).ConfigureAwait(false);
            return sm.Location() == SubmoduleStatus.InConfig;
        }
        catch (GitException)
        {
            return true;
        }
    }

    private async Task<bool> IsWorkdirModifiedAsync(
        GitDiffDelta delta, GitIndexEntry wd, CancellationToken cancellationToken)
    {
        if (WorkdirRoot is null)
        {
            return false;
        }

        // checkout.c:185-207 — submodule (gitlink) branch. A submodule's
        // "modification" is its workdir-dirty status (index or workdir dirty)
        // or a HEAD OID drift from the baseline. Matches the C flow:
        //   if (wditem->mode == GIT_FILEMODE_COMMIT) {
        //       git_submodule_lookup; git_submodule_status; IS_WD_DIRTY → true;
        //       else rval = baseitem->id != sm_wd_id; }
        if (wd.Mode == GitFileMode.GitLink)
        {
            // git_submodule_lookup ERRORS (ENOTFOUND/EEXISTS) on a
            // miss — C treats any lookup failure as "modified" (conservative),
            // checkout.c:185-207.
            GitSubmodule sm;
            try
            {
                // GitPath lookup — C's git_submodule_lookup takes the raw path bytes (submodule.c:308-433).
                sm = await GitSubmodule.LookupAsync(
                    _repo, wd.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException)
            {
                return true;
            }

            // GIT_SUBMODULE_STATUS_IS_WD_DIRTY = WD_INDEX_MODIFIED | WD_WD_MODIFIED
            // (submodule.h). A dirty submodule workdir → modified.
            if ((sm.StatusFlags & (SubmoduleStatus.WdIndexModified | SubmoduleStatus.WdWdModified)) != 0)
            {
                return true;
            }

            GitOid smOid = sm.WdId;
            return !smOid.IsZero && delta.OldFile.Id != smOid;
        }

        // checkout.c:216-227 — stat-cache short-circuit. "Look at the cache to
        // decide if the workdir is modified: if the cache contents match the
        // workdir contents, then we do not need to examine the working
        // directory directly, instead we can examine the cache to see if _it_
        // has been modified. This allows us to avoid touching the disk."
        GitIndexEntry? ie = _index.EntryByPath(wd.Path, stage: 0);
        if (ie is { } statEntry
            && !_index.EntryNewerThanIndex(statEntry)
            && IndexTimeEquals(wd.Mtime, statEntry.Mtime)
            && wd.FileSize == statEntry.FileSize
            && !IsFilemodeChanged(wd.Mode, statEntry.Mode, _respectFilemode))
        {
            // checkout.c:224-226 — workdir matches index content. The workdir
            // is modified iff the index entry's OID is neither the baseline
            // nor the target, OR the baseline-vs-index mode differs.
            return !IsWorkdirBaseOrNew(statEntry.Id, delta.OldFile, delta.NewFile)
                || IsFilemodeChanged(delta.OldFile.Mode, statEntry.Mode, _respectFilemode);
        }

        // checkout.c:229-233 — "depending on where base is coming from, we may
        // or may not know the actual size of the data, so we can't rely on
        // this shortcut." A non-zero baseline size that mismatches the
        // workdir size ⇒ modified. (delta.OldFile.Size defaults to -1, treated
        // as "unknown" — only a positive size triggers this check.)
        if (delta.OldFile.Size > 0 && (long)wd.FileSize != delta.OldFile.Size)
        {
            return true;
        }

        // checkout.c:235-237 — "if the workdir item is a directory, it cannot
        // be a modified file".
        if (wd.Mode == GitFileMode.Tree)
        {
            return false;
        }

        // checkout.c:239-240 — baseline-vs-workdir mode change.
        if (IsFilemodeChanged(delta.OldFile.Mode, wd.Mode, _respectFilemode))
        {
            return true;
        }

        // checkout.c:242-248 — content hash via git_diff__oid_for_entry's
        // managed port (symlink-aware). The workdir is "not modified" iff the
        // computed OID matches the baseline OR the target.
        string fullPath = Path.Join(WorkdirRoot, wd.Path.ToFileSystemString());
        GitOid oid = await DiffFileContent.HashWorkdirEntryAsync(
            _repo, wd.Path, fullPath, wd.Mode, cancellationToken).ConfigureAwait(false);

        return !IsWorkdirBaseOrNew(oid, delta.OldFile, delta.NewFile);
    }

    /// <summary>
    /// Returns true if <paramref name="workdirId"/> equals either the baseline
    /// or the target OID. 1:1 port of <c>is_workdir_base_or_new</c>
    /// (checkout.c:151-158). Used to decide "no checkout action needed" once
    /// the workdir content OID is known.
    /// </summary>
    private static bool IsWorkdirBaseOrNew(GitOid workdirId, GitDiffFile baseitem, GitDiffFile newitem)
        => baseitem.Id == workdirId || newitem.Id == workdirId;

    /// <summary>
    /// Returns true if two git file modes differ, honoring <c>core.filemode</c>.
    /// 1:1 port of <c>is_filemode_changed</c> (checkout.c:160-174). When
    /// <paramref name="respectFilemode"/> is false, symlinks and the executable
    /// bit are normalized away (symlink → blob, exec stripped) before
    /// comparing — matching the C bit-mask path. When true, modes compare
    /// directly (the enum values are already canonical).
    /// </summary>
    private static bool IsFilemodeChanged(GitFileMode a, GitFileMode b, bool respectFilemode)
    {
        if (!respectFilemode)
        {
            // checkout.c:163-171 — normalize symlinks to BLOB and strip the
            // 0111 exec bits when core.filemode=false. GitFileMode is already
            // canonical (no raw bits to mask), so this reduces to enum mapping.
            if (a == GitFileMode.Symlink)
            {
                a = GitFileMode.Regular;
            }

            if (b == GitFileMode.Symlink)
            {
                b = GitFileMode.Regular;
            }

            if (a == GitFileMode.Executable)
            {
                a = GitFileMode.Regular;
            }

            if (b == GitFileMode.Executable)
            {
                b = GitFileMode.Regular;
            }
        }

        return a != b;
    }

    /// <summary>
    /// Compares two <see cref="IndexTime"/> values for equality. Matches
    /// <c>git_index_time_eq</c>. (The record struct's auto-generated Equals is
    /// semantically identical; this form mirrors the C reference for grep.)
    /// </summary>
    private static bool IndexTimeEquals(IndexTime a, IndexTime b)
        => a.Seconds == b.Seconds && a.Nanoseconds == b.Nanoseconds;

    // ── Conflict loading ────────────────────────────────────────────────

    /// <summary>
    /// Collects the set of conflict paths whose index entries
    /// <see cref="ExecuteRemoveConflicts"/> will drop. Mirror of libgit2's
    /// <c>checkout_get_remove_conflicts</c> (checkout.c:1277-1286) driven by
    /// <c>checkout_conflict_append_remove</c> (checkout.c:1252-1275).
    /// </summary>
    /// <remarks>
    /// C iterates
    /// <c>data->index</c> (the REPO index) with the pathspec filter for
    /// EVERY target type (checkout.c:1277-1286 + checkout_conflicts_foreach
    /// at 958). The old <c>if (_targetIsIndex) return;</c> kept repo-index
    /// conflicts of every target type, including an explicit target index —
    /// the target-index stages are re-added afterwards by the
    /// checkout_conflict_update_index port at the end of
    /// <see cref="ExecuteCreateConflictsAsync"/>.
    /// </remarks>
    private void LoadRemoveConflicts()
    {
        // C (checkout.c:1277-1286, checkout_get_remove_conflicts): DONT_UPDATE_INDEX suppresses the conflict-entry removal too.
        if ((_strategy & GitCheckoutStrategy.DontUpdateIndex) != 0)
        {
            return;
        }

        if (!_index.HasConflicts)
        {
            return;
        }

        // De-duplicate by path: a conflicted path carries stages 1/2/3 and
        // appears as consecutive entries (the index is sorted by path then
        // stage). git_index_conflict_iterator yields one tuple per path.
        // Conflict comparisons stay case-sensitive raw strcmp (checkout.c:806,
        // 998, 1009) — NOT the diff's icase comparator — so use GitPath.Equals.
        GitPath lastPath = default;
        for (int i = 0; i < _index.EntryCount; i++)
        {
            GitIndexEntry entry = _index.EntryByIndex(i);
            if (entry.Stage == 0)
            {
                continue;
            }

            if (entry.Path.Equals(lastPath))
            {
                continue;
            }

            lastPath = entry.Path;

            // Pathspec filter (checkout_conflicts_foreach, checkout.c:958;
            // conflict_pathspec_match, checkout.c:839-864): a conflict
            // matches when ANY of ancestor/ours/theirs matches.
            (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = _index.ConflictGet(entry.Path);
            if (!ConflictPathspecMatch(ancestor, ours, theirs))
            {
                continue;
            }

            _removeConflicts.Add(entry.Path);
        }
    }

    /// <summary>
    /// Ports <c>conflict_pathspec_match</c> (checkout.c:839-864): a conflict
    /// matches the checkout pathspec when ANY of ours/theirs/ancestor
    /// matches, honoring DISABLE_PATHSPEC_MATCH (literal match) and the
    /// workdir iterator's ignore-case. A null/empty pathspec matches
    /// everything (git_pathspec__match, pathspec.c).
    /// </summary>
    private bool ConflictPathspecMatch(GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs)
    {
        if (_compiledPathspec is null || _compiledPathspec.IsEmpty)
        {
            return true;
        }

        GitPathSpec.MatchFlags flags = GitPathSpec.MatchFlags.Default;
        if (_disablePathspecMatch)
        {
            flags |= GitPathSpec.MatchFlags.NoGlob;
        }

        if (_conflictIgnoreCase)
        {
            flags |= GitPathSpec.MatchFlags.IgnoreCase;
        }

        return (ours is { } o && _compiledPathspec.MatchesPath(flags, o.Path))
            || (theirs is { } t && _compiledPathspec.MatchesPath(flags, t.Path))
            || (ancestor is { } a && _compiledPathspec.MatchesPath(flags, a.Path));
    }

    /// <summary>
    /// a conflict side
    /// whose blob is missing from the ODB fails the checkout like C's
    /// propagated git_blob_lookup error (checkout.c:874-909, GIT_ENOTFOUND).
    /// </summary>
    private static GitException MissingConflictBlob(GitOid id)
        => new(GitErrorCode.NotFound, $"conflict blob {id} is not in the database", GitErrorCategory.Odb);

    /// <summary>
    /// Builds the full conflict records that drive marker / side writing in
    /// <see cref="ExecuteCreateConflictsAsync"/>. Mirror of libgit2's
    /// <c>checkout_get_update_conflicts</c> (checkout.c:1233-1250) and the load
    /// it delegates to, <c>checkout_conflicts_load</c> (checkout.c:974-991).
    /// </summary>
    /// <remarks>
    /// Gated on the checkout target being an index — the direct port of
    /// checkout.c:979 ("Only write conflicts from sources that have them:
    /// indexes" via <c>git_iterator_index(data->target)==NULL</c>). A tree
    /// iterator carries no index, so for <c>git_checkout_tree</c> / hard reset
    /// this method is a no-op and <see cref="_updateConflicts"/> stays empty:
    /// no conflict markers are ever written to the workdir during a tree
    /// checkout.
    /// </remarks>
    private async Task LoadUpdateConflictsAsync(CancellationToken cancellationToken)
    {
        // C gates
        // checkout_get_update_conflicts on SKIP_UNMERGED (checkout.c:1240-
        // 1241), leaving update_conflicts empty rather than populating
        // _updateConflicts (which would inflate CountTotalSteps and remove
        // both-deleted conflicts).
        if ((_strategy & GitCheckoutStrategy.SkipUnmerged) != 0)
        {
            return;
        }

        if (!_targetIsIndex || !_targetIndex.HasConflicts)
        {
            return;
        }

        // Walk the target index for conflicted entries.
        for (int i = 0; i < _targetIndex.EntryCount; i++)
        {
            GitIndexEntry entry = _targetIndex.EntryByIndex(i);
            if (entry.Stage == 0)
            {
                continue;
            }

            // Skip if already loaded. Conflict lookup stays case-sensitive
            // (checkout_conflicts_cmp_entry, checkout.c:998 — raw strcmp).
            GitPath path = entry.Path;
            if (_updateConflicts.Any(c => c.Path.Equals(path)))
            {
                continue;
            }

            (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = _targetIndex.ConflictGet(path);

            // Pathspec filter (checkout_conflicts_foreach, checkout.c:958;
            // conflict_pathspec_match, checkout.c:839-864).
            if (!ConflictPathspecMatch(ancestor, ours, theirs))
            {
                continue;
            }

            // Detect submodules. Matches checkout_conflict_detect_submodule
            // (checkout.c:866-872): any gitlink side marks the conflict.
            var conflict = new CheckoutConflictData
            {
                Path = path,
                Ancestor = ancestor,
                Ours = ours,
                Theirs = theirs,
                Submodule =
                    (ancestor is { Mode: GitFileMode.GitLink }) ||
                    (ours is { Mode: GitFileMode.GitLink }) ||
                    (theirs is { Mode: GitFileMode.GitLink }),
            };

            // Detect binary. Matches checkout_conflict_detect_binary
            // (checkout.c:874-909): a conflict is binary if ANY of
            // ancestor/ours/theirs is binary, short-circuiting in that order.
            // Submodule conflicts are never binary (checkout.c:879-880).
            // C PROPAGATES git_blob_lookup failures — a
            // missing conflict blob fails the checkout (checkout.c:874-909)
            // instead of being treated as non-binary and silently merged as
            // empty content.
            if (!conflict.Submodule && ancestor is { Id: var ancestorId } && !ancestorId.IsZero)
            {
                GitBlob? blob = await _repo.Objects.LookupAsync<GitBlob>(ancestorId, cancellationToken).ConfigureAwait(false)
                    ?? throw MissingConflictBlob(ancestorId);
                conflict.Binary = blob.IsBinary;
                blob.Dispose();
            }

            if (!conflict.Binary && !conflict.Submodule && ours is { Id: var oursId } && !oursId.IsZero)
            {
                GitBlob? blob = await _repo.Objects.LookupAsync<GitBlob>(oursId, cancellationToken).ConfigureAwait(false)
                    ?? throw MissingConflictBlob(oursId);
                conflict.Binary = blob.IsBinary;
                blob.Dispose();
            }

            if (!conflict.Binary && !conflict.Submodule && theirs is { Id: var theirsId } && !theirsId.IsZero)
            {
                GitBlob? blob = await _repo.Objects.LookupAsync<GitBlob>(theirsId, cancellationToken).ConfigureAwait(false)
                    ?? throw MissingConflictBlob(theirsId);
                conflict.Binary = blob.IsBinary;
                blob.Dispose();
            }

            _updateConflicts.Add(conflict);
        }

        // Coalesce rename conflicts from NAME entries (matches
        // checkout_conflicts_coalesce_renames, checkout.c:1120-1175).
        // Must run BEFORE MarkDirectoryFile (checkout.c:1243-1245).
        CoalesceRenames();

        // Mark directory/file conflicts.
        MarkDirectoryFile();
    }

    /// <summary>
    /// Coalesces rename conflicts using the index NAME entries. Matches
    /// <c>checkout_conflicts_coalesce_renames</c> (<c>checkout.c:1120-1175</c>).
    /// For each NAME entry, remaps the ancestor conflict's ours/theirs to the
    /// renamed entry, nulls-out the "branch" conflict, sets
    /// <see cref="CheckoutConflictData.NameCollision"/>/ <see cref="CheckoutConflictData.OneToTwo"/>,
    /// and removes fully-coalesced (empty) conflicts.
    /// </summary>
    private void CoalesceRenames()
    {
        if (_targetIndex.NameCount == 0)
        {
            return;
        }

        // Build lookup: ancestor-path → conflict, and branch-path → conflict. Matches checkout_conflicts_search_ancestor (binary search by ancestor path,
        // checkout.c:1001-1022) and checkout_conflicts_search_branch (linear scan for ours/theirs path, checkout.c:1024-1047). The conflicts list is small, so
        // dictionaries suffice. Byte-wise GitPath keys — conflict comparisons stay case-sensitive (checkout.c:806,998,1009 use raw strcmp, NOT the diff's icase
        // slot).
        var byAncestorPath = new Dictionary<GitPath, CheckoutConflictData>();
        var byOurPath = new Dictionary<GitPath, CheckoutConflictData>();
        var byTheirPath = new Dictionary<GitPath, CheckoutConflictData>();

        foreach (CheckoutConflictData c in _updateConflicts)
        {
            if (c.Ancestor is not null)
            {
                byAncestorPath[c.Ancestor.Value.Path] = c;
            }

            if (c.Ours is not null)
            {
                byOurPath[c.Ours.Value.Path] = c;
            }

            if (c.Theirs is not null)
            {
                byTheirPath[c.Theirs.Value.Path] = c;
            }
        }

        foreach (GitIndexNameEntry name in _targetIndex.NameEntries)
        {
            // Matches checkout_conflicts_load_byname_entry guards
            // (checkout.c:1063-1073): ancestor must be non-null, at least
            // one of ours/theirs must be non-null.
            if (name.Ancestor is null)
            {
                throw new GitException(
                    GitErrorCode.Error, // C (checkout.c:1063-1065): -1 (GIT_ERROR)
                    "a NAME entry exists without an ancestor",
                    GitErrorCategory.Index);
            }

            if (name.Ours is null && name.Theirs is null)
            {
                throw new GitException(
                    GitErrorCode.Error, // C (checkout.c:1070-1071): -1 (GIT_ERROR)
                    "a NAME entry exists without an ours or theirs",
                    GitErrorCategory.Index);
            }

            if (!byAncestorPath.TryGetValue(name.Ancestor.Value, out CheckoutConflictData? ancestorConflict))
            {
                throw new GitException(
                    GitErrorCode.Invalid,
                    $"a NAME entry referenced ancestor entry '{name.Ancestor.Value.ToUtf8String()}' which does not exist in the main index",
                    GitErrorCategory.Index);
            }

            // Resolve "ours" conflict (matches checkout.c:1084-1095).
            CheckoutConflictData? ourConflict = null;
            if (name.Ours is not null)
            {
                if (name.Ours == name.Ancestor)
                {
                    ourConflict = ancestorConflict;
                }
                else if (!byOurPath.TryGetValue(name.Ours.Value, out ourConflict) || ourConflict.Ours is null)
                {
                    throw new GitException(
                        GitErrorCode.Invalid,
                        $"a NAME entry referenced our entry '{name.Ours.Value.ToUtf8String()}' which does not exist in the main index",
                        GitErrorCategory.Index);
                }
            }

            // Resolve "theirs" conflict (matches checkout.c:1097-1110).
            CheckoutConflictData? theirConflict = null;
            if (name.Theirs is not null)
            {
                if (name.Theirs == name.Ancestor)
                {
                    theirConflict = ancestorConflict;
                }
                else if (name.Ours is not null && name.Ours == name.Theirs)
                {
                    theirConflict = ourConflict;
                }
                else if (!byTheirPath.TryGetValue(name.Theirs.Value, out theirConflict) || theirConflict.Theirs is null)
                {
                    throw new GitException(
                        GitErrorCode.Invalid,
                        $"a NAME entry referenced their entry '{name.Theirs.Value.ToUtf8String()}' which does not exist in the main index",
                        GitErrorCategory.Index);
                }
            }

            // Coalesce ours into ancestor (matches checkout.c:1143-1152).
            if (ourConflict is not null && !ReferenceEquals(ourConflict, ancestorConflict))
            {
                ancestorConflict.Ours = ourConflict.Ours;
                ourConflict.Ours = null;

                if (ourConflict.Theirs is not null)
                {
                    ourConflict.NameCollision = true;
                }

                if (ourConflict.NameCollision)
                {
                    ancestorConflict.NameCollision = true;
                }
            }

            // Coalesce theirs into ancestor (matches checkout.c:1154-1163).
            if (theirConflict is not null && !ReferenceEquals(theirConflict, ancestorConflict))
            {
                ancestorConflict.Theirs = theirConflict.Theirs;
                theirConflict.Theirs = null;

                if (theirConflict.Ours is not null)
                {
                    theirConflict.NameCollision = true;
                }

                if (theirConflict.NameCollision)
                {
                    ancestorConflict.NameCollision = true;
                }
            }

            // 1-to-2 rename (matches checkout.c:1165-1167).
            if (ourConflict is not null && !ReferenceEquals(ourConflict, ancestorConflict) &&
                theirConflict is not null && !ReferenceEquals(theirConflict, ancestorConflict))
            {
                ancestorConflict.OneToTwo = true;
            }
        }

        // Remove fully-coalesced (empty) conflicts — matches
        // git_vector_remove_matching(checkout_conflictdata_empty)
        // (checkout.c:1170-1171, checkout_conflictdata_empty:1513-1522).
        _updateConflicts.RemoveAll(c =>
            c.Ancestor is null && c.Ours is null && c.Theirs is null);
    }

    private void MarkDirectoryFile()
    {
        // C (checkout.c:1177-1231, checkout_conflicts_mark_directoryfile):
        // for each SINGLE-sided conflict, the MAIN INDEX is scanned from the
        // conflict's own entry (git_index_find) — any LATER index entry
        // (not just other conflicts) whose path falls under the conflict
        // path marks directoryfile. A missing index entry for the conflict
        // path is an inconsistency error.
        List<GitIndexEntry> entries = [.. _targetIndex.Entries];
        for (int i = 0; i < _updateConflicts.Count; i++)
        {
            CheckoutConflictData c = _updateConflicts[i];
            if ((c.Ours is not null && c.Theirs is not null) ||
                (c.Ours is null && c.Theirs is null))
            {
                continue;
            }

            GitPath path = (c.Ours ?? c.Theirs)!.Value.Path;

            int j = _targetIndex.Find(path);
            if (j < 0)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"index inconsistency, could not find entry for expected conflict '{path.ToUtf8String()}'",
                    GitErrorCategory.Index);
            }

            for (; j < entries.Count; j++)
            {
                GitPath entryPath = entries[j].Path;
                if (GitPath.Compare(entryPath, path) == 0)
                {
                    continue; // the conflict entry itself
                }

                // Directory-prefix: entryPath is path + '/' + ...
                if (entryPath.Length > path.Length
                    && entryPath.Span[path.Length] == (byte)'/'
                    && GitPath.ComparePrefix(entryPath, path) == 0)
                {
                    c.DirectoryFile = true;
                }

                break;
            }
        }
    }

    // ── Execution passes ────────────────────────────────────────────────

    private void ExecuteRemoves()
    {
        // Delta-driven removals (baseline has it, target doesn't → Remove).
        foreach (CheckoutEntryAction action in _actions)
        {
            if ((action.Action & CheckoutAction.Remove) == 0)
            {
                continue;
            }

            _writer.Remove(action.Path);

            // Remove from index unless prevented. Matches checkout.c:1848-1853
            // which gates on DONT_UPDATE_INDEX and only removes when no
            // UPDATE_BLOB follows (the blob-update path handles its own replace).
            if (ShouldUpdateIndex && (action.Action & CheckoutAction.UpdateBlob) == 0)
            {
                _index.Remove(action.Path, 0);
            }

            _completedSteps++;
            ReportProgress(action.Path);
        }

        // Workdir-only removals (untracked/ignored files queued by
        // HandleWorkdirOnly — not in the baseline→target diff but present in
        // the workdir). Matches checkout_remove_the_old (checkout.c:1857-1871)
        // which iterates data->removes after the delta-driven removals.
        foreach (GitPath path in _removes)
        {
            _writer.Remove(path);

            // Remove from index if present unless prevented. Matches
            // checkout.c:1865-1872 (gated on DONT_UPDATE_INDEX).
            if (ShouldUpdateIndex)
            {
                _ = _index.Remove(path, 0);
            }

            _completedSteps++;
            ReportProgress(path);
        }

        // Prune empty parent directories.
        foreach (CheckoutEntryAction action in _actions)
        {
            if ((action.Action & CheckoutAction.Remove) != 0)
            {
                _writer.PruneEmptyParents(action.Path);
            }
        }

        foreach (GitPath path in _removes)
        {
            _writer.PruneEmptyParents(path);
        }
    }

    private void ExecuteRemoveConflicts()
    {
        // Tree-target checkout (git_checkout_tree / hard reset / checkout
        // branch): drop every conflict path collected in _removeConflicts.
        // Matches libgit2's checkout_remove_conflicts (checkout.c:2279-2289)
        // iterating the full remove_conflicts vector. With _updateConflicts
        // empty for tree targets (checkout_conflicts_load, checkout.c:974-991,
        // returns early when the target carries no index), no create-conflicts
        // pass runs to re-add anything, so every conflict must be cleared here.
        foreach (GitPath path in _removeConflicts)
        {
            _index.ConflictRemove(path);
        }

        // Index-target checkout (git_checkout_index — merge/cherry-pick/
        // revert/stash): remove only both-deleted conflicts from _updateConflicts.
        // Non-deleted conflicts preserve their stage 1/2/3 entries; the conflict
        // stages are (re-)added to the repo's index by the
        // checkout_conflict_update_index port at the end of
        // ExecuteCreateConflictsAsync.
        foreach (CheckoutConflictData conflict in _updateConflicts)
        {
            if (conflict.Ours is null && conflict.Theirs is null)
            {
                _index.ConflictRemove(conflict.Path);
            }
        }
    }

    private async Task ExecuteCreatesAsync(CancellationToken cancellationToken)
    {
        // First pass: regular files and symlinks.
        foreach (CheckoutEntryAction action in _actions)
        {
            if ((action.Action & CheckoutAction.UpdateBlob) == 0)
            {
                continue;
            }

            GitDiffDelta? delta = action.Delta;
            Debug.Assert(delta is not null, "Delta is set when UpdateBlob action is queued");
            GitDiffFile newFile = delta.NewFile;

            // Skip gitlinks (handled by submodule pass).
            if (newFile.Mode == GitFileMode.GitLink)
            {
                continue;
            }

            // UPDATE_ONLY: skip creating files that do not exist in the
            // workdir (or whose on-disk type doesn't match the target mode).
            // Matches libgit2's checkout_safe_for_update_only (checkout.c:1727-1749)
            // called from checkout_blob (checkout.c:1786-1803): stat the
            // workdir path; if ENOENT → skip, if type mismatch → skip.
            if ((_strategy & GitCheckoutStrategy.UpdateOnly) != 0
                && !SafeForUpdateOnly(action.Path, newFile.Mode))
            {
                continue;
            }

            await _writer.WriteBlobAsync(action.Path, newFile.Id, newFile.Mode, _canSymlink, cancellationToken).ConfigureAwait(false);

            // Update the index entry unless prevented.
            if (ShouldUpdateIndex)
            {
                UpdateIndexEntry(action.Path, newFile);
            }

            _completedSteps++;
            ReportProgress(action.Path);
        }

        // Submodule pass: port of checkout_submodule (checkout.c:1676-1715).
        // C ALWAYS
        // mkdirs the submodule path first (a gitlink whose .gitmodules entry
        // is missing yields an empty directory + a stat'd index entry,
        // checkout.c:1687-1693) and NEVER pulls or clones during checkout
        // ("Checkout will not execute a pull on the submodule",
        // checkout.c:1705-1712). The submodule path is always mkdir'd, never
        // pulled or cloned — no network clone/fetch side effect during a
        // checkout.
        if ((_strategy & GitCheckoutStrategy.UpdateOnly) != 0)
        {
            return;
        }

        foreach (CheckoutEntryAction action in _actions)
        {
            if ((action.Action & CheckoutAction.UpdateSubmodule) == 0)
            {
                continue;
            }

            GitDiffDelta? delta = action.Delta;
            Debug.Assert(delta is not null, "Delta is set when UpdateSubmodule action is queued");
            GitDiffFile newFile = delta.NewFile;
            GitPath path = delta.NewFile.Path ?? default;

            // checkout_mkdir with MKDIR_REMOVE_EXISTING when
            // should_remove_existing (checkout.c:1676-1686, 1407-1417).
            _writer.MkdirSubmodule(path, _shouldRemoveExisting);

            // C (checkout.c:1687-1693): git_submodule_lookup — a missing
            // .gitmodules entry is GIT_ENOTFOUND, which is cleared; the empty
            // directory is still stat'd into the index. Other errors
            // propagate. The lookup is byte-keyed via GitPath, matching C's raw
            // path bytes (submodule.c:308-433).
            try
            {
                _ = await GitSubmodule.LookupAsync(_repo, path, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
            {
                // git_error_clear() (checkout.c:1690).
            }

            // Record the gitlink in the index unless prevented — with the
            // workdir stat (checkout_submodule_update_index,
            // checkout.c:1650-1674).
            if (ShouldUpdateIndex)
            {
                UpdateIndexEntry(path, newFile);
            }

            _completedSteps++;
            ReportProgress(action.Path);
        }

        // The git_submodule_lookup above (checkout.c:1687-1693) loads the
        // per-repo submodule cache BEFORE the gitlink reaches the index
        // (UpdateIndexEntry runs after the lookup, matching C's order). C has
        // no persistent cache, so a later git_submodule_lookup re-reads the
        // index and sees the gitlink; the port caches, so the cache populated
        // during the loop holds a stale IndexId (zero). Reload it now that the
        // index carries the gitlinks, so the next external lookup reflects the
        // post-checkout index state.
        await _repo.ReloadSubmoduleCacheIfLoadedAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask ExecuteCreateConflictsAsync(CancellationToken cancellationToken)
    {
        // _updateConflicts is empty for tree-target checkouts (see
        // LoadUpdateConflictsAsync — the port of checkout.c:979), so this
        // method — the conflict MARKER writer — is an effective no-op for
        // git_checkout_tree / hard reset — the majority case. Only
        // index-target checkouts (merge/cherry-pick/revert/stash) populate it.
        if (_updateConflicts.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(ExecuteCreateConflictsSlowAsync(cancellationToken));
    }

    /// <summary>Slow path of <see cref="ExecuteCreateConflictsAsync"/>: writes conflict markers (blob/merge IO).</summary>
    private async Task ExecuteCreateConflictsSlowAsync(CancellationToken cancellationToken)
    {
        // Check strategy flags for conflict handling.
        bool useOurs = (_strategy & GitCheckoutStrategy.UseOurs) != 0;
        bool useTheirs = (_strategy & GitCheckoutStrategy.UseTheirs) != 0;
        bool skipUnmerged = (_strategy & GitCheckoutStrategy.SkipUnmerged) != 0;

        foreach (CheckoutConflictData conflict in _updateConflicts)
        {
            // SkipUnmerged: leave conflicts in the index, write nothing.
            if (skipUnmerged)
            {
                continue;
            }

            // Both sides deleted → nothing to write.
            // (checkout.c:2192 "Both deleted: nothing to do")
            if (conflict.Ours is null && conflict.Theirs is null)
            {
                continue;
            }

            // UseOurs / UseTheirs: write the requested side.
            // Matches checkout.c:2194-2224 including the name_collision
            // null-side handling (ignore the other side of name collisions).
            if (useOurs && conflict.Ours is not null)
            {
                await WriteConflictSideAsync(conflict, conflict.Ours.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (useOurs && conflict.Ours is null && conflict.NameCollision)
            {
                continue;
            }

            if (useTheirs && conflict.Theirs is not null)
            {
                await WriteConflictSideAsync(conflict, conflict.Theirs.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (useTheirs && conflict.Theirs is null && conflict.NameCollision)
            {
                continue;
            }

            // Modify/delete: one side null, other present → write the present side.
            // (checkout.c:2226-2229). WriteConflictSide applies suffixing for
            // NameCollision/DirectoryFile.
            if (conflict.Ours is not null && conflict.Theirs is null)
            {
                await WriteConflictSideAsync(conflict, conflict.Ours.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (conflict.Ours is null && conflict.Theirs is not null)
            {
                await WriteConflictSideAsync(conflict, conflict.Theirs.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 1-to-2 rename (add/add or rename 1→2): write both sides as
            // separate files, each suffixed per WriteConflictSide.
            // (checkout.c:2237-2238: checkout_write_entries)
            GitIndexEntry? ours = conflict.Ours;
            GitIndexEntry? theirs = conflict.Theirs;
            Debug.Assert(ours is not null && theirs is not null, "modify/delete conflicts handled above");

            if (conflict.OneToTwo)
            {
                await WriteConflictSideAsync(conflict, ours.Value, cancellationToken).ConfigureAwait(false);
                await WriteConflictSideAsync(conflict, theirs.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Both sides are symlinks → write ours.
            // (checkout.c:2241-2243)
            if (IsSymlink(ours.Value.Mode) && IsSymlink(theirs.Value.Mode))
            {
                await WriteConflictSideAsync(conflict, ours.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Link/file conflict → write the file side (not the symlink).
            // (checkout.c:2244-2247)
            if (IsSymlink(ours.Value.Mode))
            {
                await WriteConflictSideAsync(conflict, theirs.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (IsSymlink(theirs.Value.Mode))
            {
                await WriteConflictSideAsync(conflict, ours.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Gitlink/submodule → do nothing.
            // (checkout.c:2248-2250)
            if (conflict.Submodule)
            {
                continue;
            }

            // Binary conflict → write ours.
            // (checkout.c:2251-2253)
            if (conflict.Binary)
            {
                await WriteConflictSideAsync(conflict, ours.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Both sides modified → 3-way merge using the coalesced conflict
            // entries (not re-read from index, which was already cleared by
            // ExecuteRemoveConflicts). Matches checkout_write_merge which uses
            // the coalesced checkout_conflictdata ours/theirs pointers. The
            // inputs are RAW ODB contents; filters are applied to the merged
            // OUTPUT below (matches git_merge_file_from_index + the smudge at
            // checkout.c:2130-2145).
            (byte[] merged, _, string? resultPath, GitFileMode resultMode) = await MergeFileFromIndex.MergeFromEntriesAsync(
                _repo, conflict.Ancestor, ours, theirs,
                _opts, _conflictStyle, cancellationToken).ConfigureAwait(false);

            // C (checkout.c:2117-2121): a null result path or zero mode (e.g. rename/rename or one-sided-deleted merge) aborts the checkout with GIT_ECONFLICT
            // "could not merge contents of file".
            if (resultPath is null || resultMode == 0)
            {
                throw new GitException(
                    GitErrorCode.Conflict,
                    "could not merge contents of file",
                    GitErrorCategory.Checkout);
            }

            // For 2-to-1 name collisions, the merge result is written to a suffixed path. Matches checkout_merge_path (checkout.c:2042-2068): the suffix is
            // chosen by comparing result.Path to conflict.Ours.Path. Byte-wise compare: resultPath is string (from BestPath; the merge
            // subsystem); convert once to GitPath for byte-faithful comparison against ours.Value.Path (already GitPath). resultPath is non-null here (the
            // null/zero check above threw).
            GitPath writePath = conflict.Path;
            if (conflict.NameCollision)
            {
                string ourLabel = _opts.OurLabel ?? "ours";
                string theirLabel = _opts.TheirLabel ?? "theirs";
                var resultPathBytes = GitPath.FromUtf8String(resultPath);
                string suffix = resultPathBytes.Equals(ours.Value.Path)
                    ? ourLabel
                    : theirLabel;
                writePath = SuffixPath(resultPathBytes, suffix);
            }

            string? workdir = WorkdirRoot;
            Debug.Assert(workdir is not null, "Workdir root is set for working-dir checkout conflicts");

            // UPDATE_ONLY: skip the write when the workdir file is missing or
            // the on-disk type doesn't match the merged mode. Matches
            // checkout_safe_for_update_only at checkout.c:2126-2128.
            if ((_strategy & GitCheckoutStrategy.UpdateOnly) != 0
                && !SafeForUpdateOnly(writePath, resultMode))
            {
                continue;
            }

            // Apply worktree filters (CRLF smudge etc.) to the MERGED buffer,
            // with result.Path as the attribute hint. Matches
            // checkout.c:2130-2145.
            byte[] outData = merged;
            if (!_opts.DisableFilters)
            {
                GitFilterList? filters = await GitFilterList.LoadAsync(_repo, GitPath.FromUtf8String(resultPath), null, GitFilterMode.ToWorktree, GitFilterListFlags.None, attrCommitId: null, cancellationToken).ConfigureAwait(false);
                if (filters is not null)
                {
                    outData = await filters.ApplyToBufferAsync(merged, cancellationToken).ConfigureAwait(false);
                    filters.Dispose();
                }
            }

            // Write the merged content with the merged result mode (exec bit
            // preserved) — matches git_filebuf_open(..., result.mode),
            // checkout.c:2147-2151.
            await WriteConflictFileAsync(Path.Join(workdir, writePath.ToFileSystemString()), outData, resultMode, cancellationToken).ConfigureAwait(false);

            _completedSteps++;
            ReportProgress(writePath);
        }

        // Port of checkout_conflict_update_index (checkout.c:2264-2265 +
        // 2181-2197): after writing the workdir files, re-add conflict stages
        // 1/2/3 to the repo's index (_index). This is the mechanism by which
        // conflict entries from the target index (e.g. stash's modifiedIndex)
        // reach the repo's index. Only when DONT_UPDATE_INDEX is not set and
        // SKIP_UNMERGED is not set (matching libgit2's gating).
        if (ShouldUpdateIndex && !skipUnmerged)
        {
            foreach (CheckoutConflictData conflict in _updateConflicts)
            {
                if (conflict.Ancestor is not null)
                {
                    _index.Add(conflict.Ancestor.Value);
                }

                if (conflict.Ours is not null)
                {
                    _index.Add(conflict.Ours.Value);
                }

                if (conflict.Theirs is not null)
                {
                    _index.Add(conflict.Theirs.Value);
                }
            }
        }
    }

    /// <summary>
    /// Writes a single conflict side to the workdir, applying
    /// <see cref="CheckoutConflictData.NameCollision"/>/
    /// <see cref="CheckoutConflictData.DirectoryFile"/> suffixing when
    /// appropriate. Matches <c>checkout_write_entry</c>
    /// (checkout.c:1990-2030). The output path is <paramref name="side"/>'s
    /// own <see cref="GitIndexEntry.Path"/> (NOT <paramref name="conflict"/>'s
    /// path), so 1-to-2 renames where ours and theirs have different paths
    /// each write to their own path. When <c>UseOurs</c>/<c>UseTheirs</c> is
    /// set, no suffixing is applied (matches the C guard at :2002-2004).
    /// </summary>
    private async Task WriteConflictSideAsync(CheckoutConflictData conflict, GitIndexEntry side, CancellationToken cancellationToken)
    {
        if (side.Id.IsZero)
        {
            return;
        }

        GitPath path = side.Path;

        // Suffix for name collisions and d/f conflicts (unless UseOurs/Theirs).
        // Matches checkout.c:2002-2018.
        if ((conflict.NameCollision || conflict.DirectoryFile)
            && (_strategy & GitCheckoutStrategy.UseOurs) == 0
            && (_strategy & GitCheckoutStrategy.UseTheirs) == 0)
        {
            string ourLabel = _opts.OurLabel ?? "ours";
            string theirLabel = _opts.TheirLabel ?? "theirs";
            string suffix = side == conflict.Ours ? ourLabel : theirLabel;
            path = SuffixPath(path, suffix);
        }

        // C's checkout_merge_path (checkout.c:2042-2068) validates the
        // merged conflict path (joinpath(target_directory, result->path))
        // before writing; reject traversal/.git paths here as well.
        if (!await GitPathValidator.IsValidAsync(path, PathRejectPresets.WorkdirDefaults, _repo, (ushort)side.Mode, cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(GitErrorCode.Error, $"cannot checkout to invalid path '{path.ToUtf8String()}'", GitErrorCategory.Checkout);
        }

        await _writer.WriteBlobAsync(path, side.Id, side.Mode, _canSymlink, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the merged conflict content to the workdir, creating parent
    /// directories and applying the merged result mode. Matches the tail of
    /// <c>checkout_write_merge</c> (checkout.c:2147-2151) where the file is
    /// opened with <c>result.mode</c> — the exec bit of an executable
    /// conflict is preserved regardless of <c>core.filemode</c>.
    /// </summary>
    private async Task WriteConflictFileAsync(string fullPath, byte[] content, GitFileMode mode, CancellationToken cancellationToken)
    {
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // C's
        // checkout_write_merge opens the output with git_filebuf
        // (checkout.c:2147-2151) — temp file + atomic rename, so a crash
        // mid-write leaves no partial conflict file. The merged mode is
        // applied at temp creation (umask-masked by the OS), like C's
        // result.mode.
        UnixFileMode? createMode = null;
        if (!OperatingSystem.IsWindows() && mode == GitFileMode.Executable)
        {
            createMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        }

        await AsyncFileIO.WriteAtomicAsync(fullPath, content, createLeadingDirs: false, createMode, cancellationToken).ConfigureAwait(false);
        if (createMode is not null)
        {
            _perf = _perf with { ChmodCalls = _perf.ChmodCalls + 1 };
        }
    }

    /// <summary> Appends <c>~&lt;suffix&gt;</c> to the path, with collision avoidance (<c>_0</c>, <c>_1</c>, … if the suffixed path already exists). Matches
    /// <c>checkout_path_suffixed</c> (checkout.c:1957-1985). The collision check uses <c>git_fs_path_exists</c> semantics — both files and directories count as
    /// collisions. Byte-faithful <see cref="GitPath"/>; the suffix and labels are ASCII so byte concat matches the C <c>git_str_printf(&amp;buf, "%s~%s", path,
    /// suffix)</c>. </summary>
    private GitPath SuffixPath(GitPath path, string suffix)
    {
        GitPath suffixed = path + "~" + suffix;

        // Collision avoidance: append _0, _1, … while the path exists.
        // Matches checkout.c:1966-1977.
        int i = 0;
        while (PathExistsInWorkdir(suffixed))
        {
            suffixed = path + "~" + suffix + "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            i++;

            if (i == int.MaxValue)
            {
                throw new GitException(
                    GitErrorCode.Exists,
                    $"could not write '{path.ToUtf8String()}~{suffix}': working directory file exists",
                    GitErrorCategory.Checkout);
            }
        }

        return suffixed;
    }

    /// <summary> Checks whether a relative path exists in the workdir (file or directory). Matches <c>git_fs_path_exists</c>. FS-boundary transcode
    /// (the byte-domain contract). </summary>
    private bool PathExistsInWorkdir(GitPath relativePath)
    {
        if (WorkdirRoot is null)
        {
            return false;
        }

        string fullPath = Path.Join(WorkdirRoot, relativePath.ToFileSystemString());
        return File.Exists(fullPath) || Directory.Exists(fullPath);
    }

    /// <summary>
    /// Returns true if the given git file mode is a symlink.
    /// </summary>
    private static bool IsSymlink(GitFileMode mode) => mode == GitFileMode.Symlink;

    private void UpdateIndexEntry(GitPath path, GitDiffFile file)
    {
        // Build an index entry from the diff file.
        var entry = new GitIndexEntry(path, file.Id, file.Mode);

        // Set stat info from the workdir file if it exists. Matches checkout_update_index (checkout.c:1642-1647) which calls git_index_entry__init_from_stat
        // (index.c:900-916): ctime/mtime/ dev/ino/uid/gid/size are all populated from the post-write stat (trust_mode=true — the mode comes from the file
        // itself, which the entry already carries). FS-boundary transcode (the byte-domain contract).
        string? fullPath = WorkdirRoot is not null
            ? Path.Join(WorkdirRoot, path.ToFileSystemString())
            : null;
        // the submodule pass stats the created DIRECTORY
        // (checkout_submodule_update_index p_stats the path, checkout.c:1650-
        // 1674) — File.Exists alone missed it.
        if (fullPath is not null && (File.Exists(fullPath) || Directory.Exists(fullPath)))
        {
            var info = new FileInfo(fullPath);
            (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size) = StatUtil.GetStatInfo(info);
            entry = entry with
            {
                Ctime = ctime,
                Mtime = mtime,
                Dev = dev,
                Ino = ino,
                Uid = uid,
                Gid = gid,
                FileSize = size,
            };
        }

        // Remove any existing conflict entries, then add the stage-0 entry.
        if (_index.HasConflicts)
        {
            _index.ConflictRemove(path);
        }
        _index.Add(entry);
    }

    // ── Notifications and progress ──────────────────────────────────────

    private void Notify(GitCheckoutNotifyFlags why, GitPath path, GitDiffFile? baseline, GitDiffFile? target, GitDiffFile? workdir)
    {
        if (_opts.Notify is null)
        {
            return;
        }

        if ((_opts.NotifyFlags & why) == 0)
        {
            return;
        }

        var notification = new GitCheckoutNotification(why, path, baseline, target, workdir);
        if (_opts.Notify(notification))
        {
            // C (checkout.c:142-148, errors.h:35-44): a nonzero callback return sets GIT_ERROR_CALLBACK (the managed category for the bool-returning delegate
            // keeps the GIT_EUSER code).
            throw new GitException(GitErrorCode.User, "checkout aborted by notification callback", GitErrorCategory.Callback);
        }
    }

    private void ReportProgress(GitPath path)
    {
        _opts.Progress?.Report(new GitCheckoutProgress(path, _completedSteps, _totalSteps));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private bool IsForce() => (_strategy & GitCheckoutStrategy.Force) != 0;

    private bool HasRecreateMissing() => (_strategy & GitCheckoutStrategy.RecreateMissing) != 0;

    /// <summary>
    /// True unless <c>GIT_CHECKOUT_DONT_UPDATE_INDEX</c> is set. Mirrors
    /// libgit2's per-write-site gating (e.g. checkout.c:1809, 1849, 2264):
    /// blob updates, removes, and conflict entries are only written to
    /// <c>data->index</c> when this is true.
    /// </summary>
    private bool ShouldUpdateIndex => (_strategy & GitCheckoutStrategy.DontUpdateIndex) == 0;

    /// <summary>
    /// Throws <see cref="GitErrorCode.Conflict"/> if any action was
    /// classified as a conflict and <c>GIT_CHECKOUT_ALLOW_CONFLICTS</c> is
    /// not set. Matches libgit2's <c>checkout_get_actions</c>
    /// (checkout.c:1372-1380) which aborts the checkout with
    /// <c>GIT_ECONFLICT</c> before any filesystem mutation when the
    /// <c>CONFLICT</c> action count is non-zero. UseOurs/UseTheirs/
    /// SkipUnmerged do NOT suppress the abort — they only affect conflict
    /// WRITING in <c>checkout_create_conflicts</c>.
    /// </summary>
    private void ThrowIfConflicts()
    {
        // AllowConflicts explicitly keeps the conflict actions (no throw).
        if ((_strategy & GitCheckoutStrategy.AllowConflicts) != 0)
        {
            return;
        }

        int n = _actions.Count(a => (a.Action & CheckoutAction.Conflict) != 0);
        if (n > 0)
        {
            throw new GitException(
                GitErrorCode.Conflict,
                $"{n} {(n == 1 ? "conflict prevents" : "conflicts prevent")} checkout",
                GitErrorCategory.Checkout);
        }
    }

    public void Dispose()
    {
        // No unmanaged resources to clean up.
    }
}
