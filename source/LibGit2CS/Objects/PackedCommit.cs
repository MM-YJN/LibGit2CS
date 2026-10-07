// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System;
using System.Collections.Generic;
using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.Objects;

/// <summary>
/// A commit prepared for commit-graph serialization. Matches
/// <c>struct packed_commit</c> (commit_graph.c:50–58). Mutable: parent indices
/// and generation are populated after collection.
/// </summary>
internal sealed class PackedCommit
{
    /// <summary>The commit's own OID.</summary>
    public required GitOid Oid { get; init; }

    /// <summary>The commit's tree OID.</summary>
    public required GitOid TreeOid { get; init; }

    /// <summary>The commit time (committer timestamp, Unix epoch seconds).</summary>
    public required long CommitTime { get; init; }

    /// <summary>Parent commit OIDs (in order).</summary>
    public required List<GitOid> Parents { get; init; }

    /// <summary>The index of this commit in the writer's sorted list.</summary>
    public int Index { get; set; }

    /// <summary>Parent indices into the sorted commit list (populated during generation computation).</summary>
    public List<uint> ParentIndices { get; } = [];

    /// <summary>The computed generation number (populated by <c>ComputeGenerationNumbers</c>).</summary>
    public uint Generation { get; set; }

    /// <summary>
    /// Creates a <see cref="PackedCommit"/> from a <see cref="Commit"/> object.
    /// Matches <c>packed_commit_new</c> (commit_graph.c:70–100).
    /// </summary>
    public static PackedCommit Create(Commit commit)
    {
        return new PackedCommit
        {
            Oid = commit.Id,
            TreeOid = commit.Tree,
            CommitTime = commit.Time.Seconds,
            Parents = [.. commit.Parents],
        };
    }

    /// <summary>
    /// OID comparator for sorting. Matches <c>packed_commit__cmp</c>
    /// (commit_graph.c:689–694).
    /// </summary>
    public static int CompareByOid(PackedCommit a, PackedCommit b)
        => a.Oid.CompareTo(b.Oid);
}
