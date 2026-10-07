// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Rebase;

/// <summary>
/// A single rebase operation. Matches <c>git_rebase_operation</c> in
/// <c>include/git2/rebase.h:174-189</c>.
/// </summary>
/// <remarks>
/// For all operation types except <see cref="GitRebaseOperationType.Exec"/>,
/// <see cref="Id"/> is the commit OID being cherry-picked. For
/// <see cref="GitRebaseOperationType.Exec"/>, <see cref="Exec"/> is the
/// command to run, and <see cref="Id"/> is zero.
/// </remarks>
public readonly record struct GitRebaseOperation(
    GitRebaseOperationType Type,
    GitOid Id,
    string? Exec)
{
    /// <summary>Creates a PICK operation for the given commit OID.</summary>
    public static GitRebaseOperation CreatePick(GitOid id) => new(GitRebaseOperationType.Pick, id, null);
}
