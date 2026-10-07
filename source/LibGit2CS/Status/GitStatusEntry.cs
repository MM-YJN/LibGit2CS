// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Status;

/// <summary>
/// A single file's combined status. Managed port of
/// <c>git_status_entry</c> (status.h:298-302). Exposes the underlying
/// HEAD→index and index→workdir deltas (needed for porcelain <c>-v</c>
/// output and rename old/new path access).
/// </summary>
public sealed class GitStatusEntry
{
    /// <summary>The combined status flags (INDEX bits | WT bits | IGNORED | CONFLICTED).</summary>
    public GitStatusFlags Status { get; }

    /// <summary>
    /// The HEAD→index delta, or null when <c>show</c> is
    /// <see cref="GitStatusShow.WorkdirOnly"/> or the file has no staged changes.
    /// </summary>
    public Diff.GitDiffDelta? HeadToIndex { get; }

    /// <summary>
    /// The index→workdir delta, or null when <c>show</c> is
    /// <see cref="GitStatusShow.IndexOnly"/> or the file has no workdir changes.
    /// </summary>
    public Diff.GitDiffDelta? IndexToWorkdir { get; }

    /// <summary>
    /// The path of the file (byte-faithful <see cref="GitPath"/>). Matches the C
    /// <c>head_to_index ? head_to_index->old_file.path : index_to_workdir->old_file.path</c>
    /// used by <c>git_status_foreach</c> — libgit2 returns a raw
    /// <c>const char *</c> (no decode). Use <see cref="GitPath.ToUtf8String"/> for
    /// display/API egress.
    /// </summary>
    public GitPath Path
    {
        get
        {
            if (HeadToIndex is { } h2i)
            {
                return h2i.OldFile.Path ?? default;
            }

            if (IndexToWorkdir is { } i2w)
            {
                return i2w.OldFile.Path ?? default;
            }

            return default;
        }
    }

    /// <summary> The new path (for renames, byte-faithful), or empty if not renamed. </summary> <remarks> Returns <c>default(GitPath)</c> (an empty <see
    /// cref="GitPath"/>, i.e. <see cref="GitPath.IsEmpty"/> == true) when the entry is not renamed — <see cref="GitPath"/> is a non-nullable struct. Callers
    /// that need to distinguish "not renamed" should check <see cref="Diff.GitDiffDelta.Status"/> == <see cref="Diff.GitDeltaStatus.Renamed"/> on the relevant
    /// delta. Earlier APIs returned <c>string?</c> (null = not renamed); the implicit <c>GitPath -> string</c> bridge preserves source compatibility for
    /// <c>string</c> consumers (empty maps to <c>string.Empty</c>). </remarks>
    public GitPath NewPath
    {
        get
        {
            if (HeadToIndex is { } h2i && h2i.Status == Diff.GitDeltaStatus.Renamed)
            {
                return h2i.NewFile.Path ?? default;
            }

            if (IndexToWorkdir is { } i2w && i2w.Status == Diff.GitDeltaStatus.Renamed)
            {
                return i2w.NewFile.Path ?? default;
            }

            return default;
        }
    }

    internal GitStatusEntry(
        GitStatusFlags status,
        Diff.GitDiffDelta? headToIndex,
        Diff.GitDiffDelta? indexToWorkdir)
    {
        Status = status;
        HeadToIndex = headToIndex;
        IndexToWorkdir = indexToWorkdir;
    }
}
