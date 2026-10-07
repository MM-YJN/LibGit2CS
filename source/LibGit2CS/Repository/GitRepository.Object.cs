// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;

namespace LibGit2CS.Repository;

/// <content>
/// Object-model creation (blobs, trees, commits). Managed entry points over
/// libgit2's <c>git_blob_create_*</c>, <c>git_tree_create_updated</c>,
/// <c>git_treebuilder_new</c>, and <c>git_commit_create_*</c> factories. The
/// underlying factories on <see cref="GitBlob"/>/<see cref="GitTree"/>/
/// <see cref="GitTreeBuilder"/>/<see cref="Commit"/> are internal; these
/// instance methods are the public surface.
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    // ── Blob creation ───────────────────────────────────────────────────

    /// <summary>Creates a blob from a workdir-relative path. Matches <c>git_blob_create_fromworkdir</c>. Convenience wrapper for <see
    /// cref="LibGit2CS.Objects.GitBlob.CreateFromWorkdirAsync(LibGit2CS.Repository.GitRepository, LibGit2CS.IO.GitPath, System.Threading.CancellationToken)"/>. Byte-faithful: the <see cref="GitPath"/> overload is primary; the <c>string</c> overload delegates via <see
    /// cref="GitPath.FromUtf8String"/>. </summary>
    public Task<GitOid> BlobCreateFromWorkdirAsync(GitPath relativePath, CancellationToken cancellationToken = default)
        => GitBlob.CreateFromWorkdirAsync(this, relativePath, cancellationToken);

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. </summary>
    public Task<GitOid> BlobCreateFromWorkdirAsync(string relativePath, CancellationToken cancellationToken = default)
        => BlobCreateFromWorkdirAsync(GitPath.FromUtf8String(relativePath), cancellationToken);

    /// <summary>Creates a blob from a file on disk. Matches
    /// <c>git_blob_create_fromdisk</c>. Convenience wrapper for
    /// <see cref="GitBlob.CreateFromDiskAsync"/>.</summary>
    public Task<GitOid> BlobCreateFromDiskAsync(string absolutePath, CancellationToken cancellationToken = default)
        => GitBlob.CreateFromDiskAsync(this, absolutePath, cancellationToken);

    /// <summary>Opens a streaming blob writer. Matches
    /// <c>git_blob_create_from_stream</c>. Convenience wrapper for
    /// <see cref="GitBlob.CreateWriteStream"/>.</summary>
    public GitBlobWriteStream BlobCreateWriteStream(string? hintPath = null)
        => GitBlob.CreateWriteStream(this, hintPath);

    /// <summary> Byte-faithful overload of <see cref="BlobCreateWriteStream(string?)"/>. Note: no default parameter to avoid ambiguity with
    /// the <c>string?</c> overload; pass <c>default(GitPath?)</c> for no hint. </summary>
    public GitBlobWriteStream BlobCreateWriteStream(GitPath? hintPath)
        => GitBlob.CreateWriteStream(this, hintPath?.ToUtf8String());

    // ── Tree creation ───────────────────────────────────────────────────

    /// <summary>Creates a tree by applying updates to a baseline. Matches
    /// <c>git_tree_createupdated</c>. Convenience wrapper for
    /// <see cref="GitTree.CreateUpdatedAsync"/>.</summary>
    public Task<GitOid> TreeCreateUpdatedAsync(GitTree? baseline, IReadOnlyList<GitTreeUpdate> updates, CancellationToken cancellationToken = default)
        => GitTree.CreateUpdatedAsync(this, baseline, updates, cancellationToken);

    /// <summary>Creates a <see cref="GitTreeBuilder"/>. Matches
    /// <c>git_treebuilder_new</c>. Convenience wrapper for the
    /// <see cref="GitTreeBuilder"/> constructor.</summary>
    public GitTreeBuilder NewTreeBuilder(GitTree? source = null)
        => new(this, source);

    // ── Commit creation ─────────────────────────────────────────────────

    /// <summary>Creates a commit. Matches <c>git_commit_create</c>.
    /// Convenience wrapper for <see cref="LibGit2CS.Objects.Commit.CreateAsync(LibGit2CS.Repository.GitRepository, LibGit2CS.Objects.CommitCreateOptions, System.Threading.CancellationToken)"/>.</summary>
    public Task<GitOid> CommitCreateAsync(CommitCreateOptions options, CancellationToken cancellationToken = default)
        => Commit.CreateAsync(this, options, cancellationToken);

    /// <summary>Creates a commit from the staging area. Matches
    /// <c>git_commit_create_from_stage</c>. Convenience wrapper for
    /// <see cref="Commit.CreateFromStageAsync"/>.</summary>
    public Task<GitOid> CommitCreateFromStageAsync(CommitCreateOptions options, CancellationToken cancellationToken = default)
        => Commit.CreateFromStageAsync(this, options, cancellationToken);

    /// <summary>Creates a commit with a detached signature. Matches
    /// <c>git_commit_create_with_signature</c>. Convenience wrapper for
    /// <see cref="LibGit2CS.Objects.Commit.CreateWithSignatureAsync(LibGit2CS.Repository.GitRepository, string, string?, string?, System.Threading.CancellationToken)"/>.</summary>
    public Task<GitOid> CommitCreateWithSignatureAsync(string commitContent, string? signature, string? signatureField, CancellationToken cancellationToken = default)
        => Commit.CreateWithSignatureAsync(this, commitContent, signature, signatureField, cancellationToken);

    /// <summary>Looks up an object by OID, or returns null if it is not found.</summary>
    public ValueTask<GitObject?> ObjectLookupAsync(GitOid id, CancellationToken cancellationToken = default)
        => Objects.LookupAsync(id, cancellationToken);

    /// <summary>Looks up an object of the requested type, or returns null if it is not found. Throws if its type does not match.</summary>
    public ValueTask<T?> ObjectLookupAsync<T>(GitOid id, CancellationToken cancellationToken = default) where T : GitObject
        => Objects.LookupAsync<T>(id, cancellationToken);

    /// <summary>Looks up an object by abbreviated OID, or returns null if it is not found. Throws if the prefix is ambiguous.</summary>
    public Task<GitObject?> ObjectLookupPrefixAsync(GitOid prefix, CancellationToken cancellationToken = default)
        => Objects.LookupPrefixAsync(prefix, cancellationToken);

    /// <summary>Looks up an object of the requested type by abbreviated OID, or returns null if it is not found. Throws if the prefix is ambiguous or the type does not match.</summary>
    public Task<T?> ObjectLookupPrefixAsync<T>(GitOid prefix, CancellationToken cancellationToken = default) where T : GitObject
        => Objects.LookupPrefixAsync<T>(prefix, cancellationToken);

    /// <summary>Writes a raw object to this repository's object database and returns its OID.</summary>
    public Task<GitOid> ObjectWriteAsync(GitObjectType type, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
        => Objects.WriteAsync(type, body, cancellationToken);
}
