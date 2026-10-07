// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Revwalk;

/// <summary>Describe strategy. Matches <c>git_describe_strategy_t</c>.</summary>
public enum GitDescribeStrategy
{
    /// <summary>Annotated tags only (default).</summary>
    Default = 0,

    /// <summary>All tags (annotated + lightweight).</summary>
    Tags = 1,

    /// <summary>All refs (tags + branches + remotes).</summary>
    All = 2,
}
