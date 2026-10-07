// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Revwalk;

namespace LibGit2CS.Repository;

/// <content>
/// Revision parsing and walker construction. Managed entry points over
/// libgit2's <c>git_revparse_*</c> and <c>git_revwalk_new</c>. The
/// underlying factories (<see cref="GitRevParser"/>, <see cref="GitRevWalker"/>)
/// are internal; these instance methods are the public surface.
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    // ── Revparse ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a single revision specifier to one object. Matches
    /// <c>git_revparse_single</c>. Convenience wrapper for
    /// <see cref="GitRevParser.ParseSingleAsync"/>.
    /// </summary>
    public Task<GitObject?> RevparseSingleAsync(string revspec, CancellationToken cancellationToken = default)
        => GitRevParser.ParseSingleAsync(this, revspec, cancellationToken);

    /// <summary>Resolves a single revision specifier, returning the object and
    /// the intermediate reference. Matches <c>git_revparse_ext</c>
    /// (revparse.c:863-887). Specs routed through a ref
    /// (<c>@&#123;u&#125;</c>, <c>@&#123;N&#125;</c>, <c>@&#123;-N&#125;</c>,
    /// a plain refname) yield the reference; OID/abbreviation/operator-only
    /// specs yield <c>null</c> for <c>Reference</c>.</summary>
    public Task<(GitObject? Object, GitReference? Reference)> RevparseExtAsync(
        string revspec, CancellationToken cancellationToken = default)
        => GitRevParser.ParseExtAsync(this, revspec, cancellationToken);

    /// <summary>
    /// Parses a range expression <c>A..B</c> or <c>A...B</c> (or a single
    /// spec). Matches <c>git_revparse</c>. Convenience wrapper for
    /// <see cref="GitRevParser.ParseRangeAsync"/>.
    /// </summary>
    public Task<(GitObject? From, GitObject? To, GitRevSpecFlags Flags)> RevparseRangeAsync(string spec, CancellationToken cancellationToken = default)
        => GitRevParser.ParseRangeAsync(this, spec, cancellationToken);

    // ── Revwalk ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a commit walker over this repository. Matches
    /// <c>git_revwalk_new</c>. Convenience wrapper for the
    /// <see cref="GitRevWalker"/> constructor.
    /// </summary>
    public GitRevWalker NewRevWalker()
        => new(this);
}
