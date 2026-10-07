// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Status;

/// <summary>Which sides of the status to compute. Matches <c>git_status_show_t</c>.</summary>
public enum GitStatusShow
{
    /// <summary>Compute both HEAD→index and index→workdir. (<c>GIT_STATUS_SHOW_INDEX_AND_WORKDIR = 0</c>)</summary>
    IndexAndWorkdir = 0,

    /// <summary>Compute only HEAD→index (staged changes). (<c>GIT_STATUS_SHOW_INDEX_ONLY = 1</c>)</summary>
    IndexOnly = 1,

    /// <summary>Compute only index→workdir (unstaged + untracked). (<c>GIT_STATUS_SHOW_WORKDIR_ONLY = 2</c>)</summary>
    WorkdirOnly = 2,
}
