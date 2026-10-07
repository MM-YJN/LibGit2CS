// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

namespace LibGit2CS.Diff;

/// <summary>
/// Flags controlling rename/copy detection. Maps 1:1 to
/// <c>git_diff_find_t</c> in <c>include/git2/diff.h:684-751</c>.
/// </summary>
[Flags]
[SuppressMessage("Design", "CA1069:Enums values should not be duplicated", Justification = "duplicate zero values mirror C (BY_CONFIG == IGNORE_LEADING_WHITESPACE == 0).")]
[SuppressMessage("Usage", "CA2217:Do not mark enums with FlagsAttribute", Justification = "Flags is correct — these are combinable bitfield flags despite composite values.")]
public enum GitDiffFindFlags
{
    /// <summary>Look at config (<c>diff.renames</c>) to decide. (<c>GIT_DIFF_FIND_BY_CONFIG</c>)</summary>
    ByConfig = 0,

    /// <summary>Look for renames. (<c>GIT_DIFF_FIND_RENAMES</c>; <c>1u &lt;&lt; 0</c>)</summary>
    Renames = 1 << 0,

    /// <summary>Look for renames from rewrites. (<c>GIT_DIFF_FIND_RENAMES_FROM_REWRITES</c>; <c>1u &lt;&lt; 1</c>)</summary>
    RenamesFromRewrites = 1 << 1,

    /// <summary>Look for copies. (<c>GIT_DIFF_FIND_COPIES</c>; <c>1u &lt;&lt; 2</c>)</summary>
    Copies = 1 << 2,

    /// <summary>Consider unmodified files as copy sources. (<c>GIT_DIFF_FIND_COPIES_FROM_UNMODIFIED</c>; <c>1u &lt;&lt; 3</c>)</summary>
    CopiesFromUnmodified = 1 << 3,

    /// <summary>Mark rewrites (large content changes) as Modified. (<c>GIT_DIFF_FIND_REWRITES</c>; <c>1u &lt;&lt; 4</c>)</summary>
    Rewrites = 1 << 4,

    /// <summary>Break rewrites into delete/add pairs. (<c>GIT_DIFF_BREAK_REWRITES</c>; <c>1u &lt;&lt; 5</c>)</summary>
    BreakRewrites = 1 << 5,

    /// <summary>Find renames/copies for untracked files. (<c>GIT_DIFF_FIND_FOR_UNTRACKED</c>; <c>1u &lt;&lt; 6</c>)</summary>
    ForUntracked = 1 << 6,

    /// <summary>All find flags. (<c>GIT_DIFF_FIND_ALL</c>; <c>0x0ff</c>)</summary>
    All = 0x0ff,

    /// <summary>Ignore leading whitespace in similarity. (<c>GIT_DIFF_FIND_IGNORE_LEADING_WHITESPACE</c>)</summary>
    IgnoreLeadingWhitespace = 0,

    /// <summary>Ignore all whitespace in similarity. (<c>GIT_DIFF_FIND_IGNORE_WHITESPACE</c>; <c>1u &lt;&lt; 12</c>)</summary>
    IgnoreWhitespace = 1 << 12,

    /// <summary>Do not ignore whitespace in similarity. (<c>GIT_DIFF_FIND_DONT_IGNORE_WHITESPACE</c>; <c>1u &lt;&lt; 13</c>)</summary>
    DontIgnoreWhitespace = 1 << 13,

    /// <summary>Use exact OID match only (no content similarity). (<c>GIT_DIFF_FIND_EXACT_MATCH_ONLY</c>; <c>1u &lt;&lt; 14</c>)</summary>
    ExactMatchOnly = 1 << 14,

    /// <summary>Only break rewrites that yield renames. (<c>GIT_DIFF_BREAK_REWRITES_FOR_RENAMES_ONLY</c>; <c>1u &lt;&lt; 15</c>)</summary>
    BreakRewritesForRenamesOnly = 1 << 15,

    /// <summary>Remove unmodified deltas after find. (<c>GIT_DIFF_FIND_REMOVE_UNMODIFIED</c>; <c>1u &lt;&lt; 16</c>)</summary>
    RemoveUnmodified = 1 << 16,
}
