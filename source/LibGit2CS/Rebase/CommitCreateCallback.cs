// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Objects;

namespace LibGit2CS.Rebase;

/// <summary>
/// Callback that allows overriding commit creation during rebase. Matches
/// <c>git_commit_create_cb</c> in <c>include/git2/commit.h</c>.
/// </summary>
/// <param name="author">Author signature (or null to use the original commit's author).</param>
/// <param name="committer">Committer signature.</param>
/// <param name="messageEncoding">Message encoding (or null for UTF-8).</param>
/// <param name="message">Commit message.</param>
/// <param name="tree">The tree OID for the commit.</param>
/// <param name="parents">Parent commits (single parent for rebase).</param>
/// <returns>
/// The OID of the created commit, or <c>null</c> with
/// <see cref="GitErrorCode.PassThrough"/> to fall through to default commit creation.
/// </returns>
public delegate GitOid? CommitCreateCallback(
    GitSignature? author,
    GitSignature committer,
    string? messageEncoding,
    string message,
    GitOid tree,
    IReadOnlyList<Commit> parents);
