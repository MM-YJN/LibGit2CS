// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary> Redirect-following behavior for remote operations. Maps to <c>git_remote_redirect_t</c> in <c>include/git2/remote.h</c>: <c>NONE = 1</c>,
/// <c>INITIAL = 2</c>, <c>ALL = 4</c>, and <c>0</c> means unspecified (consult <c>http.followRedirects</c> config). The invented C#-only <c>Same</c> mode
/// (documented same-scheme but implemented as follow-all) was removed. </summary>
[Flags]
public enum GitRemoteRedirect
{
    /// <summary>
    /// Unspecified — resolve <c>http.followRedirects</c> config at connect
    /// time (C's <c>0</c> slot; remote.c:924-927).
    /// </summary>
    Unspecified = 0,

    /// <summary>Do not follow redirects.</summary>
    None = 1,

    /// <summary>Follow only the initial redirect.</summary>
    Initial = 1 << 1,

    /// <summary>Follow all redirects (including scheme changes).</summary>
    All = 1 << 2,
}
