// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// Classification of a single-file conflict across three trees during a
/// 3-way merge. Matches <c>git_merge_diff_t</c> in <c>merge.h:31-73</c>.
/// </summary>
[Flags]
internal enum MergeDiffType
{
    /// <summary>No conflict (all sides match the ancestor).</summary>
    None = 0,

    /// <summary>Both ours and theirs modified the same file.</summary>
    BothModified = 1 << 0,

    /// <summary>Both ours and theirs added the same file.</summary>
    BothAdded = 1 << 1,

    /// <summary>Both ours and theirs deleted the same file.</summary>
    BothDeleted = 1 << 2,

    /// <summary>Ours modified, theirs deleted (or vice versa).</summary>
    ModifiedDeleted = 1 << 3,

    /// <summary>Ours renamed and modified the file.</summary>
    RenamedModified = 1 << 4,

    /// <summary>Ours renamed and deleted the file.</summary>
    RenamedDeleted = 1 << 5,

    /// <summary>Ours renamed and theirs added a file at the same path.</summary>
    RenamedAdded = 1 << 6,

    /// <summary>Both sides renamed the file to the same name.</summary>
    BothRenamed = 1 << 7,

    /// <summary>Both renamed: one file split into two (1-to-2).</summary>
    BothRenamed1To2 = 1 << 8,

    /// <summary>Both renamed: two files merged into one (2-to-1).</summary>
    BothRenamed2To1 = 1 << 9,

    /// <summary>Directory/file conflict: one side has a file, other has a dir.</summary>
    DirectoryFile = 1 << 10,

    /// <summary>This entry is a child of a directory/file conflict.</summary>
    DfChild = 1 << 11,
}
