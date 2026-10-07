// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Attributes;

/// <summary>
/// A pattern + its attribute assignments. Managed port of
/// <c>git_attr_rule</c> in <c>src/libgit2/attr_file.h:81-84</c>.
/// </summary>
internal sealed class AttrRule(FnMatchPattern match)
{
    public FnMatchPattern Match => match;
    public Dictionary<string, AttrAssignment> Assigns { get; } = [];

    /// <summary>
    /// Looks up an assignment by name. Matches
    /// <c>git_attr_rule__lookup_assignment</c> (attr_file.c:550-562).
    /// </summary>
    public AttrAssignment? LookupAssignment(string name)
        => Assigns.TryGetValue(name, out AttrAssignment? a) ? a : null;
}
