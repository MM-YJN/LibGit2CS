// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.IO.Compression;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;

namespace LibGit2CS.Diff;

/// <summary> Patch application — pure parse core. Managed port of <c>git_apply__patch</c> + callees (<c>src/libgit2/apply.c</c>:34-447). </summary> <remarks>
/// <para> Applies a <see cref="GitPatch"/> to source content in memory, producing the postimage bytes + filename + mode. This is pure
/// string-in/string-out with NO reader/index/repo dependencies. </para> <para> The bridge (<c>apply_one</c>/<c>apply_deltas</c>/<c>git_apply_to_tree</c>) and
/// <c>git_apply</c> (CHECK + execute paths, apply.c:449-896) live on <see cref="Repository.GitRepository"/> as instance methods (<see
/// cref="Repository.GitRepository.ApplyToTreeAsync"/> / <see cref="Repository.GitRepository.ApplyAsync"/>). This class retains
/// only the pure parse core that operates on a single <see cref="GitPatch"/> with no repo dependency. </para> </remarks>
public static class GitPatchApplier
{
    /// <summary>
    /// Applies a patch to source content in memory. Matches
    /// <c>git_apply__patch</c> (apply.c:389-447) — the pure parse-core entry
    /// point. Returns the postimage content + resulting filename + mode.
    /// </summary>
    public static async Task<GitApplyResult> ApplyPatchAsync(
        ReadOnlyMemory<byte> source, GitPatch patch, GitApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        GitApplyOptions opts = options ?? new GitApplyOptions();
        GitDiffDelta delta = patch.Delta;

        GitPath? filename = null;
        GitFileMode mode = GitFileMode.Regular;

        if (delta.Status != GitDeltaStatus.Deleted)
        {
            filename = delta.NewFile.Path;
            mode = delta.NewFile.Mode != 0 ? delta.NewFile.Mode : GitFileMode.Regular;
        }

        byte[] content;

        if ((delta.Flags & GitDiffFileFlags.Binary) != 0)
        {
            content = await ApplyBinaryAsync(source, patch, cancellationToken).ConfigureAwait(false);
        }
        else if (await patch.GetHunkCountAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            content = await ApplyHunksAsync(source, patch, opts, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            content = source.ToArray();
        }

        if (delta.Status == GitDeltaStatus.Deleted && content.Length > 0)
        {
            // C (apply.c:434-436): apply_err → GIT_EAPPLYFAIL, GIT_ERROR_PATCH.
            throw new GitException(GitErrorCode.ApplyFail, "removal patch leaves file contents", GitErrorCategory.Patch);
        }

        return new GitApplyResult(content, filename, mode);
    }

    // ===== Text hunk application (apply.c:269-297 + 188-267 + 103-180) =====

    /// <summary>Matches <c>apply_hunks</c> (apply.c:269-297).</summary>
    private static async Task<byte[]> ApplyHunksAsync(ReadOnlyMemory<byte> source, GitPatch patch, GitApplyOptions opts, CancellationToken cancellationToken)
    {
        var image = PatchImage.FromBytes(source);
        var ctx = new ApplyHunksContext(opts);

        int hunkCount = await patch.GetHunkCountAsync(cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < hunkCount; i++)
        {
            GitDiffHunk hunk = (await patch.GetHunkAsync(i, cancellationToken).ConfigureAwait(false))
                ?? throw new GitException(GitErrorCode.ApplyFail, $"hunk {i} not found", GitErrorCategory.Patch);
            ApplyHunk(ref image, patch, hunk, ctx);
        }

        return image.ToBytes();
    }

    /// <summary>Matches <c>apply_hunk</c> (apply.c:188-267).</summary>
    private static void ApplyHunk(
        ref PatchImage image, GitPatch _, GitDiffHunk hunk, ApplyHunksContext ctx)
    {
        if (ctx.Options.HunkCallback is { } hunkCb)
        {
            int cbResult = hunkCb(hunk);
            if (cbResult < 0)
            {
                // C
                // propagates the callback's negative return verbatim
                // (apply.c:198-210), so e.g. GIT_EUSER (-7) is preserved;
                throw new GitException((GitErrorCode)cbResult,
                    $"hunk callback aborted (return {cbResult})", GitErrorCategory.Callback);
            }

            if (cbResult > 0)
            {
                ctx.SkippedNewLines += hunk.NewCount;
                ctx.SkippedOldLines += hunk.OldCount;
                return;
            }
        }

        var preimage = new PatchImage();
        var postimage = new PatchImage();
        GitDiffLineOrigin prevOrigin = 0;

        foreach (GitDiffLine line in hunk.Lines)
        {
            switch (line.Origin)
            {
                case GitDiffLineOrigin.ContextEofnl:
                case GitDiffLineOrigin.DelEofnl:
                case GitDiffLineOrigin.AddEofnl:
                    // Strip trailing \n from the previous line that was added to
                    // preimage/postimage. Matches C's `prev->content_len -= 1`
                    // where `prev` is the patch line just before the EOFNL marker.
                    // The previous line was added to preimage (if deletion) or
                    // postimage (if addition) or both (if context).
                    if (prevOrigin == GitDiffLineOrigin.Context)
                    {
                        TrimTrailingNewline(preimage);
                        TrimTrailingNewline(postimage);
                    }
                    else if (prevOrigin == GitDiffLineOrigin.Deletion)
                    {
                        TrimTrailingNewline(preimage);
                    }
                    else if (prevOrigin == GitDiffLineOrigin.Addition)
                    {
                        TrimTrailingNewline(postimage);
                    }
                    break;
                case GitDiffLineOrigin.Context:
                    preimage.Add(line);
                    postimage.Add(line);
                    prevOrigin = line.Origin;
                    break;
                case GitDiffLineOrigin.Deletion:
                    preimage.Add(line);
                    prevOrigin = line.Origin;
                    break;
                case GitDiffLineOrigin.Addition:
                    postimage.Add(line);
                    prevOrigin = line.Origin;
                    break;
            }
        }

        int lineNum;
        if (hunk.NewStart != 0)
        {
            lineNum = hunk.NewStart - ctx.SkippedNewLines + ctx.SkippedOldLines - 1;
        }
        else
        {
            lineNum = 0;
        }

        if (!FindHunkLineNum(out int matched, image, preimage, lineNum))
        {
            throw new GitException(GitErrorCode.ApplyFail,
                $"hunk at line {hunk.NewStart} did not apply",
                GitErrorCategory.Patch);
        }

        lineNum = matched;
        UpdateHunk(ref image, lineNum, preimage, postimage);
    }

    private static void TrimTrailingNewline(PatchImage image)
    {
        if (image.Lines.Count == 0)
        {
            return;
        }

        GitDiffLine last = image.Lines[^1];
        if (last.Content.Length > 0 && last.Content.Span[^1] == '\n')
        {
            image.Lines[^1] = new GitDiffLine(
                last.Origin, last.OldLine, last.NewLine, last.LineCount,
                last.Content.Slice(0, last.Content.Length - 1));
        }
    }

    /// <summary>Matches <c>match_hunk</c> (apply.c:103-131).</summary>
    private static bool MatchHunk(PatchImage image, PatchImage preimage, int lineNum)
    {
        if (preimage.Lines.Count + lineNum > image.Lines.Count)
        {
            return false;
        }

        for (int i = 0; i < preimage.Lines.Count; i++)
        {
            GitDiffLine preLine = preimage.Lines[i];
            GitDiffLine imgLine = image.Lines[lineNum + i];
            if (preLine.Content.Length != imgLine.Content.Length ||
                !preLine.Content.Span.SequenceEqual(imgLine.Content.Span))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Matches <c>find_hunk_linenum</c> (apply.c:133-149).</summary>
    private static bool FindHunkLineNum(out int lineNum, PatchImage image, PatchImage preimage, int startLineNum)
    {
        int max = image.Lines.Count;

        // C computes
        // line_num in size_t — a negative result (a skipping hunk callback
        // plus a later hunk with a small new_start) wraps to a huge value and
        // is clamped to max, so match_hunk fails cleanly with
        // GIT_EAPPLYFAIL (apply.c:245-258) instead of indexing a negative
        // line. Mirror the wrap-to-max clamp.
        if (startLineNum < 0)
        {
            startLineNum = max;
        }

        lineNum = startLineNum > max ? max : startLineNum;
        return MatchHunk(image, preimage, lineNum);
    }

    /// <summary>Matches <c>update_hunk</c> (apply.c:151-180).</summary>
    private static void UpdateHunk(ref PatchImage image, int lineNum, PatchImage preimage, PatchImage postimage)
    {
        int postlen = postimage.Lines.Count;
        int prelen = preimage.Lines.Count;

        if (postlen > prelen)
        {
            // Insert null slots at lineNum for (postlen - prelen) entries.
            for (int i = 0; i < postlen - prelen; i++)
            {
                image.Lines.Insert(lineNum, default);
            }
        }
        else if (prelen > postlen)
        {
            // Remove (prelen - postlen) entries at lineNum.
            image.Lines.RemoveRange(lineNum, prelen - postlen);
        }

        for (int i = 0; i < postimage.Lines.Count; i++)
        {
            image.Lines[lineNum + i] = postimage.Lines[i];
        }
    }

    // ===== Binary application (apply.c:299-387) =====

    /// <summary>Matches <c>apply_binary</c> (apply.c:347-387).</summary>
    private static async Task<byte[]> ApplyBinaryAsync(ReadOnlyMemory<byte> source, GitPatch patch, CancellationToken cancellationToken)
    {
        GitBinaryPatch binary = await patch.GetBinaryAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.ApplyFail, "patch does not contain binary data", GitErrorCategory.Patch);

        if (!binary.ContainsData)
        {
            throw new GitException(GitErrorCode.ApplyFail, "patch does not contain binary data", GitErrorCategory.Patch);
        }

        // C's apply_binary
        // does `if (!old.datalen && !new.datalen) goto done;` with the
        // caller's postimage buffer (GIT_STR_INIT) left EMPTY — the postimage
        // of a 'GIT binary patch\nliteral 0\n\nliteral 0\n\n' patch is empty,
        // NOT the source. Returning the source unchanged would make Deleted
        // deltas throw 'removal patch leaves file contents' where C succeeds.
        if (binary.OldFile.Data.Length == 0 && binary.NewFile.Data.Length == 0)
        {
            return Array.Empty<byte>();
        }

        // Apply new_file delta to source → produces postimage.
        byte[] postimage = ApplyBinaryDelta(source, binary.NewFile);

        // Apply old_file delta to postimage → sanity check (should match source).
        byte[] reverse = ApplyBinaryDelta(postimage, binary.OldFile);

        if (source.Length != reverse.Length ||
            (source.Length > 0 && !source.Span.SequenceEqual(reverse)))
        {
            throw new GitException(GitErrorCode.ApplyFail, "binary patch did not apply cleanly", GitErrorCategory.Patch);
        }

        return postimage;
    }

    /// <summary>Matches <c>apply_binary_delta</c> (apply.c:299-345).</summary>
    private static byte[] ApplyBinaryDelta(ReadOnlyMemory<byte> source, GitBinaryFile binaryFile)
    {
        // No diff means identical contents.
        if (binaryFile.Data.Length == 0)
        {
            return source.ToArray();
        }

        // Inflate the zlib-compressed delta/literal data into a buffer sized by
        // the declared inflated length. C's git_zstream_inflatebuf grows the
        // destination and only checks the length afterwards (apply.c:312-318);
        // the port fails at the destination boundary instead, with the same
        // GIT_EAPPLYFAIL "inflated delta does not match expected length" — a
        // crafted tiny stream that decompresses to gigabytes errors cleanly
        // instead of forcing a multi-GB allocation (pattern).
        if (binaryFile.InflatedLength is < 0 or > int.MaxValue)
        {
            throw new GitException(GitErrorCode.ApplyFail, "inflated delta does not match expected length", GitErrorCategory.Patch);
        }

        byte[] inflated = new byte[(int)binaryFile.InflatedLength];
        using var decoder = new ZLibDecoder();
        int totalWritten = 0;
        ReadOnlySpan<byte> remaining = binaryFile.Data;
        while (true)
        {
            OperationStatus status = decoder.Decompress(
                remaining,
                inflated.AsSpan(totalWritten),
                out int consumed,
                out int written);

            totalWritten += written;
            remaining = remaining[consumed..];

            if (status == OperationStatus.Done)
            {
                break;
            }

            switch (status)
            {
                case OperationStatus.NeedMoreData:
                    if (remaining.IsEmpty)
                    {
                        throw new InvalidDataException("truncated zlib stream");
                    }

                    continue;
                case OperationStatus.DestinationTooSmall:
                    // Decompressed output exceeds the declared length.
                    throw new GitException(GitErrorCode.ApplyFail, "inflated delta does not match expected length", GitErrorCategory.Patch);
                default: // OperationStatus.InvalidData
                    throw new InvalidDataException("corrupt zlib stream");
            }
        }

        if (totalWritten != inflated.Length)
        {
            throw new GitException(GitErrorCode.ApplyFail, "inflated delta does not match expected length", GitErrorCategory.Patch);
        }

        if (binaryFile.Type == GitBinaryPatchType.Delta)
        {
            return DeltaEncoder.Apply(source.Span, inflated);
        }

        if (binaryFile.Type == GitBinaryPatchType.Literal)
        {
            return inflated;
        }

        throw new GitException(GitErrorCode.ApplyFail, "unknown binary delta type", GitErrorCategory.Patch);
    }

    // ===== Internal types =====

    /// <summary>
    /// Represents a file as a list of lines for hunk matching/splicing. Managed
    /// equivalent of <c>patch_image</c> (apply.c:26-32).
    /// </summary>
    private sealed class PatchImage
    {
        public List<GitDiffLine> Lines { get; } = [];

        public static PatchImage FromBytes(ReadOnlyMemory<byte> source)
        {
            var image = new PatchImage();
            if (source.Length == 0)
            {
                return image;
            }

            ReadOnlySpan<byte> span = source.Span;
            int start = 0;
            for (int i = 0; i <= span.Length; i++)
            {
                if (i == span.Length || span[i] == '\n')
                {
                    int lineLen = i - start + (i < span.Length ? 1 : 0); // include \n
                    ReadOnlyMemory<byte> lineContent = source.Slice(start, lineLen);
                    image.Lines.Add(new GitDiffLine(
                        GitDiffLineOrigin.Context, 0, 0, 1, lineContent));
                    start = i + 1;
                }
            }

            return image;
        }

        public void Add(GitDiffLine line) => Lines.Add(line);

        public byte[] ToBytes()
        {
            int total = 0;
            foreach (GitDiffLine line in Lines)
            {
                total += line.Content.Length;
            }

            byte[] result = new byte[total];
            int offset = 0;
            foreach (GitDiffLine line in Lines)
            {
                line.Content.Span.CopyTo(result.AsSpan(offset));
                offset += line.Content.Length;
            }

            return result;
        }
    }

    /// <summary>Matches <c>apply_hunks_ctx</c> (apply.c:182-186).</summary>
    private sealed class ApplyHunksContext
    {
        public GitApplyOptions Options { get; }
        public int SkippedNewLines { get; set; }
        public int SkippedOldLines { get; set; }

        public ApplyHunksContext(GitApplyOptions opts)
        {
            Options = opts;
        }
    }
}
