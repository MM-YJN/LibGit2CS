// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// A resolved git attribute value. Managed port of the <c>const char *</c>
/// value pointer used throughout <c>attr_file.c</c> / <c>attr.c</c>.
/// </summary>
/// <param name="Kind">The value state.</param>
/// <param name="Text">The string value when <see cref="Kind"/> is <see cref="GitAttrValueKind.Value"/>.</param>
public readonly record struct GitAttrValue(GitAttrValueKind Kind, string? Text)
{
    /// <summary>An attribute with no matching assignment.</summary>
    public static readonly GitAttrValue None = new(GitAttrValueKind.None, null);
    /// <summary>An attribute set to true.</summary>
    public static readonly GitAttrValue True = new(GitAttrValueKind.True, null);
    /// <summary>An attribute set to false.</summary>
    public static readonly GitAttrValue False = new(GitAttrValueKind.False, null);
    /// <summary>An explicitly unspecified attribute.</summary>
    public static readonly GitAttrValue Unset = new(GitAttrValueKind.Unset, null);

    /// <summary>Creates an attribute with the supplied string value.</summary>
    public static GitAttrValue Specified(string text) => new(GitAttrValueKind.Value, text);

    /// <summary>True when no rule set the attribute (NULL or <c>!</c> in C). Matches <c>GIT_ATTR_IS_UNSPECIFIED</c>.</summary>
    public bool IsUnspecified => Kind is GitAttrValueKind.None or GitAttrValueKind.Unset;

    /// <summary>True when the attribute is set to true. Matches <c>GIT_ATTR_IS_TRUE</c>.</summary>
    public bool IsTrue => Kind == GitAttrValueKind.True;

    /// <summary>True when the attribute is set to false. Matches <c>GIT_ATTR_IS_FALSE</c>.</summary>
    public bool IsFalse => Kind == GitAttrValueKind.False;
}
