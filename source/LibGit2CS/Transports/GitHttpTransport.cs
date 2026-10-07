// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Net;
using System.Net.Http.Headers;

using LibGit2CS.Core;
using LibGit2CS.Remote;

namespace LibGit2CS.Transports;

/// <summary>
/// Smart HTTP subtransport using <see cref="HttpClient"/>. Implements
/// <see cref="IGitSubtransport"/> for <c>http://</c> and <c>https://</c> URLs.
/// </summary>
/// <remarks>
/// Managed port of <c>http_subtransport</c> in
/// <c>src/libgit2/transports/http.c</c>. The C implementation pairs a custom
/// HTTP client (<c>httpclient.c</c>, 1,636 LOC) + HTTP parser
/// (<c>httpparser.c</c>, 128 LOC) + TLS backend (~4,100 LOC across OpenSSL,
/// mbedTLS, SChannel, SecureTransport). All of that collapses into
/// <see cref="HttpClient"/> + <see cref="SocketsHttpHandler"/> in the BCL.
/// </remarks>
public sealed class GitHttpTransport : IGitSubtransport, IAsyncDisposable
{
    /// <summary>Maximum redirect/auth replay attempts per request. From <c>GIT_HTTP_REPLAY_MAX</c>.</summary>
    internal const int MaxReplays = 15;

    private static readonly HttpService s_serviceUploadPackLs = new(
        HttpMethod.Get,
        "/info/refs?service=git-upload-pack",
        null,
        "application/x-git-upload-pack-advertisement",
        IsInitial: true,
        Chunked: false);

    private static readonly HttpService s_serviceUploadPack = new(
        HttpMethod.Post,
        "/git-upload-pack",
        "application/x-git-upload-pack-request",
        "application/x-git-upload-pack-result",
        IsInitial: false,
        Chunked: false);

    private static readonly HttpService s_serviceReceivePackLs = new(
        HttpMethod.Get,
        "/info/refs?service=git-receive-pack",
        null,
        "application/x-git-receive-pack-advertisement",
        IsInitial: true,
        Chunked: false);

    private static readonly HttpService s_serviceReceivePack = new(
        HttpMethod.Post,
        "/git-receive-pack",
        "application/x-git-receive-pack-request",
        "application/x-git-receive-pack-result",
        IsInitial: false,
        Chunked: true);

    private Uri? _serverUrl;
    private Uri? _originalOrigin;
    private HttpClient? _client;
    private HttpMessageHandler? _handler;
    private readonly HttpMessageHandler? _customHandler;
    private GitCredential? _serverCred;
    private GitCredential? _proxyCred;
    private AuthContext? _serverAuthContext;
    private AuthContext? _proxyAuthContext;
    private GitAuthSchemeType _serverAuthSchemes;
    // the FULL offered
    // scheme mask (the OR of all challenges) — C's needs_probe compares the
    // offered mask, not the selected scheme (http.c:461-467).
    private GitAuthSchemeType _serverOfferedAuthSchemes;
    private GitAuthSchemeType _proxyAuthSchemes;
    private string? _serverChallenge;
    private string? _proxyChallenge;
    private bool _urlCredPresented;
    private bool _disposed;
    private readonly GitContext _context;

    /// <summary>The library context.</summary>
    internal GitContext Context => _context;

    /// <summary>
    /// Select the <see cref="HttpService"/> for a given <see cref="GitSmartService"/>.
    /// Ported from <c>select_service()</c> in <c>http.c:638-652</c>.
    /// </summary>
    internal static HttpService SelectService(GitSmartService service) => service switch
    {
        GitSmartService.UploadPackLs => s_serviceUploadPackLs,
        GitSmartService.UploadPack => s_serviceUploadPack,
        GitSmartService.ReceivePackLs => s_serviceReceivePackLs,
        GitSmartService.ReceivePack => s_serviceReceivePack,
        _ => throw new GitException(GitErrorCode.Invalid, $"unknown service: {service}", GitErrorCategory.Net),
    };

    /// <summary>The resolved server URL (may change after redirects).</summary>
    internal Uri? ServerUrl => _serverUrl;

    /// <summary>
    /// The resolved server URL, throwing if the action service has not yet
    /// established it. The action service (<c>ActionAsync</c>) is only called
    /// after <see cref="ActionAsync"/> has parsed the URL.
    /// </summary>
    private Uri ConnectedServerUrl => _serverUrl ?? throw new InvalidOperationException("HTTP transport is not connected");

