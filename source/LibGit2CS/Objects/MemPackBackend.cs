// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Pack;

namespace LibGit2CS.Objects;

/// <summary>
/// In-memory pack backend: accumulates objects in memory without writing
/// to disk. Managed port of <c>src/libgit2/odb_mempack.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implements both <see cref="IObjectBackend"/> (read) and
/// <see cref="IObjectWriteBackend"/> (write). Objects are stored in a
/// dictionary keyed by OID. This backend is used during pack assembly
/// to buffer objects before writing them to a <c>.pack</c> file.
/// </para>
/// <para>
/// <b>Not persistent.</b> Objects in a <see cref="MemPackBackend"/> are lost
/// when the backend is disposed or <see cref="Reset"/> is called. To persist,
/// call <see cref="DumpAsync"/> to write all objects to a <see cref="GitPackWriter"/>.
/// </para>
/// <para>
/// <b>Async model:</b> all methods are async but complete
/// synchronously — the data is in-memory. <see cref="IObjectBackend.EnumerateAsync"/> uses
/// <see cref="AsyncFileIO.ToAsyncEnumerableAsync{T}"/> to adapt the
/// materialized <c>Dictionary{GitOid,}.Keys</c> collection.
/// </para>
/// </remarks>
internal sealed class MemPackBackend : IObjectBackend, IObjectWriteBackend
{
    private readonly Dictionary<GitOid, (GitObjectType Type, byte[] Data)> _objects = [];
    private readonly List<GitOid> _commits = [];
    private bool _disposed;

    /// <summary>Creates a new in-memory pack backend.</summary>
    internal MemPackBackend()
    {
    }

    /// <summary>The number of objects stored.</summary>
    internal int Count => _objects.Count;

    /// <inheritdoc/>
    Task<RawObjectData?> IObjectBackend.ReadAsync(GitOid id, CancellationToken cancellationToken)
    {
        if (_objects.TryGetValue(id, out (GitObjectType Type, byte[] Data) entry))
        {
            return Task.FromResult<RawObjectData?>(new RawObjectData(entry.Type, entry.Data.Length, entry.Data));
        }

        return Task.FromResult<RawObjectData?>(null);
    }

    /// <inheritdoc/>
    Task<GitObjectHeader?> IObjectBackend.ReadHeaderAsync(GitOid id, CancellationToken cancellationToken)
    {
        if (_objects.TryGetValue(id, out (GitObjectType Type, byte[] Data) entry))
        {
            return Task.FromResult<GitObjectHeader?>(new GitObjectHeader(entry.Type, entry.Data.Length));
        }

        return Task.FromResult<GitObjectHeader?>(null);
    }

    /// <inheritdoc/>
    bool IObjectBackend.Exists(GitOid id)
        => _objects.ContainsKey(id);

    /// <inheritdoc/>
    ValueTask<(bool Found, GitOid Oid)> IObjectBackend.ExistsPrefixAsync(GitOid prefix, CancellationToken cancellationToken)
    {
        // C (odb_mempack.c:193-211): git_mempack_new registers no exists_prefix — odb_exists_prefix_1 skips the backend entirely (odb.c:1075), so mempack
        // objects never match an abbreviated-prefix lookup (even a full-length
        // one), so the lookup always reports not-found.
        return ValueTask.FromResult<(bool, GitOid)>((false, default));
    }

    /// <inheritdoc/>
    IAsyncEnumerable<GitOid> IObjectBackend.EnumerateAsync(CancellationToken cancellationToken)
        => AsyncFileIO.ToAsyncEnumerableAsync(_objects.Keys, cancellationToken);

    /// <inheritdoc/>
    Task IObjectWriteBackend.WriteAsync(GitOid oid, GitObjectType type, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (_objects.ContainsKey(oid))
        {
            return Task.CompletedTask;
        }

        byte[] data = body.ToArray();
        _objects[oid] = (type, data);

        if (type == GitObjectType.Commit)
        {
            _commits.Add(oid);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    Task<ObjectWriteStream> IObjectWriteBackend.OpenWriteStreamAsync(GitObjectType type, long declaredSize, CancellationToken cancellationToken)
        => Task.FromResult<ObjectWriteStream>(new MemPackWriteStream(this, type, declaredSize));

    /// <inheritdoc/>
    bool IObjectWriteBackend.Freshen(GitOid oid)
        => _objects.ContainsKey(oid);

    /// <summary>
    /// Inserts all stored commit objects (with their trees) into the given
    /// pack builder. Matches <c>git_mempack__dump</c> (odb_mempack.c:108-136),
    /// which calls <c>git_packbuilder_insert_commit</c> per commit — pulling
    /// in the commit's tree and every tree/blob beneath it.
    /// </summary>
    internal async Task DumpAsync(GitPackWriter pb, CancellationToken cancellationToken)
    {
        foreach (GitOid oid in _commits)
        {
            await pb.InsertCommitAsync(oid, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears all stored objects. Matches <c>git_mempack_reset</c>.
    /// </summary>
    internal void Reset()
    {
        _objects.Clear();
        _commits.Clear();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _objects.Clear();
        _commits.Clear();
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Streaming write handle for the mempack backend. Buffers body bytes
    /// in memory, then writes the complete object on finalize.
    /// </summary>
    private sealed class MemPackWriteStream : ObjectWriteStream
    {
        private readonly MemPackBackend _backend;
        private readonly MemoryStream _buffer = new();

        internal MemPackWriteStream(MemPackBackend backend, GitObjectType type, long declaredSize)
            : base(type, declaredSize)
        {
            _backend = backend;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _buffer.Write(buffer);
            ReceivedBytes += buffer.Length;
        }

        public override async Task<GitOid> FinalizeAsync(GitOid expectedOid, CancellationToken cancellationToken)
        {
            if (ReceivedBytes != DeclaredSize)
            {
                throw new GitException(GitErrorCode.Mismatch, "object size mismatch", GitErrorCategory.Odb);
            }

            byte[] data = _buffer.ToArray();
            await ((IObjectWriteBackend)_backend).WriteAsync(expectedOid, Type, data, cancellationToken).ConfigureAwait(false);
            return expectedOid;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "ObjectWriteStream uses a parameterless DisposeAsyncCore() async-dispose pattern; the base is a no-op (Task.CompletedTask) with nothing to forward.")]
        protected override async Task DisposeAsyncCore()
        {
            await _buffer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
