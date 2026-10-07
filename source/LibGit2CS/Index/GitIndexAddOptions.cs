// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Index;

/// <summary>
/// Flags controlling <see cref="GitIndex.AddAllAsync"/> behavior. Maps to
/// <c>git_index_add_option_t</c> in <c>include/git2/index.h</c>.
/// </summary>
[Flags]
public enum GitIndexAddOptions
{
    /// <summary>Default: obey ignores, glob-match pathspec.</summary>
    Default = 0,

    /// <summary>Force-add ignored files. (<c>GIT_INDEX_ADD_FORCE</c>)</summary>
    Force = 1 << 0,

    /// <summary>Treat pathspec entries as literal strings (no globbing). (<c>GIT_INDEX_ADD_DISABLE_PATHSPEC_MATCH</c>)</summary>
    DisablePathSpecMatch = 1 << 1,

    /// <summary>Error if pathspec matches an ignored file. (<c>GIT_INDEX_ADD_CHECK_PATHSPEC</c>)</summary>
    CheckPathSpec = 1 << 2,
}
