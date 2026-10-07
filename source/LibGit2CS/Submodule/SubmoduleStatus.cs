// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Submodule;

/// <summary>
/// Submodule status flags. Managed port of
/// <c>git_submodule_status_t</c> (include/git2/submodule.h:74-89).
/// </summary>
[Flags]
public enum SubmoduleStatus : uint
{
    /// <summary>Superproject HEAD contains the submodule.</summary>
    InHead = 1u << 0,

    /// <summary>Superproject index contains the submodule.</summary>
    InIndex = 1u << 1,

    /// <summary>The .gitmodules file has the submodule.</summary>
    InConfig = 1u << 2,

    /// <summary>Superproject workdir has the submodule.</summary>
    InWd = 1u << 3,

    /// <summary>In index, not in head.</summary>
    IndexAdded = 1u << 4,

    /// <summary>In head, not in index.</summary>
    IndexDeleted = 1u << 5,

    /// <summary>Index and head don't match.</summary>
    IndexModified = 1u << 6,

    /// <summary>Workdir contains empty directory.</summary>
    WdUninitialized = 1u << 7,

    /// <summary>In workdir, not in index.</summary>
    WdAdded = 1u << 8,

    /// <summary>In index, not in workdir.</summary>
    WdDeleted = 1u << 9,

    /// <summary>Index and workdir HEAD don't match.</summary>
    WdModified = 1u << 10,

    /// <summary>Submodule workdir index is dirty.</summary>
    WdIndexModified = 1u << 11,

    /// <summary>Submodule workdir has modified files.</summary>
    WdWdModified = 1u << 12,

    /// <summary>Workdir contains untracked files.</summary>
    WdUntracked = 1u << 13,
}
