// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

using LibGit2CS.IO;

namespace LibGit2CS.Diff;

/// <summary>
/// One entry in a diff: the old and new file plus a status. Managed equivalent
/// of <c>git_diff_delta</c> in <c>include/git2/diff.h:294-306</c>.
/// </summary>
/// <remarks>
/// Mutable <c>sealed class</c> (not a record): <c>diff_tform.c</c>'s
/// <c>git_diff_find_similar</c> mutates deltas heavily in place — it sets
/// <see cref="Status"/> (Modified→Renamed), swaps <see cref="OldFile"/>, sets
/// <see cref="Similarity"/>, and marks deltas for deletion/splitting. The
/// public surface is read-only; mutation is <c>internal</c> only. Mirrors the
/// C struct, mutated in place throughout the diff pipeline.
/// </remarks>
[SuppressMessage("Performance", "IDE0290:Use primary constructor", Justification = "Not using a primary constructor — the class is mutable (internal setters for rename detection); a primary constructor would not fit.")]
public sealed class GitDiffDelta
{
    /// <summary>The kind of change this delta represents.</summary>
    public GitDeltaStatus Status { get; internal set; }

    /// <summary>Per-file flags (binary / valid-id / exists / valid-size).</summary>
    public GitDiffFileFlags Flags { get; internal set; }

    /// <summary>For renames/copies: similarity score 0–100; otherwise 0.</summary>
    public int Similarity { get; internal set; }

    /// <summary>Number of files in this delta (1 for add/delete, 2 for modify/rename).</summary>
    public int FileCount { get; internal set; }

    /// <summary>The old (left) side of the delta.</summary>
    public GitDiffFile OldFile { get; internal set; }

    /// <summary>The new (right) side of the delta.</summary>
    public GitDiffFile NewFile { get; internal set; }

    /// <summary>Creates a delta with the given sides and status.</summary>
    public GitDiffDelta(GitDeltaStatus status, int fileCount, GitDiffFile oldFile, GitDiffFile newFile)
    {
        Status = status;
        FileCount = fileCount;
        OldFile = oldFile;
        NewFile = newFile;
    }

    /// <summary> The canonical path of this delta. Matches <c>diff_delta__path</c> in <c>diff.c:29-40</c>: defaults to the old path, but uses the new path when
    /// the old side is absent (<c>null</c>) or the status is <see cref="GitDeltaStatus.Added"/>/<see cref="GitDeltaStatus.Renamed"/>/ <see
    /// cref="GitDeltaStatus.Copied"/>. Byte-faithful primary; use <see cref="PathString"/> for the <c>string</c> display convenience. </summary>
    public GitPath Path => DeltaPath ?? default;

    /// <summary>
    /// <c>string</c> convenience for <see cref="Path"/>. Decodes via
    /// <see cref="GitPath.ToUtf8String"/> (lossy for non-UTF-8 paths — use
    /// <see cref="Path"/> for byte-faithful comparison).
    /// </summary>
    public string PathString => Path.ToUtf8String();

    /// <summary>
    /// Byte-faithful canonical path. Ports <c>diff_delta__path</c>
    /// (<c>diff.c:29-40</c>) 1:1. Used by the delta sort comparators and any
    /// internal byte-wise comparison. Returns <c>null</c> only when both sides
    /// are absent (never the case for a real delta).
    /// </summary>
    internal GitPath? DeltaPath
    {
        get
        {
            GitPath? str = OldFile.Path;
            if (!str.HasValue ||
                Status is GitDeltaStatus.Added or GitDeltaStatus.Renamed or GitDeltaStatus.Copied)
            {
                str = NewFile.Path;
            }

            return str;
        }
    }

    /// <summary>
    /// The index-to-workdir path: the old path if present, otherwise the new
    /// path. Ports <c>diff_delta__i2w_path</c> (<c>diff_generate.c:336-340</c>).
    /// Used by <c>git_diff__paired_foreach</c> to walk the i2w list by the
    /// index (old) name.
    /// </summary>
    internal GitPath? I2WPath => OldFile.Path ?? NewFile.Path;

    /// <summary>
    /// The status character used in <c>--name-status</c> / raw output.
    /// Matches <c>git_diff_status_char</c> in <c>diff.h</c>.
    /// </summary>
    public char StatusChar => Status switch
    {
        GitDeltaStatus.Added => 'A',
        GitDeltaStatus.Deleted => 'D',
        GitDeltaStatus.Modified => 'M',
        GitDeltaStatus.Renamed => 'R',
        GitDeltaStatus.Copied => 'C',
        GitDeltaStatus.Ignored => 'I',
        GitDeltaStatus.Untracked => '?',
        GitDeltaStatus.Typechange => 'T',
        GitDeltaStatus.Unreadable => 'U',
        GitDeltaStatus.Conflicted => 'U',
        _ => ' ',
    };

    // ━━ Delta sort comparators (git_diff_delta__cmp / _casecmp, diff.c:42-53) ━━

    /// <summary>
    /// Case-sensitive delta sort: byte compare on <see cref="DeltaPath"/>, then
    /// status tiebreak. Ports <c>git_diff_delta__cmp</c> (<c>diff.c:42-47</c>).
    /// </summary>
    internal static int DeltaCompare(GitDiffDelta a, GitDiffDelta b)
    {
        int val = GitPath.Compare(a.DeltaPath ?? default, b.DeltaPath ?? default);
        return val != 0 ? val : (int)a.Status - (int)b.Status;
    }

    /// <summary>
    /// Case-insensitive delta sort: ASCII-fold compare on <see cref="DeltaPath"/>,
    /// then status tiebreak. Ports <c>git_diff_delta__casecmp</c>
    /// (<c>diff.c:49-54</c>).
    /// </summary>
    internal static int DeltaCompareIgnoreCase(GitDiffDelta a, GitDiffDelta b)
    {
        int val = GitPath.CompareIgnoreCase(a.DeltaPath ?? default, b.DeltaPath ?? default);
        return val != 0 ? val : (int)a.Status - (int)b.Status;
    }
}
