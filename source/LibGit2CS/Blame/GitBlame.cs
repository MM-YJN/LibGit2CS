// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using Xdiff;
using Xdiff.Emit;

namespace LibGit2CS.Blame;

/// <summary>
/// Line-level blame for a file. Managed port of libgit2's
/// <c>src/libgit2/blame.c</c> public facade.
/// </summary>
/// <remarks>
/// <para>
/// Produces a "blame" (annotated history) that decorates individual lines in a
/// file with the commit that introduced that line. Call <see cref="File"/> to
/// blame a file in a repository, or <see cref="Buffer"/> to update an existing
/// blame with in-memory buffer contents.
/// </para>
/// <para>
/// <b>Dispose</b> the <see cref="GitBlame"/> when done (matches
/// <c>git_blame_free</c>).
/// </para>
/// </remarks>
public sealed class GitBlame : IDisposable
{
    private readonly List<BlameHunk> _hunks = [];
    private readonly BlameScoreboard _sb;
    private bool _disposed;

    private GitBlame(BlameScoreboard sb)
    {
        _sb = sb;
    }

    /// <summary>
    /// Gets the blame for a single file in the repository. Matches
    /// <c>git_blame_file</c>.
    /// </summary>
    /// <param name="repo">Repository whose history is to be walked.</param>
    /// <param name="path">Path to the file to consider (byte-faithful GitPath;
    /// mirrors libgit2's raw <c>const char *path</c>).</param>
    /// <param name="options">Options for the blame operation, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<GitBlame> FileAsync(GitRepository repo, GitPath path, GitBlameOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        GitBlameOptions normOptions = await NormalizeOptionsAsync(options, repo, cancellationToken).ConfigureAwait(false);
        var sb = new BlameScoreboard
        {
            Repository = repo,
            Options = normOptions,
            Path = path,
        };

        // Load mailmap if requested.
        if ((normOptions.Flags & GitBlameFlags.UseMailmap) != 0 && normOptions.Mailmap is null)
        {
            sb.Mailmap = await GitMailmap.FromRepositoryAsync(repo, cancellationToken).ConfigureAwait(false);
        }
        else if (normOptions.Mailmap is not null)
        {
            sb.Mailmap = normOptions.Mailmap;
        }

        sb.PathsAdd(path);

        var blame = new GitBlame(sb);

        // Load the final blob.
        await LoadBlobAsync(sb, cancellationToken).ConfigureAwait(false);
        // Run the blame algorithm.
        await blame.RunBlameAsync(cancellationToken).ConfigureAwait(false);

