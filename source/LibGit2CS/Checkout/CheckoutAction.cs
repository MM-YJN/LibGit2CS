// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Checkout;

/// <summary>
/// Internal checkout action bitmask. Matches the <c>CHECKOUT_ACTION__*</c>
/// macros in <c>checkout.c:38-49</c>.
/// </summary>
[Flags]
internal enum CheckoutAction : uint
{
    None = 0,
    Remove = 1,
    UpdateBlob = 2,
    UpdateSubmodule = 4,
    Conflict = 8,
    RemoveConflict = 16,
    UpdateConflict = 32,
    RemoveAndUpdate = Remove | UpdateBlob,
}
