// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Options for a push operation. Managed equivalent of
/// <c>git_push_options</c> in <c>include/git2/remote.h</c>.
/// </summary>
/// <remarks>
/// All <c>version</c> fields from the C API are dropped — forward compatibility
/// is free in managed code (callers recompile).
/// </remarks>
public sealed record GitPushOptions
{
    /// <summary>
    /// Packbuilder parallelism (number of threads). Default 1 (single-threaded).
    /// Maps to <c>git_push_options.pb_parallelism</c>.
    /// </summary>
    public int PbParallelism { get; init; } = 1;

    /// <summary>Callbacks (credentials, progress, push update, pack progress).</summary>
    public GitRemoteCallbacks? RemoteCallbacks { get; init; }

    /// <summary>Proxy configuration.</summary>
    public GitProxyConfig? ProxyConfig { get; init; }

    /// <summary>Redirect-following behavior for HTTP(S).</summary>
    public GitRemoteRedirect FollowRedirects { get; init; } = GitRemoteRedirect.Unspecified;

    /// <summary>
    /// Extra HTTP headers, sent only to the connection's original origin
    /// (scheme, host and effective port). Redirects to another origin do not
    /// receive these headers, including Authorization, Cookie and API keys.
    /// </summary>
    public IReadOnlyList<string>? CustomHeaders { get; init; }

    /// <summary>
    /// Push options (strings sent to the server via the <c>push-options</c>
    /// capability). Maps to <c>git_push_options.remote_push_options</c>.
    /// </summary>
    public IReadOnlyList<string>? RemotePushOptions { get; init; }

    /// <summary>
    /// SCP-style URL port override — see
    /// <see cref="GitRemoteConnectOptions.ScpPortOverride"/> for semantics.
    /// Propagated into the connect options by <see cref="GitRemote.PushAsync"/>.
    /// </summary>
    public int? ScpPortOverride { get; init; }
}