        return blame;
    }

    /// <summary>
    /// Gets blame data for a file that has been modified in memory. Matches
    /// <c>git_blame_buffer</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="reference"/> is a pre-calculated blame for the in-ODB
    /// history of the file. Lines that differ between the buffer and the
    /// committed version are marked with a zero OID for their
    /// <see cref="BlameHunk.FinalCommitId"/>.
    /// </remarks>
    public static async Task<GitBlame> BufferAsync(GitBlame reference, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var sb = new BlameScoreboard
        {
            Repository = reference._sb.Repository,
            Options = reference._sb.Options,
            Path = reference._sb.Path,
            Mailmap = reference._sb.Mailmap,
            FinalBlob = reference._sb.FinalBlob,
            Final = reference._sb.Final,
        };

        sb.PathsAdd(reference._sb.Path);

        // Duplicate all of the hunk structures in the reference blame.
        foreach (BlameHunk hunk in reference._hunks)
        {
            sb.Hunks.Add(new BlameScoreboardHunk
            {
                LinesInHunk = hunk.LinesInHunk,
                FinalStartLineNumber = hunk.FinalStartLineNumber,
                OrigStartLineNumber = hunk.OrigStartLineNumber,
                OrigPath = hunk.OrigPath,
                FinalCommitId = hunk.FinalCommitId,
                OrigCommitId = hunk.OrigCommitId,
                Boundary = hunk.Boundary,
            });
        }

        var sink = new BlameScoreboardSink(sb);

        // Diff the reference blob to the buffer (context_lines = 0) using XDiff directly.
        GitBlob? refBlob = reference._sb.FinalBlob;
        ReadOnlyMemory<byte> oldContent = refBlob?.Content ?? ReadOnlyMemory<byte>.Empty;
        var xopts = new DiffOptions { ContextLines = 0 };
        Xdiff.Diff.Compute(sink, oldContent, buffer, xopts);

        var blame = new GitBlame(sb);
        // Convert scoreboard hunks to public hunks.
        blame._hunks.Clear();
        foreach (BlameScoreboardHunk h in sb.Hunks)
        {
            blame._hunks.Add(await ScoreboardHunkToPublicAsync(h, reference._sb, cancellationToken).ConfigureAwait(false));
        }

        blame._hunks.Sort((a, b) => a.FinalStartLineNumber.CompareTo(b.FinalStartLineNumber));
        return blame;
    }

    private sealed class BlameScoreboardSink(BlameScoreboard sb) : HunkSinkBase
    {
        public override void BeginHunk()
            => BufferHunkCb(sb, OldStart, OldCount, NewStart);

        public override void Line(DiffLineKind kind, ReadOnlyMemory<byte> content, int oldLine, int newLine)
            => BufferLineCb(sb, kind);
    }

    /// <summary>
    /// The number of hunks in this blame. Matches <c>git_blame_hunkcount</c>.
    /// </summary>
    public int HunkCount => _hunks.Count;

    /// <summary>
    /// The number of lines in this blame. Matches <c>git_blame_linecount</c>.
    /// </summary>
    public int LineCount => _sb.NumLines;

    /// <summary>
    /// Gets the hunk at the given index. Matches <c>git_blame_hunk_byindex</c>.
    /// </summary>
    public BlameHunk GetHunk(int index)
    {
        if ((uint)index >= (uint)_hunks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _hunks[index];
    }

    /// <summary>
    /// Gets all hunks in this blame.
    /// </summary>
    public IEnumerable<BlameHunk> Hunks => _hunks;

    /// <summary>
    /// Gets the hunk that contains the given (1-based) line number. Matches
    /// <c>git_blame_hunk_byline</c>.
    /// </summary>
    /// <returns>The hunk containing the line, or null if no hunk covers it.</returns>
    public BlameHunk? GetHunkByLine(int lineNumber)
    {
        // Binary search: find the hunk where
        // final_start_line_number <= lineNumber < final_start_line_number + lines_in_hunk.
        int lo = 0, hi = _hunks.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            BlameHunk hunk = _hunks[mid];
            if (lineNumber < hunk.FinalStartLineNumber)
            {
                hi = mid;
            }
            else if (lineNumber >= hunk.FinalStartLineNumber + hunk.LinesInHunk)
            {
                lo = mid + 1;
            }
            else
            {
                return hunk;
            }
        }

        return null;
    }

    /// <summary>
    /// Disposes the blame. Matches <c>git_blame_free</c>.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _hunks.Clear();
            _sb.Mailmap?.Dispose();
        }
    }

    /// <summary>
    /// Normalizes blame options. Matches <c>normalize_options</c>
    /// (blame.c:257-287).
    /// </summary>
    private static async Task<GitBlameOptions> NormalizeOptionsAsync(GitBlameOptions? input, GitRepository repo, CancellationToken cancellationToken)
    {
        GitBlameOptions opts = input ?? new GitBlameOptions();

        // No newest_commit => HEAD. C (blame.c:267-272): a HEAD that cannot
        // be resolved FAILS normalize_options with -1 + "reference 'HEAD'
        // not found".
        if (opts.NewestCommit is null || opts.NewestCommit.Value.IsZero)
        {
            GitReference? headRef = await repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
            if (headRef is GitDirectReference dr)
            {
                opts = opts with { NewestCommit = dr.Target };
            }
            else
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "reference 'HEAD' not found",
                    GitErrorCategory.Reference);
            }
        }

        // min_line 0 really means 1.
        if (opts.MinLine is null or <= 0)
        {
            opts = opts with { MinLine = 1 };
        }

        // max_line 0/null really means N (the last line), but we don't know N yet.
        // It's resolved during RunBlame after IndexBlobLines.

        // Fix up option implications (TRACK_COPIES chain).
        GitBlameFlags flags = opts.Flags;
        if ((flags & GitBlameFlags.TrackCopiesAnyCommitCopies) != 0)
        {
            flags |= GitBlameFlags.TrackCopiesSameCommitCopies;
        }

        if ((flags & GitBlameFlags.TrackCopiesSameCommitCopies) != 0)
        {
            flags |= GitBlameFlags.TrackCopiesSameCommitMoves;
        }

        if ((flags & GitBlameFlags.TrackCopiesSameCommitMoves) != 0)
        {
            flags |= GitBlameFlags.TrackCopiesSameFile;
        }

        opts = opts with { Flags = flags };
        return opts;
    }

    /// <summary>
    /// Loads the final blob from the newest commit. Matches <c>load_blob</c>
    /// (blame.c:411-425).
    /// </summary>
    private static async Task LoadBlobAsync(BlameScoreboard sb, CancellationToken cancellationToken)
    {
        if (sb.FinalBlob is not null)
        {
            return;
        }

        GitOid? newestCommit = sb.Options.NewestCommit;
        Debug.Assert(newestCommit is not null, "NewestCommit is set when loading the final blob");
        Commit? commit = await sb.Repository.Objects.LookupAsync<Commit>(newestCommit.Value, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            throw new GitException(GitErrorCode.NotFound,
                $"Commit {sb.Options.NewestCommit} not found", GitErrorCategory.Object);
        }

        sb.Final = commit;
        GitBlob? blob = await BlameEngine.LookupBlobByPathAsync(sb.Repository, commit, sb.Path, cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            throw new GitException(GitErrorCode.NotFound,
                $"Blob at path '{sb.Path.ToUtf8String()}' not found in commit {commit.Id}", GitErrorCategory.Object);
        }

        sb.FinalBlob = blob;
    }

    /// <summary>
    /// Runs the blame algorithm on the scoreboard. Matches
    /// <c>blame_internal</c> (blame.c:427-475).
    /// </summary>
    private async Task RunBlameAsync(CancellationToken cancellationToken)
    {
        BlameScoreboard sb = _sb;
        GitBlob? finalBlob = sb.FinalBlob;
        Debug.Assert(finalBlob is not null, "FinalBlob is loaded by LoadBlobAsync");
        GitBlob blob = finalBlob;

        // Retain the raw blob content for byte-level line indexing.
        sb.FinalBuf = blob.Content;

        // Build the line index.
        sb.IndexBlobLines();

        // Resolve max_line if not set.
        GitBlameOptions opts = sb.Options;
        if (opts.MaxLine is null or <= 0)
        {
            opts = opts with { MaxLine = sb.NumLines };
        }
        sb.Options = opts;

        // Create the initial blame entry covering all lines.
        Commit? final = sb.Final;
        Debug.Assert(final is not null, "Final is set before running blame");
        BlameOrigin origin = await BlameEngine.GetOriginAsync(sb, final, sb.Path, cancellationToken).ConfigureAwait(false);
        var ent = new BlameEntry(origin)
        {
            NumLines = sb.NumLines,
            Lno = (opts.MinLine ?? 1) - 1,
            SLno = (opts.MinLine ?? 1) - 1,
        };

        // Adjust for min/max line range.
        ent.NumLines = sb.NumLines - ((opts.MinLine ?? 1) - 1);
        if (opts.MaxLine is > 0)
        {
            ent.NumLines = (opts.MaxLine.Value) - (opts.MinLine ?? 1) + 1;
        }

        sb.Ent = ent;

        // Run the core algorithm.
        await BlameEngine.LikeGitAsync(sb, opts.Flags, cancellationToken).ConfigureAwait(false);

        // Convert entries to hunks.
        for (BlameEntry? e = sb.Ent; e is not null; e = e.Next)
        {
            BlameHunk hunk = HunkFromEntry(e, sb);
            _hunks.Add(hunk);
        }

        // Sort hunks by final_start_line_number (they should already be in order
        // from the linked list, but sort to be safe).
        _hunks.Sort((a, b) => a.FinalStartLineNumber.CompareTo(b.FinalStartLineNumber));
    }

    /// <summary>
    /// Converts an internal <see cref="BlameEntry"/> to a public
    /// <see cref="BlameHunk"/>. Matches <c>hunk_from_entry</c> (blame.c:382-409).
    /// </summary>
    private static BlameHunk HunkFromEntry(BlameEntry e, BlameScoreboard sb)
    {
        BlameOrigin? suspect = e.Suspect;
        Debug.Assert(suspect is not null, "HunkFromEntry requires an entry with an assigned suspect.");
        Commit commit = suspect.Commit;

        GitSignature finalSig = sb.Mailmap is not null
            ? sb.Mailmap.ApplyAuthor(commit)
            : commit.Author;
        GitSignature finalComm = sb.Mailmap is not null
            ? sb.Mailmap.ApplyCommitter(commit)
            : commit.Committer;
        GitSignature origSig = finalSig;
        GitSignature origComm = finalComm;
        string summary = commit.Summary;

        return new BlameHunk(
            LinesInHunk: e.NumLines,
            FinalStartLineNumber: e.Lno + 1, // 0-based → 1-based
            FinalSignature: finalSig,
            FinalCommitter: finalComm,
            FinalCommitId: commit.Id,
            OrigStartLineNumber: e.SLno + 1, // 0-based → 1-based
            OrigCommitId: commit.Id,
            OrigSignature: origSig,
            OrigCommitter: origComm,
            OrigPath: suspect.Path,
            Summary: summary,
            Boundary: e.IsBoundary ? 1 : 0);
    }

    /// <summary>
    /// Converts a mutable <see cref="BlameScoreboardHunk"/> (from buffer blame)
    /// to a public <see cref="BlameHunk"/>. For hunks with a non-zero
    /// <see cref="BlameScoreboardHunk.FinalCommitId"/>, the commit is looked up
    /// to fill in signatures. For buffer-blame hunks (zero OID), empty
    /// signatures are used.
    /// </summary>
    private static async ValueTask<BlameHunk> ScoreboardHunkToPublicAsync(BlameScoreboardHunk h, BlameScoreboard sb, CancellationToken cancellationToken = default)
    {
        if (h.FinalCommitId.IsZero)
        {
            // Buffer-blame hunk: no commit, no signatures.
            var empty = new GitSignature(string.Empty, string.Empty, new GitTime(0, 0));
            return new BlameHunk(
                LinesInHunk: h.LinesInHunk,
                FinalStartLineNumber: h.FinalStartLineNumber,
                FinalSignature: empty,
                FinalCommitter: empty,
                FinalCommitId: h.FinalCommitId,
                OrigStartLineNumber: h.OrigStartLineNumber,
                OrigCommitId: h.OrigCommitId,
                OrigSignature: empty,
                OrigCommitter: empty,
                OrigPath: h.OrigPath ?? sb.Path,
                Summary: null,
                Boundary: h.Boundary);
        }

        Commit? commit = await sb.Repository.Objects.LookupAsync<Commit>(h.FinalCommitId, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            var empty = new GitSignature(string.Empty, string.Empty, new GitTime(0, 0));
            return new BlameHunk(
                LinesInHunk: h.LinesInHunk,
                FinalStartLineNumber: h.FinalStartLineNumber,
                FinalSignature: empty,
                FinalCommitter: empty,
                FinalCommitId: h.FinalCommitId,
                OrigStartLineNumber: h.OrigStartLineNumber,
                OrigCommitId: h.OrigCommitId,
                OrigSignature: empty,
                OrigCommitter: empty,
                OrigPath: h.OrigPath ?? sb.Path,
                Summary: null,
                Boundary: h.Boundary);
        }

        GitSignature finalSig = sb.Mailmap is not null
            ? sb.Mailmap.ApplyAuthor(commit)
            : commit.Author;
        GitSignature finalComm = sb.Mailmap is not null
            ? sb.Mailmap.ApplyCommitter(commit)
            : commit.Committer;

        return new BlameHunk(
            LinesInHunk: h.LinesInHunk,
            FinalStartLineNumber: h.FinalStartLineNumber,
            FinalSignature: finalSig,
            FinalCommitter: finalComm,
            FinalCommitId: h.FinalCommitId,
            OrigStartLineNumber: h.OrigStartLineNumber,
            OrigCommitId: h.OrigCommitId,
            OrigSignature: finalSig,
            OrigCommitter: finalComm,
            OrigPath: h.OrigPath ?? sb.Path,
            Summary: commit.Summary,
            Boundary: h.Boundary);
    }

    // ===== Buffer blame support =====

    /// <summary>
    /// Checks if a hunk is a buffer-blame hunk (zero final_commit_id).
    /// Matches <c>hunk_is_bufferblame</c> (blame.c:519-522).
    /// </summary>
    private static bool HunkIsBufferBlame(BlameScoreboardHunk? hunk)
    {
        return hunk is not null && hunk.FinalCommitId.IsZero;
    }

    /// <summary>
    /// Checks if a hunk ends at or before the given line. Matches
    /// <c>hunk_ends_at_or_before_line</c> (blame.c:51-54).
    /// </summary>
    private static bool HunkEndsAtOrBeforeLine(BlameScoreboardHunk hunk, int line)
    {
        return line >= hunk.FinalStartLineNumber + hunk.LinesInHunk - 1;
    }

    /// <summary>
    /// Checks if a hunk starts at or after the given line. Matches
    /// <c>hunk_starts_at_or_after_line</c> (blame.c:56-59).
    /// </summary>
    private static bool HunkStartsAtOrAfterLine(BlameScoreboardHunk hunk, int line)
    {
        return line <= hunk.FinalStartLineNumber;
    }

    /// <summary>
    /// Creates a new scoreboard hunk. Matches <c>new_hunk</c> (blame.c:61-79).
    /// </summary>
    private static BlameScoreboardHunk NewScoreboardHunk(int start, int lines, int origStart, GitPath? path)
    {
        return new BlameScoreboardHunk
        {
            LinesInHunk = lines,
            FinalStartLineNumber = start,
            OrigStartLineNumber = origStart,
            OrigPath = path,
            FinalCommitId = GitOid.Empty,
            OrigCommitId = GitOid.Empty,
        };
    }

    /// <summary>
    /// Shifts all hunks at or after <paramref name="startLine"/> by
    /// <paramref name="shiftBy"/>. Matches <c>shift_hunks_by</c>
    /// (blame.c:122-132).
    /// </summary>
    private static void ShiftHunksBy(BlameScoreboard sb, int startLine, int shiftBy)
    {
        foreach (BlameScoreboardHunk h in sb.Hunks)
        {
            if (h.FinalStartLineNumber < startLine)
            {
                continue;
            }

            h.FinalStartLineNumber += shiftBy;
        }
    }

    /// <summary>
    /// Splits a hunk in the vector. Matches <c>split_hunk_in_vector</c>
    /// (blame.c:289-324).
    /// </summary>
    private static BlameScoreboardHunk SplitHunkInVector(
        BlameScoreboard sb, BlameScoreboardHunk hunk, int relLine, bool returnNew)
    {
        // Don't split if already at a boundary.
        if (relLine <= 0 || relLine >= hunk.LinesInHunk)
        {
            return hunk;
        }

        int newLineCount = hunk.LinesInHunk - relLine;
        BlameScoreboardHunk nh = NewScoreboardHunk(
            hunk.FinalStartLineNumber + relLine,
            newLineCount,
            hunk.OrigStartLineNumber + relLine,
            hunk.OrigPath);
        nh.FinalCommitId = hunk.FinalCommitId;
        nh.OrigCommitId = hunk.OrigCommitId;

        // Adjust the original hunk.
        hunk.LinesInHunk -= newLineCount;

        // Insert sorted.
        int idx = sb.Hunks.FindIndex(x => x.FinalStartLineNumber > nh.FinalStartLineNumber);
        if (idx < 0)
        {
            idx = sb.Hunks.Count;
        }

        sb.Hunks.Insert(idx, nh);

        return returnNew ? nh : hunk;
    }

    /// <summary>
    /// Inserts a hunk into the vector keeping it sorted by
    /// <c>FinalStartLineNumber</c>. Matches <c>git_vector_insert_sorted</c>
    /// with <c>hunk_cmp</c> (blame.c:38-49, 576-579).
    /// </summary>
    private static void InsertHunkSorted(List<BlameScoreboardHunk> hunks, BlameScoreboardHunk hunk)
    {
        int lo = 0;
        int hi = hunks.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (hunks[mid].FinalStartLineNumber <= hunk.FinalStartLineNumber)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        hunks.Insert(lo, hunk);
    }

    /// <summary>
    /// Buffer hunk callback. Matches <c>buffer_hunk_cb</c> (blame.c:524-553).
    /// </summary>
    private static void BufferHunkCb(BlameScoreboard sb, int oldStart, int oldCount, int newStart)
    {
        int wedgeLine = newStart >= oldStart || oldCount == 0
            ? newStart
            : oldStart;

        sb.CurrentDiffLine = wedgeLine;
        sb.CurrentHunk = sb.Hunks.FirstOrDefault(h => h.FinalStartLineNumber <= wedgeLine && h.FinalStartLineNumber + h.LinesInHunk > wedgeLine);

        if (sb.CurrentHunk is null)
        {
            // Line added at the end of the file. C (blame.c:576-579): the
            // new hunk is inserted SORTED by final_start_line_number
            // (git_vector_insert_sorted with hunk_cmp) - not appended.
            sb.CurrentHunk = NewScoreboardHunk(wedgeLine, 0, wedgeLine, sb.Path);
            sb.CurrentDiffLine++;
            InsertHunkSorted(sb.Hunks, sb.CurrentHunk);
        }
        else if (!HunkStartsAtOrAfterLine(sb.CurrentHunk, wedgeLine))
        {
            // If this hunk doesn't start between existing hunks, split it.
            sb.CurrentHunk = SplitHunkInVector(
                sb, sb.CurrentHunk,
                wedgeLine - sb.CurrentHunk.FinalStartLineNumber, true);
        }
    }

    /// <summary>
    /// Buffer line callback. Matches <c>buffer_line_cb</c> (blame.c:556-601).
    /// </summary>
    private static void BufferLineCb(BlameScoreboard sb, DiffLineKind lineKind)
    {
        if (lineKind == DiffLineKind.Addition)
        {
            BlameScoreboardHunk? current = sb.CurrentHunk;
            if (HunkIsBufferBlame(current) &&
                current is not null && HunkEndsAtOrBeforeLine(current, sb.CurrentDiffLine))
            {
                // Append to the current buffer-blame hunk.
                BlameScoreboardHunk hunk = current;
                hunk.LinesInHunk++;
                ShiftHunksBy(sb, sb.CurrentDiffLine, 1);
            }
            else
            {
                // Create a new buffer-blame hunk with this line. C
                // (blame.c:576-579): inserted SORTED by
                // final_start_line_number (hunk_cmp), not appended.
                ShiftHunksBy(sb, sb.CurrentDiffLine, 1);
                sb.CurrentHunk = NewScoreboardHunk(sb.CurrentDiffLine, 1, 0, sb.Path);
                InsertHunkSorted(sb.Hunks, sb.CurrentHunk);
            }

            sb.CurrentDiffLine++;
        }

        if (lineKind == DiffLineKind.Deletion)
        {
            // Trim the line from the current hunk; remove it if it's now empty.
            BlameScoreboardHunk? hunk = sb.CurrentHunk;
            Debug.Assert(hunk is not null, "CurrentHunk is set in the deletion branch");
            int shiftBase = sb.CurrentDiffLine + hunk.LinesInHunk;

            if (--hunk.LinesInHunk == 0)
            {
                int idx = sb.Hunks.IndexOf(hunk);
                if (idx >= 0)
                {
                    sb.Hunks.RemoveAt(idx);
                    int iNext = Math.Min(idx, sb.Hunks.Count - 1);
                    sb.CurrentHunk = iNext >= 0 ? sb.Hunks[iNext] : null;
                }
            }

            ShiftHunksBy(sb, shiftBase, -1);
        }
    }

    /// <summary>
    /// Internal mutable hunk representation used during buffer blame.
    /// Corresponds to the C <c>git_blame_hunk</c> used in <c>git_blame_buffer</c>.
    /// </summary>
    internal sealed class BlameScoreboardHunk
    {
        public int LinesInHunk { get; set; }
        public int FinalStartLineNumber { get; set; }
        public GitOid FinalCommitId { get; set; }
        public GitOid OrigCommitId { get; set; }
        public int OrigStartLineNumber { get; set; }
        public GitPath? OrigPath { get; set; }
        public int Boundary { get; set; }
    }

    private sealed class BufferBlameState(BlameScoreboard sb)
    {
        public BlameScoreboard Sb = sb;
    }
}
