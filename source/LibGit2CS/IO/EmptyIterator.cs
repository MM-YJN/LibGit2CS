// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Index;

namespace LibGit2CS.IO;

/// <summary>
/// Trivial iterator that yields nothing. Managed port of the empty iterator
/// in <c>iterator.c</c> (lines 358-421).
/// </summary>
internal sealed class EmptyIterator : IteratorBase
{
    /// <summary>Creates an empty iterator.</summary>
    public EmptyIterator(IteratorOptions? options = null)
        : base(IteratorType.Empty, options ?? IteratorOptions.Default)
    {
    }

    /// <inheritdoc/>
    public override ValueTask<GitIndexEntry?> CurrentAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<GitIndexEntry?>(null);

    /// <inheritdoc/>
    public override Task<GitIndexEntry?> AdvanceAsync(CancellationToken cancellationToken)
        => Task.FromResult<GitIndexEntry?>(null);

    /// <inheritdoc/>
    public override Task<GitIndexEntry?> AdvanceIntoAsync(CancellationToken cancellationToken)
        => Task.FromResult<GitIndexEntry?>(null);

    /// <inheritdoc/>
    public override Task<(GitIndexEntry? Entry, IteratorStatus Status)> AdvanceOverAsync(CancellationToken cancellationToken)
        => Task.FromResult<(GitIndexEntry?, IteratorStatus)>((null, IteratorStatus.Empty));

    /// <inheritdoc/>
    public override ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        Clear();
        return ValueTask.CompletedTask;
    }
}
