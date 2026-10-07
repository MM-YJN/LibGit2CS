// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Checkout;

namespace LibGit2CS.Stash;

/// <summary>
/// Options for stash apply. Matches <c>git_stash_apply_options</c> in
/// <c>include/git2/stash.h</c>.
/// </summary>
public sealed record GitStashApplyOptions
{
    /// <summary>Flags controlling apply behavior.</summary>
    public GitStashApplyFlags Flags { get; init; }

    /// <summary>Checkout options for the apply operation.</summary>
    public GitCheckoutOptions CheckoutOptions { get; init; } = new();

    /// <summary>Optional progress callback.</summary>
    public IProgress<GitStashApplyProgress>? Progress { get; init; }
}
