// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Revwalk;

/// <summary>
/// Walk-state flags on <see cref="CommitListNode"/>. Matches the per-node
/// bitfield in C's <c>git_commit_list_node</c> (<c>seen:1</c>,
/// <c>uninteresting:1</c>, <c>topo_delay:1</c>, <c>parsed:1</c>,
/// <c>added:1</c>). These drive revwalk's traversal state machine.
/// </summary>
[Flags]
internal enum CommitListWalkFlags : byte
{
    None = 0,

    /// <summary>Node has been visited during the walk. C: <c>seen:1</c>.</summary>
    Seen = 1,

    /// <summary>Node is excluded (e.g. from ^rev). C: <c>uninteresting:1</c>.</summary>
    Uninteresting = 2,

    /// <summary>Topological ordering delay state. C: <c>topo_delay:1</c>.</summary>
    TopoDelay = 4,

    /// <summary>Node has been parsed from ODB/commit-graph. C: <c>parsed:1</c>.</summary>
    Parsed = 8,

    /// <summary>Node has been added to the walk queue. C: <c>added:1</c>.</summary>
    Added = 16,
}
