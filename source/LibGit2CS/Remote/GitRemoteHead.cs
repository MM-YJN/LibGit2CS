// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Remote;

/// <summary>
/// A reference as advertised by a remote during the ref advertisement phase
/// of the git smart protocol. Managed equivalent of libgit2's
/// <c>git_remote_head</c> in <c>include/git2/net.h</c>.
/// </summary>
/// <param name="Local">Whether this ref is already available locally (the OID is known in the local ODB).</param>
/// <param name="Oid">The OID of the ref as advertised by the remote.</param>
/// <param name="LocalOid">The local OID for this ref (valid when <paramref name="Local"/> is <c>true</c>).</param>
/// <param name="Name">The ref name, e.g. <c>refs/heads/main</c> or <c>HEAD</c>.</param>
/// <param name="SymrefTarget">The target ref name if this is a symbolic ref (e.g. <c>refs/heads/main</c> for <c>HEAD</c>), or <c>null</c>.</param>
public sealed record GitRemoteHead(
    bool Local,
    GitOid Oid,
    GitOid LocalOid,
    string Name,
    string? SymrefTarget);
