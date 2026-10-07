// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.Revwalk;

/// <summary>
/// Commit walker. Iterates commits reachable from a set of roots, optionally
/// sorted topologically, by commit time, or in reverse. Managed port of
/// libgit2's <c>src/libgit2/revwalk.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Usage: push one or more roots (<see cref="PushAsync"/>/<see cref="PushRefAsync"/>/
/// <see cref="PushGlobAsync"/>/<see cref="PushHeadAsync"/>), optionally hide commits
/// (<see cref="HideAsync"/> and friends), set a <see cref="Sort"/> mode, then
/// enumerate <see cref="WalkAsync"/>. <c>HEAD~3..HEAD</c>-style ranges use
/// <see cref="PushRangeAsync"/>.
/// </para>
/// <para>
/// The walker lazily parses commits via <see cref="CommitList.ParseAsync"/> (which
/// tries the commit-graph fast path when present, falling back to an ODB read).
/// Each walker owns its own commit-node cache (<see cref="LookupCommit"/>), so
/// multiple walkers on one repository are independent.
/// </para>
/// <para>
/// <b>Sort modes.</b> <see cref="GitSortMode.None"/> yields commits roughly in
/// discovery order. <see cref="GitSortMode.Topological"/> guarantees children
/// before parents (Kahn's algorithm with in-degree counting). <see cref="GitSortMode.Time"/>
/// orders by committer timestamp (newest first); combine with Topological for
/// <c>--date-order</c>. <see cref="GitSortMode.Reverse"/> flips any of the above.
/// </para>
/// </remarks>
public sealed class GitRevWalker : IDisposable
{
    private const int Slop = 5;

    private readonly GitRepository _repo;
    private readonly Dictionary<GitOid, CommitListNode> _commits = [];

    private GitSortMode _sorting;
    private bool _walking;
    private bool _didPush;
    private bool _limited;
    private bool _firstParent;

    private CommitList? _userInput;
    private CommitList? _iteratorTopo;
    private CommitList? _iteratorRand;
    private CommitList? _iteratorReverse;
    private readonly MinHeap<CommitListNode> _iteratorTime;

    private NextHandler _next;
    private EnqueueHandler _enqueue;
    private Func<GitOid, bool>? _hideCb;

    private bool _disposed;

    private delegate Task<CommitListNode?> NextHandler(CancellationToken cancellationToken);
    private delegate void EnqueueHandler(CommitListNode commit);

    /// <summary>Creates a walker over <paramref name="repo"/>. Internal — the
    /// public entry point is <see cref="GitRepository.NewRevWalker"/>.</summary>
    internal GitRevWalker(GitRepository repo)
    {
        ArgumentNullException.ThrowIfNull(repo);

        _repo = repo;
        _iteratorTime = new MinHeap<CommitListNode>(CommitList.TimeCompare, initialCapacity: 8);
        _next = NextUnsortedAsync;
        _enqueue = EnqueueUnsorted;
    }

    /// <summary>The owning repository. Matches <c>git_revwalk_repository</c>.</summary>
    public GitRepository Repository => _repo;

    /// <summary>
    /// The sort mode. Setting this resets the walk if iteration has begun.
    /// Matches <c>git_revwalk_sorting</c>.
    /// </summary>
    public GitSortMode Sort
    {
        get => _sorting;
        set
        {
            if (_walking)
            {
                Reset();
            }

            _sorting = value;

            if ((value & GitSortMode.Time) != 0)
            {
                _next = NextTimesortAsync;
                _enqueue = EnqueueTimesort;
            }
            else
            {
                _next = NextUnsortedAsync;
                _enqueue = EnqueueUnsorted;
            }

            if (value != GitSortMode.None)
            {
                _limited = true;
            }
        }
    }

    /// <summary>
    /// Restricts subsequent walking to the first parent of each commit.
    /// Matches <c>git_revwalk_simplify_first_parent</c>.
    /// </summary>
    public void SimplifyFirstParent() => _firstParent = true;

