// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.Objects;

/// <summary>
/// Streaming blob writer. Managed port of libgit2's <c>blob_writestream</c>
/// (<c>src/libgit2/blob.c:302-393</c>) + <c>git_blob_create_from_stream</c>/
/// <c>git_blob_create_from_stream_commit</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Departure from C (BCL substitution):</b> the C
/// <c>blob_writestream</c> wraps a <c>git_filebuf</c> temp file in
/// <c>.git/objects/streamed/</c>. The C# port uses an in-memory
/// <see cref="MemoryStream"/> — no temp file management, no filesystem
/// cleanup, no partial-state recovery. The filter pipeline produces the same
/// output bytes either way; only the staging mechanism differs.
/// </para>
/// <para>
/// <b>Filter application:</b> <see cref="Commit"/> applies
/// clean filters (<c>FilterMode.ToOdb</c>) to the accumulated content before
/// hashing, matching <c>git_blob__create_from_paths</c> as called by the C
/// <c>git_blob_create_from_stream_commit</c> (<c>blob.c:386-387</c>). The
/// filter pipeline order is handled by <see cref="GitFilterList"/> (CRLF → ident →
/// custom) — no need to re-verify.
/// </para>
/// <para>
/// If <c>hintPath</c> was provided at construction, filters are
/// loaded for that path; otherwise (path outside the workdir, or no hint) no
/// filters are applied, matching C's <c>!!stream->hintpath</c> guard at
/// <c>blob.c:387</c>.
/// </para>
/// </remarks>
public sealed class GitBlobWriteStream : Stream
{
    private readonly GitRepository _repo;
    private readonly string? _hintPath;
    private readonly MemoryStream _buffer = new();
    private bool _committed;
    private bool _disposed;

    internal GitBlobWriteStream(GitRepository repo, string? hintPath)
    {
        _repo = repo;
        _hintPath = hintPath;
    }

    /// <summary>
    /// Finalizes the blob: applies clean filters (if a hint path was provided)
    /// and writes the blob to the object database. Matches
    /// <c>git_blob_create_from_stream_commit</c> (<c>blob.c:373-393</c>).
    /// </summary>
    /// <returns>The OID of the written blob.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="CommitAsync"/> was already called on this stream.
    /// </exception>
    public async Task<GitOid> CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException(
                "BlobWriteStream.Commit has already been called on this stream");
        }

        _committed = true;
        // The private stream owns this storage and writes are disabled above.
        // Keep only the written bytes, excluding spare capacity, through the awaited write.
        ReadOnlyMemory<byte> content = _buffer.GetBuffer().AsMemory(0, checked((int)_buffer.Length));

        // Apply clean filters if a hint path was provided. Matches
        // blob.c:386-387 — the `!!stream->hintpath` guard means filters are
        // only applied when a hint path is present.
        if (_hintPath is not null)
        {
            GitFilterList? filters = await GitFilterList.LoadAsync(_repo, _hintPath, null, GitFilterMode.ToOdb, GitFilterListFlags.None, attrCommitId: null, cancellationToken).ConfigureAwait(false);
            if (filters is not null)
            {
                content = await filters.ApplyToBufferAsync(content, cancellationToken).ConfigureAwait(false);
                filters.Dispose();
            }
        }

        return await _repo.Objects.WriteAsync(GitObjectType.Blob, content, cancellationToken).ConfigureAwait(false);
    }

    // ━━ Stream overrides ━━

    /// <inheritdoc/>
    public override bool CanRead => !_disposed && !_committed;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed && !_committed;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed && !_committed;

    /// <inheritdoc/>
    public override long Length => _buffer.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _buffer.Position;
        set => throw new NotSupportedException("BlobWriteStream is write-only");
    }

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("BlobWriteStream is write-only");

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException("BlobWriteStream is append-only");

    /// <inheritdoc/>
    public override void SetLength(long value)
        => throw new NotSupportedException("BlobWriteStream is append-only");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException(
                "Cannot write to a BlobWriteStream after Commit");
        }

        _buffer.Write(buffer, offset, count);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _buffer.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
