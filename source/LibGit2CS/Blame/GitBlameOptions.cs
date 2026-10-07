// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Blame;

/// <summary>
/// Options for a blame operation. Managed equivalent of libgit2's
/// <c>git_blame_options</c>. The <c>version</c> field is dropped (managed ABI).
/// </summary>
public sealed record GitBlameOptions
{
    /// <summary>
    /// The first line in the file to blame (1-based). Default is 1. A value of
    /// 0 or null is treated as 1. Matches <c>git_blame_options.min_line</c>.
    /// </summary>
    public int? MinLine { get; init; }

    /// <summary>
    /// The last line in the file to blame. Default (null or 0) is the last line
    /// of the file. Matches <c>git_blame_options.max_line</c>.
    /// </summary>
    public int? MaxLine { get; init; }

    /// <summary>
    /// The OID of the newest commit to consider. Default (null or zero) is HEAD.
    /// Matches <c>git_blame_options.newest_commit</c>.
    /// </summary>
    public GitOid? NewestCommit { get; init; }

    /// <summary>
    /// The OID of the oldest commit to consider. Default (null or zero) is the
    /// first commit encountered with a NULL parent. Matches
    /// <c>git_blame_options.oldest_commit</c>.
    /// </summary>
    public GitOid? OldestCommit { get; init; }

    /// <summary>
    /// A combination of <see cref="GitBlameFlags"/>. Default is
    /// <see cref="GitBlameFlags.Normal"/>.
    /// </summary>
    public GitBlameFlags Flags { get; init; }

    /// <summary>
    /// An optional pre-loaded mailmap. When <see cref="Flags"/> includes
    /// <see cref="GitBlameFlags.UseMailmap"/> and this is null, the mailmap is
    /// auto-loaded from the repository. When non-null, this mailmap is used
    /// directly. This is a C# extension — C only has the flag (auto-load).
    /// </summary>
    public GitMailmap? Mailmap { get; init; }
}