    /// <summary>
    /// Installs a callback that decides per-commit whether to hide it (and stop
    /// walking its parents). Return <c>true</c> to hide. Setting a callback
    /// enables the limited (limit_list) pass. Matches <c>git_revwalk_add_hide_cb</c>.
    /// </summary>
    public void AddHideCallback(Func<GitOid, bool>? hideCb)
    {
        if (_walking)
        {
            Reset();
        }

        _hideCb = hideCb;

        if (hideCb is not null)
        {
            _limited = true;
        }
    }

    // ── Push / Hide (port of revwalk.c:44-282) ──────────────────────────

    /// <summary>Pushes a commit OID as a traversal root. Matches <c>git_revwalk_push</c>.</summary>
    public async Task PushAsync(GitOid id, CancellationToken cancellationToken = default)
        => await PushCommitAsync(id, uninteresting: false, fromGlob: false, insertByDate: false, cancellationToken).ConfigureAwait(false);

    /// <summary>Hides a commit and all its ancestors. Matches <c>git_revwalk_hide</c>.</summary>
    public async Task HideAsync(GitOid id, CancellationToken cancellationToken = default)
        => await PushCommitAsync(id, uninteresting: true, fromGlob: false, insertByDate: false, cancellationToken).ConfigureAwait(false);

    /// <summary>Pushes the OID pointed to by a fully-qualified ref name. Matches <c>git_revwalk_push_ref</c>.</summary>
    public async Task PushRefAsync(string refName, CancellationToken cancellationToken = default)
        => await PushRefNameAsync(refName, uninteresting: false, fromGlob: false, cancellationToken).ConfigureAwait(false);

    /// <summary>Hides the OID pointed to by a fully-qualified ref name. Matches <c>git_revwalk_hide_ref</c>.</summary>
    public async Task HideRefAsync(string refName, CancellationToken cancellationToken = default)
        => await PushRefNameAsync(refName, uninteresting: true, fromGlob: false, cancellationToken).ConfigureAwait(false);

    /// <summary>Pushes HEAD. Matches <c>git_revwalk_push_head</c>.</summary>
    public async Task PushHeadAsync(CancellationToken cancellationToken = default)
        => await PushRefNameAsync(GitReferences.HeadFile, uninteresting: false, fromGlob: false, cancellationToken).ConfigureAwait(false);

