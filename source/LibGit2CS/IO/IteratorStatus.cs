// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary>Directory status reported by <see cref="IIterator.AdvanceOverAsync"/>.
/// Matches <c>git_iterator_status_t</c>.</summary>
internal enum IteratorStatus
{
    /// <summary>Directory contains non-ignored files.</summary>
    Normal = 0,

    /// <summary>Directory contains only ignored files.</summary>
    Ignored = 1,

    /// <summary>Directory is empty.</summary>
    Empty = 2,

    /// <summary>Directory was not requested via pathlist.</summary>
    Filtered = 3,
}
