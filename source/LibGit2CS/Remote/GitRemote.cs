// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;
using System.Text.RegularExpressions;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.Remote;

/// <summary>
/// A remote repository connection. Managed port of <c>git_remote</c> in
/// <c>src/libgit2/remote.c</c>. Manages transport lifecycle, refspecs,
/// fetch/push orchestration, and ref tip updates.
/// </summary>
/// <remarks>
/// Covers connect, ls, download (fetch negotiation + pack), update tips,
/// fetch, prune, auto-tag, stats, stop, and remote management
/// (create/lookup/list/delete/rename).
/// </remarks>
public sealed partial class GitRemote : IAsyncDisposable
{
    private readonly GitRepository _repo;
    private readonly string? _name;
    private string? _url;
    private string? _pushUrl;
    private readonly List<GitRefSpec> _refspecs = [];
    private readonly List<GitRefSpec> _activeRefspecs = [];
    private readonly List<GitRefSpec> _passiveRefspecs = [];
    /// <summary>True when the last download was given explicit refspecs
    /// (C's <c>remote-&gt;passed_refspecs</c>, remote.c:1289) — enables the
    /// opportunistic tracking-branch updates through the passive specs.</summary>
    private bool _passedRefspecs;
    private IGitTransport? _transport;
    private readonly GitIndexerProgress _stats = new();
    private GitAutoTagOption _downloadTags = GitAutoTagOption.Auto;
    private bool _pruneRefs;
    private bool _disposed;

    /// <summary>
    /// Whether the last negotiation determined that a pack must be
    /// downloaded. Set during <see cref="FetchCoordinator.FilterWantsAsync"/>
    /// when any matched remote head's object is missing locally (or the
    /// fetch is shallow). Checked by both
    /// <see cref="FetchCoordinator.NegotiateAsync"/> and
    /// <see cref="FetchCoordinator.DownloadPackAsync"/> to short-circuit
    /// when there is nothing to fetch. Mirrors <c>remote->need_pack</c>
    /// in <c>src/libgit2/remote.h:37</c>.
    /// </summary>
    internal bool NeedPack { get; set; }

    /// <summary>Creates a remote with the given name and URL.</summary>
    internal GitRemote(GitRepository repo, string? name, string? url)
    {
        _repo = repo;
        _name = name;
        _url = url;
    }

    /// <summary>The remote name (or null for anonymous).</summary>
    public string? Name => _name;

    /// <summary> The fetch URL, or null for a push-only remote (no <c>url</c> key, only <c>pushurl</c>). Matches C's nullable <c>git_remote_url</c>. </summary>
    public string? Url => _url;

    /// <summary>The push URL (falls back to <see cref="Url"/> if null).</summary>
    public string? PushUrl => _pushUrl ?? _url;

    /// <summary>The owning repository.</summary>
    public GitRepository Owner => _repo;

    /// <summary>Whether the remote is currently connected.</summary>
    public bool IsConnected => _transport?.IsConnected ?? false;

    /// <summary>The configured fetch refspecs.</summary>
    public IReadOnlyList<GitRefSpec> RefSpecs => _refspecs;

    /// <summary>The DWIM-expanded active refspecs (from user-specified or config).</summary>
    public IReadOnlyList<GitRefSpec> ActiveRefSpecs => _activeRefspecs;

    /// <summary>The DWIM-expanded passive refspecs (from config).</summary>
    public IReadOnlyList<GitRefSpec> PassiveRefSpecs => _passiveRefspecs;

    /// <summary>Tag download policy.</summary>
    public GitAutoTagOption AutoTag => _downloadTags;

    /// <summary>Whether prune is configured for this remote.</summary>
    public bool PruneRefs => _pruneRefs;

    /// <summary>Indexer progress from the last download.</summary>
    public GitIndexerProgress Stats => _stats;

    /// <summary>
    /// Connect to the remote for fetch or push.
    /// Matches <c>git_remote_connect</c> in <c>remote.c:987</c>.
    /// </summary>
    public async Task ConnectAsync(GitDirection direction, GitRemoteConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        // C (remote.c:905-920, git_remote_connect_options_normalize): validate_custom_headers runs at connect-options normalization time — before any transport
        // work.
        ValidateCustomHeaders(options?.CustomHeaders);

        if (_transport is not null && _transport.IsConnected)
        {
            _transport.SetConnectOptions(options);
            return;
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        string? url = direction == GitDirection.Push ? (PushUrl ?? _url) : _url;

        // C (transport.c:128-136): a NULL url fails scheme resolution →
        // "unsupported URL protocol" (GIT_ERROR_NET). Reachable only for a
        // pushurl-only remote being fetched.
        if (url is null)
        {
            throw new GitException(GitErrorCode.Error, "unsupported URL protocol", GitErrorCategory.Net);
        }

        // C (git_proxy_options default is GIT_PROXY_AUTO; http.c lookup_proxy resolves it via git_remote__http_proxy — remote.c:1085-1195): when no proxy (or
        // GIT_PROXY_AUTO) was supplied, resolve the proxy from config/env and
        // hand the transport an explicit Specified/None proxy. C (proxy.c:12-17,
        // http.c:316-334): GIT_PROXY_OPTIONS_INIT zero-inits type = GIT_PROXY_NONE, and lookup_proxy only
        // consults config/env for SPECIFIED/AUTO — so a default (null/None) proxy connects directly. Only an explicit GIT_PROXY_AUTO resolves from git config +
        // environment.
        GitRemoteConnectOptions? effectiveOptions = options;
        if (options is { Proxy.Type: GitProxyType.Auto })
        {
            GitProxyConfig resolved = await ResolveProxyAsync(url, cancellationToken).ConfigureAwait(false);
            effectiveOptions = options with { Proxy = resolved };
        }

        // C (remote.c:924-927): follow_redirects == 0 (unspecified) → lookup_redirect_config reads http.followRedirects ("initial"/bool → INITIAL/ALL/NONE;
        // invalid → GIT_ERROR_CONFIG).
        if (effectiveOptions is { FollowRedirects: GitRemoteRedirect.Unspecified })
        {
            GitRemoteRedirect resolved = await ResolveFollowRedirectsConfigAsync(cancellationToken).ConfigureAwait(false);
            effectiveOptions = effectiveOptions with { FollowRedirects = resolved };
        }

        _transport = _repo.Context.Transports.Create(url, _repo.Context);
        await _transport.ConnectAsync(url, direction, effectiveOptions, cancellationToken).ConfigureAwait(false);

        // C's recv_pkt seeds the packet-parse state with the LOCAL repository's object
        // format and set_data rejects a mismatch with "the local object
        // format '%s' does not match the remote object format '%s'"
        // (smart_pkt.c:261-269, smart_protocol.c:275) — a sha256 remote
        // cannot be fetched into a sha1 repo. (Genuinely unknown remote
        // formats are already rejected during ref parsing,
        // GitPacketReader.cs:636.)
        if (_transport is GitSmartTransport smartTransport)
        {
            GitHashAlgorithmKind remoteType = smartTransport.OidType;
            GitHashAlgorithmKind localType = _repo.ObjectFormat;
            if (remoteType != localType)
            {
                throw new GitException(GitErrorCode.Error,
                    $"the local object format '{OidTypeName(localType)}' does not match the remote object format '{OidTypeName(remoteType)}'",
                    GitErrorCategory.Invalid);
            }
        }
    }

    /// <summary>
    /// Maps a hash algorithm to its git name, matching C's
    /// <c>git_oid_type_name</c> (oid.h:78): <c>"sha1"</c>/<c>"sha256"</c>.
    /// </summary>
    private static string OidTypeName(GitHashAlgorithmKind kind)
        => kind == GitHashAlgorithmKind.Sha256 ? "sha256" : "sha1";

    /// <summary>
    /// Resolves the effective redirect policy from <c>http.followRedirects</c>
    /// config. Matches <c>lookup_redirect_config</c> (remote.c:866-904):
    /// missing → INITIAL; bool value → ALL/NONE; <c>"initial"</c> (ASCII
    /// case-insensitive) → INITIAL; anything else → GIT_ERROR_CONFIG.
    /// </summary>
    internal async Task<GitRemoteRedirect> ResolveFollowRedirectsConfigAsync(CancellationToken cancellationToken)
    {
        // byte-domain read (C's lookup_redirect_config strcasecmps raw bytes, remote.c:866-904).
        byte[]? value = await _repo.Config.GetBytesAsync("http.followRedirects", cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return GitRemoteRedirect.Initial;
        }

        if (ConfigurationValueParser.TryParseBool(value, out bool boolValue))
        {
            return boolValue ? GitRemoteRedirect.All : GitRemoteRedirect.None;
        }

        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, "initial"u8))
        {
            return GitRemoteRedirect.Initial;
        }

