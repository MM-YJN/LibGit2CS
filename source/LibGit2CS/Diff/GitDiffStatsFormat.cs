// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Statistics format flags. Maps 1:1 to <c>git_diff_stats_format_t</c> in
/// <c>include/git2/diff.h:1376-1389</c>.
/// </summary>
[Flags]
public enum GitDiffStatsFormat
{
    /// <summary>No formatting. (<c>GIT_DIFF_STATS_NONE</c>)</summary>
    None = 0,

    /// <summary>Full histogram stat (<c>--stat</c>). (<c>GIT_DIFF_STATS_FULL</c>; <c>1u &lt;&lt; 0</c>)</summary>
    Full = 1 << 0,

    /// <summary>One-line summary (<c>--shortstat</c>). (<c>GIT_DIFF_STATS_SHORT</c>; <c>1u &lt;&lt; 1</c>)</summary>
    Short = 1 << 1,

    /// <summary>Number stat (<c>--numstat</c>). (<c>GIT_DIFF_STATS_NUMBER</c>; <c>1u &lt;&lt; 2</c>)</summary>
    Number = 1 << 2,

    /// <summary>Extended summary (<c>--summary</c>). (<c>GIT_DIFF_STATS_INCLUDE_SUMMARY</c>; <c>1u &lt;&lt; 3</c>)</summary>
    IncludeSummary = 1 << 3,
}
