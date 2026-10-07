// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.Status;

/// <summary>
/// Working-tree status. Managed port of <c>src/libgit2/status.c</c> +
/// <c>include/git2/status.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// Combines a HEAD→index diff (staged changes), an index→workdir diff
/// (unstaged changes + untracked + ignored), and merges them per-path via
/// <c>GitDiff.PairedForeach</c> into a flat list of
/// <see cref="GitStatusEntry"/> records.
/// </para>
/// <para>
/// The <c>UPDATE_INDEX</c> flag triggers stat-cache refresh + index write
/// via the diff engine (matches <c>GIT_STATUS_OPT_UPDATE_INDEX</c>).
/// </para>
/// <para>
/// The <c>Diff</c> type lives in <c>LibGit2CS.Diff</c> (same name as the
/// namespace — CA1724). All references to diff types here are
/// namespace-qualified to disambiguate.
/// </para>
/// </remarks>
public sealed class GitStatusList : IDisposable
{
    private readonly GitDiff? _head2Idx;
    private readonly GitDiff? _idx2Wd;
    private readonly List<GitStatusEntry> _paired = [];
    private bool _disposed;

    private GitStatusList(GitDiff? head2Idx, GitDiff? idx2Wd)
    {
        _head2Idx = head2Idx;
        _idx2Wd = idx2Wd;
    }

