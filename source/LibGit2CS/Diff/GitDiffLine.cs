// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// A single line of a diff hunk. Managed equivalent of
/// <c>git_diff_line</c> in <c>include/git2/diff.h:651-665</c>.
/// </summary>
/// <param name="Origin">Line origin (context / addition / deletion / eofnl variants / header / binary).</param>
/// <param name="OldLine">1-based line number in the old file, or -1 when not applicable (additions).</param>
/// <param name="NewLine">1-based line number in the new file, or -1 when not applicable (deletions).</param>
/// <param name="LineCount">Number of lines this entry represents (newline counting for multi-line content).</param>
/// <param name="Content">Raw line bytes (without the leading origin sigil). Borrows the source buffer.</param>
public readonly record struct GitDiffLine(
    GitDiffLineOrigin Origin,
    int OldLine,
    int NewLine,
    int LineCount,
    ReadOnlyMemory<byte> Content);
