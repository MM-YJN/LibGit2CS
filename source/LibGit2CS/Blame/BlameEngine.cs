// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using Xdiff;
using Xdiff.Emit;

namespace LibGit2CS.Blame;

/// <summary>
/// Core blame algorithm. Managed port of libgit2's <c>src/libgit2/blame_git.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm: start with all lines blamed on the newest commit. Repeatedly
/// find an "unguilty" suspect, pass its blame to its parents by diffing the
/// suspect's blob against each parent's blob. Lines that match the parent are
/// reassigned to the parent. Continue until no unguilty suspects remain. Then
/// coalesce adjacent entries with the same suspect.
/// </para>
/// <para>
/// <b>XDiff integration</b>: <see cref="DiffHunks"/> calls
/// <c>Xdiff.Diff.Compute</c> with a <see cref="BlameChunkCbDataSink"/> that fires
/// <see cref="BlameChunk"/> per hunk using <c>(OldStart, OldCount, NewStart,
/// NewCount)</c> — exactly the hunk-level data the blame algorithm needs (no
/// per-line callbacks required, matching C's <c>hunk_func</c>).
/// </para>
/// <para>
/// <b>Full diff integration</b>: <see cref="FindOriginAsync"/> uses
/// <see cref="GitDiff.TreeToTreeAsync"/> + <see cref="GitDiff.FindSimilarAsync"/> to detect
/// renames — the one place blame uses the full libgit2 diff pipeline.
/// </para>
/// </remarks>
internal static class BlameEngine
{
    /// <summary>
    /// The main blame algorithm loop. Matches <c>git_blame__like_git</c>
    /// (blame_git.c:640-677).
    /// </summary>
    public static async Task LikeGitAsync(BlameScoreboard sb, GitBlameFlags opt, CancellationToken cancellationToken)
    {
        // For the duration of the walk, allow large trees into the object cache
        // (the managed port re-allocates and re-parses on every cache miss; no
        // mmap zero-copy reads like native). The flag is cleared in finally so a
        // cancellation or exception does not leak the policy into later
        // operations on the same repository.
        sb.Repository.Objects.SetAllowLargeTrees(true);
        try
        {
            while (true)
            {
                // Find a suspect to break down.
                BlameOrigin? suspect = null;
                for (BlameEntry? ent = sb.Ent; suspect is null && ent is not null; ent = ent.Next)
                {
                    if (!ent.Guilty)
                    {
                        suspect = ent.Suspect;
                    }
                }

                if (suspect is null)
                {
                    break;
                }

                await PassBlameAsync(sb, suspect, opt, cancellationToken).ConfigureAwait(false);

                // Take responsibility for the remaining entries.
                for (BlameEntry? ent = sb.Ent; ent is not null; ent = ent.Next)
                {
                    if (SameSuspect(ent.Suspect, suspect))
                    {
                        ent.Guilty = true;
                        ent.IsBoundary = suspect.Commit.Id == sb.Options.OldestCommit;
                    }
                }
            }

            Coalesce(sb);
        }
        finally
        {
            sb.Repository.Objects.SetAllowLargeTrees(false);
        }
    }

    /// <summary>
    /// Checks if two origins refer to the same commit + path. Matches
    /// <c>same_suspect</c> (blame_git.c:88-95).
    /// </summary>
    private static bool SameSuspect(BlameOrigin? a, BlameOrigin? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a.Commit.Id != b.Commit.Id)
        {
            return false;
        }

