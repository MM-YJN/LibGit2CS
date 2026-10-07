// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Revwalk;

/// <summary>
/// Singly-linked list of <see cref="CommitListNode"/>s used by revwalk.
/// Managed port of libgit2's <c>src/libgit2/commit_list.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each element points to a <see cref="CommitListNode"/> and the next list
/// element. The list is used for revwalk's iterator stacks (topo, time, reverse).
/// </para>
/// <para>
/// The C version allocates list elements from <c>git_pool</c>; the managed port
/// uses normal GC allocation.
/// </para>
/// </remarks>
internal sealed class CommitList
{
    /// <summary>The commit node this list element wraps.</summary>
    public required CommitListNode Item { get; init; }

    /// <summary>The next list element, or <c>null</c> if this is the tail.</summary>
    public CommitList? Next { get; set; }

    /// <summary>
    /// Creates a new list element pointing to <paramref name="node"/> and
    /// chain it after <paramref name="next"/>. Matches
    /// <c>git_commit_list_create</c> / <c>git_commit_list_insert</c>.
    /// </summary>
    public static CommitList Create(CommitListNode node, CommitList? next)
        => new() { Item = node, Next = next };

    /// <summary>
    /// Inserts a node at the head of the list. Matches
    /// <c>git_commit_list_insert</c>.
    /// </summary>
    public static CommitList Insert(CommitListNode node, CommitList? head)
        => Create(node, head);

    /// <summary>
    /// Inserts a node into the list sorted by commit time (newest first, with
    /// stable tie-breaking so equal-time commits preserve insertion order).
    /// Matches <c>git_commit_list_insert_by_date</c> (commit_list.c:62-75).
    /// </summary>
    public static CommitList InsertByDate(CommitListNode node, CommitList? head)
    {
        // If the head is older than the node (or the list is empty), the node
        // becomes the new head. time_cmp(a, b) > 0 means a is older.
        if (head is null || TimeCompare(head.Item, node) > 0)
        {
            return Create(node, head);
        }

        // Walk past every element that is newer-or-equal (time_cmp <= 0),
        // stopping at the first older element. Insert before that older
        // element. Equal-time elements keep insertion order (stable).
        CommitList current = head;
        while (current.Next is not null && TimeCompare(current.Next.Item, node) <= 0)
        {
            current = current.Next;
        }

        current.Next = Create(node, current.Next);
        return head;
    }

    /// <summary>
    /// Removes and returns the head element's node. Matches
    /// <c>git_commit_list_pop</c>.
    /// </summary>
    public static (CommitListNode? node, CommitList? rest) Pop(CommitList? head)
    {
        if (head is null)
        {
            return (null, null);
        }

        return (head.Item, head.Next);
    }

    /// <summary>
    /// Counts the number of elements in the list.
    /// </summary>
    public static int Count(CommitList? head)
    {
        int count = 0;
        while (head is not null)
        {
            count++;
            head = head.Next;
        }

        return count;
    }

    /// <summary>
    /// Compares two nodes by generation number (for commit-graph priority
    /// queue ordering). Falls back to time comparison if either generation is
    /// 0 (no commit-graph). Matches <c>git_commit_list_generation_cmp</c>
    /// (commit_list.c:15-31).
    /// </summary>
    /// <remarks>
    /// A <em>higher</em> generation (more recent commit) compares as smaller,
    /// so the newest commit pops first from the min-heap.
    /// </remarks>
    public static int GenerationCompare(CommitListNode a, CommitListNode b)
    {
        if (a.Generation == 0 || b.Generation == 0)
        {
            return TimeCompare(a, b);
        }

        if (a.Generation < b.Generation)
        {
            return 1;
        }

        if (a.Generation > b.Generation)
        {
            return -1;
        }

        return 0;
    }

    /// <summary>
    /// Compares two nodes by commit time (for time-sorted priority queue
    /// ordering). Matches <c>git_commit_list_time_cmp</c>
    /// (commit_list.c:33-44).
    /// </summary>
    /// <remarks>
    /// A <em>newer</em> commit (larger timestamp) compares as smaller, so the
    /// newest commit pops first from the min-heap — matching
    /// <c>git log --date-order</c>.
    /// </remarks>
    public static int TimeCompare(CommitListNode a, CommitListNode b)
    {
        if (a.Time < b.Time)
        {
            return 1;
        }

        if (a.Time > b.Time)
        {
            return -1;
        }

        return 0;
    }

