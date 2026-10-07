// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Remote;

namespace LibGit2CS.Submodule;

/// <summary>
/// Options for submodule update. Managed port of
/// <c>git_submodule_update_options</c> (include/git2/submodule.h:135-158).
/// </summary>
/// <remarks>
/// Mirrors libgit2's <c>checkout_opts</c> + <c>fetch_opts</c> + <c>allow_fetch</c>.
/// The <see cref="FetchOptions"/> carry the credential/progress callbacks
/// required for a network (<c>ssh://</c>/<c>https://</c>) submodule clone —
/// libgit2 copies them into the <c>git_clone_options.fetch_opts</c> it builds
/// inside <c>git_submodule_clone</c>/<c>git_submodule_update</c>.
/// </remarks>
public sealed record SubmoduleUpdateOptions
{
    /// <summary>
    /// Checkout options for the submodule checkout step. If the checkout
    /// strategy is <see cref="LibGit2CS.Checkout.GitCheckoutStrategy.None"/> or
    /// <see cref="LibGit2CS.Checkout.GitCheckoutStrategy.DryRun"/>, checkout is skipped.
    /// </summary>
    public Checkout.GitCheckoutOptions? CheckoutOptions { get; init; }

    /// <summary>
    /// Fetch options (callbacks, depth, tags, …) used when the submodule
    /// must be cloned from a network URL. Matches libgit2's
    /// <c>git_submodule_update_options.fetch_opts</c>. The
    /// <see cref="GitFetchOptions.RemoteCallbacks"/> supply credentials for
    /// SSH/HTTPS submodule clones. Forwarded into the
    /// <see cref="GitCloneOptions.FetchOptions"/> built by
    /// <see cref="GitSubmodule.CloneAsync"/>/
    /// <see cref="GitSubmodule.UpdateAsync"/>.
    /// </summary>
    public GitFetchOptions? FetchOptions { get; init; }

    /// <summary>
    /// If true (default), allow fetching when the target commit is not
    /// found locally. Matches libgit2's <c>allow_fetch</c>.
    /// </summary>
    public bool AllowFetch { get; init; } = true;
}
