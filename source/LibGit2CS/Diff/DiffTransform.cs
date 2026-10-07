// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;

namespace LibGit2CS.Diff;

/// <summary>
/// Diff post-processing: rename/copy/break-rewrite detection and merge
/// deduplication. Managed port of <c>src/libgit2/diff_tform.c</c> (1,153 LOC).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FindSimilarAsync"/> post-processes a diff to detect renames and copies
/// using content similarity (<see cref="SimilarityHash"/>). It mutates the diff's
/// delta list in place — statuses change, files swap, deltas split/merge.
/// </para>
/// <para>
/// <see cref="Merge"/> combines two diffs (e.g. tree-to-index + index-to-workdir)
/// into one, using the <c>git_diff__merge_like_cgit</c> status-combination rules.
/// </para>
/// </remarks>
internal static class DiffTransform
{
    private const int DefaultThreshold = 50;
    private const int DefaultBreakRewriteThreshold = 60;
    private const int DefaultRenameLimit = 1000;

    /// <summary>
    /// Post-processes a diff to detect renames/copies. Matches
    /// <c>git_diff_find_similar</c> (diff_tform.c:819-1151). Mutates the diff's
    /// delta list in place.
    /// </summary>
    public static async Task FindSimilarAsync(DiffGenerator gen, GitDiffFindOptions? givenOpts, CancellationToken cancellationToken = default)
    {
        GitDiffFindOptions opts = await NormalizeFindOptsAsync(gen, givenOpts, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GitDiffDelta> deltas = gen.Deltas;
        int numDeltas = deltas.Count;

        if (numDeltas == 0 || (opts.Flags & GitDiffFindFlags.All) == 0)
        {
            return;
        }

        // sigcache[2*i] = old_file signature, sigcache[2*i+1] = new_file signature
        var sigcache = new SimilarityHash?[2 * numDeltas];

        int numSrcs = 0, numTgts = 0, numRewrites = 0, numUpdates = 0, numToDelete = 0;

        // ━━ Classification pass ━━
        for (int t = 0; t < numDeltas; t++)
        {
            GitDiffDelta delta = deltas[t];

            HandleNonBlob(delta, opts);

            if (await IsRenameSourceAsync(gen, opts, t, sigcache, cancellationToken).ConfigureAwait(false))
            {
                numSrcs++;
            }

            if (await IsRenameTargetAsync(gen, opts, t, sigcache, cancellationToken).ConfigureAwait(false))
            {
                numTgts++;
            }

            if ((delta.OldFile.InternalFlags & DiffInternalFlags.ToSplit) != 0)
            {
                numRewrites++;
            }

            if ((delta.OldFile.InternalFlags & DiffInternalFlags.ToDelete) != 0)
            {
                numToDelete++;
            }
        }

        // If no sources or targets, skip matching.
        if (numSrcs == 0 || numTgts == 0)
        {
            goto SplitAndDelete;
        }

        // ━━ Matching pass (stable-marriage with retry) ━━
        var tgt2src = new FindMatch[numDeltas];
        var src2tgt = new FindMatch[numDeltas];
        FindMatch[]? tgt2srcCopy = (opts.Flags & GitDiffFindFlags.Copies) != 0
            ? new FindMatch[numDeltas]
            : null;

        int numBumped;
        do
        {
            numBumped = 0;
            int triedTgts = 0;

            for (int t = 0; t < numDeltas && triedTgts < numTgts; t++)
            {
                GitDiffDelta tgt = deltas[t];
                if ((tgt.NewFile.InternalFlags & DiffInternalFlags.IsRenameTarget) == 0)
                {
                    continue;
                }

                triedTgts++;
                int triedSrcs = 0;

                for (int s = 0; s < numDeltas && triedSrcs < numSrcs; s++)
                {
                    GitDiffDelta src = deltas[s];
                    if ((src.OldFile.InternalFlags & DiffInternalFlags.IsRenameSource) == 0)
                    {
                        continue;
                    }

                    triedSrcs++;

                    // Don't measure self-similarity here.
                    int result;
                    if (s == t)
                    {
                        result = -1;
                    }
                    else
                    {
                        result = await SimilarityMeasureAsync(gen, opts, sigcache, 2 * s, 2 * t + 1, cancellationToken).ConfigureAwait(false);
                    }

                    if (result < 0)
                    {
                        continue;
                    }

                    ushort similarity = (ushort)result;

                    // Is this a better rename?
                    if (tgt2src[t].Similarity < similarity &&
                        src2tgt[s].Similarity < similarity)
                    {
                        // Eject old mappings.
                        if (src2tgt[s].Similarity > 0)
                        {
                            tgt2src[src2tgt[s].Idx].Similarity = 0;
                            numBumped++;
                        }

                        if (tgt2src[t].Similarity > 0)
                        {
                            src2tgt[tgt2src[t].Idx].Similarity = 0;
                            numBumped++;
                        }

                        // Write new mapping.
                        tgt2src[t] = new FindMatch { Idx = s, Similarity = similarity };
                        src2tgt[s] = new FindMatch { Idx = t, Similarity = similarity };
                    }

                    // Keep best absolute match for copies.
                    if (tgt2srcCopy is not null && tgt2srcCopy[t].Similarity < similarity)
                    {
                        tgt2srcCopy[t] = new FindMatch { Idx = s, Similarity = similarity };
                    }

                    if (triedSrcs > opts.RenameLimit)
                    {
                        break;
                    }
                }
            }
        }
        while (numBumped > 0);

        // ━━ Rewrite pass ━━
        for (int t = 0; t < numDeltas; t++)
        {
            GitDiffDelta tgt = deltas[t];
            if ((tgt.NewFile.InternalFlags & DiffInternalFlags.IsRenameTarget) == 0)
            {
                continue;
            }

            FindMatch bestMatch;
            if (tgt2src[t].Similarity > 0)
            {
                bestMatch = tgt2src[t];
            }
            else if (tgt2srcCopy is not null && tgt2srcCopy[t].Similarity > 0)
            {
                bestMatch = tgt2srcCopy[t];
            }
            else
            {
                continue;
            }

            int s = bestMatch.Idx;
            GitDiffDelta src = deltas[s];

            RewriteDelta(gen, src, tgt, bestMatch, opts, t, s, src2tgt, tgt2src, ref numRewrites, ref numUpdates);
        }

SplitAndDelete:
        if (numRewrites > 0 || numUpdates > 0 || numToDelete > 0)
        {
            int applyLen = numDeltas - numRewrites - numToDelete;
            bool actuallySplit = (opts.Flags & GitDiffFindFlags.BreakRewrites) != 0 &&
                                 (opts.Flags & GitDiffFindFlags.BreakRewritesForRenamesOnly) == 0;
            ApplySplitsAndDeletes(gen, deltas, applyLen, actuallySplit);
        }
    }

    /// <summary>
    /// Merges two diffs. Ports <c>git_diff__merge</c>
    /// (<c>diff_tform.c:114-197</c>) with <c>git_diff__merge_like_cgit</c> as
    /// the callback. The path comparison uses <paramref name="onto"/>'s
    /// <c>strcomp</c> slot (<c>STRCMP_CASESELECT(ignore_case, …)</c> at
    /// <c>diff_tform.c:148</c>), so the merge follows the onto diff's case mode.
    /// </summary>
    public static void Merge(DiffGenerator onto, DiffGenerator from)
    {
        var ontoDeltas = new List<GitDiffDelta>(onto.DeltaList);
        List<GitDiffDelta> fromDeltas = from.DeltaList;
        var result = new List<GitDiffDelta>(ontoDeltas.Count + fromDeltas.Count);

        bool reversed = (onto.Options.Flags & GitDiffOptionsFlags.Reverse) != 0;

        int i = 0, j = 0;
        while (i < ontoDeltas.Count || j < fromDeltas.Count)
        {
            GitDiffDelta? o = i < ontoDeltas.Count ? ontoDeltas[i] : null;
            GitDiffDelta? f = j < fromDeltas.Count ? fromDeltas[j] : null;

            int cmp = f is null ? -1 : o is null ? 1 :
                onto._strcomp(o.OldFile.Path ?? default, f.OldFile.Path ?? default);

            GitDiffDelta? delta;
            if (cmp < 0)
            {
                delta = DeltaDup(o!);
                i++;
            }
            else if (cmp > 0)
            {
                delta = DeltaDup(f!);
                j++;
            }
            else
            {
                GitDiffDelta left = reversed ? f! : o!;
                GitDiffDelta right = reversed ? o! : f!;
                delta = MergeLikeCgit(left, right);
                i++;
                j++;
            }

            if (delta is not null)
            {
                // C's
                // git_diff__merge drops every produced delta failing
                // git_diff_delta__should_skip against the ONTO options
                // (diff_tform.c:168-171; should_skip at diff_generate.c:356-
                // 378) — critically the Deleted+Added→UNMODIFIED merge result
                // when INCLUDE_UNMODIFIED is unset.
                if (ShouldSkip(onto.Options.Flags, delta))
                {
                    continue;
                }

                result.Add(delta);
            }
        }

        onto.DeltaList.Clear();
        onto.DeltaList.AddRange(result);
    }

    /// <summary>
    /// Ports <c>git_diff_delta__should_skip</c> (diff_generate.c:356-378):
    /// UNMODIFIED/IGNORED/UNTRACKED/UNREADABLE deltas are skipped unless the
    /// corresponding INCLUDE_* flag is set. Used by
    /// <see cref="Merge(DiffGenerator, DiffGenerator)"/> (diff_tform.c:168-171).
    /// </summary>
    private static bool ShouldSkip(GitDiffOptionsFlags flags, GitDiffDelta delta)
        => delta.Status switch
        {
            GitDeltaStatus.Unmodified when (flags & GitDiffOptionsFlags.IncludeUnmodified) == 0 => true,
            GitDeltaStatus.Ignored when (flags & GitDiffOptionsFlags.IncludeIgnored) == 0 => true,
            GitDeltaStatus.Untracked when (flags & GitDiffOptionsFlags.IncludeUntracked) == 0 => true,
            GitDeltaStatus.Unreadable when (flags & GitDiffOptionsFlags.IncludeUnreadable) == 0 => true,
            _ => false,
        };

    // ━━ Merge callback ━━

    /// <summary>
    /// Combines two deltas for the same path. Matches
    /// <c>git_diff__merge_like_cgit</c> (diff_tform.c:51-112).
    /// </summary>
    private static GitDiffDelta? MergeLikeCgit(GitDiffDelta a, GitDiffDelta b)
    {
        // If one is conflicted, dup it.
        if (b.Status == GitDeltaStatus.Conflicted)
        {
            return DeltaDup(b);
        }

        if (a.Status == GitDeltaStatus.Conflicted)
        {
            return DeltaDup(a);
        }

        // If b is unmodified or a is deleted, dup a.
        if (b.Status == GitDeltaStatus.Unmodified || a.Status == GitDeltaStatus.Deleted)
        {
            return DeltaDup(a);
        }

        // Otherwise, base on b.
        GitDiffDelta? dup = DeltaDup(b);
        if (dup is null)
        {
            return null;
        }

        // If a is uninteresting, done.
        if (a.Status is GitDeltaStatus.Unmodified or GitDeltaStatus.Untracked or GitDeltaStatus.Unreadable)
        {
            return dup;
        }

        // Combine statuses.
        if (dup.Status == GitDeltaStatus.Deleted)
        {
            if (a.Status == GitDeltaStatus.Added)
            {
                dup.Status = GitDeltaStatus.Unmodified;
                dup.FileCount = 2;
            }
        }
        else
        {
            dup.Status = a.Status;
            dup.FileCount = a.FileCount;
        }

        dup.OldFile.Id = a.OldFile.Id;
        dup.OldFile.Mode = a.OldFile.Mode;
        dup.OldFile.Size = a.OldFile.Size;
        dup.OldFile.Flags = a.OldFile.Flags;

        return dup;
    }

    // ━━ Delta duplication ━━

    /// <summary>
    /// Deep-copies a delta. Matches <c>git_diff__delta_dup</c>
    /// (diff_tform.c:20-49). Clears internal flags.
    /// </summary>
    private static GitDiffDelta? DeltaDup(GitDiffDelta d)
    {
        var dup = new GitDiffDelta(d.Status, d.FileCount,
            DupFile(d.OldFile), DupFile(d.NewFile))
        {
            Similarity = d.Similarity,
            Flags = d.Flags,
        };
        return dup;
    }

    private static GitDiffFile DupFile(GitDiffFile f)
    {
        return new GitDiffFile
        {
            Id = f.Id,
            Path = f.Path,
            Size = f.Size,
            Mode = f.Mode,
            Flags = f.Flags,
            InternalFlags = f.InternalFlags & ~DiffInternalFlags.AllInternal,
            IdAbbrev = f.IdAbbrev,
        };
    }

    // ━━ Classification ━━

    private static void HandleNonBlob(GitDiffDelta delta, GitDiffFindOptions opts)
    {
        if (GitFileModeUtils.IsBlob(delta.OldFile.Mode))
        {
            return;
        }

        if (delta.Status == GitDeltaStatus.Unmodified &&
            (opts.Flags & GitDiffFindFlags.RemoveUnmodified) != 0)
        {
            delta.OldFile.InternalFlags |= DiffInternalFlags.ToDelete;
        }
    }

    private static async Task<bool> IsRenameTargetAsync(
        DiffGenerator gen, GitDiffFindOptions opts, int deltaIdx, SimilarityHash?[] cache, CancellationToken cancellationToken)
    {
        GitDiffDelta delta = gen.Deltas[deltaIdx];

        if (!GitFileModeUtils.IsBlob(delta.NewFile.Mode))
        {
            return false;
        }

        switch (delta.Status)
        {
            case GitDeltaStatus.Unmodified:
            case GitDeltaStatus.Deleted:
            case GitDeltaStatus.Ignored:
            case GitDeltaStatus.Conflicted:
                return false;

            case GitDeltaStatus.Modified:
                if ((opts.Flags & (GitDiffFindFlags.Rewrites | GitDiffFindFlags.RenamesFromRewrites)) == 0)
                {
                    return false;
                }

                await CalcSelfSimilarityAsync(gen, opts, deltaIdx, cache, cancellationToken).ConfigureAwait(false);

                if ((opts.Flags & GitDiffFindFlags.BreakRewrites) != 0 &&
                    delta.Similarity < opts.BreakRewriteThreshold)
                {
                    delta.OldFile.InternalFlags |= DiffInternalFlags.ToSplit;
                    break;
                }

                if ((opts.Flags & GitDiffFindFlags.RenamesFromRewrites) != 0 &&
                    delta.Similarity < opts.RenameFromRewriteThreshold)
                {
                    delta.OldFile.InternalFlags |= DiffInternalFlags.ToSplit;
                    break;
                }

                return false;

            case GitDeltaStatus.Untracked:
                if ((opts.Flags & GitDiffFindFlags.ForUntracked) == 0)
                {
                    return false;
                }

                break;

            default:
                break;
        }

        delta.NewFile.InternalFlags |= DiffInternalFlags.IsRenameTarget;
        return true;
    }

    private static async Task<bool> IsRenameSourceAsync(
        DiffGenerator gen, GitDiffFindOptions opts, int deltaIdx, SimilarityHash?[] cache, CancellationToken cancellationToken)
    {
        GitDiffDelta delta = gen.Deltas[deltaIdx];

        if (!GitFileModeUtils.IsBlob(delta.OldFile.Mode))
        {
            return false;
        }

        switch (delta.Status)
        {
            case GitDeltaStatus.Added:
            case GitDeltaStatus.Untracked:
            case GitDeltaStatus.Unreadable:
            case GitDeltaStatus.Ignored:
            case GitDeltaStatus.Conflicted:
                return false;

            case GitDeltaStatus.Deleted:
            case GitDeltaStatus.Typechange:
                break;

            case GitDeltaStatus.Unmodified:
                if ((opts.Flags & GitDiffFindFlags.CopiesFromUnmodified) == 0)
                {
                    return false;
                }

                if ((opts.Flags & GitDiffFindFlags.RemoveUnmodified) != 0)
                {
                    delta.OldFile.InternalFlags |= DiffInternalFlags.ToDelete;
                }

                break;

            default: // MODIFIED, RENAMED, COPIED
                if ((opts.Flags & GitDiffFindFlags.Copies) != 0)
                {
                    break;
                }

                if ((opts.Flags & (GitDiffFindFlags.Rewrites | GitDiffFindFlags.RenamesFromRewrites)) == 0)
                {
                    return false;
                }

                await CalcSelfSimilarityAsync(gen, opts, deltaIdx, cache, cancellationToken).ConfigureAwait(false);

                if ((opts.Flags & GitDiffFindFlags.BreakRewrites) != 0 &&
                    delta.Similarity < opts.BreakRewriteThreshold)
                {
                    delta.OldFile.InternalFlags |= DiffInternalFlags.ToSplit;
                    break;
                }

                if ((opts.Flags & GitDiffFindFlags.RenamesFromRewrites) != 0 &&
                    delta.Similarity < opts.RenameFromRewriteThreshold)
                {
                    break;
                }

                return false;
        }

        delta.OldFile.InternalFlags |= DiffInternalFlags.IsRenameSource;
        return true;
    }

    private static async Task CalcSelfSimilarityAsync(
        DiffGenerator gen, GitDiffFindOptions opts, int deltaIdx, SimilarityHash?[] cache, CancellationToken cancellationToken)
    {
        GitDiffDelta delta = gen.Deltas[deltaIdx];
        if ((delta.OldFile.InternalFlags & DiffInternalFlags.HasSelfSimilarity) != 0)
        {
            return;
        }

        int score = await SimilarityMeasureAsync(gen, opts, cache, 2 * deltaIdx, 2 * deltaIdx + 1, cancellationToken).ConfigureAwait(false);
        if (score >= 0)
        {
            delta.Similarity = score;
            delta.OldFile.InternalFlags |= DiffInternalFlags.HasSelfSimilarity;
        }
    }

    // ━━ Similarity measurement ━━

    /// <summary>
    /// Computes the blob OID of a workdir file. Matches
    /// <c>git_diff__oid_for_file</c> (diff_generate.c:605-626): the file
    /// content is hashed as a blob. Returns the zero OID on read failure
    /// (the C also keeps the OID unset when hashing fails).
    /// </summary>
    private static async Task<GitOid> ComputeWorkdirOidAsync(
        DiffGenerator gen, GitDiffFile file, int fileIdx, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> data = await LoadFileContentAsync(gen, file, fileIdx, cancellationToken).ConfigureAwait(false);
        if (data.IsEmpty)
        {
            return default;
        }

        return GitObjectDb.HashObject(GitObjectType.Blob, data.Span, gen.Repo.ObjectFormat);
    }

    /// <summary>
    /// Computes the similarity score between two files. Returns -1 if
    /// incomparable, 0-100 otherwise. Matches <c>similarity_measure</c>
    /// (diff_tform.c:544-629).
    /// </summary>
    private static async Task<int> SimilarityMeasureAsync(
        DiffGenerator gen, GitDiffFindOptions opts, SimilarityHash?[] cache, int aIdx, int bIdx, CancellationToken cancellationToken)
    {
        GitDiffFile aFile = GetFile(gen, aIdx);
        GitDiffFile bFile = GetFile(gen, bIdx);
        bool exactMatch = (opts.Flags & GitDiffFindFlags.ExactMatchOnly) != 0;

        // Don't compare non-blobs.
        if (!GitFileModeUtils.IsBlob(aFile.Mode) || !GitFileModeUtils.IsBlob(bFile.Mode))
        {
            return -1;
        }

        // C (diff_tform.c:563-577): if exact match is requested, force the
        // computation of missing (zero) OIDs for workdir files
        // (git_diff__oid_for_file) so identical content scores 100.
        if (exactMatch)
        {
            if (aFile.Id.IsZero && gen.OldSrc == IteratorType.Workdir)
            {
                aFile.Id = await ComputeWorkdirOidAsync(gen, aFile, aIdx, cancellationToken).ConfigureAwait(false);
            }

            if (bFile.Id.IsZero && gen.NewSrc == IteratorType.Workdir)
            {
                bFile.Id = await ComputeWorkdirOidAsync(gen, bFile, bIdx, cancellationToken).ConfigureAwait(false);
            }
        }

        // Quick OID match — C compares with NO zero guard (two zero-OID
        // workdir files score 100 without hashing).
        if (aFile.Id == bFile.Id)
        {
            return 100;
        }

        if (exactMatch)
        {
            return 0;
        }

        // Size sanity check: if one is >8× the other, skip.
        if (aFile.Size > 127 && bFile.Size > 127 &&
            (aFile.Size > (bFile.Size << 3) || bFile.Size > (aFile.Size << 3)))
        {
            return -1;
        }

        // Compute signatures (cached).
        cache[aIdx] ??= await ComputeSignatureAsync(gen, aFile, aIdx, opts, cancellationToken).ConfigureAwait(false);
        cache[bIdx] ??= await ComputeSignatureAsync(gen, bFile, bIdx, opts, cancellationToken).ConfigureAwait(false);

        SimilarityHash? aSig = cache[aIdx];
        SimilarityHash? bSig = cache[bIdx];
        if (aSig is null || bSig is null)
        {
            return -1;
        }

        return SimilarityHash.Compare(aSig, bSig);
    }

    /// <summary>
    /// Computes (or loads) the similarity hash for one file. Matches
    /// <c>similarity_init</c> + <c>similarity_sig</c> (diff_tform.c:454-525).
    /// </summary>
    private static async Task<SimilarityHash?> ComputeSignatureAsync(
        DiffGenerator gen, GitDiffFile file, int fileIdx, GitDiffFindOptions opts, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> data = await LoadFileContentAsync(gen, file, fileIdx, cancellationToken).ConfigureAwait(false);
        if (data.IsEmpty)
        {
            return null;
        }

        SimilarityHashOptions hashsigOpts = SimilarityHashOptions.AllowSmallFiles;
        if ((opts.Flags & GitDiffFindFlags.IgnoreWhitespace) != 0)
        {
            hashsigOpts |= SimilarityHashOptions.IgnoreWhitespace;
        }
        else if ((opts.Flags & GitDiffFindFlags.DontIgnoreWhitespace) == 0)
        {
            hashsigOpts |= SimilarityHashOptions.SmartWhitespace;
        }

        return SimilarityHash.Create(data.Span, hashsigOpts);
    }

    /// <summary>
    /// Loads file content for similarity hashing. Tree source → blob ODB;
    /// workdir source → read file from disk.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>> LoadFileContentAsync(
        DiffGenerator gen, GitDiffFile file, int fileIdx, CancellationToken cancellationToken)
    {
        IteratorType src = (fileIdx & 1) != 0 ? gen.NewSrc : gen.OldSrc;

        if (file.Id.IsZero && src != IteratorType.Workdir)
        {
            // Untracked workdir files have a zero OID (not yet hashed into the ODB) but their
            // content is readable from disk — without this, GIT_DIFF_FIND_FOR_UNTRACKED can never
            // pair an untracked file as a rename target (git status workdir-rename detection).
            return ReadOnlyMemory<byte>.Empty;
        }

        if (src == IteratorType.Workdir)
        {
            // Workdir source implies a non-bare repo, so gen.Repo.Workdir is set.
            // Coalesce to empty string as a defensive fallback (parity with
            // diff_file.c:403 which calls git_buf_join on repo->workdir). The
            // GitPath->filesystem transcode is the single-point hook
            // (ToFileSystemString — no precompose on egress; macOS
            // auto-converts NFC->NFD on write, matching libgit2's
            // no-egress-iconv design).
            string workdirPath = Path.Join(
                gen.Repo.Workdir ?? string.Empty,
                (file.Path?.ToFileSystemString()) ?? string.Empty);
            if (!File.Exists(workdirPath))
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            return await File.ReadAllBytesAsync(workdirPath, cancellationToken).ConfigureAwait(false);
        }

        // Tree source: load blob from ODB.
        GitBlob? blob = await gen.Repo.Objects.LookupAsync<GitBlob>(file.Id, cancellationToken).ConfigureAwait(false);
        return blob?.Content ?? ReadOnlyMemory<byte>.Empty;
    }

