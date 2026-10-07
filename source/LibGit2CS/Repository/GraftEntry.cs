// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Repository;

/// <summary>
/// A single graft entry: the commit OID and its replacement parents.
/// </summary>
internal readonly record struct GraftEntry(GitOid Commit, IReadOnlyList<GitOid> Parents);