        return a.Path.Equals(b.Path);
    }

    /// <summary>
    /// Find the line number of the last line the target is suspected for.
    /// Matches <c>find_last_in_target</c> (blame_git.c:98-117).
    /// </summary>
    private static bool FindLastInTarget(out int lastInTarget, BlameScoreboard sb, BlameOrigin target)
    {
        lastInTarget = 0;
        bool found = false;

        for (BlameEntry? e = sb.Ent; e is not null; e = e.Next)
        {
            if (e.Guilty || !SameSuspect(e.Suspect, target))
            {
                continue;
            }

            if (lastInTarget < e.SLno + e.NumLines)
            {
                found = true;
                lastInTarget = e.SLno + e.NumLines;
            }
        }

        return found;
    }

    /// <summary>
    /// Splits an existing blame entry into up to three parts: before the chunk,
    /// the chunk blamed on the parent, and after the chunk. Matches
    /// <c>split_overlap</c> (blame_git.c:133-170).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <paramref name="split"/> array has 3 slots. After this call:
    /// <list type="bullet">
    /// <item><c>split[0]</c>: the pre-chunk part (same suspect), or null if none.</item>
    /// <item><c>split[1]</c>: the chunk blamed on the parent, or null if nothing to blame.</item>
    /// <item><c>split[2]</c>: the post-chunk part (same suspect), or null if none.</item>
    /// </list>
    /// </para>
    /// </remarks>
    private static void SplitOverlap(
        BlameEntry[] split, BlameEntry e,
        int tlno, int plno, int same, BlameOrigin parent)
    {
        int chunkEndLno;

        if (e.SLno < tlno)
        {
            // There is a pre-chunk part not blamed on the parent.
            split[0].Suspect = e.Suspect;
            split[0].Lno = e.Lno;
            split[0].SLno = e.SLno;
            split[0].NumLines = tlno - e.SLno;
            split[1].Lno = e.Lno + tlno - e.SLno;
            split[1].SLno = plno;
        }
        else
        {
            split[1].Lno = e.Lno;
            split[1].SLno = plno + (e.SLno - tlno);
        }

        if (same < e.SLno + e.NumLines)
        {
            // There is a post-chunk part not blamed on parent.
            split[2].Suspect = e.Suspect;
            split[2].Lno = e.Lno + (same - e.SLno);
            split[2].SLno = e.SLno + (same - e.SLno);
            split[2].NumLines = e.SLno + e.NumLines - same;
            chunkEndLno = split[2].Lno;
        }
        else
        {
            chunkEndLno = e.Lno + e.NumLines;
        }

        split[1].NumLines = chunkEndLno - split[1].Lno;

        // If there is nothing to blame the parent for, forget the splitting.
        if (split[1].NumLines < 1)
        {
            return;
        }

        split[1].Suspect = parent;
    }

    /// <summary>
    /// Links a new blame entry into the scoreboard. Entries covering the same
    /// line range have been removed previously. Matches
    /// <c>add_blame_entry</c> (blame_git.c:176-196).
    /// </summary>
    private static void AddBlameEntry(BlameScoreboard sb, BlameEntry e)
    {
        BlameEntry? prev = null;
        BlameEntry? ent = sb.Ent;
        while (ent is not null && ent.Lno < e.Lno)
        {
            prev = ent;
            ent = ent.Next;
        }

        // prev, if not null, is the last one that is below e.
        e.Prev = prev;
        if (prev is not null)
        {
            e.Next = prev.Next;
            prev.Next = e;
        }
        else
        {
            e.Next = sb.Ent;
            sb.Ent = e;
        }

        e.Next?.Prev = e;
    }

    /// <summary>
    /// Copies information from <paramref name="src"/> into <paramref name="dst"/>
    /// (which is already on the linked list). Matches <c>dup_entry</c>
    /// (blame_git.c:203-215).
    /// </summary>
    private static void DupEntry(BlameEntry dst, BlameEntry src)
    {
        BlameEntry? p = dst.Prev;
        BlameEntry? n = dst.Next;
        CopyEntryData(dst, src);
        dst.Prev = p;
        dst.Next = n;
        dst.Score = 0;
    }

    /// <summary>
    /// Copies the data fields (Lno, NumLines, Suspect, SLno, Guilty, IsBoundary)
    /// from src to dst, without touching the linked-list pointers.
    /// </summary>
    private static void CopyEntryData(BlameEntry dst, BlameEntry src)
    {
        dst.Lno = src.Lno;
        dst.NumLines = src.NumLines;
        dst.Suspect = src.Suspect;
        dst.SLno = src.SLno;
        dst.Guilty = src.Guilty;
        dst.IsBoundary = src.IsBoundary;
        dst.Scanned = src.Scanned;
    }

    /// <summary>
    /// Adjusts the linked list to reflect the split. Matches
    /// <c>split_blame</c> (blame_git.c:221-263).
    /// </summary>
    private static void SplitBlame(BlameScoreboard sb, BlameEntry[] split, BlameEntry e)
    {
        if (split[0].Suspect is not null && split[2].Suspect is not null)
        {
            // First part: reuse storage for existing entry e.
            DupEntry(e, split[0]);

            // Last part.
            var newEntry = new BlameEntry(split[2].Suspect)
            {
                Lno = split[2].Lno,
                NumLines = split[2].NumLines,
                SLno = split[2].SLno,
            };
            AddBlameEntry(sb, newEntry);

            // Middle part: parent.
            newEntry = new BlameEntry(split[1].Suspect)
            {
                Lno = split[1].Lno,
                NumLines = split[1].NumLines,
                SLno = split[1].SLno,
            };
            AddBlameEntry(sb, newEntry);
        }
        else if (split[0].Suspect is null && split[2].Suspect is null)
        {
            // Parent covers the entire area; reuse storage for e.
            DupEntry(e, split[1]);
        }
        else if (split[0].Suspect is not null)
        {
            // Me and then parent.
            DupEntry(e, split[0]);
            var newEntry = new BlameEntry(split[1].Suspect)
            {
                Lno = split[1].Lno,
                NumLines = split[1].NumLines,
                SLno = split[1].SLno,
            };
            AddBlameEntry(sb, newEntry);
        }
        else
        {
            // Parent and then me.
            DupEntry(e, split[1]);
            var newEntry = new BlameEntry(split[2].Suspect)
            {
                Lno = split[2].Lno,
                NumLines = split[2].NumLines,
                SLno = split[2].SLno,
            };
            AddBlameEntry(sb, newEntry);
        }
    }

    /// <summary>
    /// Splits entry <paramref name="e"/> and passes blame to parent. Matches
    /// <c>blame_overlap</c> (blame_git.c:280-297).
    /// </summary>
    private static void BlameOverlap(
        BlameScoreboard sb, BlameEntry e,
        int tlno, int plno, int same, BlameOrigin parent)
    {
        var split = new BlameEntry[3];
        split[0] = new BlameEntry(null);
        split[1] = new BlameEntry(null);
        split[2] = new BlameEntry(null);

        SplitOverlap(split, e, tlno, plno, same, parent);
        if (split[1].Suspect is not null)
        {
            SplitBlame(sb, split, e);
        }
    }

    /// <summary>
    /// Processes one hunk from the patch. Find and split the overlap, pass blame
    /// to the parent. Matches <c>blame_chunk</c> (blame_git.c:304-326).
    /// </summary>
    private static void BlameChunk(
        BlameScoreboard sb,
        int tlno, int plno, int same,
        BlameOrigin target, BlameOrigin parent)
    {
        for (BlameEntry? e = sb.Ent; e is not null; e = e.Next)
        {
            if (e.Guilty || !SameSuspect(e.Suspect, target))
            {
                continue;
            }

            if (same <= e.SLno)
            {
                continue;
            }

            if (tlno < e.SLno + e.NumLines)
            {
                BlameOverlap(sb, e, tlno, plno, same, parent);
            }
        }
    }

    /// <summary>
    /// The XDiff hunk callback data, accumulating tlno/plno across hunks.
    /// Matches <c>blame_chunk_cb_data</c> + <c>my_emit</c>
    /// (blame_git.c:80-86, 328-341).
    /// </summary>
    internal sealed class BlameChunkCbData(BlameScoreboard sb, BlameOrigin target, BlameOrigin parent)
    {
        public BlameScoreboard Blame = sb;
        public BlameOrigin Target = target;
        public BlameOrigin Parent = parent;
        public int Tlno;
        public int Plno;
    }

    /// <summary>
    /// Diffs two blobs and calls <see cref="BlameChunk"/> per hunk. Matches
    /// <c>diff_hunks</c> (blame_git.c:367-388).
    /// </summary>
    /// <remarks>
    /// Streams hunks from <c>Xdiff.Diff.Compute</c> into a
    /// <see cref="BlameChunkCbDataSink" /> that fires <see cref="BlameChunk" /> per
    /// hunk, using <c>(OldStart, OldCount, NewStart, NewCount)</c>.
    /// This maps to C's <c>xdl_diff</c> with a <c>hunk_func</c> callback.
    /// </remarks>
    internal static int DiffHunks(
        ReadOnlyMemory<byte> fileA, ReadOnlyMemory<byte> fileB,
        BlameChunkCbData d, GitBlameOptions options)
    {
        var xpp = new DiffOptions
        {
            Whitespace = (options.Flags & GitBlameFlags.IgnoreWhitespace) != 0
                ? WhitespaceMode.IgnoreAll
                : WhitespaceMode.None,
            ContextLines = 0,
        };

        // trim_common_tail: trim common trailing 1024-byte blocks (perf optimization).
        TrimCommonTail(ref fileA, ref fileB);

        const long GitXdiffMaxSize = 1024L * 1024 * 1024; // 1 GiB
        if (fileA.Length > GitXdiffMaxSize || fileB.Length > GitXdiffMaxSize)
        {
            // C (blame_git.c:381-385): git_error_set(GIT_ERROR_INVALID,
            // "file too large to blame"); return -1 — the error propagates
            // through pass_blame_to_parent → pass_blame → git_blame__like_git
            // and fails the whole blame (the error is propagated, not
            // swallowed).
            throw new GitException(
                GitErrorCode.Error,
                "file too large to blame",
                GitErrorCategory.Invalid);
        }

        var sink = new BlameChunkCbDataSink(d);
        Xdiff.Diff.Compute(sink, fileA, fileB, xpp);

        return 0;
    }

    private sealed class BlameChunkCbDataSink(BlameChunkCbData d) : HunkSinkBase
    {
        public override void EndHunk()
        {
            int startA = OldCount > 0 ? OldStart - 1 : OldStart;
            int countA = OldCount;
            int startB = NewCount > 0 ? NewStart - 1 : NewStart;
            int countB = NewCount;

            BlameChunk(d.Blame, d.Tlno, d.Plno, startB, d.Target, d.Parent);
            d.Plno = startA + countA;
            d.Tlno = startB + countB;
        }
    }

    /// <summary>
    /// Trims common trailing blocks before diffing (performance optimization).
    /// Matches <c>trim_common_tail</c> (blame_git.c:343-365).
    /// </summary>
    private static void TrimCommonTail(ref ReadOnlyMemory<byte> a, ref ReadOnlyMemory<byte> b)
    {
        const int Block = 1024;
        ReadOnlySpan<byte> aSpan = a.Span;
        ReadOnlySpan<byte> bSpan = b.Span;
        int trimmed = 0;
        int recovered = 0;
        int aEnd = aSpan.Length;
        int bEnd = bSpan.Length;
        int smaller = Math.Min(aSpan.Length, bSpan.Length);

        while (Block + trimmed <= smaller)
        {
            if (!aSpan.Slice(aEnd - Block - trimmed, Block).SequenceEqual(
                 bSpan.Slice(bEnd - Block - trimmed, Block)))
            {
                break;
            }

            trimmed += Block;
        }

        while (recovered < trimmed)
        {
            if (aSpan[aEnd - trimmed + recovered] == '\n')
            {
                break;
            }

            recovered++;
        }

        if (trimmed - recovered > 0)
        {
            a = a[..^(trimmed - recovered)];
            b = b[..^(trimmed - recovered)];
        }
    }

    /// <summary>
    /// Gets the blob content as a <c>ReadOnlyMemory&lt;byte&gt;</c>. Matches
    /// <c>fill_origin_blob</c> (blame_git.c:390-397).
    /// </summary>
    private static ReadOnlyMemory<byte> FillOriginBlob(BlameOrigin o)
    {
        if (o.Blob is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        return o.Blob.Content;
    }

    /// <summary>
    /// Passes blame from target to its parent. Matches
    /// <c>pass_blame_to_parent</c> (blame_git.c:399-422).
    /// </summary>
    private static int PassBlameToParent(BlameScoreboard sb, BlameOrigin target, BlameOrigin parent)
    {
        if (!FindLastInTarget(out int lastInTarget, sb, target))
        {
            return 1; // nothing remains for this target
        }

        ReadOnlyMemory<byte> fileP = FillOriginBlob(parent);
        ReadOnlyMemory<byte> fileO = FillOriginBlob(target);

        var d = new BlameChunkCbData(sb, target, parent);

        // DiffHunks throws GitException("file too large to blame") instead of
        // returning -1 (C: blame_git.c:381-385 + pass_blame_to_parent 414-415).
        DiffHunks(fileP, fileO, d, sb.Options);

        // The rest (anything after tlno) are the same as the parent.
        BlameChunk(sb, d.Tlno, d.Plno, lastInTarget, target, parent);
        return 0;
    }

    /// <summary>
    /// Creates a new origin for a commit + path. Matches <c>make_origin</c>
    /// (blame_git.c:37-61).
    /// </summary>
    internal static async Task<BlameOrigin> MakeOriginAsync(GitRepository repo, Commit commit, GitPath path, CancellationToken cancellationToken)
    {
        GitBlob? blob = await LookupBlobByPathAsync(repo, commit, path, cancellationToken).ConfigureAwait(false);
        return new BlameOrigin(commit, path, blob);
    }

    /// <summary>
    /// Resolves a blob at a path within a commit's tree. No single-call
    /// <c>git_object_lookup_bypath</c> equivalent in C# — manual cascade:
    /// <c>LookupAsync&lt;GitTree&gt;(commit.Tree)</c> → <c>tree.EntryByPathAsync(path)</c> →
    /// <c>LookupAsync&lt;GitBlob&gt;(entry.Id)</c>.
    /// </summary>
    internal static async Task<GitBlob?> LookupBlobByPathAsync(GitRepository repo, Commit commit, GitPath path, CancellationToken cancellationToken)
    {
        GitTree? tree = await repo.Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return null;
        }

        GitTreeEntry? entry = await tree.EntryByPathAsync(path, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        if (entry.Value.Type != GitObjectType.Blob)
        {
            return null;
        }

        return await repo.Objects.LookupAsync<GitBlob>(entry.Value.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Locates an existing origin or creates a new one. Matches
    /// <c>git_blame__get_origin</c> (blame_git.c:64-78).
    /// </summary>
    /// <remarks>
    /// <b>Latent C quirk:</b> the loop that searches existing entries sets
    /// <c>*out</c> but does NOT return — it always falls through to
    /// <c>make_origin</c>. Faithful port preserves this behavior.
    /// </remarks>
    internal static async Task<BlameOrigin> GetOriginAsync(BlameScoreboard sb, Commit commit, GitPath path, CancellationToken cancellationToken)
    {
        for (BlameEntry? e = sb.Ent; e is not null; e = e.Next)
        {
            if (e.Suspect is { } suspect && suspect.Commit.Id == commit.Id && suspect.Path.Equals(path))
            {
                // C sets *out = origin_incref(e->suspect) but doesn't return.
                // It falls through to make_origin. We preserve this.
            }
        }

        return await MakeOriginAsync(sb.Repository, commit, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the origin for a path in the parent commit (handling renames).
    /// Matches <c>find_origin</c> (blame_git.c:431-492).
    /// </summary>
    private static async Task<BlameOrigin?> FindOriginAsync(BlameScoreboard sb, Commit parent, BlameOrigin origin, CancellationToken cancellationToken)
    {
        BlameOrigin? porigin = null;

        // Get the trees from this commit and its parent. The origin's tree is
        // cached on the origin (lazily resolved once per origin, not once per
        // parent of a multi-parent commit).
        GitTree? otree = await origin.GetTreeAsync(cancellationToken).ConfigureAwait(false);
        GitTree? ptree = await sb.Repository.Objects.LookupAsync<GitTree>(parent.Tree, cancellationToken).ConfigureAwait(false);
        if (otree is null || ptree is null)
        {
            return null;
        }

        // Decide whether the tracked file changed in this parent. With a single
        // tracked path (before any rename is detected), the pathspec diff's only
        // output is the boolean "did the tracked file change" — a blob-OID
        // comparison at the single path answers it exactly at O(depth) lookups
        // instead of a pathspec subtree walk. After a rename, Paths.Count > 1
        // and the multi-path pathspec diff runs instead (rename chaining needs
        // the accumulated path set).
        if (sb.Paths.Count <= 1)
        {
            GitTreeEntry? oEntry = await otree.EntryByPathAsync(origin.Path, cancellationToken).ConfigureAwait(false);
            GitTreeEntry? pEntry = await ptree.EntryByPathAsync(origin.Path, cancellationToken).ConfigureAwait(false);

            bool unchanged = (oEntry is null && pEntry is null) || (oEntry is not null && pEntry is not null
                && oEntry.Value.Type == GitObjectType.Blob
                && pEntry.Value.Type == GitObjectType.Blob
                && oEntry.Value.Id == pEntry.Value.Id);

            // Note on mode-only changes (same blob OID, different file mode):
            // the pathspec diff would report a delta, but both code paths
            // converge — the full-diff branch's tracked-path scan finds the
            // same-path delta, PathsAdd(oldPath) is a no-op for the already
            // tracked path, and MakeOriginAsync(parent, origin.Path) produces
            // exactly the origin that GetOriginAsync creates here. Blame output
            // is identical either way.
            if (unchanged)
            {
                // No changes; copy data — same path in parent.
                porigin = await GetOriginAsync(sb, parent, origin.Path, cancellationToken).ConfigureAwait(false);
                return porigin;
            }

            // Both present as blobs with different OIDs → changed in place.
            // With only GIT_DIFF_FIND_RENAMES (no GIT_DIFF_FIND_REWRITES), a
            // MODIFIED delta is neither a rename source nor a rename target
            // (diff_tform.c:695-714, 760-783), so the full diff + FindSimilar
            // would produce porigin = (parent, origin.Path) — exactly what
            // GetOriginAsync returns here. Skip the full diff.
            //
            // Single-path only: the multi-path case (sb.Paths.Count > 1) needs
            // the full diff to detect renames of OTHER tracked paths to
            // untracked targets (see FindOriginPathspec_UsesAccumulatedPaths).
            if (oEntry is not null && pEntry is not null
                && oEntry.Value.Type == GitObjectType.Blob
                && pEntry.Value.Type == GitObjectType.Blob)
            {
                porigin = await GetOriginAsync(sb, parent, origin.Path, cancellationToken).ConfigureAwait(false);
                return porigin;
            }

            // Origin path absent in parent or type change — skip the pathspec
            // diff (its only purpose was this gate) and go straight to the full
            // diff so FindSimilar can detect a rename.
        }
        else
        {
            // First diff: pathspec-filtered, context=0, skip_binary_check. C (blame_git.c:451-452): diffopts.pathspec = blame->paths — the ACCUMULATED set of
            // tracked paths (the original path plus every old path added by earlier renames), not just the current origin path. GitDiffOptions.PathSpecs is
            // byte-faithful (GitPath[]).
            var diffopts = new GitDiffOptions
            {
                ContextLines = 0,
                Flags = GitDiffOptionsFlags.SkipBinaryCheck,
                PathSpecs = sb.Paths.ToArray(),
            };

            using GitDiff difflist = await GitDiff.TreeToTreeAsync(sb.Repository, ptree, otree, diffopts, cancellationToken).ConfigureAwait(false);

            if (difflist.DeltaCount == 0)
            {
                // No changes; copy data — same path in parent.
                porigin = await GetOriginAsync(sb, parent, origin.Path, cancellationToken).ConfigureAwait(false);
                return porigin;
            }
        }

        // Generate a full diff and let diff find renames.
        var fullOpts = new GitDiffOptions
        {
            ContextLines = 0,
            Flags = GitDiffOptionsFlags.SkipBinaryCheck,
        };

        using GitDiff fullDiff = await GitDiff.TreeToTreeAsync(sb.Repository, ptree, otree, fullOpts, cancellationToken).ConfigureAwait(false);
        await fullDiff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames }, cancellationToken).ConfigureAwait(false);

        // Find one that matches our tracked paths. delta.NewFile.Path / delta.OldFile.Path are byte-faithful GitPath?; use the sorted-vector helpers (binary
        // search byte-wise, matching git_vector_bsearch / git_vector_insert_sorted with paths_cmp).
        for (int i = 0; i < fullDiff.DeltaCount; i++)
        {
            GitDiffDelta delta = fullDiff.GetDelta(i);
            GitPath newPath = delta.NewFile.Path ?? default;
            if (sb.PathsContains(newPath))
            {
                // Add old path to tracked paths.
                GitPath oldPath = delta.OldFile.Path ?? default;
                sb.PathsAdd(oldPath);
                porigin = await MakeOriginAsync(sb.Repository, parent, oldPath, cancellationToken).ConfigureAwait(false);
            }
        }

        return porigin;
    }

    /// <summary>
    /// Passes whole blame when blobs exactly match. Matches
    /// <c>pass_whole_blame</c> (blame_git.c:498-516).
    /// </summary>
    private static async ValueTask<int> PassWholeBlameAsync(BlameScoreboard sb, BlameOrigin origin, BlameOrigin porigin, CancellationToken cancellationToken)
    {
        if (porigin.Blob is null && origin.Blob is not null)
        {
            // Look up the blob by the origin's blob ID.
            porigin.Blob = await sb.Repository.Objects.LookupAsync<GitBlob>(origin.Blob.Id, cancellationToken).ConfigureAwait(false);
            if (porigin.Blob is null)
            {
                // C (blame_git.c:503-506): git_object_lookup failure propagates
                // (GIT_ENOTFOUND + ODB message) — the caller must not swallow it.
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"object not found - no match for id ({origin.Blob.Id})",
                    GitErrorCategory.Odb);
            }
        }

        for (BlameEntry? e = sb.Ent; e is not null; e = e.Next)
        {
            if (!SameSuspect(e.Suspect, origin))
            {
                continue;
            }

            e.Suspect = porigin;
        }

        return 0;
    }

    /// <summary>
    /// Passes blame from origin to its parents. Matches <c>pass_blame</c>
    /// (blame_git.c:518-612).
    /// </summary>
    private static async Task PassBlameAsync(BlameScoreboard sb, BlameOrigin origin, GitBlameFlags opt, CancellationToken cancellationToken)
    {
        Commit commit = origin.Commit;
        IReadOnlyList<GitOid> parentIds = commit.Parents;
        int numParents = parentIds.Count;

        // Stop at oldest specified commit.
        if (commit.Id == sb.Options.OldestCommit)
        {
            numParents = 0;
        }
        else if ((opt & GitBlameFlags.FirstParent) != 0 && numParents > 1)
        {
            // Limit search to the first parent.
            numParents = 1;
        }

        if (numParents == 0)
        {
            sb.Options = sb.Options with { OldestCommit = commit.Id };
            return;
        }

        var sgOrigin = new BlameOrigin?[numParents];

        for (int i = 0; i < numParents; i++)
        {
            if (sgOrigin[i] is not null)
            {
                continue;
            }

            Commit? p = await sb.Repository.Objects.LookupAsync<Commit>(parentIds[i], cancellationToken).ConfigureAwait(false);
            if (p is null)
            {
                // C (blame_git.c:551-552): git_commit_parent fails when the
                // parent object is missing — the error aborts the blame
                // (GIT_ENOTFOUND + ODB message).
                // parent silently, attributing the lines to the child.
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"object not found - no match for id ({parentIds[i]})",
                    GitErrorCategory.Odb);
            }

            BlameOrigin? porigin = await FindOriginAsync(sb, p, origin, cancellationToken).ConfigureAwait(false);
            if (porigin is null)
            {
                continue;
            }

            if (porigin.Blob is not null && origin.Blob is not null &&
                porigin.Blob.Id == origin.Blob.Id)
            {
                // C (blame_git.c:567-569): error = pass_whole_blame(...); the
                // blob-lookup failure inside throws (see PassWholeBlameAsync).
                await PassWholeBlameAsync(sb, origin, porigin, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Check for duplicate parent blobs.
            bool same = false;
            for (int j = 0; j < i; j++)
            {
                GitBlob? jBlob = sgOrigin[j]?.Blob;
                if (jBlob is not null && porigin.Blob is not null &&
                    jBlob.Id == porigin.Blob.Id)
                {
                    same = true;
                    break;
                }
            }

            if (!same)
            {
                sgOrigin[i] = porigin;
            }
        }

        // Standard blame: pass blame to each parent.
        for (int i = 0; i < numParents; i++)
        {
            BlameOrigin? porigin = sgOrigin[i];
            if (porigin is null)
            {
                continue;
            }

            origin.Previous ??= porigin;

            // C (blame_git.c:593-598): ret != 0 → goto finish. Errors (< 0)
            // now throw from PassBlameToParent/DiffHunks; ret == 1 means
            // "nothing remains for this target" — stop processing further
            // parents with error = 0.
            int ret = PassBlameToParent(sb, origin, porigin);
            if (ret != 0)
            {
                return;
            }
        }

        // TODO: optionally find moves in parents' files
        // TODO: optionally find copies in parents' files
    }

    /// <summary>
    /// Merges adjacent blame entries that came from contiguous lines in the same
    /// origin. Matches <c>coalesce</c> (blame_git.c:619-638).
    /// </summary>
    private static void Coalesce(BlameScoreboard sb)
    {
        BlameEntry? ent = sb.Ent;
        while (ent is not null)
        {
            BlameEntry? next = ent.Next;
            if (next is null)
            {
                break;
            }

            if (SameSuspect(ent.Suspect, next.Suspect) &&
                ent.Guilty == next.Guilty &&
                ent.SLno + ent.NumLines == next.SLno)
            {
                // Merge next into ent.
                ent.NumLines += next.NumLines;
                ent.Next = next.Next;
                ent.Next?.Prev = ent;

                ent.Score = 0;
                // Continue with the same ent (next = ent.Next will be re-examined).
                continue;
            }

            ent = next;
        }
    }
}