    private static GitDiffFile GetFile(DiffGenerator gen, int idx)
    {
        GitDiffDelta delta = gen.Deltas[idx / 2];
        return (idx & 1) != 0 ? delta.NewFile : delta.OldFile;
    }

    // ━━ Rewrite pass ━━

    private static void RewriteDelta(
        DiffGenerator gen, GitDiffDelta src, GitDiffDelta tgt, FindMatch bestMatch,
        GitDiffFindOptions opts, int t, int s, FindMatch[] src2tgt, FindMatch[] tgt2src,
        ref int numRewrites, ref int numUpdates)
    {
        bool srcIsDeleted = src.Status == GitDeltaStatus.Deleted;
        bool srcIsSplit = DeltaIsSplit(src);

        if (srcIsDeleted)
        {
            if (DeltaIsNewOnly(tgt))
            {
                // Scenario 1: DELETE → ADD = RENAME
                if (bestMatch.Similarity < opts.RenameThreshold)
                {
                    return;
                }

                DeltaMakeRename(tgt, src, bestMatch.Similarity);
                src.OldFile.InternalFlags |= DiffInternalFlags.ToDelete;
                numRewrites++;
            }
            else
            {
                // Scenario 2: DELETE → SPLIT = RENAME + DELETE
                if (bestMatch.Similarity < opts.RenameFromRewriteThreshold)
                {
                    return;
                }

                GitDiffFile swap = tgt.OldFile;
                DeltaMakeRename(tgt, src, bestMatch.Similarity);
                numRewrites--;

                src.OldFile = swap;
                src.NewFile = new GitDiffFile
                {
                    Path = src.OldFile.Path,
                    Flags = GitDiffFileFlags.ValidId,
                };

                numUpdates++;

                // C (diff_tform.c:1027-1030): what used to be at src t is now
                // at src s — re-point any later mapping that referenced the
                // old target index.
                if (src2tgt[t].Similarity > 0 && src2tgt[t].Idx > t)
                {
                    tgt2src[src2tgt[t].Idx].Idx = s;
                }
            }
        }
        else if (srcIsSplit)
        {
            if (DeltaIsNewOnly(tgt))
            {
                // Scenario 3: SPLIT → ADD = ADD + RENAME
                if (bestMatch.Similarity < opts.RenameThreshold)
                {
                    return;
                }

                DeltaMakeRename(tgt, src, bestMatch.Similarity);

                src.Status = gen.NewSrc == IteratorType.Workdir
                    ? GitDeltaStatus.Untracked
                    : GitDeltaStatus.Added;
                src.FileCount = 1;
                src.OldFile = new GitDiffFile
                {
                    Path = src.NewFile.Path,
                    Flags = GitDiffFileFlags.ValidId,
                };
                src.OldFile.InternalFlags &= ~DiffInternalFlags.ToSplit;
                numRewrites--;
                numUpdates++;
            }
            else
            {
                // Scenario 4: SPLIT → SPLIT = RENAME + SPLIT
                if (bestMatch.Similarity < opts.RenameFromRewriteThreshold)
                {
                    return;
                }

                GitDiffFile swap = tgt.OldFile;
                DeltaMakeRename(tgt, src, bestMatch.Similarity);
                numRewrites--;
                numUpdates++;
                src.OldFile = swap;

                // C (diff_tform.c:1072-1085): if we've just swapped the new
                // element into the correct place, clear the SPLIT and
                // RENAME_TARGET flags — the source becomes a full RENAMED
                // delta (mutual pairing).
                if (tgt2src[s].Idx == t &&
                    tgt2src[s].Similarity > opts.RenameFromRewriteThreshold)
                {
                    src.Status = GitDeltaStatus.Renamed;
                    src.Similarity = tgt2src[s].Similarity;
                    tgt2src[s].Similarity = 0;
                    src.OldFile.InternalFlags &= ~(DiffInternalFlags.ToSplit | DiffInternalFlags.IsRenameTarget);
                    numRewrites--;
                }
                else if (src2tgt[t].Similarity > 0 && src2tgt[t].Idx > t)
                {
                    // Otherwise, if we just overwrote a source, update mapping:
                    // what used to be at src t is now at src s.
                    tgt2src[src2tgt[t].Idx].Idx = s;
                }
            }
        }
        else if ((opts.Flags & GitDiffFindFlags.Copies) != 0)
        {
            // Scenario 5: COPY
            if (bestMatch.Similarity < opts.CopyThreshold)
            {
                return;
            }

            tgt.Status = GitDeltaStatus.Copied;
            tgt.Similarity = bestMatch.Similarity;
            tgt.FileCount = 2;
            tgt.OldFile = DupFile(src.OldFile);
            tgt.OldFile.InternalFlags &= ~DiffInternalFlags.ToSplit;
            numUpdates++;
        }
    }

