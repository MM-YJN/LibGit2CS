// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Options for patch application. Managed equivalent of
/// <c>git_apply_options</c> (<c>include/git2/apply.h:95-109</c>).
/// </summary>
public sealed record GitApplyOptions
{
    /// <summary>Optional callback deciding whether each file delta is applied.</summary>
    public GitApplyDeltaCallback? DeltaCallback { get; init; }
    /// <summary>Optional callback deciding whether each hunk is applied.</summary>
    public GitApplyHunkCallback? HunkCallback { get; init; }
    /// <summary>Flags controlling patch application; defaults to no flags.</summary>
    public GitApplyFlags Flags { get; init; } = GitApplyFlags.None;
}
