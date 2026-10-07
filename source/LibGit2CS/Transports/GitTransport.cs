// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Remote;

namespace LibGit2CS.Transports;

/// <summary>
/// Smart subtransport for the <c>git://</c> protocol over TCP.
/// Managed port of <c>git_subtransport</c> + service functions in
/// <c>src/libgit2/transports/git.c</c>.
/// </summary>
/// <remarks>
/// Connects to port 9418 (default) via <see cref="GitSocket"/>. Stateful
/// (<c>IsRpc=false</c>): a single TCP connection is created during the
/// <c>*Ls</c> service call and reused for the subsequent <c>UploadPack</c>/
/// <c>ReceivePack</c> call. The git protocol command
/// (<c>git-upload-pack /path\0host=host\0</c>) is sent lazily on first I/O
/// by <see cref="GitStream"/>.
/// </remarks>
public sealed class GitTransport : IGitSubtransport, IAsyncDisposable
{
    private const int DefaultPort = 9418;
    private const string Scheme = "git://";

    private GitStream? _currentStream;
    private bool _disposed;

    /// <summary>Creates a new git:// subtransport.</summary>
    internal GitTransport()
    {
    }

    /// <inheritdoc/>
    public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        ObjectDisposedException.ThrowIf(_disposed, this);

        return service switch
        {
            GitSmartService.UploadPackLs => OpenServiceStreamAsync(url, "git-upload-pack", cancellationToken),
            GitSmartService.ReceivePackLs => OpenServiceStreamAsync(url, "git-receive-pack", cancellationToken),
            GitSmartService.UploadPack => Task.FromResult<IGitSubtransportStream>(ReuseStream()),
            GitSmartService.ReceivePack => Task.FromResult<IGitSubtransportStream>(ReuseStream()),
            _ => throw new GitException(GitErrorCode.Invalid, $"unknown service: {service}", GitErrorCategory.Net),
        };
    }

    /// <summary>
    /// Open a new TCP connection and create a stream for the given service.
    /// Matches <c>_git_uploadpack_ls</c> / <c>_git_receivepack_ls</c> in <c>git.c:187-279</c>.
    /// </summary>
    private async Task<IGitSubtransportStream> OpenServiceStreamAsync(string url, string cmd, CancellationToken cancellationToken)
    {
        // Parse: git://host[:port]/path
        string rest = url.StartsWith(Scheme, StringComparison.Ordinal)
            ? url[Scheme.Length..]
            : url;

        // Extract host:port and path
        string host;
        int port = DefaultPort;
        string pathPart;

        int slashIdx = rest.IndexOf('/', StringComparison.Ordinal);
        int colonIdx = rest.IndexOf(':', StringComparison.Ordinal);

        if (colonIdx >= 0 && (slashIdx < 0 || colonIdx < slashIdx))
        {
            // host:port/path
            host = rest[..colonIdx];
            int portEnd = slashIdx >= 0 ? slashIdx : rest.Length;
            if (!int.TryParse(rest[(colonIdx + 1)..portEnd], out port))
            {
                throw new GitException(GitErrorCode.Invalid, $"invalid port in git:// URL: {url}", GitErrorCategory.Net);
            }

            pathPart = slashIdx >= 0 ? rest[slashIdx..] : "/";
        }
        else if (slashIdx >= 0)
        {
            // host/path
            host = rest[..slashIdx];
            pathPart = rest[slashIdx..];
        }
        else
        {
            throw new GitException(GitErrorCode.Invalid, "malformed git:// URL", GitErrorCategory.Net);
        }

        if (host.Length == 0)
        {
            throw new GitException(GitErrorCode.Invalid, "missing host in git:// URL", GitErrorCategory.Net);
        }

        if (pathPart.Length == 0)
        {
            pathPart = "/";
        }

        // Close any existing stream (shouldn't have one for a fresh LS call)
        if (_currentStream is not null)
        {
            await _currentStream.DisposeAsync().ConfigureAwait(false);
        }

        var socket = new GitSocket(host, port);
        await socket.ConnectAsync(connectTimeoutMs: 0, cancellationToken).ConfigureAwait(false);
        var stream = new GitStream(socket, cmd, pathPart, host);
        _currentStream = stream;

        return stream;
    }

    /// <summary>
    /// Return the existing stream for the non-LS service call.
    /// Matches <c>_git_uploadpack</c> / <c>_git_receivepack</c> in <c>git.c:229-295</c>.
    /// </summary>
    private GitStream ReuseStream()
    {
        if (_currentStream is null)
        {
            throw new GitException(GitErrorCode.Invalid, "must call LS service before action service", GitErrorCategory.Net);
        }

        return _currentStream;
    }

    /// <inheritdoc/>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_currentStream is not null)
        {
            await _currentStream.DisposeAsync().ConfigureAwait(false);
        }
        _currentStream = null;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
    }
}
