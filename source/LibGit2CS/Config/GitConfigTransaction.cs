// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Config;

/// <summary>
/// An atomic config read-modify-write transaction. Locks the writable file
/// backend on construction (matches <c>git_config_lock</c>); commits or rolls
/// back on <see cref="Dispose"/>. Use via <see cref="GitConfiguration.LockAsync"/>
/// + a <c>using</c> block.
/// </summary>
/// <remarks>
/// <para>
/// While locked, <c>GitConfiguration.Set*</c>/<c>GitConfiguration.Delete*</c>
/// operate on the frozen in-memory content (no file writes until commit). On
/// commit, the locked content is written to the <c>.lock</c> temp file and
/// atomically renamed over the real config.
/// </para>
/// <para>
/// Matches libgit2's <c>git_transaction</c> with <c>TRANSACTION_CONFIG</c>.
/// </para>
/// </remarks>
public sealed class GitConfigTransaction : IDisposable
{
    private readonly FileConfigBackend _backend;
    private bool _committed;
    private bool _disposed;

    internal GitConfigTransaction(FileConfigBackend backend)
    {
        _backend = backend;
    }

    /// <summary>
    /// Commits the transaction: writes the locked content and unlocks.
    /// After commit, further writes go through the normal path.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_committed)
        {
            return;
        }

        await _backend.UnlockAsync(commit: true, cancellationToken).ConfigureAwait(false);
        _committed = true;
    }

    /// <summary>
    /// Rolls back the transaction (discards the lock without writing).
    /// Synchronous: the rollback path only deletes the <c>.lock</c> temp file
    /// (a metadata op). Called automatically on
    /// <see cref="Dispose"/> if <see cref="CommitAsync"/> was not called.
    /// Matches <c>git_config_transaction_rollback</c>.
    /// </summary>
    public void Rollback()
    {
        if (_disposed)
        {
            return;
        }

        if (!_committed)
        {
            _backend.DiscardLock();
        }

        _committed = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (!_committed)
            {
                _backend.DiscardLock();
            }
        }
    }
}
