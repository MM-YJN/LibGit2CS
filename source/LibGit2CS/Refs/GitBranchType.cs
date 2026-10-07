// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Refs;

/// <summary>
/// Branch type filter. Maps to <c>git_branch_t</c> in
/// <c>include/git2/types.h:231-235</c>.
/// </summary>
[Flags]
public enum GitBranchType
{
    /// <summary>Local branches (<c>refs/heads/</c>). (<c>GIT_BRANCH_LOCAL = 1</c>)</summary>
    Local = 1,

    /// <summary>Remote-tracking branches (<c>refs/remotes/</c>). (<c>GIT_BRANCH_REMOTE = 2</c>)</summary>
    Remote = 2,

    /// <summary>Both local and remote. (<c>GIT_BRANCH_ALL = 3</c>)</summary>
    All = Local | Remote,
}
