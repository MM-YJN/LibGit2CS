// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// User's fast-forward preference, derived from <c>merge.ff</c> config.
/// Matches <c>git_merge_preference_t</c> in <c>include/git2/merge.h</c>.
/// </summary>
[Flags]
public enum GitMergePreference
{
    /// <summary>No preference (default: allow fast-forward).</summary>
    None = 0,

    /// <summary>
    /// <c>merge.ff=false</c> — always create a merge commit, even when
    /// fast-forward is possible.
    /// </summary>
    NoFastForward = 1 << 0,

    /// <summary>
    /// <c>merge.ff=only</c> — abort if the merge cannot be fast-forwarded.
    /// </summary>
    FastForwardOnly = 1 << 1,
}