    // ━━ Delta predicates ━━

    private static bool DeltaIsSplit(GitDiffDelta delta)
    {
        return delta.Status == GitDeltaStatus.Typechange ||
               (delta.OldFile.InternalFlags & DiffInternalFlags.ToSplit) != 0;
    }

    private static bool DeltaIsNewOnly(GitDiffDelta delta)
    {
        return delta.Status is GitDeltaStatus.Added or GitDeltaStatus.Untracked
            or GitDeltaStatus.Unreadable or GitDeltaStatus.Ignored;
    }

    private static void DeltaMakeRename(GitDiffDelta to, GitDiffDelta from, int similarity)
    {
        to.Status = GitDeltaStatus.Renamed;
        to.Similarity = similarity;
        to.FileCount = 2;
        to.OldFile = DupFile(from.OldFile);
        to.OldFile.InternalFlags &= ~DiffInternalFlags.ToSplit;
    }

    // ━━ Splits and deletes ━━

    /// <summary>
    /// Rebuilds the delta list, removing TO_DELETE entries and splitting
    /// TO_SPLIT entries. Matches <c>apply_splits_and_deletes</c>
    /// (diff_tform.c:372-436). Builds a NEW list (does not mutate during
    /// iteration).
    /// </summary>
    private static void ApplySplitsAndDeletes(
        DiffGenerator gen, IReadOnlyList<GitDiffDelta> deltas, int expectedSize, bool actuallySplit)
    {
        var onto = new List<GitDiffDelta>(expectedSize);

        foreach (GitDiffDelta delta in deltas)
        {
            if ((delta.OldFile.InternalFlags & DiffInternalFlags.ToDelete) != 0)
            {
                continue;
            }

            if ((delta.OldFile.InternalFlags & DiffInternalFlags.ToSplit) != 0 && actuallySplit)
            {
                delta.Similarity = 0;

                // Insert the DELETED side of the split.
                InsertDeleteSideOfSplit(onto, delta);

                // The original delta becomes the ADDED side.
                delta.Status = gen.NewSrc == IteratorType.Workdir
                    ? GitDeltaStatus.Untracked
                    : GitDeltaStatus.Added;
                delta.FileCount = 1;
                delta.OldFile = new GitDiffFile
                {
                    Path = delta.NewFile.Path,
                    Flags = GitDiffFileFlags.ValidId,
                };
            }

            // Clear internal flags before inserting.
            delta.OldFile.InternalFlags &= ~DiffInternalFlags.AllInternal;
            delta.NewFile.InternalFlags &= ~DiffInternalFlags.AllInternal;

            if (delta.Status is not (GitDeltaStatus.Copied or GitDeltaStatus.Renamed) &&
                (delta.Status != GitDeltaStatus.Modified || actuallySplit))
            {
                delta.Similarity = 0;
            }

            onto.Add(delta);
        }

        gen.DeltaList.Clear();
        gen.DeltaList.AddRange(onto);
        gen.DeltaList.Sort(gen._deltaCmp);
    }

