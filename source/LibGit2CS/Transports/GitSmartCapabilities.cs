// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Transports;

/// <summary>
/// Capability flags negotiated during the git smart protocol ref advertisement.
/// Corresponds to the bitfield members of <c>transport_smart_caps</c> in
/// <c>src/libgit2/transports/smart.h</c>.
/// </summary>
/// <remarks>
/// Parsed from the NUL-delimited capability string on the first ref packet:
/// <c>&lt;oid&gt; HEAD\0cap1 cap2 cap3</c>. The <c>symref</c> capability is NOT a
/// flag here — it is parsed into a separate symref vector by
/// <see cref="GitSmartProtocol.DetectCapabilities"/>. The <c>object-format</c> and
/// <c>agent</c> capabilities carry string values, not boolean flags; see
/// <see cref="GitSmartCapabilitySet.ObjectFormat"/> and <see cref="GitSmartCapabilitySet.Agent"/>.
/// </remarks>
[Flags]
public enum GitSmartCapabilities
{
    /// <summary>No capabilities parsed yet.</summary>
    None = 0,

    /// <summary>Server supports OFS_DELTA pack entries. (<c>ofs-delta</c>)</summary>
    OfsDelta = 1 << 0,

    /// <summary>Server supports multi-ACK negotiation (basic). (<c>multi_ack</c>)</summary>
    MultiAck = 1 << 1,

    /// <summary>Server supports multi-ACK with status detail. (<c>multi_ack_detailed</c>)</summary>
    MultiAckDetailed = 1 << 2,

    /// <summary>Server supports side-band demux (1000-byte packets). (<c>side-band</c>)</summary>
    SideBand = 1 << 3,

    /// <summary>Server supports side-band demux (65520-byte packets). (<c>side-band-64k</c>)</summary>
    SideBand64k = 1 << 4,

    /// <summary>Server will include tags reachable from sent objects. (<c>include-tag</c>)</summary>
    IncludeTag = 1 << 5,

    /// <summary>Server supports ref deletion. (<c>delete-refs</c>)</summary>
    DeleteRefs = 1 << 6,

    /// <summary>Server sends push report-status. (<c>report-status</c>)</summary>
    ReportStatus = 1 << 7,

    /// <summary>Server can receive thin packs. (<c>thin-pack</c>)</summary>
    ThinPack = 1 << 8,

    /// <summary>Server allows tip SHA-1 in want lines. (<c>allow-tip-sha1-in-want</c>)</summary>
    WantTipSha1 = 1 << 9,

    /// <summary>Server allows reachable SHA-1 in want lines. (<c>allow-reachable-sha1-in-want</c>)</summary>
    WantReachableSha1 = 1 << 10,

    /// <summary>Server supports shallow clone / unshallow. (<c>shallow</c>)</summary>
    Shallow = 1 << 11,

    /// <summary>Server supports push-options. (<c>push-options</c>)</summary>
    PushOptions = 1 << 12,
}
