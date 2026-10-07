// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.Refs;

/// <summary>
/// Atomic reference transaction. Queues multiple ref operations and applies them
/// atomically at commit time. Managed port of libgit2's <c>git_transaction</c>
/// (<c>src/libgit2/transaction.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Full implementation.</b> The queueing API (<see cref="LibGit2CS.Refs.GitTransaction.LockRef(LibGit2CS.Refs.RefNameKey)"/>,
/// <see cref="LibGit2CS.Refs.GitTransaction.SetTarget(LibGit2CS.Refs.RefNameKey, LibGit2CS.Core.GitOid, string?)"/>, <see cref="LibGit2CS.Refs.GitTransaction.SetSymbolicTarget(LibGit2CS.Refs.RefNameKey, LibGit2CS.Refs.RefNameKey, string?)"/>, <see cref="LibGit2CS.Refs.GitTransaction.Remove(LibGit2CS.Refs.RefNameKey)"/>,
/// <see cref="LibGit2CS.Refs.GitTransaction.SetReflog(LibGit2CS.Refs.RefNameKey, LibGit2CS.Refs.GitRefLog)"/>) is available, and <see cref="CommitAsync"/> performs a
/// 2-phase atomic commit: (1) apply all queued ref writes/deletes under their
/// locks, (2) unlock all. Any failure rolls back (unlocks without committing).
/// </para>
/// <para>
/// <b>Eager locking:</b> <see cref="LibGit2CS.Refs.GitTransaction.LockRef(LibGit2CS.Refs.RefNameKey)"/> creates the <c>.lock</c> temp file
/// immediately (matches <c>git_transaction_lock_ref</c>→<c>git_refdb_lock</c>),
/// so concurrent operations see the lock held for the duration of the transaction.
/// </para>
/// <para>
/// The only libgit2 caller of <c>git_transaction_new</c> in the write path is
/// <c>git_stash_drop</c>; the managed port uses a transaction for the same path
/// (<see cref="GitRepository.StashDropAsync"/>) and exposes
/// <see cref="GitRepository.NewReferenceTransaction"/> for callers that need
/// multi-ref atomicity.
/// </para>
/// <para>
/// <b>Async IO:</b> <see cref="LibGit2CS.Refs.GitTransaction.LockRef(LibGit2CS.Refs.RefNameKey)"/> and
/// <see cref="CommitAsync"/> are async (they perform backend IO). The queue
/// mutators (<see cref="LibGit2CS.Refs.GitTransaction.SetTarget(LibGit2CS.Refs.RefNameKey, LibGit2CS.Core.GitOid, string?)"/>, <see cref="LibGit2CS.Refs.GitTransaction.SetSymbolicTarget(LibGit2CS.Refs.RefNameKey, LibGit2CS.Refs.RefNameKey, string?)"/>,
/// <see cref="LibGit2CS.Refs.GitTransaction.Remove(LibGit2CS.Refs.RefNameKey)"/>, <see cref="LibGit2CS.Refs.GitTransaction.SetReflog(LibGit2CS.Refs.RefNameKey, LibGit2CS.Refs.GitRefLog)"/>) are sync — pure in-memory op
/// mutation. <see cref="GitOid"/> is passed by value on async methods (C#
/// forbids <c>in</c> on async methods); the sync queue mutator keeps <c>in</c>.
/// </para>
/// </remarks>
public sealed class GitTransaction : IAsyncDisposable
{
    private readonly GitRepository _repo;
    private readonly RefDatabase? _db;
    private readonly List<TransactionOp> _ops = [];
    private bool _disposed;
    private bool _committed;

    /// <summary>Creates a transaction for the given repository (legacy ctor for compat).</summary>
    internal GitTransaction(GitRepository repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        _repo = repo;
        _db = null; // will use _repo.Refs internal database lazily
    }

