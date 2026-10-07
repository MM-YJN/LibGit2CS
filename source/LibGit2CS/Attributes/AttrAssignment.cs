// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// A single attribute assignment within a rule (e.g. <c>diff=foo</c>,
/// <c>-merge</c>). Managed port of <c>git_attr_assignment</c> in
/// <c>src/libgit2/attr_file.h:92-97</c>.
/// </summary>
internal sealed class AttrAssignment(string name, uint nameHash, GitAttrValue value)
{
    public string Name => name;
    public uint NameHash => nameHash;
    public GitAttrValue Value => value;
}
