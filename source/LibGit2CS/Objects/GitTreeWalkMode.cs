// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Objects;

/// <summary>
/// Walk mode for <see cref="GitTree.WalkAsync"/>. Matches <c>GIT_TREEWALK_PRE</c> /
/// <c>GIT_TREEWALK_POST</c>.
/// </summary>
public enum GitTreeWalkMode
{
    /// <summary>Visit the parent tree before its sub-trees (depth-first, pre-order).</summary>
    PreOrder,

    /// <summary>Visit sub-trees before their parent (depth-first, post-order).</summary>
    PostOrder,
}
