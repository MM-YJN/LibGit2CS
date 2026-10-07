// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary> Options controlling rename/copy/break-rewrite detection. Managed equivalent of <c>git_diff_find_options</c> in <c>include/git2/diff.h:774-828</c>.
/// </summary> <remarks> The C <c>version</c> field is dropped (managed ABI). Defaults match <c>GIT_DIFF_FIND_OPTIONS_INIT</c>: flags=BY_CONFIG (0 — the
/// effective flags are resolved from <c>diff.renames</c> at use time), rename_threshold=50, rename_from_rewrite_threshold=50, copy_threshold=50,
/// break_rewrite_threshold=60, rename_limit=1000. </remarks>
public sealed record GitDiffFindOptions
{
    /// <summary>Combination of <see cref="GitDiffFindFlags"/>. Default <see cref="GitDiffFindFlags.ByConfig"/>.</summary>
    public GitDiffFindFlags Flags { get; init; } = GitDiffFindFlags.ByConfig;

    /// <summary>Similarity above which files are considered renames (-M). Default 50.</summary>
    public int RenameThreshold { get; init; } = 50;

    /// <summary>Similarity below which a rewrite is a rename source. Default 50.</summary>
    public int RenameFromRewriteThreshold { get; init; } = 50;

    /// <summary>Similarity above which files are considered copies (-C). Default 50.</summary>
    public int CopyThreshold { get; init; } = 50;

    /// <summary>Similarity below which a modified file splits into delete/add (-B). Default 60.</summary>
    public int BreakRewriteThreshold { get; init; } = 60;

    /// <summary>Maximum candidate matches examined per file (-l). Default 1000.</summary>
    public int RenameLimit { get; init; } = 1000;

    /// <summary>Custom similarity metric, or null for the default hashsig engine.</summary>
    public GitDiffSimilarityMetric? Metric { get; init; }

    /// <summary>Default options matching <c>GIT_DIFF_FIND_OPTIONS_INIT</c>.</summary>
    public static GitDiffFindOptions Default { get; } = new();
}
