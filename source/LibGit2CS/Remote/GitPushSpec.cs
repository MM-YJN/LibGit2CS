// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Refs;

namespace LibGit2CS.Remote;

/// <summary>
/// A parsed push refspec with resolved local and remote OIDs.
/// Managed equivalent of <c>push_spec</c> in <c>src/libgit2/push.h</c>.
/// </summary>
public sealed record GitPushSpec
{
    /// <summary>The parsed refspec (push direction).</summary>
    public required GitRefSpec RefSpec { get; init; }

    /// <summary>The local source OID (what we are pushing from). Zero for delete.</summary>
    public GitOid Loid { get; set; }

    /// <summary>The remote destination OID (what is currently on the remote). Zero for create.</summary>
    public GitOid Roid { get; set; }
}
