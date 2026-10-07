// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Transports;

/// <summary>
/// A <see cref="Stream"/> adapter that writes data to an
/// <see cref="IGitSubtransportStream"/>. Used by <see cref="GitSmartProtocol.PushAsync"/>
/// to stream pack data directly to the transport without buffering the entire
/// pack in memory.
/// </summary>
/// <remarks>
/// This is a write-only stream. Read and seek operations throw
/// <see cref="NotSupportedException"/>. The adapter tracks the total number of
/// bytes written for progress reporting. Only the async <see cref="WriteAsync"/>
/// override delegates to the underlying <see cref="IGitSubtransportStream.WriteAsync"/>
/// — the sync <see cref="Write"/> overrides are not provided because
/// <see cref="IGitSubtransportStream"/> is async-only.
/// </remarks>
internal sealed class SubtransportStreamAdapter : Stream
{
    private readonly IGitSubtransportStream _target;
    private long _bytesWritten;

    internal SubtransportStreamAdapter(IGitSubtransportStream target)
    {
        _target = target;
    }

    /// <summary>Total bytes written to the underlying transport stream.</summary>
    internal long BytesWritten => _bytesWritten;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _bytesWritten;
    public override long Position
    {
        get => _bytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        // The transport stream has no flush — data is sent on Write.
    }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException(
            "SubtransportStreamAdapter is async-only. Use WriteAsync — ISubtransportStream.Write is gone.");

    /// <summary>
    /// Write data to the underlying transport stream asynchronously.
    /// This is the only write path — <see cref="LibGit2CS.Pack.GitPackWriter.WriteAsync"/> calls
    /// <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.
    /// The return type is <see cref="ValueTask"/> because the BCL
    /// <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
    /// override contract mandates it.
    /// </summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await _target.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _bytesWritten += buffer.Length;
    }
}
