// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Binary diff contents for a single delta. Managed equivalent of
/// <c>git_diff_binary</c> (<c>include/git2/diff.h:553-565</c>).
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="ContainsData"/> is <c>false</c>, only the "Binary files differ"
/// noshow header is emitted. When <c>true</c>, the full base85-encoded body
/// (literal or delta) is available via <see cref="OldFile"/> and
/// <see cref="NewFile"/>.
/// </para>
/// <para>
/// Created by <c>create_binary</c> (<c>patch_generate.c:272-337</c>) when
/// <c>GIT_DIFF_SHOW_BINARY</c> is set and the delta is binary.
/// </para>
/// </remarks>
public sealed class GitBinaryPatch
{
    /// <summary>
    /// Whether actual binary content is included. If <c>false</c>, this was
    /// produced knowing only that a binary file changed (noshow path).
    /// </summary>
    public bool ContainsData { get; init; }

    /// <summary>The old→new direction (stored as the "new" side of the patch).</summary>
    public GitBinaryFile NewFile { get; init; } = new();

    /// <summary>The new→old direction (stored as the "old" side of the patch).</summary>
    public GitBinaryFile OldFile { get; init; } = new();
}
