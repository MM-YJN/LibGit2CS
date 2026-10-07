// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Transports;

/// <summary>
/// Packet type discriminator. Maps 1:1 to <c>git_pkt_type</c> in
/// <c>src/libgit2/transports/smart.h</c>.
/// </summary>
public enum GitPacketType
{
    /// <summary>git:// protocol command (host/path).</summary>
    Cmd = 0,

    /// <summary>Flush packet ("0000").</summary>
    Flush = 1,

    /// <summary>Reference advertisement: OID + name + optional capabilities.</summary>
    Ref = 2,

    /// <summary>Client "have" line (write-only, never parsed).</summary>
    Have = 3,

    /// <summary>Server ACK response: "ACK &lt;oid&gt; [status]".</summary>
    Ack = 4,

    /// <summary>Server NAK response.</summary>
    Nak = 5,

    /// <summary>Comment line starting with '#'. RPC only.</summary>
    Comment = 6,

    /// <summary>Error: "ERR &lt;message&gt;".</summary>
    Err = 7,

    /// <summary>Side-band data (channel 0x01, pack file data).</summary>
    Data = 8,

    /// <summary>Side-band progress (channel 0x02).</summary>
    Progress = 9,

    /// <summary>Push status: "ok &lt;ref&gt;".</summary>
    Ok = 10,

    /// <summary>Push status: "ng &lt;ref&gt; &lt;msg&gt;".</summary>
    Ng = 11,

    /// <summary>Push status: "unpack ok" or "unpack &lt;error&gt;".</summary>
    Unpack = 12,

    /// <summary>Shallow clone marker: "shallow &lt;oid&gt;".</summary>
    Shallow = 13,

    /// <summary>Unshallow marker: "unshallow &lt;oid&gt;".</summary>
    Unshallow = 14,
}
