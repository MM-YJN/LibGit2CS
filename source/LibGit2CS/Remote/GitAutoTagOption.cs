// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary> Tag download policy for fetch. Maps to <c>git_remote_autotag_option_t</c> in <c>include/git2/remote.h</c> (UNSPECIFIED=0, AUTO=1, NONE=2, ALL=3). </summary>
public enum GitAutoTagOption
{
    /// <summary>Use the configured default (<c>remote.*.tagopt</c> or Auto).</summary>
    Unspecified = 0,

    /// <summary>Download tags reachable from fetched objects.
    /// (<c>GIT_REMOTE_DOWNLOAD_TAGS_AUTO = 1</c>)</summary>
    Auto = 1,

    /// <summary>Do not download any tags.
    /// (<c>GIT_REMOTE_DOWNLOAD_TAGS_NONE = 2</c>)</summary>
    None = 2,

    /// <summary>Download all tags from the remote.</summary>
    All = 3,
}
