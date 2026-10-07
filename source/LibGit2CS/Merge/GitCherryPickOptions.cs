// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Checkout;

namespace LibGit2CS.Merge;

/// <summary>
/// Options for cherry-pick. Matches <c>git_cherrypick_options</c> in
/// <c>include/git2/cherrypick.h</c>.
/// </summary>
/// <remarks>
/// C's <c>git_cherrypick_options</c> carries <c>version</c>,
/// <c>mainline</c>, <c>merge_opts</c>, and <c>checkout_opts</c>. The managed
/// port drops <c>version</c> (forward compatibility is free in managed code)
/// and exposes the remaining fields as record properties.
/// </remarks>
public sealed record GitCherryPickOptions
{
    /// <summary>
    /// The parent number (1-indexed) to diff against when cherry-picking a
    /// merge commit. Must be 0 for non-merge commits. Matches
    /// <c>git_cherrypick_options.mainline</c> (<c>cherrypick.h:33</c>).
    /// </summary>
    public uint Mainline { get; init; }

    /// <summary>
    /// Merge options. Null = use defaults. Matches
    /// <c>git_cherrypick_options.merge_opts</c>.
    /// </summary>
    public GitMergeOptions? MergeOptions { get; init; }

    /// <summary>
    /// Checkout options. Null = use defaults (strategy defaults to
    /// <see cref="GitCheckoutStrategy.AllowConflicts"/>). Matches
    /// <c>git_cherrypick_options.checkout_opts</c>.
    /// </summary>
    public GitCheckoutOptions? CheckoutOptions { get; init; }

    /// <summary>
    /// Default options — matches <c>GIT_CHERRYPICK_OPTIONS_INIT</c>:
    /// mainline 0, default merge + checkout options.
    /// </summary>
    public static GitCherryPickOptions Default { get; } = new();
}
