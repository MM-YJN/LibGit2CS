// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;
/// <summary>
/// Filter-list flags. Maps to <c>git_filter_flag_t</c>.
/// </summary>
[Flags]
public enum GitFilterListFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Allow filters that may be unsafe. (<c>GIT_FILTER_ALLOW_UNSAFE</c>; <c>1u &lt; &lt; 0</c>)</summary>
    AllowUnsafe = 1 << 0,

    /// <summary>Apply filters as if the path is the root. (<c>GIT_FILTER_NO_SYSTEM_ATTRIBUTES</c>; <c>1u &lt; &lt; 1</c>)</summary>
    NoSystemAttributes = 1 << 1,

    /// <summary>Load attributes from the HEAD commit. (<c>GIT_FILTER_ATTRIBUTES_FROM_HEAD</c>; <c>1u &lt; &lt; 2</c>)</summary>
    AttributesFromHead = 1 << 2,

    /// <summary>Load attributes from a specific commit. (<c>GIT_FILTER_ATTRIBUTES_FROM_COMMIT</c>; <c>1u &lt; &lt; 3</c>)</summary>
    AttributesFromCommit = 1 << 3,
}
