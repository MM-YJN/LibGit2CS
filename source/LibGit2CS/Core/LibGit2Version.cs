// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// The libgit2 version string this port targets. Matches
/// <c>LIBGIT2_VERSION</c> in <c>include/git2/version.h</c>.
/// </summary>
/// <remarks>
/// Used by <c>EmailFormatter</c> for the <c>format-patch</c> trailer
/// <c>-- \nlibgit2 &lt;version&gt;\n\n</c>. Golden tests embed this same
/// constant in their expected output so the comparison is consistent.
/// </remarks>
internal static class LibGit2Version
{
    /// <summary>The version string (e.g. <c>"1.9.4"</c>).</summary>
    public const string String = "1.9.4";
}
