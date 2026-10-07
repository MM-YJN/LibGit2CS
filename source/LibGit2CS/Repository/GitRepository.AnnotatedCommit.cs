// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;

namespace LibGit2CS.Repository;

/// <content>
/// Annotated-commit construction. Managed entry points over libgit2's
/// <c>git_annotated_commit_*</c> factories. The underlying factories on
/// <see cref="GitAnnotatedCommit"/> are internal; these instance methods are
/// the public surface. (<see cref="GitAnnotatedCommit.FromCommit(Commit)"/>
/// takes no repository and remains a public static on the type.)
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>
    /// Loads a commit by OID and wraps it as an annotated commit. Matches
    /// <c>git_annotated_commit_lookup</c>. Convenience wrapper for
    /// <see cref="GitAnnotatedCommit.LookupAsync"/>.
    /// </summary>
    public Task<GitAnnotatedCommit> AnnotatedCommitLookupAsync(GitOid id, CancellationToken cancellationToken = default)
        => GitAnnotatedCommit.LookupAsync(this, id, cancellationToken);

    /// <summary>
    /// Constructs an annotated commit from a fetch-head entry. Matches
    /// <c>git_annotated_commit_from_fetchhead</c>. Convenience wrapper for
    /// <see cref="GitAnnotatedCommit.FromFetchHeadAsync"/>.
    /// </summary>
    public Task<GitAnnotatedCommit> AnnotatedCommitFromFetchHeadAsync(GitOid id, string branchName, string remoteUrl, CancellationToken cancellationToken = default)
        => GitAnnotatedCommit.FromFetchHeadAsync(this, id, branchName, remoteUrl, cancellationToken);

    /// <summary>
    /// Constructs an annotated commit by peeling a reference. Matches
    /// <c>git_annotated_commit_from_ref</c>. Convenience wrapper for
    /// <see cref="GitAnnotatedCommit.FromRefAsync"/>.
    /// </summary>
    public Task<GitAnnotatedCommit> AnnotatedCommitFromRefAsync(GitReference @ref, CancellationToken cancellationToken = default)
        => GitAnnotatedCommit.FromRefAsync(this, @ref, cancellationToken);

    /// <summary>
    /// Constructs an annotated commit by resolving HEAD. Matches
    /// <c>git_annotated_commit_from_head</c>. Convenience wrapper for
    /// <see cref="GitAnnotatedCommit.FromHeadAsync"/>.
    /// </summary>
    public Task<GitAnnotatedCommit> AnnotatedCommitFromHeadAsync(CancellationToken cancellationToken = default)
        => GitAnnotatedCommit.FromHeadAsync(this, cancellationToken);

    /// <summary>
    /// Constructs an annotated commit by parsing a revision specification.
    /// Matches <c>git_annotated_commit_from_revspec</c>. Convenience wrapper
    /// for <see cref="GitAnnotatedCommit.FromRevspecAsync"/>.
    /// </summary>
    public Task<GitAnnotatedCommit> AnnotatedCommitFromRevspecAsync(string revspec, CancellationToken cancellationToken = default)
        => GitAnnotatedCommit.FromRevspecAsync(this, revspec, cancellationToken);
}
