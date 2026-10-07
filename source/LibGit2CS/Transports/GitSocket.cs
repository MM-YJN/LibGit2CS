// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Net;
using System.Net.Sockets;

using LibGit2CS.Core;

namespace LibGit2CS.Transports;

/// <summary>
/// Raw TCP socket stream for the <c>git://</c> protocol.
/// Managed port of <c>src/libgit2/streams/socket.c</c>.
/// </summary>
/// <remarks>
/// Wraps <see cref="Socket"/> + <see cref="NetworkStream"/>.
/// The C port has platform-specific connect-with-timeout, non-blocking I/O,
/// and keepalive tuning — all handled by the BCL in managed code.
/// </remarks>
public sealed class GitSocket : IGitSubtransportStream
{
    private Socket? _socket;
    private NetworkStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Creates a socket targeting <paramref name="host"/> on port <paramref name="port"/>.
    /// Does not connect — call <see cref="ConnectAsync"/> to establish the TCP connection.
    /// </summary>
    /// <param name="host">The hostname or IP address.</param>
    /// <param name="port">The TCP port (typically 9418 for git://).</param>
    public GitSocket(string host, int port)
    {
        Host = host;
        Port = port;
    }

    /// <summary>
    /// Connect to the remote endpoint with an optional timeout.
    /// </summary>
    /// <param name="connectTimeoutMs">Connect timeout in milliseconds (0 = no timeout).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ConnectAsync(int connectTimeoutMs, CancellationToken cancellationToken = default)
    {
        // C's
        // git_socket__connect iterates EVERY getaddrinfo result
        // (socket.c:185-224), so an AAAA-only or IPv6-only git:// host is
        // reachable. Resolve all addresses and try each family in turn
        // (mirroring SshTransport.OpenServiceStreamAsync).
        IPAddress[] addresses;
        if (IPAddress.TryParse(Host, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(Host, cancellationToken).ConfigureAwait(false);
        }

        if (addresses.Length == 0)
        {
            throw new GitException(GitErrorCode.Invalid,
                $"could not resolve host '{Host}'", GitErrorCategory.Net);
        }

        Exception? lastConnectError = null;
        foreach (IPAddress address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            candidate.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            try
            {
                var endpoint = new IPEndPoint(address, Port);
                if (connectTimeoutMs > 0)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(connectTimeoutMs);
                    try
                    {
                        await candidate.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        candidate.Dispose();
                        throw new GitException(GitErrorCode.Timeout, $"failed to connect to {Host}: Operation timed out", GitErrorCategory.Net);
                    }
                }
                else
                {
                    await candidate.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                }

                _socket = candidate;
                _stream = new NetworkStream(candidate, ownsSocket: true);
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastConnectError = ex;
                candidate.Dispose();
            }
        }

        throw new GitException(
            GitErrorCode.Error,
            $"failed to connect to {Host}: {lastConnectError?.Message ?? "no addresses"}",
            GitErrorCategory.Net);
    }

    /// <summary>The remote hostname.</summary>
    public string Host { get; }

    /// <summary>The remote TCP port.</summary>
    public int Port { get; }

    /// <inheritdoc/>
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is null)
        {
            throw new GitException(GitErrorCode.Invalid, "socket not connected", GitErrorCategory.Net);
        }

        return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is null)
        {
            throw new GitException(GitErrorCode.Invalid, "socket not connected", GitErrorCategory.Net);
        }

        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Close the socket and release all resources.</summary>
    public void Close()
    {
        _stream?.Dispose();
        _stream = null;
        _socket?.Dispose();
        _socket = null;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        Close();
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
