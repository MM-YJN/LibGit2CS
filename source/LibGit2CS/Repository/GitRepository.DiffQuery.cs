// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Diff;
using LibGit2CS.Objects;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Repository;

/// <content>
/// Diff and patch creation. Managed entry points over libgit2's
/// <c>git_diff_*</c> and <c>git_patch_from_*</c> factories. The underlying
/// factories on <see cref="GitDiff"/>/<see cref="GitPatch"/> are internal;
/// these instance methods are the public surface. (<c>GitDiff.Buffers</c> and
/// <c>GitDiff.FromBuffer</c> take no repository and remain public statics on
/// the type; patch application lives in <c>GitRepository.Diff.cs</c>.)
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    // ── Diff creation ───────────────────────────────────────────────────

    /// <summary>Tree-to-tree diff. Matches <c>git_diff_tree_to_tree</c>.
    /// Convenience wrapper for <see cref="GitDiff.TreeToTreeAsync"/>.</summary>
    public Task<GitDiff> DiffTreeToTreeAsync(GitTree? oldTree, GitTree? newTree, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
        => GitDiff.TreeToTreeAsync(this, oldTree, newTree, options, cancellationToken);

    /// <summary>Tree-to-index diff. Matches <c>git_diff_tree_to_index</c>.
    /// Convenience wrapper for <see cref="GitDiff.TreeToIndexAsync"/>.</summary>
    public Task<GitDiff> DiffTreeToIndexAsync(GitTree? oldTree, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
        => GitDiff.TreeToIndexAsync(this, oldTree, options, cancellationToken);

    /// <summary>Index-to-workdir diff. Matches <c>git_diff_index_to_workdir</c>.
    /// Convenience wrapper for <see cref="GitDiff.IndexToWorkdirAsync"/>.</summary>
    public Task<GitDiff> DiffIndexToWorkdirAsync(GitDiffOptions? options = null, CancellationToken cancellationToken = default)
        => GitDiff.IndexToWorkdirAsync(this, options, cancellationToken);

    /// <summary>Tree-to-workdir diff. Matches <c>git_diff_tree_to_workdir</c>.
    /// Convenience wrapper for <see cref="GitDiff.TreeToWorkdirAsync"/>.</summary>
    public Task<GitDiff> DiffTreeToWorkdirAsync(GitTree? oldTree, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
        => GitDiff.TreeToWorkdirAsync(this, oldTree, options, cancellationToken);

    /// <summary>Tree-to-workdir-with-index diff. Matches
    /// <c>git_diff_tree_to_workdir_with_index</c>. Convenience wrapper for
    /// <see cref="GitDiff.TreeToWorkdirWithIndexAsync"/>.</summary>
    public Task<GitDiff> DiffTreeToWorkdirWithIndexAsync(GitTree? oldTree, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
        => GitDiff.TreeToWorkdirWithIndexAsync(this, oldTree, options, cancellationToken);

    /// <summary>Index-to-index diff. Matches <c>git_diff_index_to_index</c>.
    /// Convenience wrapper for <see cref="GitDiff.IndexToIndexAsync"/>.</summary>
    public Task<GitDiff> DiffIndexToIndexAsync(GitIndex oldIndex, GitIndex newIndex, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
        => GitDiff.IndexToIndexAsync(this, oldIndex, newIndex, options, cancellationToken);

    /// <summary>Commit diff (vs first parent). Matches <c>git_diff__commit</c>.
    /// Convenience wrapper for <see cref="GitDiff.CommitAsync"/>.</summary>
    public Task<GitDiff> DiffCommitAsync(Commit commit, GitDiffOptions? options = null, CancellationToken cancellationToken = default)
        => GitDiff.CommitAsync(this, commit, options, cancellationToken);

    /// <summary>Blob-to-buffer diff. Matches <c>git_diff_blob_to_buffer</c>.
    /// Convenience wrapper for <see cref="GitDiff.BlobToBuffer"/>.</summary>
    public GitDiff DiffBlobToBuffer(GitBlob? oldBlob, string? newContent, GitDiffOptions? options = null)
        => GitDiff.BlobToBuffer(this, oldBlob, newContent, options);

    /// <summary>Blob-to-blob diff. Matches <c>git_diff_blobs</c>. Convenience
    /// wrapper for <see cref="GitDiff.Blobs"/>.</summary>
    public GitDiff DiffBlobs(GitBlob oldBlob, GitBlob newBlob, GitDiffOptions? options = null)
        => GitDiff.Blobs(this, oldBlob, newBlob, options);

    // ── Patch creation ──────────────────────────────────────────────────

    /// <summary>Creates a patch for a delta in a diff. Matches
    /// <c>git_patch_from_diff</c>. Convenience wrapper for
    /// <see cref="GitPatch.FromDiffAsync"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Facade entry point — uniform with the other repo diff/patch wrappers; the underlying diff already carries the repo context.")]
    public ValueTask<GitPatch> PatchFromDiffAsync(GitDiff diff, int index, CancellationToken cancellationToken = default)
        => GitPatch.FromDiffAsync(diff, index, cancellationToken);

    /// <summary>Creates a patch comparing two blobs. Matches
    /// <c>git_patch_from_blobs</c>. Convenience wrapper for
    /// <see cref="GitPatch.FromBlobs"/>.</summary>
    public GitPatch PatchFromBlobs(GitBlob? oldBlob, GitBlob? newBlob, GitDiffOptions? options = null)
        => GitPatch.FromBlobs(this, oldBlob, newBlob, oldPath: null, newPath: null, options);

    /// <summary>Creates a patch comparing a blob to an in-memory buffer.
    /// Matches <c>git_patch_from_blob_and_buffer</c>. Convenience wrapper for
    /// <see cref="GitPatch.FromBlobAndBuffer"/>.</summary>
    public GitPatch PatchFromBlobAndBuffer(GitBlob? oldBlob, ReadOnlyMemory<byte> newBuffer, GitDiffOptions? options = null)
        => GitPatch.FromBlobAndBuffer(this, oldBlob, newBuffer, options);

    /// <summary>Creates a patch comparing two in-memory buffers. Matches
    /// <c>git_patch_from_buffers</c>. Convenience wrapper for
    /// <see cref="GitPatch.FromBuffers"/>.</summary>
    public GitPatch PatchFromBuffers(ReadOnlyMemory<byte> oldBuffer, ReadOnlyMemory<byte> newBuffer, GitDiffOptions? options = null)
        => GitPatch.FromBuffers(this, oldBuffer, newBuffer, options);
}
