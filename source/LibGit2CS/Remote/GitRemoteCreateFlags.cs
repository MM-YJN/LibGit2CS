// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Flags for <see cref="GitRemoteCreateOptions"/>. Maps to
/// <c>git_remote_create_flags</c> in <c>include/git2/remote.h</c>:
/// <c>GIT_REMOTE_CREATE_SKIP_INSTEADOF = (1 &lt;&lt; 0)</c>,
/// <c>GIT_REMOTE_CREATE_SKIP_DEFAULT_FETCHSPEC = (1 &lt;&lt; 1)</c>.
/// </summary>
public static class GitRemoteCreateFlags
{
    /// <summary>Do not apply <c>url.*.insteadOf</c> rewriting to the URL.</summary>
    public const uint SkipInsteadOf = 1 << 0;

    /// <summary>Do not add the default fetch refspec
    /// (<c>+refs/heads/*:refs/remotes/&lt;name&gt;/*</c>).</summary>
    public const uint SkipDefaultFetchSpec = 1 << 1;
}
