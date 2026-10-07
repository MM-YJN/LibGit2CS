// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Remote;

/// <summary>
/// Result of a push operation. Contains the unpack status and per-ref
/// update statuses from the remote's report-status response.
/// </summary>
public sealed record GitPushResult
{
    /// <summary>
    /// <c>true</c> if the remote successfully unpacked the packfile.
    /// Always <c>true</c> when no pack was sent (e.g. delete-only push).
    /// </summary>
    public bool UnpackOk { get; init; }

    /// <summary>Per-ref update statuses from the remote.</summary>
    public IReadOnlyList<GitPushStatus> Status { get; init; } = [];
}
