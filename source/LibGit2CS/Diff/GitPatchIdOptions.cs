// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Options for patch-id computation. Managed equivalent of
/// <c>git_diff_patchid_options</c> (<c>include/git2/diff.h:1459-1467</c>).
/// </summary>
/// <remarks>
/// The C struct contains only the <c>version</c> field (ABI version), which
/// has no semantic meaning in the C# port (no ABI to version). The record is
/// here for API parity; it's a placeholder for future options.
/// </remarks>
public sealed record GitPatchIdOptions
{
    // Empty in 1.9.4 (only the `version` ABI field, which has no C# meaning).
}
