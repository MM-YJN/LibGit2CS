// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Core;

namespace LibGit2CS.Repository;

/// <content>
/// Mailmap operations. Managed entry point over libgit2's
/// <c>git_mailmap_from_repository</c>. The underlying factory on
/// <see cref="GitMailmap"/> is internal; this instance method is the public
/// surface.
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>Loads the aggregate mailmap for a repository. Matches
    /// <c>git_mailmap_from_repository</c>. Convenience wrapper for
    /// <see cref="GitMailmap.FromRepositoryAsync"/>.</summary>
    public Task<GitMailmap> MailmapFromRepositoryAsync(CancellationToken cancellationToken = default)
        => GitMailmap.FromRepositoryAsync(this, cancellationToken);
}
