// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

namespace LibGit2CS.Revwalk;

/// <summary>
/// Revision specification flags describing the kind of range parsed. Matches
/// <c>git_revspec_t</c> (<c>git2/revparse.h:30-40</c>).
/// </summary>
[Flags]
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "'Single' matches libgit2's git_revspec_single / git_revparse_single identifiers.")]
public enum GitRevSpecFlags
{
    /// <summary>A single object (not a range). <c>from</c> holds it; <c>to</c> is null.</summary>
    Single = 1,

    /// <summary>A range <c>A..B</c>. <c>from</c> and <c>to</c> both set.</summary>
    Range = 2,

    /// <summary>A symmetric difference <c>A...B</c> (merge base). OR'd with <see cref="Range"/>.</summary>
    MergeBase = 4,
}
