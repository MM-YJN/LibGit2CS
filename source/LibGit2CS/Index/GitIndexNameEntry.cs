// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Index;

/// <summary>
/// A conflict-name (NAME) index extension entry. Records the original paths
/// of conflicted entries. Matches <c>git_index_name_entry</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each field is a byte-faithful <see cref="GitPath"/>? (ports the raw
/// <c>char *</c> in <c>git_index_name_entry</c>). <c>null</c> represents an
/// absent side, matching the on-disk empty (NUL-only) entry.
/// </para>
/// </remarks>
public sealed record GitIndexNameEntry(
    GitPath? Ancestor,
    GitPath? Ours,
    GitPath? Theirs);
