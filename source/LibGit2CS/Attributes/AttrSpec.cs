// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// A single parsed attribute specification from a filter's
/// <c>Attributes</c> string. Matches the entries in <c>git_filter_def.attrs[]</c>.
/// </summary>
internal sealed class AttrSpec
{
    public AttrSpec(string name, string? expectedValue, bool requiresMatch)
    {
        Name = name;
        ExpectedValue = expectedValue;
        RequiresMatch = requiresMatch;
    }

    /// <summary>The attribute name to look up.</summary>
    public string Name { get; }

    /// <summary>
    /// The expected value (for <c>=value</c>, <c>+name</c>, <c>-name</c>,
    /// <c>!name</c>), or null for a bare name (just load the value).
    /// </summary>
    public string? ExpectedValue { get; }

    /// <summary>True if this spec requires a specific match (has <c>=/+/-/!</c>).</summary>
    public bool RequiresMatch { get; }
}
