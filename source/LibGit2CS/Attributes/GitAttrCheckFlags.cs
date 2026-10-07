// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// Attribute lookup flags. Maps 1:1 to <c>git_attr_check_t</c> in
/// <c>include/git2/attr.h</c>.
/// </summary>
[Flags]
public enum GitAttrCheckFlags
{
    /// <summary>Workdir file then index file at each level. (<c>GIT_ATTR_CHECK_FILE_THEN_INDEX = 0</c>)</summary>
    FileThenIndex = 0,

    /// <summary>Index file then workdir file. (<c>GIT_ATTR_CHECK_INDEX_THEN_FILE = 1</c>)</summary>
    IndexThenFile = 1,

    /// <summary>Index file only. (<c>GIT_ATTR_CHECK_INDEX_ONLY = 2</c>)</summary>
    IndexOnly = 2,

    /// <summary>Do not read the system-wide file. (<c>GIT_ATTR_CHECK_NO_SYSTEM = 1 &lt;&lt; 2</c>)</summary>
    NoSystem = 1 << 2,

    /// <summary>Read the file from the HEAD commit. (<c>GIT_ATTR_CHECK_INCLUDE_HEAD = 1 &lt;&lt; 3</c>)</summary>
    IncludeHead = 1 << 3,

    /// <summary>Read the file from the given commit. (<c>GIT_ATTR_CHECK_INCLUDE_COMMIT = 1 &lt;&lt; 4</c>)</summary>
    IncludeCommit = 1 << 4,
}
