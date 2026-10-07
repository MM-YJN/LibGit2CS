// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.Refs;

/// <summary>
/// Reference database. Wraps a single <see cref="IRefBackend"/> and provides
/// symbolic chain resolution. Managed port of libgit2's <c>git_refdb</c>
/// (<c>src/libgit2/refdb.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="Objects.GitObjectDb"/> (which aggregates multiple backends),
/// <c>RefDatabase</c> holds exactly one backend — mirroring libgit2's
/// <c>git_refdb_set_backend</c> (there is no <c>git_refdb_add_backend</c>).
/// </para>
/// <para>
/// <b>Symbolic chain resolution</b> lives here (not in the backend), matching
/// <c>git_refdb_resolve</c> (<c>refdb.c:147-198</c>). The chain walk caps at
/// <see cref="MaxNestingLevel"/> (10) to prevent infinite loops on cyclic symrefs.
/// </para>
/// <para>
/// <b>Write side:</b> delegates to <see cref="IRefBackend.Lock"/>,
/// <see cref="IRefBackend.WriteAsync"/>, <see cref="IRefBackend.DeleteAsync"/>,
/// <see cref="IRefBackend.RenameAsync"/>, <see cref="IRefBackend.UnlockAsync"/>,
/// reflog methods, and <see cref="BeginTransaction"/>.
/// </para>
/// <para>
/// <b>Async IO:</b> every backend-delegating method is
/// async, threading a <see cref="CancellationToken"/> through. <see cref="GitOid"/>
/// is passed by value (not <c>in</c>) because C# forbids <c>in</c> parameters
/// on async methods.
/// </para>
/// </remarks>
internal sealed class RefDatabase : IAsyncDisposable
{
    /// <summary>
    /// Default nesting depth for symbolic chain resolution. Used when
    /// <c>maxNesting &lt; 0</c> is requested. Matches <c>DEFAULT_NESTING_LEVEL</c>
    /// (<c>refdb.c:20</c>).
    /// </summary>
    public const int DefaultNestingLevel = 5;

    /// <summary>
    /// Hard cap on symbolic chain depth. Matches <c>MAX_NESTING_LEVEL</c>
    /// (<c>refdb.c:21</c>).
    /// </summary>
    public const int MaxNestingLevel = 10;

    private IRefBackend? _backend;
    private bool _disposed;

    /// <summary>Sets the single backend. Matches <c>git_refdb_set_backend</c>.</summary>
    /// <remarks>
    /// The backend is owned by this <see cref="RefDatabase"/> and disposed via
    /// <see cref="DisposeAsync"/>. The single-backend model (see class remarks)
    /// means this is called exactly once during repository initialization, so
    /// there is no previous backend to dispose here.
    /// </remarks>
    internal void SetBackend(IRefBackend backend)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(backend);

