// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Rebase;

/// <summary>
/// Rebase type (internal). Matches the <c>git_rebase_t</c> enum in
/// <c>rebase.c:53-58</c>. Only <see cref="Merge"/> is implemented;
/// <see cref="Apply"/> and <see cref="Interactive"/> are not supported.
/// </summary>
internal enum RebaseType
{
    /// <summary>No rebase in progress.</summary>
    None = 0,

    /// <summary>Patch-application rebase (NOT supported).</summary>
    Apply = 1,

    /// <summary>Merge-style rebase (the only implemented type).</summary>
    Merge = 2,

    /// <summary>Interactive rebase (NOT supported).</summary>
    Interactive = 3,
}
