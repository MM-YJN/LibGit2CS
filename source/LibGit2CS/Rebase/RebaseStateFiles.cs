// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Merge;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Rebase;

/// <summary>
/// State file names under <c>.git/rebase-merge/</c>. Matches the constants
/// in <c>rebase.c:29-46</c>.
/// </summary>
internal static class RebaseStateFiles
{
    public const string RebaseApplyDir = "rebase-apply";
    public const string RebaseMergeDir = "rebase-merge";
    public const string HeadNameFile = "head-name";
    public const string OrigHeadFile = "orig-head";
    public const string HeadFile = "head"; // old-style fallback
    public const string OntoFile = "onto";
    public const string OntoNameFile = "onto_name";
    public const string QuietFile = "quiet";
    public const string InteractiveFile = "interactive";
    public const string MsgNumFile = "msgnum";
    public const string EndFile = "end";
    public const string CurrentFile = "current";
    public const string RewrittenFile = "rewritten";

    public const string OrigDetachedHead = "detached HEAD";

    // ── Type detection ────────────────────────────────────────────────

    /// <summary>
    /// Detects the rebase type from the gitdir. Matches
    /// <c>rebase_state_type</c> (rebase.c:93-136).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="statePath">Receives the absolute path to the state directory (e.g. <c>.git/rebase-merge/</c>), or null if no rebase is in progress.</param>
    /// <returns>The detected <see cref="RebaseType"/>.</returns>
    internal static RebaseType DetectType(GitRepository repo, out string? statePath)
    {
        statePath = null;
        string gitdir = repo.Path;

        string rebaseApplyPath = Path.Join(gitdir, RebaseApplyDir);
        if (Directory.Exists(rebaseApplyPath))
        {
            statePath = rebaseApplyPath;
            return RebaseType.Apply;
        }

        string rebaseMergePath = Path.Join(gitdir, RebaseMergeDir);
        if (Directory.Exists(rebaseMergePath))
        {
            statePath = rebaseMergePath;
            string interactivePath = Path.Join(rebaseMergePath, InteractiveFile);
            return File.Exists(interactivePath)
                ? RebaseType.Interactive
                : RebaseType.Merge;
        }

        return RebaseType.None;
    }

    // ── State file reading ───────────────────────────────────────────

    /// <summary>
    /// Reads a state file as a string (right-trimmed). Matches
    /// <c>rebase_readfile</c> (rebase.c:138-163). Returns null if the file
    /// doesn't exist (GIT_ENOTFOUND).
    /// </summary>
    internal static async Task<string?> ReadFileAsync(string statePath, string filename, CancellationToken cancellationToken)
    {
        string path = Path.Join(statePath, filename);
        if (!File.Exists(path))
        {
            return null;
        }

        string content = await AsyncFileIO.ReadAllTextWithNoBomAsync(path, cancellationToken).ConfigureAwait(false);
        // C (rebase.c:158): git_str_rtrim — ASCII whitespace only.
        return AsciiText.Rtrim(content).ToString();
    }

    /// <summary>
    /// Reads a state file as an int. Matches <c>rebase_readint</c>
    /// (rebase.c:165-186). Returns null if the file doesn't exist.
    /// </summary>
    internal static async Task<int?> ReadIntAsync(string statePath, string filename, CancellationToken cancellationToken)
    {
        string? content = await ReadFileAsync(statePath, filename, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }

        if (!int.TryParse(content, out int result) || result < 0)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"the file '{filename}' contains an invalid numeric value",
                GitErrorCategory.Rebase);
        }

        return result;
    }

    /// <summary>
    /// Reads a state file as an OID. Matches <c>rebase_readoid</c>
    /// (rebase.c:188-206). Returns null if the file doesn't exist.
    /// </summary>
    internal static async Task<GitOid?> ReadOidAsync(string statePath, string filename, GitHashAlgorithmKind oidType, CancellationToken cancellationToken)
    {
        string? content = await ReadFileAsync(statePath, filename, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }

        int expectedHexSize = GitOid.HexSizeFor(oidType);
        if (content.Length != expectedHexSize)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"the file '{filename}' contains an invalid object ID",
                GitErrorCategory.Rebase);
        }

        // non-hex
        // content must surface as a Rebase-category GitException, not an
        // unhandled FormatException (C returns -1, GIT_ERROR_REBASE).
        try
        {
            return GitOid.Parse(content.AsSpan(), oidType);
        }
        catch (FormatException)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"the file '{filename}' contains an invalid object ID",
                GitErrorCategory.Rebase);
        }
    }

    // ── State file writing ───────────────────────────────────────────

    /// <summary>
    /// Writes a state file atomically. Matches <c>rebase_setupfile</c>
    /// (rebase.c:410-428). Reuses <see cref="MergeState.WriteAtomicAsync"/> for
    /// write-to-temp + atomic rename.
    /// </summary>
    internal static async Task WriteFileAsync(GitRepository repo, string statePath, string filename, string content, CancellationToken cancellationToken)
    {
        // Use MergeState.WriteAtomicAsync which writes to repo.Path/filename.
        // For rebase state files under rebase-merge/, we compose the relative
        // path and use WriteAtomicAsync.
        string relativePath = Path.Join(
            statePath.Substring(repo.Path.Length).TrimStart(Path.DirectorySeparatorChar),
            filename);
        await MergeState.WriteAtomicAsync(repo, relativePath, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a line to a state file. Used for <c>rewritten</c> which is
    /// append-only (<c>O_CREAT|O_WRONLY|O_APPEND</c> in C).
    /// </summary>
    internal static async Task AppendFileAsync(string statePath, string filename, string line, CancellationToken cancellationToken)
    {
        string path = Path.Join(statePath, filename);
        await AsyncFileIO.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
    }
}