    /// <summary>The discovered server auth schemes (set after a 401 challenge).</summary>
    internal GitAuthSchemeType ServerAuthSchemes => _serverAuthSchemes;

    /// <summary>The discovered proxy auth schemes (set after a 407 challenge).</summary>
    internal GitAuthSchemeType ProxyAuthSchemes => _proxyAuthSchemes;

    /// <summary>The resolved server auth context (for connection-affinity checks).</summary>
    internal AuthContext? ServerAuthContext => _serverAuthContext;

    /// <summary>The HttpClient instance (created lazily on first Action call).</summary>
    internal HttpClient? Client => _client;

    /// <summary>
    /// Whether a probe is needed before the real POST. NTLM and Negotiate
    /// have connection affinity — they require establishing the auth context
    /// with a dummy POST before sending the real data.
    /// Ported from <c>needs_probe()</c> in <c>http.c:461-467</c>.
    /// </summary>
    internal bool NeedsProbe => _serverOfferedAuthSchemes is GitAuthSchemeType.Ntlm or GitAuthSchemeType.Negotiate;

    /// <summary>
    /// Creates a new HTTP transport with a custom message handler (for testing).
    /// </summary>
    /// <param name="context">The owning context.</param>
    /// <param name="handler">The <see cref="HttpMessageHandler"/> to use for HTTP requests.</param>
    internal GitHttpTransport(GitContext context, HttpMessageHandler handler)
    {
        _context = context;
        _customHandler = handler;
    }

    /// <summary>Creates a new HTTP transport.</summary>
    /// <param name="context">The owning context.</param>
    internal GitHttpTransport(GitContext context)
    {
        _context = context;
    }

