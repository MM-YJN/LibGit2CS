// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Objects;

/// <summary>
/// Git object types. Maps 1:1 to <c>git_object_t</c> in <c>include/git2/types.h</c>.
/// </summary>
/// <remarks>
/// <see cref="OfsDelta"/> and <see cref="RefDelta"/> are internal to the pack format
/// (delta-encoded objects); they never appear as loose objects or in the public API.
/// </remarks>
public enum GitObjectType
{
    /// <summary>Reserved / invalid. (<c>GIT_OBJECT__EXT1 = 0</c>)</summary>
    Ext1 = 0,

    /// <summary>Commit object. (<c>GIT_OBJECT_COMMIT = 1</c>)</summary>
    Commit = 1,

    /// <summary>Tree object. (<c>GIT_OBJECT_TREE = 2</c>)</summary>
    Tree = 2,

    /// <summary>Blob object. (<c>GIT_OBJECT_BLOB = 3</c>)</summary>
    Blob = 3,

    /// <summary>Annotated tag object. (<c>GIT_OBJECT_TAG = 4</c>)</summary>
    Tag = 4,

    /// <summary>Reserved / invalid. (<c>GIT_OBJECT__EXT2 = 5</c>)</summary>
    Ext2 = 5,

    /// <summary>
    /// Offset delta — pack-internal encoding where the base is referenced by
    /// negative offset within the same pack. (<c>GIT_OBJECT_OFS_DELTA = 6</c>)
    /// </summary>
    OfsDelta = 6,

    /// <summary>
    /// Reference delta — pack-internal encoding where the base is referenced by
    /// OID. (<c>GIT_OBJECT_REF_DELTA = 7</c>)
    /// </summary>
    RefDelta = 7,

    /// <summary>Match any object type (for lookup). (<c>GIT_OBJECT_ANY = -2</c>)</summary>
    Any = -2,
}
