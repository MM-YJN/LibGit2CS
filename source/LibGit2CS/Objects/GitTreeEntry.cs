// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.Objects;

/// <summary> A single entry in a git tree. Maps to libgit2's <c>git_tree_entry</c>. </summary> <remarks> <para> Value type — no <c>IDisposable</c>/<c>free</c>
/// needed (unlike C's refcounted <c>git_tree_entry</c>). The filename is a byte-faithful <see cref="GitPath"/> sliced zero-copy from the retained raw ODB
/// buffer (matching <c>entry->filename = buffer</c> in <c>tree.c:435</c>); entries constructed by the builder own their bytes. </para> <para> Sorting uses git's tree-order: directory names get an implicit trailing <c>/</c> for comparison. Use <see cref="CompareTo"/> for this
/// canonical order; do not use plain byte comparison. </para> </remarks>
public readonly record struct GitTreeEntry
{
    /// <summary>Creates a tree entry.</summary>
    public GitTreeEntry(GitFileMode mode, GitOid id, GitPath name, GitObjectType type)
        : this(mode, id, name, type, (ushort)mode)
    {
    }

    /// <summary>Creates a tree entry with an explicit raw mode.</summary>
    public GitTreeEntry(GitFileMode mode, GitOid id, GitPath name, GitObjectType type, ushort rawMode)
    {
        Mode = mode;
        RawMode = rawMode;
        Id = id;
        Name = name;
        Type = type;
    }

    /// <summary>The canonical file mode (already normalized).</summary>
    public GitFileMode Mode { get; }

    /// <summary>
    /// The RAW mode bits as parsed from the tree object (tree.c:433-434
    /// stores <c>entry-&gt;attr</c> verbatim). Matches
    /// <c>git_tree_entry_filemode_raw</c> — a treebuilder seeded from this
    /// entry re-writes these exact bytes.
    /// </summary>
    public ushort RawMode { get; }

    /// <summary>The OID of the entry's target object.</summary>
    public GitOid Id { get; }

    /// <summary>
    /// The entry name (filename or directory name), stored byte-faithfully as a
    /// <see cref="GitPath"/>. Compared byte-wise throughout the tree subsystem.
    /// </summary>
    public GitPath Name { get; }

    /// <summary>The type derived from <see cref="Mode"/> (Commit/Tree/Blob).</summary>
    public GitObjectType Type { get; }

    /// <summary>
    /// Returns true if this entry is a directory (sub-tree), excluding submodules.
    /// </summary>
    public bool IsTree => Mode == GitFileMode.Tree;

    /// <summary>
    /// Returns true if this entry is a submodule (gitlink).
    /// </summary>
    public bool IsGitLink => Mode == GitFileMode.GitLink;

    /// <summary>
    /// Compares two entries using git's tree sort order: names are compared
    /// bytewise, but a directory entry (submodule excluded) is treated as if
    /// its name had a trailing <c>/</c>. Matches <c>entry_sort_cmp</c> +
    /// <c>git_fs_path_cmp</c>.
    /// </summary>
    public int CompareTo(in GitTreeEntry other)
        => GitPath.CompareTreeOrder(Name, IsTree, other.Name, other.IsTree);
}

