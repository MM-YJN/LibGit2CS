// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core.Hashing;

/// <summary>
/// Identifies the hash algorithm used to produce an object ID.
/// Corresponds to <c>git_oid_t</c> in libgit2.
/// Values match libgit2's git_oid_t (Sha1=1, Sha256=2); no None sentinel
/// </summary>
public enum GitHashAlgorithmKind
{
    /// <summary>
    /// SHA-1 (20 raw bytes / 40 hex chars). libgit2's default.
    /// Maps to <c>GIT_OID_SHA1 = 1</c>.
    /// </summary>
    Sha1 = 1,

    /// <summary>
    /// SHA-256 (32 raw bytes / 64 hex chars).
    /// Maps to <c>GIT_OID_SHA256 = 2</c>.
    /// </summary>
    Sha256 = 2,
}
