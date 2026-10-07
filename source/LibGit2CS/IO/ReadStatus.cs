// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.IO;

/// <summary>
/// Status of an <see cref="IObjectReader.ReadAsync"/> call. Maps the C
/// <c>GIT_ENOTFOUND</c> / <c>GIT_READER_MISMATCH</c> sentinels
/// (<c>reader.h:13</c>) to an explicit enum value (AOT-clean, no exception
/// in the hot path).
/// </summary>
internal enum ReadStatus
{
    /// <summary>The file was found and read successfully.</summary>
    Found,

    /// <summary>
    /// The file does not exist in this source. Maps to <c>GIT_ENOTFOUND</c>.
    /// </summary>
    NotFound,

    /// <summary>
    /// The workdir file does not match the index entry (mode or OID
    /// differs). Maps to <c>GIT_READER_MISMATCH</c> (<c>reader.h:13</c>).
    /// Only produced by <see cref="WorkdirReader"/> when index validation
    /// is enabled.
    /// </summary>
    Mismatch,
}
