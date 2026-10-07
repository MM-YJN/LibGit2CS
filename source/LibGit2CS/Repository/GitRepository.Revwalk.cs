// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Revwalk;
using LibGit2CS.Status;

namespace LibGit2CS.Repository;

/// <content> Describe and commit-graph query operations. Managed port of libgit2's <c>src/libgit2/describe.c</c> and <c>src/libgit2/graph.c</c> (plus the
/// read-only merge-base walk from <c>src/libgit2/merge.c</c> <c>paint_down_to_common</c>) public entry points. </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    // ── Describe ────────────────────────────────────────────────────────

    /// <summary>
    /// Describes <paramref name="commitId"/> relative to its nearest tag.
    /// Matches <c>git_describe_commit</c> + <c>git_describe_format</c> collapsed
    /// into a single call returning the formatted string.
    /// </summary>
    public async Task<string> DescribeAsync(GitOid commitId, GitDescribeOptions? options = null, CancellationToken cancellationToken = default)
    {
        GitDescribeOptions opts = options ?? new GitDescribeOptions();

        // C normalize_options (describe.c:647-648): max_candidates_tags is
        // clamped to GIT_DESCRIBE_DEFAULT_MAX_CANDIDATES_TAGS (10).
        if (opts.MaxCandidateTags > GitDescribeOptions.DefaultMaxCandidateTags)
        {
            opts = opts with { MaxCandidateTags = GitDescribeOptions.DefaultMaxCandidateTags };
        }

        // Peel the committish to a commit (accepts annotated tags pointing at commits).
        // C (describe.c:685-686): the peel error propagates AS-IS — a
        // tree/blob committish yields GIT_EPEEL, not GIT_EINVALID.
        GitObject? input = await Objects.LookupAsync(commitId, cancellationToken).ConfigureAwait(false);
        if (input is null)
        {
            throw new GitException(GitErrorCode.NotFound, $"object {commitId} not found", GitErrorCategory.Object);
        }

        Commit commit = await input.PeelAsync<Commit>(cancellationToken).ConfigureAwait(false);

        // Build the name hashmap from refs.
        Dictionary<GitOid, CommitName> names = await DescribeBuildNameMapAsync(opts, cancellationToken).ConfigureAwait(false);

        if (names.Count == 0 && !opts.ShowCommitOidAsFallback)
        {
            // C (describe.c:693-698): git_error_set(GIT_ERROR_DESCRIBE, ...);
            // error = -1 (the code is -1, not GIT_ENOTFOUND — probe verified).
            throw new GitException(
                GitErrorCode.Error,
                "cannot describe - no reference found, cannot describe anything.",
                GitErrorCategory.Describe);
        }

        DescribeResult result = await DescribeCoreAsync(commit, names, opts, cancellationToken).ConfigureAwait(false);
        return await DescribeFormatAsync(result, opts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Describes the working directory: runs <see cref="DescribeAsync"/>
    /// against HEAD, then checks <see cref="GitStatusList"/> for a dirty workdir.
    /// If dirty, appends <see cref="GitDescribeOptions.DirtySuffix"/>. Matches
    /// <c>git_describe_workdir</c> (describe.c:722-761).
    /// </summary>
    public async Task<string> DescribeWorkdirAsync(GitDescribeOptions? options = null, CancellationToken cancellationToken = default)
    {
        GitDescribeOptions opts = options ?? new GitDescribeOptions();

        // Resolve HEAD.
        GitReference headRef = await Refs.ResolveAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, "HEAD not found", GitErrorCategory.Reference);
        if (headRef is not GitDirectReference direct)
        {
            throw new GitException(GitErrorCode.NotFound, "HEAD is unborn", GitErrorCategory.Reference);
        }

        // Describe HEAD commit first.
        string described = await DescribeAsync(direct.Target, opts, cancellationToken).ConfigureAwait(false);

        // Check if the workdir is dirty. C (describe.c:730,748): status runs with GIT_STATUS_OPTIONS_INIT (flags = 0) — untracked and ignored files are NOT
        // enumerated; only tracked changes make the workdir dirty.
        using GitStatusList status = await GitStatusList.NewAsync(this, new GitStatusOptions(), cancellationToken).ConfigureAwait(false);
        if (status.EntryCount > 0 && !string.IsNullOrEmpty(opts.DirtySuffix))
        {
            return described + opts.DirtySuffix;
        }

        return described;
    }

    // ── Build the tag name map (get_name + add_to_known_names) ──────────

    /// <summary>
    /// Walks all refs and builds a map of peeled-commit-OID → <see cref="CommitName"/>.
    /// Matches the <c>get_name</c> callback + <c>add_to_known_names</c> (describe.c:204-249, 90-127).
    /// </summary>
    private async Task<Dictionary<GitOid, CommitName>> DescribeBuildNameMapAsync(GitDescribeOptions opts, CancellationToken cancellationToken)
    {
        var names = new Dictionary<GitOid, CommitName>();
        bool all = opts.Strategy == GitDescribeStrategy.All;

        await foreach (RefNameKey refName in Refs.ListNameKeysAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            bool isTag = refName.StartsWith("refs/tags/"u8);

            // Reject anything outside refs/tags/ unless --all.
            if (!all && !isTag)
            {
                continue;
            }

            // Accept only tags matching the pattern, if given (describe.c:220-223):
            // C rejects every non-tag ref outright when a pattern is set —
            // `!is_tag` short-circuits before the wildmatch, so a branch is
            // never matched against its full ref name.
            if (opts.Pattern is { } pattern)
            {
                if (!isTag || !WildMatch.IsMatch(Encoding.UTF8.GetBytes(pattern), refName.Span[GitReferences.RefsTagsDir.Length..], WildMatchFlags.None))
                {
                    continue;
                }
            }

            (GitOid peeled, GitOid sha, bool isAnnotated) = await DescribeRetrievePeeledAsync(refName, cancellationToken).ConfigureAwait(false);

            // prio: annotated=2, lightweight tag=1, other=0.
            int prio = isAnnotated ? 2 : isTag ? 1 : 0;

            string displayPath = all
                ? Encoding.UTF8.GetString(refName.Span[GitReferences.RefsDir.Length..])
                : Encoding.UTF8.GetString(refName.Span[GitReferences.RefsTagsDir.Length..]);

            await DescribeAddToKnownNumbersAsync(names, displayPath, peeled, prio, sha, cancellationToken).ConfigureAwait(false);
        }

        return names;
    }

    /// <summary>Matches <c>retrieve_peeled_tag_or_object_oid</c> (describe.c:135-163).</summary>
    private async Task<(GitOid peeled, GitOid sha, bool isAnnotated)> DescribeRetrievePeeledAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        GitReference? resolved = await Refs.ResolveAsync(refName, cancellationToken).ConfigureAwait(false);
        if (resolved is not GitDirectReference direct)
        {
            // C (describe.c:145-146): git_reference_lookup_resolved fails on a
            // broken/missing ref — get_name propagates it and the whole
            // describe aborts (GIT_ENOTFOUND).
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{refName}' not found",
                GitErrorCategory.Reference);
        }

        GitOid sha = direct.Target;

        // C git_reference_peel(ref, GIT_OBJECT_ANY) (refs.c): when the ref has
        // a pre-peeled OID (the "^<oid>" line in packed-refs) and the target is
        // not a tag, the PEELED oid is looked up; otherwise the target oid.
        // This matters for parity: an annotated tag whose TAG OBJECT is missing
        // still reaches the name map when the packed peel is available, and the
        // failure then surfaces at display_name (describe.c:328-335) instead of
        // here.
        GitObject? target;
        if (direct.Peel is { } peel && !peel.IsZero)
        {
            target = await Objects.LookupAsync(peel, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            target = await Objects.LookupAsync(sha, cancellationToken).ConfigureAwait(false);
        }

        if (target is null)
        {
            // C (refs.c peel_error): the lookup failure is remapped to
            // GIT_ERROR_INVALID "the reference '%s' cannot be peeled - Cannot
            // retrieve reference target" while the ORIGINAL code (GIT_ENOTFOUND)
            // is returned (probe verified).
            throw new GitException(
                GitErrorCode.NotFound,
                $"the reference '{refName}' cannot be peeled - Cannot retrieve reference target",
                GitErrorCategory.Invalid);
        }

        if (target is GitTag tag)
        {
            // Annotated tag: peel to ultimate non-tag target.
            GitObject peeled = tag;
            for (int depth = 0; depth < 50 && peeled is GitTag t; depth++)
            {
                peeled = await Objects.LookupAsync(t.Target, cancellationToken).ConfigureAwait(false)
                    ?? throw new GitException(GitErrorCode.NotFound, $"tag target {t.Target} not found", GitErrorCategory.Object);
            }

            return (peeled.Id, sha, isAnnotated: true);
        }

        // Lightweight tag / branch / other: annotated iff the peeled target
        // differs from the ref target (describe.c:154-157).
        return (target.Id, sha, isAnnotated: !target.Id.Equals(sha));
    }

    /// <summary>Matches <c>add_to_known_names</c> + <c>replace_name</c> (describe.c:53-127).</summary>
    private async Task DescribeAddToKnownNumbersAsync(Dictionary<GitOid, CommitName> names, string path, GitOid peeled, int prio, GitOid sha, CancellationToken cancellationToken)
    {
        if (names.TryGetValue(peeled, out CommitName? existing))
        {
            // Collision: keep the higher-priority / newer tag.
            if (!await DescribeShouldReplaceAsync(existing, prio, sha, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            names[peeled] = await DescribeMakeNameAsync(path, peeled, prio, sha, cancellationToken).ConfigureAwait(false);
            return;
        }

        names[peeled] = await DescribeMakeNameAsync(path, peeled, prio, sha, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Matches <c>replace_name</c> (describe.c:53-92). Returns true if the new entry should replace.</summary>
    private async Task<bool> DescribeShouldReplaceAsync(CommitName existing, int prio, GitOid sha, CancellationToken cancellationToken)
    {
        if (existing.Priority < prio)
        {
            return true;
        }

        if (existing.Priority != 2 || prio != 2)
        {
            return false;
        }

        // Both annotated: select by tagger date (missing tagger counts as 0).
        GitTag? existingTag = await DescribeLookupTagAsync(existing.Sha, cancellationToken).ConfigureAwait(false);
        if (existingTag is null)
        {
            return true; // can't load existing tag's object → replace
        }

        GitTag? newTag = await Objects.LookupAsync<GitTag>(sha, cancellationToken).ConfigureAwait(false);
        if (newTag is null)
        {
            return false; // can't load new tag's object → keep existing
        }

        long eTime = existingTag.Tagger?.When.Seconds ?? 0;
        long nTime = newTag.Tagger?.When.Seconds ?? 0;
        return eTime < nTime;
    }

    private async Task<CommitName> DescribeMakeNameAsync(string path, GitOid peeled, int prio, GitOid sha, CancellationToken cancellationToken)
    {
        return new CommitName
        {
            Path = path,
            Peeled = peeled,
            Sha = sha,
            Priority = prio,
            Tag = prio == 2 ? await DescribeLookupTagAsync(sha, cancellationToken).ConfigureAwait(false) : null,
        };
    }

    private async Task<GitTag?> DescribeLookupTagAsync(GitOid sha, CancellationToken cancellationToken)
        => await Objects.LookupAsync<GitTag>(sha, cancellationToken).ConfigureAwait(false);

    // ── Core describe algorithm (describe.c:429-632) ────────────────────

    /// <summary>The SEEN bit (bit 0) on the graph flags, matching describe.c:290.</summary>
    private const CommitListGraphFlags DescribeSeen = (CommitListGraphFlags)1;

    /// <summary>Matches <c>describe</c> (describe.c:429-632).</summary>
    private async Task<DescribeResult> DescribeCoreAsync(Commit commit, Dictionary<GitOid, CommitName> names, GitDescribeOptions opts, CancellationToken cancellationToken)
    {
        var result = new DescribeResult { CommitId = commit.Id };
        bool all = opts.Strategy == GitDescribeStrategy.All;
        bool tags = opts.Strategy == GitDescribeStrategy.Tags;

        // Exact-match shortcut.
        if (names.TryGetValue(commit.Id, out CommitName? exact) && (tags || all || exact.Priority == 2))
        {
            result.ExactMatch = true;
            result.Name = exact;
            return result;
        }

        if (opts.MaxCandidateTags == 0)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"cannot describe - no tag exactly matches '{commit.Id}'",
                GitErrorCategory.Describe);
        }

        using var walk = new GitRevWalker(this);
        CommitListNode cmit = walk.LookupCommit(commit.Id);
        await CommitList.ParseAsync(cmit, this, oid => walk.LookupCommit(oid), cancellationToken).ConfigureAwait(false);
        cmit.GraphFlags |= DescribeSeen;

        var list = new MinHeap<CommitListNode>(CommitList.TimeCompare, initialCapacity: 2);
        list.Insert(cmit);

        var allMatches = new List<PossibleTag>();
        int matchCnt = 0;
        int annotatedCnt = 0;
        int unannotatedCnt = 0;
        long seenCommits = 0;
        CommitListNode? gaveUpOn = null;

        while (list.Count > 0)
        {
            CommitListNode? popped = list.Pop();
            Debug.Assert(popped is not null, "list.Pop() returns non-null when Count > 0");
            CommitListNode c = popped;
            seenCommits++;

            if (names.TryGetValue(c.Oid, out CommitName? n))
            {
                if (!tags && !all && n.Priority < 2)
                {
                    unannotatedCnt++;
                }
                else if (matchCnt < opts.MaxCandidateTags)
                {
                    matchCnt++;
                    var t = new PossibleTag
                    {
                        Name = n,
                        Depth = (int)(seenCommits - 1),
                        FlagWithin = (CommitListGraphFlags)(1u << matchCnt),
                        FoundOrder = matchCnt,
                    };
                    allMatches.Add(t);
                    c.GraphFlags |= t.FlagWithin;
                    if (n.Priority == 2)
                    {
                        annotatedCnt++;
                    }
                }
                else
                {
                    gaveUpOn = c;
                    break;
                }
            }

            // Increment depth for candidates that don't yet include this commit.
            foreach (PossibleTag t in allMatches)
            {
                if ((c.GraphFlags & t.FlagWithin) == 0)
                {
                    t.Depth++;
                }
            }

            // Early termination: found annotated tags and queue empty.
            if (annotatedCnt > 0 && list.Count == 0)
            {
                break;
            }

            // Enqueue parents.
            CommitListNode[]? cParents = c.Parents;
            for (int i = 0; i < c.OutDegree; i++)
            {
                Debug.Assert(cParents is not null, "Parents is set (with OutDegree) during ParseAsync");
                CommitListNode p = cParents[i];
                await CommitList.ParseAsync(p, this, oid => walk.LookupCommit(oid), cancellationToken).ConfigureAwait(false);
                if ((p.GraphFlags & DescribeSeen) == 0)
                {
                    list.Insert(p);
                }

                p.GraphFlags |= c.GraphFlags;

                if (opts.OnlyFollowFirstParent)
                {
                    break;
                }
            }
        }

        if (matchCnt == 0)
        {
            if (opts.ShowCommitOidAsFallback)
            {
                result.FallbackToId = true;
                return result;
            }

            if (unannotatedCnt > 0)
            {
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"cannot describe - no annotated tags can describe '{commit.Id}'; however, there were unannotated tags.",
                    GitErrorCategory.Describe);
            }

            throw new GitException(
                GitErrorCode.NotFound,
                $"cannot describe - no tags can describe '{commit.Id}'.",
                GitErrorCategory.Describe);
        }

        // Sort by (depth, found_order); the first is best.
        allMatches.Sort(DescribeComparePt);
        PossibleTag best = allMatches[0];

        if (gaveUpOn is not null)
        {
            list.Insert(gaveUpOn);
            seenCommits--;
        }

        seenCommits += await DescribeFinishDepthComputationAsync(walk, list, best, cancellationToken).ConfigureAwait(false);

        result.Tag = best;
        result.CommitId = cmit.Oid;
        return result;
    }

    /// <summary>Matches <c>finish_depth_computation</c> (describe.c:292-325).</summary>
    private async Task<int> DescribeFinishDepthComputationAsync(GitRevWalker walk, MinHeap<CommitListNode> list, PossibleTag best, CancellationToken cancellationToken)
    {
        int seen = 0;

        while (list.Count > 0)
        {
            CommitListNode? popped = list.Pop();
            Debug.Assert(popped is not null, "list.Pop() returns non-null when Count > 0");
            CommitListNode c = popped;
            seen++;

            if ((c.GraphFlags & best.FlagWithin) != 0)
            {
                // Check if all remaining commits are also "within" best.
                bool allWithin = true;
                for (int i = 0; i < list.Count; i++)
                {
                    CommitListNode? item = list[i];
                    Debug.Assert(item is not null, "list[i] is non-null within Count range");
                    if ((item.GraphFlags & best.FlagWithin) == 0)
                    {
                        allWithin = false;
                        break;
                    }
                }

                if (allWithin)
                {
                    break;
                }
            }
            else
            {
                best.Depth++;
            }

            CommitListNode[]? cParents = c.Parents;
            for (int i = 0; i < c.OutDegree; i++)
            {
                Debug.Assert(cParents is not null, "Parents is set (with OutDegree) during ParseAsync");
                CommitListNode p = cParents[i];
                await CommitList.ParseAsync(p, this, oid => walk.LookupCommit(oid), cancellationToken).ConfigureAwait(false);
                if ((p.GraphFlags & DescribeSeen) == 0)
                {
                    list.Insert(p);
                }

                p.GraphFlags |= c.GraphFlags;
            }
        }

        return seen;
    }

    /// <summary>Matches <c>compare_pt</c> (describe.c:279-288).</summary>
    private static int DescribeComparePt(PossibleTag a, PossibleTag b)
    {
        int c = a.Depth.CompareTo(b.Depth);
        return c != 0 ? c : a.FoundOrder.CompareTo(b.FoundOrder);
    }

    // ── Formatting (describe.c:776-855) ────────────────────────────────

    /// <summary>Matches <c>git_describe__format</c> (describe.c:776-855).</summary>
    private async Task<string> DescribeFormatAsync(DescribeResult result, GitDescribeOptions opts, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        // C (describe.c:792-797): always_use_long_format with a zero
        // abbreviated_size is an error. The message reproduces C's typo
        // (missing space before "'abbreviated_size'").
        if (opts.AlwaysUseLongFormat && opts.MinimumAbbreviatedSize == 0)
        {
            throw new GitException(
                GitErrorCode.Error,
                "cannot describe - 'always_use_long_format' is incompatible with a zero'abbreviated_size'",
                GitErrorCategory.Describe);
        }

        if (result.ExactMatch)
        {
            CommitName? exactName = result.Name;
            Debug.Assert(exactName is not null, "result.Name is set when ExactMatch is true");
            sb.Append(await DescribeDisplayNameAsync(exactName, cancellationToken).ConfigureAwait(false));
            if (opts.AlwaysUseLongFormat)
            {
                GitOid id = exactName.Tag is { } tag ? tag.Target : result.CommitId;
                await DescribeShowSuffixAsync(sb, 0, id, opts.MinimumAbbreviatedSize, cancellationToken).ConfigureAwait(false);
            }

            if (result.Dirty && !string.IsNullOrEmpty(opts.DirtySuffix))
            {
                sb.Append(opts.DirtySuffix);
            }

            return sb.ToString();
        }

        if (result.FallbackToId)
        {
            int size = await DescribeFindUniqueAbbrevSizeAsync(result.CommitId, opts.MinimumAbbreviatedSize, cancellationToken).ConfigureAwait(false);
            sb.Append(result.CommitId.ToString()[..size]);

            if (result.Dirty && !string.IsNullOrEmpty(opts.DirtySuffix))
            {
                sb.Append(opts.DirtySuffix);
            }

            return sb.ToString();
        }

        // Normal match: tag + -depth-gabbrev.
        PossibleTag? bestTag = result.Tag;
        Debug.Assert(bestTag is not null, "result.Tag is set on normal match");
        sb.Append(await DescribeDisplayNameAsync(bestTag.Name, cancellationToken).ConfigureAwait(false));
        if (opts.MinimumAbbreviatedSize > 0)
        {
            await DescribeShowSuffixAsync(sb, bestTag.Depth, result.CommitId, opts.MinimumAbbreviatedSize, cancellationToken).ConfigureAwait(false);
        }

        if (result.Dirty && !string.IsNullOrEmpty(opts.DirtySuffix))
        {
            sb.Append(opts.DirtySuffix);
        }

        return sb.ToString();
    }

    /// <summary>Matches <c>display_name</c> (describe.c:328-357).</summary>
    private async Task<string> DescribeDisplayNameAsync(CommitName name, CancellationToken cancellationToken)
    {
        if (name.Priority == 2 && name.Tag is null)
        {
            // C (describe.c:330-335): the tag object is loaded lazily here; a
            // missing annotated tag object fails the whole describe with
            // GIT_ERROR_TAG "annotated tag '%s' not available" (-1).
            name.Tag = await DescribeLookupTagAsync(name.Sha, cancellationToken).ConfigureAwait(false);
            if (name.Tag is null)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"annotated tag '{name.Path}' not available",
                    GitErrorCategory.Tag);
            }
        }

        if (name.Tag is { } tag)
        {
            // C (describe.c:337-341): "annotated tag '%s' has no embedded
            // name" — unreachable in practice because git_tag_parse rejects a
            // tag without a name field, but kept for parity.
            if (tag.Name.Length == 0)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"annotated tag '{name.Path}' has no embedded name",
                    GitErrorCategory.Tag);
            }

            return tag.Name;
        }

        return name.Path;
    }

    /// <summary>Matches <c>show_suffix</c> (describe.c:396-417).</summary>
    private async Task DescribeShowSuffixAsync(StringBuilder sb, int depth, GitOid id, int abbrevSize, CancellationToken cancellationToken)
    {
        int size = await DescribeFindUniqueAbbrevSizeAsync(id, abbrevSize, cancellationToken).ConfigureAwait(false);
        sb.Append('-').Append(depth).Append("-g");
        sb.Append(id.ToString()[..size]);
    }

    /// <summary>Matches <c>find_unique_abbrev_size</c> (describe.c:331-361).</summary>
    private async Task<int> DescribeFindUniqueAbbrevSizeAsync(GitOid id, int abbreviatedSize, CancellationToken cancellationToken)
    {
        int hexSize = GitOid.HexSizeFor(ObjectFormat);
        int size = abbreviatedSize;

        while (size < hexSize)
        {
            // Build a prefix OID of `size` hex chars.
            string hex = id.ToString()[..size];
            if (!GitOid.TryParse(hex, ObjectFormat, out GitOid prefix))
            {
                size++;
                continue;
            }

            try
            {
                (bool found, _) = await Objects.ExistsPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
                if (found)
                {
                    return size;
                }
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.Ambiguous)
            {
                // Ambiguous: try a longer prefix.
            }

            size++;
        }

        return hexSize;
    }

    // ── Commit-graph queries ────────────────────────────────────────────

    /// <summary>
    /// Counts how many commits are reachable from <paramref name="local"/> but
    /// not <paramref name="upstream"/> (<c>ahead</c>), and vice
    /// versa (<c>behind</c>). Matches <c>git_graph_ahead_behind</c>
    /// (graph.c:146-175).
    /// </summary>
    public async Task<(int ahead, int behind)> AheadBehindAsync(GitOid local, GitOid upstream, CancellationToken cancellationToken = default)
    {
        using var walk = new GitRevWalker(this);
        CommitListNode commitU = walk.LookupCommit(upstream);
        CommitListNode commitL = walk.LookupCommit(local);

        await RevwalkMarkParentsAsync(walk, commitL, commitU, cancellationToken).ConfigureAwait(false);
        return RevwalkAheadBehindCount(commitL, commitU);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="commit"/> is a descendant of
    /// <paramref name="ancestor"/>. A commit is NOT a descendant of itself.
    /// Matches <c>git_graph_descendant_of</c> (graph.c:177-183).
    /// </summary>
    public async Task<bool> DescendantOfAsync(GitOid commit, GitOid ancestor, CancellationToken cancellationToken = default)
    {
        if (commit.Equals(ancestor))
        {
            return false;
        }

        return await RevwalkReachableFromAnyAsync(ancestor, [commit], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="commitId"/> is reachable from any
    /// of the <paramref name="descendants"/> (i.e. some descendant's history
    /// contains <paramref name="commitId"/>). Matches
    /// <c>git_graph_reachable_from_any</c> (graph.c:185-249). Answers the
    /// "ancestor of any of these commits" question in a single generation-pruned
    /// walk, unlike looping <see cref="AheadBehindAsync"/> (each call is a full
    /// <c>mark_parents</c> traversal).
    /// </summary>
    public Task<bool> ReachableFromAnyAsync(GitOid commitId, IReadOnlyList<GitOid> descendants, CancellationToken cancellationToken = default)
        => RevwalkReachableFromAnyAsync(commitId, descendants, cancellationToken);

    /// <summary>
    /// Returns <c>true</c> if <paramref name="commitId"/> is reachable from any
    /// of the <paramref name="descendants"/> (i.e. some descendant's history
    /// contains <paramref name="commitId"/>). Matches
    /// <c>git_graph_reachable_from_any</c> (graph.c:185-249).
    /// </summary>
    private async Task<bool> RevwalkReachableFromAnyAsync(GitOid commitId, IReadOnlyList<GitOid> descendants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descendants);

        if (descendants.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < descendants.Count; i++)
        {
            if (commitId.Equals(descendants[i]))
            {
                return true;
            }
        }

        using var walk = new GitRevWalker(this);
        var twos = new List<CommitListNode>(descendants.Count);
        uint minimumGeneration = uint.MaxValue;

        foreach (GitOid descendant in descendants)
        {
            CommitListNode node = walk.LookupCommit(descendant);
            twos.Add(node);
            if (node.Generation < minimumGeneration)
            {
                minimumGeneration = node.Generation;
            }
        }

        CommitListNode one = walk.LookupCommit(commitId);
        if (one.Generation < minimumGeneration)
        {
            minimumGeneration = one.Generation;
        }

        // Matches git_commit_list_parse(walk, one) (merge.c:541): a missing
        // commit_id is an error, not a "no merge base" answer. C's
        // git_graph_reachable_from_any on_error returns -1 (GIT_ERROR) with
        // the ODB message.
        await ParseForGraphAsync(walk, one, cancellationToken).ConfigureAwait(false);

        List<CommitListNode> mergeBases = await RevwalkPaintDownToCommonAsync(walk, one, twos, minimumGeneration, cancellationToken).ConfigureAwait(false);

        // commitId is reachable from a descendant iff commitId is itself a merge base.
        foreach (CommitListNode mb in mergeBases)
        {
            if (mb.Oid.Equals(commitId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses <paramref name="node"/> for the graph/merge walk, mapping the
    /// underlying <see cref="CommitList.ParseAsync"/> error to the bare -1
    /// (GIT_ERROR) convention used at the graph/merge public boundary. C:
    /// mark_parents on_error (graph.c:102-105) and git_merge__bases_many's
    /// initial parse (merge.c:541-542) return -1 with the parse error's
    /// message still in the error cache.
    /// </summary>
    private static async Task ParseForGraphAsync(GitRevWalker walk, CommitListNode node, CancellationToken cancellationToken)
    {
        try
        {
            await CommitList.ParseAsync(node, walk.Repository, oid => walk.LookupCommit(oid), cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            throw new GitException(GitErrorCode.Error, ex.Message, ex.Category);
        }
    }

    // ── mark_parents (graph.c:33-105) ───────────────────────────────────

    /// <summary>
    /// Paints all ancestors of <paramref name="one"/> with <see cref="CommitListGraphFlags.Parent1"/>,
    /// all ancestors of <paramref name="two"/> with <see cref="CommitListGraphFlags.Parent2"/>.
    /// Commits reachable from both get <see cref="CommitListGraphFlags.Result"/> (merge base) and
    /// their parents are marked <see cref="CommitListGraphFlags.Stale"/> to stop expansion.
    /// </summary>
    private static async Task RevwalkMarkParentsAsync(GitRevWalker walk, CommitListNode one, CommitListNode two, CancellationToken cancellationToken)
    {
        // If the commit is repeated, we have our merge base already.
        if (ReferenceEquals(one, two))
        {
            one.GraphFlags |= CommitListGraphFlags.Parent1 | CommitListGraphFlags.Parent2 | CommitListGraphFlags.Result;
            return;
        }

        var list = new MinHeap<CommitListNode>(CommitList.GenerationCompare, initialCapacity: 2);
        CommitList? roots = null;

        // Matches git_commit_list_parse(walk, one) (graph.c:49-50): a missing
        // commit aborts the whole computation with the ODB error; C's
        // mark_parents on_error returns a bare -1 (GIT_ERROR).
        await ParseForGraphAsync(walk, one, cancellationToken).ConfigureAwait(false);

        one.GraphFlags |= CommitListGraphFlags.Parent1;
        list.Insert(one);

        // Matches git_commit_list_parse(walk, two) (graph.c:55-56).
        await ParseForGraphAsync(walk, two, cancellationToken).ConfigureAwait(false);

        two.GraphFlags |= CommitListGraphFlags.Parent2;
        list.Insert(two);

        // As long as there are non-STALE commits.
        while (RevwalkInteresting(list, roots))
        {
            CommitListNode? commit = list.Pop();
            if (commit is null)
            {
                break;
            }

            CommitListGraphFlags flags = commit.GraphFlags & (CommitListGraphFlags.Parent1 | CommitListGraphFlags.Parent2 | CommitListGraphFlags.Stale);
            if (flags == (CommitListGraphFlags.Parent1 | CommitListGraphFlags.Parent2))
            {
                if ((commit.GraphFlags & CommitListGraphFlags.Result) == 0)
                {
                    commit.GraphFlags |= CommitListGraphFlags.Result;
                }

                // We mark the parents of a merge base stale.
                flags |= CommitListGraphFlags.Stale;
            }

            CommitListNode[]? commitParents = commit.Parents;
            for (int i = 0; i < commit.OutDegree; i++)
            {
                Debug.Assert(commitParents is not null, "Parents is set (with OutDegree) during ParseAsync");
                CommitListNode p = commitParents[i];
                if ((p.GraphFlags & flags) == flags)
                {
                    continue;
                }

                // Matches git_commit_list_parse(walk, p) (graph.c:82-83): a
                // missing parent aborts the whole computation.
                await ParseForGraphAsync(walk, p, cancellationToken).ConfigureAwait(false);

                p.GraphFlags |= flags;
                list.Insert(p);
            }

            // Keep track of root commits, to make sure the path gets marked.
            if (commit.OutDegree == 0)
            {
                roots = CommitList.Insert(commit, roots);
            }
        }
    }

    /// <summary>Matches <c>interesting</c> (graph.c:14-31).</summary>
    private static bool RevwalkInteresting(MinHeap<CommitListNode> list, CommitList? roots)
    {
        for (int i = 0; i < list.Count; i++)
        {
            CommitListNode? item = list[i];
            Debug.Assert(item is not null, "list[i] is non-null within Count range");
            if ((item.GraphFlags & CommitListGraphFlags.Stale) == 0)
            {
                return true;
            }
        }

        for (CommitList? r = roots; r is not null; r = r.Next)
        {
            if ((r.Item.GraphFlags & CommitListGraphFlags.Stale) == 0)
            {
                return true;
            }
        }

        return false;
    }

    // ── ahead_behind (graph.c:108-144) ──────────────────────────────────

    /// <summary>
    /// Counts commits unique to each side, skipping merge bases (commits with
    /// both PARENT1 and PARENT2). Matches <c>ahead_behind</c> (graph.c:108-144).
    /// Requires <see cref="RevwalkMarkParentsAsync"/> to have been called first.
    /// </summary>
    private static (int ahead, int behind) RevwalkAheadBehindCount(CommitListNode one, CommitListNode two)
    {
        var pq = new MinHeap<CommitListNode>(CommitList.TimeCompare, initialCapacity: 2);
        pq.Insert(one);
        pq.Insert(two);

        int ahead = 0;
        int behind = 0;

        while (pq.Pop() is { } commit)
        {
            if ((commit.GraphFlags & CommitListGraphFlags.Result) != 0)
            {
                continue;
            }

            if ((commit.GraphFlags & (CommitListGraphFlags.Parent1 | CommitListGraphFlags.Parent2))
                == (CommitListGraphFlags.Parent1 | CommitListGraphFlags.Parent2))
            {
                continue;
            }
            else if ((commit.GraphFlags & CommitListGraphFlags.Parent1) != 0)
            {
                ahead++;
            }
            else if ((commit.GraphFlags & CommitListGraphFlags.Parent2) != 0)
            {
                behind++;
            }

            CommitListNode[]? commitParents = commit.Parents;
            for (int i = 0; i < commit.OutDegree; i++)
            {
                Debug.Assert(commitParents is not null, "Parents is set (with OutDegree) during ParseAsync");
                pq.Insert(commitParents[i]);
            }

            commit.GraphFlags |= CommitListGraphFlags.Result;
        }

        return (ahead, behind);
    }

    // ── paint_down_to_common (merge.c:379-447) ──────────────────────────

    /// <summary>
    /// Finds the merge bases of <paramref name="one"/> and the commits in
    /// <paramref name="twos"/> by painting the commit graph with
    /// PARENT1/PARENT2/RESULT/STALE flags. Matches <c>paint_down_to_common</c>
    /// (merge.c:379-447). Generation-number accelerated when available.
    /// </summary>
    /// <param name="walk">The walker providing the commit-node cache.</param>
    /// <param name="one">The first commit.</param>
    /// <param name="twos">The other commits.</param>
    /// <param name="minimumGeneration">Don't explore commits older than this
    /// generation (optimization; pass 0 to disable).</param>
    /// <returns>The merge-base commit nodes (those that got RESULT without STALE).</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task<List<CommitListNode>> RevwalkPaintDownToCommonAsync(GitRevWalker walk, CommitListNode one, List<CommitListNode> twos, uint minimumGeneration, CancellationToken cancellationToken)
    {
        var list = new MinHeap<CommitListNode>(CommitList.GenerationCompare, initialCapacity: Math.Max(twos.Count * 2, 2));
        var result = new List<CommitListNode>();

        one.GraphFlags |= CommitListGraphFlags.Parent1;
        list.Insert(one);

        foreach (CommitListNode two in twos)
        {
            // Matches git_commit_list_parse(walk, two) (merge.c:401-402): a
            // missing descendant is an error, not a "no merge base" answer.
            // C's merge_bases/many on_error return a bare -1 (GIT_ERROR) with
            // the ODB's "object not found - no match for id (...)" message.
            await ParseForGraphAsync(walk, two, cancellationToken).ConfigureAwait(false);

            two.GraphFlags |= CommitListGraphFlags.Parent2;
            list.Insert(two);
        }

        // As long as there are non-STALE commits.
        while (RevwalkInterestingSimple(list))
        {
            CommitListNode? commit = list.Pop();
            if (commit is null)
            {
                break;
            }

            CommitListGraphFlags flags = commit.GraphFlags & (CommitListGraphFlags.Parent1 | CommitListGraphFlags.Parent2 | CommitListGraphFlags.Stale);
            if (flags == (CommitListGraphFlags.Parent1 | CommitListGraphFlags.Parent2))
            {
                if ((commit.GraphFlags & CommitListGraphFlags.Result) == 0)
                {
                    commit.GraphFlags |= CommitListGraphFlags.Result;
                    result.Add(commit);
                }

                // We mark the parents of a merge base stale.
                flags |= CommitListGraphFlags.Stale;
            }

            CommitListNode[]? commitParents = commit.Parents;
            for (int i = 0; i < commit.OutDegree; i++)
            {
                Debug.Assert(commitParents is not null, "Parents is set (with OutDegree) during ParseAsync");
                CommitListNode p = commitParents[i];
                if ((p.GraphFlags & flags) == flags)
                {
                    continue;
                }

                if (p.Generation < minimumGeneration)
                {
                    continue;
                }

                // Matches git_commit_list_parse(walk, p) (merge.c:435-436): a
                // missing parent is an error, not a "no merge base" answer.
                await ParseForGraphAsync(walk, p, cancellationToken).ConfigureAwait(false);

                p.GraphFlags |= flags;
                list.Insert(p);
            }
        }

        return result;
    }

    /// <summary>The simple <c>interesting</c> from merge.c (no roots list).</summary>
    private static bool RevwalkInterestingSimple(MinHeap<CommitListNode> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            CommitListNode? item = list[i];
            Debug.Assert(item is not null, "list[i] is non-null within Count range");
            if ((item.GraphFlags & CommitListGraphFlags.Stale) == 0)
            {
                return true;
            }
        }

        return false;
    }

    // ── Commit-graph presence/write facades (LibGit2CS additions) ──────

    /// <summary>
    /// Whether a <c>.git/objects/info/commit-graph</c> file is present and
    /// readable for this repository. A boolean gate over the internal
    /// ODB-owned commit-graph cache (the <see cref="CommitGraph"/> type itself
    /// is public but the ODB getter stays internal). Returns <c>false</c> when
    /// the file is absent or corrupt (the ODB lazy-load catches
    /// <see cref="GitException"/> and returns null, matching C's silent
    /// revwalk fallback to ODB reads).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No C counterpart:</b> libgit2 exposes no repository-level
    /// commit-graph presence check; this is a LibGit2CS convenience. Consumers
    /// deciding between <see cref="GitSortMode.None"/> (lazy) and
    /// <see cref="GitSortMode.Topological"/> | <see cref="GitSortMode.Time"/>
    /// (eager) should call this — the eager walk is O(1)-per-commit only when
    /// a graph is present (the <c>CommitList.ParseAsync</c> fast path). The
    /// revwalk itself consults the same cached instance regardless.
    /// </para>
    /// <para>
    /// <b>Behavioral note:</b> unlike <see cref="CommitGraph.OpenAsync"/>
    /// (which throws on a corrupt file, mirroring C's public
    /// <c>git_commit_graph_open</c>), this returns <c>false</c> for a corrupt
    /// file — presence is a gate, not a validation. The result is cached on
    /// the ODB for the life of this <see cref="GitRepository"/> instance;
    /// <see cref="WriteCommitGraphAsync"/> refreshes the cache so a subsequent
    /// call on the same handle sees the new file.
    /// </para>
    /// </remarks>
    public async Task<bool> HasCommitGraphAsync(CancellationToken cancellationToken = default)
        => await Objects.GetCommitGraphAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Writes <c>.git/objects/info/commit-graph</c> covering all commits
    /// reachable from <c>refs/*</c> (equivalent to
    /// <c>git commit-graph write --reachable</c>), atomically via a
    /// <c>.lock</c> file. Convenience facade over the public
    /// <see cref="CommitGraphWriter"/> for the common case; callers needing
    /// finer control (HEAD-only scope, pack-index input, in-memory dump) use
    /// the writer directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No C counterpart:</b> libgit2 exposes no repository-level
    /// commit-graph write; this is a LibGit2CS convenience sequencing
    /// <see cref="NewRevWalker"/> + <see cref="GitRevWalker.PushGlobAsync"/>
    /// + <see cref="CommitGraphWriter.AddRevwalkAsync"/> +
    /// <see cref="CommitGraphWriter.CommitAsync"/> — the same recipe as
    /// libgit2's own equivalence test (tests/libgit2/graph/commitgraph.c,
    /// <c>test_graph_commitgraph__writer</c>, which pushes <c>refs/*</c>).
    /// </para>
    /// <para>
    /// After a successful write the ODB's cached graph is reset so the next
    /// <see cref="HasCommitGraphAsync"/> on the same repository handle (and
    /// subsequent revwalks through it) re-read the file from disk.
    /// </para>
    /// </remarks>
    public async Task WriteCommitGraphAsync(CancellationToken cancellationToken = default)
    {
        string infoDir = System.IO.Path.Join(Path, "objects", "info");
        Directory.CreateDirectory(infoDir);

        using GitRevWalker walker = NewRevWalker();
        await walker.PushGlobAsync("refs/*", cancellationToken).ConfigureAwait(false);

        using CommitGraphWriter graphWriter = new(infoDir, new CommitGraphWriterOptions(ObjectFormat));
        await graphWriter.AddRevwalkAsync(walker, cancellationToken).ConfigureAwait(false);
        await graphWriter.CommitAsync(cancellationToken).ConfigureAwait(false);

        Objects.ResetCommitGraphCache();
    }

    // ── Internal data structures ───────────────────────────────────────

    /// <summary>Matches <c>struct commit_name</c> (describe.c:29-37).</summary>
    private sealed class CommitName
    {
        public string Path { get; set; } = string.Empty;
        public GitOid Peeled { get; set; }
        public GitOid Sha { get; set; }
        public int Priority { get; set; }
        public GitTag? Tag { get; set; }
    }

    /// <summary>Matches <c>struct possible_tag</c> (describe.c:251-256).</summary>
    private sealed class PossibleTag
    {
        public required CommitName Name { get; set; }
        public int Depth { get; set; }
        public int FoundOrder { get; set; }
        public CommitListGraphFlags FlagWithin { get; set; }
    }

    /// <summary>Matches <c>struct git_describe_result</c> (describe.c:160-167).</summary>
    private sealed class DescribeResult
    {
        public bool ExactMatch { get; set; }
        public bool FallbackToId { get; set; }
        public GitOid CommitId { get; set; }
        public CommitName? Name { get; set; }
        public PossibleTag? Tag { get; set; }
        public bool Dirty { get; set; }
    }
}
