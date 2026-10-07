// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary>
/// Flags controlling <see cref="WildMatch"/> behavior. Matches libgit2's
/// <c>WM_PATHNAME</c>/<c>WM_CASEFOLD</c>.
/// </summary>
[Flags]
internal enum WildMatchFlags
{
    /// <summary>No special handling: <c>*</c> crosses slashes, case-sensitive.</summary>
    None = 0,

    /// <summary>
    /// Case-insensitive comparison. Matches libgit2's <c>WM_CASEFOLD</c>
    /// (wildmatch.h:13: <c>#define WM_CASEFOLD 1</c>).
    /// </summary>
    CaseInsensitive = 1,

    /// <summary>
    /// <c>*</c> does not match <c>/</c>; <c>**</c> does. Matches libgit2's
    /// <c>WM_PATHNAME</c> (wildmatch.h:14: <c>#define WM_PATHNAME 2</c>).
    /// </summary>
    Pathname = 2,
}
