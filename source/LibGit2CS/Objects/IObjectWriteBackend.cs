// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Objects;

/// <summary>
/// Write-side interface for an object database backend. Managed equivalent of
/// the write methods in libgit2's <c>git_odb_backend</c> vtable
/// (<c>write</c>, <c>writestream</c>, <c>freshen</c>).
/// </summary>
/// <remarks>
/// <b>Internal</b> — backends are implementation details. <see cref="GitObjectDb"/>
/// is the public API; it dispatches writes to the first backend that implements
/// this interface. Read-only backends (e.g. <see cref="PackObjectBackend"/>) do
/// not implement this interface; pack writing is provided by <c>MemPackBackend</c>.
/// <para>
/// Write atomicity: <see cref="WriteAsync"/> and <see cref="OpenWriteStreamAsync"/>
/// must persist atomically (temp file + rename) so that a crash never leaves a
/// partially-written object. <see cref="Freshen"/> touches an existing
/// object's mtime to prevent garbage collection.
/// </para>
/// <para>
/// <b>Async model:</b> <see cref="GitOid"/> is passed by value
/// (C# forbids <c>in</c> on async). <see cref="ReadOnlySpan{T}"/> becomes
/// <see cref="ReadOnlyMemory{T}"/> (spans are not allowed in async method
/// signatures). The streaming write API (<see cref="ObjectWriteStream.Write"/>
/// on the returned stream) stays sync — it only appends to an in-memory buffer
/// (no IO); only <see cref="ObjectWriteStream.FinalizeAsync"/>
/// and disposal are async (the atomic persist + cleanup).
/// </para>
/// </remarks>
internal interface IObjectWriteBackend
{
    /// <summary>
    /// Writes a complete object to the backend. Matches
    /// <c>git_odb_backend::write</c>. The <paramref name="oid"/> is precomputed
    /// by <see cref="GitObjectDb"/> (the caller hashes the header+body). The backend
    /// must not re-hash; it persists <paramref name="body"/> under the object
    /// path derived from <paramref name="oid"/>.
    /// </summary>
    /// <param name="oid">The precomputed OID (hash of header+body).</param>
    /// <param name="type">The object type.</param>
    /// <param name="body">The raw object body (without the type/size header).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(GitOid oid, GitObjectType type, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a streaming write. Matches <c>git_odb_backend::writestream</c>.
    /// The returned stream accepts body bytes via
    /// <see cref="ObjectWriteStream.Write"/> and is finalized by
    /// <see cref="ObjectWriteStream.FinalizeAsync"/>. The backend writes the
    /// <c>"&lt;type&gt; &lt;size&gt;\0"</c> header internally (compressed) before
    /// any body bytes.
    /// </summary>
    /// <param name="type">The object type.</param>
    /// <param name="declaredSize">The expected body size in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A write stream.</returns>
    Task<ObjectWriteStream> OpenWriteStreamAsync(GitObjectType type, long declaredSize, CancellationToken cancellationToken);

    /// <summary>
    /// Touches an existing object's mtime to prevent garbage collection.
    /// Matches <c>git_odb_backend::freshen</c>. Returns true if the object
    /// exists and was freshened; false if the object does not exist.
    /// Synchronous: implemented as a sync mtime stat / in-memory dictionary
    /// lookup that never yields.
    /// </summary>
    bool Freshen(GitOid oid);
}
