// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Config file priority levels. Matches libgit2's <c>git_config_level_t</c>.
/// </summary>
public enum GitConfigLevel
{
    /// <summary>No level specified.</summary>
    None = 0,

    /// <summary>ProgramData (Windows only, lowest priority).</summary>
    ProgramData = 1,

    /// <summary>System-wide (<c>/etc/gitconfig</c>).</summary>
    System = 2,

    /// <summary>XDG (<c>$XDG_CONFIG_HOME/git/config</c>).</summary>
    Xdg = 3,

    /// <summary>Global / user (<c>~/.gitconfig</c>).</summary>
    Global = 4,

    /// <summary>Local / repository (<c>.git/config</c>).</summary>
    Local = 5,

    /// <summary>Worktree-specific (<c>.git/config.worktree</c>).</summary>
    Worktree = 6,

    /// <summary>App-level (highest priority).</summary>
    App = 7,

    /// <summary>Highest possible level (for snapshots).</summary>
    Highest = -1,
}
