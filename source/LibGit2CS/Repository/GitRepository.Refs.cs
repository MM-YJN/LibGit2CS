// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.Refs;

namespace LibGit2CS.Repository;

/// <content>
/// Reference-database entry points. Managed ports of libgit2's
/// <c>git_repository_head</c> (repository.c:2963-2982) and
/// <c>git_refdb_compress</c> (refdb.c:92-100). The underlying factories are
/// internal; these instance methods are the public surface.
/// </content>
public sealed partial class GitRepository
{
    /// <summary>Resolves HEAD to a reference, or returns <c>null</c> if HEAD is
    /// unborn or missing. Matches <c>git_repository_head</c>
    /// (repository.c:2963-2982) with a managed-API divergence: the C function
    /// returns <c>GIT_EUNBORNBRANCH</c> for a symbolic HEAD whose target does
    /// not exist, and <c>GIT_ENOTFOUND</c> if HEAD itself is missing; this
    /// overload returns <c>null</c> in both cases (per AGENTS.md — documented
    /// null-for-not-found-like APIs). A detached HEAD returns the HEAD direct
    /// reference; a symbolic HEAD returns the branch reference it points at
    /// (fully resolved through a symbolic chain).</summary>
    public async Task<GitReference?> HeadAsync(CancellationToken cancellationToken = default)
    {
        GitReference? head = await Refs.LookupAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return null; // no HEAD file — empty/unborn repo
        }

        if (head is GitDirectReference direct)
        {
            return direct; // detached HEAD
        }

