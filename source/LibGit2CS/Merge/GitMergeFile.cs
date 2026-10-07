// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;

using Xdiff;

namespace LibGit2CS.Merge;

/// <summary>
/// File-level 3-way merge facade. Matches <c>git_merge_file</c> and
/// <c>git_merge_file_from_index</c> in <c>src/libgit2/merge_file.c</c>.
/// </summary>
/// <remarks>
/// Delegates to <see cref="Merger.Merge(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, MergeOptions?)"/> for the actual text merge
/// (xdl_merge equivalent). Handles binary file detection, binary merge
/// (favor-based), path/mode selection, and ODB reads for
/// <see cref="LibGit2CS.Repository.GitRepository.MergeFileFromIndexAsync"/>.
/// </remarks>
public static class GitMergeFile
{
    /// <summary>
    /// Only examine the first 8000 bytes for binary detection. Matches
    /// <c>GIT_MERGE_FILE_BINARY_SIZE</c> in <c>merge_file.c:25</c>.
    /// </summary>
    private const int BinaryDetectionSize = 8000;

    /// <summary>
    /// Maximum file size that xdiff will process. Files larger than this are
    /// treated as binary. Matches <c>GIT_XDIFF_MAX_SIZE</c>
    /// (1023 MiB).
    /// </summary>
    private const long XdiffMaxSize = (long)1024 * 1024 * 1023;

    /// <summary>
    /// Merges three in-memory file buffers. Matches <c>git_merge_file</c>
    /// (<c>merge_file.c:250-275</c>).
    /// </summary>
    /// <param name="ancestor">The ancestor (merge base) input, or <c>null</c>
    /// if no ancestor exists.</param>
    /// <param name="ours">Our side. Must not be <c>null</c>.</param>
    /// <param name="theirs">Their side. Must not be <c>null</c>.</param>
    /// <param name="options">Optional merge file options. If <c>null</c>,
    /// uses <see cref="GitMergeFileOptions.Default"/>.</param>
    /// <returns>The merge result.</returns>
    public static GitMergeFileResult Merge(
        GitMergeFileInput? ancestor,
        GitMergeFileInput ours,
        GitMergeFileInput theirs,
        GitMergeFileOptions? options = null)
    {
        GitMergeFileInput? normalizedAncestor = ancestor is { } a ? NormalizeInput(a) : null;
        GitMergeFileInput normalizedOurs = NormalizeInput(ours);
        GitMergeFileInput normalizedTheirs = NormalizeInput(theirs);

        return FromInputs(normalizedAncestor, normalizedOurs, normalizedTheirs, options);
    }

    // ── Internal dispatch ────────────────────────────────────────────────

    /// <summary>
    /// Dispatcher: text vs binary. Matches <c>merge_file__from_inputs</c>
    /// (<c>merge_file.c:220-233</c>).
    /// </summary>
    internal static GitMergeFileResult FromInputs(
        GitMergeFileInput? ancestor,
        GitMergeFileInput? ours,
        GitMergeFileInput? theirs,
        GitMergeFileOptions? options)
    {
        if (IsBinary(ancestor) || IsBinary(ours) || IsBinary(theirs))
        {
            return BinaryMerge(ours, theirs, options);
        }

        Debug.Assert(ours is not null && theirs is not null, "ours and theirs must be non-null for xdiff merge");
        return XdiffMerge(ancestor, ours.Value, theirs.Value, options);
    }

    /// <summary>
    /// Normalizes a single input: null path → "file.txt", mode 0 → 0100644.
    /// Matches <c>git_merge_file__normalize_inputs</c>
    /// (<c>merge_file.c:235-248</c>).
    /// </summary>
    internal static GitMergeFileInput NormalizeInput(GitMergeFileInput input)
    {
        GitPath path = input.Path ?? GitPath.FromUtf8String("file.txt");
        uint mode = input.Mode == 0 ? (uint)GitFileMode.Regular : input.Mode;
        return input with { Path = path, Mode = mode };
    }

    // ── Text merge (xdiff) ───────────────────────────────────────────────

