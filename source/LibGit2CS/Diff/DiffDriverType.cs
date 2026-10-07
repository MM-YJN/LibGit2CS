// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Driver type. Maps to <c>git_diff_driver_t</c> in
/// <c>src/libgit2/diff_driver.c:20-25</c>.
/// </summary>
internal enum DiffDriverType
{
    Auto = 0,
    Binary = 1,
    Text = 2,
    PatternList = 3,
}