        // Symbolic HEAD — resolve the target fully (C uses
        // git_reference_lookup_resolved with max_nesting = -1; Refs.ResolveAsync
        // is the port). ResolveAsync returns null on not-found (no throw),
        // mapping the unborn-branch case to null per the documented contract.
        var sym = (GitSymbolicReference)head;
        return await Refs.ResolveAsync(sym.TargetNameKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Packs loose refs into <c>packed-refs</c>. Matches
    /// <c>git_refdb_compress</c> (refdb.c:92-100 → refdb_fs.c:1897-1910).
    /// Loose ref files are left on disk (no pruning).</summary>
    public Task PackRefsAsync(CancellationToken cancellationToken = default)
        => Refs.CompressAsync(cancellationToken);

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
    public Task<GitReference?> ReferenceLookupAsync(string name, CancellationToken cancellationToken = default)
        => Refs.LookupAsync(name, cancellationToken);

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
    public Task<GitReference?> ReferenceResolveAsync(string name, CancellationToken cancellationToken = default)
        => Refs.ResolveAsync(name, cancellationToken);

    /// <summary>
    /// Enumerates all references, optionally filtered by glob. Matches
    /// <c>git_reference_foreach</c> / <c>git_reference_iterator_new</c>.
    /// </summary>
    /// <param name="glob">Optional glob pattern (<c>*</c> does not cross <c>/</c>). Null/empty returns all.</param>
    /// <param name="cancellationToken">Cancellation token, propagated by <c>await foreach</c>.</param>
    public IAsyncEnumerable<GitReference> ReferenceListAsync(string? glob = null, CancellationToken cancellationToken = default)
        => Refs.ListAsync(glob, cancellationToken);

    /// <summary>
    /// Reads the reflog for the given reference. Returns null if no reflog file
    /// exists. Matches <c>git_reflog_read</c>.
    /// </summary>
    public Task<GitRefLog?> ReferenceReadLogAsync(string refName, CancellationToken cancellationToken = default)
        => Refs.ReadLogAsync(refName, cancellationToken);

    /// <summary>
    /// Creates a direct reference pointing at <paramref name="id"/>. If the ref
    /// already exists and <paramref name="force"/> is false, throws
    /// <see cref="GitErrorCode.Exists"/>. Matches <c>git_reference_create</c>
    /// (refs.c:487-496). Optionally writes a reflog entry with
    /// <paramref name="logMessage"/>.
    /// </summary>
    /// <returns>The created reference.</returns>
    public Task<GitReference> ReferenceCreateAsync(string name, GitOid id, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.CreateAsync(name, id, force, logMessage, cancellationToken);

    /// <summary> Creates a direct reference pointing at <paramref name="id"/> with a byte-faithful reflog message. The byte-primary surface — C's
    /// <c>git_reference_create</c> carries the raw <c>char *</c> message bytes to the reflog serializer verbatim (refs.c:487-496, refdb_fs.c:2195); non-UTF-8
    /// message bytes round-trip byte-exact. The string overload above is the UTF-8 convenience tier. </summary>
    public Task<GitReference> ReferenceCreateAsync(string name, GitOid id, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.CreateAsync(name, id, force, logMessageBytes, cancellationToken);

    /// <summary>
    /// Creates a symbolic reference pointing at <paramref name="target"/>. If
    /// the ref already exists and <paramref name="force"/> is false, throws
    /// <see cref="GitErrorCode.Exists"/>. Matches <c>git_reference_symbolic_create</c>.
    /// </summary>
    public Task<GitReference> ReferenceCreateSymbolicAsync(string name, string target, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.CreateSymbolicAsync(name, target, force, logMessage, cancellationToken);

    /// <summary> Creates a symbolic reference with a byte-faithful reflog message. byte-primary surface — see <see cref="ReferenceCreateAsync(string,
    /// GitOid, bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    public Task<GitReference> ReferenceCreateSymbolicAsync(string name, string target, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.CreateSymbolicAsync(name, target, force, logMessageBytes, cancellationToken);

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
    public Task<GitReference> ReferenceCreateMatchingAsync(string name, GitOid target, GitOid expectedOldId, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.CreateMatchingAsync(name, target, expectedOldId, force, logMessage, cancellationToken);

    /// <summary> Creates or updates a direct reference (compare-and-swap) with a byte-faithful reflog message. byte-primary surface — see
    /// <see cref="ReferenceCreateAsync(string, GitOid, bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    public Task<GitReference> ReferenceCreateMatchingAsync(string name, GitOid target, GitOid expectedOldId, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.CreateMatchingAsync(name, target, expectedOldId, force, logMessageBytes, cancellationToken);

    /// <summary>
    /// Sets the target of a direct reference. The ref must exist. Matches
    /// <c>git_reference_set_target</c> (refs.c:542-561).
    /// </summary>
    /// <param name="reference">The existing reference to update (only <see cref="GitReference.Name"/> is used).</param>
    /// <param name="newTarget">The new target OID.</param>
    /// <param name="logMessage">Optional reflog message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated reference.</returns>
    public Task<GitReference> ReferenceSetTargetAsync(GitReference reference, GitOid newTarget, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.SetTargetAsync(reference, newTarget, logMessage, cancellationToken);

    /// <summary> Sets the target of a direct reference with a byte-faithful reflog message. byte-primary surface — see <see
    /// cref="ReferenceCreateAsync(string, GitOid, bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    public Task<GitReference> ReferenceSetTargetAsync(GitReference reference, GitOid newTarget, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.SetTargetAsync(reference, newTarget, logMessageBytes, cancellationToken);

    /// <summary>
    /// Sets the target of a symbolic reference. The ref must exist. Matches
    /// <c>git_reference_symbolic_set_target</c> (refs.c:572-601).
    /// </summary>
    public Task<GitReference> ReferenceSetSymbolicTargetAsync(GitReference reference, string newTarget, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.SetSymbolicTargetAsync(reference, newTarget, logMessage, cancellationToken);

    /// <summary> Sets the target of a symbolic reference with a byte-faithful reflog message. byte-primary surface — see <see
    /// cref="ReferenceCreateAsync(string, GitOid, bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    public Task<GitReference> ReferenceSetSymbolicTargetAsync(GitReference reference, string newTarget, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.SetSymbolicTargetAsync(reference, newTarget, logMessageBytes, cancellationToken);

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
    public Task<GitReference> ReferenceRenameAsync(GitReference reference, string newName, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.RenameAsync(reference, newName, force, logMessage, cancellationToken);

    /// <summary> Renames a reference with a byte-faithful reflog message. byte-primary surface — see <see cref="ReferenceCreateAsync(string, GitOid,
    /// bool, ReadOnlyMemory{byte}?, CancellationToken)"/>. </summary>
    public Task<GitReference> ReferenceRenameAsync(GitReference reference, string newName, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.RenameAsync(reference, newName, force, logMessageBytes, cancellationToken);

    /// <summary>
    /// Begins an atomic multi-ref transaction. Matches <c>git_transaction_new</c>.
    /// Lock refs with <see cref="LibGit2CS.Refs.GitTransaction.LockRef(LibGit2CS.Refs.RefNameKey)"/>, queue operations, then
    /// <see cref="GitTransaction.CommitAsync"/>. The transaction must be disposed
    /// via <c>await using</c>.
    /// </summary>
    public GitTransaction NewReferenceTransaction()
        => Refs.BeginTransaction();

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
    public Task<GitReference?> ReferenceDwimAsync(string shorthand, CancellationToken cancellationToken = default)
        => Refs.DwimAsync(shorthand, cancellationToken);
    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    /// <returns>The result, or null if not found.</returns>
    public Task<GitReference?> ReferenceLookupAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
        => Refs.LookupAsync(RefNameKey.From(name), cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    /// <returns>The result, or null if not found.</returns>
    public Task<GitReference?> ReferenceResolveAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
        => Refs.ResolveAsync(RefNameKey.From(name), cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public IAsyncEnumerable<GitReference> ReferenceListBytesAsync(ReadOnlyMemory<byte> glob, CancellationToken cancellationToken = default)
        => Refs.ListAsync(RefNameKey.From(glob), cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    /// <returns>The result, or null if not found.</returns>
    public Task<GitRefLog?> ReferenceReadLogAsync(ReadOnlyMemory<byte> refName, CancellationToken cancellationToken = default)
        => Refs.ReadLogAsync(RefNameKey.From(refName), cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceCreateAsync(ReadOnlyMemory<byte> name, GitOid id, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.CreateAsync(RefNameKey.From(name), id, force, logMessage, cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceCreateAsync(ReadOnlyMemory<byte> name, GitOid id, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.CreateAsync(RefNameKey.From(name), id, force, logMessageBytes, cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceCreateSymbolicAsync(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> target, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.CreateSymbolicAsync(RefNameKey.From(name), RefNameKey.From(target), force, logMessage, cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceCreateSymbolicAsync(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> target, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.CreateSymbolicAsync(RefNameKey.From(name), RefNameKey.From(target), force, logMessageBytes, cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceCreateMatchingAsync(ReadOnlyMemory<byte> name, GitOid target, GitOid expectedOldId, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.CreateMatchingAsync(RefNameKey.From(name), target, expectedOldId, force, logMessage, cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceCreateMatchingAsync(ReadOnlyMemory<byte> name, GitOid target, GitOid expectedOldId, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.CreateMatchingAsync(RefNameKey.From(name), target, expectedOldId, force, logMessageBytes, cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceSetSymbolicTargetAsync(GitReference reference, ReadOnlyMemory<byte> newTarget, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.SetSymbolicTargetAsync(reference, RefNameKey.From(newTarget), logMessage, cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceSetSymbolicTargetAsync(GitReference reference, ReadOnlyMemory<byte> newTarget, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.SetSymbolicTargetAsync(reference, RefNameKey.From(newTarget), logMessageBytes, cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceRenameAsync(GitReference reference, ReadOnlyMemory<byte> newName, bool force = false, string? logMessage = null, CancellationToken cancellationToken = default)
        => Refs.RenameAsync(reference, RefNameKey.From(newName), force, logMessage, cancellationToken);

    /// <summary>Operates on raw reference-name bytes. Filesystem operations reject names that cannot be represented losslessly.</summary>
    public Task<GitReference> ReferenceRenameAsync(GitReference reference, ReadOnlyMemory<byte> newName, bool force, ReadOnlyMemory<byte>? logMessageBytes, CancellationToken cancellationToken = default)
        => Refs.RenameAsync(reference, RefNameKey.From(newName), force, logMessageBytes, cancellationToken);

    /// <summary>Deletes a reference by raw name. Unrepresentable filesystem names fail without mutation.</summary>
    public Task ReferenceDeleteAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
        => Refs.DeleteAsync(RefNameKey.From(name), cancellationToken);

    /// <summary>Deletes a reference by UTF-8 name.</summary>
    public Task ReferenceDeleteAsync(string name, CancellationToken cancellationToken = default)
        => Refs.DeleteAsync(name, cancellationToken);

    /// <summary>Deletes the exact reference snapshot, checking its old target.</summary>
    public Task ReferenceDeleteAsync(GitReference reference, CancellationToken cancellationToken = default)
        => Refs.DeleteAsync(reference, cancellationToken);

    /// <summary>Enumerates reference names preserving the stored bytes. An empty glob matches every name.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReferenceListNamesBytesAsync(
        ReadOnlyMemory<byte> glob = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (GitReference reference in Refs.ListAsync((RefNameKey?)RefNameKey.From(glob), cancellationToken).ConfigureAwait(false))
        {
            yield return reference.NameBytes;
        }
    }

    /// <summary>Ensures a reflog exists for the raw reference name.</summary>
    public Task ReferenceEnsureLogAsync(ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
        => Refs.EnsureLogAsync(RefNameKey.From(name), cancellationToken);

    /// <summary>Appends a byte-faithful reflog entry for the raw reference name.</summary>
    public Task ReferenceAppendReflogAsync(ReadOnlyMemory<byte> name, GitOid oldId, GitOid newId,
        GitSignature committer, ReadOnlyMemory<byte>? message, CancellationToken cancellationToken = default)
        => Refs.AppendReflogAsync(RefNameKey.From(name), oldId, newId, committer, message, cancellationToken);

}
