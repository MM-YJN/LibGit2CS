// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Revwalk;

/// <summary>
/// Graph flags on <see cref="CommitListNode"/>. Matches C's
/// <c>flags : FLAG_BITS</c> field (<c>PARENT1</c>, <c>PARENT2</c>,
/// <c>RESULT</c>, <c>STALE</c>) used by graph.c (ahead-behind / descendant-of)
/// and merge.c (merge-base computation). Independent from
/// <see cref="CommitListWalkFlags"/> — a node can carry both sets concurrently.
/// </summary>
[Flags]
internal enum CommitListGraphFlags : uint
{
    None = 0,

    /// <summary>Commit reachable from the "one"/local lineage. C: <c>PARENT1 = 1&lt;&lt;0</c>.</summary>
    Parent1 = 1,

    /// <summary>Commit reachable from the "two"/upstream lineage. C: <c>PARENT2 = 1&lt;&lt;1</c>.</summary>
    Parent2 = 2,

    /// <summary>Merge-base result / already-processed marker. C: <c>RESULT = 1&lt;&lt;2</c>.</summary>
    Result = 4,

    /// <summary>Parent of a merge-base (stop expanding that path). C: <c>STALE = 1&lt;&lt;3</c>.</summary>
    Stale = 8,
}
