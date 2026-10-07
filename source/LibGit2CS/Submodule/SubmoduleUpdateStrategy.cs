// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Submodule;

/// <summary>
/// Submodule update strategy. Managed port of
/// <c>git_submodule_update_t</c> (include/git2/types.h:311-318).
/// </summary>
public enum SubmoduleUpdateStrategy
{
    /// <summary>Default: checkout detached HEAD.</summary>
    Default = 0,

    /// <summary>Checkout detached HEAD.</summary>
    Checkout = 1,

    /// <summary>Rebase onto superproject commit.</summary>
    Rebase = 2,

    /// <summary>Merge superproject commit.</summary>
    Merge = 3,

    /// <summary>Do not update.</summary>
    None = 4,
}

