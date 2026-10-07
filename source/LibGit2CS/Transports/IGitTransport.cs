// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.Transports;

/// <summary>
/// Transport interface — the top-level abstraction for talking to a remote.
/// Maps to <c>git_transport</c> in <c>include/git2/sys/transport.h</c>.
/// </summary>
/// <remarks>
/// Custom transports implement this interface directly (no function pointers,
/// AOT-clean). The smart transport (<see cref="GitSmartTransport"/>) delegates to
/// an <see cref="IGitSubtransport"/> for the wire-level I/O.
/// </remarks>
public interface IGitTransport : IAsyncDisposable
{
    /// <summary>Connect to the remote for the given direction.</summary>
    /// <param name="url">The remote URL.</param>
    /// <param name="direction">Fetch or push.</param>
    /// <param name="options">Connection options (callbacks, proxy, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConnectAsync(string url, GitDirection direction, GitRemoteConnectOptions? options, CancellationToken cancellationToken = default);

    /// <summary>Update connect options on an already-connected transport.</summary>
    void SetConnectOptions(GitRemoteConnectOptions? options);

    /// <summary>Return the remote's capabilities (see <see cref="GitRemoteCapability"/>).</summary>
    GitRemoteCapability Capabilities { get; }

    /// <summary>The OID type (SHA-1 or SHA-256) detected from the remote.</summary>
    GitHashAlgorithmKind OidType { get; }

    /// <summary>Return the cached remote heads (ref advertisement).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<GitRemoteHead>> LsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Negotiate which objects the client needs. Sends wants/haves via
    /// the smart protocol and determines the common ancestor set.
    /// Maps to <c>git_transport->negotiate_fetch</c>.
    /// </summary>
    /// <param name="repo">The local repository (for revwalk to find local commits).</param>
    /// <param name="wants">The fetch negotiation parameters (refs, shallow roots, depth).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NegotiateFetchAsync(GitRepository repo, GitFetchNegotiation wants, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download the pack from the remote after negotiation. Streams pack
    /// data to the ODB via <see cref="IGitWritePack"/>.
    /// Maps to <c>git_transport->download_pack</c>.
    /// </summary>
    /// <param name="repo">The local repository (for object writing).</param>
    /// <param name="stats">Progress accumulator (updated during download).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DownloadPackAsync(GitRepository repo, GitIndexerProgress stats, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the shallow root OIDs negotiated during a shallow fetch.
    /// Maps to <c>git_transport->shallow_roots</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IReadOnlyList<GitOid>> ShallowRootsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Push ref updates + optional pack to the remote. Sends push commands
    /// via pkt-line, uploads the pack (if any), and parses the report-status
    /// response. Maps to <c>git_transport->push</c>.
    /// </summary>
    /// <param name="repo">The local repository (for pack building).</param>
    /// <param name="specs">The push specs (refspecs with resolved local/remote OIDs).</param>
    /// <param name="packWriter">Optional pre-populated pack writer. If null and a pack is needed, the transport builds one.</param>
    /// <param name="callbacks">Remote callbacks (push transfer progress, push update reference, pack progress).</param>
    /// <param name="reportStatus">Whether to request the report-status capability.</param>
    /// <param name="pushOptions">Optional push option strings (sent via the push-options capability).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The push result (unpack status + per-ref statuses).</returns>
    Task<GitPushResult> PushAsync(GitRepository repo, IReadOnlyList<GitPushSpec> specs, GitPackWriter? packWriter, GitRemoteCallbacks? callbacks, bool reportStatus, IReadOnlyList<string>? pushOptions, CancellationToken cancellationToken = default);

    /// <summary>Whether the transport is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Cancel an in-progress operation.</summary>
    void Cancel();

    /// <summary>Close the transport connection.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
