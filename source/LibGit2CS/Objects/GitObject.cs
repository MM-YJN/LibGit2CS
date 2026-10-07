// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Repository;

namespace LibGit2CS.Objects;

/// <summary>
/// Abstract base for all git objects (commit, tree, blob, tag). Managed port of
/// libgit2's <c>src/libgit2/object.c</c> + <c>object_api.c</c> (read side).
/// </summary>
/// <remarks>
/// <para>
/// In libgit2, <c>git_object</c> is an abstract base with type-specific subclasses
/// (<c>git_commit</c>, <c>git_tree</c>, <c>git_blob</c>, <c>git_tag</c>). The
/// managed port mirrors this: <see cref="GitObject"/> is the abstract base;
/// concrete types (<see cref="Commit"/>, <see cref="GitTree"/>, <see cref="GitBlob"/>, <see cref="GitTag"/>) live here.
/// </para>
/// <para>
/// <see cref="GitObjectDb.LookupAsync"/> dispatches on the parsed
/// <see cref="Type"/> to call the appropriate concrete parser via the static
/// <see cref="Parse"/> factory. Callers can also use the generic
/// <see cref="GitObjectDb.LookupAsync{T}"/> overload to get a typed reference directly.
/// </para>
/// </remarks>
[SuppressMessage("Usage", "CA1063:Implement IDisposable Correctly", Justification = "The Dispose pattern here matches the libgit2 object lifetime: a single Dispose() with a virtual DisposeCore(bool) hook. The analyzer's prescribed pattern (finalizer + Dispose(bool) overload) is unnecessary — there are no unmanaged resources and subclasses do not need to add cleanup.")]
public abstract class GitObject(GitRepository? owner, GitOid id, GitObjectType type, long size, ReadOnlyMemory<byte> raw) : IDisposable
{
    private bool _disposed;

    /// <summary>The repository that owns this object (null for standalone ODB lookups).</summary>
    public GitRepository? Owner { get; } = owner;

    /// <summary>The OID (hash) of this object.</summary>
    public GitOid Id { get; } = id;

    /// <summary>The object type (commit, tree, blob, tag).</summary>
    public GitObjectType Type { get; } = type;

    /// <summary>The size of the raw object body in bytes.</summary>
    public long Size { get; } = size;

    /// <summary>The raw (unparsed) object body bytes.</summary>
    public ReadOnlyMemory<byte> Raw { get; } = raw;

    /// <summary>
    /// Factory that dispatches on <paramref name="type"/> to construct the
    /// appropriate concrete <see cref="GitObject"/> subclass from raw bytes.
    /// Matches libgit2's <c>git_object__from_raw</c> dispatch table.
    /// </summary>
    /// <param name="owner">Owning repository (may be null for standalone ODB lookups).</param>
    /// <param name="id">The OID of the object.</param>
    /// <param name="type">The object type.</param>
    /// <param name="raw">The raw object body bytes (decompressed, without the type/size header).</param>
    /// <param name="algorithm">Hash algorithm (for OID line widths during parsing).</param>
    /// <returns>A concrete <see cref="GitObject"/> (<see cref="Commit"/>/<see cref="GitTree"/>/<see cref="GitBlob"/>/<see cref="GitTag"/>).</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> if <paramref name="type"/> is not a parseable object type.
    /// </exception>
    internal static GitObject Parse(GitRepository? owner, GitOid id, GitObjectType type, ReadOnlyMemory<byte> raw, GitHashAlgorithmKind algorithm)
        => type switch
        {
            GitObjectType.Commit => Commit.Parse(owner, id, raw, algorithm),
            GitObjectType.Tree => GitTree.Parse(owner, id, raw, algorithm),
            GitObjectType.Blob => GitBlob.Parse(owner, id, raw),
            GitObjectType.Tag => GitTag.Parse(owner, id, raw, algorithm),
            _ => throw new GitException(
                GitErrorCode.Invalid,
                $"cannot parse object of type {type}",
                GitErrorCategory.Object),
        };

