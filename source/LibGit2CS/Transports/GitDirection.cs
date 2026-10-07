// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Transports;

/// <summary>
/// Direction of a remote operation. Maps to <c>git_direction</c> in
/// <c>include/git2/net.h</c>.
/// </summary>
public enum GitDirection
{
    /// <summary>Fetch direction (git-upload-pack service).</summary>
    Fetch = 0,

    /// <summary>Push direction (git-receive-pack service).</summary>
    Push = 1,
}
