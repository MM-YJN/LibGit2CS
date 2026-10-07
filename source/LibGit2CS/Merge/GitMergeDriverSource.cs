// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Repository;

using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.Merge;

/// <summary>
/// Data about the file to be merged, passed to a merge driver. Matches
/// <c>git_merge_driver_source</c> (<c>src/libgit2/merge_driver.h:16-24</c>).
/// </summary>
/// <remarks>
/// In C, the fields are accessed via accessor functions
/// (<c>git_merge_driver_source_repo</c>, etc.); here they are record
/// properties.
/// </remarks>
public sealed record GitMergeDriverSource(
    GitRepository Repo,
    string? DefaultDriver,
    GitMergeFileOptions? FileOptions,
    GitIndexEntry? Ancestor,
    GitIndexEntry? Ours,
    GitIndexEntry? Theirs);
