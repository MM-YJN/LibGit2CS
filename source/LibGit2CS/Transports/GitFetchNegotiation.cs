// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Remote;

namespace LibGit2CS.Transports;

/// <summary>
/// Fetch negotiation parameters passed to <see cref="IGitTransport.NegotiateFetchAsync"/>.
/// Managed equivalent of <c>git_fetch_negotiation</c> in
/// <c>include/git2/sys/transport.h</c>.
/// </summary>
public sealed record GitFetchNegotiation(
    IReadOnlyList<GitRemoteHead> Refs,
    IReadOnlyList<GitOid> ShallowRoots,
    int Depth);
