// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// Options for file-level 3-way merge. Matches <c>git_merge_file_options</c>
/// in <c>include/git2/merge.h</c>.
/// </summary>
public sealed record GitMergeFileOptions
{
    /// <summary>
    /// Label for the ancestor side in diff3/zdiff3 output. If <c>null</c>,
    /// the ancestor input's <see cref="GitMergeFileInput.Path"/> is used.
    /// </summary>
    public string? AncestorLabel { get; init; }

    /// <summary>
    /// Label for our side in conflict markers. If <c>null</c>, the our input's
    /// <see cref="GitMergeFileInput.Path"/> is used.
    /// </summary>
    public string? OurLabel { get; init; }

    /// <summary>
    /// Label for their side in conflict markers. If <c>null</c>, the their
    /// input's <see cref="GitMergeFileInput.Path"/> is used.
    /// </summary>
    public string? TheirLabel { get; init; }

    /// <summary>
    /// Which side to favor for content conflicts. Default
    /// <see cref="GitMergeFileFavor.Normal"/> (leave conflicts marked).
    /// </summary>
    public GitMergeFileFavor Favor { get; init; } = GitMergeFileFavor.Normal;

    /// <summary>
    /// Merge behavior flags (style, whitespace, diff algorithm, etc.).
    /// </summary>
    public GitMergeFileFlags Flags { get; init; }

    /// <summary>
    /// Number of characters used to build each conflict marker. Default
    /// <c>7</c>. Values ≤ <c>0</c> fall back to <c>7</c>.
    /// </summary>
    public ushort MarkerSize { get; init; } = 7;

    /// <summary>Default options — matches <c>GIT_MERGE_FILE_OPTIONS_INIT</c>.</summary>
    public static GitMergeFileOptions Default { get; } = new();
}
