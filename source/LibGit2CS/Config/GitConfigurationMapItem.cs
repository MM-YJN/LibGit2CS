// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Config;

/// <summary>
/// A single mapping entry in a <see cref="GitConfigurationMap{T}"/>.
/// </summary>
/// <param name="Type">How to interpret and match <paramref name="StringMatch"/>.</param>
/// <param name="StringMatch">The string to match for <see cref="GitConfigurationMapType.String"/> items; ignored otherwise.</param>
/// <param name="Value">The value to return when this item matches.</param>
public readonly record struct GitConfigurationMapItem<T>(
    GitConfigurationMapType Type,
    string? StringMatch,
    T Value);
