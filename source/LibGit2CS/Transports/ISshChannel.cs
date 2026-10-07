// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Transports;

/// <summary>
/// Internal seam over the subset of <c>LibSsh2CS.SshChannel</c> that
/// <see cref="SshStream"/> consumes. The production implementation
/// <see cref="SshChannelAdapter"/> delegates to LibSsh2CS's public API; tests
/// inject fakes that script exec/read/write results.
/// </summary>
internal interface ISshChannel
{
    /// <summary>
    /// Sends an <c>exec</c> channel request (parity with
    /// <c>LibSsh2CS.SshChannel.ExecAsync</c>). The command is the remote
    /// process to run (e.g. <c>git-upload-pack '/path/to/repo'</c>).
    /// </summary>
    Task ExecAsync(string command, CancellationToken cancellationToken);

    /// <summary>
    /// Reads stdout data into <paramref name="buffer"/>. Returns 0 on EOF
    /// (parity with <c>SshChannel.ReadAsync</c>).
    /// </summary>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Writes stdin data (parity with <c>SshChannel.WriteAsync</c>).
    /// </summary>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Signals end-of-file on the write side of the channel (parity with
    /// <c>SshChannel.SendEofAsync</c>). Used by the push protocol after the
    /// pack is written so the server's <c>git-receive-pack</c> knows the
    /// pack is complete. The read side stays open for the report-status
    /// response.
    /// </summary>
    Task SendEofAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads stderr data into <paramref name="buffer"/>. Used by
    /// <see cref="SshStream.ReadAsync"/> on EOF to surface server error
    /// messages (parity <c>ssh_stream_read</c> lines 112-150).
    /// </summary>
    Task<int> ReadStderrAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Disposes the channel — sends EOF (if not already sent) + CLOSE, waits
    /// for peer CLOSE, unregisters (parity with
    /// <c>SshChannel.DisposeAsync</c>:
    /// caller must NOT call <c>SendEofAsync</c> separately).
    /// </summary>
    ValueTask DisposeAsync();
}
