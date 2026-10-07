// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Output format for a single <see cref="GitPatch.ToBufferAsync(CancellationToken)"/>.
/// </summary>
public enum GitPatchPrintFormat
{
    /// <summary>Unified diff format.</summary>
    Unified,

    /// <summary>Raw format.</summary>
    Raw,

    /// <summary>Summary format.</summary>
    Summary,
}
