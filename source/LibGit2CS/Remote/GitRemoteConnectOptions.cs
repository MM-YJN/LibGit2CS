// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Connection-time options for a remote transport. Managed equivalent of
/// <c>git_remote_connect_options</c> in <c>include/git2/remote.h</c>.
/// </summary>
/// <remarks>
/// All <c>version</c> fields from the C API are dropped — forward compatibility
/// is free in managed code (callers recompile).
/// </remarks>
public sealed record GitRemoteConnectOptions
{
    /// <summary>Callbacks for this connection (credentials, progress, cert check).</summary>
    public GitRemoteCallbacks? Callbacks { get; init; }

    /// <summary>Proxy configuration for this connection.</summary>
    public GitProxyConfig? Proxy { get; init; }

    /// <summary>Redirect-following behavior for HTTP(S) connections.</summary>
    public GitRemoteRedirect FollowRedirects { get; init; } = GitRemoteRedirect.Unspecified;

    /// <summary>
    /// Extra HTTP headers, sent only to the connection's original origin
    /// (scheme, host and effective port). Redirects to another origin do not
    /// receive these headers, including Authorization, Cookie and API keys.
    /// </summary>
    public IReadOnlyList<string>? CustomHeaders { get; init; }

    /// <summary>Fetch depth (0 = full history, &gt;0 = shallow clone depth).</summary>
    public int Depth { get; init; }

    /// <summary>
    /// SCP-style URL port override. When non-null AND the URL is SCP-style
    /// (<c>user@host:path</c>), the SSH transport connects to this port
    /// instead of the default 22. libgit2's SCP-style URL grammar has no
    /// port syntax (<c>net.c:661-804</c>'s <c>PORT_START</c> state is
    /// unreachable for normal URLs); this is a <b>LibGit2CS-specific
    /// extension</b> to support non-default ports when the URL string is
    /// fixed (e.g. <c>git@github.com:path</c> against a port-mapped sshd in
    /// integration tests, or against an SSH bastion on a non-standard port).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scope.</b> Applies only when the URL parses as SCP-style. Setting
    /// this for a scheme-style URL (<c>ssh://host:port/path</c>) that already
    /// encodes its own port is a programmer error — <c>GitSshUrl.Parse</c>
    /// throws <see cref="ArgumentException"/> in that case.
    /// </para>
    /// <para>
    /// <b>Range.</b> 1..65535. Out-of-range values throw
    /// <see cref="ArgumentOutOfRangeException"/> at parse time.
    /// </para>
    /// <para>
    /// <b>Why per-connection.</b> Rides the existing
    /// <see cref="GitRemoteConnectOptions"/> plumbing — no process-global
    /// static state, so no parallel-test races (unlike a static field
    /// override would introduce).
    /// </para>
    /// </remarks>
    public int? ScpPortOverride { get; init; }
}
