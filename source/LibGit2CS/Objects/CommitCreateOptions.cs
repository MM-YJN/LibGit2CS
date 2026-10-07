// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Objects;

/// <summary>
/// Options for creating a commit. Managed equivalent of the parameters to
/// libgit2's <c>git_commit_create</c> / <c>git_commit_create_from_ids</c>.
/// </summary>
/// <remarks>
/// <para>
/// C's <c>git_commit_create</c> uses varargs for the parent list. The managed
/// port uses <see cref="Parents"/> as <see cref="IReadOnlyList{GitOid}"/> — no
/// varargs in C#.
/// </para>
/// <para>
/// <see cref="UpdateRef"/> is the ref to update after creating the commit (e.g.
/// <c>"HEAD"</c>). When non-null, the ref is updated atomically with a reflog
/// entry.
/// </para>
/// </remarks>
public sealed record CommitCreateOptions
{
    /// <summary>
    /// The tree OID this commit points at. Required.
    /// </summary>
    public GitOid Tree { get; init; }

    /// <summary>
    /// The parent commit OIDs, in order. Empty for a root commit.
    /// </summary>
    public IReadOnlyList<GitOid> Parents { get; init; } = [];

    /// <summary>
    /// The author signature (who wrote the change). Required.
    /// </summary>
    public required GitSignature Author { get; init; }

    /// <summary>
    /// The committer signature (who applied the change). Required.
    /// </summary>
    public required GitSignature Committer { get; init; }

    /// <summary>
    /// The commit message encoding (e.g. <c>"UTF-8"</c>), or null for default.
    /// </summary>
    public string? MessageEncoding { get; init; }

    /// <summary>
    /// The commit message. Required.
    /// </summary>
    public required string Message { get; init; }

    /// <summary> The commit message as raw bytes. byte-parity surface — C writes the message bytes verbatim (commit.c:73). When set, wins over <see
    /// cref="Message"/> (which stays the UTF-8 display tier). </summary>
    public ReadOnlyMemory<byte>? MessageBytes { get; init; }

    /// <summary>
    /// The ref to update after creating the commit (e.g. <c>"HEAD"</c>).
    /// Null = don't update any ref. When set, the ref is updated atomically
    /// via <c>Refs.Create</c>. The named reference is written directly; to preserve
    /// a symbolic HEAD, supply its resolved branch name instead of <c>"HEAD"</c>.
    /// </summary>
    public string? UpdateRef { get; init; }

    /// <summary>
    /// If true, allow creating a commit even when the index matches HEAD
    /// (no staged changes). Used by <see cref="Commit.CreateFromStageAsync"/>.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool AllowEmptyCommit { get; init; }
}
