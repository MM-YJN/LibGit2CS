// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Repository;

namespace LibGit2CS.Revwalk;

/// <summary>
/// Options for <see cref="GitRepository.DescribeAsync"/>. Managed equivalent of
/// <c>git_describe_options</c> + <c>git_describe_format_options</c> (collapsed).
/// </summary>
public sealed record GitDescribeOptions
{
    /// <summary>
    /// The maximum number of candidate tags tracked during the walk. Matches
    /// <c>GIT_DESCRIBE_DEFAULT_MAX_CANDIDATES_TAGS</c> (describe.h:68).
    /// <c>normalize_options</c> (describe.c:647-648) clamps any larger explicit
    /// value to this.
    /// </summary>
    public const int DefaultMaxCandidateTags = 10;

    /// <summary>
    /// Which refs to consider as tags. Default is annotated tags only.
    /// </summary>
    public GitDescribeStrategy Strategy { get; init; } = GitDescribeStrategy.Default;

    /// <summary>
    /// Maximum number of candidate tags to track during the walk. Default 10;
    /// values above 10 are clamped to 10 (C normalize_options, describe.c:647-648).
    /// </summary>
    public int MaxCandidateTags { get; init; } = DefaultMaxCandidateTags;

    /// <summary>
    /// Minimum abbreviated OID length for the <c>-g&lt;abbrev&gt;</c> suffix.
    /// Default 7. Extended if ambiguous.
    /// </summary>
    public int MinimumAbbreviatedSize { get; init; } = 7;

    /// <summary>
    /// Always emit the long format (<c>tag-0-g&lt;abbrev&gt;</c>) even on exact match.
    /// </summary>
    public bool AlwaysUseLongFormat { get; init; }

    /// <summary>
    /// Optional glob to filter tag names (<c>*</c> does not cross <c>/</c>).
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// Only follow the first parent during the walk.
    /// </summary>
    public bool OnlyFollowFirstParent { get; init; }

    /// <summary>
    /// Fall back to the abbreviated commit OID if no tag is found, instead of
    /// throwing.
    /// </summary>
    public bool ShowCommitOidAsFallback { get; init; }

    /// <summary>
    /// Suffix to append when the workdir is dirty (e.g. <c>-dirty</c>).
    /// Matches <c>git_describe_format_options.dirty_suffix</c>. Only used by
    /// <see cref="GitRepository.DescribeWorkdirAsync"/>. Null/empty suppresses the
    /// suffix entirely.
    /// </summary>
    /// <remarks>
    /// The default is <c>null</c>, matching C's
    /// <c>GIT_DESCRIBE_FORMAT_OPTIONS_INIT</c> (describe.h) which leaves
    /// <c>dirty_suffix = NULL</c>; describe.c:814-815 appends it only when
    /// set.
    /// </remarks>
    public string? DirtySuffix { get; init; }
}
