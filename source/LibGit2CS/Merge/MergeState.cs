// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Globalization;

using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Merge;

/// <summary>
/// Writes and reads merge state files (<c>MERGE_HEAD</c>, <c>MERGE_MODE</c>,
/// <c>MERGE_MSG</c>). Managed port of <c>git_merge__setup</c> (merge.c:2844-2863),
/// <c>write_merge_head</c> (merge.c:2481-2512), <c>write_merge_mode</c>
/// (merge.c:2514-2538), <c>write_merge_msg</c> (merge.c:2727-2842), and
/// <c>git_merge__append_conflicts_to_merge_msg</c> (merge.c:3123-163).
/// </summary>
/// <remarks>
/// <para>
/// All state files are written atomically (write-to-temp + rename) to avoid
/// torn writes on crash. The files live in <c>repo.Path</c> (the <c>.git</c>
/// directory).
/// </para>
/// <para>
/// <c>MERGE_HEAD</c> contains one OID per line (one per merge head).
/// <c>MERGE_MODE</c> always contains <c>"no-ff"</c>.
/// <c>MERGE_MSG</c> contains the human-readable merge message, e.g.
/// <c>"Merge branch 'feature'\n"</c>, optionally followed by a
/// <c>#Conflicts:\n#\tpath\n</c> section.
/// </para>
/// </remarks>
internal static class MergeState
{
    private const string MergeHeadFile = "MERGE_HEAD";
    private const string MergeModeFile = "MERGE_MODE";
    private const string MergeMsgFile = "MERGE_MSG";

    /// <summary>
    /// Writes all merge state files: <c>ORIG_HEAD</c>, <c>MERGE_HEAD</c>,
    /// <c>MERGE_MODE</c>, <c>MERGE_MSG</c>. Matches <c>git_merge__setup</c>
    /// (merge.c:2844-2863).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="ourHead">Our HEAD as an annotated commit (for ORIG_HEAD).</param>
    /// <param name="theirHeads">The merge heads being merged in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task SetupAsync(
        GitRepository repo,
        GitAnnotatedCommit ourHead,
        IReadOnlyList<GitAnnotatedCommit> theirHeads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(ourHead);
        ArgumentNullException.ThrowIfNull(theirHeads);

        // Write ORIG_HEAD (merge.c:2856).
        await repo.SetOrigHeadAsync(ourHead.Id, cancellationToken).ConfigureAwait(false);

        // Write MERGE_HEAD (merge.c:2857).
        await WriteMergeHeadAsync(repo, theirHeads, cancellationToken).ConfigureAwait(false);

        // Write MERGE_MODE (merge.c:2858).
        await WriteMergeModeAsync(repo, cancellationToken).ConfigureAwait(false);

        // Write MERGE_MSG (merge.c:2859).
        await WriteMergeMsgAsync(repo, theirHeads, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <c>.git/MERGE_HEAD</c> — one OID per line. Matches
    /// <c>write_merge_head</c> (merge.c:2481-2512).
    /// </summary>
    public static async Task WriteMergeHeadAsync(
        GitRepository repo,
        IReadOnlyList<GitAnnotatedCommit> heads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(heads);

        string content;

        const int InitialCapacity = 512;
        using (var sb = new ValueStringBuilder(InitialCapacity))
        {
            foreach (GitAnnotatedCommit head in heads)
            {
                sb.AppendSpanFormattable(head.Id, provider: CultureInfo.InvariantCulture);
                sb.Append('\n');
            }
            content = sb.ToString();
        }

        await WriteAtomicAsync(repo, MergeHeadFile, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <c>.git/MERGE_MODE</c> — always <c>"no-ff"</c>. Matches
    /// <c>write_merge_mode</c> (merge.c:2514-2538).
    /// </summary>
    public static async Task WriteMergeModeAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        await WriteAtomicAsync(repo, MergeModeFile, "no-ff", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <c>.git/MERGE_MSG</c> — the merge commit message. Matches
    /// <c>write_merge_msg</c> (merge.c:2727-2842).
    /// </summary>
    public static async Task WriteMergeMsgAsync(
        GitRepository repo,
        IReadOnlyList<GitAnnotatedCommit> heads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(heads);

        string msg = MergeMessage.Build(heads);
        await WriteAtomicAsync(repo, MergeMsgFile, msg, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Appends conflict path list to <c>MERGE_MSG</c>. Matches <c>git_merge__append_conflicts_to_merge_msg</c> (merge.c:3123-3163). Does nothing if
    /// the index has no conflicts. C opens the file in <c>GIT_FILEBUF_APPEND</c> mode and writes the raw index path bytes (merge.c:3137-3149) — the port
    /// appends raw bytes too (no read-modify-write, no decode). </summary>
    public static async Task AppendConflictsToMergeMsgAsync(
        GitRepository repo,
        GitIndex index,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(index);

        if (!index.HasConflicts)
        {
            return;
        }

        string msgPath = Path.Join(repo.Path, MergeMsgFile);

        using var buffer = new PooledByteBufferWriter(256);
        buffer.Write("\n#Conflicts:\n"u8);

        GitPath? last = null;
        foreach (GitIndexEntry entry in index.Entries)
        {
            if (!entry.IsConflict)
            {
                continue;
            }

            // Deduplicate: each conflict occupies 3 index entries (stages 1/2/3)
            // with the same path. Only write each path once.
            if (last is null || entry.Path != last.Value)
            {
                buffer.Write("#\t"u8);
                buffer.Write(entry.Path.Span);
                buffer.WriteByte((byte)'\n');
            }

            last = entry.Path;
        }

        await AsyncFileIO.AppendAllBytesAsync(msgPath, buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <c>.git/MERGE_MSG</c> with the given content directly (no
    /// merge-message builder). Used by cherry-pick and revert, which
    /// supply their own message content (the raw commit message or a
    /// "Revert ..." template).
    /// </summary>
    public static async Task WriteMergeMsgAsync(GitRepository repo, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        await WriteAtomicAsync(repo, MergeMsgFile, content, cancellationToken).ConfigureAwait(false);
    }

    // ── Atomic write helper ────────────────────────────────────────────

    /// <summary>
    /// Atomically writes <paramref name="content"/> to
    /// <c>repo.Path/<paramref name="fileName"/></c> via temp-file + rename.
    /// Delegates to <see cref="AsyncFileIO.WriteAtomicTextAsync"/>.
    /// </summary>
    internal static async Task WriteAtomicAsync(GitRepository repo, string fileName, string content, CancellationToken cancellationToken = default)
    {
        string path = Path.Join(repo.Path, fileName);
        await AsyncFileIO.WriteAtomicTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }
}
