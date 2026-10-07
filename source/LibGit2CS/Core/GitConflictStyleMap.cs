// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Config;

namespace LibGit2CS.Core;

/// <summary>
/// Configuration map for <c>merge.conflictstyle</c>. Maps the string config
/// value to the <see cref="GitConflictStyle"/> enum.
/// </summary>
public static class GitConflictStyleMap
{
    /// <summary>The configuration map for <c>merge.conflictstyle</c>.</summary>
    public static readonly GitConfigurationMap<GitConflictStyle> Map = new(
    [
        new(GitConfigurationMapType.String, "merge", GitConflictStyle.Merge),
        new(GitConfigurationMapType.String, "diff3", GitConflictStyle.Diff3),
        new(GitConfigurationMapType.String, "zdiff3", GitConflictStyle.ZealousDiff3),
    ]);
}
