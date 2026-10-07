// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Transports;

/// <summary>
/// Parsing context carried between packet parse calls.
/// Maps to <c>git_pkt_parse_data</c> in <c>smart.h</c>.
/// </summary>
public struct GitPacketParseState
{
    /// <summary>
    /// The OID type (SHA-1 or SHA-256) expected in packet payloads.
    /// Set from the first ref packet's capabilities (object-format).
    /// A value of <c>0</c> means "not yet determined" — defaults to SHA-1.
    /// </summary>
    public GitHashAlgorithmKind OidType { get; set; }

    /// <summary>
    /// Whether capabilities have already been parsed from the first ref
    /// packet. Once true, subsequent ref packets must not carry caps.
    /// </summary>
    public bool SeenCapabilities { get; set; }
}
