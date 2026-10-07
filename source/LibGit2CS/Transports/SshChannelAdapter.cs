// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibSsh2CS;

namespace LibGit2CS.Transports;

/// <summary>
/// Production <see cref="ISshChannel"/> over a real <see cref="SshChannel"/>.
/// Thin pass-through to LibSsh2CS's public API.
/// </summary>
internal sealed class SshChannelAdapter : ISshChannel
{
    private readonly SshChannel _channel;

    public SshChannelAdapter(SshChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
    }

    /// <inheritdoc/>
    public Task ExecAsync(string command, CancellationToken cancellationToken)
        => _channel.ExecAsync(command, cancellationToken);

    /// <inheritdoc/>
    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _channel.ReadAsync(buffer, cancellationToken).AsTask();

    /// <inheritdoc/>
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => _channel.WriteAsync(data, cancellationToken);

    /// <inheritdoc/>
    public async Task SendEofAsync(CancellationToken cancellationToken)
        => await _channel.SendEofAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<int> ReadStderrAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _channel.ReadStderrAsync(buffer, cancellationToken).AsTask();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
        => _channel.DisposeAsync();
}
