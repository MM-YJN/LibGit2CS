// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Status;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Repository;

/// <content> Status and ignore operations. Managed port of libgit2's <c>src/libgit2/status.c</c> and <c>src/libgit2/ignore.c</c> public entry points.
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    // ── Status ──────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the status flags for a single file. Matches
    /// <c>git_status_file</c> (status.c:488-539). Returns
    /// <see cref="GitStatusFlags.Current"/> if the file is unmodified.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if the file does not exist in the
    /// working tree or index. <see cref="GitErrorCode.Ambiguous"/> if the path
    /// matches multiple files.
    /// </exception>
    public async Task<GitStatusFlags> StatusFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return await StatusFileAsync(GitPath.FromUtf8String(path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Byte-faithful overload of <see cref="StatusFileAsync(string, CancellationToken)"/>. </summary>
    public async ValueTask<GitStatusFlags> StatusFileAsync(GitPath path, CancellationToken cancellationToken = default)
    {
        GitIndex index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        bool ignoreCase = index?.IgnoreCase ?? false;

        var opts = new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeIgnored
                  | GitStatusFlags.RecurseIgnoredDirs
                  | GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs
                  | GitStatusFlags.IncludeUnmodified
                  | GitStatusFlags.DisablePathspecMatch,
            PathSpecs = [path],
        };

        int count = 0;
        GitStatusFlags foundStatus = GitStatusFlags.Current;
        bool ambiguous = false;

        // Byte-wise strcomp (git__strcmp / git__strcasecmp) — ports get_one_status (status.c:467-486) faithfully: the C dispatcher picks
        // git__strcmp or git__strcasecmp from wildmatch_flags, NOT from the diff's strcomp slot. ASCII-fold (not OrdinalIgnoreCase) matches libgit2
        // byte-for-byte. The caller-supplied `path` is byte-faithful.
        GitPath pathBytes = path;

        using GitStatusList list = await GitStatusList.NewAsync(this, opts, cancellationToken).ConfigureAwait(false);
        foreach (GitStatusEntry entry in list.Entries)
        {
            count++;
            foundStatus = entry.Status;

            GitPath entryPath = entry.Path;
            int strcomp = ignoreCase
                ? GitPath.CompareIgnoreCase(pathBytes, entryPath)
                : GitPath.Compare(pathBytes, entryPath);

            // Matches get_one_status (status.c:477-483): ambiguous if count > 1 OR (exact compare fails AND wildmatch fails). Byte-faithful wildmatch — the
            // path and entryPath are compared as raw bytes; non-UTF-8 paths round-trip byte-exact and the ASCII-fold parity fix applies.
            WildMatchFlags wildmatchFlags = ignoreCase ? WildMatchFlags.CaseInsensitive : WildMatchFlags.None;
            bool wildmatchFails = !WildMatch.IsMatch(pathBytes.Span, entryPath.Span, wildmatchFlags);

            if (count > 1 || (strcomp != 0 && wildmatchFails))
            {
                ambiguous = true;
                break;
            }
        }

        if (ambiguous)
        {
            throw new GitException(GitErrorCode.Ambiguous,
                $"ambiguous path '{path}' given to Status.File", GitErrorCategory.Repository);
        }

        if (count == 0)
        {
            throw new GitException(GitErrorCode.NotFound,
                $"attempt to get status of nonexistent file '{path}'", GitErrorCategory.Repository);
        }

        return foundStatus;
    }

    /// <summary>
    /// Checks whether ignore rules apply to a path. Matches
    /// <c>git_status_should_ignore</c> (status.c:541-547).
    /// </summary>
    public ValueTask<bool> StatusShouldIgnoreAsync(string path, CancellationToken cancellationToken = default)
        => IsIgnoredAsync(path, cancellationToken);

    /// <summary>Creates a status list. Convenience wrapper for <see cref="GitStatusList.NewAsync"/>.</summary>
    public Task<GitStatusList> StatusNewAsync(GitStatusOptions? options = null, CancellationToken cancellationToken = default)
        => GitStatusList.NewAsync(this, options, cancellationToken);

    // ── Ignore ──────────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether a path (relative to the workdir) is ignored by the
    /// aggregate ignore rules. Matches <c>git_ignore_path_is_ignored</c>
    /// (ignore.c:533-601).
    /// </summary>
    /// <param name="path">The path to check (relative to workdir root).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the path is ignored; false otherwise.</returns>
    public async ValueTask<bool> IsIgnoredAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return await IgnoreContext.PathIsIgnoredAsync(this, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Checks whether a byte-faithful path (relative to the workdir) is ignored. Matches <c>git_ignore_path_is_ignored</c> (ignore.c:533-601). The
    /// <see cref="GitPath"/> overload avoids the lossy UTF-8 round-trip of the <see cref="string"/> overload for non-UTF-8 paths. </summary>
    public async ValueTask<bool> IsIgnoredAsync(GitPath path, CancellationToken cancellationToken = default)
    {
        return await IgnoreContext.PathIsIgnoredAsync(this, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds in-memory ignore rules to the repository's internal ignore
    /// set. Matches <c>git_ignore_add_rule</c> (ignore.c:503-515).
    /// </summary>
    /// <param name="rules">Newline-separated <c>.gitignore</c> rule text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The rules are ephemeral (not written to disk) and have the highest
    /// priority. They persist for the life of the repository's attr cache
    /// in libgit2; in this managed port they persist on the repository's
    /// <see cref="IgnoreState"/> until cleared.
    /// </remarks>
    public async Task IgnoreAddRuleAsync(string rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        await IgnoreState.AddRuleAsync(rules, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the internal (in-memory) ignore rules and re-seeds the
    /// defaults (<c>.</c>, <c>..</c>, <c>.git</c>). Matches
    /// <c>git_ignore_clear_internal_rules</c> (ignore.c:517-531).
    /// </summary>
    public async Task IgnoreClearInternalRulesAsync(CancellationToken cancellationToken = default)
    {
        await IgnoreState.ClearInternalRulesAsync(cancellationToken).ConfigureAwait(false);
    }
}
