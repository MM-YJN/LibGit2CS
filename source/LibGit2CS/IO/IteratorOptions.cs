// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core.Hashing;

namespace LibGit2CS.IO;

/// <summary>
/// Iterator creation options. Matches <c>git_iterator_options</c>.
/// </summary>
internal sealed record IteratorOptions
{
    /// <summary>Begin iteration at this path prefix, or null for no lower bound.</summary>
    public GitPath? Start { get; init; }

    /// <summary>Stop iteration at this path prefix, or null for no upper bound.</summary>
    public GitPath? End { get; init; }

    /// <summary>Literal paths to limit iteration to, or null for no pathlist.</summary>
    public GitPath[]? PathList { get; init; }

    /// <summary>Iterator flags.</summary>
    public IteratorFlags Flags { get; init; }

    /// <summary>OID type for non-workdir filesystem iterators.</summary>
    public GitHashAlgorithmKind OidType { get; init; } = GitHashAlgorithmKind.Sha1;

    /// <summary>Default options (no flags, no range, no pathlist).</summary>
    public static IteratorOptions Default { get; } = new();
}
