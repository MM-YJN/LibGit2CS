// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Objects;

/// <summary>
/// Kind of tree update. Matches <c>git_tree_update_t</c>.
/// </summary>
public enum GitTreeUpdateAction
{
    /// <summary>Update or insert an entry at the specified path.</summary>
    Upsert = 0,

    /// <summary>Remove an entry from the specified path.</summary>
    Remove = 1,
}
