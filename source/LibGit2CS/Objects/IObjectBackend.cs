// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;

namespace LibGit2CS.Objects;

/// <summary>
/// Read-side interface for an object database backend. Managed equivalent of
/// libgit2's <c>git_odb_backend</c> vtable (read methods only).
/// </summary>
/// <remarks>
/// <b>Internal</b> — backends are implementation details. <see cref="GitObjectDb"/>
/// is the public API; it aggregates one or more backends. The C
/// <c>git_odb_init_backend</c> validators become interface contract checks
/// (debug assertions). Write methods are on <see cref="IObjectWriteBackend"/>;
/// pack writing is provided by <c>MemPackBackend</c>.
/// <para>
/// <b>Async model:</b> all methods are <c>*Async</c> returning
/// <see cref="ValueTask"/>/<see cref="ValueTask{TResult}"/>/<see cref="IAsyncEnumerable{T}"/>.
/// <see cref="GitOid"/> is passed by value (C# forbids <c>in</c> on async
/// method parameters — the 20-byte struct copy is negligible). The
/// interface is <see cref="IAsyncDisposable"/>; backends
/// that perform IO on disposal (<c>LooseObjectBackend</c>, <c>PackObjectBackend</c>)
/// await their cleanup, while in-memory backends (<c>MemPackBackend</c>)
/// return <see cref="ValueTask.CompletedTask"/>.
/// </para>
/// </remarks>
internal interface IObjectBackend : IAsyncDisposable
{
    /// <summary>
    /// Reads the full object (header + body) for <paramref name="id"/>.
    /// Matches <c>git_odb_backend::read</c>.
    /// </summary>
    /// <returns>The raw object data, or null if not found.</returns>
    /// <remarks>
    /// Returned data must remain stable for as long as a consumer retains it.
    /// Backends must not mutate or recycle the array, including on reset or disposal.
    /// Parsed objects and pack delta windows may share this storage without copying.
    /// </remarks>
    Task<RawObjectData?> ReadAsync(GitOid id, CancellationToken cancellationToken);

    /// <summary>
    /// Reads only the header (type + size) for <paramref name="id"/>.
    /// Matches <c>git_odb_backend::read_header</c>.
    /// </summary>
    /// <returns>The header, or null if not found.</returns>
    Task<GitObjectHeader?> ReadHeaderAsync(GitOid id, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether an object with the given <paramref name="id"/> exists.
    /// Matches <c>git_odb_backend::exists</c>. Synchronous: all backends
    /// implement this as a sync file-stat or in-memory dictionary lookup that
    /// never yields.
    /// </summary>
    bool Exists(GitOid id);

    /// <summary>
    /// Finds the full OID for an abbreviated prefix. Matches
    /// <c>git_odb_backend::exists_prefix</c>.
    /// </summary>
    /// <param name="prefix">The abbreviated OID (at least 4 hex chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A tuple of (found, oid). <c>found</c> is <c>true</c> if exactly one
    /// object matches; <c>false</c> if none match. Throws
    /// <see cref="GitException"/> with <see cref="GitErrorCode.Ambiguous"/> if
    /// multiple objects match. The <c>out</c> parameter of the sync era is
    /// replaced by a tuple return — <c>out</c> parameters are unidiomatic in
    /// async methods.
    /// </returns>
    ValueTask<(bool Found, GitOid Oid)> ExistsPrefixAsync(GitOid prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates all OIDs in this backend. Matches
    /// <c>git_odb_backend::foreach</c>.
    /// </summary>
    IAsyncEnumerable<GitOid> EnumerateAsync(CancellationToken cancellationToken);
}
