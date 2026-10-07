// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Submodule;

/// <summary>
/// Submodule fetch recursion strategy. Managed port of
/// <c>git_submodule_recurse_t</c> (include/git2/types.h:366-370).
/// </summary>
public enum SubmoduleRecurse
{
    /// <summary>No recursion.</summary>
    No = 0,

    /// <summary>Recursive.</summary>
    Yes = 1,

    /// <summary>On-demand recursion.</summary>
    OnDemand = 2,
}
