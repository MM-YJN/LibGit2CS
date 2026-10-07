// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Transports;

/// <summary>
/// Definition for creating a smart subtransport. Maps to
/// <c>git_smart_subtransport_definition</c> in <c>include/git2/sys/transport.h</c>.
/// </summary>
/// <param name="Factory">Factory function that creates the subtransport, receiving the owning <see cref="GitContext"/>.</param>
/// <param name="IsRpc">Whether this is an RPC (stateless) transport (HTTP) vs stateful (git://, SSH).</param>
/// <param name="Param">Optional user-specified parameter passed to the factory.</param>
internal sealed record SubtransportDefinition(
    Func<GitContext, IGitSubtransport> Factory,
    bool IsRpc,
    object? Param);