    /// <summary>
    /// Peels this object to the given target type. Matches
    /// <c>git_object_peel</c> (object.c:427-466): a tag is dereferenced to
    /// its target, a commit to its tree, and the chain is walked until the
    /// target type is reached.
    /// </summary>
    /// <typeparam name="T">The target type (Commit, Tree, Blob, Tag).</typeparam>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peeled object, or this object if already of the target type.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.InvalidSpec"/> for the cross-type combinations C
    /// rejects in <c>check_type_combination</c> (blob/tree → anything else,
    /// commit → non-tree); <see cref="GitErrorCode.Peel"/> if the chain ends
    /// at a different type; <see cref="GitErrorCode.NotFound"/> if a chain
    /// link is missing.
    /// </exception>
    /// <remarks>
    /// <b>Async model:</b> the base virtual method and the
    /// <see cref="Commit"/>/<see cref="GitTag"/> overrides became async because
    /// they call <see cref="GitObjectDb.LookupAsync"/> (which is now async). The
    /// base implementation completes synchronously via
    /// <see cref="ValueTask.FromResult"/> when <c>this is T</c>.
    /// </remarks>
    public virtual async Task<T> PeelAsync<T>(CancellationToken cancellationToken = default) where T : GitObject
    {
        return await PeelCoreAsync<T>(this, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared <c>git_object_peel</c> implementation (object.c:427-466).
    /// </summary>
    internal static async Task<T> PeelCoreAsync<T>(GitObject start, CancellationToken cancellationToken) where T : GitObject
    {
        // check_type_combination (object.c:396-420).
        if (start is T typed)
        {
            return typed;
        }

        if (start is GitBlob or GitTree)
        {
            // C (object.c:381-394): a blob or tree can never be peeled to anything but themselves — peel_error sets GIT_ERROR_OBJECT (category Object), code
            // stays GIT_EINVALIDSPEC.
            throw PeelError<T>(start);
        }

        if (start is Commit && typeof(T) != typeof(GitTree))
        {
            // A commit can only be peeled to a tree.
            throw PeelError<T>(start);
        }

        GitObject source = start;
        for (int depth = 0; depth < MaxPeelDepth; depth++)
        {
            GitObject? deref = await DereferenceObjectAsync(source, cancellationToken).ConfigureAwait(false);
            if (deref is null)
            {
                // dereference_object returns GIT_EPEEL for tree/blob sources
                // (object.c:372-374) — the chain ends at the wrong type.
                throw new GitException(
                    GitErrorCode.Peel,
                    $"the git_object of id '{start.Id}' can not be successfully peeled into a {typeof(T).Name}",
                    GitErrorCategory.Object);
            }

            if (deref is T target)
            {
                return target;
            }

            source = deref;
        }

        // C loops forever on tag cycles; the port caps the chain.
        throw new GitException(
            GitErrorCode.Peel,
            $"cannot peel {start.Type} to {typeof(T).Name} (chain too deep)",
            GitErrorCategory.Object);
    }

    /// <summary>
    /// Matches C's <c>peel_error</c> (object.c:381-394): the message names the
    /// TARGET type and the error category is GIT_ERROR_OBJECT.
    /// </summary>
    private static GitException PeelError<T>(GitObject source)
        where T : GitObject
        => new(
            GitErrorCode.InvalidSpec,
            $"the git_object of id '{source.Id}' can not be successfully peeled into a {typeof(T).Name} (git_object_t={(int)TargetTypeOf<T>()}).",
            GitErrorCategory.Object);

    /// <summary>Maps the peel target type argument to its <see cref="GitObjectType"/>.</summary>
    private static GitObjectType TargetTypeOf<T>()
        where T : GitObject
        => typeof(T) == typeof(Commit) ? GitObjectType.Commit
         : typeof(T) == typeof(GitTree) ? GitObjectType.Tree
         : typeof(T) == typeof(GitBlob) ? GitObjectType.Blob
         : GitObjectType.Tag;

    /// <summary>
    /// Matches <c>dereference_object</c> (object.c:361-379): a tag yields its
    /// target, a commit its tree, anything else fails the peel.
    /// </summary>
    private static async Task<GitObject?> DereferenceObjectAsync(GitObject obj, CancellationToken cancellationToken)
    {
        return obj switch
        {
            GitTag tag => tag.Owner is null
                ? throw new GitException(GitErrorCode.Peel, "tag has no owning repository; cannot dereference", GitErrorCategory.Object)
                : await tag.Owner.Objects.LookupAsync(tag.Target, cancellationToken).ConfigureAwait(false)
                    ?? throw new GitException(GitErrorCode.NotFound, $"tag target {tag.Target} not found", GitErrorCategory.Object),
            Commit commit => commit.Owner is null
                ? throw new GitException(GitErrorCode.Peel, "commit has no owning repository; cannot cascade to tree", GitErrorCategory.Object)
                : await commit.Owner.Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false)
                    ?? throw new GitException(GitErrorCode.NotFound, $"tree {commit.Tree} not found", GitErrorCategory.Object),
            _ => null,
        };
    }

    /// <summary>Depth cap for peel chains (C has no limit; tags cycles loop forever).</summary>
    private const int MaxPeelDepth = 50;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCore(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Override to release resources. Always check <paramref name="disposing"/>.</summary>
    protected virtual void DisposeCore(bool disposing)
    {
    }
}
