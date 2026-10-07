// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibGit2CS.Blame;
using LibGit2CS.IO;

namespace LibGit2CS.Repository;

/// <content>
/// Blame operations. Managed entry point over libgit2's
/// <c>git_blame_file</c>. The underlying factory on <see cref="GitBlame"/> is
/// internal; this instance method is the public surface.
/// (<c>GitBlame.BufferAsync</c> takes no repository and stays a public static
/// on the type.)
/// </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>Computes blame for a file. Matches <c>git_blame_file</c>.
    /// Convenience wrapper for <see cref="GitBlame.FileAsync"/>.</summary>
    /// <param name="path">Byte-faithful <see cref="GitPath"/> (mirrors libgit2's
    /// raw <c>const char *path</c>). Use <see cref="GitPath.ToUtf8String"/> for
    /// display.</param>
    /// <param name="options">Options controlling this operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<GitBlame> BlameFileAsync(GitPath path, GitBlameOptions? options = null, CancellationToken cancellationToken = default)
        => GitBlame.FileAsync(this, path, options, cancellationToken);

    /// <summary>Computes blame for a file. Matches <c>git_blame_file</c>.
    /// Convenience wrapper for <see cref="GitBlame.FileAsync"/>; encodes the
    /// string as UTF-8 (lossy for non-UTF-8 paths — use the
    /// <see cref="BlameFileAsync(GitPath, GitBlameOptions?, CancellationToken)"/>
    /// overload for byte-faithful paths).</summary>
    public Task<GitBlame> BlameFileAsync(string path, GitBlameOptions? options = null, CancellationToken cancellationToken = default)
        => GitBlame.FileAsync(this, GitPath.FromUtf8String(path), options, cancellationToken);
}
