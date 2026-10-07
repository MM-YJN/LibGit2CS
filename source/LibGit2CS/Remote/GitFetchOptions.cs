// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Options for a fetch operation. Managed equivalent of
/// <c>git_fetch_options</c> in <c>include/git2/remote.h</c>.
/// </summary>
/// <remarks>
/// All <c>version</c> fields from the C API are dropped — forward compatibility
/// is free in managed code (callers recompile).
/// </remarks>
public sealed record GitFetchOptions
{
    /// <summary>Fetch depth (0 = full history, &gt;0 = shallow clone depth).</summary>
    public int Depth { get; init; }

    /// <summary>Tag download policy.</summary>
    public GitAutoTagOption DownloadTags { get; init; } = GitAutoTagOption.Unspecified;

    /// <summary>Redirect-following behavior for HTTP(S).</summary>
    public GitRemoteRedirect FollowRedirects { get; init; } = GitRemoteRedirect.Unspecified;

    /// <summary>Callbacks (credentials, progress, cert check, update refs).</summary>
    public GitRemoteCallbacks? RemoteCallbacks { get; init; }

    /// <summary>Proxy configuration.</summary>
    public GitProxyConfig? ProxyConfig { get; init; }

    /// <summary>
    /// Extra HTTP headers, sent only to the connection's original origin
    /// (scheme, host and effective port). Redirects to another origin do not
    /// receive these headers, including Authorization, Cookie and API keys.
    /// </summary>
    public IReadOnlyList<string>? CustomHeaders { get; init; }

    /// <summary>Prune behavior.</summary>
    public GitFetchPrune Prune { get; init; } = GitFetchPrune.Unspecified;

    /// <summary>Whether to update <c>FETCH_HEAD</c> after fetch (default true).</summary>
    public bool UpdateFetchhead { get; init; } = true;

    /// <summary> Report unchanged refs to the <c>update_refs</c> callback. Maps to <c>GIT_REMOTE_UPDATE_REPORT_UNCHANGED</c> in C's
    /// <c>git_fetch_options.update_fetchhead</c> bitmask (remote.h:419-421, remote.c:1364) — when true, an unchanged tip still fires the callback. </summary>
    public bool ReportUnchanged { get; init; }

    /// <summary>
    /// SCP-style URL port override — see
    /// <see cref="GitRemoteConnectOptions.ScpPortOverride"/> for semantics.
    /// Propagated into the connect options by <see cref="GitRemote.FetchAsync"/>
    /// and <see cref="LibGit2CS.Remote.GitClone.RunAsync"/>.
    /// </summary>
    public int? ScpPortOverride { get; init; }
}