    /// <summary>Creates a transaction bound to a specific ref database.</summary>
    internal GitTransaction(GitRepository repo, RefDatabase db)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(db);
        _repo = repo;
        _db = db;
    }

    /// <summary>The repository this transaction operates on.</summary>
    public GitRepository Repository => _repo;

    private RefDatabase Db => _db ?? GetDbFromRepo();

    private static RefDatabase GetDbFromRepo()
    {
        // Access the internal RefDatabase via the GitReferences facade's backing field.
        // GitReferences wraps a RefDatabase; we reach it through the owner chain.
        // Since we can't access the private _db in GitReferences, use the backend directly.
        // The repo.Refs facade delegates to RefDatabase; we need the RefDatabase instance.
        // Workaround: lock/unlock go through GitReferences which delegates to its _db.
        // For BeginTransaction on RefDatabase, we pass the db directly.
        // For the legacy ctor (used by external callers), we use _repo.Refs which
        // delegates to RefDatabase methods — but those aren't on the public facade.
        // So: the legacy ctor is only used by code that calls via GitReferences.BeginTransaction.
        // To avoid this complexity, always use the (repo, db) ctor internally.
        throw new InvalidOperationException("transaction is not bound to a ref database");
    }

    /// <summary>
    /// Locks a reference for modification. Eagerly creates the <c>.lock</c> temp
    /// file. Matches <c>git_transaction_lock_ref</c>. Synchronous: the lock is an
    /// O_EXCL metadata probe that never yields.
    /// </summary>
    internal void LockRef(RefNameKey name)
    {
        ThrowIfDisposed();

        name = RefNameKey.From(name.Bytes.ToArray());
        IRefLock lockHandle = Db.Lock(name);
        _ops.Add(new TransactionOp(name, OpKind.Lock, lockHandle));
    }

    /// <summary>
    /// Queues a direct ref target update. The ref must have been locked first
    /// via <see cref="LibGit2CS.Refs.GitTransaction.LockRef(LibGit2CS.Refs.RefNameKey)"/>. Matches <c>git_transaction_set_target</c>.
    /// UTF-8 convenience — the byte-parity surface is
    /// <see cref="SetTarget(string, GitOid, ReadOnlyMemory{byte}?)"/>.
    /// </summary>
    internal void SetTarget(RefNameKey name, GitOid id, string? logMessage = null)
        => SetTarget(name, id, logMessage is null ? null : Encoding.UTF8.GetBytes(logMessage));

    /// <summary> Queues a direct ref target update with byte-faithful reflog message bytes. byte-primary surface. </summary>
    internal void SetTarget(RefNameKey name, GitOid id, ReadOnlyMemory<byte>? logMessageBytes)
    {
        ThrowIfDisposed();

        TransactionOp op = FindLockedOp(name) ?? throw new GitException(
            GitErrorCode.NotFound,
            $"the specified reference '{name}' is not locked",
            GitErrorCategory.Reference);

        op.Kind = OpKind.SetTarget;
        op.TargetId = id;
        op.LogMessage = logMessageBytes;
    }

    /// <summary>
    /// Queues a symbolic ref target update. The ref must have been locked first.
    /// Matches <c>git_transaction_set_symbolic_target</c>. UTF-8 convenience —
    /// the byte-parity surface is
    /// <see cref="SetSymbolicTarget(string, string, ReadOnlyMemory{byte}?)"/>.
    /// </summary>
    internal void SetSymbolicTarget(RefNameKey name, RefNameKey target, string? logMessage = null)
        => SetSymbolicTarget(name, target, logMessage is null ? null : Encoding.UTF8.GetBytes(logMessage));

    /// <summary> Queues a symbolic ref target update with byte-faithful reflog message bytes. byte-primary surface. </summary>
    internal void SetSymbolicTarget(RefNameKey name, RefNameKey target, ReadOnlyMemory<byte>? logMessageBytes)
    {
        ThrowIfDisposed();

        TransactionOp op = FindLockedOp(name) ?? throw new GitException(
            GitErrorCode.NotFound,
            $"the specified reference '{name}' is not locked",
            GitErrorCategory.Reference);

        op.Kind = OpKind.SetSymbolicTarget;
        op.SymbolicTarget = RefNameKey.From(target.Bytes.ToArray());
        op.LogMessage = logMessageBytes;
    }

    /// <summary>
    /// Queues a ref deletion. The ref must have been locked first. Matches
    /// <c>git_transaction_remove</c>.
    /// </summary>
    internal void Remove(RefNameKey name)
    {
        ThrowIfDisposed();

        TransactionOp op = FindLockedOp(name) ?? throw new GitException(
            GitErrorCode.NotFound,
            $"the specified reference '{name}' is not locked",
            GitErrorCategory.Reference);

        op.Kind = OpKind.Remove;
    }

    /// <summary>
    /// Queues a custom reflog to write for the given ref. The ref must have been
    /// locked first. Matches <c>git_transaction_set_reflog</c>.
    /// </summary>
    internal void SetReflog(RefNameKey name, GitRefLog reflog)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(reflog);
        TransactionOp op = FindLockedOp(name) ?? throw new GitException(
            GitErrorCode.NotFound,
            $"the specified reference '{name}' is not locked",
            GitErrorCategory.Reference);

        op.Reflog = reflog;
    }

    /// <summary>
    /// Atomically applies all queued operations. Phase 1: write all reflog
    /// entries + apply all ref writes/deletes under their locks. Phase 2: unlock
    /// all (committing). Any failure rolls back (unlocks without committing).
    /// Matches <c>git_transaction_commit</c> (transaction.c:333-368).
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_committed)
        {
            return;
        }

        var applied = new List<TransactionOp>();
        try
        {
            // Phase 1: write reflogs + apply ref writes/deletes.
            foreach (TransactionOp op in _ops)
            {
                if (op.Reflog is not null)
                {
                    await Db.ReflogWriteAsync(op.Name, op.Reflog, cancellationToken).ConfigureAwait(false);
                }

                if (op.Kind == OpKind.Lock)
                {
                    // Ref was locked but not modified — just unlock later.
                    continue;
                }

                if (op.Kind == OpKind.Remove)
                {
                    IRefLock? lockHandle = op.LockHandle;
                    Debug.Assert(lockHandle is not null, "LockHandle is set for non-Lock ops");
                    await Db.DeleteAsync(op.Name, lockHandle, oldId: default, oldTarget: null, cancellationToken).ConfigureAwait(false);
                }
                else if (op.Kind == OpKind.SetTarget)
                {
                    var reference = new GitDirectReference { NameKey = op.Name, Target = op.TargetId };
                    IRefLock? lockHandle = op.LockHandle;
                    Debug.Assert(lockHandle is not null, "LockHandle is set for non-Lock ops");
                    await Db.WriteAsync(reference, lockHandle, updateReflog: true, oldId: default, oldTarget: null, committer: null, message: op.LogMessage, cancellationToken).ConfigureAwait(false);
                }
                else if (op.Kind == OpKind.SetSymbolicTarget)
                {
                    RefNameKey? symbolicTarget = op.SymbolicTarget;
                    Debug.Assert(symbolicTarget is not null, "SymbolicTarget is set by SetSymbolicTarget");
                    var reference = new GitSymbolicReference { NameKey = op.Name, TargetNameKey = symbolicTarget.Value };
                    IRefLock? lockHandle = op.LockHandle;
                    Debug.Assert(lockHandle is not null, "LockHandle is set for non-Lock ops");
                    await Db.WriteAsync(reference, lockHandle, updateReflog: true, oldId: default, oldTarget: null, committer: null, message: op.LogMessage, cancellationToken).ConfigureAwait(false);
                }

                applied.Add(op);
                op.Committed = true;
            }

            // Phase 2: unlock all (the Write/Delete already committed the locks;
            // for pure locks, unlock discards).
            foreach (TransactionOp op in _ops)
            {
                if (op.Kind == OpKind.Lock && !op.Committed)
                {
                    IRefLock? lockHandle = op.LockHandle;
                    Debug.Assert(lockHandle is not null, "LockHandle is set for Lock ops");
                    await Db.UnlockAsync(lockHandle, cancellationToken).ConfigureAwait(false);
                    op.Committed = true;
                }
            }

            _committed = true;
        }
        catch
        {
            // Rollback: unlock any uncommitted locks (discard).
            foreach (TransactionOp op in _ops)
            {
                if (!op.Committed && op.LockHandle is not null)
                {
                    try
                    {
                        await Db.UnlockAsync(op.LockHandle, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort rollback.
                    }

                    op.Committed = true;
                }
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_committed)
        {
            // Rollback: unlock all uncommitted locks.
            foreach (TransactionOp op in _ops)
            {
                if (!op.Committed && op.LockHandle is not null)
                {
                    try
                    {
                        await Db.UnlockAsync(op.LockHandle, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort.
                    }
                }
            }
        }

        _ops.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Finds the op for a locked ref, or null if not locked.</summary>
    private TransactionOp? FindLockedOp(RefNameKey name)
    {
        foreach (TransactionOp op in _ops)
        {
            if (op.Name == name)
            {
                return op;
            }
        }

        return null;
    }

    private sealed class TransactionOp
    {
        public RefNameKey Name { get; }
        public OpKind Kind { get; set; }
        public IRefLock? LockHandle { get; }
        public GitOid TargetId { get; set; }
        public RefNameKey? SymbolicTarget { get; set; }
        public ReadOnlyMemory<byte>? LogMessage { get; set; }
        public GitRefLog? Reflog { get; set; }
        public bool Committed { get; set; }

        public TransactionOp(RefNameKey name, OpKind kind, IRefLock? lockHandle = null)
        {
            Name = name;
            Kind = kind;
            LockHandle = lockHandle;
        }
    }

    private enum OpKind
    {
        Lock,
        SetTarget,
        SetSymbolicTarget,
        Remove,
        SetReflog,
    }
    /// <summary>UTF-8 convenience reference operation.</summary>
    public void LockRef(string name)
        => LockRef((RefNameKey)name);

    /// <summary>Raw-byte reference operation.</summary>
    public void LockRef(ReadOnlyMemory<byte> name)
        => LockRef(RefNameKey.From(name));

    /// <summary>UTF-8 convenience reference operation.</summary>
    public void SetTarget(string name, GitOid id, string? logMessage = null)
        => SetTarget((RefNameKey)name, id, logMessage);

    /// <summary>Raw-byte reference operation.</summary>
    public void SetTarget(ReadOnlyMemory<byte> name, GitOid id, string? logMessage = null)
        => SetTarget(RefNameKey.From(name), id, logMessage);

    /// <summary>UTF-8 convenience reference operation.</summary>
    public void SetTarget(string name, GitOid id, ReadOnlyMemory<byte>? logMessageBytes)
        => SetTarget((RefNameKey)name, id, logMessageBytes);

    /// <summary>Raw-byte reference operation.</summary>
    public void SetTarget(ReadOnlyMemory<byte> name, GitOid id, ReadOnlyMemory<byte>? logMessageBytes)
        => SetTarget(RefNameKey.From(name), id, logMessageBytes);

    /// <summary>UTF-8 convenience reference operation.</summary>
    public void SetSymbolicTarget(string name, string target, string? logMessage = null)
        => SetSymbolicTarget((RefNameKey)name, (RefNameKey)target, logMessage);

    /// <summary>Raw-byte reference operation.</summary>
    public void SetSymbolicTarget(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> target, string? logMessage = null)
        => SetSymbolicTarget(RefNameKey.From(name), RefNameKey.From(target), logMessage);

    /// <summary>UTF-8 convenience reference operation.</summary>
    public void SetSymbolicTarget(string name, string target, ReadOnlyMemory<byte>? logMessageBytes)
        => SetSymbolicTarget((RefNameKey)name, (RefNameKey)target, logMessageBytes);

    /// <summary>Raw-byte reference operation.</summary>
    public void SetSymbolicTarget(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> target, ReadOnlyMemory<byte>? logMessageBytes)
        => SetSymbolicTarget(RefNameKey.From(name), RefNameKey.From(target), logMessageBytes);

    /// <summary>UTF-8 convenience reference operation.</summary>
    public void Remove(string name)
        => Remove((RefNameKey)name);

    /// <summary>Raw-byte reference operation.</summary>
    public void Remove(ReadOnlyMemory<byte> name)
        => Remove(RefNameKey.From(name));

    /// <summary>UTF-8 convenience reference operation.</summary>
    public void SetReflog(string name, GitRefLog reflog)
        => SetReflog((RefNameKey)name, reflog);

    /// <summary>Raw-byte reference operation.</summary>
    public void SetReflog(ReadOnlyMemory<byte> name, GitRefLog reflog)
        => SetReflog(RefNameKey.From(name), reflog);

}
