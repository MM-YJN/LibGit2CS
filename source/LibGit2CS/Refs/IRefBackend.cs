// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Refs;

/// <summary>
/// Read-side contract for a reference database backend. Managed equivalent of
/// the read methods on libgit2's <c>git_refdb_backend</c> vtable
/// (<c>include/git2/sys/refdb_backend.h</c>).
/// </summary>
/// <remarks>
/// <para>
/// libgit2's <c>git_refdb_backend</c> is a vtable with read methods
/// (<c>exists</c>, <c>lookup</c>, <c>iterator</c>, <c>has_log</c>,
/// <c>reflog_read</c>, <c>free</c>) plus write-side methods (<c>write</c>,
/// <c>rename</c>, <c>del</c>, <c>lock</c>, <c>unlock</c>, <c>ensure_log</c>,
/// <c>reflog_write</c>, <c>reflog_rename</c>, <c>reflog_delete</c>). This
/// interface unifies both halves (single-backend model, matching C's
/// <c>git_refdb_set_backend</c>); read-only backends throw
/// <see cref="NotSupportedException"/> from write methods.
/// </para>
/// <para>
/// <b>Internal</b> — backends are implementation details. <see cref="GitReferences"/>
/// is the public API; it delegates through <see cref="RefDatabase"/> to the
/// registered backend. The C <c>version</c> field and <c>git_refdb_init_backend</c>
/// validator are dropped (managed ABI).
/// </para>
/// <para>
/// Backends create <see cref="GitReference"/> instances with <see cref="GitReference.Owner"/>
/// set to <c>null</c>; <see cref="RefDatabase"/>/<see cref="GitReferences"/> injects
/// the owning <see cref="LibGit2CS.Repository.GitRepository"/> via <c>with</c> expressions before returning
/// to callers.
/// </para>
/// <para>
/// All IO-performing methods are async. Stat-only probes
/// (<see cref="HasLogAsync"/>) are sync-completing <see cref="ValueTask{TResult}"/>
/// — the body is <c>ValueTask.FromResult(...)</c> over a metadata call, so no
/// threadpool blocking occurs. <see cref="IRefLock"/> stays <see cref="IDisposable"/>
/// because its <c>Commit</c>/<c>Dispose</c> paths only do metadata ops
/// (<c>File.Move</c>/<c>File.Delete</c>).
/// </para>
/// </remarks>
internal interface IRefBackend : IAsyncDisposable
{
    // ── Read side ──────────────────────────────────────────────────────

    /// <summary> Updates the OID type used to parse ref files. Default no-op for backends that read the type from their repository. </summary>
    void SetOidType(GitHashAlgorithmKind oidType) { }

