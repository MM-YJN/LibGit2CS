// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.Remote;

/// <summary>
/// Callback for creating the target repository during clone. Maps to
/// <c>git_repository_create_cb</c>.
/// </summary>
/// <param name="path">The local path for the new repository.</param>
/// <param name="isBare">Whether the repository should be bare.</param>
/// <param name="context">The library context.</param>
/// <param name="cancellationToken">The cancellation token supplied to clone.</param>
/// <returns>The newly created and opened repository.</returns>
public delegate Task<GitRepository> GitRepositoryCreateCallback(string path, bool isBare, GitContext context, CancellationToken cancellationToken);
