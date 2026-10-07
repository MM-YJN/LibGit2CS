// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Repository;

namespace LibGit2CS.Remote;

/// <summary>
/// Callback for creating the remote during clone. Maps to
/// <c>git_remote_create_cb</c>.
/// </summary>
/// <param name="repo">The repository the remote will belong to.</param>
/// <param name="name">The remote name (typically "origin").</param>
/// <param name="url">The source URL.</param>
/// <param name="cancellationToken">The cancellation token supplied to clone.</param>
/// <returns>The configured remote.</returns>
public delegate Task<GitRemote> GitRemoteCreateCallback(GitRepository repo, string name, string url, CancellationToken cancellationToken);
