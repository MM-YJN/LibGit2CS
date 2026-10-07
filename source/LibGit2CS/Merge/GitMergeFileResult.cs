// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Merge;

/// <summary>
/// Result of a file-level 3-way merge. Matches <c>git_merge_file_result</c>
/// in <c>include/git2/merge.h</c>.
/// </summary>
public sealed record GitMergeFileResult
{
    /// <summary>
    /// <c>true</c> if the merge produced a clean result with no conflict
    /// markers. For binary files with no favor, this is <c>false</c> and
    /// <see cref="Content"/> is empty.
    /// </summary>
    public bool Automergeable { get; init; }

    /// <summary> The result file path (selected from the inputs via best-path rules), or <c>null</c> if no path could be determined. Byte-faithful. </summary>
    public GitPath? Path { get; init; }

    /// <summary>
    /// The result file mode (selected from the inputs via best-mode rules).
    /// </summary>
    public uint Mode { get; init; }

    /// <summary>
    /// The merged content (possibly containing conflict markers).
    /// </summary>
    public ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>
    /// The number of conflict regions in <see cref="Content"/>. Always
    /// <c>0</c> when <see cref="GitMergeFileOptions.Favor"/> is anything other
    /// than <see cref="GitMergeFileFavor.Normal"/>.
    /// </summary>
    public int ConflictCount { get; init; }

    /// <summary>
    /// <c>true</c> if the merge has conflicts (either not automergeable or
    /// the conflict count is non-zero).
    /// </summary>
    public bool HasConflicts => !Automergeable || ConflictCount > 0;
}
