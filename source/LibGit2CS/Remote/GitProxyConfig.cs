// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Proxy configuration for remote connections. Managed equivalent of
/// <c>git_proxy_options</c> in <c>include/git2/proxy.h</c>.
/// </summary>
/// <remarks>
/// In the C port, this wraps <c>System.Net.WebProxy</c> for HTTP proxy
/// support. The <c>Url</c> is a proxy URL like <c>http://proxy:8080</c>.
/// </remarks>
public sealed record GitProxyConfig
{
    /// <summary> The proxy type. Default is <see cref="GitProxyType.None"/> — C's <c>GIT_PROXY_OPTIONS_INIT</c> zero-inits <c>type = GIT_PROXY_NONE</c>, so
    /// connections go direct unless the caller opts into <see cref="GitProxyType.Auto"/> or <see cref="GitProxyType.Specified"/>. </summary>
    public GitProxyType Type { get; init; } = GitProxyType.None;

    /// <summary>
    /// The proxy URL (used when <see cref="Type"/> is
    /// <see cref="GitProxyType.Specified"/>). E.g.
    /// <c>http://proxy.example.com:8080</c>.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Credential callback for proxy authentication. May perform async IO
    /// (secret store, keychain). Return a <see cref="GitCredential"/> or
    /// <c>null</c>.
    /// </summary>
    public Func<GitCredentialType, string?, string?, CancellationToken, Task<GitCredential?>>? Credentials { get; init; }

    /// <summary>
    /// Certificate check callback for the proxy connection. Return
    /// <c>true</c> to accept, <c>false</c> to reject.
    /// </summary>
    public Func<GitCertificateInfo, bool>? CertificateCheck { get; init; }
}
