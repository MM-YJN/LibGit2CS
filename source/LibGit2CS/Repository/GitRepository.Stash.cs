// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Stash;
using LibGit2CS.Status;
using LibGit2CS.Utils;

using GitDiff = LibGit2CS.Diff.GitDiff;
using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Repository;

/// <content> Stash operations. Managed port of libgit2's <c>src/libgit2/stash.c</c> public entry points. </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>The refs/stash reference name.</summary>
    public const string StashRefsStashFile = "refs/stash";

    // ── Save ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stashes the current index and workdir changes. Matches
    /// <c>git_stash_save</c> (stash.c:663-679).
    /// </summary>
    public async Task<GitOid> StashSaveAsync(GitSignature stasher, string? message, GitStashFlags flags, CancellationToken cancellationToken = default)
    {
        return await StashSaveWithOptsAsync(new GitStashSaveOptions
        {
            Stasher = stasher,
            Message = message,
            Flags = flags,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stashes with full options. Matches <c>git_stash_save_with_opts</c>
    /// (stash.c:681-766).
    /// </summary>
    public async Task<GitOid> StashSaveWithOptsAsync(GitStashSaveOptions opts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(opts.Stasher);

        if (IsBare)
        {
            throw new GitException(
                GitErrorCode.BareRepo,
                "cannot stash on a bare repository",
                GitErrorCategory.Stash);
        }

        bool hasPaths = opts.Paths is { Length: > 0 };
        string[]? paths = opts.Paths;

        // Retrieve base commit and message prefix. The prefix is byte-primary (C's stash.c builds it from the raw git_commit_summary bytes + HEAD hex,
        // stash.c:30-44, 328-374) — a non-UTF-8 base-commit subject keeps its raw bytes.
        (Commit? bCommit, byte[]? msgPrefixBytes) = await StashRetrieveBaseCommitAndMessageAsync(cancellationToken).ConfigureAwait(false);

        // Ensure there are changes to stash.
        if (!hasPaths)
        {
            await StashEnsureThereAreChangesToStashAsync(opts.Flags, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Debug.Assert(paths is not null, "paths is non-null when hasPaths is true");
            await StashEnsureThereAreChangesToStashPathsAsync(opts.Flags, paths, cancellationToken).ConfigureAwait(false);
        }

        // Commit the index.
        GitIndex index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        GitOid iCommit = await StashCommitIndexAsync(index, opts.Stasher, msgPrefixBytes, bCommit, cancellationToken).ConfigureAwait(false);

        // Commit untracked/ignored if requested.
        GitOid? uCommit = null;
        if ((opts.Flags & (GitStashFlags.IncludeUntracked | GitStashFlags.IncludeIgnored)) != 0)
        {
            uCommit = await StashCommitUntrackedAsync(opts.Stasher, msgPrefixBytes, iCommit, opts.Flags, cancellationToken).ConfigureAwait(false);
        }

        // Prepare the worktree commit message.
        byte[] stashMsg = StashPrepareWorktreeCommitMessage(msgPrefixBytes, opts.Message);

        GitOid wCommitOid;
        if (!hasPaths)
        {
            wCommitOid = await StashCommitWorktreeAsync(opts.Stasher, stashMsg, iCommit, bCommit, uCommit, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Debug.Assert(paths is not null, "paths is non-null when hasPaths is true");
            wCommitOid = await StashCommitWorktreeFromPathsAsync(opts.Stasher, stashMsg, iCommit, bCommit, uCommit, paths, cancellationToken).ConfigureAwait(false);
        }

        // Update refs/stash with reflog. C (stash.c:745): git_str_rtrim — ASCII whitespace only (a trailing non-ASCII space stays in the reflog message). rtrim
        // over the message BYTES, fed to the byte reflog tier.
        byte[] msgRtrim = AsciiText.Rtrim(stashMsg).ToArray();
        await StashUpdateReflogAsync(wCommitOid, msgRtrim, cancellationToken).ConfigureAwait(false);

        // Reset index and workdir unless KeepAll.
        if ((opts.Flags & GitStashFlags.KeepAll) == 0)
        {
            GitOid resetTarget = (opts.Flags & GitStashFlags.KeepIndex) != 0
                ? iCommit
                : bCommit.Id;
            await StashResetIndexAndWorkdirAsync(resetTarget, opts.Flags, cancellationToken).ConfigureAwait(false);
        }

        return wCommitOid;
    }

    // ── ForEach ──────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates stash entries via the stash reflog. Matches
    /// <c>git_stash_foreach</c> (stash.c:1176-1217).
    /// </summary>
    public async IAsyncEnumerable<GitStashEntry> StashForEachAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        GitReference? stash = await Refs.LookupAsync(StashRefsStashFile, cancellationToken).ConfigureAwait(false);
        if (stash is null)
        {
            yield break;
        }

        GitRefLog? reflog = await Refs.ReadLogAsync(StashRefsStashFile, cancellationToken).ConfigureAwait(false);
        if (reflog is null)
        {
            yield break;
        }

        int i = 0;
        foreach (GitRefLogEntry entry in reflog)
        {
            yield return new GitStashEntry(i, entry.Message, entry.NewId);
            i++;
        }
    }

    // ── Drop ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Drops a stash entry by index. Matches <c>git_stash_drop</c>
    /// (stash.c:1219-1273).
    /// </summary>
    public async Task StashDropAsync(int index, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        GitTransaction tx = Refs.BeginTransaction();
        await using ConfiguredAsyncDisposable txDisposable = tx.ConfigureAwait(false);
        tx.LockRef(StashRefsStashFile);

        GitReference stash = await Refs.LookupAsync(StashRefsStashFile, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "no stash found", GitErrorCategory.Stash);

        GitRefLog reflog = await Refs.ReadLogAsync(StashRefsStashFile, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "no stash reflog found", GitErrorCategory.Stash);

        int max = reflog.EntryCount;
        if (max == 0 || index > max - 1)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"no stashed state at position {index}",
                GitErrorCategory.Stash);
        }

        reflog.Drop(index, rewritePreviousEntry: true);
        tx.SetReflog(StashRefsStashFile, reflog);

        if (max == 1)
        {
            tx.Remove(StashRefsStashFile);
        }
        else if (index == 0)
        {
            GitRefLogEntry entry = reflog[0];
            tx.SetTarget(StashRefsStashFile, entry.NewId);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Apply / Pop ──────────────────────────────────────────────────────

    /// <summary>
    /// Applies a stash entry by index. Matches <c>git_stash_apply</c>
    /// (stash.c:1036-1174).
    /// </summary>
    public async Task StashApplyAsync(int index, GitStashApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        GitStashApplyOptions opts = options ?? new GitStashApplyOptions();

        // Normalize apply options.
        GitCheckoutOptions checkoutOpts = opts.CheckoutOptions with
        {
            Strategy = opts.CheckoutOptions.Strategy | GitCheckoutStrategy.NoRefresh,
        };
        if (checkoutOpts.OurLabel is null)
        {
            checkoutOpts = checkoutOpts with { OurLabel = "Updated upstream" };
        }
        if (checkoutOpts.TheirLabel is null)
        {
            checkoutOpts = checkoutOpts with { TheirLabel = "Stashed changes" };
        }

        opts.Progress?.Report(GitStashApplyProgress.LoadingStash);

        // Retrieve the stash commit.
        GitOid stashCommit = await StashRetrieveStashCommitAsync(index, cancellationToken).ConfigureAwait(false);
        Commit stashCommitObj = await Objects.LookupAsync<Commit>(stashCommit, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "stash commit not found", GitErrorCategory.Stash);

        // Retrieve all trees from the stash commit.
        (GitTree? stashTree, GitTree? stashParentTree, GitTree? indexTree, GitTree? indexParentTree, GitTree? untrackedTree) =
            await StashRetrieveStashTreesAsync(stashCommitObj, cancellationToken).ConfigureAwait(false);

        Debug.Assert(stashTree is not null && stashParentTree is not null && indexTree is not null, "a stash commit always has base, index, and worktree trees");

        // Load repo index.
        GitIndex repoIndex = await GetIndexAsync(cancellationToken).ConfigureAwait(false);

        opts.Progress?.Report(GitStashApplyProgress.AnalyzeIndex);

        // Ensure the index is clean (no uncommitted changes).
        await StashEnsureCleanIndexAsync(repoIndex, cancellationToken).ConfigureAwait(false);

        // Restore index if required, or stage new files.
        GitIndex? unstashedIndex = null;
        if ((opts.Flags & GitStashApplyFlags.ReinstateIndex) != 0 &&
            !stashParentTree.Id.Equals(indexTree.Id))
        {
            unstashedIndex = await MergeIndexAndTreeAsync(indexParentTree, repoIndex, indexTree, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (unstashedIndex.HasConflicts)
            {
                throw new GitException(GitErrorCode.Conflict, "conflicts while reinstating index", GitErrorCategory.Merge);
            }
        }
        else if ((opts.Flags & GitStashApplyFlags.ReinstateIndex) == 0)
        {
            // Stage new files from the stash tree.
            GitIndex stashAdds = await StashStageNewFilesAsync(stashParentTree, stashTree, cancellationToken).ConfigureAwait(false);
            unstashedIndex = await MergeIndexesAsync(stashParentTree, repoIndex, stashAdds, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        // C (stash.c:1082-1104): with REINSTATE_INDEX and equal
        // stash-parent/index trees, NEITHER branch runs — unstashed_index
        // stays NULL and the repo index is left untouched.

        opts.Progress?.Report(GitStashApplyProgress.AnalyzeModified);

        // Restore modified files in workdir.
        GitIndex modifiedIndex = await MergeIndexAndTreeAsync(stashParentTree, repoIndex, stashTree, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Restore untracked files if applicable.
        GitIndex? untrackedIndex = null;
        if (untrackedTree is not null)
        {
            opts.Progress?.Report(GitStashApplyProgress.AnalyzeUntracked);
            untrackedIndex = await MergeIndexAndTreeAsync(null, repoIndex, untrackedTree, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Check out untracked files first.
        if (untrackedIndex is not null)
        {
            opts.Progress?.Report(GitStashApplyProgress.CheckoutUntracked);
            GitCheckoutOptions untrackedOpts = checkoutOpts with
            {
                Strategy = checkoutOpts.Strategy | GitCheckoutStrategy.DontUpdateIndex,
            };
            await CheckoutIndexAsync(untrackedIndex, untrackedOpts, cancellationToken).ConfigureAwait(false);
        }

        // Check out modified files.
        GitCheckoutOptions modifiedOpts = checkoutOpts;
        if (!modifiedIndex.HasConflicts)
        {
            modifiedOpts = modifiedOpts with
            {
                Strategy = modifiedOpts.Strategy | GitCheckoutStrategy.DontUpdateIndex,
            };
        }

        // Use repo index as baseline so existing modifications can be rewritten.
        modifiedOpts = modifiedOpts with { BaselineIndex = repoIndex };

        opts.Progress?.Report(GitStashApplyProgress.CheckoutModified);
        await CheckoutIndexAsync(modifiedIndex, modifiedOpts, cancellationToken).ConfigureAwait(false);

        // If no conflicts, update the repo index from the unstashed index.
        if (unstashedIndex is not null && !modifiedIndex.HasConflicts)
        {
            repoIndex.ReadIndex(unstashedIndex);
        }

        opts.Progress?.Report(GitStashApplyProgress.Done);

        await repoIndex.WriteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies and drops a stash entry. Matches <c>git_stash_pop</c>
    /// (stash.c:1275-1285).
    /// </summary>
    public async Task StashPopAsync(int index, GitStashApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        await StashApplyAsync(index, options, cancellationToken).ConfigureAwait(false);
        await StashDropAsync(index, cancellationToken).ConfigureAwait(false);
    }

    // ── Private: save helpers ────────────────────────────────────────────

    private async Task<(Commit bCommit, byte[] msgPrefix)> StashRetrieveBaseCommitAndMessageAsync(CancellationToken cancellationToken)
    {
        GitReference head = await Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false)
            // C (stash.c:30-44): every save failure is prefixed with
            // "cannot stash changes - " (create_error).
            ?? throw new GitException(GitErrorCode.UnbornBranch, "cannot stash changes - you do not have the initial commit yet.", GitErrorCategory.Stash);

        // byte-primary prefix (C builds it from the raw branch name + git_commit_summary bytes, stash.c:328-374). The ref name is surfaced by the string ref
        // model, so the branch slice is UTF-8- encoded once; the summary comes from the raw commit bytes.
        byte[] msgPrefix;
        if (head.Name == "HEAD")
        {
            msgPrefix = "(no branch): "u8.ToArray();
        }
        else
        {
            // Extract branch short name from refs/heads/<name>.
            string branchName = head.Name.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? head.Name["refs/heads/".Length..]
                : head.Name;
            msgPrefix = [.. Encoding.UTF8.GetBytes(branchName), .. ": "u8.ToArray()];
        }

        GitOid headTarget;
        if (head is GitDirectReference dr)
        {
            headTarget = dr.Target;
        }
        else if (head is GitSymbolicReference sr && await sr.TargetAsync(cancellationToken).ConfigureAwait(false) is GitDirectReference dt)
        {
            headTarget = dt.Target;
        }
        else
        {
            throw new GitException(GitErrorCode.UnbornBranch, "cannot resolve HEAD target", GitErrorCategory.Stash);
        }

        Commit bCommit = await Objects.LookupAsync<Commit>(headTarget, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "HEAD commit not found", GitErrorCategory.Stash);

        // Append commit description: "<7-char hex> <summary>\n" — the
        // summary from the raw message bytes (C's git_commit_summary over
        // the raw buffer; a non-UTF-8 subject stays raw, no U+FFFD).
        string hex = headTarget.ToString()[..7];
        msgPrefix = [.. msgPrefix, .. Encoding.ASCII.GetBytes(hex), .. " "u8.ToArray(), .. bCommit.SummaryBytes.ToArray(), .. "\n"u8.ToArray()];

        return (bCommit, msgPrefix);
    }

    private async Task StashEnsureThereAreChangesToStashAsync(GitStashFlags flags, CancellationToken cancellationToken)
    {
        // C (stash.c:575-600, ensure_there_are_changes_to_stash): a STATUS
        // walk with GIT_STATUS_SHOW_INDEX_AND_WORKDIR + EXCLUDE_SUBMODULES;
        // the callback returns GIT_PASSTHROUGH on the first non-CURRENT
        // entry, so staged-only changes count as stasheable.
        var statusOpts = new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.ExcludeSubmodules,
        };

        if ((flags & GitStashFlags.IncludeUntracked) != 0)
        {
            statusOpts = statusOpts with
            {
                Flags = statusOpts.Flags | GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs,
            };
        }

        if ((flags & GitStashFlags.IncludeIgnored) != 0)
        {
            statusOpts = statusOpts with
            {
                Flags = statusOpts.Flags | GitStatusFlags.IncludeIgnored | GitStatusFlags.RecurseIgnoredDirs,
            };
        }

        using GitStatusList list = await GitStatusList.NewAsync(this, statusOpts, cancellationToken).ConfigureAwait(false);
        foreach (GitStatusEntry entry in list.Entries)
        {
            if (entry.Status != GitStatusFlags.Current)
            {
                return; // changes found (GIT_PASSTHROUGH)
            }
        }

        // C: create_error(GIT_ENOTFOUND, "there is nothing to stash.")
        // (stash.c:588-589) — the "cannot stash changes - " prefix comes from
        // create_error (stash.c:33-37).
        throw new GitException(
            GitErrorCode.NotFound,
            "cannot stash changes - there is nothing to stash.",
            GitErrorCategory.Stash);
    }

    private async Task StashEnsureThereAreChangesToStashPathsAsync(GitStashFlags flags, string[] paths, CancellationToken cancellationToken)
    {
        GitPath[] pathSpecs = Array.ConvertAll(paths, GitPath.FromUtf8String);
        await StashEnsureThereAreChangesToStashPathsAsync(flags, pathSpecs, cancellationToken).ConfigureAwait(false);
    }

    private async Task StashEnsureThereAreChangesToStashPathsAsync(GitStashFlags flags, GitPath[] paths, CancellationToken cancellationToken)
    {
        GitStatusFlags statusFlags = GitStatusFlags.ExcludeSubmodules |
                          GitStatusFlags.IncludeUnmodified |
                          GitStatusFlags.DisablePathspecMatch;
        if ((flags & GitStashFlags.IncludeUntracked) != 0)
        {
            statusFlags |= GitStatusFlags.IncludeUntracked | GitStatusFlags.RecurseUntrackedDirs;
        }
        if ((flags & GitStashFlags.IncludeIgnored) != 0)
        {
            statusFlags |= GitStatusFlags.IncludeIgnored | GitStatusFlags.RecurseIgnoredDirs;
        }

        using GitStatusList statusList = await GitStatusList.NewAsync(this, new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = statusFlags,
            PathSpecs = paths,
        }, cancellationToken).ConfigureAwait(false);

        // C (stash.c:602-648, has_changes_cb): the callback returns
        // GIT_ENOTFOUND the moment ANY pathspec-matched file is CURRENT —
        // the walk stops and the stash errors even when other pathspec'd
        // files have changes (C-verified quirk).
        foreach (GitStatusEntry entry in statusList.Entries)
        {
            if (entry.Status == GitStatusFlags.Current)
            {
                throw new GitException(
                    GitErrorCode.NotFound,
                    "cannot stash changes - one of the files does not have any changes to stash.",
                    GitErrorCategory.Stash);
            }
        }
    }

    private async Task<GitOid> StashCommitIndexAsync(
        GitIndex index, GitSignature stasher,
        byte[] msgPrefix, Commit parent, CancellationToken cancellationToken)
    {
        // Build tree from index.
        GitOid iTreeOid = await index.WriteTreeToAsync(this, cancellationToken).ConfigureAwait(false);
        // C (stash.c:137): git_str_printf(&msg, "index on %s\n", message) —
        // message already ends with '\n', so the commit ends with "\n\n".
        byte[] msg = [.. "index on "u8.ToArray(), .. msgPrefix, .. "\n"u8.ToArray()];

        return await Commit.CreateAsync(this, new CommitCreateOptions
        {
            Tree = iTreeOid,
            Parents = [parent.Id],
            Author = stasher,
            Committer = stasher,
            Message = Encoding.UTF8.GetString(msg),
            MessageBytes = msg,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitOid> StashCommitUntrackedAsync(
        GitSignature stasher, byte[] msgPrefix,
        GitOid iCommitOid, GitStashFlags flags, CancellationToken cancellationToken)
    {
        Commit iCommit = await Objects.LookupAsync<Commit>(iCommitOid, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "stash index commit not found", GitErrorCategory.Object);
        GitTree iTree = await Objects.LookupAsync<GitTree>(iCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "stash index tree not found", GitErrorCategory.Object);

        // Build untracked tree.
        var uIndex = GitIndex.New(ObjectFormat);

        var diffOpts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.Normal,
        };
        if ((flags & GitStashFlags.IncludeUntracked) != 0)
        {
            diffOpts = diffOpts with
            {
                Flags = diffOpts.Flags | GitDiffOptionsFlags.IncludeUntracked | GitDiffOptionsFlags.RecurseUntrackedDirs,
            };
        }
        if ((flags & GitStashFlags.IncludeIgnored) != 0)
        {
            diffOpts = diffOpts with
            {
                Flags = diffOpts.Flags | GitDiffOptionsFlags.IncludeIgnored | GitDiffOptionsFlags.RecurseIgnoredDirs,
            };
        }

        using GitDiff diff = await GitDiff.TreeToWorkdirAsync(this, iTree, diffOpts, cancellationToken).ConfigureAwait(false);

        foreach (GitDiffDelta delta in diff.Deltas)
        {
            if (delta.Status is GitDeltaStatus.Ignored or GitDeltaStatus.Untracked)
            {
                if (delta.NewFile.Mode == GitFileMode.Tree)
                {
                    continue;
                }

                (GitOid oid, GitIndexEntry entry) = await BlobHelper.CreateFromWorkdirAsync(this, delta.NewFile.Path ?? default, cancellationToken).ConfigureAwait(false);
                entry = entry with { Id = oid };
                uIndex.Add(entry);
            }
        }

        GitOid uTreeOid = await uIndex.WriteTreeToAsync(this, cancellationToken).ConfigureAwait(false);
        // C (stash.c:336): git_str_printf(&msg, "untracked files on %s\n", message).
        byte[] msg = [.. "untracked files on "u8.ToArray(), .. msgPrefix, .. "\n"u8.ToArray()];

        return await Commit.CreateAsync(this, new CommitCreateOptions
        {
            Tree = uTreeOid,
            Parents = [],
            Author = stasher,
            Committer = stasher,
            Message = Encoding.UTF8.GetString(msg),
            MessageBytes = msg,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] StashPrepareWorktreeCommitMessage(byte[] msgPrefix, string? userMessage)
    {
        // C (stash.c:518-546, prepare_worktree_commit_message): the branch is keyed on user_message == NULL ONLY — an EMPTY string is non-NULL and produces "On
        // <branch>: \n". byte-primary — the user message is authored text (UTF-8-encoded); the prefix stays raw.
        if (userMessage is null)
        {
            return [.. "WIP on "u8.ToArray(), .. msgPrefix];
        }

        // "On <branch>: <user_message>\n"
        // Extract the branch part before the colon.
        int colonPos = msgPrefix.AsSpan().IndexOf((byte)':');
        if (colonPos < 0)
        {
            return [.. "WIP on "u8.ToArray(), .. msgPrefix];
        }

        return
        [
            .. "On "u8.ToArray(),
            .. msgPrefix[..colonPos],
            .. ": "u8.ToArray(),
            .. Encoding.UTF8.GetBytes(userMessage),
            .. "\n"u8.ToArray(),
        ];
    }

    private async ValueTask<GitOid> StashCommitWorktreeAsync(
        GitSignature stasher, byte[] msg,
        GitOid iCommitOid, Commit bCommit, GitOid? uCommitOid, CancellationToken cancellationToken)
    {
        // Build the workdir tree by diffing base→index and index→workdir,
        // then merging.
        var iIndex = GitIndex.New(ObjectFormat);
        GitIndex rIndex = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        iIndex.Fill(rIndex.Snapshot());
        // C (stash.c:487-494): the stash's index ignore-case comes from the
        // core.ignorecase configmap lookup directly — an unparseable value
        // FAILS the stash save (goto cleanup) instead of falling back to the
        // repo index's value.
        iIndex.IgnoreCase = await Config.GetConfigmapBoolOrThrowAsync("core.ignorecase", false, cancellationToken).ConfigureAwait(false);

        GitTree bTree = await Objects.LookupAsync<GitTree>(bCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "base tree not found", GitErrorCategory.Object);

        // Build workdir tree.
        GitOid wTreeOid = await StashBuildWorkdirTreeAsync(iIndex, bTree, cancellationToken).ConfigureAwait(false);

        // Build the stash commit with 2 or 3 parents.
        var parents = new List<GitOid> { bCommit.Id, iCommitOid };
        if (uCommitOid.HasValue)
        {
            parents.Add(uCommitOid.Value);
        }

        return await Commit.CreateAsync(this, new CommitCreateOptions
        {
            Tree = wTreeOid,
            Parents = parents,
            Author = stasher,
            Committer = stasher,
            Message = Encoding.UTF8.GetString(msg),
            MessageBytes = msg,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitOid> StashBuildWorkdirTreeAsync(GitIndex iIndex, GitTree bTree, CancellationToken cancellationToken)
    {
        // Exact port of build_workdir_tree (stash.c:379-414): the base tree
        // → index diff is MERGED with the index → workdir diff via
        // stash_delta_merge, then stash_update_index_from_diff applies the
        // merged deltas (include_changed=true, include_untracked=false).
        var opts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IgnoreSubmodules | GitDiffOptionsFlags.IncludeUntracked,
        };

        DiffGenerator idxDiff = await DiffGenerator.TreeToIndexAsync(this, bTree, opts, cancellationToken).ConfigureAwait(false);
        DiffGenerator wdDiff = await DiffGenerator.IndexToWorkdirAsync(this, opts, cancellationToken).ConfigureAwait(false);
        List<GitDiffDelta> merged = MergeStashDeltas(idxDiff.Deltas, wdDiff.Deltas);

        // stash_update_index_from_diff (stash.c:221-265).
        foreach (GitDiffDelta delta in merged)
        {
            switch (delta.Status)
            {
                case GitDeltaStatus.Added:
                case GitDeltaStatus.Modified:
                    // stash_to_index: read the WORKDIR blob for the path.
                    (GitOid oid, GitIndexEntry entry) = await BlobHelper.CreateFromWorkdirAsync(this, delta.NewFile.Path ?? default, cancellationToken).ConfigureAwait(false);
                    iIndex.Add(entry with { Id = oid });
                    break;
                case GitDeltaStatus.Deleted:
                    _ = iIndex.Remove(delta.OldFile.Path ?? default, 0);
                    break;
                case GitDeltaStatus.Untracked:
                case GitDeltaStatus.Ignored:
                    // include_untracked/include_ignored are false here.
                    break;
                default:
                    // C (stash.c:258-263): "cannot update index. Unimplemented
                    // status (%d)" with GIT_ERROR_INVALID.
                    throw new GitException(
                        GitErrorCode.Error,
                        $"cannot update index. Unimplemented status ({(int)delta.Status})",
                        GitErrorCategory.Invalid);
            }
        }

        return await iIndex.WriteTreeToAsync(this, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges the tree→index delta list with the index→workdir delta list,
    /// pairing by path. Matches <c>git_diff__merge</c> with
    /// <c>stash_delta_merge</c> (stash.c:360-366) + <c>git_diff__merge_like_cgit</c>
    /// (diff_tform.c:51-110).
    /// </summary>
    private static List<GitDiffDelta> MergeStashDeltas(IReadOnlyList<GitDiffDelta> indexDeltas, IReadOnlyList<GitDiffDelta> wdDeltas)
    {
        var merged = new List<GitDiffDelta>(indexDeltas.Count + wdDeltas.Count);
        var used = new HashSet<int>();

        foreach (GitDiffDelta b in wdDeltas)
        {
            int i = FindIndex(indexDeltas, b.OldFile.Path);
            if (i < 0)
            {
                merged.Add(b);
                continue;
            }

            used.Add(i);
            GitDiffDelta a = indexDeltas[i];

            // stash_delta_merge (stash.c:360-366): a file deleted in the index
            // but present in the workdir is stashed as MODIFIED (workdir copy).
            if (a.Status == GitDeltaStatus.Deleted && b.Status == GitDeltaStatus.Untracked)
            {
                merged.Add(new GitDiffDelta(GitDeltaStatus.Modified, b.FileCount, b.OldFile, b.NewFile));
                continue;
            }

            // git_diff__merge_like_cgit (diff_tform.c:51-110).
            if (b.Status == GitDeltaStatus.Conflicted)
            {
                merged.Add(b);
                continue;
            }

            if (a.Status == GitDeltaStatus.Conflicted)
            {
                merged.Add(a);
                continue;
            }

            if (b.Status == GitDeltaStatus.Unmodified || a.Status == GitDeltaStatus.Deleted)
            {
                merged.Add(a);
                continue;
            }

            if (a.Status is GitDeltaStatus.Unmodified or GitDeltaStatus.Untracked or GitDeltaStatus.Unreadable)
            {
                merged.Add(b);
                continue;
            }

            GitDeltaStatus mergedStatus = b.Status;
            if (mergedStatus == GitDeltaStatus.Deleted)
            {
                // The cgit exception: a file only in the index (added in the
                // index diff, deleted in the workdir diff) is given as empty.
                if (a.Status == GitDeltaStatus.Added)
                {
                    mergedStatus = GitDeltaStatus.Unmodified;
                }
            }
            else
            {
                mergedStatus = a.Status;
            }

            merged.Add(new GitDiffDelta(mergedStatus, a.FileCount, a.OldFile, b.NewFile));
        }

        for (int i = 0; i < indexDeltas.Count; i++)
        {
            if (!used.Contains(i))
            {
                merged.Add(indexDeltas[i]);
            }
        }

        return merged;
    }

    private static int FindIndex(IReadOnlyList<GitDiffDelta> deltas, GitPath? path)
    {
        for (int i = 0; i < deltas.Count; i++)
        {
            if (deltas[i].OldFile.Path == path)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Stash-specific delta merge fixup: if a file is Deleted in the index but
    /// Untracked in the workdir, treat it as Modified (stash the workdir copy).
    /// Matches <c>stash_delta_merge</c> (stash.c:360-377).
    /// </summary>
    private static void StashDeltaMergeFixup(GitDiff _)
    {
        // The Diff.Merge already combined the deltas using the standard
        // git_diff__merge_like_cgit logic. The stash special case (Deleted +
        // Untracked → Modified) needs to be applied as a post-merge fixup.
        // Since DiffDelta objects have internal-set Status, we scan for
        // Deleted+Untracked pairs at the same path and handle them.
        // Note: the standard merge already produces a single delta for matching
        // paths. A Deleted (from index diff) merged with Untracked (from
        // workdir diff) produces a Modified delta per the cgit merge matrix
        // (Deleted + Untracked → Modified). So the fixup is already handled
        // by the standard merge logic. This is a no-op in practice.
    }

    private async Task<GitOid> StashCommitWorktreeFromPathsAsync(
        GitSignature stasher, byte[] msg,
        GitOid iCommitOid, Commit bCommit, GitOid? uCommitOid,
        string[] paths, CancellationToken cancellationToken)
    {
        GitReference head = await Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "HEAD is not set", GitErrorCategory.Reference);
        GitOid headTarget = head is GitDirectReference dr
            ? dr.Target
            : (await ((GitSymbolicReference)head).TargetAsync(cancellationToken).ConfigureAwait(false)) is GitDirectReference dt
                ? dt.Target
                : throw new GitException(GitErrorCode.UnbornBranch, "cannot resolve HEAD", GitErrorCategory.Stash);

        Commit tree = await Objects.LookupAsync<Commit>(headTarget, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "HEAD commit not found", GitErrorCategory.Object);
        GitTree headTree = await Objects.LookupAsync<GitTree>(tree.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "HEAD tree not found", GitErrorCategory.Object);

        var pathsIndex = GitIndex.New(ObjectFormat);
        await pathsIndex.ReadTreeAsync(headTree, cancellationToken).ConfigureAwait(false);

        // Update index from paths.
        foreach (string path in paths)
        {
            GitStatusFlags statusFlags = await StatusFileAsync(path, cancellationToken).ConfigureAwait(false);

            if ((statusFlags & (GitStatusFlags.WorkdirDeleted | GitStatusFlags.IndexDeleted)) != 0)
            {
                _ = pathsIndex.Remove(path, 0);
            }
            else
            {
                (GitOid oid, GitIndexEntry entry) = await BlobHelper.CreateFromWorkdirAsync(this, path, cancellationToken).ConfigureAwait(false);
                entry = entry with { Id = oid };
                pathsIndex.Add(entry);
            }
        }

        GitOid treeOid = await pathsIndex.WriteTreeToAsync(this, cancellationToken).ConfigureAwait(false);

        var parents = new List<GitOid> { bCommit.Id, iCommitOid };
        if (uCommitOid.HasValue)
        {
            parents.Add(uCommitOid.Value);
        }

        return await Commit.CreateAsync(this, new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = stasher,
            Committer = stasher,
            Message = Encoding.UTF8.GetString(msg),
            MessageBytes = msg,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task StashUpdateReflogAsync(GitOid wCommitOid, byte[] message, CancellationToken cancellationToken)
    {
        await Refs.EnsureLogAsync(StashRefsStashFile, cancellationToken).ConfigureAwait(false);
        await Refs.CreateAsync(StashRefsStashFile, wCommitOid, force: true, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task StashResetIndexAndWorkdirAsync(GitOid commitOid, GitStashFlags flags, CancellationToken cancellationToken)
    {
        // Build the checkout strategy: Force (to overwrite workdir changes) +
        // RemoveUntracked/RemoveIgnored if the stash included untracked/ignored
        // files. The checkout's workdir lockstep walk handles
        // untracked/ignored removal natively — no separate pass needed.
        GitCheckoutStrategy strategy = GitCheckoutStrategy.Force;

        if ((flags & GitStashFlags.IncludeUntracked) != 0)
        {
            strategy |= GitCheckoutStrategy.RemoveUntracked;
        }

        if ((flags & GitStashFlags.IncludeIgnored) != 0)
        {
            strategy |= GitCheckoutStrategy.RemoveIgnored;
        }

        Commit commit = await Objects.LookupAsync<Commit>(commitOid, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "untracked commit not found", GitErrorCategory.Object);
        GitTree tree = await Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "untracked commit tree not found", GitErrorCategory.Object);
        await CheckoutTreeAsync(tree, new GitCheckoutOptions
        {
            Strategy = strategy,
        }, cancellationToken).ConfigureAwait(false);

        // Reset the index to match the target tree.
        GitIndex index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        index.Clear();
        await index.ReadTreeAsync(tree, cancellationToken).ConfigureAwait(false);
        await index.WriteAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Private: apply helpers ──────────────────────────────────────────

    private async Task<GitOid> StashRetrieveStashCommitAsync(int index, CancellationToken cancellationToken)
    {
        if (await Refs.LookupAsync(StashRefsStashFile, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new GitException(GitErrorCode.NotFound, "no stash found", GitErrorCategory.Stash);
        }

        GitRefLog reflog = await Refs.ReadLogAsync(StashRefsStashFile, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "no stash reflog found", GitErrorCategory.Stash);

        int max = reflog.EntryCount;
        if (max == 0 || index > max - 1)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"no stashed state at position {index}",
                GitErrorCategory.Stash);
        }

        return reflog[index].NewId;
    }

    private async Task<(GitTree? stashTree, GitTree? stashParentTree, GitTree? indexTree, GitTree? indexParentTree, GitTree? untrackedTree)>
        StashRetrieveStashTreesAsync(Commit stashCommit, CancellationToken cancellationToken)
    {
        GitTree? stashTree = await Objects.LookupAsync<GitTree>(stashCommit.Tree, cancellationToken).ConfigureAwait(false);
        GitTree? stashParentTree = null;
        GitTree? indexTree = null;
        GitTree? indexParentTree = null;
        GitTree? untrackedTree = null;

        // Parent 0 = base commit.
        if (stashCommit.Parents.Count > 0)
        {
            Commit baseCommit = await Objects.LookupAsync<Commit>(stashCommit.Parents[0], cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, "stash base commit not found", GitErrorCategory.Object);
            stashParentTree = await Objects.LookupAsync<GitTree>(baseCommit.Tree, cancellationToken).ConfigureAwait(false);
        }

        // Parent 1 = index commit.
        if (stashCommit.Parents.Count > 1)
        {
            Commit indexCommit = await Objects.LookupAsync<Commit>(stashCommit.Parents[1], cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, "stash index commit not found", GitErrorCategory.Object);
            indexTree = await Objects.LookupAsync<GitTree>(indexCommit.Tree, cancellationToken).ConfigureAwait(false);

            // Index commit's parent 0 = base commit.
            if (indexCommit.Parents.Count > 0)
            {
                Commit indexParent = await Objects.LookupAsync<Commit>(indexCommit.Parents[0], cancellationToken).ConfigureAwait(false)
                    ?? throw new GitException(GitErrorCode.NotFound, "stash index parent commit not found", GitErrorCategory.Object);
                indexParentTree = await Objects.LookupAsync<GitTree>(indexParent.Tree, cancellationToken).ConfigureAwait(false);
            }
        }

        // Parent 2 = untracked commit (optional).
        if (stashCommit.Parents.Count == 3)
        {
            Commit untrackedCommit = await Objects.LookupAsync<Commit>(stashCommit.Parents[2], cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(GitErrorCode.NotFound, "stash untracked commit not found", GitErrorCategory.Object);
            untrackedTree = await Objects.LookupAsync<GitTree>(untrackedCommit.Tree, cancellationToken).ConfigureAwait(false);
        }

        return (stashTree, stashParentTree, indexTree, indexParentTree, untrackedTree);
    }

    private async Task StashEnsureCleanIndexAsync(GitIndex _, CancellationToken cancellationToken)
    {
        // Diff HEAD tree → index. If any deltas, there are uncommitted changes.
        // C (stash.c:971-992): git_repository_head_tree FAILS on an unborn/
        // missing HEAD (GIT_EUNBORNBRANCH) and the whole stash apply errors —
        GitReference? head = await Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            throw new GitException(
                GitErrorCode.UnbornBranch,
                "reference 'HEAD' not found",
                GitErrorCategory.Reference);
        }

        GitOid headTarget;
        if (head is GitDirectReference dr)
        {
            headTarget = dr.Target;
        }
        else if (head is GitSymbolicReference sr && await sr.TargetAsync(cancellationToken).ConfigureAwait(false) is GitDirectReference dt)
        {
            headTarget = dt.Target;
        }
        else
        {
            throw new GitException(
                GitErrorCode.UnbornBranch,
                "reference 'HEAD' not found",
                GitErrorCategory.Reference);
        }

        Commit? headCommit = await Objects.LookupAsync<Commit>(headTarget, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "commit not found", GitErrorCategory.Object);

        GitTree? headTree = await Objects.LookupAsync<GitTree>(headCommit.Tree, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "tree not found", GitErrorCategory.Object);

        using GitDiff diff = await GitDiff.TreeToIndexAsync(this, headTree, null, cancellationToken).ConfigureAwait(false);
        if (diff.DeltaCount > 0)
        {
            throw new GitException(
                GitErrorCode.Uncommitted,
                $"{diff.DeltaCount} uncommitted changes exist in the index",
                GitErrorCategory.Merge);
        }
    }

    private async Task<GitIndex> StashStageNewFilesAsync(GitTree? parentTree, GitTree tree, CancellationToken cancellationToken)
    {
        // Walk both trees, staging new files from the stash tree.
        // Matches stage_new_files (stash.c:1004-1034).
        var index = GitIndex.New(ObjectFormat);

        var iterOpts = new IteratorOptions();

        using IIterator iter0 = TreeIterator.ForTree(parentTree, this, iterOpts);
        using IIterator iter1 = TreeIterator.ForTree(tree, this, iterOpts);

        await IteratorWalker.WalkAsync([iter0, iter1], async (entries, ct) =>
        {
            GitIndexEntry? e0 = entries[0];
            GitIndexEntry? e1 = entries[1];

            // C (stash.c:994-1002, stage_new_file): when the file exists in
            // the PARENT tree (entries[0]), the parent entry is staged; the
            // stash-tree entry is used only when the parent has no entry
            // (otherwise the repo index would end up holding the
            // stash's content after merge_indexes).
            if (e0.HasValue && e0.Value.Mode != 0)
            {
                index.Add(e0.Value);
            }
            else if (e1.HasValue && e1.Value.Mode != 0)
            {
                index.Add(e1.Value);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);

        return index;
    }
}
