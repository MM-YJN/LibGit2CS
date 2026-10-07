// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Refs;

namespace LibGit2CS.Repository;

/// <summary>
/// Flags controlling worktree pruning. Managed port of
/// <c>git_worktree_prune_t</c> (include/git2/worktree.h:194-201).
/// </summary>
[Flags]
public enum WorktreePruneFlags
{
    /// <summary>Prune even if the worktree is valid.</summary>
    Valid = 1 << 0,

    /// <summary>Prune even if the worktree is locked.</summary>
    Locked = 1 << 1,

    /// <summary>Also delete the actual working tree directory.</summary>
    WorkingTree = 1 << 2,
}

/// <summary>
/// Options for adding a linked worktree. Managed port of
/// <c>git_worktree_add_options</c> (include/git2/worktree.h:86-97).
/// </summary>
public sealed record WorktreeAddOptions
{
    /// <summary>If true, lock the newly created worktree.</summary>
    public bool Lock { get; init; }

    /// <summary>
    /// If true, allow checking out an existing branch matching
    /// <c>name</c> instead of creating a new branch.
    /// </summary>
    public bool CheckoutExisting { get; init; }

    /// <summary>
    /// The reference to use for the worktree HEAD. If null, a new branch
    /// named <c>name</c> is created from the repo HEAD.
    /// </summary>
    public GitReference? Ref { get; init; }

    /// <summary>Checkout options for populating the new worktree.</summary>
    public Checkout.GitCheckoutOptions? CheckoutOptions { get; init; }
}

/// <summary>
/// Options for pruning a worktree. Managed port of
/// <c>git_worktree_prune_options</c> (include/git2/worktree.h:210-215).
/// </summary>
public sealed record WorktreePruneOptions
{
    /// <summary>Combination of <see cref="WorktreePruneFlags"/>.</summary>
    public WorktreePruneFlags Flags { get; init; }
}
