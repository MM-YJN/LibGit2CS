// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary>
/// Ownership classes for <c>git_fs_path_owner_is</c>. Managed port of
/// <c>git_fs_path_owner_t</c> (<c>src/util/fs_path.h:765-788</c>).
/// </summary>
[Flags]
internal enum GitFsPathOwner
{
    /// <summary>No owner class — clears the mock (real ownership checks).</summary>
    None = 0,

    /// <summary>The file must be owned by the current user.</summary>
    CurrentUser = 1 << 0,

    /// <summary>The file must be owned by the system account.</summary>
    Administrator = 1 << 1,

    /// <summary>
    /// The file may be owned by a system account if the current user is in an
    /// administrator group. Windows only; a noop on non-Windows systems.
    /// </summary>
    UserIsAdministrator = 1 << 2,

    /// <summary>The file is owned by the current user, who is running <c>sudo</c>.</summary>
    RunningSudo = 1 << 3,

    /// <summary>The file may be owned by another user.</summary>
    Other = 1 << 4,
}
