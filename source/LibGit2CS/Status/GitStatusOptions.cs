// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Status;

/// <summary>
/// Status computation options. Managed port of <c>git_status_options</c>
/// (status.h:222-262). Drops the <c>version</c> field (managed ABI).
/// </summary>
public sealed record GitStatusOptions
{
    /// <summary>Default options matching <c>GIT_STATUS_OPT_DEFAULTS</c>.</summary>
    public static readonly GitStatusOptions Default = new()
    {
        Show = GitStatusShow.IndexAndWorkdir,
        Flags = GitStatusFlags.IncludeIgnored
              | GitStatusFlags.IncludeUntracked
              | GitStatusFlags.RecurseUntrackedDirs,
    };

    /// <summary>Which sides to compute. Default <see cref="GitStatusShow.IndexAndWorkdir"/>.</summary>
    public GitStatusShow Show { get; init; } = GitStatusShow.IndexAndWorkdir;

    /// <summary>Option flags controlling what to include and how to scan.</summary>
    public GitStatusFlags Flags { get; init; }

    /// <summary> Pathspec patterns or literal paths. Byte-faithful primary; use <see cref="PathSpecStrings"/> for the <c>string[]</c> convenience. </summary>
    public GitPath[]? PathSpecs { get; init; }

    /// <summary>
    /// <c>string[]</c> convenience for <see cref="PathSpecs"/>. Setter encodes
    /// via <see cref="GitPath.FromUtf8String"/>; getter decodes via
    /// <see cref="GitPath.ToUtf8String"/>. Prefer <see cref="PathSpecs"/> for
    /// byte-faithful paths.
    /// </summary>
    public string[]? PathSpecStrings
    {
        get => PathSpecs is { } ps ? Array.ConvertAll(ps, p => p.ToUtf8String()) : null;
        init => PathSpecs = value is null ? null : Array.ConvertAll(value, GitPath.FromUtf8String);
    }

    /// <summary>
    /// Tree to compare against (default HEAD). Matches
    /// <c>git_status_options.baseline</c>.
    /// </summary>
    public Objects.GitTree? Baseline { get; init; }

    /// <summary>Similarity threshold for rename detection (default 50).</summary>
    public int RenameThreshold { get; init; } = 50;
}
