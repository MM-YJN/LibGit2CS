// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Transports;

/// <summary>
/// Stream state machine. Ported from <c>http_state</c> in
/// <c>src/libgit2/transports/http.c:27-32</c>.
/// </summary>
internal enum HttpStreamState
{
    /// <summary>No request sent yet.</summary>
    None,

    /// <summary>Request is being sent (POST body may still be streaming).</summary>
    SendingRequest,

    /// <summary>Response headers received; reading body.</summary>
    ReceivingResponse,

    /// <summary>Stream is complete.</summary>
    Done,
}
