// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Transports;

/// <summary>
/// Remote-side capability flags returned by <c>ITransport.Capabilities</c>.
/// Maps to <c>git_remote_capability_t</c> in <c>include/git2/sys/remote.h</c>.
/// </summary>
[Flags]
public enum GitRemoteCapability
{
    /// <summary>No special capabilities.</summary>
    None = 0,

    /// <summary>Remote can accept tip SHA-1 in want lines.</summary>
    TipOid = 1 << 0,

    /// <summary>Remote can accept any reachable SHA-1 in want lines.</summary>
    ReachableOid = 1 << 1,

    /// <summary>Remote accepts push-options (sys/remote.h:34, <c>1u &lt;&lt; 2</c>).</summary>
    PushOptions = 1 << 2,
}
