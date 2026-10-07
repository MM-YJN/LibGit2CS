// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Transports;

/// <summary>
/// Bidirectional stream over a subtransport. Maps to
/// <c>git_smart_subtransport_stream</c> in <c>include/git2/sys/transport.h</c>.
/// </summary>
public interface IGitSubtransportStream : IAsyncDisposable
{
    /// <summary>
    /// Read data from the stream into the buffer.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of bytes read (0 = EOF).</returns>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write data to the stream.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
