// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Transports;

/// <summary>
/// ACK status from the server during multi_ack / multi_ack_detailed negotiation.
/// Maps to <c>enum git_ack_status</c> in <c>smart.h</c>.
/// </summary>
public enum GitAckStatus
{
    /// <summary>No status (plain ACK with no suffix).</summary>
    None = 0,

    /// <summary>"ACK &lt;oid&gt; continue" — more ACKs coming.</summary>
    Continue = 1,

    /// <summary>"ACK &lt;oid&gt; common" — this commit is common.</summary>
    Common = 2,

    /// <summary>"ACK &lt;oid&gt; ready" — server has enough, close negotiation.</summary>
    Ready = 3,
}
