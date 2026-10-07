// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// One side of a binary diff. Managed equivalent of
/// <c>git_diff_binary_file</c> (<c>include/git2/diff.h:530-542</c>).
/// </summary>
public sealed class GitBinaryFile
{
    /// <summary>The type of binary data (literal vs delta).</summary>
    public GitBinaryPatchType Type { get; init; }

    /// <summary>The deflated (zlib-compressed) binary data.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>The inflated (uncompressed) length.</summary>
    public long InflatedLength { get; init; }
}
