// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Submodule;

/// <summary>
/// Submodule ignore strategy. Managed port of
/// <c>git_submodule_ignore_t</c> (include/git2/types.h:347-354).
/// </summary>
public enum SubmoduleIgnore
{
    /// <summary>Use the submodule's configured value.</summary>
    Unspecified = -1,

    /// <summary>Any change or untracked == dirty.</summary>
    None = 1,

    /// <summary>Dirty if tracked files change.</summary>
    Untracked = 2,

    /// <summary>Only dirty if HEAD moved.</summary>
    Dirty = 3,

    /// <summary>Never dirty.</summary>
    All = 4,
}
