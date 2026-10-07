// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary>
/// Filesystem-specific default reject set. On Windows this includes the full
/// set of Windows checks; on POSIX it is <see cref="GitPathRejectFlags.EmptyComponent"/> |
/// <see cref="GitPathRejectFlags.Traversal"/>. Matches <c>GIT_FS_PATH_REJECT_FILESYSTEM_DEFAULTS</c>.
/// </summary>
internal static class PathRejectPresets
{
    /// <summary>The filesystem-defaults preset for the current platform.</summary>
    public static readonly GitPathRejectFlags FilesystemDefaults = OperatingSystem.IsWindows()
        ? GitPathRejectFlags.EmptyComponent
          | GitPathRejectFlags.Traversal
          | GitPathRejectFlags.Backslash
          | GitPathRejectFlags.TrailingDot
          | GitPathRejectFlags.TrailingSpace
          | GitPathRejectFlags.TrailingColon
          | GitPathRejectFlags.DosPaths
          | GitPathRejectFlags.NtChars
        : GitPathRejectFlags.EmptyComponent | GitPathRejectFlags.Traversal;

    /// <summary>
    /// Default reject set for workdir paths (checkout). Matches
    /// <c>GIT_PATH_REJECT_WORKDIR_DEFAULTS</c>.
    /// </summary>
    public static readonly GitPathRejectFlags WorkdirDefaults = FilesystemDefaults | GitPathRejectFlags.DotGit;
}
