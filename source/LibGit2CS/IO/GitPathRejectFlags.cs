// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary>
/// Path validation flags. Maps 1:1 to libgit2's <c>GIT_FS_PATH_REJECT_*</c>
/// (fs_path.h) and <c>GIT_PATH_REJECT_*</c> (path.h) flags.
/// </summary>
/// <remarks>
/// Combine with bitwise OR. Use <see cref="PathRejectPresets.WorkdirDefaults"/> or
/// <see cref="IndexDefaults"/> for the standard presets.
/// </remarks>
[Flags]
public enum GitPathRejectFlags
{
    /// <summary>No rejection — accept any path.</summary>
    None = 0,

    /// <summary>Reject empty path components (consecutive slashes).</summary>
    EmptyComponent = 1 << 0,

    /// <summary>Reject <c>.</c> and <c>..</c> traversal.</summary>
    Traversal = 1 << 1,

    /// <summary>Reject forward slashes in individual components.</summary>
    Slash = 1 << 2,

    /// <summary>Reject backslashes in individual components.</summary>
    Backslash = 1 << 3,

    /// <summary>Reject components with a trailing dot.</summary>
    TrailingDot = 1 << 4,

    /// <summary>Reject components with a trailing space.</summary>
    TrailingSpace = 1 << 5,

    /// <summary>Reject components with a trailing colon.</summary>
    TrailingColon = 1 << 6,

    /// <summary>Reject DOS reserved device names (CON, PRN, AUX, NUL, COM*, LPT*).</summary>
    DosPaths = 1 << 7,

    /// <summary>Reject Windows-NT forbidden characters (&lt;, &gt;, :, ", |, ?, *, control chars).</summary>
    NtChars = 1 << 8,

    /// <summary>Reject paths exceeding the platform max path length (Windows only).</summary>
    LongPaths = 1 << 9,

    /// <summary>Reject <c>.git</c> — expands to the appropriate dotgit checks.</summary>
    DotGit = 1 << 10,

    /// <summary>Reject <c>.git</c> literally (case-insensitive ASCII).</summary>
    DotGitLiteral = 1 << 11,

    /// <summary>Reject <c>.git</c> with HFS Unicode normalization.</summary>
    DotGitHfs = 1 << 12,

    /// <summary>Reject <c>.git</c> with NTFS 8.3 short-name rules.</summary>
    DotGitNtfs = 1 << 13,

    /// <summary>
    /// Default reject set for index paths. Matches
    /// <c>GIT_PATH_REJECT_INDEX_DEFAULTS</c>.
    /// </summary>
    IndexDefaults = Traversal | DotGit,
}
