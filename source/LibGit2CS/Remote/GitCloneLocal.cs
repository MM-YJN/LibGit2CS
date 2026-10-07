// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Local clone behavior. Maps to <c>git_clone_local_t</c> in
/// <c>include/git2/clone.h</c>.
/// </summary>
public enum GitCloneLocal
{
    /// <summary>
    /// Auto-detect: if the source is a local path or <c>file://</c> URL
    /// pointing to a directory, use the local (hardlink/copy) path;
    /// otherwise use the network (transport) path.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always use the local clone path (hardlink objects if possible,
    /// copy otherwise). The source must be a local path or
    /// <c>file://</c> URL.
    /// </summary>
    Local = 1,

    /// <summary>
    /// Never use the local clone path — always go through the transport
    /// layer (even for local sources).
    /// </summary>
    NoLocal = 2,

    /// <summary>
    /// Use the local clone path but never hardlink — always copy objects.
    /// </summary>
    NoLinks = 3,
}
