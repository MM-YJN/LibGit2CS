// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.Index;

/// <summary>
/// A resolve-undo (REUC) index extension entry. Records the modes and OIDs of
/// resolved conflict entries. Matches <c>git_index_reuc_entry</c>.
/// </summary>
/// <remarks>
/// <see cref="Path"/> is a byte-faithful <see cref="GitPath"/> (ports the raw
/// <c>char *path</c> in <c>reuc_entry_internal</c>, <c>index.c:117-121</c>).
/// </remarks>
public sealed record GitIndexReucEntry(
    GitPath Path,
    uint[] Modes,   // [3]: ancestor, ours, theirs (0 if absent)
    GitOid[] Oids); // [3]: ancestor, ours, theirs
