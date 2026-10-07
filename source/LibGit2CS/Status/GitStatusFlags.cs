// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Status;

/// <summary>
/// Status flags for a single file. Matches <c>git_status_t</c>
/// (status.h:34-52). INDEX bits occupy 0-4, WT bits occupy 7-12, IGNORED
/// and CONFLICTED are 14-15. The gaps allow INDEX and WT bits to OR
/// together without collision.
/// </summary>
[Flags]
public enum GitStatusFlags : uint
{
    /// <summary>No changes. (<c>GIT_STATUS_CURRENT = 0</c>)</summary>
    Current = 0,

    /// <summary>File newly added in the index. (<c>GIT_STATUS_INDEX_NEW = 1 &lt;&lt; 0</c>)</summary>
    IndexNew = 1u << 0,

    /// <summary>Index entry differs from HEAD. (<c>GIT_STATUS_INDEX_MODIFIED = 1 &lt;&lt; 1</c>)</summary>
    IndexModified = 1u << 1,

    /// <summary>File deleted from index relative to HEAD. (<c>GIT_STATUS_INDEX_DELETED = 1 &lt;&lt; 2</c>)</summary>
    IndexDeleted = 1u << 2,

    /// <summary>File renamed in index. (<c>GIT_STATUS_INDEX_RENAMED = 1 &lt;&lt; 3</c>)</summary>
    IndexRenamed = 1u << 3,

    /// <summary>File type changed in index. (<c>GIT_STATUS_INDEX_TYPECHANGE = 1 &lt;&lt; 4</c>)</summary>
    IndexTypeChange = 1u << 4,

    /// <summary>File is new in workdir (untracked). (<c>GIT_STATUS_WT_NEW = 1 &lt;&lt; 7</c>)</summary>
    WorkdirNew = 1u << 7,

    /// <summary>Workdir file differs from index. (<c>GIT_STATUS_WT_MODIFIED = 1 &lt;&lt; 8</c>)</summary>
    WorkdirModified = 1u << 8,

    /// <summary>File deleted from workdir. (<c>GIT_STATUS_WT_DELETED = 1 &lt;&lt; 9</c>)</summary>
    WorkdirDeleted = 1u << 9,

    /// <summary>File type changed in workdir. (<c>GIT_STATUS_WT_TYPECHANGE = 1 &lt;&lt; 10</c>)</summary>
    WorkdirTypeChange = 1u << 10,

    /// <summary>File renamed in workdir. (<c>GIT_STATUS_WT_RENAMED = 1 &lt;&lt; 11</c>)</summary>
    WorkdirRenamed = 1u << 11,

    /// <summary>File is unreadable. (<c>GIT_STATUS_WT_UNREADABLE = 1 &lt;&lt; 12</c>)</summary>
    WorkdirUnreadable = 1u << 12,

    /// <summary>File is gitignored. (<c>GIT_STATUS_IGNORED = 1 &lt;&lt; 14</c>)</summary>
    Ignored = 1u << 14,

    /// <summary>File has merge conflicts. (<c>GIT_STATUS_CONFLICTED = 1 &lt;&lt; 15</c>)</summary>
    Conflicted = 1u << 15,

    // ━━ Status option flags (git_status_opt_t, status.h:100-207) ━━

    /// <summary>Include untracked files. (<c>GIT_STATUS_OPT_INCLUDE_UNTRACKED = 1 &lt;&lt; 0</c>)</summary>
    IncludeUntracked = 1u << 16,

    /// <summary>Include ignored files. (<c>GIT_STATUS_OPT_INCLUDE_IGNORED = 1 &lt;&lt; 1</c>)</summary>
    IncludeIgnored = 1u << 17,

    /// <summary>Include unmodified files. (<c>GIT_STATUS_OPT_INCLUDE_UNMODIFIED = 1 &lt;&lt; 2</c>)</summary>
    IncludeUnmodified = 1u << 18,

    /// <summary>Exclude submodules. (<c>GIT_STATUS_OPT_EXCLUDE_SUBMODULES = 1 &lt;&lt; 3</c>)</summary>
    ExcludeSubmodules = 1u << 19,

    /// <summary>Recurse into untracked directories. (<c>GIT_STATUS_OPT_RECURSE_UNTRACKED_DIRS = 1 &lt;&lt; 4</c>)</summary>
    RecurseUntrackedDirs = 1u << 20,

    /// <summary>Treat pathspec as literal paths. (<c>GIT_STATUS_OPT_DISABLE_PATHSPEC_MATCH = 1 &lt;&lt; 5</c>)</summary>
    DisablePathspecMatch = 1u << 21,

    /// <summary>Recurse into ignored directories. (<c>GIT_STATUS_OPT_RECURSE_IGNORED_DIRS = 1 &lt;&lt; 6</c>)</summary>
    RecurseIgnoredDirs = 1u << 22,

    /// <summary>Detect renames in HEAD→index. (<c>GIT_STATUS_OPT_RENAMES_HEAD_TO_INDEX = 1 &lt;&lt; 7</c>)</summary>
    RenamesHeadToIndex = 1u << 23,

    /// <summary>Detect renames in index→workdir. (<c>GIT_STATUS_OPT_RENAMES_INDEX_TO_WORKDIR = 1 &lt;&lt; 8</c>)</summary>
    RenamesIndexToWorkdir = 1u << 24,

    /// <summary>Sort case-sensitively. (<c>GIT_STATUS_OPT_SORT_CASE_SENSITIVELY = 1 &lt;&lt; 9</c>)</summary>
    SortCaseSensitively = 1u << 25,

    /// <summary>Sort case-insensitively. (<c>GIT_STATUS_OPT_SORT_CASE_INSENSITIVELY = 1 &lt;&lt; 10</c>)</summary>
    SortCaseInsenzively = 1u << 26,

    /// <summary>Allow renames from rewrites. (<c>GIT_STATUS_OPT_RENAMES_FROM_REWRITES = 1 &lt;&lt; 11</c>)</summary>
    RenamesFromRewrites = 1u << 27,

    /// <summary>Don't refresh the index from disk. (<c>GIT_STATUS_OPT_NO_REFRESH = 1 &lt;&lt; 12</c>)</summary>
    NoRefresh = 1u << 28,

    /// <summary>Refresh index stat cache and write it back. (<c>GIT_STATUS_OPT_UPDATE_INDEX = 1 &lt;&lt; 13</c>)</summary>
    UpdateIndex = 1u << 29,

    /// <summary>Include unreadable files. (<c>GIT_STATUS_OPT_INCLUDE_UNREADABLE = 1 &lt;&lt; 14</c>)</summary>
    IncludeUnreadable = 1u << 30,

    /// <summary>Treat unreadable files as untracked. (<c>GIT_STATUS_OPT_INCLUDE_UNREADABLE_AS_UNTRACKED = 1 &lt;&lt; 15</c>)</summary>
    IncludeUnreadableAsUntracked = 1u << 31,
}
