// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;

using LibGit2CS.Attributes;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Diff;

/// <summary>
/// Central delta pipeline: lock-step iterator walk + delta-status
/// determination. Managed port of <c>src/libgit2/diff_generate.c</c> (1,750 LOC).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-phase split:</b> delta generation does NOT load blob
/// content or run XDiff. XDiff runs lazily when a <c>Patch</c> is materialized
/// (<c>PatchGenerator</c>). Iterating <c>Deltas</c> is cheap.
/// </para>
/// <para>
/// Submodule diff support, the filter pipeline, and attr sources are
/// fully wired.
/// </para>
/// </remarks>
internal sealed class DiffGenerator : IDiffPatchSource
{
    private readonly GitRepository _repo;
    private readonly IIterator _oldIter;
    private readonly IIterator _newIter;
    private GitDiffOptions _opts;
    private readonly List<GitDiffDelta> _deltas = [];

    // Diff capabilities (from config — diff_generated_apply_options).
    private bool _trustModeBits;
    private bool _trustCtime;
    private bool _hasSymlinks;
    private bool _ignoreCase;

    // Comparator slots — the 4-function-pointer dispatch model from libgit2's
    // git_diff_generated (set once in SetIgnoreCase, mirroring
    // diff_set_ignore_case at diff_generate.c:402-425). The GitPath
    // comparator family maps 1:1 onto these slots. Plain string/Ordinal
    // ternaries are gone — every path comparison routes through these.
    //
    //   strcomp   = git__strcmp  / git__strcasecmp   (entry path compare)
    //   strncomp  = git__strncmp / git__strncasecmp  (bounded path compare)
    //   pfxcomp   = git__prefixcmp / git__prefixcmp_icase (prefix match)
    //   entrycomp = git_diff__entry_cmp / git_diff__entry_icmp (iterator entry)
    //
    // Plus the delta-list sort comparator (git_diff_delta__cmp / _casecmp).
    // strcomp and pfxcomp are internal so DiffTransform.Merge (STRCMP_CASESELECT
    // in git_diff__merge, diff_tform.c:148) and CheckoutContext (strcomp/pfxcomp
    // slots pulled at checkout.c:678-679) can share the same slots — faithful to
    // libgit2 where the checkout reads data->diff->strcomp/pfxcomp directly.
    internal Func<GitPath, GitPath, int> _strcomp = GitPath.Compare;
    private Func<GitPath, GitPath, int, int> _strncomp = GitPath.Compare;
    internal Func<GitPath, GitPath, int> _pfxcomp = GitPath.ComparePrefix;
    private Func<GitIndexEntry, GitIndexEntry, int> _entrycomp = static (a, b) => GitPath.Compare(a.Path, b.Path);

    // Delta sort comparator (git_diff_delta__cmp / _casecmp). Internal so
    // DiffTransform.ApplySplitsAndDeletes can sort with the diff's own comparator
    // (git_vector_sort after apply_splits_and_deletes, diff_tform.c:428).
    internal Comparison<GitDiffDelta> _deltaCmp = GitDiffDelta.DeltaCompare;

    // Pathspec (null = no pathspec filtering).
    private GitPathSpec? _pathspec;

    // Driver registry (per-diff, owns attr files + macros).
    private DiffDriverRegistry? _driverRegistry;

    // True if the index was updated during this diff (GIT_DIFF_UPDATE_INDEX).
    // Set by DiffFileContent.ComputeWorkdirOid when it writes a refreshed
    // entry back to the index. Matches diff_generated::index_updated.
    internal bool _indexUpdated;

    // Prefixes for patch output.
    internal string OldPrefix { get; private set; } = "a/";
    internal string NewPrefix { get; private set; } = "b/";

    /// <summary>The generated deltas (read-only view).</summary>
    public IReadOnlyList<GitDiffDelta> Deltas => _deltas;

    /// <summary>The mutable delta list (for DiffTransform rename/merge mutations).</summary>
    internal List<GitDiffDelta> DeltaList => _deltas;

    /// <summary>The diff options (possibly modified by config).</summary>
    public GitDiffOptions Options => _opts;

    /// <summary>Number of patches (equals delta count). <see cref="IDiffPatchSource"/>.</summary>
    public int PatchCount => _deltas.Count;

    /// <summary>Retrieves the patch at <paramref name="index"/>. <see cref="IDiffPatchSource"/>.</summary>
    public async ValueTask<GitPatch> GetPatchAsync(int index, CancellationToken cancellationToken = default)
        => new(await PatchGenerator.FromDiffAsync(this, index, cancellationToken).ConfigureAwait(false));

    /// <summary>The repository.</summary>
    public GitRepository Repo => _repo;

    /// <summary>Iterator source types (for DiffFileContent init).</summary>
    internal IteratorType OldSrc => _oldIter.Type;
    internal IteratorType NewSrc => _newIter.Type;

    /// <summary>Driver registry (for PatchGenerator). Set during <see cref="GenerateAsync"/>.</summary>
    internal DiffDriverRegistry DriverRegistry => _driverRegistry ?? throw new InvalidOperationException("DiffGenerator not initialized");

    private DiffGenerator(GitRepository repo, IIterator oldIter, IIterator newIter)
    {
        _repo = repo;
        _oldIter = oldIter;
        _newIter = newIter;
        _opts = new GitDiffOptions();
    }