    /// <summary>
    /// Parses a commit node from the ODB, populating <see cref="CommitListNode.Time"/>,
    /// <see cref="CommitListNode.OutDegree"/>, and <see cref="CommitListNode.Parents"/>.
    /// Matches <c>git_commit_list_parse</c> (commit_list.c:179-226).
    /// </summary>
    /// <param name="node">The node to parse.</param>
    /// <param name="repo">The repository to read from.</param>
    /// <param name="lookupParent">Callback to resolve a parent OID to a
    /// <see cref="CommitListNode"/> (provided by <c>RevWalker</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> with the ODB message when the object
    /// is missing (C: <c>git_odb_read</c> failure, commit_list.c:198), or
    /// <see cref="GitErrorCode.Error"/> with <c>"object is no commit object"</c>
    /// (GIT_ERROR_INVALID) when the object exists but is not a commit
    /// (commit_list.c:210-212).
    /// </exception>
    public static async Task ParseAsync(CommitListNode node, GitRepository repo, Func<GitOid, CommitListNode> lookupParent, CancellationToken cancellationToken)
    {
        if (node.IsParsed)
        {
            return;
        }

        // Commit-graph fast path: O(1) parse avoiding an ODB read + zlib inflate.
        // C only uses the graph entry when git__is_uint16(e.parent_count)
        // (commit_list.c:187); a graph entry with >65535 parents is discarded
        // and the commit is re-parsed from the ODB.
        CommitGraph? graph = await repo.GetCommitGraphAsync(cancellationToken).ConfigureAwait(false);
        if (graph?.FindEntry(node.Oid) is { } entry && entry.ParentCount <= ushort.MaxValue)
        {
            node.Time = entry.CommitTime;
            node.Generation = entry.Generation;
            node.OutDegree = (ushort)entry.ParentCount;
            node.Parents = new CommitListNode[entry.Parents.Length];
            for (int i = 0; i < entry.Parents.Length; i++)
            {
                node.Parents[i] = lookupParent(entry.Parents[i]);
            }

            node.WalkFlags |= CommitListWalkFlags.Parsed;
            return;
        }

        // ODB fallback: read the raw object and quick-parse it. C reads the
        // object with git_odb_read and then checks the type itself — the
        // typed lookup would map a non-commit object to a different error, so
        // the raw object is examined here to reproduce commit_list.c exactly.
        GitObject? obj = await repo.Objects.LookupAsync(node.Oid, cancellationToken).ConfigureAwait(false);
        if (obj is null)
        {
            // C (commit_list.c:198-199): git_odb_read failure — GIT_ENOTFOUND
            // "object not found - no match for id (<oid>)" (GIT_ERROR_ODB).
            throw new GitException(
                GitErrorCode.NotFound,
                $"object not found - no match for id ({node.Oid})",
                GitErrorCategory.Odb);
        }

        if (obj is not Commit raw)
        {
            obj.Dispose();
            // C (commit_list.c:210-212): git_error_set(GIT_ERROR_INVALID,
            // "object is no commit object"); error = -1.
            throw new GitException(
                GitErrorCode.Error,
                "object is no commit object",
                GitErrorCategory.Invalid);
        }

        try
        {
            (long time, GitOid[]? parentOids) = Commit.ParseQuick(raw, repo.ObjectFormat);
            node.Time = time;

            // C's
            // commit_quick_parse fails with GIT_ERROR_INVALID 'commit has
            // more than 2^16 parents' when the parent count exceeds
            // ushort.MaxValue (commit_list.c:147-151) — the ODB fallback
            // must not truncate OutDegree, or every revwalk loop (bounded by
            // OutDegree) silently drops commits reachable only through
            // parents ≥ 65536. The commit-graph path already guards (line
            // 184, mirroring git__is_uint16).
            if (parentOids.Length > ushort.MaxValue)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "commit has more than 2^16 parents",
                    GitErrorCategory.Invalid);
            }

            node.OutDegree = (ushort)parentOids.Length;
            node.Parents = new CommitListNode[parentOids.Length];
            for (int i = 0; i < parentOids.Length; i++)
            {
                node.Parents[i] = lookupParent(parentOids[i]);
            }
        }
        finally
        {
            raw.Dispose();
        }

        node.WalkFlags |= CommitListWalkFlags.Parsed;
    }
}
