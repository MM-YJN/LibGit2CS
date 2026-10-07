// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Public per-file flags. Maps 1:1 to <c>git_diff_flag_t</c> in
/// <c>include/git2/diff.h:204-211</c>.
/// </summary>
[Flags]
public enum GitDiffFileFlags
{
    /// <summary>File(s) treated as binary data. (<c>GIT_DIFF_FLAG_BINARY</c>; <c>1u &lt;&lt; 0</c>)</summary>
    Binary = 1 << 0,

    /// <summary>File(s) treated as text data. (<c>GIT_DIFF_FLAG_NOT_BINARY</c>; <c>1u &lt;&lt; 1</c>)</summary>
    NotBinary = 1 << 1,

    /// <summary><c>id</c> value is known correct. (<c>GIT_DIFF_FLAG_VALID_ID</c>; <c>1u &lt;&lt; 2</c>)</summary>
    ValidId = 1 << 2,

    /// <summary>File exists at this side of the delta. (<c>GIT_DIFF_FLAG_EXISTS</c>; <c>1u &lt;&lt; 3</c>)</summary>
    Exists = 1 << 3,

    /// <summary>File size value is known correct. (<c>GIT_DIFF_FLAG_VALID_SIZE</c>; <c>1u &lt;&lt; 4</c>)</summary>
    ValidSize = 1 << 4,
}
