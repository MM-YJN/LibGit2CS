// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// The resolved state of a git attribute. Maps to the sentinel pointers
/// <c>git_attr__true</c> / <c>git_attr__false</c> / <c>git_attr__unset</c>
/// in <c>src/libgit2/attr.c:19-21</c> plus string values and the no-match
/// (NULL) case.
/// </summary>
public enum GitAttrValueKind
{
    /// <summary>No rule matched (NULL in C). Reported as unspecified.</summary>
    None = 0,

    /// <summary>Attribute set with no value, e.g. <c>diff</c>. (<c>git_attr__true</c>)</summary>
    True = 1,

    /// <summary>Attribute unset with <c>-</c>, e.g. <c>-diff</c>. (<c>git_attr__false</c>)</summary>
    False = 2,

    /// <summary>Attribute explicitly unspecified with <c>!</c>, e.g. <c>!diff</c>. (<c>git_attr__unset</c>)</summary>
    Unset = 3,

    /// <summary>Attribute set to a string value, e.g. <c>diff=foo</c>.</summary>
    Value = 4,
}