    private static void InsertDeleteSideOfSplit(List<GitDiffDelta> onto, GitDiffDelta delta)
    {
        var deleted = new GitDiffDelta(GitDeltaStatus.Deleted, 1,
            DupFile(delta.OldFile),
            new GitDiffFile { Path = delta.OldFile.Path, Flags = GitDiffFileFlags.ValidId });
        onto.Add(deleted);
    }

    // ━━ Options normalization ━━

    /// <summary>
    /// Normalizes find options: reads config, sets defaults, applies flag
    /// implications. Matches <c>normalize_find_opts</c> (diff_tform.c:246-353).
    /// </summary>
    private static async Task<GitDiffFindOptions> NormalizeFindOptsAsync(DiffGenerator gen, GitDiffFindOptions? given, CancellationToken cancellationToken)
    {
        // C (normalize_find_opts, diff_tform.c:248-250): a NULL opts pointer
        // means GIT_DIFF_FIND_OPTIONS_INIT — ALL flags zero — and the
        // diff.renames config alone decides (so "off"/"no" disables renames).
        // (The C# GitDiffFindOptions.Default type carries Renames for direct
        // users of the options record.)
        GitDiffFindOptions opts = given ?? new GitDiffFindOptions { Flags = default };

        // If BY_CONFIG (or no options given), read diff.renames config
        // (diff_tform.c:263-283). Values: "false" → no renames;
        // "copies"/"copy" → Renames | Copies; anything else (incl. "true")
        // → Renames.
        if (given is null || (opts.Flags & GitDiffFindFlags.All) == GitDiffFindFlags.ByConfig)
        {
            // byte-domain read (C's git_config__get_string_force returns raw bytes; the "true" default is the ASCII literal).
            byte[]? renamesCfg = await gen.Repo.Config.GetBytesAsync("diff.renames", cancellationToken).ConfigureAwait(false);
            ReadOnlySpan<byte> renames = renamesCfg ?? "true"u8;
            if (ConfigKeyName.AsciiEqualsIgnoreCase(renames, "copies"u8) ||
                ConfigKeyName.AsciiEqualsIgnoreCase(renames, "copy"u8))
            {
                // C (diff_tform.c:271-276): strcasecmp — case-insensitive.
                opts = opts with { Flags = opts.Flags | GitDiffFindFlags.Renames | GitDiffFindFlags.Copies };
            }
            else if (!ConfigurationValueParser.TryParseBool(renames, out bool renamesBool) || renamesBool)
            {
                // ParseBool matches git__parse_bool: "true"/"yes"/"on" → true;
                // "false"/"no"/"off"/"" → false. If parse fails, treat as true
                // (git's default). Only an explicit false suppresses renames.
                opts = opts with { Flags = opts.Flags | GitDiffFindFlags.Renames };
            }
        }

        // Flag implications.
        GitDiffFindFlags flags = opts.Flags;
        if ((flags & GitDiffFindFlags.ExactMatchOnly) != 0)
        {
            flags &= ~(GitDiffFindFlags.Rewrites | GitDiffFindFlags.BreakRewrites);
            flags &= ~GitDiffFindFlags.RenamesFromRewrites;
        }

        if ((flags & GitDiffFindFlags.RenamesFromRewrites) != 0)
        {
            flags |= GitDiffFindFlags.Renames;
        }

        if ((flags & GitDiffFindFlags.CopiesFromUnmodified) != 0)
        {
            flags |= GitDiffFindFlags.Copies;
        }

        if ((flags & GitDiffFindFlags.BreakRewrites) != 0)
        {
            flags |= GitDiffFindFlags.Rewrites;
        }

        // Threshold defaults.
        int renameThreshold = UseDefault(opts.RenameThreshold) ? DefaultThreshold : opts.RenameThreshold;
        int rewriteThreshold = UseDefault(opts.RenameFromRewriteThreshold) ? DefaultThreshold : opts.RenameFromRewriteThreshold;
        int copyThreshold = UseDefault(opts.CopyThreshold) ? DefaultThreshold : opts.CopyThreshold;
        int breakThreshold = UseDefault(opts.BreakRewriteThreshold) ? DefaultBreakRewriteThreshold : opts.BreakRewriteThreshold;

        // Rename limit: read diff.renamelimit config when unset
        // (diff_tform.c:322-330).
        int renameLimit = opts.RenameLimit;
        if (renameLimit <= 0)
        {
            renameLimit = await gen.Repo.Config.GetIntAsync("diff.renamelimit", DefaultRenameLimit, cancellationToken).ConfigureAwait(false);
            if (renameLimit <= 0)
            {
                renameLimit = DefaultRenameLimit;
            }
        }

        return opts with
        {
            Flags = flags,
            RenameThreshold = renameThreshold,
            RenameFromRewriteThreshold = rewriteThreshold,
            CopyThreshold = copyThreshold,
            BreakRewriteThreshold = breakThreshold,
            RenameLimit = renameLimit,
        };
    }

    private static bool UseDefault(int value) => value is 0 or > 100;

    // ━━ Helpers ━━

    /// <summary>
    /// A match record: delta index + similarity score. Mutable for eject logic.
    /// </summary>
    private struct FindMatch
    {
        public int Idx;
        public ushort Similarity;
    }
}

/// <summary>
/// File mode utilities. Matches the <c>GIT_MODE_ISBLOB</c> macro.
/// </summary>
internal static class GitFileModeUtils
{
    /// <summary>Returns true if the mode represents a regular blob, exec, or symlink.</summary>
    public static bool IsBlob(GitFileMode mode)
    {
        return mode is GitFileMode.Regular or
               GitFileMode.Executable or
               GitFileMode.Symlink;
    }
}