    /// <summary>
    /// Core text merge via xdiff. Matches <c>merge_file__xdiff</c>
    /// (<c>merge_file.c:66-176</c>).
    /// </summary>
    private static GitMergeFileResult XdiffMerge(
        GitMergeFileInput? ancestor,
        GitMergeFileInput ours,
        GitMergeFileInput theirs,
        GitMergeFileOptions? givenOpts)
    {
        GitMergeFileOptions opts = givenOpts ?? GitMergeFileOptions.Default;

        // Map MergeFileOptions → Xdiff.MergeOptions.
        MergeOptions xdiffOpts = MapToXdiffOptions(opts, ancestor, ours, theirs);

        MergeResult mergeResult = Merger.Merge(ancestor?.Contents ?? ReadOnlyMemory<byte>.Empty, ours.Contents, theirs.Contents, xdiffOpts);

        GitPath? path = BestPath(
            ancestor is { } an ? an.Path : null,
            ours.Path,
            theirs.Path);

        uint mode = BestMode(
            ancestor is { } am ? am.Mode : 0,
            ours.Mode,
            theirs.Mode);

        return new GitMergeFileResult
        {
            Automergeable = mergeResult.ConflictCount == 0,
            Path = path,
            Mode = mode,
            Content = mergeResult.Content,
            ConflictCount = mergeResult.ConflictCount,
        };
    }

    /// <summary>
    /// Maps <see cref="GitMergeFileOptions"/> + input paths to
    /// <see cref="MergeOptions"/>.
    /// </summary>
    private static MergeOptions MapToXdiffOptions(
        GitMergeFileOptions opts,
        GitMergeFileInput? ancestor,
        GitMergeFileInput ours,
        GitMergeFileInput theirs)
    {
        // Labels: option label takes priority, otherwise fall back to input path.
        string? ancestorLabel = opts.AncestorLabel ?? (ancestor is { } a && a.Contents.Length > 0 ? a.Path?.ToUtf8String() : null);
        string? ourLabel = opts.OurLabel ?? (ours.Contents.Length > 0 ? ours.Path?.ToUtf8String() : null);
        string? theirLabel = opts.TheirLabel ?? (theirs.Contents.Length > 0 ? theirs.Path?.ToUtf8String() : null);

        GitMergeFileFlags flags = opts.Flags;
        WhitespaceMode whitespace = WhitespaceMode.None;
        if ((flags & GitMergeFileFlags.IgnoreWhitespace) != 0)
        {
            whitespace |= WhitespaceMode.IgnoreAll;
        }

        if ((flags & GitMergeFileFlags.IgnoreWhitespaceChange) != 0)
        {
            whitespace |= WhitespaceMode.IgnoreChanges;
        }

        if ((flags & GitMergeFileFlags.IgnoreWhitespaceEol) != 0)
        {
            whitespace |= WhitespaceMode.IgnoreAtEol;
        }

        // C (merge_file.c:138-142): XDF_PATIENCE_DIFF and XDF_NEED_MINIMAL are OR'd independently, but the xdiff engine's algorithm selection masks only
        // PATIENCE|HISTOGRAM (XDF_DIFF_ALGORITHM_MASK, xdiffi.c) — with BOTH bits set PATIENCE wins and NEED_MINIMAL is inert, so the single-value
        // selection must prefer Patience over Minimal, which the if/else-if below does.
        DiffAlgorithm algorithm = DiffAlgorithm.Myers;
        if ((flags & GitMergeFileFlags.DiffPatience) != 0)
        {
            algorithm = DiffAlgorithm.Patience;
        }
        else if ((flags & GitMergeFileFlags.DiffMinimal) != 0)
        {
            algorithm = DiffAlgorithm.Minimal;
        }

        // C (merge_file.c:126-129): two INDEPENDENT ifs — ZDIFF3 is written LAST, so it wins when both DIFF3 and ZDIFF3 are set. An if/else-if would
        // make DIFF3 win.
        MergeStyle style = MergeStyle.Merge;
        if ((flags & GitMergeFileFlags.StyleZdiff3) != 0)
        {
            style = MergeStyle.ZealousDiff3;
        }
        else if ((flags & GitMergeFileFlags.StyleDiff3) != 0)
        {
            style = MergeStyle.Diff3;
        }

        MergeLevel level = MergeLevel.Zealous;
        if ((flags & GitMergeFileFlags.SimplifyAlnum) != 0)
        {
            level = MergeLevel.ZealousAlnum;
        }

        MergeFavor favor = opts.Favor switch
        {
            GitMergeFileFavor.Ours => MergeFavor.Ours,
            GitMergeFileFavor.Theirs => MergeFavor.Theirs,
            GitMergeFileFavor.Union => MergeFavor.Union,
            _ => MergeFavor.Default,
        };

        int markerSize = opts.MarkerSize <= 0 ? 7 : opts.MarkerSize;

        return new MergeOptions
        {
            Algorithm = algorithm,
            Whitespace = whitespace,
            Level = level,
            Favor = favor,
            Style = style,
            MarkerSize = markerSize,
            AncestorLabel = ancestorLabel,
            OurLabel = ourLabel,
            TheirLabel = theirLabel,
        };
    }

