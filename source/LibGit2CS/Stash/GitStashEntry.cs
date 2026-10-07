// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.Stash;

/// <summary>
/// A single stash entry from the stash reflog. Returned by
/// <see cref="GitRepository.StashForEachAsync"/>.
/// </summary>
public readonly record struct GitStashEntry
{
    /// <summary>The zero-based index in the stash list (0 = most recent).</summary>
    public int Index { get; init; }

    /// <summary>The reflog message for this stash entry.</summary>
    public string Message { get; init; }

    /// <summary>The commit OID of the stash commit.</summary>
    public GitOid CommitId { get; init; }

    /// <summary>Creates a stash entry with its stack index, message, and commit ID.</summary>
    public GitStashEntry(int index, string message, GitOid commitId)
    {
        Index = index;
        Message = message;
        CommitId = commitId;
    }
}
