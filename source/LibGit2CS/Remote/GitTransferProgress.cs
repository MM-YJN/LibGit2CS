// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Progress information for a transfer (fetch/push). Maps to
/// <c>git_indexer_progress</c> in <c>include/git2/indexer.h</c>.
/// </summary>
public sealed record GitTransferProgress(
    int TotalObjects,
    int IndexedObjects,
    int ReceivedObjects,
    int LocalObjects,
    int TotalDeltas,
    int IndexedDeltas,
    long ReceivedBytes);
