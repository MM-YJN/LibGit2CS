// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Stash;

/// <summary>
/// Options for stash save. Matches <c>git_stash_save_options</c> in
/// <c>include/git2/stash.h</c>.
/// </summary>
public sealed record GitStashSaveOptions
{
    /// <summary>Flags controlling stashing behavior.</summary>
    public GitStashFlags Flags { get; init; }

    /// <summary>The identity of the person performing the stashing.</summary>
    public required GitSignature Stasher { get; init; }

    /// <summary>Optional description for the stashed state.</summary>
    public string? Message { get; init; }

    /// <summary>Optional paths that control which files are stashed.</summary>
    public string[]? Paths { get; init; }
}
