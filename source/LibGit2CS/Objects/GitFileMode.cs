// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

namespace LibGit2CS.Objects;

/// <summary>
/// Canonical git file modes for tree entries. Maps 1:1 to
/// <c>git_filemode_t</c> in <c>include/git2/types.h</c>.
/// </summary>
/// <remarks>
/// C# has no octal literal syntax; values are expressed in decimal (the
/// underlying POSIX octal values are noted in the doc-comments). Use
/// <see cref="GitFileModeExtensions.Normalize"/> to collapse raw mode bits
/// (as stored on disk for some entries) into one of these five canonical
/// values. <see cref="GitFileModeExtensions.TypeFromMode"/> maps a mode to
/// its <see cref="GitObjectType"/>.
/// </remarks>
[SuppressMessage("Performance", "IDE1006:Naming rule violation", Justification = "S_* prefix matches POSIX naming for these well-known stat macros.")]
[SuppressMessage("Design", "CA1028:Enum Storage should be Int32", Justification = "ushort is intentional — git tree-entry file modes are 16-bit on disk.")]
[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "no zero value — file modes never use 0; smallest is Tree (0x4000).")]
public enum GitFileMode : ushort
{
    /// <summary>Directory / subtree. (<c>GIT_FILEMODE_TREE</c>; POSIX <c>040000</c>)</summary>
    Tree = 0x4000,

    /// <summary>Regular file (non-executable). (<c>GIT_FILEMODE_BLOB</c>; POSIX <c>100644</c>)</summary>
    Regular = 0x81A4,

    /// <summary>Regular file, executable bit set. (<c>GIT_FILEMODE_BLOB_EXECUTABLE</c>; POSIX <c>100755</c>)</summary>
    Executable = 0x81ED,

    /// <summary>Symbolic link. (<c>GIT_FILEMODE_LINK</c>; POSIX <c>120000</c>)</summary>
    Symlink = 0xA000,

    /// <summary>Submodule (gitlink). (<c>GIT_FILEMODE_COMMIT</c>; POSIX <c>160000</c>)</summary>
    GitLink = 0xE000,
}
