// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Repository;

/// <content>
/// Worktree operations. Managed entry points over libgit2's
/// <c>git_worktree_*</c> factories. The underlying factories on
/// <see cref="Worktree"/> are internal; these instance methods are the public
/// surface.
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>Lists all worktrees. Matches <c>git_worktree_list</c>.
    /// Convenience wrapper for <see cref="Worktree.ListAsync"/>.</summary>
    public ValueTask<IReadOnlyList<Worktree>> WorktreeListAsync(CancellationToken cancellationToken = default)
        => Worktree.ListAsync(this, cancellationToken);

    /// <summary>Looks up a worktree by name. Matches <c>git_worktree_lookup</c>.
    /// Convenience wrapper for <see cref="Worktree.LookupAsync"/>.</summary>
    public Task<Worktree?> WorktreeLookupAsync(string name, CancellationToken cancellationToken = default)
        => Worktree.LookupAsync(this, name, cancellationToken);

    /// <summary>Gets the worktree that owns a repository. Matches
    /// <c>git_worktree_open_from_repository</c>. Convenience wrapper for
    /// <see cref="Worktree.FromRepositoryAsync"/>.</summary>
    public Task<Worktree?> WorktreeFromRepositoryAsync(CancellationToken cancellationToken = default)
        => Worktree.FromRepositoryAsync(this, cancellationToken);

    /// <summary>Adds a new linked worktree. Matches <c>git_worktree_add</c>.
    /// Convenience wrapper for <see cref="Worktree.AddAsync"/>.</summary>
    public Task<Worktree> WorktreeAddAsync(string name, string worktreePath, WorktreeAddOptions? options = null, CancellationToken cancellationToken = default)
        => Worktree.AddAsync(this, name, worktreePath, options, cancellationToken);
}
