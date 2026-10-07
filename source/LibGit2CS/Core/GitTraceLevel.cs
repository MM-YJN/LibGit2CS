// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Trace severity levels. Matches libgit2's <c>git_trace_level_t</c>.
/// </summary>
public enum GitTraceLevel
{
    /// <summary>No tracing will be performed.</summary>
    None = 0,

    /// <summary>Severe errors that may impact the program's execution.</summary>
    Fatal = 1,

    /// <summary>Errors that do not impact the program's execution.</summary>
    Error = 2,

    /// <summary>Warnings that suggest abnormal data.</summary>
    Warn = 3,

    /// <summary>Informational messages about program execution.</summary>
    Info = 4,

    /// <summary>Detailed data that allows for debugging.</summary>
    Debug = 5,

    /// <summary>Exceptionally detailed debugging data.</summary>
    Trace = 6,
}
