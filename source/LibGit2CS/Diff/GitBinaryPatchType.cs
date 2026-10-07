// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Binary patch data type. Maps 1:1 to <c>git_diff_binary_t</c> in
/// <c>include/git2/diff.h:518-527</c>.
/// </summary>
public enum GitBinaryPatchType
{
    /// <summary>No binary delta. (<c>GIT_DIFF_BINARY_NONE</c>)</summary>
    None = 0,

    /// <summary>Literal file contents (deflated). (<c>GIT_DIFF_BINARY_LITERAL</c>)</summary>
    Literal = 1,

    /// <summary>Delta from one side to the other (deflated). (<c>GIT_DIFF_BINARY_DELTA</c>)</summary>
    Delta = 2,
}