    /// <summary>
    /// Creates a status list for the repository. Matches
    /// <c>git_status_list_new</c> (status.c:261-393). Internal — the public
    /// entry point is <see cref="GitRepository.StatusNewAsync"/>.
    /// </summary>
    internal static async Task<GitStatusList> NewAsync(GitRepository repo, GitStatusOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        GitStatusOptions opts = options ?? GitStatusOptions.Default;
        GitStatusShow show = opts.Show;
        GitStatusFlags flags = opts.Flags;

        // C (status.c:246-256, status_validate_options): the option validations run BEFORE the bare-repo check — a bare repo with an invalid option reports the
        // OPTION error, not "cannot get status of a bare repository".
        if ((int)show > (int)GitStatusShow.WorkdirOnly)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "unknown status 'show' option",
                GitErrorCategory.Invalid);
        }

        if ((flags & GitStatusFlags.NoRefresh) != 0 &&
            (flags & GitStatusFlags.UpdateIndex) != 0)
        {
            throw new GitException(GitErrorCode.Invalid,
                "updating index from status is not allowed when index refresh is disabled",
                GitErrorCategory.Repository);
        }

        if (repo.IsBare)
        {
            throw new GitException(GitErrorCode.BareRepo,
                "cannot get status of a bare repository", GitErrorCategory.Repository);
        }

        // C (status.c:296-299): unless GIT_STATUS_OPT_NO_REFRESH, the index
        // is re-read from disk so external edits are visible.
        if ((flags & GitStatusFlags.NoRefresh) == 0)
        {
            await repo.RefreshIndexAsync(cancellationToken).ConfigureAwait(false);
        }

        // Resolve HEAD tree (unborn → null → empty iterator).
        GitTree? head = null;
        if (opts.Baseline is { } baseline)
        {
            head = baseline;
        }
        else
        {
            GitReference? headRef = await repo.Refs.ResolveAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false);
            if (headRef is GitDirectReference direct)
            {
                // C (repository.c:3366-3381, git_repository_head_tree): the
                // HEAD object is peeled to a TREE — a detached HEAD at an
                // annotated tag works (a plain Commit lookup would throw on
                // tag objects).
                GitObject? obj = await repo.Objects.LookupAsync(direct.Target, cancellationToken).ConfigureAwait(false);
                if (obj is not null)
                {
                    head = await obj.PeelAsync<GitTree>(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Build diff options from status flags.
        GitDiffOptionsFlags diffFlags = GitDiffOptionsFlags.IncludeTypechange;
        if ((flags & GitStatusFlags.IncludeUntracked) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.IncludeUntracked;
        }

        if ((flags & GitStatusFlags.IncludeIgnored) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.IncludeIgnored;
        }

        if ((flags & GitStatusFlags.IncludeUnmodified) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.IncludeUnmodified;
        }

        if ((flags & GitStatusFlags.RecurseUntrackedDirs) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.RecurseUntrackedDirs;
        }

        if ((flags & GitStatusFlags.DisablePathspecMatch) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.DisablePathspecMatch;
        }

        if ((flags & GitStatusFlags.RecurseIgnoredDirs) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.RecurseIgnoredDirs;
        }

        if ((flags & GitStatusFlags.ExcludeSubmodules) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.IgnoreSubmodules;
        }

        if ((flags & GitStatusFlags.UpdateIndex) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.UpdateIndex;
        }

        if ((flags & GitStatusFlags.IncludeUnreadable) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.IncludeUnreadable;
        }

        if ((flags & GitStatusFlags.IncludeUnreadableAsUntracked) != 0)
        {
            diffFlags |= GitDiffOptionsFlags.IncludeUnreadableAsUntracked;
        }

        var diffOpts = new GitDiffOptions
        {
            Flags = diffFlags,
            PathSpecs = opts.PathSpecs,
        };

        // Find options for rename detection.
        GitDiffFindFlags findFlags = GitDiffFindFlags.ForUntracked;
        if ((flags & GitStatusFlags.RenamesFromRewrites) != 0)
        {
            // GIT_DIFF_FIND_AND_BREAK_REWRITES | GIT_DIFF_FIND_RENAMES_FROM_REWRITES |
            // GIT_DIFF_BREAK_REWRITES_FOR_RENAMES_ONLY
            findFlags |= GitDiffFindFlags.RenamesFromRewrites
                       | GitDiffFindFlags.BreakRewrites
                       | GitDiffFindFlags.BreakRewritesForRenamesOnly;
        }

        var findOpts = new GitDiffFindOptions
        {
            Flags = findFlags,
            RenameThreshold = opts.RenameThreshold,
        };

        GitDiff? head2Idx = null;
        GitDiff? idx2Wd = null;

        // HEAD → index diff.
        if (show != GitStatusShow.WorkdirOnly)
        {
            head2Idx = await GitDiff.TreeToIndexAsync(repo, head, diffOpts, cancellationToken).ConfigureAwait(false);
            if ((flags & GitStatusFlags.RenamesHeadToIndex) != 0)
            {
                await head2Idx.FindSimilarAsync(findOpts, cancellationToken).ConfigureAwait(false);
            }
        }

        // Index → workdir diff.
        if (show != GitStatusShow.IndexOnly)
        {
            idx2Wd = await GitDiff.IndexToWorkdirAsync(repo, diffOpts, cancellationToken).ConfigureAwait(false);
            if ((flags & GitStatusFlags.RenamesIndexToWorkdir) != 0)
            {
                await idx2Wd.FindSimilarAsync(findOpts, cancellationToken).ConfigureAwait(false);
            }
        }

        // Pair the two diffs into status entries.
        var status = new GitStatusList(head2Idx, idx2Wd);
        await status.ComputeAsync(repo, flags, head2Idx, idx2Wd, cancellationToken).ConfigureAwait(false);
        return status;
    }

    /// <summary>
    /// Runs the paired-foreach merge. Matches the
    /// <c>git_diff__paired_foreach</c> + <c>status_collect</c> interaction
    /// (status.c:166-185, 363-365).
    /// </summary>
    private async ValueTask ComputeAsync(
        GitRepository repo,
        GitStatusFlags flags,
        GitDiff? head2Idx,
        GitDiff? idx2Wd,
        CancellationToken cancellationToken)
    {
        bool excludeSubmodules = (flags & GitStatusFlags.ExcludeSubmodules) != 0;
        DiffGenerator? idx2WdGen = idx2Wd?.Generator;

        // First pass: collect the paired deltas (sync iteration).
        var pairs = new List<(GitDiffDelta? h2i, GitDiffDelta? i2w)>();
        GitDiff.PairedForeach(head2Idx, idx2Wd, (h2i, i2w) =>
        {
            if (excludeSubmodules && !StatusIsIncluded(h2i, i2w))
            {
                return true;
            }

            pairs.Add((h2i, i2w));
            return true;
        });

        // Second pass: compute status (may await lazy OID recomputation).
        foreach ((GitDiffDelta? h2i, GitDiffDelta? i2w) in pairs)
        {
            GitStatusFlags st = GitStatusFlags.Current;
            if (h2i is not null)
            {
                st |= IndexDelta2Status(h2i);
            }

            if (i2w is not null)
            {
                st |= await WorkdirDelta2StatusAsync(repo, idx2WdGen, i2w, cancellationToken).ConfigureAwait(false);
            }

            _paired.Add(new GitStatusEntry(st, h2i, i2w));
        }

        // Sort if renames or sort-override flags are set.
        bool needSort = (flags & (GitStatusFlags.RenamesHeadToIndex
                               | GitStatusFlags.RenamesIndexToWorkdir
                               | GitStatusFlags.SortCaseSensitively
                               | GitStatusFlags.SortCaseInsenzively)) != 0;

        if (needSort)
        {
            // C (status.c:221-237, 368-378): the paired vector's comparator is
            // the INDEX's ignore_case by default; only an explicit SORT flag
            // overrides it.
            bool ignoreCase;
            if ((flags & GitStatusFlags.SortCaseInsenzively) != 0)
            {
                ignoreCase = true;
            }
            else if ((flags & GitStatusFlags.SortCaseSensitively) != 0)
            {
                ignoreCase = false;
            }
            else
            {
                GitIndex? idx = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
                ignoreCase = idx.IgnoreCase;
            }
            TimSort.Sort(_paired, (a, b) =>
            {
                GitDiffDelta? da = a.IndexToWorkdir ?? a.HeadToIndex;
                GitDiffDelta? db = b.IndexToWorkdir ?? b.HeadToIndex;
                if (da is null && db is null)
                {
                    return 0;
                }

                if (da is null)
                {
                    return -1;
                }

                if (db is null)
                {
                    return 1;
                }

                // Byte-wise compare (git__strcmp / git__strcasecmp) — ports status_entry_cmp_base (status.c:187-219) faithfully: the C
                // dispatcher picks git__strcmp or git__strcasecmp from index->ignore_case, NOT from the diff's strcomp slot. ASCII-fold (not OrdinalIgnoreCase)
                // matches libgit2 byte-for-byte; any golden reordering is a parity fix.
                GitPath pa = da.NewFile.Path ?? default;
                GitPath pb = db.NewFile.Path ?? default;
                return ignoreCase
                    ? GitPath.CompareIgnoreCase(pa, pb)
                    : GitPath.Compare(pa, pb);
            });
        }
    }

    /// <summary>
    /// Maps a HEAD→index delta to the INDEX status bits. Matches
    /// <c>index_delta2status</c> (status.c:25-57).
    /// </summary>
    private static GitStatusFlags IndexDelta2Status(GitDiffDelta head2Idx)
    {
        return head2Idx.Status switch
        {
            GitDeltaStatus.Added or GitDeltaStatus.Copied => GitStatusFlags.IndexNew,
            GitDeltaStatus.Deleted => GitStatusFlags.IndexDeleted,
            GitDeltaStatus.Modified => GitStatusFlags.IndexModified,
            GitDeltaStatus.Renamed => GitStatusFlags.IndexRenamed
                | (head2Idx.OldFile.Id != head2Idx.NewFile.Id ? GitStatusFlags.IndexModified : 0),
            GitDeltaStatus.Typechange => GitStatusFlags.IndexTypeChange,
            GitDeltaStatus.Conflicted => GitStatusFlags.Conflicted,
            _ => GitStatusFlags.Current,
        };
    }

    /// <summary>
    /// Maps an index→workdir delta to the WT status bits. Matches
    /// <c>workdir_delta2status</c> (status.c:59-118), including the lazy
    /// OID recomputation for RENAMED+MODIFIED detection.
    /// </summary>
    private static async ValueTask<GitStatusFlags> WorkdirDelta2StatusAsync(
        GitRepository repo,
        DiffGenerator? idx2WdGen,
        GitDiffDelta idx2Wd,
        CancellationToken cancellationToken)
    {
        GitStatusFlags st = idx2Wd.Status switch
        {
            GitDeltaStatus.Added or GitDeltaStatus.Copied or GitDeltaStatus.Untracked => GitStatusFlags.WorkdirNew,
            GitDeltaStatus.Unreadable => GitStatusFlags.WorkdirUnreadable,
            GitDeltaStatus.Deleted => GitStatusFlags.WorkdirDeleted,
            GitDeltaStatus.Modified => GitStatusFlags.WorkdirModified,
            GitDeltaStatus.Ignored => GitStatusFlags.Ignored,
            GitDeltaStatus.Typechange => GitStatusFlags.WorkdirTypeChange,
            GitDeltaStatus.Conflicted => GitStatusFlags.Conflicted,
            _ => GitStatusFlags.Current,
        };

        // RENAMED — check for content modification (status.c:82-105).
        if (idx2Wd.Status == GitDeltaStatus.Renamed)
        {
            st = GitStatusFlags.WorkdirRenamed;

            if (idx2Wd.OldFile.Id != idx2Wd.NewFile.Id)
            {
                // OIDs may be deferred for workdir-sourced entries — recompute lazily via DiffFileContent.ComputeWorkdirOidAsync.
                if (idx2Wd.OldFile.Id.IsZero &&
                    idx2WdGen is { OldSrc: IO.IteratorType.Workdir })
                {
                    idx2Wd.OldFile.Id = await DiffFileContent.ComputeWorkdirOidAsync(
                        repo, idx2Wd.OldFile.Path ?? default, idx2Wd.OldFile.Mode, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                if (idx2Wd.NewFile.Id.IsZero &&
                    idx2WdGen is { NewSrc: IO.IteratorType.Workdir })
                {
                    idx2Wd.NewFile.Id = await DiffFileContent.ComputeWorkdirOidAsync(
                        repo, idx2Wd.NewFile.Path ?? default, idx2Wd.NewFile.Mode, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                if (idx2Wd.OldFile.Id != idx2Wd.NewFile.Id)
                {
                    st |= GitStatusFlags.WorkdirModified;
                }
            }
        }

        return st;
    }

    /// <summary>
    /// Checks whether an entry should be included when EXCLUDE_SUBMODULES is
    /// set. Matches <c>status_is_included</c> (status.c:120-148). Returns
    /// false (exclude) only if every valid mode is GIT_FILEMODE_COMMIT.
    /// </summary>
    private static bool StatusIsIncluded(
        GitDiffDelta? head2Idx,
        GitDiffDelta? idx2Wd)
    {
        if (head2Idx is not null)
        {
            if (head2Idx.Status != GitDeltaStatus.Added &&
                head2Idx.OldFile.Mode != GitFileMode.GitLink)
            {
                return true;
            }

            if (head2Idx.Status != GitDeltaStatus.Deleted &&
                head2Idx.NewFile.Mode != GitFileMode.GitLink)
            {
                return true;
            }
        }

        if (idx2Wd is not null)
        {
            if (idx2Wd.Status != GitDeltaStatus.Added &&
                idx2Wd.OldFile.Mode != GitFileMode.GitLink)
            {
                return true;
            }

            if (idx2Wd.Status != GitDeltaStatus.Deleted &&
                idx2Wd.NewFile.Mode != GitFileMode.GitLink)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The number of status entries.</summary>
    public int EntryCount => _paired.Count;

    /// <summary>Gets the status entry at the given index.</summary>
    public GitStatusEntry GetEntry(int index) => _paired[index];

    /// <summary>Enumerates all status entries.</summary>
    public IEnumerable<GitStatusEntry> Entries => _paired;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _head2Idx?.Dispose();
        _idx2Wd?.Dispose();
    }
}