    /// <inheritdoc/>
    public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Parse/update server URL (preserve redirect target if already set)
        _serverUrl ??= new Uri(url);
        _originalOrigin ??= new Uri(_serverUrl.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));

        HttpService httpService = SelectService(service);

        EnsureClient(options);

        return Task.FromResult<IGitSubtransportStream>(new HttpStream(this, httpService, options, url));
    }

    /// <summary>
    /// Create or reuse the <see cref="HttpClient"/> with the given options.
    /// Configures proxy, TLS certificate validation, and redirect behavior.
    /// </summary>
    private void EnsureClient(GitRemoteConnectOptions? options)
    {
        if (_client is not null)
        {
            return;
        }

        // If a custom handler was provided (for testing), use it directly.
        if (_customHandler is not null)
        {
            _handler = _customHandler;
            _client = new HttpClient(_handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            return;
        }

        var httpClientHandler = new HttpClientHandler
        {
            // We handle redirects manually to respect FollowRedirects policy
            AllowAutoRedirect = false,
            UseDefaultCredentials = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        // Proxy configuration. C's GIT_PROXY_NONE (the zero-initialized default, and a null proxy options here) means connect DIRECTLY — HttpClientHandler must
        // not fall back to the system proxy.
        GitProxyConfig? proxy = options?.Proxy;
        if (proxy is null)
        {
            httpClientHandler.Proxy = null;
            httpClientHandler.UseProxy = false;
        }
        else
        {
            switch (proxy.Type)
            {
                case GitProxyType.None:
                    httpClientHandler.Proxy = null;
                    httpClientHandler.UseProxy = false;
                    break;
                case GitProxyType.Specified when !string.IsNullOrEmpty(proxy.Url):
                    httpClientHandler.Proxy = new WebProxy(proxy.Url);
                    httpClientHandler.UseProxy = true;
                    break;
                case GitProxyType.Auto:
                    // Use system default proxy
                    break;
            }
        }

        // TLS certificate validation
        Func<GitCertificateInfo, bool>? certCallback = options?.Callbacks?.CertificateCheck;
        if (certCallback is not null)
        {
            httpClientHandler.ServerCertificateCustomValidationCallback = (_, cert, chain, sslErrors) =>
            {
                bool isValid = sslErrors == System.Net.Security.SslPolicyErrors.None;
                var info = new GitCertificateInfo
                {
                    Type = GitCertificateType.X509,
                    IsValid = isValid,
                    Hostname = _serverUrl?.Host ?? "",
                };
                return certCallback(info);
            };
        }

        _handler = httpClientHandler;
        _client = new HttpClient(_handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Resolve the server URL for a request, combining the base URL with the
    /// service path. Handles redirects (uses the latest <see cref="_serverUrl"/>).
    /// </summary>
    internal Uri BuildRequestUri(HttpService service)
    {
        UriBuilder builder = new(ConnectedServerUrl)
        {
            Path = CombinePath(ConnectedServerUrl.AbsolutePath, service.Path),
            Query = ExtractQuery(service.Path),
        };
        return builder.Uri;
    }

    private static string CombinePath(string basePath, string servicePath)
    {
        // servicePath may contain a query string (e.g. "/info/refs?service=...")
        int q = servicePath.IndexOf('?', StringComparison.Ordinal);
        string pathOnly = q >= 0 ? servicePath[..q] : servicePath;

        if (basePath.Length == 0 || basePath == "/")
        {
            return pathOnly;
        }

        return basePath.TrimEnd('/') + pathOnly;
    }

    private static string ExtractQuery(string servicePath)
    {
        int q = servicePath.IndexOf('?', StringComparison.Ordinal);
        return q >= 0 ? servicePath[(q + 1)..] : string.Empty;
    }

    /// <summary>
    /// Apply auth headers to an HTTP request message, using the current auth
    /// context. If no auth context is established, no header is added.
    /// </summary>
    internal void ApplyAuthHeaders(HttpRequestMessage request, bool isProxy)
    {
        AuthContext? ctx = isProxy ? _proxyAuthContext : _serverAuthContext;
        if (ctx is null)
        {
            return;
        }

        // C (httpclient.c:578-586): the challenge from the last 401/407 is fed into next_token so the multi-round NTLM/Negotiate exchange can complete. The
        // challenge is consumed by the retry.
        string? challenge = isProxy ? _proxyChallenge : _serverChallenge;
        if (isProxy)
        {
            _proxyChallenge = null;
        }
        else
        {
            _serverChallenge = null;
        }

        string? token = ctx.NextToken(challenge);
        if (token is null)
        {
            return;
        }

        // Parse "Scheme value" into AuthenticationHeaderValue
        int space = token.IndexOf(' ', StringComparison.Ordinal);
        AuthenticationHeaderValue authHeader = space >= 0
            ? new(token[..space], token[(space + 1)..])
            : new(token);

        if (isProxy)
        {
            request.Headers.ProxyAuthorization = authHeader;
        }
        else
        {
            request.Headers.Authorization = authHeader;
        }
    }

    /// <summary>
    /// Handle a 401 response: parse challenges, resolve credentials, create auth context.
    /// Ported from <c>handle_remote_auth()</c> in <c>http.c:175-196</c>.
    /// </summary>
    /// <param name="response">The 401 response.</param>
    /// <param name="options">Connection options (for credential callback).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if auth context was established; false if no challenge/credential.</returns>
    internal async Task<bool> HandleServerAuthAsync(HttpResponseMessage response, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
    {
        List<AuthChallenge> challenges = AuthHandlers.ParseChallenges(response.Headers.WwwAuthenticate.Select(h => h.ToString()));
        if (challenges.Count == 0)
        {
            return false;
        }

        // C (httpclient.c:309-322, collect_authinfo): the credential is acquired with the UNION of the offered schemes' credtypes, then
        // best_scheme_and_challenge picks the scheme matching the ACQUIRED credential's type.
        GitCredentialType credTypes = GitCredentialType.None;
        foreach (AuthChallenge challenge in challenges)
        {
            credTypes |= AuthHandlers.CredentialTypesFor(challenge.Scheme);
        }

        _serverCred?.Dispose();
        (_serverCred, _urlCredPresented) = await AuthHandlers.ResolveCredentialAsync(
            options?.Callbacks,
            ConnectedServerUrl,
            credTypes,
            _urlCredPresented,
            username: ExtractUsername(_serverUrl),
            cancellationToken).ConfigureAwait(false);

        if (_serverCred is null)
        {
            throw new GitException(GitErrorCode.Auth, "authentication required but no credentials provided", GitErrorCategory.Net);
        }

        GitAuthSchemeType scheme = AuthHandlers.SelectBestScheme(challenges, _serverCred);
        if (scheme == GitAuthSchemeType.None)
        {
            throw new GitException(GitErrorCode.Auth, "could not find appropriate mechanism for credentials", GitErrorCategory.Net);
        }

        _serverAuthSchemes = scheme;

        // the offered mask is the OR of every challenge scheme — a
        // server offering "Negotiate, Basic" must NOT fire the probe when
        // Basic is selected (C: needs_probe compares the full mask,
        // http.c:461-467).
        _serverOfferedAuthSchemes = GitAuthSchemeType.None;
        foreach (AuthChallenge challenge in challenges)
        {
            _serverOfferedAuthSchemes |= challenge.Scheme;
        }

        // C (httpclient.c:566-575): the auth context is kept across replays (only created once) so the multi-round NTLM/Negotiate exchange can complete —
        // recreating it on every 401 loses the challenge-response state. The context captures the credential + host at creation, and C's
        // apply_credentials (httpclient.c:586) derives every token from the CURRENT request->credentials — so recreate the context whenever the host changed
        // (redirect) or the re-resolved credential differs (corrected credentials), never replaying a stale or foreign-host token.
        if (_serverAuthContext is null
            || !string.Equals(_serverAuthContext.Host, ConnectedServerUrl.Host, StringComparison.OrdinalIgnoreCase)
            || !ReferenceEquals(_serverAuthContext.Credential, _serverCred))
        {
            _serverAuthContext?.Dispose();
            _serverAuthContext = AuthContext.Create(scheme, _serverCred, ConnectedServerUrl.Host, isProxy: false);
        }

        _serverChallenge = AuthHandlers.ChallengeForScheme(challenges, scheme);
        return true;
    }

    /// <summary>
    /// Handle a 407 response: parse challenges, resolve proxy credentials, create auth context.
    /// Ported from <c>handle_proxy_auth()</c> in <c>http.c:198-219</c>.
    /// </summary>
    /// <param name="response">The 407 response.</param>
    /// <param name="options">Connection options (for proxy credential callback).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if auth context was established; false if no challenge/credential.</returns>
    internal async Task<bool> HandleProxyAuthAsync(HttpResponseMessage response, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
    {
        List<AuthChallenge> challenges = AuthHandlers.ParseChallenges(response.Headers.ProxyAuthenticate.Select(h => h.ToString()));
        if (challenges.Count == 0)
        {
            return false;
        }

        GitCredentialType credTypes = GitCredentialType.None;
        foreach (AuthChallenge challenge in challenges)
        {
            credTypes |= AuthHandlers.CredentialTypesFor(challenge.Scheme);
        }

        _proxyCred?.Dispose();

        string? proxyUrl = options?.Proxy?.Url;
        _proxyCred = await AuthHandlers.ResolveProxyCredentialAsync(
            options?.Proxy,
            credTypes,
            username: proxyUrl is not null ? ExtractUsername(new Uri(proxyUrl)) : null,
            url: proxyUrl,
            cancellationToken).ConfigureAwait(false);

        if (_proxyCred is null)
        {
            throw new GitException(GitErrorCode.Auth, "proxy authentication required but no credentials provided", GitErrorCategory.Net);
        }

        // Scheme selection after credential acquisition; the context is kept across replays so NTLM/Negotiate multi-round exchanges can complete, unless the
        // host or the re-resolved credential changed (same rationale as the server context above).
        GitAuthSchemeType scheme = AuthHandlers.SelectBestScheme(challenges, _proxyCred);
        if (scheme == GitAuthSchemeType.None)
        {
            throw new GitException(GitErrorCode.Auth, "could not find appropriate mechanism for credentials", GitErrorCategory.Net);
        }

        _proxyAuthSchemes = scheme;
        string proxyHost = new Uri(proxyUrl ?? "").Host;
        if (_proxyAuthContext is null
            || !string.Equals(_proxyAuthContext.Host, proxyHost, StringComparison.OrdinalIgnoreCase)
            || !ReferenceEquals(_proxyAuthContext.Credential, _proxyCred))
        {
            _proxyAuthContext?.Dispose();
            _proxyAuthContext = AuthContext.Create(scheme, _proxyCred, proxyHost, isProxy: true);
        }

        _proxyChallenge = AuthHandlers.ChallengeForScheme(challenges, scheme);
        return true;
    }

    /// <summary>
    /// Check if a redirect should be followed based on the FollowRedirects policy.
    /// Ported from <c>allow_redirect()</c> in <c>http.c:221-233</c>.
    /// </summary>
    internal static bool AllowRedirect(GitRemoteRedirect followRedirects, bool isInitial)
    {
        return followRedirects switch
        {
            GitRemoteRedirect.All => true,
            GitRemoteRedirect.None => false,
            GitRemoteRedirect.Initial => isInitial,
            // Unspecified is normalized to a concrete value at connect time.
            _ => isInitial,
        };
    }

    /// <summary>
    /// Apply a redirect by updating the server URL from the Location header.
    /// mirrors C's
    /// <c>git_net_url_apply_redirect</c> (net.c:933-982) — a scheme change is
    /// only legal when the target is https, and a host change is only legal
    /// when offsite redirects are permitted by the follow policy. The port
    /// used to follow any target, sending the cached Authorization header
    /// over plaintext to an attacker host after an https→http downgrade.
    /// </summary>
    /// <param name="location">The Location header value.</param>
    /// <param name="allowOffsite">Whether an offsite host change is permitted
    /// (C's <c>allow_redirect(stream)</c> — the FollowRedirects policy).</param>
    /// <exception cref="GitException">The redirect target is not legal
    /// (scheme downgrade, or offsite host under a policy that forbids it).</exception>
    internal void ApplyRedirect(string location, bool allowOffsite)
    {
        // C treats every leading slash as a path, including "//host/path".
        // Anchor it to the current authority instead of letting Uri resolve
        // a network-path reference to a different host.
        Uri newUrl = location.StartsWith('/', StringComparison.Ordinal)
            ? new Uri(ConnectedServerUrl.GetLeftPart(UriPartial.Authority) + location)
            : new Uri(ConnectedServerUrl, location);

        // C (net.c:949-956): strcmp on schemes; a change is legal only when
        // the target is https (Uri.Scheme is already lowercased by the BCL).
        if (!string.Equals(ConnectedServerUrl.Scheme, newUrl.Scheme, StringComparison.Ordinal)
            && !string.Equals(newUrl.Scheme, "https", StringComparison.Ordinal))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"cannot redirect from '{ConnectedServerUrl.Scheme}' to '{newUrl.Scheme}'",
                GitErrorCategory.Net);
        }

        // C (net.c:958-966): git__strcasecmp on hosts; port differences are
        // allowed (only the host is compared).
        if (!allowOffsite && !string.Equals(ConnectedServerUrl.Host, newUrl.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"cannot redirect from '{ConnectedServerUrl.Host}' to '{newUrl.Host}'",
                GitErrorCategory.Net);
        }

        if (!SameOrigin(ConnectedServerUrl, newUrl))
        {
            // Upstream resets the HTTP client's auth context on scheme,
            // host or port changes (httpclient.c:setup_hosts). Also discard
            // the managed credential and challenge before the first retry.
            // URL credentials must not be reused later in this connection.
            newUrl = new UriBuilder(newUrl) { UserName = string.Empty, Password = string.Empty }.Uri;
            ResetServerAuth();
            _urlCredPresented = true;
        }

        _serverUrl = newUrl;
    }

    /// <summary>
    /// Custom headers can contain arbitrary secrets. Unlike upstream, bind
    /// all of them to the initial origin, including across service streams.
    /// </summary>
    internal bool CanSendCustomHeaders(Uri requestUri)
        => _originalOrigin is not null && SameOrigin(_originalOrigin, requestUri);

    private static bool SameOrigin(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;

    private static string? ExtractUsername(Uri? url)
    {
        if (url is null || string.IsNullOrEmpty(url.UserInfo))
        {
            return null;
        }

        int colon = url.UserInfo.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? Uri.UnescapeDataString(url.UserInfo[..colon]) : Uri.UnescapeDataString(url.UserInfo);
    }

    /// <summary>
    /// Release the current auth context and credential (for retry with new creds).
    /// </summary>
    private void ResetServerAuth()
    {
        _serverAuthContext?.Dispose();
        _serverAuthContext = null;
        _serverCred?.Dispose();
        _serverCred = null;
        _serverChallenge = null;
        _serverAuthSchemes = GitAuthSchemeType.None;
        _serverOfferedAuthSchemes = GitAuthSchemeType.None;
    }

    internal void ResetAuth()
    {
        ResetServerAuth();
        _proxyAuthContext?.Dispose();
        _proxyAuthContext = null;
        _proxyCred?.Dispose();
        _proxyCred = null;
        _proxyChallenge = null;
        _proxyAuthSchemes = GitAuthSchemeType.None;
    }

    /// <inheritdoc/>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ResetAuth();
        _serverUrl = null;
        _originalOrigin = null;
        _urlCredPresented = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _client?.Dispose();
        _handler?.Dispose();
        _client = null;
        _handler = null;
        _disposed = true;
    }
}
