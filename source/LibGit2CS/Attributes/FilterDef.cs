// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// A registered filter definition. Managed port of <c>git_filter_def</c>
/// (filter.c:45-53).
/// </summary>
internal sealed class FilterDef
{
    public FilterDef(string name, IFilter filter, int priority, int nattrs, int nmatches, List<AttrSpec> specs)
    {
        Name = name;
        Filter = filter;
        Priority = priority;
        Nattrs = nattrs;
        Nmatches = nmatches;
        Specs = specs;
    }

    public string Name { get; }
    public IFilter Filter { get; }
    public int Priority { get; }
    public int Nattrs { get; }
    public int Nmatches { get; }
    public List<AttrSpec> Specs { get; }
}
