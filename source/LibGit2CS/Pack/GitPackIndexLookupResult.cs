// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Pack;

/// <summary>
/// Result of a pack index OID lookup. Returned by
/// <see cref="GitPackIndex.FindIndex"/>.
/// </summary>
/// <param name="Index">The 0-based index of the found object, or -1 if not found.</param>
/// <param name="Ambiguous">True if multiple objects match the abbreviated OID prefix.</param>
public readonly record struct GitPackIndexLookupResult(int Index, bool Ambiguous)
{
    /// <summary>True if exactly one object was found.</summary>
    public bool Found => Index >= 0 && !Ambiguous;
}
