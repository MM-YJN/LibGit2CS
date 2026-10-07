// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Status of a single ref update during push. Managed equivalent of
/// <c>push_status</c> in <c>src/libgit2/push.h</c>.
/// </summary>
public sealed record GitPushStatus
{
    /// <summary>
    /// <c>true</c> if the ref update succeeded; <c>false</c> if it failed (see <see cref="Message"/>).
    /// </summary>
    public bool Ok { get; init; }

    /// <summary>The remote ref name (e.g. <c>refs/heads/master</c>).</summary>
    public string Ref { get; init; } = string.Empty;

    /// <summary>Failure message (null on success).</summary>
    public string? Message { get; init; }
}
