// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// Result of <see cref="IFilter.CheckAsync"/>. Maps to the return values of
/// C's <c>git_filter_check_fn</c>: <c>0</c> → <see cref="Apply"/>,
/// <c>GIT_PASSTHROUGH</c> → <see cref="Passthrough"/>.
/// </summary>
public enum GitFilterResult
{
    /// <summary>The filter should be applied to this file. (C returns 0.)</summary>
    Apply = 0,

    /// <summary>The filter does not apply; pass content through unchanged. (C returns <c>GIT_PASSTHROUGH</c>.)</summary>
    Passthrough = 1,
}
