// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Remote;

namespace LibGit2CS.Transports;

/// <summary>
/// Base type for all parsed pkt-line packets. Maps to the tagged-union
/// <c>git_pkt</c> family in <c>src/libgit2/transports/smart.h</c>.
/// </summary>
public abstract record GitPacket(GitPacketType Type);

/// <summary>Flush packet ("0000") — end of a message sequence.</summary>
public sealed record GitFlushPacket() : GitPacket(GitPacketType.Flush);

/// <summary>NAK packet — server has no common ancestors.</summary>
public sealed record GitNakPacket() : GitPacket(GitPacketType.Nak);

/// <summary>Comment packet — "# ...".</summary>
public sealed record GitCommentPacket(string Text) : GitPacket(GitPacketType.Comment);

/// <summary>Error packet — "ERR &lt;message&gt;".</summary>
public sealed record GitErrorPacket(string Message) : GitPacket(GitPacketType.Err);

/// <summary>Side-band data packet (channel 0x01 — pack file data).</summary>
public sealed record GitDataPacket(byte[] Data) : GitPacket(GitPacketType.Data);

/// <summary>Side-band progress packet (channel 0x02).</summary>
public sealed record GitProgressPacket(byte[] Data) : GitPacket(GitPacketType.Progress);

/// <summary>Reference advertisement packet.</summary>
/// <param name="Head">The advertised remote head.</param>
/// <param name="Capabilities">Capability string from the first ref (null for subsequent refs).</param>
public sealed record GitRefPacket(GitRemoteHead Head, string? Capabilities) : GitPacket(GitPacketType.Ref);

/// <summary>ACK packet from the server during negotiation.</summary>
public sealed record GitAckPacket(GitOid Oid, GitAckStatus Status) : GitPacket(GitPacketType.Ack);

/// <summary>Push status: "ok &lt;ref&gt;".</summary>
public sealed record GitOkPacket(string Ref) : GitPacket(GitPacketType.Ok);

/// <summary>Push status: "ng &lt;ref&gt; &lt;msg&gt;".</summary>
public sealed record GitNgPacket(string Ref, string Message) : GitPacket(GitPacketType.Ng);

/// <summary>Push status: "unpack ok" or "unpack &lt;error&gt;".</summary>
public sealed record GitUnpackPacket(bool UnpackOk) : GitPacket(GitPacketType.Unpack);

/// <summary>Shallow clone marker: "shallow &lt;oid&gt;".</summary>
public sealed record GitShallowPacket(GitOid Oid) : GitPacket(GitPacketType.Shallow);

/// <summary>Unshallow marker: "unshallow &lt;oid&gt;".</summary>
public sealed record GitUnshallowPacket(GitOid Oid) : GitPacket(GitPacketType.Unshallow);

/// <summary>git:// protocol command (host/path). Not used by smart protocol parsing.</summary>
public sealed record GitCmdPacket(string Command, string Path, string? Host) : GitPacket(GitPacketType.Cmd);

/// <summary>
/// Outcome category for <see cref="GitPacketReader.TryParse"/>. Distinguishes
/// the retryable underrun (<see cref="BufferTooShort"/>, meaning "read more
/// data and try again") from fatal parse failures. Mirrors the C distinction
/// between <c>GIT_EBUFS</c> (retry) and other negative return codes (fatal) in
/// <c>git_pkt_parse_line</c> (<c>smart_pkt.c</c>).
/// </summary>
public enum GitPacketParseError
{
    /// <summary>No error — a packet was parsed successfully.</summary>
    None,

    /// <summary>
    /// The buffer does not yet hold a complete pkt-line (truncated length
    /// prefix or truncated payload). The caller should refill the buffer and
    /// retry; this is an expected condition, not corruption.
    /// </summary>
    BufferTooShort,

    /// <summary>The length prefix is corrupt (invalid hex digit, out-of-range, or empty packet).</summary>
    InvalidLength,

    /// <summary>
    /// The length prefix contains a non-hex digit. Mirrors the C distinction
    /// between a <c>parse_len</c> failure (&lt;c&gt;smart_pkt.c&lt;/c&gt; "bad packet
    /// length" / "unexpected pack file") and the other length errors.
    /// </summary>
    InvalidHexLength,

    /// <summary>
    /// The packet is exactly the 4-byte length prefix ("0004") — an empty
    /// packet, which C rejects with "Invalid empty packet".
    /// </summary>
    InvalidEmptyPacket,

    /// <summary>The payload is corrupt (malformed ACK / REF / shallow / report-status / etc.).</summary>
    InvalidPayload,
}