        _backend = backend;
    }

    internal IRefBackend? Backend => _backend;

    /// <summary>
    /// Returns the configured backend, throwing if none has been set. Write/reflog
    /// operations require a backend (set once via <see cref="SetBackend"/> during
    /// repository initialization).
    /// </summary>
    private IRefBackend RequiredBackend
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _backend ?? throw new InvalidOperationException("no ref backend configured");
        }
    }

    // ── Read side ───────────────────────────────────────────────────────

    /// <summary>
    /// Looks up a reference by name without resolving symbolic chains.
    /// Matches <c>git_refdb_lookup</c> (<c>refdb.c:126</c>).
    /// </summary>
    internal Task<GitReference?> LookupAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend is { } b ? b.LookupAsync(refName, cancellationToken) : Task.FromResult<GitReference?>(null);
    }

    /// <summary>
    /// Looks up a reference and walks the symbolic chain up to
    /// <paramref name="maxNesting"/> levels. Matches <c>git_refdb_resolve</c>
    /// (<c>refdb.c:147-198</c>).
    /// </summary>
    /// <param name="refName">The fully-qualified reference name.</param>
    /// <param name="maxNesting">
    /// Maximum chain depth. <c>0</c> = no resolution (return as-is).
    /// <c>&lt; 0</c> = use <see cref="DefaultNestingLevel"/> (5).
    /// Values <c>&gt; MaxNestingLevel</c> are clamped to <see cref="MaxNestingLevel"/> (10).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The resolved reference (always a <see cref="GitDirectReference"/> when
    /// <paramref name="maxNesting"/> != 0 and the chain resolves). If the chain
    /// ends at a dangling symbolic ref and <paramref name="maxNesting"/> != 0,
    /// returns null (NotFound semantics).
    /// </returns>
    internal async Task<GitReference?> ResolveAsync(RefNameKey refName, int maxNesting, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (maxNesting > MaxNestingLevel)
        {
            maxNesting = MaxNestingLevel;
        }
        else if (maxNesting < 0)
        {
            maxNesting = DefaultNestingLevel;
        }

        GitReference? current = await LookupAsync(refName, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        for (int depth = 0; depth < maxNesting; depth++)
        {
            if (!current.IsSymbolic)
            {
                break;
            }

            RefNameKey targetName = ((GitSymbolicReference)current).TargetNameKey;
            GitReference? resolved = await LookupAsync(targetName, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                // Dangling symbolic ref: target doesn't exist.
                // If maxNesting != 0, callers expect this as NotFound.
                return maxNesting != 0 ? null : current;
            }

            current = resolved;
        }

        // If we exceeded the nesting limit and are still symbolic, that's an
        // error — C (refdb.c:186-190) returns -1 (GIT_ERROR), not EAMBIGUOUS.
        if (maxNesting != 0 && current.IsSymbolic)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"cannot resolve reference (>{maxNesting} levels deep)",
                GitErrorCategory.Reference);
        }

        return current;
    }

    /// <summary>Checks if a reference exists. Matches <c>git_refdb_exists</c>.</summary>
    internal async Task<bool> ExistsAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend is { } b && await b.ExistsAsync(refName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enumerates all references, optionally filtered by glob.</summary>
    internal IAsyncEnumerable<GitReference> EnumerateAsync(RefNameKey? glob, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend?.EnumerateAsync(glob, cancellationToken) ?? EmptyAsyncEnumerableAsync<GitReference>(cancellationToken);
    }

    /// <summary>Enumerates all reference names, optionally filtered by glob.</summary>
    internal IAsyncEnumerable<RefNameKey> EnumerateNamesAsync(RefNameKey? glob, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend?.EnumerateNamesAsync(glob, cancellationToken) ?? EmptyAsyncEnumerableAsync<RefNameKey>(cancellationToken);
    }

    /// <summary>Checks if a reflog exists. Matches <c>git_refdb_has_log</c>.</summary>
    internal ValueTask<bool> HasLogAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend is { } b ? b.HasLogAsync(refName, cancellationToken) : ValueTask.FromResult(false);
    }

    /// <summary>Reads the reflog. Returns null if no reflog file exists.</summary>
    internal Task<GitRefLog?> ReadLogAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend is { } b ? b.ReadLogAsync(refName, cancellationToken) : Task.FromResult<GitRefLog?>(null);
    }

    // ── Write side ──────────────────────────────────────────────────────

    /// <summary>Locks a reference for modification. Matches <c>git_refdb_lock</c>.
    /// Synchronous: the lock is an O_EXCL metadata probe that never yields.</summary>
    internal IRefLock Lock(RefNameKey refName)
    {
        return RequiredBackend.Lock(refName);
    }

    /// <summary>Writes a reference under an existing lock. Matches <c>git_refdb_backend::write</c>.</summary>
    internal Task WriteAsync(GitReference reference, IRefLock lockHandle, bool updateReflog, GitOid oldId, RefNameKey? oldTarget, GitSignature? committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequiredBackend.WriteAsync(reference, lockHandle, updateReflog, oldId, oldTarget, committer, message, cancellationToken);
    }

    /// <summary>Deletes a reference under an existing lock. Matches <c>git_refdb_backend::del</c>.</summary>
    internal Task DeleteAsync(RefNameKey refName, IRefLock lockHandle, GitOid oldId, RefNameKey? oldTarget, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequiredBackend.DeleteAsync(refName, lockHandle, oldId, oldTarget, cancellationToken);
    }

    /// <summary>Renames a reference under an existing lock. Matches <c>git_refdb_backend::rename</c>.</summary>
    internal Task RenameAsync(IRefLock lockHandle, RefNameKey newRefName, GitOid newId, RefNameKey? newTarget, bool force, GitSignature? committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequiredBackend.RenameAsync(lockHandle, newRefName, newId, newTarget, force, committer, message, cancellationToken);
    }

    /// <summary>Unlocks a locked reference, discarding changes. Matches <c>git_refdb_unlock(success=false)</c>.</summary>
    internal ValueTask UnlockAsync(IRefLock lockHandle, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequiredBackend.UnlockAsync(lockHandle, cancellationToken);
    }

    /// <summary>Ensures a reflog file exists. Matches <c>git_refdb_backend::ensure_log</c>.</summary>
    internal Task EnsureLogAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequiredBackend.EnsureLogAsync(refName, cancellationToken);
    }

    /// <summary>Writes a full reflog. Matches <c>git_refdb_backend::reflog_write</c>.</summary>
    internal Task ReflogWriteAsync(RefNameKey refName, GitRefLog reflog, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequiredBackend.ReflogWriteAsync(refName, reflog, cancellationToken);
    }

    /// <summary>Appends a reflog entry. Matches <c>reflog_append</c>.</summary>
    internal Task ReflogAppendAsync(RefNameKey refName, GitOid oldId, GitOid newId, GitSignature committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequiredBackend.ReflogAppendAsync(refName, oldId, newId, committer, message, cancellationToken);
    }

    /// <summary>Renames a reflog. Matches <c>git_refdb_backend::reflog_rename</c>.</summary>
    internal ValueTask<bool> ReflogRenameAsync(RefNameKey oldName, RefNameKey newName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequiredBackend.ReflogRenameAsync(oldName, newName, cancellationToken);
    }

    /// <summary>Deletes a reflog. Matches <c>git_refdb_backend::reflog_delete</c>.</summary>
    internal ValueTask ReflogDeleteAsync(RefNameKey refName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequiredBackend.ReflogDeleteAsync(refName, cancellationToken);
    }

    /// <summary>Compresses the ref store. Matches <c>git_refdb_compress</c>
    /// (refdb.c:92-100).</summary>
    internal Task CompressAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend?.CompressAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>Begin a multi-ref atomic transaction. Matches <c>git_transaction_new</c>.</summary>
    internal GitTransaction BeginTransaction(GitRepository repo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new GitTransaction(repo, this);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_backend is { } backend)
        {
            await backend.DisposeAsync().ConfigureAwait(false);
        }

        _backend = null;
    }

    /// <summary>An empty <see cref="IAsyncEnumerable{T}"/> for the no-backend case.</summary>
    private static async IAsyncEnumerable<T> EmptyAsyncEnumerableAsync<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
