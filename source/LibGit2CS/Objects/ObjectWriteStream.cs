// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Objects;

/// <summary>
/// Streaming object write handle. Managed equivalent of libgit2's
/// <c>git_odb_stream</c> (write side). Internal — callers use
/// <see cref="GitObjectDb.OpenWriteStreamAsync"/> which returns this type.
/// </summary>
/// <remarks>
/// Usage: <see cref="Write"/> body bytes in chunks, then
/// <see cref="FinalizeAsync"/> with the expected OID to atomically persist. The
/// backend compresses (zlib) and writes the <c>"&lt;type&gt; &lt;size&gt;\0"</c>
/// header before the first body byte. <see cref="FinalizeAsync"/> verifies the
/// expected OID, performs the atomic temp-rename, and returns the OID.
/// <para>
/// <b>Async model:</b> the base is <see cref="IAsyncDisposable"/>
/// (cascades to <c>OdbWriteStream</c> + <c>LooseWriteStream</c> +
/// <c>MemPackWriteStream</c>). The sync <see cref="Write"/> API stays (it only
/// appends to an in-memory buffer — no IO); only
/// <see cref="FinalizeAsync"/> and disposal are async (the atomic persist + the
/// backend's <see cref="IAsyncDisposable"/> disposal).
/// </para>
/// </remarks>
internal abstract class ObjectWriteStream : IAsyncDisposable
{
    private bool _disposed;

    /// <summary>The object type being written.</summary>
    public GitObjectType Type { get; }

    /// <summary>The declared body size (from <see cref="GitObjectDb.OpenWriteStreamAsync"/>).</summary>
    public long DeclaredSize { get; }

    /// <summary>Bytes received so far via <see cref="Write"/>.</summary>
    public long ReceivedBytes { get; protected set; }

    /// <summary>Creates a stream for the given type and declared size.</summary>
    protected ObjectWriteStream(GitObjectType type, long declaredSize)
    {
        Type = type;
        DeclaredSize = declaredSize;
    }

    /// <summary>
    /// Writes body bytes to the backend. Matches <c>git_odb_stream::write</c>.
    /// The backend compresses and persists; no header is written by the caller.
    /// Stays synchronous — appends to an in-memory buffer only (no IO).
    /// </summary>
    public abstract void Write(ReadOnlySpan<byte> buffer);

    /// <summary>
    /// Finalizes the write: verifies <paramref name="expectedOid"/>, checks
    /// <see cref="ReceivedBytes"/> == <see cref="DeclaredSize"/>, performs the
    /// atomic persist (temp-rename), and returns the OID. Matches
    /// <c>git_odb_stream::finalize_write</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Mismatch"/> if <see cref="ReceivedBytes"/> !=
    /// <see cref="DeclaredSize"/>.
    /// </exception>
    public abstract Task<GitOid> FinalizeAsync(GitOid expectedOid, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Override to release resources asynchronously (e.g. close a backend stream).</summary>
    protected virtual Task DisposeAsyncCore() => Task.CompletedTask;
}
