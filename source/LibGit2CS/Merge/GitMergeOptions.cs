// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// Options for 3-way tree/index merge. Matches <c>git_merge_options</c>
/// in <c>include/git2/merge.h</c>.
/// </summary>
public sealed record GitMergeOptions
{
    /// <summary>
    /// Merge behavior flags. Default includes <see cref="GitMergeFlags.FindRenames"/>,
    /// matching <c>GIT_MERGE_OPTIONS_INIT</c> (<c>merge.h:329-330</c>).
    /// </summary>
    public GitMergeFlags Flags { get; init; } = GitMergeFlags.FindRenames;

    /// <summary>
    /// Rename detection threshold (0-100). Only used when
    /// <see cref="Flags"/> includes <see cref="GitMergeFlags.FindRenames"/>.
    /// 0 means "use the default" (50); <see cref="LibGit2CS.Repository.GitRepository.NormalizeMergeOptionsAsync"/>
    /// fills in 50 when left at 0. Renames at or above this similarity are
    /// coalesced (exact OID match always = 100; inexact via
    /// <see cref="Core.SimilarityHash"/>).
    /// </summary>
    public int RenameThreshold { get; init; } = 50;

    /// <summary>
    /// Maximum rename targets. 0 means "use config default" —
    /// <see cref="LibGit2CS.Repository.GitRepository.NormalizeMergeOptionsAsync"/> reads <c>merge.renamelimit</c>
    /// then <c>diff.renamelimit</c>, falling back to 200.
    /// </summary>
    public int TargetLimit { get; init; }

    /// <summary>
    /// Which side to favor for content conflicts. Default
    /// <see cref="GitMergeFileFavor.Normal"/> (leave conflicts marked).
    /// Matches <c>git_merge_options.file_favor</c> (<c>merge.h:319</c>).
    /// </summary>
    public GitMergeFileFavor Favor { get; init; } = GitMergeFileFavor.Normal;

    /// <summary>
    /// File-level merge flags passed to the merge driver. Matches
    /// <c>git_merge_options.file_flags</c> (<c>merge.h:322</c>).
    /// Default <see cref="GitMergeFileFlags.None"/>.
    /// </summary>
    public GitMergeFileFlags FileFlags { get; init; } = GitMergeFileFlags.None;

    /// <summary>
    /// Maximum recursion depth for recursive merge base computation.
    /// 0 = unlimited. Matches <c>git_merge_options.recursion_limit</c>
    /// (<c>merge.h:307</c>). When multiple merge bases exist, they are
    /// recursively merged to produce a single virtual ancestor; this
    /// limits how deep that recursion can go. Default 0 (unlimited).
    /// </summary>
    public int RecursionLimit { get; init; }

    /// <summary>
    /// Default merge driver name. When null, <see cref="LibGit2CS.Repository.GitRepository.NormalizeMergeOptionsAsync"/>
    /// reads <c>merge.default</c> from repo config (matches
    /// <c>merge_normalize_opts</c>, <c>merge.c:1904-1914</c>).
    /// </summary>
    public string? DefaultDriver { get; init; }

    /// <summary>
    /// Default options — matches <c>GIT_MERGE_OPTIONS_INIT</c>:
    /// <see cref="GitMergeFlags.FindRenames"/> on, threshold 50.
    /// </summary>
    public static GitMergeOptions Default { get; } = new();
}