        throw new GitException(
            GitErrorCode.Error,
            $"invalid configuration setting '{Encoding.UTF8.GetString(value)}' for 'http.followRedirects'",
            GitErrorCategory.Config);
    }

    /// <summary> The OID type (hash algorithm) of the connected transport. Matches <c>git_remote_oid_type</c> (remote.c:3014-3023): the transport's advertised
    /// object format (clone.c:444-450). </summary>
    internal GitHashAlgorithmKind OidType
        => _transport?.OidType ?? GitHashAlgorithmKind.Sha1;

    /// <summary> Validates custom HTTP headers. Ported from <c>validate_custom_headers</c> (remote.c:844-864): a header with CR/LF or without a non-empty
    /// <c>name:</c> prefix is "malformed"; a header whose name is a prefix of a forbidden name (User-Agent, Host, Accept, Content-Type, Transfer-Encoding,
    /// Content-Length) is "already set by libgit2". Both fail with GIT_ERROR_INVALID. </summary>
    internal static void ValidateCustomHeaders(IReadOnlyList<string>? customHeaders)
    {
        if (customHeaders is null)
        {
            return;
        }

        foreach (string header in customHeaders)
        {
            if (IsMalformedHttpHeader(header))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"custom HTTP header '{header}' is malformed",
                    GitErrorCategory.Invalid);
            }

            if (IsForbiddenCustomHeader(header))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"custom HTTP header '{header}' is already set by libgit2",
                    GitErrorCategory.Invalid);
            }
        }
    }

    /// <summary>
    /// C's <c>is_malformed_http_header</c> (remote.c:804-820): CR/LF anywhere,
    /// or no <c>:</c> / an empty name before it.
    /// </summary>
    private static bool IsMalformedHttpHeader(string header)
    {
        if (header.Contains('\r', StringComparison.Ordinal) ||
            header.Contains('\n', StringComparison.Ordinal))
        {
            return true;
        }

        int colon = header.IndexOf(':', StringComparison.Ordinal);
        return colon < 1;
    }

    /// <summary>
    /// C's <c>is_forbidden_custom_header</c> (remote.c:822-842):
    /// <c>strncmp(forbidden, header, name_len) == 0</c> — the header name
    /// must be a PREFIX of the forbidden name (so "User" is forbidden but
    /// "UserX" is not).
    /// </summary>
    private static bool IsForbiddenCustomHeader(string header)
    {
        int nameLen = header.IndexOf(':', StringComparison.Ordinal);
        if (nameLen < 1)
        {
            return false;
        }

        ReadOnlySpan<char> name = header.AsSpan(0, nameLen);
        foreach (string forbidden in s_forbiddenCustomHeaders)
        {
            if (forbidden.AsSpan(0, Math.Min(nameLen, forbidden.Length)).SequenceEqual(name))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Collections.Immutable.ImmutableArray<string> s_forbiddenCustomHeaders =
    [
        "User-Agent",
        "Host",
        "Accept",
        "Content-Type",
        "Transfer-Encoding",
        "Content-Length",
    ];

    /// <summary>
    /// Resolve the effective proxy for a connection. Matches
    /// <c>git_remote__http_proxy</c> (remote.c:1176-1193): config keys first
    /// (<c>remote.&lt;name&gt;.proxy</c>, then <c>http.&lt;url&gt;.proxy</c>
    /// walking the URL path up, then <c>http.proxy</c>), then the
    /// <c>https_proxy</c>/<c>http_proxy</c> environment variables with
    /// <c>no_proxy</c>/<c>NO_PROXY</c> handling. Returns a
    /// <see cref="GitProxyType.Specified"/> proxy when one is found and a
    /// <see cref="GitProxyType.None"/> proxy when none is (C connects
    /// directly in that case).
    /// </summary>
    private async Task<GitProxyConfig> ResolveProxyAsync(string url, CancellationToken cancellationToken)
    {
        string? proxy = await LookupProxyConfigAsync(url, cancellationToken).ConfigureAwait(false);
        proxy ??= LookupProxyEnv(url);

        if (string.IsNullOrEmpty(proxy))
        {
            return new GitProxyConfig { Type = GitProxyType.None };
        }

        return new GitProxyConfig { Type = GitProxyType.Specified, Url = proxy };
    }

    /// <summary>
    /// Look up the proxy from git config. Matches <c>http_proxy_config</c>
    /// (remote.c:1085-1118): <c>remote.&lt;name&gt;.proxy</c>, then
    /// <c>http.&lt;formatted-url&gt;.proxy</c> with the URL path trimmed up
    /// (url_config_trim), then <c>http.proxy</c>. A found-but-empty value
    /// stops the chain (returns "" — no proxy).
    /// </summary>
    private async Task<string?> LookupProxyConfigAsync(string url, CancellationToken cancellationToken)
    {
        GitConfiguration cfg = _repo.Config;

        if (_name is { Length: > 0 })
        {
            string? v = await cfg.GetStringAsync($"remote.{_name}.proxy", cancellationToken).ConfigureAwait(false);
            if (v is not null)
            {
                return v;
            }
        }

        if (TryFormatProxyUrl(url, out string hostPort, out string path))
        {
            while (true)
            {
                string? v = await cfg.GetStringAsync($"http.{hostPort}{path}.proxy", cancellationToken).ConfigureAwait(false);
                if (v is not null)
                {
                    return v;
                }

                if (path.Length == 0)
                {
                    break;
                }

                path = TrimUrlPath(path);
            }
        }

        return await cfg.GetStringAsync("http.proxy", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Look up the proxy from the environment. Matches <c>http_proxy_env</c>
    /// (remote.c:1138-1174): <c>https_proxy</c>/<c>http_proxy</c> (lowercase
    /// first, then uppercase) with <c>no_proxy</c>/<c>NO_PROXY</c> pattern
    /// matching.
    /// </summary>
    private string? LookupProxyEnv(string url)
    {
        bool useSsl = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        string? proxy = _repo.Context.Env[useSsl ? "https_proxy" : "http_proxy"];
        proxy ??= _repo.Context.Env[useSsl ? "HTTPS_PROXY" : "HTTP_PROXY"];

        if (proxy is null)
        {
            return null;
        }

        string? noProxy = _repo.Context.Env["no_proxy"];
        noProxy ??= _repo.Context.Env["NO_PROXY"];

        if (noProxy is not null && UrlMatchesNoProxy(url, noProxy))
        {
            return null;
        }

        return proxy;
    }

    /// <summary>
    /// Matches a URL against a comma-separated no_proxy pattern list.
    /// Ported from <c>git_net_url_matches_pattern_list</c> +
    /// <c>matches_pattern</c> (net.c:1070-1142).
    /// </summary>
    private static bool UrlMatchesNoProxy(string url, string patternList)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        // C compares url->host — parsed VERBATIM by git_net_url_parse
        // (no lowercasing) — against the pattern with git__strlcmp/memcmp
        // (net.c:1105,1134), i.e. case-sensitively. System.Uri normalizes
        // the host to lowercase, which would silently defeat StringComparison
        // .Ordinal; extract the case-preserved host from the raw string
        // instead.
        string host = ExtractRawHost(url);
        int port = uri.Port;

        foreach (string rawPattern in patternList.Split(','))
        {
            string pattern = rawPattern;
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern == "*")
            {
                return true;
            }

            int wildcard = 0;
            if (pattern.Length > 1 && pattern[0] == '*' && pattern[1] == '.')
            {
                wildcard = 2;
            }
            else if (pattern[0] == '.')
            {
                wildcard = 1;
            }

            string domain = pattern[wildcard..];
            string? patternPort = null;
            int colon = domain.LastIndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                patternPort = domain[(colon + 1)..];
                domain = domain[..colon];
            }

            // A pattern's port must match if specified.
            if (patternPort is not null && patternPort != port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                continue;
            }

            if (wildcard == 0)
            {
                // C
                // compares host vs domain CASE-SENSITIVELY (git__strlcmp /
                // memcmp, net.c:1130-1142).
                if (string.Equals(host, domain, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (host.Length >= domain.Length &&
                     host.EndsWith(domain, StringComparison.Ordinal) &&
                     // C requires a subdomain '.' boundary for hosts
                     // longer than the pattern (net.c:1137-1141) —
                     // "*.example.com" must NOT match "badexample.com"; a
                     // host exactly equal to the pattern matches (host_len ==
                     // domain_len, net.c:1136).
                     (host.Length == domain.Length || host[host.Length - domain.Length - 1] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the host component of a URL verbatim (case-preserved),
    /// matching C's <c>url-&gt;host</c> — <c>git_net_url_parse</c> does not
    /// lowercase the host. Strips the scheme, userinfo (up to the last '@'),
    /// an explicit port, and IPv6 brackets. Mirrors the shape
    /// <c>System.Uri.Host</c> produces, except that case is kept.
    /// </summary>
    private static string ExtractRawHost(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        int start = scheme < 0 ? 0 : scheme + 3;

        int end = url.Length;
        for (int i = start; i < url.Length; i++)
        {
            if (url[i] is '/' or '?' or '#')
            {
                end = i;
                break;
            }
        }

        // Strip userinfo: the last '@' before the end of the authority
        // (user:pass@host — the last '@' is the userinfo/host separator).
        int at = end > start ? url.LastIndexOf('@', end - 1, end - start, StringComparison.Ordinal) : -1;
        int authStart = at >= start ? at + 1 : start;

        string authority = url[authStart..end];
        if (authority.Length == 0)
        {
            return string.Empty;
        }

        if (authority[0] == '[')
        {
            // Bracketed IPv6 literal: strip the brackets (C keeps them in
            // url->host for scp-style but not for scheme URLs; the port's
            // matcher always compares the unbracketed form).
            int close = authority.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? authority[1..close] : authority;
        }

        // Strip an explicit port (the last ':'); a leading ':' (empty host,
        // e.g. ":8080") is left intact so the empty-host case surfaces.
        int colon = authority.LastIndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? authority[..colon] : authority;
    }

    /// <summary>
    /// Format a URL as <c>scheme://host[:port]/path</c> for the
    /// <c>http.&lt;url&gt;.proxy</c> config key, mirroring
    /// <c>git_net_url_fmt</c> (net.c:1041-1063). Returns <c>false</c> for
    /// unparseable (non-scheme) URLs.
    /// </summary>
    private static bool TryFormatProxyUrl(string url, out string hostPort, out string path)
    {
        hostPort = string.Empty;
        path = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            string.IsNullOrEmpty(uri.Scheme) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        string scheme = uri.Scheme.ToLowerInvariant();
        string host = uri.Host;
        if (host.Contains(':', StringComparison.Ordinal))
        {
            host = "[" + host + "]";
        }

        string portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        hostPort = $"{scheme}://{host}{portPart}";
        path = uri.AbsolutePath;
        return true;
    }

    /// <summary>
    /// Trim one path segment off a URL path. Ported from
    /// <c>url_config_trim</c> (remote.c:1070-1084).
    /// </summary>
    private static string TrimUrlPath(string path)
    {
        if (path.EndsWith('/', StringComparison.Ordinal))
        {
            return path[..^1];
        }

        int lastSlash = path.LastIndexOf('/', StringComparison.Ordinal);
        return lastSlash < 0 ? string.Empty : path[..lastSlash];
    }

    /// <summary>Disconnect from the remote.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_transport is not null && _transport.IsConnected)
        {
            await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// List the remote's advertised refs. Must be connected.
    /// Matches <c>git_remote_ls</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<GitRemoteHead>> LsAsync(CancellationToken cancellationToken = default)
    {
        if (_transport is null)
        {
            throw new GitException(GitErrorCode.Invalid, "the remote is not connected", GitErrorCategory.Net);
        }

        return await _transport.LsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Download (negotiate + pack) from the remote.
    /// Matches <c>git_remote_download</c> in <c>remote.c:1323</c>.
    /// </summary>
    /// <param name="refspecs">Optional override refspecs (null to use config).</param>
    /// <param name="options">Fetch options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DownloadAsync(IReadOnlyList<string>? refspecs = null, GitFetchOptions? options = null, CancellationToken cancellationToken = default)
    {
        // C's git_remote_download self-connects (remote.c:1341-1342 via
        // connect_or_reset_options, remote.c:1251-1259), so direct callers
        // work without an explicit connect.
        if (_transport is null)
        {
            var connectOpts = new GitRemoteConnectOptions
            {
                Callbacks = options?.RemoteCallbacks,
                Proxy = options?.ProxyConfig,
                FollowRedirects = options?.FollowRedirects ?? GitRemoteRedirect.Unspecified,
                CustomHeaders = options?.CustomHeaders,
                Depth = options?.Depth ?? 0,
                ScpPortOverride = options?.ScpPortOverride,
            };
            await ConnectAsync(GitDirection.Fetch, connectOpts, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<GitRemoteHead> remoteHeads = await LsAsync(cancellationToken).ConfigureAwait(false);

        // C (fetch.c:106-109): the caller's download_tags overrides the configured remote tagopt only when != UNSPECIFIED. GitFetchOptions defaults
        // DownloadTags to Unspecified, so a default options object must fall back to the configured _downloadTags.
        GitAutoTagOption tagOpt = options is { DownloadTags: not GitAutoTagOption.Unspecified } fetchOpts
            ? fetchOpts.DownloadTags
            : _downloadTags;
        int depth = options?.Depth ?? 0;

        // Build refspecs
        var activeSpecs = new List<GitRefSpec>();
        if (refspecs is { } userSpecs)
        {
            foreach (string spec in userSpecs)
            {
                activeSpecs.Add(GitRefSpec.Parse(spec, isFetch: true));
            }

            // Store active refspecs for UpdateTips
            _activeRefspecs.Clear();
            _activeRefspecs.AddRange(activeSpecs);
        }
        else
        {
            activeSpecs.AddRange(_refspecs);
            _activeRefspecs.Clear();
        }

        // DWIM (git_remote__download → dwim_refspecs, remote.c:125-130):
        // shorthand refspecs are expanded against the advertised heads.
        // With no refspecs at all, C fetches the remote's HEAD
        // (fetch.c:121-130 parses "HEAD" and DWIMs it).
        if (activeSpecs.Count == 0)
        {
            activeSpecs.Add(GitRefSpec.Parse("HEAD", isFetch: true));
        }

        var advertisedNames = remoteHeads.Select(h => h.Name).ToHashSet(StringComparer.Ordinal);

        // C (remote.c:1292-1293): the CONFIGURED refspecs are ALWAYS dwim'd into passive_refspecs; when explicit refspecs were passed, passed_refspecs is set
        // so UpdateTipsAsync runs the opportunistic updates through the passive specs (remote.c:2156-2157).
        _passiveRefspecs.Clear();
        foreach (GitRefSpec spec in _refspecs)
        {
            _passiveRefspecs.Add(spec.DwimOne(advertisedNames));
        }

        _passedRefspecs = refspecs is { Count: > 0 };

        for (int i = 0; i < activeSpecs.Count; i++)
        {
            activeSpecs[i] = activeSpecs[i].DwimOne(advertisedNames);
        }

        _activeRefspecs.Clear();
        _activeRefspecs.AddRange(activeSpecs);

        // Get shallow roots from repo. C (fetch.c:196-198): the negotiator
        // sends the client's existing shallow roots
        // (git_repository__shallow_roots reads the <gitdir>/shallow file).
        // The list is read from the repository so shallow fetches advertise the
        // existing shallow boundary.
        GitOid[] shallowRoots;
        if (_repo.ShallowGrafts is { } shallow)
        {
            // C (repository.c:3793-3797): git_repository__shallow_roots
            // REFRESHES the shallow grafts from disk first — a fetch that
            // rewrote <gitdir>/shallow (unshallow) is visible to the next
            // negotiation.
            await shallow.RefreshAsync(cancellationToken).ConfigureAwait(false);
            shallowRoots = [.. shallow.Oids()];
        }
        else
        {
            shallowRoots = [];
        }

        // Negotiate
        await FetchCoordinator.NegotiateAsync(_transport!, this, remoteHeads, activeSpecs, tagOpt, depth, shallowRoots, cancellationToken).ConfigureAwait(false);

        // Download pack. Reset the shared accumulator in place (matches C's
        // memset(&stats, 0, ...) before git_remote_download); the same
        // instance is mutated by GitPackIndexer.AppendAsync/CommitAsync and
        // read live by the packetsize callback, so remote.Stats reflects the
        // download after FetchAsync returns.
        _stats.TotalObjects = 0;
        _stats.IndexedObjects = 0;
        _stats.ReceivedObjects = 0;
        _stats.LocalObjects = 0;
        _stats.TotalDeltas = 0;
        _stats.IndexedDeltas = 0;
        _stats.ReceivedBytes = 0;
        await FetchCoordinator.DownloadPackAsync(_transport!, this, _stats, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Update local refs from fetched heads and write FETCH_HEAD.
    /// Matches <c>git_remote_update_tips</c> in <c>remote.c:2109</c> +
    /// <c>update_tips_for_spec</c> (remote.c:1925-1975) +
    /// <c>update_one_tip</c> (remote.c:1836-1904) +
    /// <c>git_remote_write_fetchhead</c> (remote.c:1536-1598).
    /// </summary>
    /// <param name="callbacks">Callbacks for update_refs interception.</param>
    /// <param name="updateFetchhead">Whether to write FETCH_HEAD.</param>
    /// <param name="tagOpt">Tag download policy.</param>
    /// <param name="reflogMessage">Reflog message (null = default).</param>
    /// <param name="reportUnchanged">Whether to report unchanged entries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UpdateTipsAsync(
        GitRemoteCallbacks? callbacks = null,
        bool updateFetchhead = true,
        GitAutoTagOption? tagOpt = null,
        string? reflogMessage = null,
        bool reportUnchanged = false,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GitRemoteHead> remoteHeads = await LsAsync(cancellationToken).ConfigureAwait(false);
        GitAutoTagOption effectiveTagOpt = tagOpt ?? _downloadTags;
        string logMsg = reflogMessage ?? $"fetch {_name ?? _url}";

        // Truncate FETCH_HEAD (once, before any spec batch — remote.c:2142-2143).
        // C truncates FETCH_HEAD
        // UNCONDITIONALLY (remote.c:2139-2143) — only the per-spec writes are
        // gated on GIT_REMOTE_UPDATE_FETCHHEAD. Truncation still happens
        // unconditionally, so updateFetchhead=false does not leave stale
        // FETCH_HEAD entries for a later git-CLI merge to consume
        // entries C would not have.
        await GitFetchHead.TruncateAsync(_repo.Path, cancellationToken).ConfigureAwait(false);

        // C (remote.c:2146-2157): with DOWNLOAD_TAGS_ALL the tagspec itself is
        // run as a spec first, so every advertised tag becomes a local tag and
        // a FETCH_HEAD entry.
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);
        if (effectiveTagOpt == GitAutoTagOption.All)
        {
            List<GitFetchHeadEntry> batch = await UpdateTipsForSpecAsync(tagSpec, remoteHeads, effectiveTagOpt, callbacks, logMsg, reportUnchanged, cancellationToken).ConfigureAwait(false);
            if (updateFetchhead && batch.Count > 0)
            {
                await GitFetchHead.AppendAsync(_repo.Path, batch, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (GitRefSpec spec in _activeRefspecs.Count > 0 ? _activeRefspecs : _refspecs)
        {
            if (!spec.IsFetch)
            {
                continue;
            }

            // C appends each spec's sorted FETCH_HEAD batch in spec order
            // (remote.c:2109-2157: truncate once, then per-spec
            // git_fetchhead_write with GIT_FILEBUF_APPEND).
            List<GitFetchHeadEntry> batch = await UpdateTipsForSpecAsync(spec, remoteHeads, effectiveTagOpt, callbacks, logMsg, reportUnchanged, cancellationToken).ConfigureAwait(false);
            if (updateFetchhead && batch.Count > 0)
            {
                await GitFetchHead.AppendAsync(_repo.Path, batch, cancellationToken).ConfigureAwait(false);
            }
        }

        // C (remote.c:2156-2157): when explicit refspecs were passed to download, the configured remote-tracking branches are ALSO updated through the passive
        // (configured) refspecs — without FETCH_HEAD entries.
        if (_passedRefspecs)
        {
            await OpportunisticUpdatesAsync(remoteHeads, callbacks, logMsg, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Updates the configured remote-tracking branches for heads matched by
    /// an explicit (active) refspec. Matches <c>opportunistic_updates</c>
    /// (remote.c:2055-2086) driven by <c>next_head</c> (remote.c:2023-2053):
    /// every advertised head matching at least one active spec also updates
    /// the local ref through EVERY passive spec whose src matches — the ref
    /// write is forced with CAS (C's <c>update_ref</c>, remote.c:1755-1799)
    /// and the update_refs callback fires per update.
    /// </summary>
    private async Task OpportunisticUpdatesAsync(
        IReadOnlyList<GitRemoteHead> remoteHeads,
        GitRemoteCallbacks? callbacks,
        string logMsg,
        CancellationToken cancellationToken)
    {
        foreach (GitRemoteHead head in remoteHeads)
        {
            // C (next_head, remote.c:2030-2033): skip invalid names (which
            // also saves us from tag^{}).
            if (head.Name.EndsWith("^{}", StringComparison.Ordinal))
            {
                continue;
            }

            if (GitReferences.NormalizeName(head.Name) is null)
            {
                continue;
            }

            bool matchedActive = false;
            foreach (GitRefSpec active in _activeRefspecs)
            {
                if (active.SrcMatches(head.Name))
                {
                    matchedActive = true;
                    break;
                }
            }

            if (!matchedActive)
            {
                continue;
            }

            foreach (GitRefSpec passive in _passiveRefspecs)
            {
                if (!passive.SrcMatches(head.Name))
                {
                    continue;
                }

                string refname = passive.Transform(head.Name);

                // C update_ref (remote.c:1755-1799): resolve the current
                // value; an already-equal ref is left alone (no callback).
                GitReference? existing = await _repo.Refs.ResolveAsync(refname, cancellationToken).ConfigureAwait(false);
                GitOid old = existing is GitDirectReference directRef ? directRef.Target : default;
                if (existing is not null && old.Equals(head.Oid))
                {
                    continue;
                }

                if (existing is null)
                {
                    await _repo.Refs.CreateAsync(refname, head.Oid, force: true, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _repo.Refs.SetTargetAsync(existing, head.Oid, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
                }

                if (callbacks?.UpdateRefs is { } updateRefs && !updateRefs(refname, old, head.Oid, passive))
                {
                    throw new GitException(
                        GitErrorCode.Error,
                        "git_remote_fetch callback returned -1",
                        GitErrorCategory.Callback);
                }
            }
        }
    }

    /// <summary>
    /// Updates the tips matching one refspec and returns its FETCH_HEAD
    /// batch. Matches <c>update_tips_for_spec</c> (remote.c:1925-1975).
    /// </summary>
    private async Task<List<GitFetchHeadEntry>> UpdateTipsForSpecAsync(
        GitRefSpec spec,
        IReadOnlyList<GitRemoteHead> remoteHeads,
        GitAutoTagOption tagOpt,
        GitRemoteCallbacks? callbacks,
        string logMsg,
        bool reportUnchanged,
        CancellationToken cancellationToken)
    {
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);
        var updateHeads = new List<GitRemoteHead>();

        foreach (GitRemoteHead head in remoteHeads)
        {
            // C (remote.c:1829-1834, update_tips_for_spec): skip ALL invalid ref names — "which also saves us from tag^{}" — not just the "^{}" suffix,
            // so other malformed advertised names never reach the resolve path. Like C's git_reference_name_is_valid (refs.c:1367-1370) the check
            // allows one-level names ("HEAD").
            if (GitReferences.NormalizeName(head.Name, GitReferenceFormatFlags.AllowOneLevel) is null)
            {
                continue;
            }

            // Skip peeled tag entries — they're for negotiation only.
            if (head.Name.EndsWith("^{}", StringComparison.Ordinal))
            {
                continue;
            }

            await UpdateOneTipAsync(updateHeads, spec, tagSpec, head, tagOpt, callbacks, logMsg, reportUnchanged, cancellationToken).ConfigureAwait(false);
        }

        // C (remote.c:1955-1977, update_tips_for_spec): an explicitly specified OID source updates spec's dst directly (update_ref) and contributes a
        // FETCH_HEAD entry named after the OID.
        GitHashAlgorithmKind oidType = _repo.ObjectFormat;
        int oidHexSize = GitOid.HexSizeFor(oidType);
        if (spec.Source.Length == oidHexSize && GitOid.TryParse(spec.Source, oidType, out GitOid oid))
        {
            if (spec.Destination.Length > 0)
            {
                await UpdateRefForOidSpecAsync(spec, oid, logMsg, callbacks, cancellationToken).ConfigureAwait(false);
            }

            updateHeads.Add(new GitRemoteHead(
                Local: false,
                Oid: oid,
                LocalOid: default,
                Name: spec.Source,
                SymrefTarget: null));
        }

        return await BuildFetchheadForSpecAsync(spec, updateHeads, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the local ref for an OID-source refspec. Matches C's
    /// <c>update_ref</c> (remote.c:1755-1799): a missing ref is created
    /// (force), an existing ref is CAS-updated unless already equal, and the
    /// update_refs callback fires with (refname, old, new, spec).
    /// </summary>
    private async Task UpdateRefForOidSpecAsync(
        GitRefSpec spec,
        GitOid id,
        string logMsg,
        GitRemoteCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        GitReference? existing = await _repo.Refs.ResolveAsync(spec.Destination, cancellationToken).ConfigureAwait(false);
        GitOid oldId = existing is GitDirectReference direct ? direct.Target : default;

        // C (remote.c:1768-1770): already-equal → no update, no callback.
        if (existing is not null && oldId.Equals(id))
        {
            return;
        }

        if (existing is null)
        {
            // C (remote.c:1774-1775): git_reference_create(force=true).
            await _repo.Refs.CreateAsync(spec.Destination, id, force: true, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // C (remote.c:1776-1778): git_reference_create_matching (CAS).
            await _repo.Refs.SetTargetAsync(existing, id, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
        }

        if (callbacks?.UpdateRefs is { } updateRefs && !updateRefs(spec.Destination, oldId, id, spec))
        {
            throw new GitException(
                GitErrorCode.Error,
                "git_remote_fetch callback returned -1",
                GitErrorCategory.Callback);
        }
    }

    /// <summary>
    /// Updates one remote head's local ref (or auto-followed tag). Matches
    /// <c>update_one_tip</c> (remote.c:1836-1904).
    /// </summary>
    private async Task UpdateOneTipAsync(
        List<GitRemoteHead> updateHeads,
        GitRefSpec spec,
        GitRefSpec tagSpec,
        GitRemoteHead head,
        GitAutoTagOption tagOpt,
        GitRemoteCallbacks? callbacks,
        string logMsg,
        bool reportUnchanged,
        CancellationToken cancellationToken)
    {
        string? refname = null;
        bool autotag = false;

        // If we have a tag, see if the auto-follow rules say to update it.
        if (tagSpec.SrcMatches(head.Name))
        {
            if (tagOpt == GitAutoTagOption.Auto)
            {
                autotag = true;
            }

            if (tagOpt != GitAutoTagOption.None)
            {
                refname = head.Name;
            }
        }

        // If we didn't want to auto-follow the tag, check if the refspec matches.
        if (!autotag && spec.SrcMatches(head.Name))
        {
            if (spec.Destination.Length > 0)
            {
                refname = spec.Transform(head.Name);
            }
            else
            {
                // No rhs means store it in FETCH_HEAD, even if we don't
                // update anything else.
                updateHeads.Add(head);
                return;
            }
        }

        // If we still don't have a refname, we don't want it.
        if (string.IsNullOrEmpty(refname))
        {
            return;
        }

        // In autotag mode, only create tags for objects already in db.
        if (autotag && !await _repo.Objects.ExistsAsync(head.Oid, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!autotag)
        {
            updateHeads.Add(head);
        }

        GitReference? existing = await _repo.Refs.ResolveAsync(refname, cancellationToken).ConfigureAwait(false);
        bool notFound = existing is null;
        GitOid old = existing is GitDirectReference directRef ? directRef.Target : default;

        // Fast-forward policy (remote.c:1873-1880): when the existing ref is
        // not an ancestor of the fetched tip and the spec is not forced, the
        // update is silently skipped.
        if (!notFound && !spec.Force)
        {
            // A graph
            // failure — most notably a missing 'old' object (a stale
            // tracking ref pointing at a pruned object) — is TOLERATED by C:
            // a negative git_graph_descendant_of return falsifies the gate
            // and the update PROCEEDS (remote.c:1869-1874).
            bool isAncestor;
            try
            {
                isAncestor = await _repo.DescendantOfAsync(head.Oid, old, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException)
            {
                isAncestor = true; // proceed with the update, like C
            }

            if (!isAncestor)
            {
                return;
            }
        }

        if (notFound)
        {
            old = default;
            if (autotag)
            {
                updateHeads.Add(head);
            }
        }

        bool updated = !old.Equals(head.Oid);

        // C (remote.c:1890-1897): the ref is WRITTEN FIRST, then the callback
        // fires (remote.c:1900-1908). An autotag update against an existing
        // tag fails with EEXISTS, which C tolerates and which skips the
        // callback entirely (goto done).
        if (updated)
        {
            if (autotag)
            {
                if (notFound)
                {
                    await _repo.Refs.CreateAsync(refname, head.Oid, force: false, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    return; // EEXISTS tolerated — no callback (C: goto done)
                }
            }
            else if (notFound)
            {
                await _repo.Refs.CreateAsync(refname, head.Oid, force: spec.Force, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _repo.Refs.SetTargetAsync(existing!, head.Oid, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (!reportUnchanged || callbacks?.UpdateRefs is null)
        {
            // C (remote.c:1900-1903): no callbacks, or an unchanged tip
            // without GIT_REMOTE_UPDATE_REPORT_UNCHANGED → skip the callback.
            return;
        }

        // C (remote.c:1904-1911): the update_refs callback fires AFTER the
        // ref write; a rejection (nonzero return) fails the whole fetch with
        // "git_remote_fetch callback returned -1" (GIT_ERROR_CALLBACK).
        if (callbacks?.UpdateRefs is { } updateRefs && !updateRefs(refname, old, head.Oid, spec))
        {
            throw new GitException(
                GitErrorCode.Error,
                "git_remote_fetch callback returned -1",
                GitErrorCategory.Callback);
        }
    }

    /// <summary>
    /// Appends the FETCH_HEAD batch for one spec, determining the single
    /// merge entry. Matches <c>git_remote_write_fetchhead</c>
    /// (remote.c:1536-1598): for a wildcard spec the local HEAD's upstream
    /// (branch.&lt;name&gt;.remote/merge) is resolved and reverse-transformed
    /// through the spec; for a non-wildcard spec the head matching the spec
    /// src is the merge.
    /// </summary>
    private async Task<List<GitFetchHeadEntry>> BuildFetchheadForSpecAsync(
        GitRefSpec spec,
        List<GitRemoteHead> updateHeads,
        CancellationToken cancellationToken)
    {
        // No heads, nothing to do.
        if (updateHeads.Count == 0)
        {
            return [];
        }

        // Iff refspec is refs/heads/* (but not subdir slash star), include
        // all fetched heads.
        bool includeAllFetchheads = spec.Source == "refs/heads/*";

        GitRemoteHead? mergeRemoteRef;
        // C's git_refspec_is_wildcard requires the src to END with '*' — a
        // mid-pattern glob like refs/heads/*/x is non-wildcard for the
        // merge decision (refspec.c:350-355).
        if (spec.IsWildcardByC)
        {
            // Determine what to merge: if the refspec was a wildcard, use
            // the local HEAD's upstream.
            string? localHeadName = await ResolveLocalHeadNameAsync(cancellationToken).ConfigureAwait(false);
            if (localHeadName is not null)
            {
                string? remoteRefName = await RefToUpdateAsync(spec, localHeadName, cancellationToken).ConfigureAwait(false);
                if (remoteRefName is not null)
                {
                    mergeRemoteRef = updateHeads.FirstOrDefault(h => h.Name == remoteRefName);
                }
                else
                {
                    mergeRemoteRef = null;
                }
            }
            else
            {
                mergeRemoteRef = null;
            }
        }
        else
        {
            // If we're fetching a single refspec, that's the only thing that
            // should be in FETCH_HEAD.
            mergeRemoteRef = updateHeads.FirstOrDefault(h => h.Name == spec.Source);
        }

        var entries = new List<GitFetchHeadEntry>(updateHeads.Count);
        foreach (GitRemoteHead remoteRef in updateHeads)
        {
            bool mergeThisFetchhead = mergeRemoteRef is not null && mergeRemoteRef.Name == remoteRef.Name;

            if (!includeAllFetchheads && !spec.SrcMatches(remoteRef.Name) && !mergeThisFetchhead)
            {
                continue;
            }

            entries.Add(new GitFetchHeadEntry(
                Oid: remoteRef.Oid,
                IsMerge: mergeThisFetchhead,
                RefName: remoteRef.Name,
                RemoteUrl: GitFetchHead.SanitizeRemoteUrl(_url ?? string.Empty)));
        }

        return entries;
    }

    /// <summary>
    /// Resolves the local HEAD to a branch ref name. Matches the head of
    /// <c>remote_head_for_ref</c> (remote.c:1471-1497): an unborn branch
    /// (symbolic HEAD whose target does not exist) uses the symbolic target.
    /// </summary>
    private async Task<string?> ResolveLocalHeadNameAsync(CancellationToken cancellationToken)
    {
        GitReference? head = await _repo.Refs.LookupAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is GitDirectReference direct)
        {
            return direct.NameKey.ToUtf8StringStrict();
        }

        if (head is GitSymbolicReference symbolic)
        {
            // Try to resolve; an unborn branch keeps the symbolic target.
            GitReference? resolved = await _repo.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
            if (resolved is GitDirectReference resolvedDirect)
            {
                return resolvedDirect.NameKey.ToUtf8StringStrict();
            }

            return symbolic.TargetNameKey.ToUtf8StringStrict();
        }

        return null;
    }

    /// <summary>
    /// Decides whether the local branch <paramref name="refName"/> tracks
    /// this remote through <paramref name="spec"/>, and if so returns the
    /// remote ref name to mark as the merge entry. Matches
    /// <c>ref_to_update</c> + <c>git_branch__upstream_name</c>
    /// (remote.c:1457-1490, branch.c:427-487).
    /// </summary>
    private async Task<string?> RefToUpdateAsync(GitRefSpec spec, string refName, CancellationToken cancellationToken)
    {
        if (!refName.StartsWith("refs/heads/", StringComparison.Ordinal) || _name is null)
        {
            return null;
        }

        string branchName = refName["refs/heads/".Length..];
        // byte-domain compare — C's ref_to_update (remote.c:1469) uses git__strcmp over the raw value bytes.
        byte[]? upstreamRemote = await _repo.Config.GetBytesAsync($"branch.{branchName}.remote", cancellationToken).ConfigureAwait(false);
        if (upstreamRemote is null || !ConfigKeyName.AsciiEquals(upstreamRemote, Encoding.UTF8.GetBytes(_name)))
        {
            return null;
        }

        byte[]? upstreamMerge = await _repo.Config.GetBytesAsync($"branch.{branchName}.merge", cancellationToken).ConfigureAwait(false);
        if (upstreamMerge is null)
        {
            return null;
        }

        // git_branch__upstream_name: the merge value is transformed through the remote's matching fetch refspec (branch.c:463-473). The refspec pipeline runs
        // in the byte domain (C wildmatches and substitutes raw bytes); the result is decoded once at the wire-name boundary.
        byte[] upstreamName = upstreamMerge;
        foreach (GitRefSpec rs in _refspecs)
        {
            if (rs.IsFetch && rs.SrcMatches(upstreamMerge))
            {
                upstreamName = rs.Transform(upstreamMerge);
                break;
            }
        }

        if (!spec.DstMatches(upstreamName))
        {
            return null;
        }

        return Encoding.UTF8.GetString(spec.ReverseTransform(upstreamName));
    }

    /// <summary>
    /// Fetch from the remote: connect + download + disconnect + update tips + prune.
    /// Matches <c>git_remote_fetch</c> in <c>remote.c:1352</c>.
    /// </summary>
    /// <param name="refspecs">Optional override refspecs.</param>
    /// <param name="options">Fetch options.</param>
    /// <param name="reflogMessage">Custom reflog message (null = default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task FetchAsync(
        IReadOnlyList<string>? refspecs = null,
        GitFetchOptions? options = null,
        string? reflogMessage = null,
        CancellationToken cancellationToken = default)
    {
        var connectOpts = new GitRemoteConnectOptions
        {
            Callbacks = options?.RemoteCallbacks,
            Proxy = options?.ProxyConfig,
            FollowRedirects = options?.FollowRedirects ?? GitRemoteRedirect.Unspecified,
            CustomHeaders = options?.CustomHeaders,
            Depth = options?.Depth ?? 0,
            ScpPortOverride = options?.ScpPortOverride,
        };

        // Connect or reset options
        await ConnectAsync(GitDirection.Fetch, connectOpts, cancellationToken).ConfigureAwait(false);

        GitAutoTagOption tagOpt = options?.DownloadTags ?? GitAutoTagOption.Unspecified;
        bool updateFetchhead = options?.UpdateFetchhead ?? true;

        // Download
        await DownloadAsync(refspecs, options, cancellationToken).ConfigureAwait(false);

        // Update tips — must be BEFORE DisconnectAsync, because
        // UpdateTipsAsync calls LsAsync which reads the cached heads from
        // the transport, and DisconnectAsync clears those heads.
        await UpdateTipsAsync(
            callbacks: options?.RemoteCallbacks,
            updateFetchhead: updateFetchhead,
            tagOpt: tagOpt == GitAutoTagOption.Unspecified ? null : tagOpt,
            reflogMessage: reflogMessage,
            reportUnchanged: options?.ReportUnchanged ?? false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Prune — also BEFORE DisconnectAsync: the smart
        // transport clears its cached heads on close, so a post-disconnect
        // LsAsync returns an empty list and prune would delete every
        // tracking ref. (C prunes after disconnect because its transport
        // caches t->refs across close; moving the prune up produces the
        // same observable result for every transport.)
        bool shouldPrune = options?.Prune switch
        {
            GitFetchPrune.Prune => true,
            GitFetchPrune.NoPrune => false,
            _ => _pruneRefs,
        };

        if (shouldPrune)
        {
            await PruneAsync(options?.RemoteCallbacks, cancellationToken).ConfigureAwait(false);
        }

        // Disconnect
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Upload (push) to the remote: connect + push + status. Does not update
    /// local tracking refs or disconnect — the caller is responsible for
    /// calling <see cref="UpdateTipsAsync"/> and <see cref="DisconnectAsync"/> if needed.
    /// Matches <c>git_remote_upload</c> in <c>remote.c:2970</c>.
    /// </summary>
    /// <param name="refspecs">Optional override push refspecs (null to use config push refspecs).</param>
    /// <param name="options">Push options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitPushResult> UploadAsync(
        IReadOnlyList<string>? refspecs = null,
        GitPushOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        (GitPushResult result, _) = await UploadCoreAsync(refspecs, options, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Shared push-upload core returning both the result and the resolved
    /// push specs (needed by <see cref="PushAsync"/> for update-tips).
    /// </summary>
    private async Task<(GitPushResult Result, IReadOnlyList<GitPushSpec> Specs)> UploadCoreAsync(
        IReadOnlyList<string>? refspecs,
        GitPushOptions? options,
        CancellationToken cancellationToken)
    {
        // C's git_remote_upload self-connects (remote.c:2991-2992 via
        // connect_or_reset_options), so direct callers work without an
        // explicit connect.
        if (_transport is null)
        {
            var connectOpts = new GitRemoteConnectOptions
            {
                Callbacks = options?.RemoteCallbacks,
                Proxy = options?.ProxyConfig,
                FollowRedirects = options?.FollowRedirects ?? GitRemoteRedirect.Unspecified,
                CustomHeaders = options?.CustomHeaders,
                ScpPortOverride = options?.ScpPortOverride,
            };
            await ConnectAsync(GitDirection.Push, connectOpts, cancellationToken).ConfigureAwait(false);
        }

        using var push = new PushCoordinator(this);

        // Determine which refspecs to use
        IReadOnlyList<byte[]>? specs = refspecs is null ? null : [.. refspecs.Select(s => Encoding.UTF8.GetBytes(s))];
        if (specs is null)
        {
            // Load push refspecs from config. C: a missing key is GIT_ENOTFOUND — no configured push specs. byte-domain read (C reads config values as raw
            // bytes).
            if (_name is not null)
            {
                try
                {
                    specs = await _repo.Config.GetMultiBytesAsync($"remote.{_name}.push", cancellationToken).ConfigureAwait(false);
                }
                catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
                {
                    specs = [];
                }
            }
            else
            {
                specs = [];
            }
        }

        foreach (byte[] spec in specs)
        {
            await push.AddRefSpecAsync(spec, cancellationToken).ConfigureAwait(false);
        }

        // C (push.c:460): git_packbuilder_set_threads(push->pb, push->pb_parallelism) — the option is threaded into the pack writer.
        await push.FinishAsync(_transport!, options?.RemoteCallbacks, options?.RemotePushOptions, options?.PbParallelism ?? 1, cancellationToken).ConfigureAwait(false);

        // Snapshot the specs — PushCoordinator.Dispose clears its list.
        return (push.Result ?? new GitPushResult { UnpackOk = false, Status = [] }, [.. push.Specs]);
    }

    /// <summary>
    /// Push to the remote: connect + upload + update tips + disconnect.
    /// Matches <c>git_remote_push</c> in <c>remote.c:3045</c>.
    /// </summary>
    /// <param name="refspecs">Optional override push refspecs (null to use config push refspecs).</param>
    /// <param name="options">Push options.</param>
    /// <param name="reflogMessage">Custom reflog message (null = default "push").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitPushResult> PushAsync(
        IReadOnlyList<string>? refspecs = null,
        GitPushOptions? options = null,
        string? reflogMessage = null,
        CancellationToken cancellationToken = default)
    {
        var connectOpts = new GitRemoteConnectOptions
        {
            Callbacks = options?.RemoteCallbacks,
            Proxy = options?.ProxyConfig,
            FollowRedirects = options?.FollowRedirects ?? GitRemoteRedirect.Unspecified,
            CustomHeaders = options?.CustomHeaders,
            ScpPortOverride = options?.ScpPortOverride,
        };

        // Connect or reset options for push
        await ConnectAsync(GitDirection.Push, connectOpts, cancellationToken).ConfigureAwait(false);

        // Upload
        (GitPushResult result, IReadOnlyList<GitPushSpec> pushSpecs) = await UploadCoreAsync(refspecs, options, cancellationToken).ConfigureAwait(false);

        // Update local tracking refs (push.c:203-214, git_push_update_tips).
        if (result.Status.Count > 0)
        {
            await UpdatePushTipsAsync(result, pushSpecs, options?.RemoteCallbacks, reflogMessage, cancellationToken).ConfigureAwait(false);
        }

        // Disconnect
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary> Updates local tracking refs after a push, based on the push result. Matches <c>git_push_update_tips</c> (push.c:203-238): per successful
    /// status the local tracking ref is updated ("update by push" reflog) and the update_refs callback fires with (refname, roid, loid, push-spec refspec); a
    /// rejected callback fails the push with GIT_ERROR_CALLBACK. </summary>
    private async Task UpdatePushTipsAsync(
        GitPushResult result,
        IReadOnlyList<GitPushSpec> pushSpecs,
        GitRemoteCallbacks? callbacks,
        string? reflogMessage,
        CancellationToken cancellationToken)
    {
        string logMsg = reflogMessage ?? "update by push";

        // C's git_push_update_tips matches against the ACTIVE (dwimmed) refspecs
        // (push.c:211-214, remote.c:2631-2643), so a shorthand fetch spec like
        // "master" matches the pushed refs/heads/master. Dwim the configured specs
        // against the advertised heads (as git_remote_upload does,
        // remote.c:2995-2996) when the download path hasn't populated the
        // active specs.
        // The active specs may be raw (the push path does not dwim them);
        // dwim whichever source is used (idempotent for already-dwimmed
        // specs - the src already starts with "refs/").
        IReadOnlyList<GitRemoteHead> heads = await LsAsync(cancellationToken).ConfigureAwait(false);
        var advertisedNames = heads.Select(h => h.Name).ToHashSet(StringComparer.Ordinal);
        var fetchSpecs = (_activeRefspecs.Count > 0 ? _activeRefspecs : _refspecs)
            .Select(s => s.DwimOne(advertisedNames)).ToList();

        foreach (GitPushStatus status in result.Status)
        {
            // Skip unsuccessful updates (push.c:206-207: status->msg non-empty).
            if (!status.Ok)
            {
                continue;
            }

            // Find the matching fetch refspec (git_remote__matching_refspec,
            // push.c:211-214 — C dwims the CONFIGURED refspecs at upload
            // time, so the configured fetch specs are the source of truth).
            GitRefSpec? fetchSpec = null;
            foreach (GitRefSpec rs in fetchSpecs)
            {
                if (rs.IsFetch && rs.SrcMatches(status.Ref))
                {
                    fetchSpec = rs;
                    break;
                }
            }

            if (fetchSpec is null)
            {
                continue;
            }

            string localRef = fetchSpec.Transform(status.Ref);

            // Find the push spec whose dst is this ref (push.c:217-221).
            GitPushSpec? pushSpec = null;
            foreach (GitPushSpec spec in pushSpecs)
            {
                if (spec.RefSpec.Destination == status.Ref)
                {
                    pushSpec = spec;
                    break;
                }
            }

            if (pushSpec is null)
            {
                continue;
            }

            // C (push.c:224-234): a deletion push deletes the local tracking
            // ref, tolerating a missing ref (GIT_ENOTFOUND → no callback);
            // a create/update uses git_reference_create(force=1).
            int fireCallback = 1;
            if (pushSpec.Loid.IsZero)
            {
                GitReference? remoteRef = await _repo.Refs.LookupAsync(localRef, cancellationToken).ConfigureAwait(false);
                if (remoteRef is null)
                {
                    // GIT_ENOTFOUND is swallowed and the callback is skipped
                    // (push.c:229-233).
                    fireCallback = 0;
                }
                else
                {
                    await _repo.Refs.DeleteAsync(localRef, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await _repo.Refs.CreateAsync(localRef, pushSpec.Loid, force: true, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
            }

            if (fireCallback == 0 || callbacks?.UpdateRefs is null)
            {
                continue;
            }

            // C (push.c:236-250): callbacks->update_refs(localRef, &roid,
            // &loid, &push_spec->refspec, payload) — the PUSH spec's
            // refspec; an error fails the push (GIT_ERROR_CALLBACK).
            if (!callbacks.UpdateRefs(localRef, pushSpec.Roid, pushSpec.Loid, pushSpec.RefSpec))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "git_remote_push callback returned -1",
                    GitErrorCategory.Callback);
            }
        }
    }

    /// <summary> Prune local tracking refs that no longer exist on the remote. Matches <c>git_remote_prune</c> in <c>remote.c:1641</c>: <c>prune_candidates</c>
    /// (remote.c:1605-1628) collects local refs matching the DESTINATION of an active refspec, then a candidate is KEPT when ANY active spec reverse-transforms
    /// it to an advertised head (remote.c:1669-1699); symrefs are never pruned and each deletion fires <c>callbacks-&gt;update_refs(refname, &amp;old,
    /// &amp;zero, NULL)</c> (remote.c:1734-1740). </summary>
    public async Task PruneAsync(GitRemoteCallbacks? callbacks = null, CancellationToken cancellationToken = default)
    {
        if (_transport is null)
        {
            throw new GitException(GitErrorCode.Invalid, "the remote is not connected", GitErrorCategory.Net);
        }

        IReadOnlyList<GitRemoteHead> remoteHeads = await LsAsync(cancellationToken).ConfigureAwait(false);
        var remoteRefNames = new HashSet<RefNameKey>(remoteHeads.Select(h => (RefNameKey)h.Name));

        // C (prune_candidates, remote.c:1616-1622): git_remote__matching_dst_
        // refspec uses the ACTIVE (dwim'd) refspecs — no fallback to the raw
        // configured specs. Active specs also exclude push specs.
        List<GitRefSpec> pruneSpecs = _activeRefspecs;

        // Collect candidates: local refs whose name matches an active spec's
        // destination (deduplicated — C's candidates vector holds each ref
        // name once from git_reference_list).
        var candidates = new List<RefNameKey>();
        var seen = new HashSet<RefNameKey>();
        foreach (GitRefSpec spec in pruneSpecs)
        {
            if (!spec.IsFetch)
            {
                continue;
            }

            await foreach (GitReference localRef in _repo.Refs.ListAsync(spec.Destination, cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(localRef.NameKey))
                {
                    candidates.Add(localRef.NameKey);
                }
            }
        }

        // C (remote.c:1665-1700): remove candidates for which SOME active
        // spec reverse-transforms to an advertised head — any match keeps the
        // ref.
        foreach (RefNameKey refName in candidates)
        {
            bool keep = false;
            foreach (GitRefSpec spec in pruneSpecs)
            {
                if (!spec.IsFetch)
                {
                    continue;
                }

                try
                {
                    if (remoteRefNames.Contains(RefNameKey.From(spec.ReverseTransform(refName.Bytes.ToArray()))))
                    {
                        // Found a source — keep this candidate.
                        keep = true;
                        break;
                    }
                }
                catch (GitException)
                {
                    // Ref doesn't match the refspec pattern — try next spec.
                }
            }

            if (keep)
            {
                continue;
            }

            // No spec reverse-transforms to an advertised head — prune.
            GitReference? ref_ = await _repo.Refs.LookupAsync(refName, cancellationToken).ConfigureAwait(false);
            if (ref_ is null)
            {
                // GIT_ENOTFOUND — "as we want it gone, let's not consider
                // this an error" (remote.c:1716-1718).
                continue;
            }

            if (ref_ is GitSymbolicReference)
            {
                // Never prune symrefs (origin/HEAD etc.) — remote.c:1722-1725.
                continue;
            }

            GitOid oldId = ref_ is GitDirectReference direct ? direct.Target : default;
            await _repo.Refs.DeleteAsync(refName, cancellationToken).ConfigureAwait(false);

            // C (remote.c:1734-1746): update_refs(refname, &old, &zero, NULL)
            // fires after the delete; a failure aborts the prune with
            // GIT_ERROR_CALLBACK.
            if (callbacks?.UpdateRefs is { } updateRefs && !updateRefs(refName.ToString(), oldId, default, null))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    "git_remote_fetch callback returned -1",
                    GitErrorCategory.Callback);
            }
        }
    }

    /// <summary>Stop an in-progress operation.</summary>
    public void Stop()
    {
        _transport?.Cancel();
    }

    /// <summary>
    /// Set the tag download policy.
    /// Matches <c>git_remote_set_autotag</c> in <c>remote.c:2292</c>.
    /// </summary>
    public async Task SetAutoTagAsync(GitAutoTagOption value, CancellationToken cancellationToken = default)
    {
        _downloadTags = value;

        if (_name is not null)
        {
            string key = $"remote.{_name}.tagopt";
            switch (value)
            {
                case GitAutoTagOption.None:
                    await _repo.Config.SetStringAsync(key, "--no-tags", cancellationToken).ConfigureAwait(false);
                    break;
                case GitAutoTagOption.All:
                    await _repo.Config.SetStringAsync(key, "--tags", cancellationToken).ConfigureAwait(false);
                    break;
                case GitAutoTagOption.Auto:
                case GitAutoTagOption.Unspecified:
                    await _repo.Config.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Set the fetch URL.</summary>
    public async Task SetUrlAsync(string? url, CancellationToken cancellationToken = default)
    {
        // C (remote.c:142-147, 651-658, set_url): a NULL url deletes the config entry; an EMPTY url fails canonicalize_url with GIT_EINVALIDSPEC "cannot set
        // empty URL"; a non-empty url is canonicalized before being stored.
        if (url is null)
        {
            _url = null;
            if (_name is not null)
            {
                await _repo.Config.DeleteAsync($"remote.{_name}.url", cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        string canonical = CanonicalizeUrl(url);
        _url = canonical;
        if (_name is not null)
        {
            await _repo.Config.SetStringAsync($"remote.{_name}.url", canonical, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Set the push URL.</summary>
    public async Task SetPushUrlAsync(string? url, CancellationToken cancellationToken = default)
    {
        _pushUrl = url;
        if (_name is not null)
        {
            if (url is null)
            {
                await _repo.Config.DeleteAsync($"remote.{_name}.pushurl", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _repo.Config.SetStringAsync($"remote.{_name}.pushurl", url, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [GeneratedRegex("^$", RegexOptions.None)]
    private static partial Regex EmptyStringRegex { get; }

    /// <summary>Add a fetch refspec to config.</summary>
    public async Task AddFetchAsync(string spec, CancellationToken cancellationToken = default)
    {
        var rs = GitRefSpec.Parse(spec, isFetch: true);
        _refspecs.Add(rs);
        if (_name is not null)
        {
            await _repo.Config.SetMultiAsync($"remote.{_name}.fetch", EmptyStringRegex, spec, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Add a push refspec to config.</summary>
    public async Task AddPushAsync(string spec, CancellationToken cancellationToken = default)
    {
        if (_name is not null)
        {
            await _repo.Config.SetMultiAsync($"remote.{_name}.push", EmptyStringRegex, spec, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Look up a remote by name from config. Matches <c>git_remote_lookup</c>.
    /// </summary>
    internal static async Task<GitRemote> LookupAsync(GitRepository repo, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        // C (remote.c:477, 88-102): ensure_remote_name_is_valid runs FIRST — an invalid name is GIT_EINVALIDSPEC (class GIT_ERROR_CONFIG), not GIT_ENOTFOUND.
        if (!NameIsValid(name))
        {
            throw new GitException(GitErrorCode.InvalidSpec,
                $"'{name}' is not a valid remote name.", GitErrorCategory.Config);
        }

        GitConfiguration cfg = repo.Config;
        string? url = await cfg.GetStringAsync($"remote.{name}.url", cancellationToken).ConfigureAwait(false);
        string? configuredPushUrl = await cfg.GetStringAsync($"remote.{name}.pushurl", cancellationToken).ConfigureAwait(false);

        // C (remote.c:497-527): the "does not exist" error fires only when NEITHER key is present — a pushurl-only (push-only) remote looks up fine with a NULL
        // fetch URL (url is null, pushurl is set).
        if (url is null && configuredPushUrl is null)
        {
            throw new GitException(GitErrorCode.NotFound, $"remote '{name}' does not exist", GitErrorCategory.Config);
        }

        // C (remote.c:508-535): insteadOf is applied to non-empty values —
        // the fetch URL at lookup time (and to the push URL — from the url
        // with the PUSH suffix, or from a configured pushurl with the FETCH
        // suffix).
        string? rewrittenUrl = url is { Length: > 0 }
            ? await ApplyInsteadOfAsync(repo, url, GitDirection.Fetch, useDefaultIfEmpty: true, cancellationToken).ConfigureAwait(false)
            : null;
        string? pushUrl = url is { Length: > 0 }
            ? await ApplyInsteadOfAsync(repo, url, GitDirection.Push, useDefaultIfEmpty: false, cancellationToken).ConfigureAwait(false)
            : null;
        if (configuredPushUrl is { Length: > 0 })
        {
            pushUrl = await ApplyInsteadOfAsync(repo, configuredPushUrl, GitDirection.Fetch, useDefaultIfEmpty: true, cancellationToken).ConfigureAwait(false);
        }

        var remote = new GitRemote(repo, name, rewrittenUrl)
        {
            _pushUrl = pushUrl,
        };

        // Load fetch refspecs. C (git_remote__get_fetch_refspecs): a missing key is GIT_ENOTFOUND — the remote simply has no configured specs. byte-domain
        // fetch-spec read (C reads config values as raw bytes, config.c git_config_get_multivar_foreach).
        IReadOnlyList<byte[]> fetchSpecs;
        try
        {
            fetchSpecs = await cfg.GetMultiBytesAsync($"remote.{name}.fetch", cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            fetchSpecs = [];
        }

        foreach (byte[] spec in fetchSpecs)
        {
            remote._refspecs.Add(GitRefSpec.Parse(spec, isFetch: true));
        }

        // C (remote.c:477-480, git_remote_lookup): the configured refspecs
        // are DWIM'd into active_refspecs at lookup time. With no advertised
        // refs yet (the transport is not connected), dwim_refspecs is a raw
        // copy — the specs are expanded against the advertised heads later
        // during fetch. PruneAsync relies on active_refspecs being populated
        // after a bare ConnectAsync (remote.c:1616-1622).
        remote._activeRefspecs.Clear();
        remote._activeRefspecs.AddRange(remote._refspecs);

        // Load tagopt. byte-domain (C's download_tags_value strcmps raw bytes, remote.c:78-81 — case-SENSITIVE).
        byte[]? tagopt = await cfg.GetBytesAsync($"remote.{name}.tagopt", cancellationToken).ConfigureAwait(false);
        if (tagopt is not null && ConfigKeyName.AsciiEquals(tagopt, "--no-tags"u8))
        {
            remote._downloadTags = GitAutoTagOption.None;
        }
        else if (tagopt is not null && ConfigKeyName.AsciiEquals(tagopt, "--tags"u8))
        {
            remote._downloadTags = GitAutoTagOption.All;
        }
        else
        {
            remote._downloadTags = GitAutoTagOption.Auto;
        }

        // Load prune config
        string? pruneVal = await cfg.GetStringAsync($"remote.{name}.prune", cancellationToken).ConfigureAwait(false);
        if (pruneVal is not null)
        {
            remote._pruneRefs = await cfg.GetBoolAsync($"remote.{name}.prune", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string? fetchPruneVal = await cfg.GetStringAsync("fetch.prune", cancellationToken).ConfigureAwait(false);
            if (fetchPruneVal is not null)
            {
                remote._pruneRefs = await cfg.GetBoolAsync("fetch.prune", cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        return remote;
    }

    /// <summary> Create a new remote in config. Matches <c>git_remote_create</c> — the 3-arg convenience entry point (remote.c:323-345): name validation, then
    /// <c>git_remote_create_with_opts</c> with the default fetchspec. An empty URL fails with GIT_ERROR (class GIT_ERROR_INVALID) in this entry point (the
    /// canonicalize failure is mapped to -1). </summary>
    internal static async Task<GitRemote> CreateAsync(GitRepository repo, string name, string url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(url);

        // C (remote.c:327-328): those 2 tests are duplicated here because of
        // backward-compatibility.
        if (!NameIsValid(name))
        {
            throw new GitException(GitErrorCode.InvalidSpec,
                $"'{name}' is not a valid remote name.", GitErrorCategory.Config);
        }

        // C (remote.c:329-333): canonicalize_url failure (empty URL) is
        // mapped to GIT_ERROR (-1) with the GIT_ERROR_INVALID class.
        try
        {
            CanonicalizeUrl(url);
        }
        catch (GitException) when (string.IsNullOrEmpty(url))
        {
            throw new GitException(GitErrorCode.Error, "cannot set empty URL", GitErrorCategory.Invalid);
        }

        return await CreateWithOptsAsync(repo, url, new GitRemoteCreateOptions { Name = name }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Create a remote with options. Matches <c>git_remote_create_with_opts</c> (remote.c:197-316): name validation + does-not-exist check, URL
    /// canonicalization, insteadOf rewriting (unless <see cref="GitRemoteCreateFlags.SkipInsteadOf"/>), config write for named remotes, and a fetchspec that is
    /// either the custom one, the default (unless <see cref="GitRemoteCreateFlags.SkipDefaultFetchSpec"/>), or nothing for anonymous remotes. An anonymous
    /// remote (no name) writes no config and downloads no tags. </summary>
    internal static async Task<GitRemote> CreateWithOptsAsync(GitRepository repo, string url, GitRemoteCreateOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(url);
        options ??= new GitRemoteCreateOptions();

        string? name = options.Name;
        if (name is not null)
        {
            // C (remote.c:210-213, ensure_remote_name_is_valid).
            if (!NameIsValid(name))
            {
                throw new GitException(GitErrorCode.InvalidSpec,
                    $"'{name}' is not a valid remote name.", GitErrorCategory.Config);
            }

            // C (remote.c:214-218, ensure_remote_doesnot_exist).
            bool exists = true;
            try
            {
                await LookupAsync(repo, name, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
            {
                exists = false;
            }

            if (exists)
            {
                throw new GitException(GitErrorCode.Exists,
                    $"remote '{name}' already exists", GitErrorCategory.Config);
            }
        }

        // Canonicalize the URL (remote.c:237-245): rejects empty URLs with
        // GIT_EINVALIDSPEC "cannot set empty URL" in the with-opts entry.
        string canonicalUrl = CanonicalizeUrl(url);

        // C (remote.c:246-257): insteadOf is applied to the in-memory URL at
        // create time unless SKIP_INSTEADOF; the config stores the canonical
        // original.
        string rewrittenUrl;
        string? pushUrl;
        if ((options.Flags & GitRemoteCreateFlags.SkipInsteadOf) != 0)
        {
            rewrittenUrl = canonicalUrl;
            pushUrl = null;
        }
        else
        {
            rewrittenUrl = (await ApplyInsteadOfAsync(repo, canonicalUrl, GitDirection.Fetch, useDefaultIfEmpty: true, cancellationToken).ConfigureAwait(false))!;
            pushUrl = await ApplyInsteadOfAsync(repo, canonicalUrl, GitDirection.Push, useDefaultIfEmpty: false, cancellationToken).ConfigureAwait(false);
        }

        // C (remote.c:258-263): the url is only written for named remotes.
        if (name is not null)
        {
            await repo.Config.SetStringAsync($"remote.{name}.url", canonicalUrl, cancellationToken).ConfigureAwait(false);
        }

        var remote = new GitRemote(repo, name, rewrittenUrl)
        {
            _pushUrl = pushUrl,
        };

        // C (remote.c:265-293): a custom fetchspec, or the default unless
        // SKIP_DEFAULT_FETCHSPEC; anonymous remotes get none.
        bool hasCustomSpec = options.FetchRefSpecs is { Count: > 0 };
        if (hasCustomSpec || (name is not null && (options.Flags & GitRemoteCreateFlags.SkipDefaultFetchSpec) == 0))
        {
            if (hasCustomSpec)
            {
                foreach (string spec in options.FetchRefSpecs!)
                {
                    remote._refspecs.Add(GitRefSpec.Parse(spec, isFetch: true));
                }

                // Only write for named remotes with a repository
                // (remote.c:281-285).
                if (name is not null)
                {
                    foreach (string spec in options.FetchRefSpecs)
                    {
                        await repo.Config.SetMultiAsync($"remote.{name}.fetch", EmptyStringRegex, spec, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                string defaultSpec = DefaultFetchSpec(name!);
                remote._refspecs.Add(GitRefSpec.Parse(defaultSpec, isFetch: true));
                await repo.Config.SetStringAsync($"remote.{name}.fetch", defaultSpec, cancellationToken).ConfigureAwait(false);
            }
        }

        // C (remote.c:295-299): a remote without a name doesn't download tags.
        if (name is null)
        {
            remote._downloadTags = GitAutoTagOption.None;
        }

        // C (remote.c:285-291, lookup_remote_prune_config): the prune config
        // (remote.<name>.prune, falling back to fetch.prune) is read at
        // create time.
        if (name is not null)
        {
            string? pruneVal = await repo.Config.GetStringAsync($"remote.{name}.prune", cancellationToken).ConfigureAwait(false);
            if (pruneVal is not null)
            {
                remote._pruneRefs = await repo.Config.GetBoolAsync($"remote.{name}.prune", cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string? fetchPruneVal = await repo.Config.GetStringAsync("fetch.prune", cancellationToken).ConfigureAwait(false);
                if (fetchPruneVal is not null)
                {
                    remote._pruneRefs = await repo.Config.GetBoolAsync("fetch.prune", cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return remote;
    }

    /// <summary> Create an anonymous (unnamed) remote. Matches <c>git_remote_create_anonymous</c> (remote.c:363-368): no config writes, no default fetchspec,
    /// download_tags = NONE. </summary>
    internal static async Task<GitRemote> CreateAnonymousAsync(GitRepository repo, string url, CancellationToken cancellationToken = default)
        => await CreateWithOptsAsync(repo, url, null, cancellationToken).ConfigureAwait(false);

    /// <summary>List all remote names from config.</summary>
    internal static async Task<IReadOnlyList<string>> ListAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        // C (remote.c:2270-2271): git_config_foreach_match "^remote\\..*\\.(push)?url$" — only remotes with a url/pushurl key are listed; the result is sorted
        // (git_vector + git__strcmp_cb) and deduplicated.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        await foreach (GitConfigEntry entry in repo.Config.EnumerateAsync(@"^remote\..*\.(push)?url$", cancellationToken).ConfigureAwait(false))
        {
            // extract the remote name from the raw name bytes (C's all_iter name bytes) and decode at the egress — a non-UTF-8 remote name surfaces as U+FFFD
            // in the string list (display tier).
            AddRemoteName(names, entry.NameBytes);
        }

        return [.. names];
    }

    private static void AddRemoteName(SortedSet<string> names, ReadOnlyMemory<byte> entryName)
    {
        // Extract remote name from "remote.<name>.url" / "remote.<name>.pushurl".
        ReadOnlySpan<byte> rest = entryName.Span["remote.".Length..];
        int dot = rest.LastIndexOf((byte)'.');
        if (dot > 0)
        {
            names.Add(Encoding.UTF8.GetString(rest[..dot]));
        }
    }

    // ==============================
    // Remote management
    // ==============================

    /// <summary>
    /// Deletes a remote from config, removes tracking branches, and cleans up
    /// branch upstream configuration. Matches <c>git_remote_delete</c>
    /// (remote.c:2884-2897).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="name">The remote name to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task DeleteAsync(GitRepository repo, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(name);

        await RemoveBranchConfigRelatedEntriesAsync(repo, name, cancellationToken).ConfigureAwait(false);
        await RemoveRemoteTrackingAsync(repo, name, cancellationToken).ConfigureAwait(false);
        // byte-key section delete — C's rename_remote_config_section (remote.c:2340-2360) builds "remote.%s" from the raw name bytes and calls
        // git_config_rename_section(..., NULL); the byte overload keeps non-UTF-8 remote names byte-exact.
        byte[] sectionBytes = [.. "remote."u8, .. Encoding.UTF8.GetBytes(name)];
        await repo.Config.DeleteSectionAsync(sectionBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames a remote: moves the config section, updates branch upstream
    /// config, renames tracking refs, and rewrites fetch refspecs.
    /// Matches <c>git_remote_rename</c> (remote.c:2561-2599).
    /// </summary>
    /// <param name="repo">The repository.</param>
    /// <param name="oldName">The current remote name.</param>
    /// <param name="newName">The new remote name.</param>
    /// <returns>A list of non-default fetch refspecs that could not be
    /// automatically rewritten (their destination does not match
    /// <c>refs/remotes/&lt;old&gt;/*</c>).</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<IReadOnlyList<string>> RenameAsync(
        GitRepository repo, string oldName, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);

        // Validate old remote exists.
        await LookupAsync(repo, oldName, cancellationToken).ConfigureAwait(false);

        // Validate new name.
        if (!NameIsValid(newName))
        {
            throw new GitException(GitErrorCode.InvalidSpec,
                $"'{newName}' is not a valid remote name.", GitErrorCategory.Config);
        }

        // Ensure new name doesn't already exist. C (remote.c:2572,
        // ensure_remote_doesnot_exist): git_remote_lookup — a remote with
        // url/pushurl config exists, anything else is GIT_EEXISTS.
        bool exists = true;
        try
        {
            await LookupAsync(repo, newName, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            exists = false;
        }

        if (exists)
        {
            throw new GitException(GitErrorCode.Exists,
                $"remote '{newName}' already exists", GitErrorCategory.Config);
        }

        // Rename config section remote.<old> -> remote.<new>. byte-key rename — C's rename_remote_config_section (remote.c:2340-2360) builds "remote.%s" from
        // the raw name bytes; the byte overload keeps non-UTF-8 remote names byte-exact.
        byte[] oldSectionBytes = [.. "remote."u8, .. Encoding.UTF8.GetBytes(oldName)];
        byte[] newSectionBytes = [.. "remote."u8, .. Encoding.UTF8.GetBytes(newName)];
        await repo.Config.RenameSectionAsync(oldSectionBytes, newSectionBytes, cancellationToken).ConfigureAwait(false);

        // Update branch.<name>.remote config entries.
        await UpdateBranchRemoteConfigEntryAsync(repo, oldName, newName, cancellationToken).ConfigureAwait(false);

        // Rename tracking refs refs/remotes/<old>/* -> refs/remotes/<new>/*.
        await RenameRemoteReferencesAsync(repo, oldName, newName, cancellationToken).ConfigureAwait(false);

        // Rewrite fetch refspecs; collect non-default ones as problems. C (remote.c:2528-2549, rename_fetch_refspecs): the default spec is rewritten with
        // git_config_set_string, which FAILS on a multivar key ("entry is not unique due to being a multivar", GIT_ERROR_CONFIG) — a remote with the default
        // fetchspec plus extra fetch refspecs errors the whole rename. Non-default specs are reported as problems and left in place (the section
        // rename already moved them under the new name).
        var problems = new List<string>();
        string oldDefaultSpec = DefaultFetchSpec(oldName);
        string newDefaultSpec = DefaultFetchSpec(newName);
        // C's rename_fetch_refspecs (remote.c:2528) compares the raw spec bytes with strcmp — byte-domain compare; the problem list decodes each spec for
        // display only.
        byte[] oldDefaultSpecBytes = Encoding.UTF8.GetBytes(oldDefaultSpec);
        IReadOnlyList<byte[]> oldFetchSpecs = await repo.Config.GetMultiBytesAsync($"remote.{newName}.fetch", cancellationToken).ConfigureAwait(false);
        foreach (byte[] spec in oldFetchSpecs)
        {
            if (ConfigKeyName.AsciiEquals(spec, oldDefaultSpecBytes))
            {
                // Default refspec — rewrite with new name.
                await repo.Config.SetStringAsync($"remote.{newName}.fetch", newDefaultSpec, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Non-default refspec — report as problem.
                problems.Add(Encoding.UTF8.GetString(spec));
            }
        }

        return problems;
    }

    /// <summary>
    /// Returns the remote's default branch by resolving the HEAD symref.
    /// The remote must be connected. Matches <c>git_remote_default_branch</c>
    /// (remote.c:2899-2968).
    /// </summary>
    /// <returns>The default branch ref name (e.g.
    /// <c>refs/heads/master</c>), or null if no default can be determined.
    /// </returns>
    public async Task<string?> DefaultBranchAsync(CancellationToken cancellationToken = default)
    {
        if (_transport is null)
        {
            throw new GitException(GitErrorCode.Invalid,
                "the remote is not connected", GitErrorCategory.Net);
        }

        IReadOnlyList<GitRemoteHead> heads = await LsAsync(cancellationToken).ConfigureAwait(false);
        if (heads.Count == 0 || heads[0].Name != "HEAD")
        {
            return null;
        }

        // If HEAD has a symref target, use it directly.
        if (heads[0].SymrefTarget is { } symref && symref.Length > 0)
        {
            return symref;
        }

        // No symref info — guess from the heads matching HEAD's OID.
        GitOid headId = heads[0].Oid;
        string localDefault = await _repo.InitialBranchAsync(cancellationToken).ConfigureAwait(false);
        GitRemoteHead? guess = null;

        for (int i = 1; i < heads.Count; i++)
        {
            if (heads[i].Oid != headId)
            {
                continue;
            }

            if (!heads[i].Name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                continue;
            }

            if (guess is null)
            {
                guess = heads[i];
                continue;
            }

            if (heads[i].Name == localDefault)
            {
                guess = heads[i];
                break;
            }
        }

        return guess?.Name;
    }

    /// <summary>
    /// Validates a remote name. Matches <c>git_remote_name_is_valid</c>
    /// (remote.c:2601-2629).
    /// </summary>
    /// <param name="remoteName">The name to validate.</param>
    /// <returns><c>true</c> if valid; <c>false</c> otherwise.</returns>
    public static bool NameIsValid(string? remoteName)
    {
        if (string.IsNullOrEmpty(remoteName))
        {
            return false;
        }

        // Build a refspec that uses the name and see if it parses.
        try
        {
            GitRefSpec.Parse($"refs/heads/test:refs/remotes/{remoteName}/test", isFetch: true);
            return true;
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.InvalidSpec)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies <c>url.&lt;base&gt;.insteadOf</c> or
    /// <c>url.&lt;base&gt;.pushInsteadOf</c> rewriting to a URL.
    /// Matches <c>apply_insteadof</c> (remote.c:3079-3142).
    /// </summary>
    /// <param name="repo">The repository (for config access).</param>
    /// <param name="url">The URL to rewrite.</param>
    /// <param name="direction">Fetch or push direction.</param>
    /// <param name="useDefaultIfEmpty">If true and no match, return the
    /// original URL; if false and no match, return null.</param>
    /// <returns>The rewritten URL, or null if no rewrite applies and
    /// <paramref name="useDefaultIfEmpty"/> is false.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<string?> ApplyInsteadOfAsync(
        GitRepository repo, string url, GitDirection direction, bool useDefaultIfEmpty = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(url);

        string suffix = direction == GitDirection.Push ? "pushinsteadof" : "insteadof";
        byte[] dotSuffixBytes = Encoding.UTF8.GetBytes("." + suffix);
        string? bestMatch = null;
        int matchLength = 0;
        string? bestBase = null;

        // byte-domain compare — C's apply_insteadof (remote.c:3079-3142) runs git__prefixcmp over the raw value bytes and tracks strlen(entry->value) (byte
        // length). The URL is encoded to UTF-8 bytes once; the value bytes are the parity surface.
        byte[] urlBytes = Encoding.UTF8.GetBytes(url);

        await foreach (GitConfigEntry entry in repo.Config.EnumerateAsync("url.*", cancellationToken).ConfigureAwait(false))
        {
            if (entry.ValueBytes is not { } valueBytes)
            {
                continue;
            }

            // Check if this is an insteadof/pushinsteadof entry. byte-domain suffix test on the raw name bytes (C's strrchr over the char* name).
            if (!entry.NameBytes.Span.EndsWith(dotSuffixBytes))
            {
                continue;
            }

            // The value is the prefix to match; the base URL is in the section name.
            if (!urlBytes.AsSpan().StartsWith(valueBytes.Span))
            {
                continue;
            }

            int n = valueBytes.Length;
            if (n <= matchLength)
            {
                continue;
            }

            matchLength = n;
            bestMatch = Encoding.UTF8.GetString(valueBytes.Span);

            // Extract <base> from "url.<base>.<suffix>".
            // Section name format: url.<base>.insteadof
            // We need to strip "url." prefix and ".<suffix>" suffix.
            ReadOnlySpan<byte> middle = entry.NameBytes.Span["url.".Length..];
            int dotIdx = middle.LastIndexOf(dotSuffixBytes);
            if (dotIdx > 0)
            {
                bestBase = Encoding.UTF8.GetString(middle[..dotIdx]);
            }
        }

        if (matchLength == 0)
        {
            return useDefaultIfEmpty ? url : null;
        }

        if (bestMatch is null || bestBase is null)
        {
            return useDefaultIfEmpty ? url : null;
        }

        return bestBase + Encoding.UTF8.GetString(urlBytes.AsSpan(matchLength));
    }

    /// <summary>
    /// Canonicalizes a URL. Matches <c>canonicalize_url</c> (remote.c:142-164).
    /// Rejects empty URLs. On Windows, converts UNC paths (<c>\\server\path</c>)
    /// to forward slashes (<c>//server/path</c>). On other platforms, passes
    /// through unchanged.
    /// </summary>
    public static string CanonicalizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            // C (remote.c:142-164): GIT_ERROR_INVALID class with
            // GIT_EINVALIDSPEC code.
            throw new GitException(GitErrorCode.InvalidSpec,
                "cannot set empty URL", GitErrorCategory.Invalid);
        }

        if (OperatingSystem.IsWindows() && url.Length >= 3 &&
            url[0] == '\\' && url[1] == '\\' &&
            (char.IsLetterOrDigit(url[2])))
        {
            return url.Replace('\\', '/');
        }

        return url;
    }

    /// <summary>
    /// Returns the default fetch refspec for a remote name:
    /// <c>+refs/heads/*:refs/remotes/&lt;name&gt;/*</c>.
    /// Matches <c>default_fetchspec_for_name</c> (remote.c:166-172).
    /// </summary>
    public static string DefaultFetchSpec(string name)
        => $"+refs/heads/*:refs/remotes/{name}/*";

    /// <summary>
    /// Creates a deep copy of this remote. Matches <c>git_remote_dup</c>
    /// (remote.c:377-424).
    /// </summary>
    public GitRemote Duplicate()
    {
        var dup = new GitRemote(_repo, _name, _url)
        {
            _pushUrl = _pushUrl,
            _downloadTags = _downloadTags,
            _pruneRefs = _pruneRefs,
        };

        foreach (GitRefSpec spec in _refspecs)
        {
            dup._refspecs.Add(spec);
        }

        return dup;
    }

    // ==============================
    // Remote management helpers
    // ==============================

    /// <summary>
    /// Removes branch upstream config entries (remote + merge) for the
    /// given remote name. Matches <c>remove_branch_config_related_entries</c>
    /// (remote.c:2753-2809).
    /// </summary>
    private static async Task RemoveBranchConfigRelatedEntriesAsync(GitRepository repo, string remoteName, CancellationToken cancellationToken)
    {
        var keysToDelete = new List<(ReadOnlyMemory<byte> remoteKey, ReadOnlyMemory<byte> mergeKey)>();

        await foreach (GitConfigEntry entry in repo.Config.EnumerateAsync("branch.*.remote", cancellationToken).ConfigureAwait(false))
        {
            // byte-domain compare — C's remove_branch_config_related_entries (remote.c:2753-2809) uses strcmp over the raw value bytes.
            if (entry.ValueBytes is not { } valueBytes || !ConfigKeyName.AsciiEquals(valueBytes.Span, Encoding.UTF8.GetBytes(remoteName)))
            {
                continue;
            }

            // Extract branch name from "branch.<name>.remote". C's name_offset (remote.c:2742-2751) uses strchr — the FIRST dot — so "branch.v1.2.remote"
            // yields branch "v1" (the LAST dot would yield "v1.2"). byte-domain extraction on the raw name bytes.
            ReadOnlySpan<byte> middle = entry.NameBytes.Span["branch.".Length..];
            int dot = middle.IndexOf((byte)'.');
            if (dot <= 0)
            {
                continue;
            }

            ReadOnlyMemory<byte> branchName = entry.NameBytes.Slice("branch.".Length, dot);
            // C deletes the FIRST-dot-derived keys (branch.<name>.remote and branch.<name>.merge) — NOT the matched entry itself, so a dotted branch name
            // ("v1.2") leaves its own keys stale. The keys are built from the raw branch bytes (C's git_str_printf, remote.c:2783) and deleted via the byte-key
            // API.
            keysToDelete.Add((
                ConfigKeyName.BuildNameBytes("branch"u8, branchName.Span, "remote"u8),
                ConfigKeyName.BuildNameBytes("branch"u8, branchName.Span, "merge"u8)));
        }

        foreach ((ReadOnlyMemory<byte> remoteKey, ReadOnlyMemory<byte> mergeKey) in keysToDelete)
        {
            try
            {
                await repo.Config.DeleteAsync(mergeKey, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException) { }
            try
            {
                await repo.Config.DeleteAsync(remoteKey, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException) { }
        }
    }

    /// <summary>
    /// Removes all remote tracking refs matching the remote's refspecs.
    /// Matches <c>remove_remote_tracking</c> (remote.c:2858-2882).
    /// </summary>
    private static async Task RemoveRemoteTrackingAsync(GitRepository repo, string remoteName, CancellationToken cancellationToken)
    {
        GitRemote remote = await LookupAsync(repo, remoteName, cancellationToken).ConfigureAwait(false);
        var refsToRemove = new List<RefNameKey>();

        // C (remote.c:2858-2882): remove_remote_tracking iterates ALL refspecs — fetch AND push (git_remote_lookup loads both into the refspec vector). A push
        // refspec like +refs/heads/*:refs/heads/* therefore removes matching LOCAL branches (after git_remote_delete, refs/heads/master is
        // GIT_ENOTFOUND). The port's _refspecs holds only fetch specs, so the configured push specs are loaded here to mirror C's combined iteration.
        var specs = new List<GitRefSpec>(remote._refspecs);
        try
        {
            // byte-domain push-spec read (C reads config values as raw bytes).
            IReadOnlyList<byte[]> pushSpecs = await repo.Config.GetMultiBytesAsync($"remote.{remoteName}.push", cancellationToken).ConfigureAwait(false);
            foreach (byte[] spec in pushSpecs)
            {
                specs.Add(GitRefSpec.Parse(spec, isFetch: false));
            }
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            // No push specs configured.
        }

        foreach (GitRefSpec spec in specs)
        {
            await foreach (GitReference localRef in repo.Refs.ListAsync(spec.Destination, cancellationToken).ConfigureAwait(false))
            {
                refsToRemove.Add(localRef.NameKey);
            }
        }

        foreach (RefNameKey refName in refsToRemove)
        {
            try
            {
                await repo.Refs.DeleteAsync(refName, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException) { }
        }
    }

    /// <summary>
    /// Updates <c>branch.*.remote</c> config entries from old to new remote name.
    /// Matches <c>update_branch_remote_config_entry</c> (remote.c:2387-2403).
    /// </summary>
    private static async Task UpdateBranchRemoteConfigEntryAsync(
        GitRepository repo, string oldName, string newName, CancellationToken cancellationToken)
    {
        var updates = new List<(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)>();

        await foreach (GitConfigEntry entry in repo.Config.EnumerateAsync("branch.*.remote", cancellationToken).ConfigureAwait(false))
        {
            // byte-domain compare — C's update_remote_name_cb (remote.c:2380) uses strcmp over the raw value bytes.
            if (entry.ValueBytes is not { } valueBytes || !ConfigKeyName.AsciiEquals(valueBytes.Span, Encoding.UTF8.GetBytes(oldName)))
            {
                continue;
            }

            // set under the RAW existing name bytes (C's git_config_set_multivar over the char* name) — a non-UTF-8 branch subsection round-trips byte-exact.
            updates.Add((entry.NameBytes, Encoding.UTF8.GetBytes(newName)));
        }

        foreach ((ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value) in updates)
        {
            await repo.Config.SetBytesAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renames tracking references from <c>refs/remotes/&lt;old&gt;/*</c>
    /// to <c>refs/remotes/&lt;new&gt;/*</c>. Also fixes symref targets.
    /// Matches <c>rename_remote_references</c> (remote.c:2466-2493).
    /// </summary>
    private static async Task RenameRemoteReferencesAsync(
        GitRepository repo, string oldName, string newName, CancellationToken cancellationToken)
    {
        string oldPrefix = $"refs/remotes/{oldName}/";
        string newPrefix = $"refs/remotes/{newName}/";

        var refsToRename = new List<GitReference>();

        await foreach (GitReference ref_ in repo.Refs.ListAsync($"refs/remotes/{oldName}/*", cancellationToken).ConfigureAwait(false))
        {
            refsToRename.Add(ref_);
        }

        foreach (GitReference ref_ in refsToRename)
        {
            var newName2 = RefNameKey.From((byte[])[.. Encoding.UTF8.GetBytes(newPrefix), .. ref_.NameBytes.Span[Encoding.UTF8.GetByteCount(oldPrefix)..]]);
            GitReference renamed = await repo.Refs.RenameAsync(ref_, newName2, force: true,
                logMessage: $"renamed remote {oldName} to {newName}", cancellationToken).ConfigureAwait(false);

            // If it's a symbolic ref (e.g. origin/HEAD -> origin/master),
            // fix its target too.
            if (renamed is GitSymbolicReference sym)
            {
                ReadOnlyMemory<byte> target = sym.TargetNameBytes;
                if (target.Span.StartsWith(Encoding.UTF8.GetBytes(oldPrefix)))
                {
                    var newTarget = RefNameKey.From((byte[])[.. Encoding.UTF8.GetBytes(newPrefix), .. target.Span[Encoding.UTF8.GetByteCount(oldPrefix)..]]);
                    try
                    {
                        await repo.Refs.CreateSymbolicAsync(renamed.NameKey, newTarget,
                            force: true,
                            logMessage: $"renamed remote {oldName} to {newName}", cancellationToken).ConfigureAwait(false);
                    }
                    catch (GitException) { }
                }
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        _transport = null;
        _disposed = true;
    }
}
