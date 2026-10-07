// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary>Iterator flags. Matches <c>git_iterator_flag_t</c>.</summary>
[Flags]
internal enum IteratorFlags : uint
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Ignore case for entry sort order.</summary>
    IgnoreCase = 1u << 0,

    /// <summary>Force case sensitivity for entry sort order.</summary>
    DontIgnoreCase = 1u << 1,

    /// <summary>Return tree items in addition to blob items.</summary>
    IncludeTrees = 1u << 2,

    /// <summary>Don't flatten trees; requires <see cref="IIterator.AdvanceIntoAsync"/>.
    /// Implies <see cref="IncludeTrees"/>.</summary>
    DontAutoexpand = 1u << 3,

    /// <summary>Convert precomposed unicode to decomposed.</summary>
    PrecomposeUnicode = 1u << 4,

    /// <summary>Never convert precomposed unicode.</summary>
    DontPrecomposeUnicode = 1u << 5,

    /// <summary>Include index conflict entries.</summary>
    IncludeConflicts = 1u << 6,

    /// <summary>Descend into symlinked directories.</summary>
    DescendSymlinks = 1u << 7,

    /// <summary>Hash files in workdir/filesystem iterators.</summary>
    IncludeHash = 1u << 8,

    // Internal flags (not exposed to callers).

    /// <summary>Has the iterator been accessed yet? (internal)</summary>
    FirstAccess = 1u << 15,

    /// <summary>Should we check gitignore rules? (workdir only, internal)</summary>
    HonorIgnores = 1u << 16,

    /// <summary>Skip <c>.git</c> directory entries? (workdir only, internal)</summary>
    IgnoreDotGit = 1u << 17,
}
