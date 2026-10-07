// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Delta status (what changed for one file). Maps 1:1 to
/// <c>git_delta_t</c> in <c>include/git2/diff.h:221-235</c>.
/// </summary>
public enum GitDeltaStatus
{
    /// <summary>No changes. (<c>GIT_DELTA_UNMODIFIED</c>)</summary>
    Unmodified = 0,

    /// <summary>Entry does not exist in old version. (<c>GIT_DELTA_ADDED</c>)</summary>
    Added = 1,

    /// <summary>Entry does not exist in new version. (<c>GIT_DELTA_DELETED</c>)</summary>
    Deleted = 2,

    /// <summary>Entry content changed between old and new. (<c>GIT_DELTA_MODIFIED</c>)</summary>
    Modified = 3,

    /// <summary>Entry was renamed between old and new. (<c>GIT_DELTA_RENAMED</c>)</summary>
    Renamed = 4,

    /// <summary>Entry was copied from another old entry. (<c>GIT_DELTA_COPIED</c>)</summary>
    Copied = 5,

    /// <summary>Entry is ignored item in workdir. (<c>GIT_DELTA_IGNORED</c>)</summary>
    Ignored = 6,

    /// <summary>Entry is untracked item in workdir. (<c>GIT_DELTA_UNTRACKED</c>)</summary>
    Untracked = 7,

    /// <summary>Type of entry changed between old and new. (<c>GIT_DELTA_TYPECHANGE</c>)</summary>
    Typechange = 8,

    /// <summary>Entry is unreadable. (<c>GIT_DELTA_UNREADABLE</c>)</summary>
    Unreadable = 9,

    /// <summary>Entry in the index is conflicted. (<c>GIT_DELTA_CONFLICTED</c>)</summary>
    Conflicted = 10,
}
