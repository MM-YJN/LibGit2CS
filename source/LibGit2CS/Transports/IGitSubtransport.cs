// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Remote;

namespace LibGit2CS.Transports;

/// <summary>
/// Subtransport interface — the wire-level I/O backend for a smart transport.
/// Maps to <c>git_smart_subtransport</c> in <c>include/git2/sys/transport.h</c>.
/// </summary>
/// <remarks>
/// Implementations: <c>HttpTransport</c>, <c>GitTransport</c>, and
/// <c>SshTransport</c>. The subtransport opens a stream for a given
/// service (upload-pack-ls, upload-pack, receive-pack-ls, receive-pack).
/// </remarks>
public interface IGitSubtransport
{
    /// <summary>
    /// Open a stream for the given service and URL.
    /// </summary>
    /// <param name="url">The remote URL.</param>
    /// <param name="service">The service to invoke.</param>
    /// <param name="options">Connection options (callbacks, proxy, custom headers). May be null for test mocks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A bidirectional stream for reading/writing pkt-line data.</returns>
    Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken = default);

    /// <summary>Close the subtransport and release all resources.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
