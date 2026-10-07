// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Objects;

/// <summary>
/// Helpers for interpreting raw tree-entry mode bits. Managed port of the
/// inline helpers in <c>src/libgit2/tree.c</c> (<c>normalize_filemode</c>,
/// <c>git_tree_entry__is_tree</c>) and the <c>S_IFMT</c> / <c>S_IS*</c>
/// POSIX macros they rely on.
/// </summary>
public static class GitFileModeExtensions
{
    private const ushort IFMT = 0xF000;
    private const ushort IFDIR = 0x4000;
    private const ushort IFLNK = 0xA000;
    private const ushort IFGITLINK = 0xE000;
    private const ushort IXUSR = 0x40;

    /// <summary>
    /// Collapses raw mode bits into a canonical <see cref="GitFileMode"/>. Matches
    /// <c>normalize_filemode</c> in <c>tree.c:35-55</c>.
    /// </summary>
    /// <remarks>
    /// Order matters: directory (0040000) is checked before submodule (0160000)
    /// so the two non-overlapping <c>S_IFMT</c> cases stay distinct.
    /// </remarks>
    public static GitFileMode Normalize(ushort raw)
    {
        ushort type = (ushort)(raw & IFMT);

        if (type == IFDIR)
        {
            return GitFileMode.Tree;
        }

        if ((raw & IXUSR) != 0)
        {
            return GitFileMode.Executable;
        }

        if (type == IFGITLINK)
        {
            return GitFileMode.GitLink;
        }

        if (type == IFLNK)
        {
            return GitFileMode.Symlink;
        }

        return GitFileMode.Regular;
    }

    /// <summary>
    /// Maps a raw mode to the <see cref="GitObjectType"/> of the entry's target.
    /// Matches <c>git_tree_entry_type</c> in <c>tree.c:276-286</c>.
    /// </summary>
    public static GitObjectType TypeFromMode(ushort mode)
    {
        ushort type = (ushort)(mode & IFMT);

        if (type == IFGITLINK)
        {
            return GitObjectType.Commit;
        }

        if (type == IFDIR)
        {
            return GitObjectType.Tree;
        }

        return GitObjectType.Blob;
    }

    /// <summary>
    /// True if the mode names a directory entry (submodule excluded). Matches
    /// <c>git_tree_entry__is_tree</c> in <c>tree.h:39-42</c>.
    /// </summary>
    /// <remarks>
    /// libgit2's macro is <c>S_ISDIR(m) &amp;&amp; !S_ISGITLINK(m)</c>, but the
    /// second half is dead code (S_IFDIR != S_IFGITLINK, so the conjunction is
    /// fully covered by the first half). Simplified here.
    /// </remarks>
    public static bool IsTree(ushort mode)
        => (mode & IFMT) == IFDIR;

    /// <summary>
    /// True if the mode names a submodule (gitlink) entry. Matches
    /// <c>S_ISGITLINK</c> in <c>posix.h:19</c>.
    /// </summary>
    public static bool IsGitLink(ushort mode)
        => (mode & IFMT) == IFGITLINK;
}
