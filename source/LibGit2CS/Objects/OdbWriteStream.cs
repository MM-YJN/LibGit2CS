// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Objects;

/// <summary>
/// ODB-level write stream wrapper. Wraps a backend <see cref="ObjectWriteStream"/>
/// and adds hash verification (matches libgit2's <c>git_odb_stream</c> hash_ctx
/// logic in <c>odb.c</c>). Internal — returned by <see cref="GitObjectDb.OpenWriteStreamAsync"/>.
/// </summary>
/// <remarks>
/// The hash is computed over the same content the backend writes: the
/// <c>"&lt;type&gt; &lt;size&gt;\0"</c> header (hashed on construction) plus all
/// body bytes (hashed on each <see cref="Write"/>). At
/// <see cref="FinalizeAsync"/>, the computed hash is compared to the caller's
/// expected OID; a mismatch throws <see cref="GitErrorCode.Mismatch"/>.
/// <para>
/// <b>Async model:</b> <c>Finalize</c> →
/// <see cref="FinalizeAsync"/>; <see cref="Write"/> stays sync (in-memory buffer
/// append only). Disposal is async — awaits the backend stream's
/// <see cref="ObjectWriteStream.DisposeAsync"/> and disposes the hash.
/// </para>
/// </remarks>
internal sealed class OdbWriteStream : ObjectWriteStream
{
    private readonly ObjectWriteStream _backend;
    private readonly GitIncrementalHash _hash;

    /// <summary>
    /// Creates an ODB-level wrapper over <paramref name="backend"/>.
    /// Immediately hashes the object header so the running hash matches
    /// <c>git_odb__hashobj</c>.
    /// </summary>
    public OdbWriteStream(ObjectWriteStream backend, GitObjectType type, long declaredSize, GitHashAlgorithmKind algorithm)
        : base(type, declaredSize)
    {
        _backend = backend;
        _hash = GitIncrementalHash.Create(algorithm);
        Span<byte> header = stackalloc byte[32];
        int headerSize = GitOid.WriteHeader(header, type, declaredSize);
        _hash.AppendData(header.Slice(0, headerSize));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Matches <c>git_odb_stream_write</c> (odb.c:1754-1765): the running byte
    /// count is checked BEFORE the backend write — an overrun fails immediately
    /// at the offending <see cref="Write"/> ("cannot stream_write() - Invalid
    /// length...", GIT_ERROR_ODB).
    /// </remarks>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (ReceivedBytes + buffer.Length > DeclaredSize)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"cannot stream_write() - Invalid length. {DeclaredSize} was expected. The total size of the received chunks amounts to {ReceivedBytes + buffer.Length}.",
                GitErrorCategory.Odb);
        }

        _backend.Write(buffer);
        _hash.AppendData(buffer);
        ReceivedBytes += buffer.Length;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Verifies <see cref="ObjectWriteStream.ReceivedBytes"/> ==
    /// <see cref="ObjectWriteStream.DeclaredSize"/> (C's
    /// <c>git_odb_stream_finalize_write</c> length check, odb.c:1767-1772),
    /// finalizes the hash, compares to <paramref name="expectedOid"/>, and
    /// delegates the atomic persist to the backend.
    /// </remarks>
    public override async Task<GitOid> FinalizeAsync(GitOid expectedOid, CancellationToken cancellationToken)
    {
        if (ReceivedBytes != DeclaredSize)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"cannot stream_finalize_write() - Invalid length. {DeclaredSize} was expected. The total size of the received chunks amounts to {ReceivedBytes}.",
                GitErrorCategory.Odb);
        }

        GitOid computed = _hash.Finalize();
        if (!computed.Equals(expectedOid))
        {
            throw new GitException(
                GitErrorCode.Mismatch,
                $"object hash mismatch: expected {expectedOid}, computed {computed}",
                GitErrorCategory.Odb);
        }

        return await _backend.FinalizeAsync(expectedOid, cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "ObjectWriteStream uses a parameterless DisposeAsyncCore() async-dispose pattern; the base is a no-op (ValueTask.CompletedTask) with nothing to forward.")]
    protected override async Task DisposeAsyncCore()
    {
        await _backend.DisposeAsync().ConfigureAwait(false);
        _hash.Dispose();
    }
}