    /// <summary>Hides HEAD. Matches <c>git_revwalk_hide_head</c>.</summary>
    public async Task HideHeadAsync(CancellationToken cancellationToken = default)
        => await PushRefNameAsync(GitReferences.HeadFile, uninteresting: true, fromGlob: false, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Pushes all refs matching a glob (with implied <c>refs/</c> prefix and
    /// <c>/*</c> suffix). Matches <c>git_revwalk_push_glob</c>.
    /// </summary>
    public async Task PushGlobAsync(string glob, CancellationToken cancellationToken = default)
        => await PushGlobImplAsync(glob, uninteresting: false, cancellationToken).ConfigureAwait(false);

    /// <summary>Hides all refs matching a glob. Matches <c>git_revwalk_hide_glob</c>.</summary>
    public async Task HideGlobAsync(string glob, CancellationToken cancellationToken = default)
        => await PushGlobImplAsync(glob, uninteresting: true, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Parses <c>"A..B"</c>, hides A, pushes B. Symmetric difference
    /// (<c>A...B</c>) is rejected. Matches <c>git_revwalk_push_range</c>.
    /// </summary>
    public async Task PushRangeAsync(string range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);

        (GitObject? from, GitObject? to, GitRevSpecFlags flags) = await GitRevParser.ParseRangeAsync(_repo, range, cancellationToken).ConfigureAwait(false);

        if (to is null)
        {
            throw new GitException(
                GitErrorCode.InvalidSpec,
                "invalid revspec: range not provided",
                GitErrorCategory.Invalid);
        }

        if ((flags & GitRevSpecFlags.MergeBase) != 0)
        {
            throw new GitException(
                GitErrorCode.InvalidSpec,
                "symmetric differences not implemented in revwalk",
                GitErrorCategory.Invalid);
        }

        if (from is not null)
        {
            await PushCommitAsync(from.Id, uninteresting: true, fromGlob: false, insertByDate: false, cancellationToken).ConfigureAwait(false);
            from.Dispose();
        }

        await PushCommitAsync(to.Id, uninteresting: false, fromGlob: false, insertByDate: false, cancellationToken).ConfigureAwait(false);
        to.Dispose();
    }

    /// <summary>
    /// The user-pushed commit roots with their uninteresting flags. Matches
    /// C's <c>walk-&gt;user_input</c> — used by
    /// <see cref="Pack.GitPackWriter.InsertWalkAsync"/> to mark the edges of
    /// hidden commits uninteresting (pack-objects.c:1684-1705).
    /// </summary>
    internal IReadOnlyList<CommitListNode> UserInput
    {
        get
        {
            var list = new List<CommitListNode>();
            for (CommitList? l = _userInput; l is not null; l = l.Next)
            {
                list.Add(l.Item);
            }

            return list;
        }
    }

    /// <summary>
    /// Look up (or create) the <see cref="CommitListNode"/> for <paramref name="oid"/>
    /// in this walker's cache. Matches <c>git_revwalk__commit_lookup</c>.
    /// </summary>
    internal CommitListNode LookupCommit(GitOid oid)
    {
        if (_commits.TryGetValue(oid, out CommitListNode? commit))
        {
            return commit;
        }

        commit = new CommitListNode { Oid = oid };
        _commits[oid] = commit;
        return commit;
    }

    /// <summary>
    /// Core push/hide. Matches <c>git_revwalk__push_commit</c> (revwalk.c:44-104).
    /// </summary>
    private async Task PushCommitAsync(GitOid oid, bool uninteresting, bool fromGlob, bool insertByDate, CancellationToken cancellationToken)
    {
        // C (revwalk.c:52-53): the object lookup failure is returned
        // unconditionally — the from_glob early-return guards ONLY the
        // peel-error path (lines 58-65), so a dangling ref fails the whole
        // push_glob/hide_glob.
        GitObject? obj = await _repo.Objects.LookupAsync(oid, cancellationToken).ConfigureAwait(false);
        if (obj is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"object not found - no match for id ({oid})",
                GitErrorCategory.Odb);
        }

        Commit commit;
        try
        {
            commit = await obj.PeelAsync<Commit>(cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code is GitErrorCode.NotFound or GitErrorCode.InvalidSpec or GitErrorCode.Peel)
        {
            obj.Dispose();
            if (fromGlob)
            {
                return;
            }

            // C (revwalk.c:58-65): the message is set but the ORIGINAL error
            // code is returned (e.g. GIT_EPEEL for a tag to a blob,
            // GIT_EINVALIDSPEC for a tree/blob oid) — the port must not
            // remap it to GIT_EINVALID.
            throw new GitException(
                ex.Code,
                "object is not a committish",
                GitErrorCategory.Invalid);
        }

        if (obj is not Commit)
        {
            obj.Dispose();
        }

        try
        {
            GitOid commitId = commit.Id;
            CommitListNode node = LookupCommit(commitId);

            // A previous hide already told us we don't want this commit.
            if (node.IsUninteresting)
            {
                return;
            }

            if (uninteresting)
            {
                _limited = true;
            }
            else
            {
                _didPush = true;
            }

            if (uninteresting)
            {
                node.WalkFlags |= CommitListWalkFlags.Uninteresting;
            }

            // To insert by date, we need to parse so we know the date.
            if (insertByDate)
            {
                await CommitList.ParseAsync(node, _repo, oid => LookupCommit(oid), cancellationToken).ConfigureAwait(false);
            }

            if (insertByDate)
            {
                _userInput = CommitList.InsertByDate(node, _userInput);
            }
            else
            {
                _userInput = CommitList.Insert(node, _userInput);
            }
        }
        finally
        {
            if (obj is Commit)
            {
                commit.Dispose();
            }
        }
    }

    /// <summary>Matches <c>git_revwalk__push_ref</c> (revwalk.c:128-140).</summary>
    private async Task PushRefNameAsync(RefNameKey refName, bool uninteresting, bool fromGlob, CancellationToken cancellationToken)
    {
        GitReference? resolved = await _repo.Refs.ResolveAsync(refName, cancellationToken).ConfigureAwait(false);
        if (resolved is not GitDirectReference direct)
        {
            if (fromGlob)
            {
                return;
            }

            // C (revwalk.c:135-137): a plain push_ref/hide_ref of a missing
            // ref returns generic -1 (GIT_ERROR) — the ENOTFOUND stays only
            // in the error cache.
            throw new GitException(
                GitErrorCode.Error,
                $"reference '{refName}' not found",
                GitErrorCategory.Reference);
        }

        await PushCommitAsync(direct.Target, uninteresting, fromGlob, insertByDate: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Matches <c>git_revwalk__push_glob</c> (revwalk.c:142-186).</summary>
    private async Task PushGlobImplAsync(string glob, bool uninteresting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(glob);

        // refs/ is implied if not given in the glob.
        string buf = glob.StartsWith(GitReferences.RefsDir, StringComparison.Ordinal)
            ? glob
            : $"{GitReferences.RefsDir}{glob}";

        // If no '?', '*' or '[' exist, append '/*'.
        if (glob.IndexOfAny(['?', '*', '[']) < 0)
        {
            buf += "/*";
        }

        await foreach (RefNameKey name in _repo.Refs.ListNameKeysAsync(buf, cancellationToken).ConfigureAwait(false))
        {
            await PushRefNameAsync(name, uninteresting, fromGlob: true, cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Enqueue / Next handlers (port of revwalk.c:284-346) ─────────────

    private void EnqueueTimesort(CommitListNode commit) => _iteratorTime.Insert(commit);

    private void EnqueueUnsorted(CommitListNode commit)
        => _userInput = CommitList.Insert(commit, _userInput);

    private Task<CommitListNode?> NextTimesortAsync(CancellationToken cancellationToken)
    {
        while (_iteratorTime.Pop() is { } next)
        {
            // Some commits might become uninteresting after being added to the list.
            if (!next.IsUninteresting)
            {
                return Task.FromResult<CommitListNode?>(next);
            }
        }

        return Task.FromResult<CommitListNode?>(null);
    }

    private async Task<CommitListNode?> NextUnsortedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // the
            // enumerating-phase ODB/commit-graph reads must honor the
            // caller's token.
            (CommitListNode? next, CommitList? rest) = await GetRevisionAsync(_iteratorRand, cancellationToken).ConfigureAwait(false);
            _iteratorRand = rest;
            if (next is null)
            {
                break;
            }

            if (!next.IsUninteresting)
            {
                return next;
            }
        }

        return null;
    }

    private async Task<CommitListNode?> NextToposortAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // see NextUnsortedAsync.
            (CommitListNode? next, CommitList? rest) = await GetRevisionAsync(_iteratorTopo, cancellationToken).ConfigureAwait(false);
            _iteratorTopo = rest;
            if (next is null)
            {
                break;
            }

            if (!next.IsUninteresting)
            {
                return next;
            }
        }

        return null;
    }

    private Task<CommitListNode?> NextReverseAsync(CancellationToken cancellationToken)
    {
        (CommitListNode? node, CommitList? rest) = CommitList.Pop(_iteratorReverse);
        _iteratorReverse = rest;
        return Task.FromResult<CommitListNode?>(node);
    }

    // ── Walk internals (port of revwalk.c:348-698) ──────────────────────

    /// <summary>Matches <c>mark_parents_uninteresting</c> (revwalk.c:348-378).</summary>
    private static void MarkParentsUninteresting(CommitListNode commit)
    {
        CommitList? parents = null;
        CommitListNode[]? commitParents = commit.Parents;
        for (int i = 0; i < commit.OutDegree; i++)
        {
            Debug.Assert(commitParents is not null, "Parents is set (with OutDegree) during ParseAsync");
            parents = CommitList.Insert(commitParents[i], parents);
        }

        while (parents is not null)
        {
            (CommitListNode? node, CommitList? rest) = CommitList.Pop(parents);
            parents = rest;
            Debug.Assert(node is not null, "CommitList.Pop on non-null head yields non-null node");
            commit = node;

            while (commit is not null)
            {
                if (commit.IsUninteresting)
                {
                    break;
                }

                commit.WalkFlags |= CommitListWalkFlags.Uninteresting;

                // If we've reached this commit some other way already, we need to
                // mark its parents uninteresting as well.
                if (commit.Parents is null)
                {
                    break;
                }

                for (int i = 0; i < commit.OutDegree; i++)
                {
                    parents = CommitList.Insert(commit.Parents[i], parents);
                }

                if (commit.OutDegree == 0)
                {
                    break;
                }

                commit = commit.Parents[0];
            }
        }
    }

    /// <summary>Matches <c>add_parents_to_list</c> (revwalk.c:380-440).</summary>
    private async Task<CommitList?> AddParentsToListAsync(CommitListNode commit, CommitList? list, CancellationToken cancellationToken)
    {
        if ((commit.WalkFlags & CommitListWalkFlags.Added) != 0)
        {
            return list;
        }

        commit.WalkFlags |= CommitListWalkFlags.Added;

        // Uninteresting case: walk ALL parents, marking them uninteresting.
        if (commit.IsUninteresting)
        {
            CommitListNode[]? commitParents = commit.Parents;
            for (int i = 0; i < commit.OutDegree; i++)
            {
                Debug.Assert(commitParents is not null, "Parents is set (with OutDegree) during ParseAsync");
                CommitListNode p = commitParents[i];
                p.WalkFlags |= CommitListWalkFlags.Uninteresting;

                // C (revwalk.c:398-399): git_commit_list_parse propagates the
                // ODB error (missing object → GIT_ENOTFOUND, non-commit → -1).
                await CommitList.ParseAsync(p, _repo, oid => LookupCommit(oid), cancellationToken).ConfigureAwait(false);

                if (p.Parents is not null)
                {
                    MarkParentsUninteresting(p);
                }

                p.WalkFlags |= CommitListWalkFlags.Seen;
                list = CommitList.InsertByDate(p, list);
            }

            return list;
        }

        // Interesting case: respect first_parent.
        CommitListNode[]? parentsArr = commit.Parents;
        for (int i = 0; i < commit.OutDegree; i++)
        {
            Debug.Assert(parentsArr is not null, "Parents is set (with OutDegree) during ParseAsync");
            CommitListNode p = parentsArr[i];

            await CommitList.ParseAsync(p, _repo, oid => LookupCommit(oid), cancellationToken).ConfigureAwait(false);

            if (_hideCb is not null && _hideCb(p.Oid))
            {
                continue;
            }

            if ((p.WalkFlags & CommitListWalkFlags.Seen) == 0)
            {
                p.WalkFlags |= CommitListWalkFlags.Seen;
                list = CommitList.InsertByDate(p, list);
            }

            if (_firstParent)
            {
                break;
            }
        }

        return list;
    }

    /// <summary>Matches <c>still_interesting</c> (revwalk.c:445-469).</summary>
    private static int StillInteresting(CommitList? list, long time, int slop)
    {
        // The empty list is pretty boring.
        if (list is null)
        {
            return 0;
        }

        // If the destination list has commits with an earlier date than our
        // source, reset the slop counter as we're not done.
        if (time <= list.Item.Time)
        {
            return Slop;
        }

        for (CommitList? l = list; l is not null; l = l.Next)
        {
            // If the destination list still contains interesting commits, continue.
            if (l.Item.IsUninteresting is false || l.Item.Time > time)
            {
                return Slop;
            }
        }

        // Everything's uninteresting, reduce the count.
        return slop - 1;
    }

    /// <summary>Matches <c>limit_list</c> (revwalk.c:471-505).</summary>
    private async Task<CommitList?> LimitListAsync(CommitList? commits, CancellationToken cancellationToken)
    {
        int slop = Slop;
        long time = long.MaxValue;
        CommitList? list = commits;
        CommitList? newList = null;
        CommitList? newTail = null;

        while (list is not null)
        {
            (CommitListNode? commit, CommitList? rest) = CommitList.Pop(list);
            list = rest;

            if (commit is null)
            {
                break;
            }

            // C (revwalk.c:482-483): add_parents_to_list propagates the parse
            // error (missing parent → GIT_ENOTFOUND, non-commit → -1).
            list = await AddParentsToListAsync(commit, list, cancellationToken).ConfigureAwait(false);

            if (commit.IsUninteresting)
            {
                MarkParentsUninteresting(commit);

                slop = StillInteresting(list, time, slop);
                if (slop > 0)
                {
                    continue;
                }

                break;
            }

            if (_hideCb is not null && _hideCb(commit.Oid))
            {
                continue;
            }

            time = commit.Time;

            // Append to tail of newList (mirrors `p = &insert(commit, p)->next`).
            var newNode = CommitList.Create(commit, null);
            if (newTail is null)
            {
                newList = newNode;
            }
            else
            {
                newTail.Next = newNode;
            }

            newTail = newNode;
        }

        return newList;
    }

    /// <summary>Matches <c>get_revision</c> (revwalk.c:507-529).</summary>
    private async Task<(CommitListNode? node, CommitList? rest)> GetRevisionAsync(CommitList? list, CancellationToken cancellationToken)
    {
        (CommitListNode? popped, CommitList? rest) = CommitList.Pop(list);

        if (popped is null)
        {
            return (null, rest);
        }

        // If we did not run limit_list, add parents to the list ourselves.
        if (!_limited)
        {
            rest = await AddParentsToListAsync(popped, rest, cancellationToken).ConfigureAwait(false);
        }

        return (popped, rest);
    }

    /// <summary>Matches <c>sort_in_topological_order</c> (revwalk.c:531-615).</summary>
    private CommitList? SortInTopologicalOrder(CommitList? list)
    {
        Comparison<CommitListNode>? queueCmp = null;
        if ((_sorting & GitSortMode.Time) != 0)
        {
            queueCmp = CommitList.TimeCompare;
        }

        var queue = new MinHeap<CommitListNode>(queueCmp, initialCapacity: 8);

        // Reset in-degree to 1 for commits in the list.
        for (CommitList? ll = list; ll is not null; ll = ll.Next)
        {
            ll.Item.InDegree = 1;
        }

        // Count children: increment parent in-degree when parent is in the set.
        for (CommitList? ll = list; ll is not null; ll = ll.Next)
        {
            CommitListNode[]? llParents = ll.Item.Parents;
            for (int i = 0; i < ll.Item.OutDegree; i++)
            {
                Debug.Assert(llParents is not null, "Parents is set (with OutDegree) during ParseAsync");
                CommitListNode parent = llParents[i];
                if (parent.InDegree != 0)
                {
                    parent.InDegree++;
                }
            }
        }

        // Tips: commits with in_degree == 1 (no visible children).
        for (CommitList? ll = list; ll is not null; ll = ll.Next)
        {
            if (ll.Item.InDegree == 1)
            {
                queue.Insert(ll.Item);
            }
        }

        // When not time-sorting, reverse so tips come out in input order.
        if ((_sorting & GitSortMode.Time) == 0)
        {
            queue.Reverse();
        }

        CommitList? newList = null;
        CommitList? newTail = null;

        while (queue.Pop() is { } next)
        {
            CommitListNode[]? nextParents = next.Parents;
            for (int i = 0; i < next.OutDegree; i++)
            {
                Debug.Assert(nextParents is not null, "Parents is set (with OutDegree) during ParseAsync");
                CommitListNode parent = nextParents[i];
                if (parent.InDegree == 0)
                {
                    continue;
                }

                if (--parent.InDegree == 1)
                {
                    queue.Insert(parent);
                }
            }

            // All children of 'next' have been emitted.
            next.InDegree = 0;

            // Append to tail.
            var newNode = CommitList.Create(next, null);
            if (newTail is null)
            {
                newList = newNode;
            }
            else
            {
                newTail.Next = newNode;
            }

            newTail = newNode;
        }

        return newList;
    }

    /// <summary>Matches <c>prepare_walk</c> (revwalk.c:617-698).</summary>
    private async Task PrepareWalkAsync(CancellationToken cancellationToken)
    {
        // If there were no pushes, the walk is already over.
        if (!_didPush)
        {
            return;
        }

        CommitList? commits = null;
        CommitList? commitsLast = null;

        for (CommitList? list = _userInput; list is not null; list = list.Next)
        {
            CommitListNode commit = list.Item;

            // C (revwalk.c:655-657): git_commit_list_parse propagates the ODB
            // error (missing pushed commit → GIT_ENOTFOUND).
            await CommitList.ParseAsync(commit, _repo, oid => LookupCommit(oid), cancellationToken).ConfigureAwait(false);

            if (commit.IsUninteresting)
            {
                MarkParentsUninteresting(commit);
            }

            if ((commit.WalkFlags & CommitListWalkFlags.Seen) == 0)
            {
                var newNode = CommitList.Create(commit, null);
                if (commitsLast is null)
                {
                    commits = newNode;
                }
                else
                {
                    commitsLast.Next = newNode;
                }

                commitsLast = newNode;
                commit.WalkFlags |= CommitListWalkFlags.Seen;
            }
        }

        if (_limited)
        {
            commits = await LimitListAsync(commits, cancellationToken).ConfigureAwait(false);
        }

        if ((_sorting & GitSortMode.Topological) != 0)
        {
            _iteratorTopo = SortInTopologicalOrder(commits);
            _next = NextToposortAsync;
        }
        else if ((_sorting & GitSortMode.Time) != 0)
        {
            for (CommitList? list = commits; list is not null; list = list.Next)
            {
                _enqueue(list.Item);
            }

            _next = NextTimesortAsync;
        }
        else
        {
            _iteratorRand = commits;
            _next = NextUnsortedAsync;
        }

        if ((_sorting & GitSortMode.Reverse) != 0)
        {
            while (await _next(cancellationToken).ConfigureAwait(false) is { } next)
            {
                _iteratorReverse = CommitList.Insert(next, _iteratorReverse);
            }

            _next = NextReverseAsync;
        }

        _walking = true;
    }

    /// <summary>
    /// Enumerates commit OIDs in the configured sort order. Matches repeated
    /// <c>git_revwalk_next</c> calls.
    /// </summary>
    /// <remarks>
    /// <b>Async model:</b> returns
    /// <see cref="IAsyncEnumerable{T}"/> — the walk's <c>prepare_walk</c> +
    /// <c>limit_list</c> passes now await <see cref="CommitList.ParseAsync"/>
    /// (commit-graph/ODB reads), so the iterator itself is async.
    /// </remarks>
    public async IAsyncEnumerable<GitOid> WalkAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_walking)
        {
            await PrepareWalkAsync(cancellationToken).ConfigureAwait(false);
        }

        // C (revwalk.c:782-793): the reset runs only when the walk was actually prepared+iterated (get_next returned ITEROVER). A zero-push walk (prepare_walk
        // → GIT_ITEROVER, revwalk.c:623-627) returns WITHOUT git_revwalk_reset — Sort/_limited survive, so the caller can push and re-walk with the same
        // configuration.
        bool walked = _walking;
        while (await _next(cancellationToken).ConfigureAwait(false) is { } next)
        {
            walked = true;
            cancellationToken.ThrowIfCancellationRequested();
            yield return next.Oid;
        }

        if (walked)
        {
            Reset();
        }
    }

    /// <summary>
    /// Resets the walker to its post-push state: clears all queues and node
    /// walk/graph flags, but retains the pushed/hide roots. Matches
    /// <c>git_revwalk_reset</c> (revwalk.c:801-829).
    /// </summary>
    public void Reset()
    {
        foreach (CommitListNode commit in _commits.Values)
        {
            // Clear walk-state bits except Parsed (commits stay parsed).
            commit.WalkFlags &= CommitListWalkFlags.Parsed;
            commit.InDegree = 0;
            commit.GraphFlags = CommitListGraphFlags.None;
        }

        _iteratorTime.Clear();
        _iteratorTopo = null;
        _iteratorRand = null;
        _iteratorReverse = null;
        _userInput = null;
        _firstParent = false;
        _walking = false;
        _limited = false;
        _didPush = false;
        _sorting = GitSortMode.None;
        _next = NextUnsortedAsync;
        _enqueue = EnqueueUnsorted;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commits.Clear();
    }
}
