// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;

namespace LibGit2CS.Diff;

/// <summary>
/// One side of a diff delta. Managed equivalent of <c>git_diff_file</c> in
/// <c>include/git2/diff.h:245-282</c>.
/// </summary>
/// <remarks>
/// Mutable <c>sealed class</c> (not a record): <c>diff_tform.c</c>'s
/// <c>find_similar</c> swaps <c>old_file</c>/<c>new_file</c> in place during
/// rename detection, and lazy binary-detection sets <see cref="Flags"/>. The
/// public surface is read-only; mutation is <c>internal</c> only. This mirrors
/// the C struct, which is mutated in place throughout the diff pipeline.
/// </remarks>
public sealed class GitDiffFile
{
    /// <summary>Object ID (zero OID for workdir entries before OID computation).</summary>
    public GitOid Id { get; internal set; }

    /// <summary> Slash-separated path relative to the repository root. May be null for a deleted/new side of a delta where libgit2 sets the path to NULL (e.g.
    /// the new side of a pure deletion, or the old side of a pure addition). Byte-faithful (<see cref="GitPath"/>); compared byte-wise
    /// end-to-end, matching libgit2's raw <c>path</c> pointer model. </summary>
    public GitPath? Path { get; internal set; }

    /// <summary>File size in bytes, or -1 when unknown.</summary>
    public long Size { get; internal set; } = -1;

    /// <summary>Canonical git file mode.</summary>
    public GitFileMode Mode { get; internal set; }

    /// <summary>Public per-file flags (binary / valid-id / exists / valid-size).</summary>
    public GitDiffFileFlags Flags { get; internal set; }

    /// <summary>Hex length used when abbreviating <see cref="Id"/> in output.</summary>
    public int IdAbbrev { get; internal set; }

    /// <summary>Internal bookkeeping flags (rename detection / lazy load).</summary>
    internal DiffInternalFlags InternalFlags { get; set; }

    /// <summary>Creates an empty diff file (zero OID, default mode).</summary>
    public GitDiffFile()
    {
    }

    /// <summary>Creates a diff file with the given core fields.</summary>
    public GitDiffFile(GitOid id, GitPath? path, long size, GitFileMode mode, GitDiffFileFlags flags = 0, int idAbbrev = 0)
    {
        Id = id;
        Path = path;
        Size = size;
        Mode = mode;
        Flags = flags;
        IdAbbrev = idAbbrev;
    }
}
