// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Index;
using LibGit2CS.IO;

namespace LibGit2CS.Checkout;

/// <summary>
/// Internal conflict description. Matches <c>checkout_conflictdata</c> in
/// <c>checkout.c:79-89</c>.
/// </summary>
internal sealed class CheckoutConflictData
{
    public GitIndexEntry? Ancestor { get; set; }
    public GitIndexEntry? Ours { get; set; }
    public GitIndexEntry? Theirs { get; set; }
    public bool NameCollision { get; set; }
    public bool DirectoryFile { get; set; }
    public bool OneToTwo { get; set; }
    public bool Binary { get; set; }
    public bool Submodule { get; set; }

    /// <summary>
    /// The conflict path (byte-faithful). In C, the lookup key is
    /// <c>ancestor->path</c> (checkout_conflictdata has no standalone path
    /// field); the C# port adds one for convenience. Conflict comparisons stay
    /// case-sensitive raw <c>strcmp</c> (checkout_idxentry_cmp at
    /// checkout.c:806) — NOT the diff's icase comparator — so use
    /// <see cref="LibGit2CS.IO.GitPath.Equals(LibGit2CS.IO.GitPath)"/>/Compare directly, not the diff's _strcomp slot.
    /// </summary>
    public GitPath Path { get; set; }
}
