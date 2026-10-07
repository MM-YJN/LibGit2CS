// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Transports;

/// <summary>
/// Smart protocol service identifier. Maps to <c>git_smart_service_t</c>
/// in <c>include/git2/sys/transport.h</c>.
/// </summary>
public enum GitSmartService
{
    /// <summary>Fetch: ref advertisement (GET /info/refs?service=git-upload-pack).</summary>
    UploadPackLs = 1,

    /// <summary>Fetch: negotiation + pack download (POST /git-upload-pack).</summary>
    UploadPack = 2,

    /// <summary>Push: ref advertisement (GET /info/refs?service=git-receive-pack).</summary>
    ReceivePackLs = 3,

    /// <summary>Push: pack upload (POST /git-receive-pack).</summary>
    ReceivePack = 4,
}
