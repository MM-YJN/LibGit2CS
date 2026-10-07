// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Repository;

namespace LibGit2CS.Rebase;

/// <summary>
/// Rebase state file setup and parsing. Managed port of the state-file
/// portions of <c>rebase.c</c> (lines 229–493).
/// </summary>
internal static class RebaseState
{
    // ── Open (parse existing state files) ─────────────────────────────

    /// <summary>
    /// Parses merge-style rebase state files to reconstruct the operation
    /// list and state. Matches <c>rebase_open_merge</c> (rebase.c:229-282).
    /// </summary>
    /// <param name="rebase">The rebase object to populate (operations, current, started, onto_name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task OpenMergeAsync(GitRebase rebase, CancellationToken cancellationToken)
    {
        string statePath = rebase.CurrentStatePath;
        GitHashAlgorithmKind oidType = rebase.Repository.ObjectFormat;

        // Read 'msgnum' if it exists (otherwise let msgnum = 0).
        int? msgnum = await RebaseStateFiles.ReadIntAsync(statePath, RebaseStateFiles.MsgNumFile, cancellationToken).ConfigureAwait(false);
        if (msgnum is > 0)
        {
            rebase.Started = true;
            rebase.Current = msgnum.Value - 1;
        }

        // Read 'end' (total operation count).
        int end = (await RebaseStateFiles.ReadIntAsync(statePath, RebaseStateFiles.EndFile, cancellationToken).ConfigureAwait(false))
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"rebase state file '{RebaseStateFiles.EndFile}' not found",
                GitErrorCategory.Rebase);

        // Read 'current' if it exists (OID of the commit currently being processed).
        // Not strictly needed for state reconstruction; C reads it but doesn't use it
        // beyond the read. We read it to validate.
        _ = await RebaseStateFiles.ReadOidAsync(statePath, RebaseStateFiles.CurrentFile, oidType, cancellationToken).ConfigureAwait(false);

        // Read cmt.* files and build operations list.
        var operations = new List<GitRebaseOperation>(end);
        for (int i = 0; i < end; i++)
        {
            string cmtFilename = $"cmt.{i + 1}";
            GitOid id = (await RebaseStateFiles.ReadOidAsync(statePath, cmtFilename, oidType, cancellationToken).ConfigureAwait(false))
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    $"rebase state file '{cmtFilename}' not found",
                    GitErrorCategory.Rebase);
            operations.Add(GitRebaseOperation.CreatePick(id));
        }

        rebase.Operations = operations;

        // Read 'onto_name'.
        string ontoName = (await RebaseStateFiles.ReadFileAsync(statePath, RebaseStateFiles.OntoNameFile, cancellationToken).ConfigureAwait(false))
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"rebase state file '{RebaseStateFiles.OntoNameFile}' not found",
                GitErrorCategory.Rebase);
        rebase.OntoName = ontoName;
    }

    // ── Setup (write state files for new rebase) ──────────────────────

    /// <summary>
    /// Writes all state files for a new merge-style rebase. Matches
    /// <c>rebase_setupfiles</c> (rebase.c:469-493) +
    /// <c>rebase_setupfiles_merge</c> (rebase.c:440-467).
    /// </summary>
    internal static async Task SetupFilesAsync(GitRebase rebase, CancellationToken cancellationToken)
    {
        GitRepository repo = rebase.Repository;
        string statePath = rebase.CurrentStatePath;

        // Create the state directory.
        Directory.CreateDirectory(statePath);

        // Set ORIG_HEAD in the gitdir.
        await repo.SetOrigHeadAsync(rebase.OrigHeadId, cancellationToken).ConfigureAwait(false);

        // Write head-name, onto, orig-head, quiet.
        string? origHeadName = rebase.HeadDetached
            ? RebaseStateFiles.OrigDetachedHead
            : rebase.OrigHeadName;
        await RebaseStateFiles.WriteFileAsync(repo, statePath, RebaseStateFiles.HeadNameFile, origHeadName + "\n", cancellationToken).ConfigureAwait(false);
        await RebaseStateFiles.WriteFileAsync(repo, statePath, RebaseStateFiles.OntoFile, rebase.OntoId + "\n", cancellationToken).ConfigureAwait(false);
        await RebaseStateFiles.WriteFileAsync(repo, statePath, RebaseStateFiles.OrigHeadFile, rebase.OrigHeadId + "\n", cancellationToken).ConfigureAwait(false);
        await RebaseStateFiles.WriteFileAsync(repo, statePath, RebaseStateFiles.QuietFile, rebase.Quiet ? "t\n" : "\n", cancellationToken).ConfigureAwait(false);

        // Write merge-style files: end, onto_name, cmt.<N>.
        await SetupFilesMergeAsync(rebase, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the merge-style state files: <c>end</c>, <c>onto_name</c>,
    /// <c>cmt.&lt;N&gt;</c>. Matches <c>rebase_setupfiles_merge</c>
    /// (rebase.c:440-467).
    /// </summary>
    internal static async Task SetupFilesMergeAsync(GitRebase rebase, CancellationToken cancellationToken)
    {
        GitRepository repo = rebase.Repository;
        string statePath = rebase.CurrentStatePath;

        await RebaseStateFiles.WriteFileAsync(repo, statePath, RebaseStateFiles.EndFile, rebase.Operations.Count + "\n", cancellationToken).ConfigureAwait(false);
        await RebaseStateFiles.WriteFileAsync(repo, statePath, RebaseStateFiles.OntoNameFile, rebase.OntoName + "\n", cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < rebase.Operations.Count; i++)
        {
            GitRebaseOperation op = rebase.Operations[i];
            string cmtFilename = $"cmt.{i + 1}";
            await RebaseStateFiles.WriteFileAsync(repo, statePath, cmtFilename, op.Id + "\n", cancellationToken).ConfigureAwait(false);
        }
    }
}
