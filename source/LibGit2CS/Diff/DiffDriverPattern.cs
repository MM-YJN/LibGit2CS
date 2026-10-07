// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Diff;

/// <summary>
/// A single funcname pattern (regex + negate flag). Managed port of
/// <c>git_diff_driver_pattern</c> in <c>diff_driver.c:27-30</c>.
/// </summary>
internal sealed class DiffDriverPattern(RegexAdapter regex, bool negate)
{
    public RegexAdapter Regex => regex;
    public bool Negate => negate;
}
