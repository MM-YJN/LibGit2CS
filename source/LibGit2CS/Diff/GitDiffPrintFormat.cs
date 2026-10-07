// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Output format for <see cref="GitDiff.PrintAsync"/> / <see cref="LibGit2CS.Diff.GitDiff.ToBufferAsync(System.Buffers.IBufferWriter{byte}, LibGit2CS.Diff.GitDiffPrintFormat, System.Threading.CancellationToken)"/>.
/// Maps to <c>git_diff_format_t</c> in <c>include/git2/diff.h:1156-1162</c>,
/// with <see cref="Stat"/>/<see cref="Summary"/> folded in as a C# convenience
/// (they route internally to <see cref="GitDiffStats"/>, mirroring how
/// <c>git diff --stat</c>/<c>--summary</c> are emitted via
/// <c>git_diff_stats_to_buf</c> rather than <c>git_diff_print</c>).
/// </summary>
/// <remarks>
/// <c>GIT_DIFF_FORMAT_PATCH_ID</c> (used by <c>git_diff_patchid</c>) is
/// intentionally absent: patch-id is a separate hash-computing API
/// (<see cref="GitPatchId"/>), not a print format.
/// </remarks>
public enum GitDiffPrintFormat
{
    /// <summary>Full unified diff. (<c>GIT_DIFF_FORMAT_PATCH</c>)</summary>
    Patch = 1,

    /// <summary>Just the file headers of a patch. (<c>GIT_DIFF_FORMAT_PATCH_HEADER</c>)</summary>
    PatchHeader = 2,

    /// <summary>Raw format (<c>:old new oid oid M\tpath</c>). (<c>GIT_DIFF_FORMAT_RAW</c>)</summary>
    Raw = 3,

    /// <summary>Just file paths. (<c>GIT_DIFF_FORMAT_NAME_ONLY</c>)</summary>
    NameOnly = 4,

    /// <summary>Status + path (<c>M\tpath</c>). (<c>GIT_DIFF_FORMAT_NAME_STATUS</c>)</summary>
    NameStatus = 5,

    /// <summary>Histogram stat (<c>--stat</c>); routes to <see cref="GitDiffStats"/>.</summary>
    Stat = 6,

    /// <summary>Summary (<c>--summary</c>); routes to <see cref="GitDiffStats"/>.</summary>
    Summary = 7,
}
