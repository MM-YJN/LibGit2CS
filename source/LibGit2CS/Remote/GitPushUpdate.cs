// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Remote;

/// <summary>
/// A push ref update notification. Maps to <c>git_push_update</c> in
/// <c>include/git2/remote.h</c>.
/// </summary>
public sealed record GitPushUpdate
{
    /// <summary>The source ref name on the local side.</summary>
    public string SrcRefName { get; init; } = string.Empty;

    /// <summary>The destination ref name on the remote side.</summary>
    public string DstRefName { get; init; } = string.Empty;

    /// <summary> The source OID — the CURRENT target of the reference on the remote (before the push). Matches C's <c>git_push_update.src</c> (remote.h: "The
    /// current target of the reference"; push.c:389-390 copies <c>spec-&gt;roid</c>). Zero for a create. </summary>
    public GitOid Src { get; init; }

    /// <summary> The destination OID — the NEW target for the reference (the local side's value being pushed). Matches C's <c>git_push_update.dst</c>
    /// (remote.h: "The new target for the reference"; push.c:390 copies <c>spec-&gt;loid</c>). Zero for a delete. </summary>
    public GitOid Dst { get; init; }
}
