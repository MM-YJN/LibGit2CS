// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Objects;
using LibGit2CS.Rebase;

namespace LibGit2CS.Repository;

/// <content>
/// Rebase operations. Managed entry points over libgit2's
/// <c>git_rebase_init</c>/<c>git_rebase_open</c>. The underlying factories on
/// <see cref="GitRebase"/> are internal; these instance methods are the public
/// surface.
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>Initializes a rebase. Matches <c>git_rebase_init</c>.
    /// Convenience wrapper for <see cref="GitRebase.InitAsync"/>.</summary>
    public Task<GitRebase> RebaseInitAsync(GitAnnotatedCommit? branch, GitAnnotatedCommit? upstream, GitAnnotatedCommit? onto, GitRebaseOptions? options = null, CancellationToken cancellationToken = default)
        => GitRebase.InitAsync(this, branch, upstream, onto, options, cancellationToken);

    /// <summary>Opens an in-progress rebase. Matches <c>git_rebase_open</c>.
    /// Convenience wrapper for <see cref="GitRebase.OpenAsync"/>.</summary>
    public Task<GitRebase> RebaseOpenAsync(GitRebaseOptions? options = null, CancellationToken cancellationToken = default)
        => GitRebase.OpenAsync(this, options, cancellationToken);
}
