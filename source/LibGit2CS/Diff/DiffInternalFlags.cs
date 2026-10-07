// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Internal per-file bookkeeping bits used by <c>diff_file.c</c>,
/// <c>diff_generate.c</c>, and <c>diff_tform.c</c>. These map to the
/// <c>GIT_DIFF_FLAG__*</c> macros in <c>src/libgit2/diff_generate.h:27-42</c>
/// and are kept separate from the public <see cref="GitDiffFileFlags"/>. They are
/// <c>internal</c> because they are mutated during rename detection
/// (<see cref="DiffTransform"/>) and lazy content loading
/// (<see cref="DiffFileContent"/>).
/// </summary>
[Flags]
internal enum DiffInternalFlags
{
    None = 0,

    /// <summary>Path string was heap-allocated (must free). (<c>GIT_DIFF_FLAG__FREE_PATH</c>; bit 7)</summary>
    FreePath = 1 << 7,

    /// <summary>File data was heap-allocated. (<c>GIT_DIFF_FLAG__FREE_DATA</c>; bit 8)</summary>
    FreeData = 1 << 8,

    /// <summary>File data was mmap'ed. (<c>GIT_DIFF_FLAG__UNMAP_DATA</c>; bit 9)</summary>
    UnmapData = 1 << 9,

    /// <summary>File data should NOT be loaded. (<c>GIT_DIFF_FLAG__NO_DATA</c>; bit 10)</summary>
    NoData = 1 << 10,

    /// <summary>Release the blob when finished. (<c>GIT_DIFF_FLAG__FREE_BLOB</c>; bit 11)</summary>
    FreeBlob = 1 << 11,

    /// <summary>File data has been loaded. (<c>GIT_DIFF_FLAG__LOADED</c>; bit 12)</summary>
    Loaded = 1 << 12,

    /// <summary>Delete this delta during rename finalization. (<c>GIT_DIFF_FLAG__TO_DELETE</c>; bit 16)</summary>
    ToDelete = 1 << 16,

    /// <summary>Split this delta during rename finalization. (<c>GIT_DIFF_FLAG__TO_SPLIT</c>; bit 17)</summary>
    ToSplit = 1 << 17,

    /// <summary>Marked as a rename target. (<c>GIT_DIFF_FLAG__IS_RENAME_TARGET</c>; bit 18)</summary>
    IsRenameTarget = 1 << 18,

    /// <summary>Marked as a rename source. (<c>GIT_DIFF_FLAG__IS_RENAME_SOURCE</c>; bit 19)</summary>
    IsRenameSource = 1 << 19,

    /// <summary>Has a self-similarity score (for break-rewrite). (<c>GIT_DIFF_FLAG__HAS_SELF_SIMILARITY</c>; bit 20)</summary>
    HasSelfSimilarity = 1 << 20,

    /// <summary>All internal bookkeeping flags (for clearing). Matches <c>GIT_DIFF_FLAG__CLEAR_INTERNAL</c>.</summary>
    AllInternal = ToDelete | ToSplit | IsRenameTarget | IsRenameSource | HasSelfSimilarity,
}
