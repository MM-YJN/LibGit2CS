// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.Objects;

/// <summary>
/// A single update to apply during <see cref="GitTree.CreateUpdatedAsync"/>. Matches
/// <c>git_tree_update</c>.
/// </summary>
public readonly record struct GitTreeUpdate
{
    /// <summary>The update action (upsert or remove).</summary>
    public GitTreeUpdateAction Action { get; init; }

    /// <summary>The target OID (ignored for <see cref="GitTreeUpdateAction.Remove"/>).</summary>
    public GitOid Id { get; init; }

    /// <summary>The file mode / object kind.</summary>
    public GitFileMode FileMode { get; init; }

    /// <summary>The full path from the root tree (e.g. <c>"src/foo.txt"</c>). Byte-faithful.</summary>
    public GitPath Path { get; init; }

    /// <summary>Creates a tree update with a <c>string</c> path (encoded as UTF-8).</summary>
    public GitTreeUpdate(GitTreeUpdateAction action, string path, GitOid id = default, GitFileMode fileMode = GitFileMode.Regular)
        : this(action, GitPath.FromUtf8String(path), id, fileMode)
    {
    }

    /// <summary>Creates a tree update with a byte-faithful <see cref="GitPath"/>.</summary>
    public GitTreeUpdate(GitTreeUpdateAction action, GitPath path, GitOid id = default, GitFileMode fileMode = GitFileMode.Regular)
    {
        Action = action;
        Path = path;
        Id = id;
        FileMode = fileMode;
    }
}
