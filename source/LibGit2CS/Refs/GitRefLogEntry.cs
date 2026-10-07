// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Refs;

/// <summary> A single reflog entry: who changed a reference, when, from what OID to what OID, and why. Managed equivalent of libgit2's <c>git_reflog_entry</c>
/// (<c>src/libgit2/reflog.h:19-26</c>). </summary>
/// <param name="Committer">Who made the change.</param>
/// <param name="OldId">The OID before the change.</param>
/// <param name="NewId">The OID after the change.</param>
/// <param name="Message">The reflog message (e.g. <c>"commit: fix bug"</c>). Empty string if none.</param>
/// <param name="MessageBytes">The raw reflog message bytes.</param>
public readonly record struct GitRefLogEntry(
    GitSignature Committer,
    GitOid OldId,
    GitOid NewId,
    string Message,
    ReadOnlyMemory<byte> MessageBytes = default)
{
    /// <summary>The hash algorithm of the OIDs. Derived from <see cref="OldId"/> (or <see cref="NewId"/> if OldId is zero).</summary>
    public GitHashAlgorithmKind Algorithm => OldId.IsZero ? NewId.Algorithm : OldId.Algorithm;
}
