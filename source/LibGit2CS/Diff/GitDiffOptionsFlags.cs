// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Flags controlling diff generation. Maps 1:1 to <c>git_diff_option_t</c>
/// in <c>include/git2/diff.h:28-173</c>. Bit values are exact.
/// </summary>
[Flags]
public enum GitDiffOptionsFlags
{
    /// <summary>Normal diff behavior. (<c>GIT_DIFF_NORMAL</c>)</summary>
    Normal = 0,

    /// <summary>Reverse the sides of the diff. (<c>GIT_DIFF_REVERSE</c>; <c>1u &lt;&lt; 0</c>)</summary>
    Reverse = 1 << 0,

    /// <summary>Include ignored files in the diff. (<c>GIT_DIFF_INCLUDE_IGNORED</c>; <c>1u &lt;&lt; 1</c>)</summary>
    IncludeIgnored = 1 << 1,

    /// <summary>Recursively descend into ignored directories. (<c>GIT_DIFF_RECURSE_IGNORED_DIRS</c>; <c>1u &lt;&lt; 2</c>)</summary>
    RecurseIgnoredDirs = 1 << 2,

    /// <summary>Include untracked files in the diff. (<c>GIT_DIFF_INCLUDE_UNTRACKED</c>; <c>1u &lt;&lt; 3</c>)</summary>
    IncludeUntracked = 1 << 3,

    /// <summary>Recursively descend into untracked directories. (<c>GIT_DIFF_RECURSE_UNTRACKED_DIRS</c>; <c>1u &lt;&lt; 4</c>)</summary>
    RecurseUntrackedDirs = 1 << 4,

    /// <summary>Include unmodified files in the diff. (<c>GIT_DIFF_INCLUDE_UNMODIFIED</c>; <c>1u &lt;&lt; 5</c>)</summary>
    IncludeUnmodified = 1 << 5,

    /// <summary>Treat a typechange (file&lt;-&gt;symlink) as a single delta. (<c>GIT_DIFF_INCLUDE_TYPECHANGE</c>; <c>1u &lt;&lt; 6</c>)</summary>
    IncludeTypechange = 1 << 6,

    /// <summary>Also include typechange to/from a tree. (<c>GIT_DIFF_INCLUDE_TYPECHANGE_TREES</c>; <c>1u &lt;&lt; 7</c>)</summary>
    IncludeTypechangeTrees = 1 << 7,

    /// <summary>Ignore file mode changes. (<c>GIT_DIFF_IGNORE_FILEMODE</c>; <c>1u &lt;&lt; 8</c>)</summary>
    IgnoreFilemode = 1 << 8,

    /// <summary>Treat all submodules as unmodified. (<c>GIT_DIFF_IGNORE_SUBMODULES</c>; <c>1u &lt;&lt; 9</c>)</summary>
    IgnoreSubmodules = 1 << 9,

    /// <summary>Use case-insensitive filename matching. (<c>GIT_DIFF_IGNORE_CASE</c>; <c>1u &lt;&lt; 10</c>)</summary>
    IgnoreCase = 1 << 10,

    /// <summary>Include case-only changes (requires <see cref="IgnoreCase"/>). (<c>GIT_DIFF_INCLUDE_CASECHANGE</c>; <c>1u &lt;&lt; 11</c>)</summary>
    IncludeCasechange = 1 << 11,

    /// <summary>Pathspec matches paths literally (no glob). (<c>GIT_DIFF_DISABLE_PATHSPEC_MATCH</c>; <c>1u &lt;&lt; 12</c>)</summary>
    DisablePathspecMatch = 1 << 12,

    /// <summary>Skip the binary-check / content load entirely. (<c>GIT_DIFF_SKIP_BINARY_CHECK</c>; <c>1u &lt;&lt; 13</c>)</summary>
    SkipBinaryCheck = 1 << 13,

    /// <summary>Use a fast single-readdir pass for untracked dirs. (<c>GIT_DIFF_ENABLE_FAST_UNTRACKED_DIRS</c>; <c>1u &lt;&lt; 14</c>)</summary>
    EnableFastUntrackedDirs = 1 << 14,

    /// <summary>Refresh stat info in the index. (<c>GIT_DIFF_UPDATE_INDEX</c>; <c>1u &lt;&lt; 15</c>)</summary>
    UpdateIndex = 1 << 15,

    /// <summary>Include unreadable files. (<c>GIT_DIFF_INCLUDE_UNREADABLE</c>; <c>1u &lt;&lt; 16</c>)</summary>
    IncludeUnreadable = 1 << 16,

    /// <summary>Include unreadable files as untracked. (<c>GIT_DIFF_INCLUDE_UNREADABLE_AS_UNTRACKED</c>; <c>1u &lt;&lt; 17</c>)</summary>
    IncludeUnreadableAsUntracked = 1 << 17,

    /// <summary>Enable the indent heuristic. (<c>GIT_DIFF_INDENT_HEURISTIC</c>; <c>1u &lt;&lt; 18</c>)</summary>
    IndentHeuristic = 1 << 18,

    /// <summary>Ignore blank lines. (<c>GIT_DIFF_IGNORE_BLANK_LINES</c>; <c>1u &lt;&lt; 19</c>)</summary>
    IgnoreBlankLines = 1 << 19,

    /// <summary>Treat all files as text. (<c>GIT_DIFF_FORCE_TEXT</c>; <c>1u &lt;&lt; 20</c>)</summary>
    ForceText = 1 << 20,

    /// <summary>Treat all files as binary. (<c>GIT_DIFF_FORCE_BINARY</c>; <c>1u &lt;&lt; 21</c>)</summary>
    ForceBinary = 1 << 21,

    /// <summary>Ignore all whitespace. (<c>GIT_DIFF_IGNORE_WHITESPACE</c>; <c>1u &lt;&lt; 22</c>)</summary>
    IgnoreWhitespace = 1 << 22,

    /// <summary>Ignore whitespace changes. (<c>GIT_DIFF_IGNORE_WHITESPACE_CHANGE</c>; <c>1u &lt;&lt; 23</c>)</summary>
    IgnoreWhitespaceChange = 1 << 23,

    /// <summary>Ignore whitespace at end of line. (<c>GIT_DIFF_IGNORE_WHITESPACE_EOL</c>; <c>1u &lt;&lt; 24</c>)</summary>
    IgnoreWhitespaceEol = 1 << 24,

    /// <summary>Show content of untracked files. (<c>GIT_DIFF_SHOW_UNTRACKED_CONTENT</c>; <c>1u &lt;&lt; 25</c>)</summary>
    ShowUntrackedContent = 1 << 25,

    /// <summary>Include unmodified files (alias). (<c>GIT_DIFF_SHOW_UNMODIFIED</c>; <c>1u &lt;&lt; 26</c>)</summary>
    ShowUnmodified = 1 << 26,

    /// <summary>Use the patience diff algorithm. (<c>GIT_DIFF_PATIENCE</c>; <c>1u &lt;&lt; 28</c>)</summary>
    Patience = 1 << 28,

    /// <summary>Use the minimal diff algorithm. (<c>GIT_DIFF_MINIMAL</c>; <c>1u &lt;&lt; 29</c>)</summary>
    Minimal = 1 << 29,

    /// <summary>Show binary content (base85-encoded) instead of "Binary files differ". (<c>GIT_DIFF_SHOW_BINARY</c>; <c>1u &lt;&lt; 30</c>)</summary>
    ShowBinary = 1 << 30,
}
