// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Core;
using LibGit2CS.Objects;

namespace LibGit2CS.Repository;

/// <content>
/// Tag operations. Managed entry points over libgit2's <c>git_tag_*</c>
/// factories. The underlying factories on <see cref="GitTag"/> are internal;
/// these instance methods are the public surface.
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>Creates a tag (annotated or lightweight). Matches
    /// <c>git_tag_create</c>. Convenience wrapper for
    /// <see cref="GitTag.CreateAsync"/>.</summary>
    public Task<GitOid> TagCreateAsync(string tagName, GitObject target, GitSignature? tagger, string? message, bool allowOverwrite = false, CancellationToken cancellationToken = default)
        => GitTag.CreateAsync(this, tagName, target, tagger, message, allowOverwrite, cancellationToken);

    /// <summary>Creates an annotated tag object (no ref). Matches
    /// <c>git_tag_annotation_create</c>. Convenience wrapper for
    /// <see cref="LibGit2CS.Objects.GitTag.CreateAnnotationAsync(LibGit2CS.Repository.GitRepository, string, LibGit2CS.Objects.GitObject, LibGit2CS.Core.GitSignature, string, System.Threading.CancellationToken)"/>.</summary>
    public Task<GitOid> TagCreateAnnotationAsync(string tagName, GitObject target, GitSignature tagger, string message, CancellationToken cancellationToken = default)
        => GitTag.CreateAnnotationAsync(this, tagName, target, tagger, message, cancellationToken);

    /// <summary>Creates a tag object from a raw buffer. Matches
    /// <c>git_tag_create_from_buffer</c>. Convenience wrapper for
    /// <see cref="LibGit2CS.Objects.GitTag.CreateFromBufferAsync(LibGit2CS.Repository.GitRepository, string, bool, System.Threading.CancellationToken)"/>.</summary>
    public Task<GitOid> TagCreateFromBufferAsync(string buffer, bool allowOverwrite = false, string? tagName = null, CancellationToken cancellationToken = default)
        => GitTag.CreateFromBufferAsync(this, buffer, allowOverwrite, cancellationToken);

    /// <summary>Deletes a tag. Matches <c>git_tag_delete</c>. Convenience
    /// wrapper for <see cref="GitTag.DeleteAsync"/>.</summary>
    public Task TagDeleteAsync(string tagName, CancellationToken cancellationToken = default)
        => GitTag.DeleteAsync(this, tagName, cancellationToken);

    /// <summary>Lists tag names, optionally filtered by glob. Matches
    /// <c>git_tag_list</c>. Convenience wrapper for
    /// <see cref="GitTag.ListAsync"/>.</summary>
    public Task<IReadOnlyList<string>> TagListAsync(string? pattern = null, CancellationToken cancellationToken = default)
        => GitTag.ListAsync(this, pattern, cancellationToken);

    /// <summary>Enumerates tags as <c>(Name, Oid)</c> pairs. Convenience
    /// wrapper for <see cref="GitTag.EnumerateAsync"/>.</summary>
    public IAsyncEnumerable<(string Name, GitOid Oid)> TagEnumerateAsync(CancellationToken cancellationToken = default)
        => GitTag.EnumerateAsync(this, cancellationToken);
}
