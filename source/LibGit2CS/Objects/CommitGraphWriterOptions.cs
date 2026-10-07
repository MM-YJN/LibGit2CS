// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Objects;

/// <summary>
/// Options for the <see cref="CommitGraphWriter"/>. Managed port of
/// <c>git_commit_graph_writer_options</c> (<c>include/git2/sys/commit_graph.h</c>).
/// </summary>
/// <param name="ObjectFormat">
/// The hash algorithm (OID type) to use for commit-graph generation. Defaults to
/// <see cref="GitHashAlgorithmKind.Sha1"/>. Matches <c>git_commit_graph_writer_options.oid_type</c>.
/// </param>
public sealed record CommitGraphWriterOptions(
    GitHashAlgorithmKind ObjectFormat = GitHashAlgorithmKind.Sha1);
