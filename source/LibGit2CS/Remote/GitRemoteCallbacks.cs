// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Refs;

namespace LibGit2CS.Remote;

/// <summary>
/// Callbacks for remote operations. Managed equivalent of
/// <c>git_remote_callbacks</c> in <c>include/git2/remote.h</c>.
/// </summary>
/// <remarks>
/// C callbacks with <c>void *payload</c> are replaced by delegates with
/// captured state. All callbacks are optional (may be <c>null</c>).
/// </remarks>
public sealed record GitRemoteCallbacks
{
    /// <summary>
    /// Called to acquire credentials for authentication. May perform async IO
    /// (secret store, keychain, SSH agent). Return a <see cref="GitCredential"/>
    /// instance to authenticate, or <c>null</c> to skip authentication.
    /// For HTTP, arguments are allowed credential types, suggested username,
    /// URL and cancellation token; SSH supplies URL before suggested username.
    /// For HTTP redirects, the URL is the destination; validate its origin
    /// before returning credentials for a trusted remote.
    /// </summary>
    public Func<GitCredentialType, string?, string?, CancellationToken, Task<GitCredential?>>? Credentials { get; init; }

    /// <summary>
    /// Textual progress messages from the remote side band (0x02).
    /// </summary>
    public IProgress<string>? SidebandProgress { get; init; }

    /// <summary>
    /// Transfer (download) progress reporting.
    /// </summary>
    public IProgress<GitTransferProgress>? TransferProgress { get; init; }

    /// <summary>
    /// Certificate validation callback. Return <c>true</c> to accept,
    /// <c>false</c> to reject the certificate.
    /// </summary>
    public Func<GitCertificateInfo, bool>? CertificateCheck { get; init; }

    /// <summary>
    /// Push upload transfer progress.
    /// </summary>
    public IProgress<GitPushTransferProgress>? PushTransferProgress { get; init; }

    /// <summary>
    /// Called for each ref updated during push. <c>refName</c> is the
    /// remote ref being updated; <c>errorMessage</c> is non-null on failure.
    /// </summary>
    public Action<string, string?>? PushUpdateReference { get; init; }

    /// <summary>
    /// Pack building progress reporting.
    /// </summary>
    public IProgress<GitPackProgress>? PackProgress { get; init; }

    /// <summary> Called for each ref updated during fetch AND push. Return <c>true</c> to allow the update, <c>false</c> to abort the operation. Maps to C's
    /// <c>update_refs</c> callback (preferred over the deprecated <c>update_tips</c>). During push (push.c:236-250) the spec is the PUSH refspec and the ref
    /// name is the local tracking ref; during prune (remote.c:1734-1740) newId is zero and the spec is null. </summary> <para><c>refName</c>: The local ref
    /// name being updated.</para> <para><c>oldId</c>: The previous OID (zero if new).</para> <para><c>newId</c>: The new OID (zero for a prune
    /// deletion).</para> <para><c>spec</c>: The refspec that triggered this update, or null.</para>
    public Func<string, GitOid, GitOid, GitRefSpec?, bool>? UpdateRefs { get; init; }

    /// <summary>
    /// Called before the pack is sent, with the list of ref updates being
    /// pushed. May perform async IO (consult a UI, check permissions).
    /// Return <c>false</c> to abort the push. Matches C's
    /// <c>push_negotiation</c> callback.
    /// </summary>
    public Func<IReadOnlyList<GitPushUpdate>, CancellationToken, Task<bool>>? PushNegotiation { get; init; }

    /// <summary>
    /// SSH keepalive interval in seconds. 0 (the default) disables keepalive —
    /// wire parity with the reference libgit2 + libssh2, which never send SSH
    /// keepalive packets (libssh2's <c>keepalive_interval</c> is 0 until
    /// <c>libssh2_keepalive_config</c> is called, and libgit2's ssh transport
    /// never calls it). A positive value opts into the port's keepalive
    /// extension: the transport calls <c>ConfigureKeepAlive(true, interval)</c>
    /// and pumps <c>SendKeepAliveAsync</c> on a <see cref="PeriodicTimer"/>.
    /// </summary>
    public int KeepaliveIntervalSeconds { get; init; }
}