    /// <summary>
    /// Core entry: drives the lock-step walk. Matches
    /// <c>git_diff__from_iterators</c> (diff_generate.c:1254-1334).
    /// </summary>
    public static async Task<DiffGenerator> GenerateAsync(
        GitRepository repo, IIterator oldIter, IIterator newIter, GitDiffOptions? opts, CancellationToken cancellationToken)
    {
        var diff = new DiffGenerator(repo, oldIter, newIter);
        await diff.ApplyOptionsAsync(opts, cancellationToken).ConfigureAwait(false);

        GitIndexEntry? oitem = await oldIter.CurrentAsync(cancellationToken).ConfigureAwait(false);
        GitIndexEntry? nitem = await newIter.CurrentAsync(cancellationToken).ConfigureAwait(false);

        while (oitem is not null || nitem is not null)
        {
            // Progress callback (diff_generate.c:1293-1296).
            diff._opts.Progress?.Report(new GitDiffProgress(
                oitem is { } o ? o.Path : null,
                nitem is { } n ? n.Path : null));

            int cmp;
            if (oitem is null)
            {
                cmp = 1;
            }
            else if (nitem is null)
            {
                cmp = -1;
            }
            else
            {
                cmp = diff.CompareEntries(oitem.Value, nitem.Value);
            }

            if (cmp < 0)
            {
                Debug.Assert(oitem is not null, "cmp < 0 implies oitem is non-null");
                (GitIndexEntry? newItem, bool oldConsumed) = await diff.HandleUnmatchedOldAsync(oitem.Value, nitem, cancellationToken).ConfigureAwait(false);
                nitem = newItem;
                if (oldConsumed)
                {
                    oitem = await oldIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else if (cmp > 0)
            {
                Debug.Assert(nitem is not null, "cmp > 0 implies nitem is non-null");
                nitem = await diff.HandleUnmatchedNewAsync(nitem.Value, oitem, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Debug.Assert(oitem is not null && nitem is not null, "cmp == 0 implies both oitem and nitem are non-null");
                await diff.MaybeModifiedAsync(oitem.Value, nitem.Value, cancellationToken).ConfigureAwait(false);
                oitem = await oldIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
                nitem = await newIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return diff;
    }

    // ━━ Options / config ━━

    /// <summary>
    /// Applies diff options + config lookups. Matches
    /// <c>diff_generated_apply_options</c> (diff_generate.c:482-603).
    /// </summary>
    private async Task ApplyOptionsAsync(GitDiffOptions? opts, CancellationToken cancellationToken)
    {
        _opts = opts ?? new GitDiffOptions();

        // Seed OidType from the repository's object format if the caller left
        // it at the default (Sha1). Matches git_diff_options.oid_type being
        // initialized from the repo in diff_generated_apply_options.
        if (_opts.OidType == GitHashAlgorithmKind.Sha1
            && _repo.ObjectFormat != GitHashAlgorithmKind.Sha1)
        {
            _opts = _opts with { OidType = _repo.ObjectFormat };
        }

        // C's
        // diff_generated_apply_options implies INCLUDE_TYPECHANGE from
        // INCLUDE_TYPECHANGE_TREES and INCLUDE_UNTRACKED from
        // SHOW_UNTRACKED_CONTENT (diff_generate.c:510-516). Without these,
        // IncludeTypechangeTrees alone split blob→dir changes into
        // DELETED+ADDED, and ShowUntrackedContent alone produced no
        // untracked deltas at all.
        if ((_opts.Flags & GitDiffOptionsFlags.IncludeTypechangeTrees) != 0)
        {
            _opts = _opts with { Flags = _opts.Flags | GitDiffOptionsFlags.IncludeTypechange };
        }

        if ((_opts.Flags & GitDiffOptionsFlags.ShowUntrackedContent) != 0)
        {
            _opts = _opts with { Flags = _opts.Flags | GitDiffOptionsFlags.IncludeUntracked };
        }

        // Config lookups.
        GitConfiguration cfg = _repo.Config;

        // C (diff_generate.c:472-477): the ignore-case mode comes from the
        // ITERATORS (git_iterator_ignore_case(old) || ...) — tree-to-tree
        // iterators are DONT_IGNORE_CASE unless the user passes
        // GIT_DIFF_IGNORE_CASE (diff_generate.c:1382-1387), index-based
        // iterators follow the index's case mode.
        SetIgnoreCase(_oldIter.IgnoreCase || _newIter.IgnoreCase);

        // C (diff_generate.c:515-535, git_diff_generated__set_caps): the
        // diffcaps configmap lookups SWALLOW parse errors — an unparseable
        // value leaves the cap OFF (the "!lookup(...) && val" gate), so the
        // port must not fall back to the GIT_*_DEFAULT on an invalid value.
        // FILEMODE additionally requires GIT_DIFF_IGNORE_FILEMODE to be clear.
        bool ignoreFilemode = opts is not null && (opts.Flags & GitDiffOptionsFlags.IgnoreFilemode) != 0;
        _trustModeBits = !ignoreFilemode
            && (await cfg.TryGetConfigmapBoolAsync("core.filemode", true, cancellationToken).ConfigureAwait(false) ?? false);
        _trustCtime = await cfg.TryGetConfigmapBoolAsync("core.trustctime", true, cancellationToken).ConfigureAwait(false) ?? false;
        // C (repository.h:95): GIT_SYMLINKS_DEFAULT = GIT_CONFIGMAP_TRUE on
        // every platform — there is no Windows-specific default.
        _hasSymlinks = await cfg.TryGetConfigmapBoolAsync("core.symlinks", true, cancellationToken).ConfigureAwait(false) ?? false;
        // C sets GIT_DIFFCAPS_IGNORE_STAT from core.ignorestat but never
        // reads the cap (diff_generate.c:526) — faithfully not modeled.

        // diff.context: C (diff_generate.c:538-543) consults the config only
        // when the caller passed NO options (opts == NULL); a caller that
        // explicitly passes context_lines (even 3) keeps its value. Negative
        // config values clamp to 3.
        if (opts is null)
        {
            int ctx = await cfg.GetIntAsync("diff.context", 3, cancellationToken).ConfigureAwait(false);
            _opts = _opts with { ContextLines = ctx >= 0 ? ctx : 3 };
        }

        // C (diff_generate.c:560-569): diff.ignoresubmodules resolves the submodule-ignore level from config when the option is unspecified
        // (GIT_SUBMODULE_IGNORE_UNSPECIFIED = -1, so <= 0); a parse error is swallowed and the unspecified state kept.
        if (_opts.IgnoreSubmodules <= 0)
        {
            // byte-domain read (C's git_config_lookup_map_value strcasecmps raw bytes, submodule.c:1948-1960).
            byte[]? ignoreCfg = await cfg.GetBytesAsync("diff.ignoresubmodules", cancellationToken).ConfigureAwait(false);
            if (ignoreCfg is not null
                && SubmoduleCache.TryParseIgnore(ignoreCfg, out SubmoduleIgnore ignoreLevel))
            {
                _opts = _opts with { IgnoreSubmodules = (GitDiffIgnoreSubmodules)ignoreLevel };
            }
        }

        // Prefixes (diff_generated_apply_options:572-602). opts.OldPrefix/
        // NewPrefix default to "a/"/"b/"; a null value means "derive from
        // config" (diff.noprefix / diff.mnemonicprefix / builtin default).
        string? oldPrefix = _opts.OldPrefix;
        string? newPrefix = _opts.NewPrefix;
        if (oldPrefix is null || newPrefix is null)
        {
            if (await cfg.GetBoolAsync("diff.noprefix", false, cancellationToken).ConfigureAwait(false))
            {
                oldPrefix = string.Empty;
                newPrefix = string.Empty;
            }
            else if (await cfg.GetBoolAsync("diff.mnemonicprefix", false, cancellationToken).ConfigureAwait(false))
            {
                ApplyMnemonicPrefixes();
                oldPrefix = OldPrefix;
                newPrefix = NewPrefix;
            }
            else
            {
                oldPrefix ??= "a/";
                newPrefix ??= "b/";
            }
        }

        OldPrefix = oldPrefix;
        NewPrefix = newPrefix;

        // REVERSE: swap the prefixes (diff_generate.c:593-596) so the header
        // reads "diff --git b/x a/x" and "--- b/x" / "+++ a/x".
        if ((_opts.Flags & GitDiffOptionsFlags.Reverse) != 0)
        {
            (OldPrefix, NewPrefix) = (NewPrefix, OldPrefix);
        }

        // Pathspec.
        if (_opts.PathSpecs is { Length: > 0 } specs)
        {
            _pathspec = GitPathSpec.New(specs);
        }

        // Driver registry + attr files.
        AttributeCache attrCache = await _repo.GetAttributeCacheAsync(cancellationToken).ConfigureAwait(false);
        _driverRegistry = new DiffDriverRegistry(_repo, _ignoreCase, attrCache);

        // Unset UPDATE_INDEX unless diffing workdir AND index.
        // Matches diff_generated_apply_options (diff_generate.c:552-558).
        if ((_opts.Flags & GitDiffOptionsFlags.UpdateIndex) != 0)
        {
            bool oldIsWorkdir = _oldIter.Type == IteratorType.Workdir;
            bool newIsWorkdir = _newIter.Type == IteratorType.Workdir;
            bool oldIsIndex = _oldIter.Type == IteratorType.Index;
            bool newIsIndex = _newIter.Type == IteratorType.Index;

            if (!((oldIsWorkdir || newIsWorkdir) && (oldIsIndex || newIsIndex)))
            {
                _opts = _opts with { Flags = _opts.Flags & ~GitDiffOptionsFlags.UpdateIndex };
            }
        }
    }

    private void ApplyMnemonicPrefixes()
    {
        // Mnemonic prefixes from diff_mnemonic_prefix (diff_generate.c:381-400).
        // c = commit, i = index, w = workdir, o = (object/tree).
        // For tree-to-tree: a/b. For tree-to-index: c/i. For index-to-workdir: i/w.
        IteratorType oldType = _oldIter.Type;
        IteratorType newType = _newIter.Type;

        OldPrefix = oldType switch
        {
            IteratorType.Tree when newType == IteratorType.Tree => "a/",
            IteratorType.Tree when newType == IteratorType.Index => "c/",
            IteratorType.Index when newType == IteratorType.Workdir => "i/",
            IteratorType.Tree when newType == IteratorType.Workdir => "c/",
            _ => "a/",
        };

        NewPrefix = newType switch
        {
            IteratorType.Tree when oldType == IteratorType.Tree => "b/",
            IteratorType.Index when oldType == IteratorType.Tree => "i/",
            IteratorType.Workdir when oldType == IteratorType.Index => "w/",
            IteratorType.Workdir when oldType == IteratorType.Tree => "w/",
            _ => "b/",
        };
    }

    /// <summary>
    /// Sets the case-sensitivity mode and (re)wires the 4 comparator slots +
    /// delta sort comparator. Ports <c>diff_set_ignore_case</c> 1:1
    /// (<c>diff_generate.c:402-425</c>): each slot swaps between the
    /// case-sensitive and ASCII-fold (<c>git__strcasecmp</c>) GitPath static.
    /// </summary>
    private void SetIgnoreCase(bool ignoreCase)
    {
        _ignoreCase = ignoreCase;

        if (!ignoreCase)
        {
            _opts = _opts with { Flags = _opts.Flags & ~GitDiffOptionsFlags.IgnoreCase };

            _strcomp = GitPath.Compare;
            _strncomp = GitPath.Compare;
            _pfxcomp = GitPath.ComparePrefix;
            _entrycomp = static (a, b) => GitPath.Compare(a.Path, b.Path);
            _deltaCmp = GitDiffDelta.DeltaCompare;
        }
        else
        {
            _opts = _opts with { Flags = _opts.Flags | GitDiffOptionsFlags.IgnoreCase };

            _strcomp = GitPath.CompareIgnoreCase;
            _strncomp = GitPath.CompareIgnoreCase;
            _pfxcomp = GitPath.ComparePrefixIgnoreCase;
            _entrycomp = static (a, b) => GitPath.CompareIgnoreCase(a.Path, b.Path);
            _deltaCmp = GitDiffDelta.DeltaCompareIgnoreCase;
        }

        _deltas.Sort(_deltaCmp);
    }

    /// <summary>
    /// Returns true if this diff is sorted case-insensitively. Ports
    /// <c>git_diff_is_sorted_icase</c> (<c>diff.c:111-114</c>).
    /// </summary>
    internal bool IsSortedIcase => (_opts.Flags & GitDiffOptionsFlags.IgnoreCase) != 0;

    // ━━ Entry comparison ━━

    private int CompareEntries(GitIndexEntry a, GitIndexEntry b) => _entrycomp(a, b);

    // ━━ Pathspec matching ━━

    /// <summary>
    /// Matches an entry against the pathspec. Matches <c>diff_pathspec_match</c>
    /// (diff_generate.c:97-121).
    /// </summary>
    private bool PathspecMatch(GitIndexEntry entry, out GitPath? matchedPathspec)
    {
        if (_pathspec is null)
        {
            matchedPathspec = null;
            return true;
        }

        bool disableMatch = (_opts.Flags & GitDiffOptionsFlags.DisablePathspecMatch) != 0;

        // When DISABLE_PATHSPEC_MATCH is set, the pathspec is applied as a
        // literal path filter (NoGlob) rather than a glob match. In libgit2,
        // the iterator applies the pathspec via its pathlist and the diff
        // engine bypasses matching for regular files. However, the C# workdir
        // iterator does not carry the pathspec (it walks the full filesystem),
        // so the bypass is unsafe — we must still apply the literal match here
        // for all entry types. The tree/index iterators DO carry the pathlist
        // and pre-filter, so this per-entry match is redundant for them but
        // correct (the NoGlob literal match produces the same result).
        var matchFlags = (GitPathSpec.MatchFlags)0;
        if (disableMatch)
        {
            matchFlags |= GitPathSpec.MatchFlags.NoGlob;
        }

        if (_ignoreCase)
        {
            matchFlags |= GitPathSpec.MatchFlags.IgnoreCase;
        }

        // Use the byte-faithful GitPath overload so non-UTF-8 paths match
        // byte-exact. The empty GitPath (default) means "no pattern matched";
        // a non-empty GitPath is the matched pattern.
        GitPath matched = _pathspec.MatchPathspec(entry.Path, matchFlags);
        matchedPathspec = matched.IsEmpty ? null : matched;
        return !matched.IsEmpty || _pathspec.IsEmpty;
    }

    // ━━ Delta creation ━━

    /// <summary>
    /// Creates a single-sided delta (DELETE/ADD/UNTRACKED/IGNORED/UNREADABLE).
    /// Matches <c>diff_delta__from_one</c> (diff_generate.c:143-219).
    /// </summary>
    private bool TryCreateFromOne(GitDeltaStatus status, GitIndexEntry? oitem, GitIndexEntry? nitem)
    {
        GitIndexEntry entry = nitem ?? oitem ?? throw new InvalidOperationException("either nitem or oitem must be non-null");
        bool hasOld = oitem is not null;

        // REVERSE: swap old/new sides AND invert add/delete status. Matches
        // libgit2 where GIT_DIFF_REVERSE flips has_old in diff_delta__from_one
        // (diff_generate.c:164) and swaps ADDED↔DELETED in diff_delta__alloc
        // (diff_generate.c:55-64).
        if ((_opts.Flags & GitDiffOptionsFlags.Reverse) != 0)
        {
            hasOld = !hasOld;
            status = status switch
            {
                GitDeltaStatus.Added => GitDeltaStatus.Deleted,
                GitDeltaStatus.Deleted => GitDeltaStatus.Added,
                _ => status,
            };
        }

        // Assume-unchanged: skip.
        if ((entry.Flags & GitIndexEntry.Valid) != 0)
        {
            return false;
        }

        // Flag-based filtering.
        if (status == GitDeltaStatus.Ignored &&
            (_opts.Flags & GitDiffOptionsFlags.IncludeIgnored) == 0)
        {
            return false;
        }

        if (status == GitDeltaStatus.Untracked &&
            (_opts.Flags & GitDiffOptionsFlags.IncludeUntracked) == 0)
        {
            return false;
        }

        if (status == GitDeltaStatus.Unreadable &&
            (_opts.Flags & GitDiffOptionsFlags.IncludeUnreadable) == 0)
        {
            return false;
        }

        // Pathspec filtering.
        if (!PathspecMatch(entry, out GitPath? matchedPathspec))
        {
            return false;
        }

        GitDiffDelta delta = CreateDeltaFromOne(status, entry, hasOld);
        InsertDelta(delta, matchedPathspec);
        return true;
    }

    /// <summary>
    /// Inserts a delta into the list, invoking the notify callback first.
    /// Matches <c>diff_insert_delta</c> (diff_generate.c:70-95): positive
    /// return from notify → skip delta; negative → abort diff.
    /// </summary>
    private void InsertDelta(GitDiffDelta delta, GitPath? matchedPathspec)
    {
        if (_opts.Notify is { } notify)
        {
            int result = notify(delta, matchedPathspec);
            if (result < 0)
            {
                throw new InvalidOperationException("diff aborted by notify callback");
            }

            if (result > 0)
            {
                return; // skip this delta
            }
        }

        _deltas.Add(delta);
    }

    /// <summary>
    /// Creates a two-sided delta (MODIFIED/UNMODIFIED/TYPECHANGE/CONFLICTED).
    /// Matches <c>diff_delta__from_two</c> (diff_generate.c:221-286).
    /// </summary>
    private void CreateFromTwo(
        GitDeltaStatus status,
        GitIndexEntry oldEntry, uint oldMode,
        GitIndexEntry newEntry, uint newMode,
        GitOid? newId, GitPath? matchedPathspec)
    {
        // INCLUDE_UNMODIFIED check.
        if (status == GitDeltaStatus.Unmodified &&
            (_opts.Flags & GitDiffOptionsFlags.IncludeUnmodified) == 0)
        {
            return;
        }

        GitIndexEntry actualOldEntry = oldEntry;
        GitIndexEntry actualNewEntry = newEntry;
        uint actualOldMode = oldMode;
        uint actualNewMode = newMode;
        GitOid actualOldId = oldEntry.Id;
        GitOid actualNewId = newId ?? newEntry.Id;

        // REVERSE: swap old/new.
        if ((_opts.Flags & GitDiffOptionsFlags.Reverse) != 0)
        {
            (actualOldEntry, actualNewEntry) = (actualNewEntry, actualOldEntry);
            (actualOldMode, actualNewMode) = (actualNewMode, actualOldMode);
            (actualOldId, actualNewId) = (actualNewId, actualOldId);
        }

        GitDiffDelta delta = CreateDeltaFromTwo(
            status, actualOldEntry, actualOldMode, actualNewEntry, actualNewMode, actualOldId, actualNewId);
        InsertDelta(delta, matchedPathspec);
    }

    /// <summary>
    /// Allocates and populates a single-sided delta. Matches
    /// <c>diff_delta__alloc</c> (diff_generate.c:38-68) + the field-setting in
    /// <c>diff_delta__from_one</c> (diff_generate.c:195-216).
    /// </summary>
    internal static GitDiffDelta CreateDeltaFromOne(GitDeltaStatus status, GitIndexEntry entry, bool hasOld)
    {
        GitPath path = entry.Path;

        var oldFile = new GitDiffFile { Path = path };
        var newFile = new GitDiffFile { Path = path };

        if (hasOld)
        {
            oldFile.Mode = entry.Mode;
            oldFile.Size = entry.FileSize;
            oldFile.Id = entry.Id;
            oldFile.Flags = GitDiffFileFlags.Exists | GitDiffFileFlags.ValidId;
            oldFile.IdAbbrev = entry.Id.HexLength;
        }
        else
        {
            newFile.Mode = entry.Mode;
            newFile.Size = entry.FileSize;
            newFile.Id = entry.Id;
            newFile.Flags = GitDiffFileFlags.Exists;
            newFile.IdAbbrev = entry.Id.HexLength;

            if (!entry.Id.IsZero)
            {
                newFile.Flags |= GitDiffFileFlags.ValidId;
            }
        }

        oldFile.Flags |= GitDiffFileFlags.ValidId;

        // C's
        // diff_delta__from_one sets VALID_ID on the new_file whenever
        // has_old (diff_generate.c:211-214) — a DELETED delta's empty new
        // side carries VALID_ID, which also flips VALID_SIZE via
        // FlagKnownSizes. (The else branch above already sets it for
        // non-zero ids; the combined condition mirrors C exactly.)
        if (hasOld || !newFile.Id.IsZero)
        {
            newFile.Flags |= GitDiffFileFlags.ValidId;
        }

        FlagKnownSizes(oldFile);
        FlagKnownSizes(newFile);

        return new GitDiffDelta(status, 1, oldFile, newFile);
    }

    /// <summary>
    /// Allocates and populates a two-sided delta. Matches
    /// <c>diff_delta__alloc</c> + field-setting in
    /// <c>diff_delta__from_two</c> (diff_generate.c:258-284).
    /// </summary>
    internal static GitDiffDelta CreateDeltaFromTwo(
        GitDeltaStatus status,
        GitIndexEntry oldEntry, uint oldMode,
        GitIndexEntry newEntry, uint newMode,
        GitOid oldId, GitOid newId)
    {
        GitPath path = oldEntry.Path;
        var oldFile = new GitDiffFile { Path = path };
        var newFile = new GitDiffFile { Path = path };

        if (!oldEntry.IsConflict)
        {
            oldFile.Size = oldEntry.FileSize;
            oldFile.Mode = (GitFileMode)oldMode;
            oldFile.Id = oldId;
            oldFile.IdAbbrev = oldId.HexLength;
            oldFile.Flags = GitDiffFileFlags.ValidId | GitDiffFileFlags.Exists;
        }

        if (!newEntry.IsConflict)
        {
            newFile.Id = newId;
            newFile.IdAbbrev = newId.HexLength;
            newFile.Size = newEntry.FileSize;
            newFile.Mode = (GitFileMode)newMode;
            oldFile.Flags |= GitDiffFileFlags.Exists;
            newFile.Flags = GitDiffFileFlags.Exists;

            if (!newId.IsZero)
            {
                newFile.Flags |= GitDiffFileFlags.ValidId;
            }
        }

        FlagKnownSizes(oldFile);
        FlagKnownSizes(newFile);

        return new GitDiffDelta(status, 2, oldFile, newFile);
    }

    /// <summary>
    /// Sets VALID_SIZE if the file has a known size. Matches
    /// <c>diff_delta__flag_known_size</c> (diff_generate.c:123-135).
    /// </summary>
    private static void FlagKnownSizes(GitDiffFile file)
    {
        if (file.Size != 0 ||
            (file.Flags & GitDiffFileFlags.ValidId) == 0 ||
            file.Id == GitOid.EmptyBlobSha1)
        {
            file.Flags |= GitDiffFileFlags.ValidSize;
        }
    }

    // ━━ State machine ━━

    /// <summary>
    /// Handles a matched item pair (paths equal). Matches
    /// <c>handle_matched_item</c> (diff_generate.c:1240-1252) →
    /// <c>maybe_modified</c> (diff_generate.c:803-949).
    /// </summary>
    private async Task MaybeModifiedAsync(GitIndexEntry oitem, GitIndexEntry nitem, CancellationToken cancellationToken)
    {
        GitDeltaStatus status = GitDeltaStatus.Modified;
        uint omode = (uint)oitem.Mode;
        uint nmode = (uint)nitem.Mode;
        bool newIsWorkdir = _newIter.Type == IteratorType.Workdir;
        GitOid? noid = null;

        // C tracks
        // modified_uncertain separately from the status — set only for size
        // 0→positive or the mtime/ctime/ino/uid/gid/racy branch
        // (diff_generate.c:889-903) — and recomputes the workdir OID only
        // when it is set. Gating on `status == Modified && Id.IsZero` would
        // recompute for every content-modified file (the common size-change
        // case), populating delta.NewFile.Id (and triggering the
        // UPDATE_INDEX stat-cache refresh) where C leaves the id zero.
        bool modifiedUncertain = false;

        // Pathspec filtering.
        if (!PathspecMatch(oitem, out GitPath? matchedPathspec))
        {
            return;
        }

        // Platform symlink/execmode adjustments (diff_generate.c:821-832).
        if (!_hasSymlinks && oitem.Mode == GitFileMode.Symlink &&
            nitem.Mode == GitFileMode.Regular && newIsWorkdir)
        {
            nmode = omode;
        }

        // On platforms with no execmode (core.filemode=false), preserve old
        // mode bits for workdir files (diff_generate.c:829-832).
        // MODE_BITS_MASK = 0o777 = 0x1FF (permission bits rwxrwxrwx).
        const uint ModeBitsMask = 0x1FFu;
        if (!_trustModeBits && newIsWorkdir &&
            (nmode & ModeBitsMask) != (omode & ModeBitsMask))
        {
            nmode = (nmode & ~ModeBitsMask) | (omode & ModeBitsMask);
        }

        // Conflict → CONFLICTED.
        if (oitem.IsConflict || nitem.IsConflict)
        {
            status = GitDeltaStatus.Conflicted;
        }
        // Assume-unchanged → UNMODIFIED.
        else if ((oitem.Flags & GitIndexEntry.Valid) != 0)
        {
            status = GitDeltaStatus.Unmodified;
        }
        // Skip-worktree → UNMODIFIED.
        else if ((oitem.FlagsExtended & GitIndexEntry.SkipWorktree) != 0)
        {
            status = GitDeltaStatus.Unmodified;
        }
        // Typechange: basic file type differs.
        else if (ModeType(omode) != ModeType(nmode))
        {
            if ((_opts.Flags & GitDiffOptionsFlags.IncludeTypechange) != 0)
            {
                status = GitDeltaStatus.Typechange;
            }
            else if ((int)nitem.Mode == 0)
            {
                // C (diff_generate.c:853-857): GIT_FILEMODE_UNREADABLE (0) —
                // an unreadable workdir file yields DELETED + UNREADABLE
                // deltas.
                TryCreateFromOne(GitDeltaStatus.Deleted, oitem, null);
                TryCreateFromOne(GitDeltaStatus.Unreadable, null, nitem);
                return;
            }
            else
            {
                // Split into DELETE + ADD.
                TryCreateFromOne(GitDeltaStatus.Deleted, oitem, null);
                TryCreateFromOne(GitDeltaStatus.Added, null, nitem);
                return;
            }
        }
        // OID + mode match → UNMODIFIED.
        else if (oitem.Id == nitem.Id && omode == nmode && !oitem.Id.IsZero)
        {
            status = GitDeltaStatus.Unmodified;
        }
        // Workdir OID unknown — stat heuristic.
        else if (nitem.Id.IsZero && newIsWorkdir)
        {
            status = GitDeltaStatus.Unmodified;

            // Submodule: compare submodule HEAD OID with index gitlink OID. C (diff_generate.c:747-753,
            // maybe_modified_submodule): the diff's ignore_submodules level (resolved from diff.ignoresubmodules config) gates this path too.
            if (nitem.Mode == GitFileMode.GitLink)
            {
                bool ignoreFlag = (_opts.Flags & GitDiffOptionsFlags.IgnoreSubmodules) != 0;
                if (!ignoreFlag && _opts.IgnoreSubmodules != GitDiffIgnoreSubmodules.All)
                {
                    GitOid smHeadOid = await DiffFileContent.ComputeWorkdirOidAsync(_repo, nitem.Path, GitFileMode.GitLink, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!smHeadOid.IsZero && smHeadOid != oitem.Id)
                    {
                        status = GitDeltaStatus.Modified;
                    }

                    // C (maybe_modified_submodule → git_submodule__status,
                    // diff_generate.c:912-925): the submodule HEAD OID is
                    // reported as the new-file OID (submodule_copy_oid_maybe
                    // zeroes it when invalid).
                    noid = smHeadOid;
                }
            }
            // Stat differs by mode or size → MODIFIED.
            else if (omode != nmode || oitem.FileSize != nitem.FileSize)
            {
                status = GitDeltaStatus.Modified;

                // C (diff_generate.c:889-893): modified_uncertain only for
                // size 0→positive.
                modifiedUncertain = oitem.FileSize <= 0 && nitem.FileSize > 0;
            }
            // Stat differs by mtime/ctime/ino/uid/gid, OR racy-git → MODIFIED.
            // The EntryNewerThanIndex check matches C's
            // git_index_entry_newer_than_index (diff_generate.c:884,
            // index.h:101-118): if the file's mtime is newer than OR equal to
            // the index file's own mtime (Stamp), the entry is "racy" and the
            // OID must be recomputed (below) to confirm. This catches
            // same-second modifications where the stat fields would otherwise
            // compare equal (the "zero-stat" problem).
            else if (!IndexTimeEquals(oitem.Mtime, nitem.Mtime) ||
                     (_trustCtime && !IndexTimeEquals(oitem.Ctime, nitem.Ctime)) ||
                     oitem.Ino != nitem.Ino ||
                     oitem.Uid != nitem.Uid ||
                     oitem.Gid != nitem.Gid ||
                     EntryNewerThanIndex(nitem, _newIter.Index))
            {
                status = GitDeltaStatus.Modified;
                modifiedUncertain = true;
            }

            // Recompute workdir OID to confirm modification (C:
            // diff_generate.c:908-930, gated on modified_uncertain).
            if (modifiedUncertain && nitem.Id.IsZero)
            {
                // Pass updateMatch when UPDATE_INDEX is set and modes match
                // (diff_generate.c:914-920). This tells ComputeWorkdirOid to
                // refresh the index stat cache if the OID matches.
                GitOid? updateMatch = null;
                if ((_opts.Flags & GitDiffOptionsFlags.UpdateIndex) != 0 && omode == nmode)
                {
                    updateMatch = oitem.Id;
                }

                noid = await DiffFileContent.ComputeWorkdirOidAsync(
                    _repo, nitem.Path, nitem.Mode, updateMatch, this, cancellationToken).ConfigureAwait(false);

                // If OID matches old, file is unmodified (not a submodule).
                if (omode == nmode && oitem.Mode != GitFileMode.GitLink &&
                    oitem.Id == noid)
                {
                    status = GitDeltaStatus.Unmodified;
                }
            }
        }
        // Gitlink + ignore submodules → UNMODIFIED. Respects the
        // DiffIgnoreSubmodules enum granularity (None/Untracked/Dirty/All).
        else if (nitem.Mode == GitFileMode.GitLink)
        {
            GitDiffIgnoreSubmodules ignoreLevel = _opts.IgnoreSubmodules;
            bool ignoreFlag = (_opts.Flags & GitDiffOptionsFlags.IgnoreSubmodules) != 0;

            if (ignoreFlag || ignoreLevel == GitDiffIgnoreSubmodules.All)
            {
                // Full ignore → always unmodified.
                status = GitDeltaStatus.Unmodified;
            }
            else if (ignoreLevel == GitDiffIgnoreSubmodules.None)
            {
                // No ignore → compare OIDs normally (fall through to default handling).
            }
            else
            {
                // Untracked or Dirty: compare OIDs. If OIDs match, check for dirty
                // workdir — if Dirty/Untracked and the submodule is clean, treat
                // as unmodified.
                if (omode == nmode && oitem.Id == nitem.Id)
                {
                    status = GitDeltaStatus.Unmodified;
                }
                else if (omode == nmode)
                {
                    status = GitDeltaStatus.Modified;
                }
            }
        }

        // Case-change split (IGNORE_CASE + INCLUDE_CASECHANGE). The raw byte
        // compare (git__strcmp, not the icase slot) detects a case-only rename
        // — the paths folded equal to reach here, but their raw bytes differ.
        // Matches diff_generate.c:938 (strcmp(oitem->path, nitem->path) != 0).
        if ((_opts.Flags & GitDiffOptionsFlags.IgnoreCase) != 0 &&
            (_opts.Flags & GitDiffOptionsFlags.IncludeCasechange) != 0 &&
            !oitem.Path.Equals(nitem.Path))
        {
            TryCreateFromOne(GitDeltaStatus.Deleted, oitem, null);
            TryCreateFromOne(GitDeltaStatus.Added, null, nitem);
            return;
        }

        CreateFromTwo(status, oitem, omode, nitem, nmode, noid, matchedPathspec);
    }

    /// <summary>
    /// Handles an old item with no matching new item → DELETE. Matches
    /// <c>handle_unmatched_old_item</c> (diff_generate.c:1202-1238).
    /// </summary>
    /// <returns>
    /// The (possibly advanced) current new item, and whether the old item
    /// was consumed.
    /// in the TREE-skip case C advances ONLY the new iterator
    /// (diff_generate.c:1232-1234), leaving the old item in place so the
    /// main loop re-evaluates it and creates a second plain DELETED record
    /// </returns>
    private async Task<(GitIndexEntry? NewItem, bool OldConsumed)> HandleUnmatchedOldAsync(GitIndexEntry oitem, GitIndexEntry? nitem, CancellationToken cancellationToken)
    {
        GitDeltaStatus status = oitem.IsConflict ? GitDeltaStatus.Conflicted : GitDeltaStatus.Deleted;
        TryCreateFromOne(status, oitem, null);

        // C (diff_generate.c:1211-1227): with INCLUDE_TYPECHANGE_TREES, an old
        // item whose path has become a tree (the current new item is prefixed
        // by it) converts the DELETED record to a TYPECHANGE with
        // new_file.mode = TREE. Without this, a blob→tree change surfaces as
        // DELETE + ADD and the checkout TYPECHANGE action is never exercised.
        if ((_opts.Flags & GitDiffOptionsFlags.IncludeTypechangeTrees) != 0 &&
            nitem is { } n &&
            EntryIsPrefixed(n, oitem))
        {
            GitDiffDelta? last = LastDeltaForItem(oitem);
            if (last is not null)
            {
                last.Status = GitDeltaStatus.Typechange;
                last.NewFile.Mode = GitFileMode.Tree;
            }

            // C (diff_generate.c:1222-1226): with a workdir new iterator this
            // situation is followed by a series of untracked items — skip the
            // directory unless RECURSE_UNTRACKED_DIRS is set. The old item is
            // NOT consumed: the main loop re-evaluates it and emits a second
            // plain DELETED record.
            if (n.Mode == GitFileMode.Tree &&
                (_opts.Flags & GitDiffOptionsFlags.RecurseUntrackedDirs) == 0 &&
                _newIter is FilesystemIterator)
            {
                return (await _newIter.AdvanceAsync(cancellationToken).ConfigureAwait(false), false);
            }
        }

        return (nitem, true);
    }

    /// <summary>
    /// Handles a new item with no matching old item → ADD/UNTRACKED/IGNORED.
    /// Matches <c>handle_unmatched_new_item</c> (diff_generate.c:1045-1200).
    /// </summary>
    private async Task<GitIndexEntry?> HandleUnmatchedNewAsync(GitIndexEntry nitem, GitIndexEntry? oitem, CancellationToken cancellationToken)
    {
        GitDeltaStatus status = GitDeltaStatus.Untracked;

        // Check if the old side has an item inside this new item's directory
        // (diff_generate.c:1053-1054, entry_is_prefixed).
        bool containsOitem = EntryIsPrefixed(oitem, nitem);

        // Conflict → CONFLICTED.
        if (nitem.IsConflict)
        {
            status = GitDeltaStatus.Conflicted;
        }
        // Ignored — consult the workdir iterator's ignore engine
        // (diff_generate.c:1060-1062). Only the FilesystemIterator (workdir
        // backend) has ignore state; the IndexIterator/TreeIterator do not.
        else if (_newIter is FilesystemIterator ignoreFsIter && ignoreFsIter.CurrentIsIgnored())
        {
            status = GitDeltaStatus.Ignored;
        }

        // Directory: decide whether to recurse.
        if (nitem.Mode == GitFileMode.Tree)
        {
            // Tracked files inside the directory must surface (deletions and
            // edits inside it), so recurse if the old side has an item
            // prefixed by this directory (diff_generate.c:1064-1072).
            bool recurseIntoDir = containsOitem
                || (status == GitDeltaStatus.Untracked &&
                    (_opts.Flags & GitDiffOptionsFlags.RecurseUntrackedDirs) != 0)
                || (status == GitDeltaStatus.Ignored &&
                    (_opts.Flags & GitDiffOptionsFlags.RecurseIgnoredDirs) != 0);

            // Do not advance into directories that contain a .git file
            // (diff_generate.c:1074-1083) — unless the directory contains
            // tracked items (contains_oitem).
            if (recurseIntoDir && !containsOitem && _newIter is FilesystemIterator dirFsIter)
            {
                string? dirPath = dirFsIter.CurrentWorkdirPath();
                if (dirPath is not null && PathContainsDotGit(dirPath))
                {
                    recurseIntoDir = false;
                }
            }

            // Still have to look into untracked directories to match core
            // git — with no untracked files, the directory is treated as
            // ignored (diff_generate.c:1085-1128).
            if (!recurseIntoDir &&
                status == GitDeltaStatus.Untracked &&
                (_opts.Flags & GitDiffOptionsFlags.EnableFastUntrackedDirs) == 0)
            {
                // Attempt to insert a record for this directory.
                TryCreateFromOne(status, null, nitem);

                // If the delta wasn't created (flag/pathspec/notify rules),
                // just skip ahead (diff_generate.c:1100-1102).
                GitDiffDelta? last = LastDeltaForItem(nitem);
                if (last is null)
                {
                    return await _newIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
                }

                // Iterate into the dir looking for an actual untracked file
                // (diff_generate.c:1104-1107).
                (GitIndexEntry? advanced, IteratorStatus untrackedState) =
                    await _newIter.AdvanceOverAsync(cancellationToken).ConfigureAwait(false);

                // If we found nothing that matched our pathlist filter,
                // exclude the record (diff_generate.c:1109-1113).
                if (untrackedState == IteratorStatus.Filtered)
                {
                    _deltas.RemoveAt(_deltas.Count - 1);
                    return advanced;
                }

                // If we found nothing or just ignored items, update the
                // record to IGNORED (diff_generate.c:1115-1125).
                if (untrackedState is IteratorStatus.Ignored or IteratorStatus.Empty)
                {
                    last.Status = GitDeltaStatus.Ignored;

                    // Remove the record if we don't want ignored records.
                    if ((_opts.Flags & GitDiffOptionsFlags.IncludeIgnored) == 0)
                    {
                        _deltas.RemoveAt(_deltas.Count - 1);
                    }
                }

                return advanced;
            }

            // Try to advance into the directory if necessary
            // (diff_generate.c:1130-1141).
            if (recurseIntoDir)
            {
                GitIndexEntry? advanced = await _newIter.AdvanceIntoAsync(cancellationToken).ConfigureAwait(false);

                // If the directory is empty, can't advance into it, so skip it.
                return advanced ?? await _newIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
            }

            // Fast untracked dirs, or an ignored dir without
            // RECURSE_IGNORED_DIRS: report the directory itself and skip
            // past it (falls through to the common tail below).
        }

        // Ignored file inside an ignored directory, without
        // RECURSE_IGNORED_DIRS → skip it (diff_generate.c:1144-1148).
        if (status == GitDeltaStatus.Ignored &&
            (_opts.Flags & GitDiffOptionsFlags.RecurseIgnoredDirs) == 0 &&
            _newIter is FilesystemIterator skipFsIter && skipFsIter.CurrentTreeIsIgnored())
        {
            return await _newIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
        }

        // Non-workdir iterator: everything is ADDED (not UNTRACKED).
        if (_newIter.Type != IteratorType.Workdir)
        {
            if (status != GitDeltaStatus.Conflicted)
            {
                status = GitDeltaStatus.Added;
            }
        }
        // Gitlink (submodule) in workdir with no index entry.
        // If the submodule is initialized (has .git), treat as untracked
        // only when not in .gitmodules. If it's a configured submodule, skip
        // it (it's handled by the index-side comparison).
        else if (nitem.Mode == GitFileMode.GitLink)
        {
            // C (diff_generate.c:1155-1171): a CONFIGURED submodule keeps
            // UNTRACKED and a record IS created; a non-configured submodule
            // becomes IGNORED (subject to INCLUDE_IGNORED) — unless it
            // contains a tracked item, which is treated as a normal TREE via
            // advance_into.
            // git_submodule_lookup errors (ENOTFOUND/EEXISTS) on a miss
            // — the non-configured-submodule case (diff_generate.c:1155-1171).
            bool isConfiguredSubmodule = true;
            try
            {
                // GitPath lookup — C's git_submodule_lookup takes the raw path bytes (submodule.c:308-433).
                _ = await GitSubmodule.LookupAsync(_repo, nitem.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException)
            {
                isConfiguredSubmodule = false;
            }

            if (!isConfiguredSubmodule)
            {
                status = GitDeltaStatus.Ignored;

                if (containsOitem)
                {
                    // C: advance_into — the caller continues with the first
                    // child (treated as a normal TREE item); an empty
                    // directory advances instead.
                    GitIndexEntry? child = await _newIter.AdvanceIntoAsync(cancellationToken).ConfigureAwait(false);
                    return child ?? await _newIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Actually create the record for this item if necessary
        // (diff_generate.c:1180-1182).
        TryCreateFromOne(status, null, nitem);

        // If the user requested TYPECHANGE records, then check for that
        // instead of just generating an ADDED/UNTRACKED record
        // (diff_generate.c:1184-1197).
        if (status != GitDeltaStatus.Ignored &&
            (_opts.Flags & GitDiffOptionsFlags.IncludeTypechangeTrees) != 0 &&
            containsOitem)
        {
            GitDiffDelta? last = LastDeltaForItem(nitem);
            if (last is not null)
            {
                last.Status = GitDeltaStatus.Typechange;
                last.OldFile.Mode = GitFileMode.Tree;
            }
        }

        return await _newIter.AdvanceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true if <paramref name="item"/>'s path is prefixed by
    /// <paramref name="prefixItem"/>'s path (a directory), i.e. the item is
    /// contained in the directory. Matches <c>entry_is_prefixed</c>
    /// (diff_generate.c:951-966): the prefix compare goes through the
    /// <c>pfxcomp</c> slot (case-sensitive or ASCII-fold); the trailing-byte
    /// test is byte-wise (<c>'/'</c>, NUL, or <c>'/'</c>).
    /// </summary>
    private bool EntryIsPrefixed(GitIndexEntry? item, GitIndexEntry prefixItem)
    {
        if (!item.HasValue)
        {
            return false;
        }

        GitPath itemPath = item.Value.Path;
        GitPath prefixPath = prefixItem.Path;

        if (_pfxcomp(itemPath, prefixPath) != 0)
        {
            return false;
        }

        int pathLen = prefixPath.Length;
        if (pathLen == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> pb = prefixPath.Span;
        ReadOnlySpan<byte> ib = itemPath.Span;
        return pb[pathLen - 1] == (byte)'/'
            || ib.Length == pathLen
            || ib[pathLen] == (byte)'/';
    }

    /// <summary>
    /// Returns the last delta created for <paramref name="item"/>, or null.
    /// Ports <c>diff_delta__last_for_item</c> (<c>diff_generate.c:288-323</c>)
    /// 1:1: the match is OID-based and status-gated; only UNTRACKED/UNREADABLE
    /// also require a <c>strcomp</c> path match (untracked files may share an
    /// all-zero OID). Comparing paths for every status diverged from libgit2.
    /// </summary>
    private GitDiffDelta? LastDeltaForItem(GitIndexEntry item)
    {
        if (_deltas.Count == 0)
        {
            return null;
        }

        GitDiffDelta delta = _deltas[^1];

        switch (delta.Status)
        {
            case GitDeltaStatus.Unmodified:
            case GitDeltaStatus.Deleted:
                if (delta.OldFile.Id == item.Id)
                {
                    return delta;
                }

                break;

            case GitDeltaStatus.Added:
                if (delta.NewFile.Id == item.Id)
                {
                    return delta;
                }

                break;

            case GitDeltaStatus.Unreadable:
            case GitDeltaStatus.Untracked:
                if (_strcomp(delta.NewFile.Path ?? default, item.Path) == 0 &&
                    delta.NewFile.Id == item.Id)
                {
                    return delta;
                }

                break;

            case GitDeltaStatus.Modified:
                if (delta.OldFile.Id == item.Id ||
                    (delta.NewFile.Mode == item.Mode && delta.NewFile.Id == item.Id))
                {
                    return delta;
                }

                break;

            default:
                break;
        }

        return null;
    }

    /// <summary>
    /// Returns true if the directory at <paramref name="dirPath"/> contains a
    /// <c>.git</c> file or directory. Matches <c>git_fs_path_contains</c>
    /// (futils.c) as used by diff_generate.c:1076-1082.
    /// </summary>
    private static bool PathContainsDotGit(string dirPath)
    {
        string gitPath = Path.Join(dirPath, ".git");
        return File.Exists(gitPath) || Directory.Exists(gitPath);
    }

    // ━━ Helpers ━━

    /// <summary>
    /// Extracts the basic file type from a mode. Matches <c>GIT_MODE_TYPE</c>.
    /// </summary>
    private static uint ModeType(uint mode) => mode & 0xF000;

    /// <summary>
    /// Compares two <see cref="IndexTime"/> values. Matches <c>git_index_time_eq</c>.
    /// </summary>
    private static bool IndexTimeEquals(IndexTime a, IndexTime b)
        => a.Seconds == b.Seconds && a.Nanoseconds == b.Nanoseconds;

    /// <summary>
    /// Racy-git check: returns true if <paramref name="entry"/>'s mtime is
    /// newer than OR equal to the index file's own mtime (<c>Stamp</c>),
    /// matching <c>git_index_entry_newer_than_index</c> (index.h:101-118).
    /// Delegates to <see cref="GitIndex.EntryNewerThanIndex"/>; see that method
    /// for the full rationale. The canonical home is on <c>GitIndex</c> itself
    /// (mirroring libgit2's inline in <c>index.h</c>), shared by diff and
    /// checkout (<c>checkout.c:219</c>).
    /// </summary>
    private static bool EntryNewerThanIndex(GitIndexEntry entry, GitIndex? index)
        => index?.EntryNewerThanIndex(entry) ?? false;

    // ━━ Exports ━━

    /// <summary>
    /// Pairs head→index + index→workdir deltas. Ports
    /// <c>git_diff__paired_foreach</c> (<c>diff_generate.c:1621-1707</c>) 1:1,
    /// including the icase-mismatch re-sort logic. Used by <c>status.c</c>.
    /// </summary>
    /// <remarks>
    /// The walk matches <c>h2i-&gt;new_file.path</c> against
    /// <c>i2w-&gt;old_file.path</c> (the index name on both sides). To traverse
    /// renames in index→workdir correctly, the i2w list is (temporarily) re-sorted
    /// by its <c>i2w_path</c> (old path). When the two diffs disagree on case
    /// sensitivity, the h2i list is temporarily forced to case-sensitive order so
    /// the walk stays consistent. Both lists are restored after the walk.
    /// </remarks>
    public static void PairedForeach(
        DiffGenerator? head2Idx,
        DiffGenerator? idx2Wd,
        Func<GitDiffDelta?, GitDiffDelta?, bool> callback)
    {
        int iMax = head2Idx?._deltas.Count ?? 0;
        int jMax = idx2Wd?._deltas.Count ?? 0;
        if (iMax == 0 && jMax == 0)
        {
            return;
        }

        bool h2iIcase = head2Idx?.IsSortedIcase ?? false;
        bool i2wIcase = idx2Wd?.IsSortedIcase ?? false;
        bool icaseMismatch = head2Idx is not null && idx2Wd is not null && h2iIcase != i2wIcase;

        // Force h2i to case-sensitive order if it is icase but i2w is not.
        if (icaseMismatch && h2iIcase)
        {
            head2Idx!._deltas.Sort(GitDiffDelta.DeltaCompare);
        }

        // strcomp defaults to git__strcmp; only git__strcasecmp when i2w is icase
        // AND the two diffs agree on case sensitivity. The i2w list is (re)sorted
        // by its i2w_path (old path, fallback new) so the walk matches on the
        // index name on both sides.
        Func<GitPath, GitPath, int> strcomp = GitPath.Compare;
        if (idx2Wd is not null)
        {
            if (i2wIcase && !icaseMismatch)
            {
                strcomp = GitPath.CompareIgnoreCase;
                idx2Wd._deltas.Sort(DeltaI2WCompareIgnoreCase);
            }
            else
            {
                idx2Wd._deltas.Sort(DeltaI2WCompare);
            }
        }

        int i = 0;
        int j = 0;
        while (i < iMax || j < jMax)
        {
            GitDiffDelta? h2i = i < iMax ? head2Idx!._deltas[i] : null;
            GitDiffDelta? i2w = j < jMax ? idx2Wd!._deltas[j] : null;

            int cmp;
            if (i2w is null)
            {
                cmp = -1;
            }
            else if (h2i is null)
            {
                cmp = 1;
            }
            else
            {
                // Compare h2i's new path (index name) with i2w's old path (index name).
                cmp = strcomp(h2i.NewFile.Path ?? default, i2w.OldFile.Path ?? default);
            }

            if (cmp < 0)
            {
                i++;
                i2w = null;
            }
            else if (cmp > 0)
            {
                j++;
                h2i = null;
            }
            else
            {
                i++;
                j++;
            }

            if (!callback(h2i, i2w))
            {
                break;
            }
        }

        // Restore case-insensitive delta sort on h2i.
        if (icaseMismatch && h2iIcase)
        {
            head2Idx!._deltas.Sort(GitDiffDelta.DeltaCompareIgnoreCase);
        }

        // Restore idx2wd sort by canonical (new) path.
        idx2Wd?._deltas.Sort(i2wIcase ? GitDiffDelta.DeltaCompareIgnoreCase : GitDiffDelta.DeltaCompare);
    }

    /// <summary>
    /// Index-to-workdir delta sort: compares by <see cref="GitDiffDelta.I2WPath"/>
    /// (old path, fallback new) with a status tiebreak. Ports
    /// <c>diff_delta_i2w_cmp</c> (<c>diff_generate.c:342-347</c>).
    /// </summary>
    private static int DeltaI2WCompare(GitDiffDelta a, GitDiffDelta b)
    {
        int val = GitPath.Compare(a.I2WPath ?? default, b.I2WPath ?? default);
        return val != 0 ? val : (int)a.Status - (int)b.Status;
    }

    /// <summary>
    /// Index-to-workdir delta sort, ASCII-fold variant. Ports
    /// <c>diff_delta_i2w_casecmp</c> (<c>diff_generate.c:349-354</c>).
    /// </summary>
    private static int DeltaI2WCompareIgnoreCase(GitDiffDelta a, GitDiffDelta b)
    {
        int val = GitPath.CompareIgnoreCase(a.I2WPath ?? default, b.I2WPath ?? default);
        return val != 0 ? val : (int)a.Status - (int)b.Status;
    }

    /// <summary>
    /// Diffs a commit against its first parent. Matches
    /// <c>git_diff__commit</c> (diff_generate.c:1709-1750). Root commits diff
    /// against an empty tree; merge commits use first parent (matching git's
    /// <c>git show</c> behavior).
    /// </summary>
    public static async Task<DiffGenerator> CommitAsync(GitRepository repo, Commit commit, GitDiffOptions? opts, CancellationToken cancellationToken = default)
    {
        int parentCount = commit.Parents.Count;

        // Merge commits: use first parent (matches `git show` / `git_diff_commit`).
        GitTree? oldTree = null;
        if (parentCount > 0)
        {
            Commit? parent = await repo.Objects.LookupAsync<Commit>(commit.ParentId(0), cancellationToken).ConfigureAwait(false);
            oldTree = parent?.Tree is { } treeId
                ? await repo.Objects.LookupAsync<GitTree>(treeId, cancellationToken).ConfigureAwait(false)
                : null;
        }

        GitTree? newTree = await repo.Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
        return await TreeToTreeAsync(repo, oldTree, newTree, opts, cancellationToken).ConfigureAwait(false);
    }

    // ━━ Iterator option preparation ━━

    /// <summary>
    /// Builds iterator options with the pathspec threaded in, mirroring
    /// <c>diff_prepare_iterator_opts</c> (diff_generate.c:1336-1360). When
    /// <see cref="GitDiffOptionsFlags.DisablePathspecMatch"/> is set, the
    /// pathspec strings are copied into <see cref="IteratorOptions.PathList"/>
    /// so the iterator only walks those literal paths. Otherwise the common
    /// non-wildcard prefix (<see cref="GitPathSpec.Prefix"/>, matching
    /// <c>git_pathspec_prefix</c>) is used as the <see cref="IteratorOptions.Start"/>
    /// and <see cref="IteratorOptions.End"/> range, bounding the walk to the
    /// potentially-matching subtree. When no pathspec is present, the base
    /// options are returned unchanged.
    /// </summary>
    /// <param name="opts">The diff options (may carry a pathspec).</param>
    /// <param name="baseFlags">The iterator flags the caller would have set
    /// (e.g. <see cref="IteratorFlags.IncludeConflicts"/>).</param>
    internal static IteratorOptions PrepareIteratorOptions(GitDiffOptions opts, IteratorFlags baseFlags)
    {
        // C (diff_generate.c:1382-1387): the user's GIT_DIFF_IGNORE_CASE is
        // carried on the iterators (GIT_ITERATOR_IGNORE_CASE).
        if ((opts.Flags & GitDiffOptionsFlags.IgnoreCase) != 0)
        {
            baseFlags |= IteratorFlags.IgnoreCase;
        }

        if (opts.PathSpecs is not { Length: > 0 } specs)
        {
            return new IteratorOptions { Flags = baseFlags };
        }

        if ((opts.Flags & GitDiffOptionsFlags.DisablePathspecMatch) != 0)
        {
            // C:1344-1348 — iterator applies pathspec as a literal path filter.
            return new IteratorOptions { Flags = baseFlags, PathList = specs };
        }

        // C:1350-1357 — compute the common non-wildcard prefix and use it as
        // the start/end range. GitPathSpec.Prefix matches git_pathspec_prefix.
        var ps = GitPathSpec.New(specs);
        GitPath? prefix = ps.Prefix;

        return new IteratorOptions { Flags = baseFlags, Start = prefix, End = prefix };
    }

    /// <summary>Tree-to-tree diff via iterators.</summary>
    public static async Task<DiffGenerator> TreeToTreeAsync(
        GitRepository repo, GitTree? oldTree, GitTree? newTree, GitDiffOptions? opts, CancellationToken cancellationToken = default)
    {
        GitDiffOptions options = opts ?? new GitDiffOptions();
        IteratorOptions iterOpts = PrepareIteratorOptions(options, IteratorFlags.None);

        IIterator oldIter = oldTree is not null
            ? TreeIterator.ForTree(oldTree, repo, iterOpts)
            : new EmptyIterator();

        IIterator newIter = newTree is not null
            ? TreeIterator.ForTree(newTree, repo, iterOpts)
            : new EmptyIterator();

        // Pass the original (nullable) opts to GenerateAsync so
        // ApplyOptionsAsync can distinguish "no options passed" (opts == NULL)
        // from an explicit options object — C's diff_generated_apply_options
        // consults diff.context only when opts == NULL (diff_generate.c:538-543).
        return await GenerateAsync(repo, oldIter, newIter, opts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tree-to-index diff via iterators.</summary>
    public static async Task<DiffGenerator> TreeToIndexAsync(
        GitRepository repo, GitTree? oldTree, GitDiffOptions? opts, CancellationToken cancellationToken = default)
    {
        GitDiffOptions options = opts ?? new GitDiffOptions();
        // Unmerged index entries must surface as GIT_DELTA_CONFLICTED deltas (status relies on
        // them); matches libgit2's diff_tree_to_index, which passes GIT_ITERATOR_INCLUDE_CONFLICTS.
        IteratorOptions iterOpts = PrepareIteratorOptions(options, IteratorFlags.IncludeConflicts);

        // C (diff_generate.c:1460-1461): tree-to-index forces
        // GIT_ITERATOR_DONT_IGNORE_CASE on BOTH iterators regardless of the
        // index's case mode — the walk stays case-sensitively sorted, and the
        // delta list is re-sorted icase below when the index is icase. The
        // port's IndexIterator resolves the case mode from the index when the
        // flag is unset, which would desync the lock-step walk on a
        // case-insensitive index (NTFS inits core.ignorecase=true).
        if ((iterOpts.Flags & (IteratorFlags.IgnoreCase | IteratorFlags.DontIgnoreCase)) == 0)
        {
            iterOpts = iterOpts with { Flags = iterOpts.Flags | IteratorFlags.DontIgnoreCase };
        }

        IIterator oldIter = oldTree is not null
            ? TreeIterator.ForTree(oldTree, repo, iterOpts)
            : new EmptyIterator();

        GitIndex index = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        IIterator newIter = IndexIterator.ForIndex(index, repo, iterOpts);

        DiffGenerator diff = await GenerateAsync(repo, oldIter, newIter, opts, cancellationToken).ConfigureAwait(false);

        // C (diff_generate.c:1470-1472): if the index is in case-insensitive
        // order, re-sort the deltas to match.
        if (index.IgnoreCase)
        {
            diff.SetIgnoreCase(true);
        }

        return diff;
    }

    /// <summary>Index-to-workdir diff via iterators.</summary>
    public static async Task<DiffGenerator> IndexToWorkdirAsync(GitRepository repo, GitDiffOptions? opts, CancellationToken cancellationToken = default)
    {
        GitDiffOptions options = opts ?? new GitDiffOptions();
        // Unmerged index entries must surface as GIT_DELTA_CONFLICTED deltas (status relies on
        // them); matches libgit2's diff_index_to_workdir, which passes GIT_ITERATOR_INCLUDE_CONFLICTS
        // for the index side and GIT_ITERATOR_DONT_AUTOEXPAND for the workdir side
        // (diff_generate.c:1486-1489). DONT_AUTOEXPAND surfaces directory entries to
        // handle_unmatched_new_item, which gates recursion on the directory's ignore status.
        // The pathspec prefix range is applied to the index iterator (sorted, list-based —
        // range-bounding is effective) but not the workdir iterator (directory-walk-based —
        // ExaminePath already filters per-entry, and the range interacts poorly with the
        // workdir's unsorted directory descent + DontAutoexpand tree-entry handling).
        IteratorOptions indexIterOpts = PrepareIteratorOptions(options, IteratorFlags.IncludeConflicts);
        var workdirIterOpts = new IteratorOptions { Flags = IteratorFlags.DontAutoexpand };

        GitIndex index = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        IIterator oldIter = IndexIterator.ForIndex(index, repo, indexIterOpts);

        IIterator newIter = await FilesystemIterator.ForWorkdirAsync(repo, index, null, workdirIterOpts, cancellationToken).ConfigureAwait(false);

        DiffGenerator diff = await GenerateAsync(repo, oldIter, newIter, opts, cancellationToken).ConfigureAwait(false);

        // If UPDATE_INDEX is set and the diff updated the index, write it
        // (diff_generate.c:1493-1495).
        if ((diff.Options.Flags & GitDiffOptionsFlags.UpdateIndex) != 0 && diff._indexUpdated)
        {
            await index.WriteAsync(cancellationToken).ConfigureAwait(false);
        }

        return diff;
    }

    /// <summary>
    /// Tree-to-workdir diff via direct iterators. Matches
    /// <c>git_diff_tree_to_workdir</c> (diff_generate.c:1508-1544): a tree
    /// iterator vs a real workdir iterator (not the merge approach used by
    /// <c>git_diff_tree_to_workdir_with_index</c> at diff_generate.c:1546).
    /// The workdir iterator takes <paramref name="oldTree"/> as its baseline
    /// (third arg to <c>git_iterator_for_workdir</c>, diff_generate.c:1531),
    /// used for submodule detection. Iterator flags follow C:
    /// <c>aflags=0</c> (no <c>DONT_IGNORE_CASE</c>, no <c>INCLUDE_CONFLICTS</c>
    /// — those are tree-to-index only, diff_generate.c:1424-1425) and
    /// <c>bflags=GIT_ITERATOR_DONT_AUTOEXPAND</c> (diff_generate.c:1527-1528).
    /// </summary>
    public static async Task<DiffGenerator> TreeToWorkdirAsync(
        GitRepository repo, GitTree? oldTree, GitDiffOptions? opts, CancellationToken cancellationToken = default)
    {
        GitDiffOptions options = opts ?? new GitDiffOptions();

        // aflags=0: matches C's git_diff_tree_to_workdir (diff_generate.c:1527).
        // IncludeConflicts is NOT set here — it is tree-to-index only.
        IteratorOptions treeIterOpts = PrepareIteratorOptions(options, IteratorFlags.None);

        // bflags=DONT_AUTOEXPAND: surfaces directory entries to
        // HandleUnmatchedNewAsync, which gates recursion on the directory's
        // ignore status (diff_generate.c:1528, 1168-1170). No pathspec range
        // on the workdir iterator (see IndexToWorkdirAsync comment).
        var workdirIterOpts = new IteratorOptions { Flags = IteratorFlags.DontAutoexpand };

        GitIndex index = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);

        // C (iterator_init_common, iterator.c:111-167): with aflags=0 the
        // tree iterator resolves its case mode from the repository's index,
        // exactly like the workdir iterator — both sides of the lock-step
        // walk must sort identically. The port's TreeIterator defaults to
        // DontIgnoreCase when the flag is unset, so on a case-insensitive
        // index (NTFS inits core.ignorecase=true) the tree side would sort
        // case-sensitively against the workdir's icase order and desync the
        // walk (every file reported Deleted). Propagate the index's mode.
        if ((treeIterOpts.Flags & (IteratorFlags.IgnoreCase | IteratorFlags.DontIgnoreCase)) == 0)
        {
            treeIterOpts = treeIterOpts with
            {
                Flags = treeIterOpts.Flags | (index.IgnoreCase ? IteratorFlags.IgnoreCase : IteratorFlags.DontIgnoreCase),
            };
        }

        IIterator oldIter = oldTree is not null
            ? TreeIterator.ForTree(oldTree, repo, treeIterOpts)
            : new EmptyIterator(treeIterOpts);

        // Pass oldTree as the baseline (third arg): used by
        // FilesystemIterator.IsSubmoduleAsync for HEAD-tree gitlink detection
        // (filesystem_iterator_is_submodule, iterator.c).
        IIterator newIter = await FilesystemIterator.ForWorkdirAsync(repo, index, oldTree, workdirIterOpts, cancellationToken).ConfigureAwait(false);

        return await GenerateAsync(repo, oldIter, newIter, opts, cancellationToken).ConfigureAwait(false);
    }
}