    // ── Binary merge ────────────────────────────────────────────────────

    /// <summary>
    /// Binary merge: always picks the favored side, or returns an empty
    /// conflict result if no favor is set. Matches <c>merge_file__binary</c>
    /// (<c>merge_file.c:190-218</c>).
    /// </summary>
    private static GitMergeFileResult BinaryMerge(
        GitMergeFileInput? ours,
        GitMergeFileInput? theirs,
        GitMergeFileOptions? givenOpts)
    {
        GitMergeFileOptions opts = givenOpts ?? GitMergeFileOptions.Default;

        GitMergeFileInput? favored = null;
        if (opts.Favor == GitMergeFileFavor.Ours)
        {
            favored = ours;
        }
        else if (opts.Favor == GitMergeFileFavor.Theirs)
        {
            favored = theirs;
        }

        if (favored is null)
        {
            // No favor — return empty, not automergeable.
            return new GitMergeFileResult
            {
                Automergeable = false,
                Path = null,
                Mode = 0,
                Content = ReadOnlyMemory<byte>.Empty,
                ConflictCount = 0,
            };
        }

        GitMergeFileInput f = favored.Value;
        return new GitMergeFileResult
        {
            Automergeable = true,
            Path = f.Path,
            Mode = f.Mode,
            Content = f.Contents,
            ConflictCount = 0,
        };
    }

    // ── Binary detection ─────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if the file contains a NUL byte in the first 8000
    /// bytes, or if the file exceeds <c>GIT_XDIFF_MAX_SIZE</c>. Matches
    /// <c>merge_file__is_binary</c> (<c>merge_file.c:178-188</c>).
    /// </summary>
    private static bool IsBinary(GitMergeFileInput? input)
    {
        if (input is null)
        {
            return false;
        }

        ReadOnlySpan<byte> span = input.Value.Contents.Span;
        int len = span.Length;

        if (len > XdiffMaxSize)
        {
            return true;
        }

        int checkLen = Math.Min(len, BinaryDetectionSize);
        if (checkLen == 0)
        {
            return false;
        }

        return span[..checkLen].Contains((byte)0);
    }

    // ── Path / mode selection ────────────────────────────────────────────

    /// <summary>
    /// Selects the best path from the three inputs. Matches
    /// <c>git_merge_file__best_path</c> (<c>merge.h:160-182</c>).
    /// </summary>
    internal static GitPath? BestPath(GitPath? ancestor, GitPath? ours, GitPath? theirs)
    {
        if (ancestor is null)
        {
            if (ours is not null && theirs is not null && ours.Value == theirs.Value)
            {
                return ours;
            }

            if (ours is not null && theirs is null)
            {
                return ours;
            }

            if (theirs is not null && ours is null)
            {
                return theirs;
            }

            return null;
        }

        if (ours is not null && ancestor.Value == ours.Value)
        {
            return theirs;
        }

        if (theirs is not null && ancestor.Value == theirs.Value)
        {
            return ours;
        }

        return null;
    }

    /// <summary>
    /// Selects the best mode from the three inputs. Matches
    /// <c>git_merge_file__best_mode</c> (<c>merge.h:184-206</c>).
    /// </summary>
    internal static uint BestMode(uint ancestor, uint ours, uint theirs)
    {
        if (ancestor == 0)
        {
            if (ours == (uint)GitFileMode.Executable ||
                theirs == (uint)GitFileMode.Executable)
            {
                return (uint)GitFileMode.Executable;
            }

            return (uint)GitFileMode.Regular;
        }

        if (ours != 0 && theirs != 0)
        {
            if (ancestor == ours)
            {
                return theirs;
            }

            return ours;
        }

        return 0;
    }
}
