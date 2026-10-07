// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Refs;

/// <summary>
/// Read-only access to git references. Managed port of libgit2's
/// <c>git_reference</c> public API (<c>src/libgit2/refs.c</c> read side).
/// </summary>
/// <remarks>
/// <para>
/// Wraps a <see cref="RefDatabase"/> and provides name normalization, symbolic
/// chain resolution, enumeration, and reflog access. <see cref="LibGit2CS.Refs.GitReferences.LookupAsync(LibGit2CS.Refs.RefNameKey, System.Threading.CancellationToken)"/>
/// returns the reference as-is (no chain resolution); <see cref="LibGit2CS.Refs.GitReferences.ResolveAsync(LibGit2CS.Refs.RefNameKey, System.Threading.CancellationToken)"/>
/// walks the symbolic chain to a <see cref="GitDirectReference"/>.
/// </para>
/// <para>
/// <b>Write side</b> (<c>CreateAsync</c>/<c>DeleteAsync</c>/<c>RenameAsync</c>) delegates to
/// <see cref="RefDatabase"/> via the backend's write vtable.
/// </para>
/// <para>
/// <b>DWIM</b> (short-name resolution) is handled by revparse.
/// <see cref="LibGit2CS.Refs.GitReferences.LookupAsync(LibGit2CS.Refs.RefNameKey, System.Threading.CancellationToken)"/> requires a fully-qualified ref name. Short-name
/// resolution (e.g. <c>"master"</c> → <c>refs/heads/master</c>) is provided
/// by <see cref="Revwalk.GitRevParser"/>.
/// </para>
/// <para>
/// <b>Async IO:</b> every IO-performing method is async,
/// threading a <see cref="CancellationToken"/>. <see cref="GitOid"/> is passed
/// by value (not <c>in</c>) because C# forbids <c>in</c> on async methods.
/// Name-normalization/validation helpers (<see cref="LibGit2CS.Refs.GitReferences.NormalizeName(string, LibGit2CS.Refs.GitReferenceFormatFlags)"/>,
/// <see cref="LibGit2CS.Refs.GitReferences.IsNameValid(System.ReadOnlySpan{char}, LibGit2CS.Refs.GitReferenceFormatFlags)"/>, <see cref="Shorthand"/>) stay sync — pure
/// computation, no IO.
/// </para>
/// </remarks>
public sealed class GitReferences : IAsyncDisposable
{
    private readonly RefDatabase _db;
    private GitRepository? _repo;
    private bool _disposed;

    /// <summary>Creates a facade wrapping the given database.</summary>
    internal GitReferences(RefDatabase db) => _db = db;

    /// <summary>
    /// Sets the owning repository. Called once during <see cref="GitRepository"/>
    /// construction to resolve the chicken-and-egg between <see cref="GitRepository.Refs"/>
    /// and <see cref="GitReference.Owner"/>. Also wires the backend's owner for
    /// reflog policy/signature lookup.
    /// </summary>
    internal void SetOwner(GitRepository repo)
    {
        _repo = repo;
        if (_db.Backend is FileRefBackend fileBackend)
        {
            fileBackend.SetOwner(repo);
        }
    }

    /// <summary> Updates the OID type used by the ref backend to parse ref files. Called from <c>git_repository__set_objectformat</c>. </summary>
    internal void SetOidType(GitHashAlgorithmKind oidType)
    {
        _db.Backend?.SetOidType(oidType);
    }

    /// <summary>
    /// Looks up a reference by fully-qualified name. Does NOT resolve symbolic
    /// chains — returns the reference as-is. Matches <c>git_reference_lookup</c>
    /// (<c>refs.c:181</c>, max_nesting=0).
    /// </summary>
    /// <param name="name">Fully-qualified ref name (e.g. <c>refs/heads/master</c>, <c>HEAD</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reference, or null if not found.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.InvalidSpec"/> if <paramref name="name"/> is not a valid ref name.
    /// </exception>
    internal async Task<GitReference?> LookupAsync(RefNameKey name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RefNameKey normalized = NormalizeOrThrow(name);
        GitReference? raw = await _db.LookupAsync(normalized, cancellationToken).ConfigureAwait(false);
        return raw is null ? null : raw with { Owner = _repo };
    }

    /// <summary>
    /// Looks up a reference and walks the symbolic chain to a direct reference.
    /// Matches <c>git_reference_name_to_id</c> / <c>git_reference_resolve</c>
    /// (<c>refs.c:187</c>, max_nesting=-1 → 5).
    /// </summary>
    /// <param name="name">Fully-qualified ref name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved direct reference, or null if not found or the chain is dangling.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.InvalidSpec"/> if <paramref name="name"/> is invalid.
    /// <see cref="GitErrorCode.Ambiguous"/> if the symbolic chain exceeds the nesting limit.
    /// </exception>
    internal async Task<GitReference?> ResolveAsync(RefNameKey name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RefNameKey normalized = NormalizeOrThrow(name);
        GitReference? raw = await _db.ResolveAsync(normalized, maxNesting: -1, cancellationToken).ConfigureAwait(false);
        return raw is null ? null : raw with { Owner = _repo };
    }

