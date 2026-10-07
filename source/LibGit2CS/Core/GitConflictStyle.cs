// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Merge conflict marker style. Maps to the <c>merge.conflictstyle</c> config
/// values and to <c>git_checkout_strategy_t</c> conflict style flags.
/// </summary>
public enum GitConflictStyle
{
    /// <summary>
    /// Standard <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>/<c>=======</c>/<c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>
    /// conflict markers (ours then theirs). Default.
    /// </summary>
    Merge = 0,

    /// <summary>
    /// Diff3-style: includes the ancestor (base) section between
    /// <c>|||||||</c> and <c>=======</c>.
    /// </summary>
    Diff3 = 1,

    /// <summary>
    /// Zealous diff3: like diff3 but tries to trim common lines from the
    /// ours/theirs sections.
    /// </summary>
    ZealousDiff3 = 2,
}
