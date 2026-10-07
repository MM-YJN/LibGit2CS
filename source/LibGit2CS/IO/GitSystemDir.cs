// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary>
/// System directory selector. Matches libgit2's <c>git_sysdir_t</c>.
/// </summary>
public enum GitSystemDir
{
    /// <summary>System-wide config dir (e.g. <c>/etc</c>).</summary>
    System = 0,

    /// <summary>Global (user) config dir (e.g. <c>$HOME</c>).</summary>
    Global = 1,

    /// <summary>XDG config dir (e.g. <c>$XDG_CONFIG_HOME/git</c>).</summary>
    Xdg = 2,

    /// <summary>Windows ProgramData dir. Empty on POSIX.</summary>
    ProgramData = 3,

    /// <summary>Template dir (e.g. <c>/usr/share/git-core/templates</c>).</summary>
    Template = 4,

    /// <summary>Home directory.</summary>
    Home = 5,
}
