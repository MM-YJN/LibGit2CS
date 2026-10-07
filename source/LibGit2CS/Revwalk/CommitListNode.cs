// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Revwalk;

/// <summary>
/// A node in the commit graph used by revwalk. Managed equivalent of
/// libgit2's <c>git_commit_list_node</c> (from <c>commit_list.h</c>).
/// </summary>
/// <remarks>
/// <para>
/// Class (not struct) — mutable during walk (flags, in_degree, parents are
/// modified as the walk progresses). The C version uses a pool allocator; the
/// managed port uses normal GC allocation.
/// </para>
/// <para>
/// Fields mirror the C struct: <see cref="Oid"/>, <see cref="Time"/>,
/// <see cref="Generation"/>, <see cref="WalkFlags"/>, <see cref="GraphFlags"/>,
/// <see cref="InDegree"/>, <see cref="OutDegree"/>, <see cref="Parents"/>.
/// </para>
/// <para>
/// The C <c>git_commit_list_node</c> has two distinct flag bitfields: the
/// walk-state bits (<c>seen</c>, <c>uninteresting</c>, <c>topo_delay</c>,
/// <c>parsed</c>, <c>added</c>) and the separate <c>flags : FLAG_BITS</c> field
/// used by graph.c / merge.c (<c>PARENT1</c>, <c>PARENT2</c>, <c>RESULT</c>,
/// <c>STALE</c>). The managed port keeps these as two enums —
/// <see cref="CommitListWalkFlags"/> and <see cref="CommitListGraphFlags"/> — to
/// avoid the value collision that a single enum would produce.
/// </para>
/// </remarks>
internal sealed class CommitListNode
{
    /// <summary>The commit's OID.</summary>
    public required GitOid Oid { get; init; }

    /// <summary>Committer timestamp (Unix epoch seconds). From commit_quick_parse.</summary>
    public long Time { get; set; }

    /// <summary>
    /// Generation number from commit-graph (0 if unknown/no commit-graph).
    /// </summary>
    public uint Generation { get; set; }

    /// <summary>
    /// Walk-state flags (seen/uninteresting/topo_delay/parsed/added). Matches
    /// the per-node bitfield in <c>git_commit_list_node</c>.
    /// </summary>
    public CommitListWalkFlags WalkFlags { get; set; }

    /// <summary>
    /// Graph flags (PARENT1/PARENT2/RESULT/STALE) used by graph.c and merge.c.
    /// Matches the <c>flags : FLAG_BITS</c> field in <c>git_commit_list_node</c>.
    /// Cleared on revwalk reset via <c>commit->flags &amp;= ~ALL_FLAGS</c>.
    /// </summary>
    public CommitListGraphFlags GraphFlags { get; set; }

    /// <summary>Count of commits that have this node as a parent (topo sort).</summary>
    public ushort InDegree { get; set; }

    /// <summary>Count of parents (out-edges).</summary>
    public ushort OutDegree { get; set; }

    /// <summary>
    /// Parent commit nodes. Populated during parse. Null until parsed.
    /// </summary>
    public CommitListNode[]? Parents { get; set; }

    /// <summary>True if this node has been parsed from ODB/commit-graph.</summary>
    public bool IsParsed => (WalkFlags & CommitListWalkFlags.Parsed) != 0;

    /// <summary>True if this node has been seen (visited) during the walk.</summary>
    public bool IsSeen => (WalkFlags & CommitListWalkFlags.Seen) != 0;

    /// <summary>True if this node is uninteresting (negated/ref-prefix excluded).</summary>
    public bool IsUninteresting => (WalkFlags & CommitListWalkFlags.Uninteresting) != 0;
}
