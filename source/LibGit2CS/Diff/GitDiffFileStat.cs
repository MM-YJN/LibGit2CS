// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Diff;

/// <summary> Per-file insertion/deletion counts for one delta. </summary>
/// <param name="Path">Display path (new file path, or old if new is absent). Byte-faithful.</param>
/// <param name="Insertions">Lines added (0 for binary — C's git_patch_line_stats counts 0/0, diff_stats.c:231).</param>
/// <param name="Deletions">Lines removed (0 for binary).</param>
/// <param name="OldPath">Old file path for renames, or null if same as <paramref name="Path"/>.</param>
/// <param name="OldSize">Old file size in bytes (for binary <c>Bin</c> display).</param>
/// <param name="NewSize">New file size in bytes (for binary <c>Bin</c> display).</param>
/// <param name="OldMode">Old file mode (for <c>--summary</c> mode changes).</param>
/// <param name="NewMode">New file mode (for <c>--summary</c> mode changes).</param>
/// <param name="IsBinary">The delta's binary flag (GIT_DIFF_FLAG_BINARY) — drives the "- -" numstat and "Bin" full-stat rendering like C's diff_file_stats_number_to_buf (diff_stats.c:146-147).</param>
public readonly record struct GitDiffFileStat(
    GitPath Path,
    int Insertions,
    int Deletions,
    GitPath? OldPath = null,
    long OldSize = 0,
    long NewSize = 0,
    uint OldMode = 0,
    uint NewMode = 0,
    bool IsBinary = false)
{
    /// <summary>True when this delta is a rename (old path differs from new).</summary>
    public bool IsRename => OldPath is not null && OldPath != Path;
}
