// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

namespace LibGit2CS.Config;

/// <summary>
/// How a <see cref="GitConfigurationMapItem{T}"/> matches its input. Matches
/// libgit2's <c>git_configmap_t</c>.
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "'Int32'/'String' match libgit2's GIT_CONFIGMAP_INT32/GIT_CONFIGMAP_STRING identifiers.")]
public enum GitConfigurationMapType
{
    /// <summary>Matches when the value parses as boolean false.</summary>
    False = 0,

    /// <summary>Matches when the value parses as boolean true.</summary>
    True = 1,

    /// <summary>Matches when the value parses as a 32-bit integer.</summary>
    Int32 = 2,

    /// <summary>Matches when the value equals <see cref="GitConfigurationMapItem{T}.StringMatch"/> (case-insensitive).</summary>
    String = 3,
}
