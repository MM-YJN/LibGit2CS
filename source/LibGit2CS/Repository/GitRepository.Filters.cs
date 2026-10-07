// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.Repository;

/// <content>
/// Filter-list operations. The underlying factories on <see cref="GitFilterList"/>
/// are internal; these instance methods provide the public entry points.
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>
    /// Loads the filters that apply to a repository-relative path, in priority order.
    /// Matches <c>git_filter_list__load</c>.
    /// </summary>
    /// <param name="path">The byte-faithful relative path of the file being filtered.</param>
    /// <param name="blobId">The OID of the source blob, or null if unknown.</param>
    /// <param name="mode">The filter direction (smudge or clean).</param>
    /// <param name="flags">Filter flags.</param>
    /// <param name="attrCommitId">
    /// The commit whose attributes are used when <see cref="GitFilterListFlags.AttributesFromCommit"/>
    /// is set. This is distinct from the source blob OID; null or zero means unset.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The filter list, or <c>null</c> if no filters apply.</returns>
    public ValueTask<GitFilterList?> FilterListLoadAsync(
        GitPath path,
        GitOid? blobId,
        GitFilterMode mode,
        GitFilterListFlags flags,
        GitOid? attrCommitId = null,
        CancellationToken cancellationToken = default)
        => GitFilterList.LoadAsync(this, path, blobId, mode, flags, attrCommitId, cancellationToken);

    /// <summary>
    /// Loads the filters that apply to a repository-relative UTF-8 path, in priority order.
    /// </summary>
    /// <param name="path">The relative path, encoded as UTF-8 before attribute lookup.</param>
    /// <param name="blobId">The OID of the source blob, or null if unknown.</param>
    /// <param name="mode">The filter direction (smudge or clean).</param>
    /// <param name="flags">Filter flags.</param>
    /// <param name="attrCommitId">
    /// The commit whose attributes are used when <see cref="GitFilterListFlags.AttributesFromCommit"/>
    /// is set. This is distinct from the source blob OID; null or zero means unset.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The filter list, or <c>null</c> if no filters apply.</returns>
    public ValueTask<GitFilterList?> FilterListLoadAsync(
        string path,
        GitOid? blobId,
        GitFilterMode mode,
        GitFilterListFlags flags,
        GitOid? attrCommitId = null,
        CancellationToken cancellationToken = default)
        => GitFilterList.LoadAsync(this, path, blobId, mode, flags, attrCommitId, cancellationToken);
}
