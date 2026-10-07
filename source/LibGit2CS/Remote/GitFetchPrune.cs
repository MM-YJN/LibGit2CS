// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Prune behavior during fetch. Maps to <c>git_fetch_prune_t</c>.
/// </summary>
public enum GitFetchPrune
{
    /// <summary>Use the configured default (<c>remote.*.prune</c> or <c>fetch.prune</c>).</summary>
    Unspecified = 0,

    /// <summary>Prune local tracking refs that no longer exist on the remote.</summary>
    Prune = 1,

    /// <summary>Do not prune.</summary>
    NoPrune = 2,
}