    /// <summary>Compresses the ref store. Matches <c>git_refdb_compress</c>.</summary>
    internal Task CompressAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _db.CompressAsync(cancellationToken);
    }

    /// <summary>
    /// Enumerates all references, optionally filtered by glob. Matches
    /// <c>git_reference_foreach</c> / <c>git_reference_iterator_new</c>.
    /// </summary>
    /// <param name="glob">Optional glob pattern (<c>*</c> does not cross <c>/</c>). Null/empty returns all.</param>
    /// <param name="cancellationToken">Cancellation token, propagated by <c>await foreach</c>.</param>
    internal async IAsyncEnumerable<GitReference> ListAsync(RefNameKey? glob, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await foreach (GitReference raw in _db.EnumerateAsync(glob, cancellationToken).ConfigureAwait(false))
        {
            yield return raw with { Owner = _repo };
        }
    }

    /// <summary>
    /// Enumerates all reference names, optionally filtered by glob. Matches
    /// <c>git_reference_foreach_name</c> / <c>git_reference_list</c>.
    /// </summary>
    internal IAsyncEnumerable<RefNameKey> ListNameKeysAsync(string? glob = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _db.EnumerateNamesAsync(glob is null ? (RefNameKey?)null : (RefNameKey)glob, cancellationToken);
    }

    /// <summary>Display-only enumeration; internal identity operations use ListNameKeysAsync.</summary>
    internal async IAsyncEnumerable<string> ListNamesAsync(string? glob = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await foreach (RefNameKey name in _db.EnumerateNamesAsync(glob is null ? (RefNameKey?)null : (RefNameKey)glob, cancellationToken).ConfigureAwait(false))
        {
            yield return name.ToString();
        }
    }

    /// <summary>
    /// Checks whether a reflog file exists for the given reference. Matches
    /// <c>git_reference_has_log</c>.
    /// </summary>
    internal ValueTask<bool> HasLogAsync(RefNameKey refName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _db.HasLogAsync(refName, cancellationToken);
    }

    /// <summary>
    /// Reads the reflog for the given reference. Returns null if no reflog file
    /// exists. Matches <c>git_reflog_read</c>.
    /// </summary>
    internal Task<GitRefLog?> ReadLogAsync(RefNameKey refName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _db.ReadLogAsync(refName, cancellationToken);
    }

    // ── Write side ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a direct reference pointing at <paramref name="id"/>. If the ref
    /// already exists and <paramref name="force"/> is false, throws
    /// <see cref="GitErrorCode.Exists"/>. Matches <c>git_reference_create</c>
    /// (refs.c:487-496). Optionally writes a reflog entry with
    /// <paramref name="logMessage"/>.
    /// </summary>
    /// <returns>The created reference.</returns>
    internal Task<GitReference> CreateAsync(RefNameKey name, GitOid id, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => CreateAsync(name, id, force, logMessage is null ? null : Encoding.UTF8.GetBytes(logMessage), cancellationToken);

    /// <summary> Creates a direct reference pointing at <paramref name="id"/> with a byte-faithful reflog message. The byte-primary surface — C's
    /// <c>git_reference_create</c> carries the raw <c>char *</c> message bytes to the reflog serializer verbatim (refs.c:487-496, refdb_fs.c:2195); non-UTF-8
    /// message bytes round-trip byte-exact. The string overload above is the UTF-8 convenience tier. </summary>
    internal async Task<GitReference> CreateAsync(RefNameKey name, GitOid id, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RefNameKey normalized = NormalizeOrThrow(name);
        await EnsureTargetExistsAsync(id, cancellationToken).ConfigureAwait(false);

        // Check for existing ref unless force. C (reference_path_available,
        // refdb_fs.c:1112-1126) reports GIT_EEXISTS with this message.
        if (!force && await _db.LookupAsync(normalized, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new GitException(
                GitErrorCode.Exists,
                $"failed to write reference '{normalized}': a reference with that name already exists.",
                GitErrorCategory.Reference);
        }

        var reference = new GitDirectReference { NameKey = normalized, Target = id };
        IRefLock lockHandle = _db.Lock(normalized);
        try
        {
            await _db.WriteAsync(reference, lockHandle, updateReflog: true, oldId: default, oldTarget: null, committer: null, message: logMessageBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _db.UnlockAsync(lockHandle, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return reference with { Owner = _repo };
    }

    /// <summary>
    /// Creates a symbolic reference pointing at <paramref name="target"/>. If
    /// the ref already exists and <paramref name="force"/> is false, throws
    /// <see cref="GitErrorCode.Exists"/>. Matches <c>git_reference_symbolic_create</c>.
    /// </summary>
    internal Task<GitReference> CreateSymbolicAsync(RefNameKey name, RefNameKey target, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => CreateSymbolicAsync(name, target, force, logMessage is null ? null : Encoding.UTF8.GetBytes(logMessage), cancellationToken);

    /// <summary> Creates a symbolic reference with a byte-faithful reflog message. byte-primary surface — see <see cref="CreateAsync(string,
    /// GitOid, bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    internal async Task<GitReference> CreateSymbolicAsync(RefNameKey name, RefNameKey target, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RefNameKey normalized = NormalizeOrThrow(name);

        // C (refs.c:413-421): the symbolic target is normalized via
        // reference_normalize_for_repo — an invalid target (e.g. containing
        // "..") fails with GIT_EINVALIDSPEC "the given reference name '%s'
        // is not valid"; valid targets are stored in normalized form
        // (empty segments are skipped, e.g. "refs//heads/x" → "refs/heads/x").
        if (!TryNormalizeName(target.Span, GitReferenceFormatFlags.AllowOneLevel, out RefNameKey normalizedTarget))
        {
            throw new GitException(
                GitErrorCode.InvalidSpec,
                $"the given reference name '{target}' is not valid",
                GitErrorCategory.Reference);
        }

        if (!force && await _db.LookupAsync(normalized, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new GitException(
                GitErrorCode.Exists,
                $"failed to write reference '{normalized}': a reference with that name already exists.",
                GitErrorCategory.Reference);
        }

        var reference = new GitSymbolicReference { NameKey = normalized, TargetNameKey = normalizedTarget };
        IRefLock lockHandle = _db.Lock(normalized);
        try
        {
            await _db.WriteAsync(reference, lockHandle, updateReflog: true, oldId: default, oldTarget: null, committer: null, message: logMessageBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _db.UnlockAsync(lockHandle, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return reference with { Owner = _repo };
    }

    /// <summary>
    /// Creates or updates a direct reference, but only if the ref's current
    /// value matches <paramref name="expectedOldId"/>. This is a compare-and-swap
    /// (CAS) operation. Matches <c>git_reference_create_matching</c>
    /// (refs.c:482-535). If <paramref name="force"/> is false and the ref
    /// exists but doesn't match <paramref name="expectedOldId"/>, throws
    /// <see cref="GitErrorCode.Modified"/>.
    /// </summary>
    /// <param name="name">Fully-qualified ref name.</param>
    /// <param name="target">The new target OID.</param>
    /// <param name="expectedOldId">The expected current OID. If zero, the ref must not exist (unless <paramref name="force"/> is true).</param>
    /// <param name="force">If true, overwrite even if the ref exists (CAS check still applies when <paramref name="expectedOldId"/> is non-zero).</param>
    /// <param name="logMessage">Optional reflog message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created/updated reference.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Modified"/> if the current ref value doesn't match <paramref name="expectedOldId"/>.
    /// <see cref="GitErrorCode.Exists"/> if <paramref name="force"/> is false and the ref exists but <paramref name="expectedOldId"/> is zero.
    /// </exception>
    internal Task<GitReference> CreateMatchingAsync(RefNameKey name, GitOid target, GitOid expectedOldId, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => CreateMatchingAsync(name, target, expectedOldId, force, logMessage is null ? null : Encoding.UTF8.GetBytes(logMessage), cancellationToken);

    /// <summary> Creates or updates a direct reference (compare-and-swap) with a byte-faithful reflog message. byte-primary surface — see
    /// <see cref="CreateAsync(string, GitOid, bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    internal async Task<GitReference> CreateMatchingAsync(RefNameKey name, GitOid target, GitOid expectedOldId, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RefNameKey normalized = NormalizeOrThrow(name);
        await EnsureTargetExistsAsync(target, cancellationToken).ConfigureAwait(false);

        // C (reference_path_available, refdb_fs.c:1112-1126): the EEXISTS
        // check runs BEFORE the CAS, so a non-forced create_matching on an
        // existing ref is GIT_EEXISTS even when the old value matches.
        if (!force && await _db.LookupAsync(normalized, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new GitException(
                GitErrorCode.Exists,
                $"failed to write reference '{normalized}': a reference with that name already exists.",
                GitErrorCategory.Reference);
        }

        var reference = new GitDirectReference { NameKey = normalized, Target = target };
        IRefLock lockHandle = _db.Lock(normalized);
        try
        {
            // Pass expectedOldId to Write for CAS enforcement at the backend level.
            await _db.WriteAsync(reference, lockHandle, updateReflog: true, oldId: expectedOldId, oldTarget: null, committer: null, message: logMessageBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _db.UnlockAsync(lockHandle, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return reference with { Owner = _repo };
    }

    /// <summary>
    /// Sets the target of a direct reference. The ref must exist. Matches
    /// <c>git_reference_set_target</c> (refs.c:542-561).
    /// </summary>
    /// <param name="reference">The existing reference to update (only <see cref="GitReference.Name"/> is used).</param>
    /// <param name="newTarget">The new target OID.</param>
    /// <param name="logMessage">Optional reflog message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated reference.</returns>
    internal Task<GitReference> SetTargetAsync(GitReference reference, GitOid newTarget, string? logMessage = null, CancellationToken cancellationToken = default)
        => SetTargetAsync(reference, newTarget, logMessage is null ? null : Encoding.UTF8.GetBytes(logMessage), cancellationToken);

    /// <summary> Sets the target of a direct reference with a byte-faithful reflog message. byte-primary surface — see <see
    /// cref="CreateAsync(string, GitOid, bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    internal async Task<GitReference> SetTargetAsync(GitReference reference, GitOid newTarget, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(reference);

        // C (ensure_is_an_updatable_direct_reference, refs.c:533-540): an OID
        // cannot be set on a symbolic reference.
        if (reference.IsSymbolic)
        {
            throw new GitException(
                GitErrorCode.Error,
                "cannot set OID on symbolic reference",
                GitErrorCategory.Reference);
        }

        await EnsureTargetExistsAsync(newTarget, cancellationToken).ConfigureAwait(false);

        if (await _db.LookupAsync(reference.NameKey, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{reference.NameKey}' not found",
                GitErrorCategory.Reference);
        }

        var updated = new GitDirectReference { NameKey = reference.NameKey, Target = newTarget };
        IRefLock lockHandle = _db.Lock(reference.NameKey);
        try
        {
            // C (refs.c:560, git_reference_create_matching): the write is a compare-and-swap against the LOADED target — a concurrent change yields
            // GIT_EMODIFIED instead of a silent overwrite.
            await _db.WriteAsync(updated, lockHandle, updateReflog: true, oldId: ((GitDirectReference)reference).Target, oldTarget: null, committer: null, message: logMessageBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _db.UnlockAsync(lockHandle, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return updated with { Owner = _repo };
    }

    /// <summary>
    /// Sets the target of a symbolic reference. The ref must exist. Matches
    /// <c>git_reference_symbolic_set_target</c> (refs.c:572-601).
    /// </summary>
    internal Task<GitReference> SetSymbolicTargetAsync(GitReference reference, RefNameKey newTarget, string? logMessage = null, CancellationToken cancellationToken = default)
        => SetSymbolicTargetAsync(reference, newTarget, logMessage is null ? null : Encoding.UTF8.GetBytes(logMessage), cancellationToken);

    /// <summary> Sets the target of a symbolic reference with a byte-faithful reflog message. byte-primary surface — see <see
    /// cref="CreateAsync(string, GitOid, bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    internal async Task<GitReference> SetSymbolicTargetAsync(GitReference reference, RefNameKey newTarget, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(reference);

        // C (ensure_is_an_updatable_symbolic_reference, refs.c:563-569): a
        // symbolic target cannot be set on a direct reference.
        if (!reference.IsSymbolic)
        {
            throw new GitException(
                GitErrorCode.Error,
                "cannot set symbolic target on a direct reference",
                GitErrorCategory.Reference);
        }

        // C (refs.c:413-421): the symbolic target is normalized/validated.
        if (!TryNormalizeName(newTarget.Span, GitReferenceFormatFlags.AllowOneLevel, out RefNameKey normalizedTarget))
        {
            throw new GitException(
                GitErrorCode.InvalidSpec,
                $"the given reference name '{newTarget}' is not valid",
                GitErrorCategory.Reference);
        }

        var updated = new GitSymbolicReference { NameKey = reference.NameKey, TargetNameKey = normalizedTarget };
        IRefLock lockHandle = _db.Lock(reference.NameKey);
        try
        {
            // C (refs.c:587-588, git_reference_symbolic_create_matching): the write is a compare-and-swap against the LOADED symbolic target.
            await _db.WriteAsync(updated, lockHandle, updateReflog: true, oldId: default, oldTarget: ((GitSymbolicReference)reference).TargetNameKey, committer: null, message: logMessageBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _db.UnlockAsync(lockHandle, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return updated with { Owner = _repo };
    }

    /// <summary>
    /// Deletes a reference. Matches <c>git_reference_delete</c> (refs.c:152-167).
    /// </summary>
    internal async Task DeleteAsync(RefNameKey name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RefNameKey normalized = NormalizeOrThrow(name);
        IRefLock lockHandle = _db.Lock(normalized);
        try
        {
            await _db.DeleteAsync(normalized, lockHandle, oldId: default, oldTarget: null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _db.UnlockAsync(lockHandle, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Deletes a reference (by reference object). Matches
    /// <c>git_reference_delete</c> (refs.c:152-167), which refuses to delete
    /// HEAD ("cannot delete HEAD", GIT_ERROR).
    /// </summary>
    public async Task DeleteAsync(GitReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.NameKey == "HEAD")
        {
            throw new GitException(
                GitErrorCode.Error,
                "cannot delete HEAD",
                GitErrorCategory.Reference);
        }

        // C (refs.c:162-167, git_reference_delete): the delete is a compare-and-swap against the LOADED value — a direct ref passes its target OID, a symbolic
        // ref its target name.
        IRefLock lockHandle = _db.Lock(reference.NameKey);
        try
        {
            await _db.DeleteAsync(
                reference.NameKey, lockHandle,
                oldId: reference is GitDirectReference direct ? direct.Target : default,
                oldTarget: reference is GitSymbolicReference symbolic ? symbolic.TargetNameKey : (RefNameKey?)null,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _db.UnlockAsync(lockHandle, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// C (reference__create, refs.c:402-409): a direct ref's target OID must
    /// exist in the odb — dangling refs cannot be created.
    /// </summary>
    private async Task EnsureTargetExistsAsync(GitOid oid, CancellationToken cancellationToken)
    {
        if (_repo is null)
        {
            return;
        }

        // C: git_object__is_valid (refs.c:402-409) — a READ-based check, so
        // the hardcoded empty tree counts as valid.
        if (!await _repo.Objects.IsValidAsync(oid, GitObjectType.Any, cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(
                GitErrorCode.Error,
                "target OID for the reference doesn't exist on the repository",
                GitErrorCategory.Reference);
        }
    }

    /// <summary>
    /// Renames a reference. Matches <c>git_reference_rename</c> (refs.c:621-705).
    /// If <paramref name="force"/> is false and the new name exists, throws
    /// <see cref="GitErrorCode.Exists"/>.
    /// </summary>
    /// <param name="reference">The reference to rename (only <see cref="GitReference.Name"/> is used).</param>
    /// <param name="newName">The new fully-qualified ref name.</param>
    /// <param name="force">Allow overwriting an existing ref at <paramref name="newName"/>.</param>
    /// <param name="logMessage">Optional reflog message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The renamed reference.</returns>
    internal Task<GitReference> RenameAsync(GitReference reference, RefNameKey newName, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => RenameAsync(reference, newName, force, logMessage is null ? null : Encoding.UTF8.GetBytes(logMessage), cancellationToken);

    /// <summary> Renames a reference with a byte-faithful reflog message. byte-primary surface — see <see cref="CreateAsync(string, GitOid,
    /// bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    internal async Task<GitReference> RenameAsync(GitReference reference, RefNameKey newName, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(reference);
        RefNameKey newNormalized = NormalizeOrThrow(newName);

        IRefLock lockHandle = _db.Lock(reference.NameKey);
        try
        {
            // Read the current value to carry over.
            GitReference? current = await _db.LookupAsync(reference.NameKey, cancellationToken).ConfigureAwait(false);
            GitOid newId = default;
            RefNameKey? newTarget = null;
            if (current is not null)
            {
                if (current.IsSymbolic)
                {
                    newTarget = ((GitSymbolicReference)current).TargetNameKey;
                }
                else
                {
                    newId = ((GitDirectReference)current).Target;
                }
            }

            await _db.RenameAsync(lockHandle, newNormalized, newId, newTarget, force, committer: null, message: logMessageBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _db.UnlockAsync(lockHandle, cancellationToken).ConfigureAwait(false);
            throw;
        }

        // Update HEAD if it was pointing to the renamed reference.
        // Matches refs.c:594-646 (refs_update_head via git_repository_foreach_worktree).
        await UpdateHeadIfPointingAtAsync(reference.NameKey, newNormalized, cancellationToken).ConfigureAwait(false);

        // Also update HEAD in linked worktrees that point at the renamed ref.
        // Matches C's git_repository_foreach_worktree in refs_update_head.
        await UpdateLinkedWorktreeHeadsAsync(reference.NameKey, newNormalized, cancellationToken).ConfigureAwait(false);

        // Return the new reference (lookup the new name).
        GitReference? renamed = await _db.LookupAsync(newNormalized, cancellationToken).ConfigureAwait(false);
        return renamed is null
            ? throw new GitException(GitErrorCode.NotFound, "renamed reference not found", GitErrorCategory.Reference)
            : renamed with { Owner = _repo };
    }

    /// <summary>
    /// If HEAD is a symbolic ref pointing at <paramref name="oldName"/>,
    /// repoint it to <paramref name="newName"/>. Matches
    /// <c>refs_update_head</c> (refs.c:596-619).
    /// </summary>
    private async Task UpdateHeadIfPointingAtAsync(RefNameKey oldName, RefNameKey newName, CancellationToken cancellationToken)
    {
        GitReference? head = await _db.LookupAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is not GitSymbolicReference symRef)
        {
            return;
        }

        if (symRef.TargetNameKey != oldName)
        {
            return;
        }

        // Repoint HEAD to the new name. C's refs_update_head goes through git_reference_symbolic_set_target → the backend write with update_reflog=1
        // (refdb_fs.c:1616), so logs/HEAD gains an entry with the old/new branch OIDs (reflog_append resolves both from the symbolic targets,
        // refdb_fs.c:2324-2335).
        IRefLock headLock = _db.Lock("HEAD");
        try
        {
            await _db.WriteAsync(
                new GitSymbolicReference { NameKey = "HEAD", TargetNameKey = newName },
                headLock,
                updateReflog: true,
                oldId: default,
                oldTarget: oldName,
                committer: null,
                message: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _db.UnlockAsync(headLock, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Updates HEAD in linked worktrees that point at the renamed ref.
    /// Matches the worktree iteration in <c>refs_update_head</c>
    /// (refs.c:594-646).
    /// </summary>
    private async Task UpdateLinkedWorktreeHeadsAsync(RefNameKey oldName, RefNameKey newName, CancellationToken cancellationToken)
    {
        GitRepository repo = _repo ?? throw new ObjectDisposedException(nameof(GitReferences));
        foreach (Worktree wt in await Worktree.ListAsync(repo, cancellationToken).ConfigureAwait(false))
        {
            string headPath = Path.Join(wt.GitdirPath, "HEAD");
            if (!File.Exists(headPath))
            {
                continue;
            }

            // byte-domain read/write (C reads/writes the raw HEAD file bytes, refdb_fs.c:244-253, 1188-1205) — non-UTF-8 symref targets round-trip byte-exact.
            byte[] headContent = await File.ReadAllBytesAsync(headPath, cancellationToken).ConfigureAwait(false);
            ReadOnlySpan<byte> trimmed = AsciiText.Rtrim(headContent);
            if (!trimmed.StartsWith("ref: "u8))
            {
                continue; // detached HEAD — no update needed
            }

            var refName = RefNameKey.From(trimmed["ref: ".Length..].ToArray());
            if (refName != oldName)
            {
                continue;
            }

            await AsyncFileIO.WriteAtomicAsync(headPath, newName.SymbolicContent(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures a reflog file exists for the given reference. Matches
    /// <c>git_reference_ensure_log</c> (refs.c:1228).
    /// </summary>
    internal async Task EnsureLogAsync(RefNameKey refName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RefNameKey normalized = NormalizeOrThrow(refName);
        await _db.EnsureLogAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a reflog entry for the given reference. Matches
    /// <c>git_reflog_append</c>. The message is UTF-8-encoded into the byte
    /// tier.
    /// </summary>
    internal Task AppendReflogAsync(RefNameKey refName, GitOid oldId, GitOid newId, GitSignature committer, string? message, CancellationToken cancellationToken = default)
        => AppendReflogAsync(refName, oldId, newId, committer, message is null ? null : Encoding.UTF8.GetBytes(message), cancellationToken);

    /// <summary> Appends a reflog entry with byte-faithful message bytes. byte-primary surface — C's reflog append carries the raw <c>char
    /// *</c> message bytes verbatim (reflog.c:83-110). </summary>
    internal async Task AppendReflogAsync(RefNameKey refName, GitOid oldId, GitOid newId, GitSignature committer, ReadOnlyMemory<byte>? messageBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RefNameKey normalized = NormalizeOrThrow(refName);
        await _db.ReflogAppendAsync(normalized, oldId, newId, committer, messageBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Begins an atomic multi-ref transaction. Matches <c>git_transaction_new</c>.
    /// Lock refs with <see cref="LibGit2CS.Refs.GitTransaction.LockRef(LibGit2CS.Refs.RefNameKey)"/>, queue operations, then
    /// <see cref="GitTransaction.CommitAsync"/>. The transaction must be disposed
    /// via <c>await using</c>.
    /// </summary>
    internal GitTransaction BeginTransaction()
    {
        ThrowIfDisposed();
        return _db.BeginTransaction(_repo ?? throw new ObjectDisposedException(nameof(GitReferences)));
    }

    // ── DWIM + Shorthand (port of refs.c:254-317 + 1370-1389) ──────────

    /// <summary>
    /// Reference directory prefixes. Matches <c>GIT_REFS_DIR</c> et al.
    /// (<c>refs.h:20-24</c>).
    /// </summary>
    public const string RefsDir = "refs/";
    /// <summary>The local branch reference prefix: refs/heads/.</summary>
    public const string RefsHeadsDir = "refs/heads/";
    /// <summary>The tag reference prefix: refs/tags/.</summary>
    public const string RefsTagsDir = "refs/tags/";
    /// <summary>The remote-tracking reference prefix: refs/remotes/.</summary>
    public const string RefsRemotesDir = "refs/remotes/";
    /// <summary>The HEAD reference filename.</summary>
    public const string HeadFile = "HEAD";

    /// <summary>
    /// The candidate name formatters tried by <see cref="DwimAsync"/>, in order.
    /// Matches the <c>formatters[]</c> array in <c>git_reference_dwim</c>
    /// (<c>refs.c:261-269</c>).
    /// </summary>
    private static readonly ImmutableArray<string> s_dwimFormatters =
    [
        "{0}",
        "refs/{0}",
        "refs/tags/{0}",
        "refs/heads/{0}",
        "refs/remotes/{0}",
        "refs/remotes/{0}/HEAD",
    ];

    /// <summary>
    /// Resolves a shorthand reference name to a reference by trying the DWIM
    /// (Do What I Mean) search order. Matches <c>git_reference_dwim</c>
    /// (<c>refs.c:254-317</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a non-empty shorthand, the following candidates are tried in order
    /// (first hit wins, each resolved through the symbolic chain):
    /// <c>&lt;name&gt;</c>, <c>refs/&lt;name&gt;</c>, <c>refs/tags/&lt;name&gt;</c>,
    /// <c>refs/heads/&lt;name&gt;</c>, <c>refs/remotes/&lt;name&gt;</c>,
    /// <c>refs/remotes/&lt;name&gt;/HEAD</c>.
    /// </para>
    /// <para>
    /// For an empty shorthand, only <c>HEAD</c> is tried (no fallback search).
    /// </para>
    /// <para>
    /// Returns null if no candidate resolves (NotFound semantics). Throws
    /// <see cref="GitException"/> with <see cref="GitErrorCode.InvalidSpec"/> only
    /// if every candidate produces an invalid reference name.
    /// </para>
    /// </remarks>
    internal async Task<GitReference?> DwimAsync(string shorthand, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(shorthand);

        string name = shorthand.Length > 0 ? shorthand : HeadFile;
        bool fallbackMode = shorthand.Length > 0;
        bool foundValid = false;

        for (int i = 0; i < s_dwimFormatters.Length && (fallbackMode || i == 0); i++)
        {
            string candidate = string.Format(CultureInfo.InvariantCulture, s_dwimFormatters[i], name);

            // AllowOneLevel: accept HEAD, FETCH_HEAD, etc. as well as refs/... names.
            if (!IsNameValid(candidate, GitReferenceFormatFlags.AllowOneLevel))
            {
                continue;
            }

            foundValid = true;

            GitReference? resolved = await ResolveAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (!foundValid)
        {
            throw new GitException(
                GitErrorCode.InvalidSpec,
                $"could not use '{name}' as valid reference name",
                GitErrorCategory.Reference);
        }

        return null;
    }

    /// <summary>
    /// Returns the short form of a fully-qualified reference name. Matches
    /// <c>git_reference_shorthand</c> / <c>shorten_names</c>
    /// (<c>refs.c:1370-1389</c>).
    /// </summary>
    /// <param name="fullName">A fully-qualified reference name (e.g.
    /// <c>refs/heads/master</c>, <c>HEAD</c>).</param>
    /// <returns>The shorthand (<c>master</c>); the input unchanged if it is not
    /// under a known <c>refs/</c> subdirectory.</returns>
    public static string Shorthand(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);

        if (fullName.StartsWith(RefsHeadsDir, StringComparison.Ordinal))
        {
            return fullName[RefsHeadsDir.Length..];
        }

        if (fullName.StartsWith(RefsTagsDir, StringComparison.Ordinal))
        {
            return fullName[RefsTagsDir.Length..];
        }

        if (fullName.StartsWith(RefsRemotesDir, StringComparison.Ordinal))
        {
            return fullName[RefsRemotesDir.Length..];
        }

        if (fullName.StartsWith(RefsDir, StringComparison.Ordinal))
        {
            return fullName[RefsDir.Length..];
        }

        return fullName;
    }

    /// <summary>
    /// Normalizes and validates a reference name. Matches
    /// <c>git_reference_normalize_name</c> (<c>refs.c:1051</c>).
    /// </summary>
    /// <param name="name">The ref name to normalize.</param>
    /// <param name="flags">Format flags controlling validation rules.</param>
    /// <returns>The normalized name, or null if the name is invalid.</returns>
    public static string? NormalizeName(string name, GitReferenceFormatFlags flags = GitReferenceFormatFlags.Normal)
    {
        ArgumentNullException.ThrowIfNull(name);

        bool result = TryNormalizeName(name.AsSpan(), flags, out string? normalized);
        return result ? normalized : null;
    }

    /// <summary> Normalizes a ref name using the repository's <c>core.precomposeunicode</c> setting, matching <c>reference_normalize_for_repo</c>
    /// (<c>refs.c:201-218</c>). When precompose is enabled (and the runtime is macOS), an NFD-decomposed input is folded to NFC before
    /// validation. </summary> <param name="repo">The repository whose config determines the precompose behavior.</param> <param name="name">The ref name to
    /// normalize.</param> <param name="validate">If <see langword="true"/>, validate the name; if <see langword="false"/>, skip validation.</param> <param
    /// name="cancellationToken">Cancellation token.</param> <returns>The normalized name, or null if the name is invalid.</returns>
    internal static async ValueTask<string?> NormalizeNameForRepoAsync(
        GitRepository repo,
        string name,
        bool validate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);

        GitReferenceFormatFlags flags = GitReferenceFormatFlags.AllowOneLevel;

        bool precompose = await repo.Config.GetBoolAsync(
            "core.precomposeunicode", false, cancellationToken).ConfigureAwait(false);
        if (precompose)
        {
            flags |= GitReferenceFormatFlags.PrecomposeUnicode;
        }

        if (!validate)
        {
            // Ports GIT_REFERENCE_FORMAT__VALIDATION_DISABLE; the existing
            // TryNormalizeName always validates, so the unvalidated path
            // returns the precomposed input directly when validation is
            // disabled (the C git_reference__normalize_name early-returns the
            // iconv output when the disable flag is set and the name has no
            // illegal characters — this is the closest equivalent here).
            string precomposed = (flags & GitReferenceFormatFlags.PrecomposeUnicode) != 0
                ? PathPrecompose.PrecomposeCore(name)
                : name;
            return precomposed;
        }

        return NormalizeName(name, flags);
    }

    /// <summary>
    /// Checks whether a reference name is valid. Matches
    /// <c>git_reference_name_is_valid</c> (<c>refs.c:1367</c>).
    /// </summary>
    public static bool IsNameValid(ReadOnlySpan<char> name, GitReferenceFormatFlags flags = GitReferenceFormatFlags.Normal)
    {
        // C (refs.c:1346-1365): git_reference__name_is_valid runs the
        // normalizer with buf == NULL — normalization is off, so empty
        // segments ("refs//heads") are invalid.
        return TryNormalizeName(name, flags, out _, normalize: false);
    }

    /// <summary>Validates raw reference-name bytes without decoding, matching C's ASCII rejection rules.</summary>
    public static bool IsNameValid(ReadOnlySpan<byte> name, GitReferenceFormatFlags flags = GitReferenceFormatFlags.Normal)
        => TryNormalizeName(name, flags, out _, normalize: false);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _db.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Normalizes the name and throws on invalid. Used by <see cref="LibGit2CS.Refs.GitReferences.LookupAsync(LibGit2CS.Refs.RefNameKey, System.Threading.CancellationToken)"/>/
    /// <see cref="LibGit2CS.Refs.GitReferences.ResolveAsync(LibGit2CS.Refs.RefNameKey, System.Threading.CancellationToken)"/>.
    /// </summary>
    private static string NormalizeOrThrow(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!TryNormalizeName(name.AsSpan(), GitReferenceFormatFlags.AllowOneLevel, out string? normalized))
        {
            throw new GitException(
                GitErrorCode.InvalidSpec,
                $"the given reference name '{name}' is not valid",
                GitErrorCategory.Reference);
        }

        return normalized;
    }

    // ── Name normalization (port of refs.c:820-1049) ──────────────────

    private const string FileLockExtension = ".lock";

    /// <summary>
    /// Core normalization. Returns true on success with the normalized name in
    /// <paramref name="normalized"/>. Matches <c>git_reference__normalize_name</c>
    /// (<c>refs.c:911-1049</c>).
    /// </summary>
    private static bool TryNormalizeName(ReadOnlySpan<char> name, GitReferenceFormatFlags flags, out string normalized, bool normalize = true)
    {
        int byteCount = Encoding.UTF8.GetByteCount(name);
        byte[]? rented = null;
        Span<byte> buffer = byteCount <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            int written = Encoding.UTF8.GetBytes(name, buffer);
            bool valid = TryNormalizeName(buffer[..written], flags, out RefNameKey raw, normalize);
            normalized = valid && normalize ? raw.ToString() : string.Empty;
            return valid;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>UTF-8 convenience reference operation.</summary>
    /// <returns>The result, or null if not found.</returns>
    internal Task<GitReference?> LookupAsync(string name, CancellationToken cancellationToken = default)
        => LookupAsync((RefNameKey)name, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    /// <returns>The result, or null if not found.</returns>
    internal Task<GitReference?> LookupAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
        => LookupAsync(RefNameKey.From(name), cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    /// <returns>The result, or null if not found.</returns>
    internal Task<GitReference?> ResolveAsync(string name, CancellationToken cancellationToken = default)
        => ResolveAsync((RefNameKey)name, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    /// <returns>The result, or null if not found.</returns>
    internal Task<GitReference?> ResolveAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
        => ResolveAsync(RefNameKey.From(name), cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal IAsyncEnumerable<GitReference> ListAsync(string? glob = null, CancellationToken cancellationToken = default)
        => ListAsync(glob is null ? (RefNameKey?)null : (RefNameKey)glob, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal IAsyncEnumerable<GitReference> ListBytesAsync(ReadOnlyMemory<byte> glob, CancellationToken cancellationToken = default)
        => ListAsync(RefNameKey.From(glob), cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    public ValueTask<bool> HasLogAsync(string refName, CancellationToken cancellationToken = default)
        => HasLogAsync((RefNameKey)refName, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    public ValueTask<bool> HasLogAsync(ReadOnlyMemory<byte> refName, CancellationToken cancellationToken = default)
        => HasLogAsync(RefNameKey.From(refName), cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    /// <returns>The result, or null if not found.</returns>
    internal Task<GitRefLog?> ReadLogAsync(string refName, CancellationToken cancellationToken = default)
        => ReadLogAsync((RefNameKey)refName, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    /// <returns>The result, or null if not found.</returns>
    internal Task<GitRefLog?> ReadLogAsync(ReadOnlyMemory<byte> refName, CancellationToken cancellationToken = default)
        => ReadLogAsync(RefNameKey.From(refName), cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> CreateAsync(string name, GitOid id, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => CreateAsync((RefNameKey)name, id, force, logMessage, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> CreateAsync(ReadOnlyMemory<byte> name, GitOid id, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => CreateAsync(RefNameKey.From(name), id, force, logMessage, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> CreateAsync(string name, GitOid id, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => CreateAsync((RefNameKey)name, id, force, logMessageBytes, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> CreateAsync(ReadOnlyMemory<byte> name, GitOid id, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => CreateAsync(RefNameKey.From(name), id, force, logMessageBytes, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> CreateSymbolicAsync(string name, string target, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => CreateSymbolicAsync((RefNameKey)name, (RefNameKey)target, force, logMessage, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> CreateSymbolicAsync(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> target, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => CreateSymbolicAsync(RefNameKey.From(name), RefNameKey.From(target), force, logMessage, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> CreateSymbolicAsync(string name, string target, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => CreateSymbolicAsync((RefNameKey)name, (RefNameKey)target, force, logMessageBytes, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> CreateSymbolicAsync(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> target, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => CreateSymbolicAsync(RefNameKey.From(name), RefNameKey.From(target), force, logMessageBytes, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> CreateMatchingAsync(string name, GitOid target, GitOid expectedOldId, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => CreateMatchingAsync((RefNameKey)name, target, expectedOldId, force, logMessage, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> CreateMatchingAsync(ReadOnlyMemory<byte> name, GitOid target, GitOid expectedOldId, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => CreateMatchingAsync(RefNameKey.From(name), target, expectedOldId, force, logMessage, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> CreateMatchingAsync(string name, GitOid target, GitOid expectedOldId, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => CreateMatchingAsync((RefNameKey)name, target, expectedOldId, force, logMessageBytes, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> CreateMatchingAsync(ReadOnlyMemory<byte> name, GitOid target, GitOid expectedOldId, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => CreateMatchingAsync(RefNameKey.From(name), target, expectedOldId, force, logMessageBytes, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> SetSymbolicTargetAsync(GitReference reference, string newTarget, string? logMessage = null, CancellationToken cancellationToken = default)
        => SetSymbolicTargetAsync(reference, (RefNameKey)newTarget, logMessage, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> SetSymbolicTargetAsync(GitReference reference, ReadOnlyMemory<byte> newTarget, string? logMessage = null, CancellationToken cancellationToken = default)
        => SetSymbolicTargetAsync(reference, RefNameKey.From(newTarget), logMessage, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> SetSymbolicTargetAsync(GitReference reference, string newTarget, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => SetSymbolicTargetAsync(reference, (RefNameKey)newTarget, logMessageBytes, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> SetSymbolicTargetAsync(GitReference reference, ReadOnlyMemory<byte> newTarget, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => SetSymbolicTargetAsync(reference, RefNameKey.From(newTarget), logMessageBytes, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        => DeleteAsync((RefNameKey)name, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    public Task DeleteAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
        => DeleteAsync(RefNameKey.From(name), cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> RenameAsync(GitReference reference, string newName, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => RenameAsync(reference, (RefNameKey)newName, force, logMessage, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> RenameAsync(GitReference reference, ReadOnlyMemory<byte> newName, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => RenameAsync(reference, RefNameKey.From(newName), force, logMessage, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    internal Task<GitReference> RenameAsync(GitReference reference, string newName, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => RenameAsync(reference, (RefNameKey)newName, force, logMessageBytes, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    internal Task<GitReference> RenameAsync(GitReference reference, ReadOnlyMemory<byte> newName, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => RenameAsync(reference, RefNameKey.From(newName), force, logMessageBytes, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    public Task EnsureLogAsync(string refName, CancellationToken cancellationToken = default)
        => EnsureLogAsync((RefNameKey)refName, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    public Task EnsureLogAsync(ReadOnlyMemory<byte> refName, CancellationToken cancellationToken = default)
        => EnsureLogAsync(RefNameKey.From(refName), cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    public Task AppendReflogAsync(string refName, GitOid oldId, GitOid newId, GitSignature committer, string? message, CancellationToken cancellationToken = default)
        => AppendReflogAsync((RefNameKey)refName, oldId, newId, committer, message, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    public Task AppendReflogAsync(ReadOnlyMemory<byte> refName, GitOid oldId, GitOid newId, GitSignature committer, string? message, CancellationToken cancellationToken = default)
        => AppendReflogAsync(RefNameKey.From(refName), oldId, newId, committer, message, cancellationToken);

    /// <summary>UTF-8 convenience reference operation.</summary>
    public Task AppendReflogAsync(string refName, GitOid oldId, GitOid newId, GitSignature committer, ReadOnlyMemory<byte>? messageBytes, CancellationToken cancellationToken = default)
        => AppendReflogAsync((RefNameKey)refName, oldId, newId, committer, messageBytes, cancellationToken);

    /// <summary>Raw-byte reference operation.</summary>
    public Task AppendReflogAsync(ReadOnlyMemory<byte> refName, GitOid oldId, GitOid newId, GitSignature committer, ReadOnlyMemory<byte>? messageBytes, CancellationToken cancellationToken = default)
        => AppendReflogAsync(RefNameKey.From(refName), oldId, newId, committer, messageBytes, cancellationToken);

    private static RefNameKey NormalizeOrThrow(RefNameKey name)
    {
        if (!TryNormalizeName(name.Span, GitReferenceFormatFlags.AllowOneLevel, out RefNameKey normalized))
        {
            throw new GitException(GitErrorCode.InvalidSpec, $"the given reference name '{name}' is not valid", GitErrorCategory.Reference);
        }
        return normalized;
    }

    /// <summary>Normalizes raw reference bytes, or returns null if invalid.</summary>
    public static ReadOnlyMemory<byte>? NormalizeName(ReadOnlyMemory<byte> name, GitReferenceFormatFlags flags = GitReferenceFormatFlags.Normal)
        => TryNormalizeName(name.Span, flags, out RefNameKey normalized) ? normalized.Bytes : null;

    private static bool TryNormalizeName(ReadOnlySpan<byte> name, GitReferenceFormatFlags flags, out RefNameKey normalized, bool normalize = true)
    {
        normalized = default;

        if (name.IsEmpty)
        {
            return false;
        }

        // Ports git_reference__normalize_name (refs.c:911-1049): the iconv precompose happens BEFORE the validation loop when the PRECOMPOSE_UNICODE flag is
        // set.
        if ((flags & GitReferenceFormatFlags.PrecomposeUnicode) != 0)
        {
            var key = RefNameKey.From(name.ToArray());
            if (key.TryGetFileSystemString(out string? text))
            {
                name = System.Text.Encoding.UTF8.GetBytes(PathPrecompose.PrecomposeCore(text));
            }
        }

        // Skip leading '/'.
        int start = 0;
        while (start < name.Length && name[start] == (byte)'/')
        {
            return false; // leading slash is invalid
        }

        using PooledByteBufferWriter? sb = normalize ? new PooledByteBufferWriter(name.Length) : null;
        int segmentsCount = 0;
        GitReferenceFormatFlags processFlags = flags;
        bool allowCaretPrefix = true;
        int pos = start;
        int lastSegmentLen = 0;

        while (pos <= name.Length)
        {
            // Find end of segment (next '/' or end of string).
            int segEnd = pos;
            while (segEnd < name.Length && name[segEnd] != (byte)'/')
            {
                segEnd++;
            }

            ReadOnlySpan<byte> segment = name[pos..segEnd];
            int segLen = segment.Length;

            // Validate segment.
            int validLen = EnsureSegmentValidity(segment, processFlags, allowCaretPrefix, out bool hasGlob);
            if (validLen < 0)
            {
                return false;
            }

            if (segLen > 0)
            {
                // If this segment has a glob, clear the pattern flag.
                if (hasGlob)
                {
                    processFlags &= ~GitReferenceFormatFlags.RefspecPattern;
                }

                // "@" alone (first segment, length 1) → "HEAD"
                if (segmentsCount == 0 && segLen == 1 && segment[0] == (byte)'@')
                {
                    sb?.Clear();
                    sb?.Write("HEAD"u8);
                }
                else
                {
                    if (sb is not null)
                    {
                        if (sb.WrittenCount > 0)
                        {
                            sb.Write("/"u8);
                        }

                        sb.Write(segment);
                    }
                }

                segmentsCount++;
                lastSegmentLen = segLen;
            }
            else if (segLen == 0 && pos < name.Length)
            {
                // C (refs.c:991-993): "No empty segment is allowed when not
                // normalizing" — git_reference_name_is_valid runs the
                // normalizer with buf == NULL, so refs//heads is invalid
                // there while NormalizeName (buf != NULL) skips the empty
                // segment.
                if (!normalize)
                {
                    return false;
                }
            }

            if (segEnd >= name.Length)
            {
                break;
            }

            // Move past the '/'.
            pos = segEnd + 1;
            allowCaretPrefix = false;
        }

        // Can't be empty.
        if (segmentsCount == 0)
        {
            return false;
        }

        // Can't end with '.'.
        if (name[^1] == (byte)'.')
        {
            return false;
        }

        // Can't end with '/'.
        if (name[^1] == (byte)'/')
        {
            return false;
        }

        // One-level name checks.
        if (segmentsCount == 1)
        {
            if ((flags & GitReferenceFormatFlags.AllowOneLevel) == 0)
            {
                return false;
            }

            if ((flags & GitReferenceFormatFlags.RefspecShorthand) == 0)
            {
                // Must be all-caps/underscore, or "*" with RefspecPattern.
                ReadOnlySpan<byte> firstSeg = name[start..(start + lastSegmentLen)];
                if (!IsValidNormalizedName(firstSeg) &&
                    !((flags & GitReferenceFormatFlags.RefspecPattern) != 0 && firstSeg.Length == 1 && firstSeg[0] == (byte)'*'))
                {
                    return false;
                }
            }
        }
        else
        {
            // Multi-segment: without REFSPEC_SHORTHAND, first segment must NOT be all-caps.
            if ((flags & GitReferenceFormatFlags.RefspecShorthand) == 0)
            {
                int firstSlash = name.IndexOf((byte)'/');
                ReadOnlySpan<byte> firstSeg = name[start..firstSlash];
                if (IsValidNormalizedName(firstSeg))
                {
                    return false;
                }
            }
        }

        if (sb is not null)
        {
            normalized = RefNameKey.From(sb.WrittenSpan.ToArray());
        }
        return true;
    }

    /// <summary>
    /// Validates a single path segment. Returns the segment length on success,
    /// -1 on failure. Matches <c>ensure_segment_validity</c> (<c>refs.c:838-881</c>).
    /// </summary>
    private static int EnsureSegmentValidity(ReadOnlySpan<byte> segment, GitReferenceFormatFlags flags, bool allowCaretPrefix, out bool hasGlob)
    {
        hasGlob = false;

        if (segment.IsEmpty)
        {
            return 0;
        }

        bool mayContainGlob = (flags & GitReferenceFormatFlags.RefspecPattern) != 0;
        int start = 0;

        // No leading '.'.
        if (segment[0] == (byte)'.')
        {
            return -1;
        }

        // Allow '^' prefix (negative refspec) only in first segment.
        if (allowCaretPrefix && segment[0] == (byte)'^')
        {
            start = 1;
        }

        byte prev = (byte)'\0';

        for (int i = start; i < segment.Length; i++)
        {
            byte ch = segment[i];

            if (!IsValidRefChar(ch))
            {
                return -1;
            }

            if (prev == (byte)'.' && ch == (byte)'.')
            {
                return -1; // ".."
            }

            if (prev == (byte)'@' && ch == (byte)'{')
            {
                return -1; // "@{"
            }

            if (ch == (byte)'*')
            {
                if (!mayContainGlob)
                {
                    return -1;
                }

                mayContainGlob = false;
                hasGlob = true;
            }

            prev = ch;
        }

        // Can't end with ".lock".
        if (segment.Length >= FileLockExtension.Length &&
            segment.EndsWith(".lock"u8))
        {
            return -1;
        }

        return segment.Length;
    }

    /// <summary>
    /// Checks if a character is valid in a ref name. Matches
    /// <c>is_valid_ref_char</c> (<c>refs.c:820-836</c>).
    /// </summary>
    private static bool IsValidRefChar(byte ch)
    {
        if (ch is <= (byte)' ' or (byte)'\x7f')
        {
            return false;
        }

        return ch is not ((byte)'~' or (byte)'^' or (byte)':' or (byte)'\\' or (byte)'?' or (byte)'[');
    }

    /// <summary>
    /// Checks if a one-level name is all uppercase + underscore (e.g. HEAD, ORIG_HEAD).
    /// Matches <c>is_valid_normalized_name</c> (<c>refs.c:883-908</c>).
    /// </summary>
    private static bool IsValidNormalizedName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            byte c = name[i];

            // First byte can be '^' (negative refspec).
            if (i == 0 && c == (byte)'^')
            {
                continue;
            }

            // "@" alone is HEAD abbreviation.
            if (name.Length == 1 && c == (byte)'@')
            {
                return true;
            }

            if (c is (< (byte)'A' or > (byte)'Z') and not (byte)'_')
            {
                return false;
            }
        }

        // Can't start or end with '_'.
        if (name[0] == (byte)'_' || name[^1] == (byte)'_')
        {
            return false;
        }

        return true;
    }
}
