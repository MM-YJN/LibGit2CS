// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Options for clone. Managed equivalent of <c>git_clone_options</c>
/// in <c>include/git2/clone.h</c>. Drops the C <c>version</c> field.
/// </summary>
public sealed record GitCloneOptions
{
    /// <summary>Create a bare repository (no working directory).</summary>
    public bool Bare { get; init; }

    /// <summary>Local clone behavior. Default is <see cref="GitCloneLocal.Auto"/>.</summary>
    public GitCloneLocal CloneLocal { get; init; } = GitCloneLocal.Auto;

    /// <summary>Fetch options for the initial fetch.</summary>
    public GitFetchOptions? FetchOptions { get; init; }

    /// <summary>Checkout options for the post-fetch checkout.</summary>
    public Checkout.GitCheckoutOptions? CheckoutOptions { get; init; }

    /// <summary>
    /// The remote name (default "origin"). Ignored if
    /// <see cref="RemoteCreate"/> is set.
    /// </summary>
    public string RemoteName { get; init; } = "origin";

    /// <summary>
    /// A specific branch to check out after clone. If null, the remote's
    /// default branch (HEAD) is used.
    /// </summary>
    public string? BranchName { get; init; }

    /// <summary>
    /// Custom repository-creation callback. If null, the default
    /// (<see cref="LibGit2CS.Repository.GitRepository.InitAsync"/>) is used.
    /// </summary>
    public GitRepositoryCreateCallback? RepositoryCreate { get; init; }

    /// <summary>
    /// Custom remote-creation callback. If null, the default
    /// (<see cref="LibGit2CS.Repository.GitRepository.RemoteCreateAsync"/>) is used.
    /// </summary>
    public GitRemoteCreateCallback? RemoteCreate { get; init; }
}
