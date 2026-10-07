// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Remote;
using LibGit2CS.Transports;

namespace LibGit2CS.Repository;

/// <content>
/// Remote-management operations. Managed entry points over libgit2's
/// <c>git_remote_*</c> factories. The underlying factories on
/// <see cref="GitRemote"/> are internal; these instance methods are the public
/// surface. (Per-instance remote operations — fetch/push/connect — remain on
/// the <see cref="GitRemote"/> type.)
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>Looks up a remote by name. Matches <c>git_remote_lookup</c>.
    /// Convenience wrapper for <see cref="GitRemote.LookupAsync"/>.</summary>
    public Task<GitRemote> RemoteLookupAsync(string name, CancellationToken cancellationToken = default)
        => GitRemote.LookupAsync(this, name, cancellationToken);

    /// <summary>Creates a remote. Matches <c>git_remote_create</c>. Convenience
    /// wrapper for <see cref="GitRemote.CreateAsync"/>.</summary>
    public Task<GitRemote> RemoteCreateAsync(string name, string url, CancellationToken cancellationToken = default)
        => GitRemote.CreateAsync(this, name, url, cancellationToken);

    /// <summary>Creates a remote with options. Matches <c>git_remote_create_with_opts</c>. Convenience wrapper for <see
    /// cref="GitRemote.CreateWithOptsAsync"/>.</summary>
    public Task<GitRemote> RemoteCreateWithOptionsAsync(string url, GitRemoteCreateOptions options, CancellationToken cancellationToken = default)
        => GitRemote.CreateWithOptsAsync(this, url, options, cancellationToken);

    /// <summary>Creates an anonymous (unnamed) remote. Matches <c>git_remote_create_anonymous</c>. Convenience wrapper for <see
    /// cref="GitRemote.CreateAnonymousAsync"/>.</summary>
    public Task<GitRemote> RemoteCreateAnonymousAsync(string url, CancellationToken cancellationToken = default)
        => GitRemote.CreateAnonymousAsync(this, url, cancellationToken);

    /// <summary>Lists remote names. Matches <c>git_remote_list</c>.
    /// Convenience wrapper for <see cref="GitRemote.ListAsync"/>.</summary>
    public Task<IReadOnlyList<string>> RemoteListAsync(CancellationToken cancellationToken = default)
        => GitRemote.ListAsync(this, cancellationToken);

    /// <summary>Deletes a remote. Matches <c>git_remote_delete</c>.
    /// Convenience wrapper for <see cref="GitRemote.DeleteAsync"/>.</summary>
    public Task RemoteDeleteAsync(string name, CancellationToken cancellationToken = default)
        => GitRemote.DeleteAsync(this, name, cancellationToken);

    /// <summary>Renames a remote. Matches <c>git_remote_rename</c>.
    /// Convenience wrapper for <see cref="GitRemote.RenameAsync"/>.</summary>
    public Task<IReadOnlyList<string>> RemoteRenameAsync(string oldName, string newName, CancellationToken cancellationToken = default)
        => GitRemote.RenameAsync(this, oldName, newName, cancellationToken);

    /// <summary>Applies <c>url.&lt;base&gt;.insteadOf</c> rewrites to a URL.
    /// Matches <c>git_remote_apply_insteadof</c>. Convenience wrapper for
    /// <see cref="GitRemote.ApplyInsteadOfAsync"/>.</summary>
    public Task<string?> RemoteApplyInsteadOfAsync(string url, GitDirection direction, bool useDefaultIfEmpty = true, CancellationToken cancellationToken = default)
        => GitRemote.ApplyInsteadOfAsync(this, url, direction, useDefaultIfEmpty, cancellationToken);
}