    /// <summary>
    /// Looks up a single reference by fully-qualified name. Returns the reference
    /// as stored (no symbolic chain resolution). Matches <c>git_refdb_backend::lookup</c>.
    /// </summary>
    /// <param name="refName">The fully-qualified reference name (e.g. <c>refs/heads/master</c>, <c>HEAD</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reference, or <c>null</c> if not found.</returns>
    Task<GitReference?> LookupAsync(RefNameKey refName, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether a reference with the given name exists. Matches
    /// <c>git_refdb_backend::exists</c>.
    /// </summary>
    Task<bool> ExistsAsync(RefNameKey refName, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates all references in this backend, optionally filtered by glob.
    /// Loose refs shadow packed refs with the same name (loose wins). Matches
    /// <c>git_refdb_backend::iterator</c>.
    /// </summary>
    /// <param name="glob">Optional glob pattern (wildmatch with <c>WM_PATHNAME</c>). Null/empty returns all.</param>
    /// <param name="cancellationToken">Cancellation token, propagated by <c>await foreach</c>.</param>
    IAsyncEnumerable<GitReference> EnumerateAsync(RefNameKey? glob, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates all reference names, optionally filtered by glob. More efficient
    /// than <see cref="EnumerateAsync"/> when only names are needed. Matches
    /// <c>git_refdb_backend::iterator</c> (next_name variant).
    /// </summary>
    IAsyncEnumerable<RefNameKey> EnumerateNamesAsync(RefNameKey? glob, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether a reflog file exists for the given reference. Matches
    /// <c>git_refdb_backend::has_log</c>. Sync-completing: body is
    /// <c>ValueTask.FromResult(File.Exists(...))</c> (stat-only).
    /// </summary>
    ValueTask<bool> HasLogAsync(RefNameKey refName, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the reflog for the given reference. Returns null if no reflog file
    /// exists; returns an empty <see cref="GitRefLog"/> if the file exists but is empty.
    /// Matches <c>git_refdb_backend::reflog_read</c>.
    /// </summary>
    Task<GitRefLog?> ReadLogAsync(RefNameKey refName, CancellationToken cancellationToken);

    // ── Write side ──────────────────────────────────────────────────────

    /// <summary>
    /// Locks a reference for modification by creating a <c>&lt;refname&gt;.lock</c>
    /// temp file. Returns a lock handle that must be passed to <see cref="UnlockAsync"/>
    /// or <see cref="WriteAsync"/>'s implicit commit. Matches <c>git_refdb_backend::lock</c>
    /// (refdb_fs.c <c>refdb_fs_backend__lock</c>). Synchronous: the <c>.lock</c>
    /// creation is an O_EXCL metadata probe, never yields.
    /// </summary>
    /// <returns>An opaque lock handle (the <c>.lock</c> file path).</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Exists"/> if the ref is already locked.
    /// <see cref="GitErrorCode.InvalidSpec"/> if <paramref name="refName"/> is invalid.
    /// </exception>
    IRefLock Lock(RefNameKey refName);

    /// <summary>
    /// Writes a reference into the loose ref file identified by
    /// <paramref name="lockHandle"/>, appending a reflog entry when appropriate, then
    /// atomically renames the <c>.lock</c> file over the real ref. Matches
    /// <c>git_refdb_backend::write</c> + <c>refdb_fs_backend__write_tail</c> +
    /// <c>loose_commit</c>.
    /// </summary>
    /// <param name="reference">The reference to write (direct or symbolic).</param>
    /// <param name="lockHandle">Lock handle from <see cref="Lock"/>.</param>
    /// <param name="updateReflog">If true, append a reflog entry (subject to <c>should_write_reflog</c>).</param>
    /// <param name="oldId">Expected old OID for direct refs (zero OID = no check). Used for atomic compare-and-set.</param>
    /// <param name="oldTarget">Expected old target name for symbolic refs (null = no check).</param>
    /// <param name="committer">The signature for the reflog entry; null uses the repo default.</param>
    /// <param name="message">The reflog message; null/empty = no message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Modified"/> if <paramref name="oldId"/>/<paramref name="oldTarget"/> doesn't match the current value.
    /// </exception>
    Task WriteAsync(GitReference reference, IRefLock lockHandle, bool updateReflog, GitOid oldId, RefNameKey? oldTarget, GitSignature? committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a reference: removes the loose file (holding <paramref name="lockHandle"/>),
    /// deletes the reflog, and removes the entry from packed-refs if present.
    /// Matches <c>git_refdb_backend::del</c> + <c>refdb_fs_backend__delete_tail</c>.
    /// </summary>
    /// <param name="refName">The fully-qualified ref name to delete.</param>
    /// <param name="lockHandle">Lock handle from <see cref="Lock"/>.</param>
    /// <param name="oldId">Expected old OID for direct refs (zero OID = no check).</param>
    /// <param name="oldTarget">Expected old target name for symbolic refs (null = no check).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Modified"/> if <paramref name="oldId"/>/<paramref name="oldTarget"/> doesn't match.
    /// </exception>
    Task DeleteAsync(RefNameKey refName, IRefLock lockHandle, GitOid oldId, RefNameKey? oldTarget, CancellationToken cancellationToken);

    /// <summary>
    /// Renames a reference: writes the new ref content to <paramref name="lockHandle"/>'s
    /// <c>.lock</c> file, renames the reflog, removes the old loose ref, commits
    /// the <c>.lock</c> file. Matches <c>git_refdb_backend::rename</c>
    /// (<c>refdb_fs_backend__rename</c>).
    /// </summary>
    Task RenameAsync(IRefLock lockHandle, RefNameKey newRefName, GitOid newId, RefNameKey? newTarget, bool force, GitSignature? committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken);

    /// <summary>
    /// Unlocks a previously locked reference, discarding any pending changes.
    /// Matches <c>git_refdb_backend::unlock</c> with <c>success=false</c>.
    /// Sync-completing: body is a metadata <c>File.Delete</c> over the <c>.lock</c> path.
    /// </summary>
    ValueTask UnlockAsync(IRefLock lockHandle, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures a reflog file exists for the given reference (creates an empty file
    /// if <c>core.logallrefupdates</c> allows). Matches <c>git_refdb_backend::ensure_log</c>
    /// (<c>refdb_reflog_fs__ensure_log</c>).
    /// </summary>
    Task EnsureLogAsync(RefNameKey refName, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a full reflog (replaces the file). Matches
    /// <c>git_refdb_backend::reflog_write</c> (<c>refdb_reflog_fs__write</c>).
    /// </summary>
    Task ReflogWriteAsync(RefNameKey refName, GitRefLog reflog, CancellationToken cancellationToken);

    /// <summary>
    /// Appends a single reflog entry to the reflog file. Matches
    /// <c>reflog_append</c> (refdb_fs.c:2284-2375).
    /// </summary>
    Task ReflogAppendAsync(RefNameKey refName, GitOid oldId, GitOid newId, GitSignature committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken);

    /// <summary>
    /// Renames a reflog file (used by ref rename). Matches
    /// <c>git_refdb_backend::reflog_rename</c>.
    /// </summary>
    /// <returns><c>true</c> when the reflog was placed at the new name (or
    /// there was nothing to rename); <c>false</c> when the destination is a
    /// non-empty directory and the reflog is stranded at a temp path — C
    /// skips the reflog append in that case but still completes the ref
    /// rename.</returns>
    ValueTask<bool> ReflogRenameAsync(RefNameKey oldName, RefNameKey newName, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a reflog file. Matches <c>git_refdb_backend::reflog_delete</c>.
    /// </summary>
    ValueTask ReflogDeleteAsync(RefNameKey refName, CancellationToken cancellationToken);

    /// <summary>
    /// Compresses the ref store (packs loose refs into packed-refs).
    /// Matches the <c>compress</c> vtable slot / <c>git_refdb_compress</c>
    /// (refdb.c:92-100). Backends without a packed representation no-op.
    /// </summary>
    Task CompressAsync(CancellationToken cancellationToken);
}
