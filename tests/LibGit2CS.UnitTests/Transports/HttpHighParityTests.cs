using System.Net;
using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

/// <summary> Regression tests for the HTTP behaviors (libgit2 1.9.4): (auth scheme selected by acquired credential type), (challenge
/// threading into the auth context), (GitRemoteRedirect enum values), (http.followRedirects config), (proxy default None). </summary>
public sealed class HttpHighParityTests : IDisposable
{
    private readonly string _tempDir;

    public HttpHighParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_HttpHigh_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
        internal int _requestCount;
        internal string? _lastAuthorization;

        internal ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requestCount++;
            _lastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(_responder(request, _requestCount));
        }
    }

    private static HttpResponseMessage Unauthorized(string wwwAuthenticate)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ParseAdd(wwwAuthenticate);
        return response;
    }

    private static HttpResponseMessage Ok(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-git-upload-pack-advertisement");
        return response;
    }

    [Fact]
    public async Task Auth_NegotiateAndBasicAdvertised_UserPassCredential_UsesBasic()
    {
        // C acquires the credential with the UNION of the offered schemes' credtypes, then best_scheme_and_challenge picks the scheme matching the
        // ACQUIRED credential's type. A user/pass credential with Negotiate+Basic advertised must fall back to Basic (httpclient.c:464-487).
        var handler = new ScriptedHandler((request, count) =>
        {
            if (request.Headers.Authorization is { Scheme: "Basic" })
            {
                return Ok("0000");
            }

            return Unauthorized("Negotiate, Basic realm=\"test\"");
        });

        GitContext ctx = new();
        await using var transport = new GitHttpTransport(ctx, handler);
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitUserPassCredential("user", "pass")),
            },
        };

        await using IGitSubtransportStream stream = await transport.ActionAsync(
            "http://example.com/repo", GitSmartService.UploadPackLs, options, CancellationToken.None);

        // Drive the request (first ReadAsync sends the request and handles 401).
        byte[] buf = new byte[16];
        int n = await stream.ReadAsync(buf, CancellationToken.None);

        Assert.True(n > 0, "expected the 200 response body to be readable");
        Assert.StartsWith("Basic ", handler._lastAuthorization, StringComparison.Ordinal);
        // Two requests: the initial (401) and the authenticated retry.
        Assert.Equal(2, handler._requestCount);
    }

    [Fact]
    public void Auth_SchemeSelection_MatchesAcquiredCredentialType()
    {
        // scheme selection runs AFTER credential acquisition and matches the acquired credential's type (best_scheme_and_challenge, httpclient.c:464-487).
        var negotiateBasic = new List<AuthChallenge>
        {
            new(GitAuthSchemeType.Negotiate, null),
            new(GitAuthSchemeType.Basic, "realm=\"test\""),
        };
        using var userPass = new GitUserPassCredential("u", "p");
        using var defaultCred = new GitDefaultCredential();

        // Negotiate+Basic advertised + user/pass → Basic (Negotiate needs
        // DEFAULT).
        Assert.Equal(GitAuthSchemeType.Basic, AuthHandlers.SelectBestScheme(negotiateBasic, userPass));

        // Negotiate+Basic + DEFAULT credential → Negotiate (priority).
        Assert.Equal(GitAuthSchemeType.Negotiate, AuthHandlers.SelectBestScheme(negotiateBasic, defaultCred));

        // NTLM+Basic + user/pass → NTLM (NTLM outranks Basic in auth_schemes).
        var ntlmBasic = new List<AuthChallenge>
        {
            new(GitAuthSchemeType.Ntlm, null),
            new(GitAuthSchemeType.Basic, "realm=\"test\""),
        };
        Assert.Equal(GitAuthSchemeType.Ntlm, AuthHandlers.SelectBestScheme(ntlmBasic, userPass));

        // Negotiate-only + user/pass → none (no scheme accepts userpass).
        var negotiateOnly = new List<AuthChallenge> { new(GitAuthSchemeType.Negotiate, null) };
        Assert.Equal(GitAuthSchemeType.None, AuthHandlers.SelectBestScheme(negotiateOnly, userPass));

        // Per-scheme credtype masks.
        Assert.Equal(GitCredentialType.Default, AuthHandlers.CredentialTypesFor(GitAuthSchemeType.Negotiate));
        Assert.Equal(GitCredentialType.UserPassPlaintext, AuthHandlers.CredentialTypesFor(GitAuthSchemeType.Ntlm));
        Assert.Equal(GitCredentialType.UserPassPlaintext, AuthHandlers.CredentialTypesFor(GitAuthSchemeType.Basic));
    }

    [Fact]
    public void Auth_NtlmChallenge_ExtractedWithParameters()
    {
        // the challenge for the context's scheme must carry the server's parameters (e.g. the NTLM type-2 blob) so the multi-round exchange can complete
        // (challenge_for_context, httpclient.c:492-508).
        string type2 = "TlRMTVNTUAACAAAAAAAAACgAAAABggAAUnifiedS";
        var challenges = new List<AuthChallenge>
        {
            new(GitAuthSchemeType.Basic, "realm=\"test\""),
            new(GitAuthSchemeType.Ntlm, type2),
        };

        Assert.Equal("NTLM " + type2, AuthHandlers.ChallengeForScheme(challenges, GitAuthSchemeType.Ntlm));
        Assert.Equal("Basic realm=\"test\"", AuthHandlers.ChallengeForScheme(challenges, GitAuthSchemeType.Basic));

        // A challenge without parameters yields the bare scheme name.
        var bare = new List<AuthChallenge> { new(GitAuthSchemeType.Negotiate, null) };
        Assert.Equal("Negotiate", AuthHandlers.ChallengeForScheme(bare, GitAuthSchemeType.Negotiate));
        Assert.Null(AuthHandlers.ChallengeForScheme(bare, GitAuthSchemeType.Ntlm));
    }

    [Fact]
    public async Task Auth_NtlmChallenge_ReachesAuthContext()
    {
        // the end-to-end NTLM flow — a userpass credential selects NTLM (userpass matches NTLM's credtypes) and the port drives the BCL NegotiateAuthentication
        // with the threaded challenge. The provider is unavailable in this environment (GetOutgoingBlob → Unsupported/InvalidToken), so the flow surfaces the
        // port's GitException(Auth) — the scheme-selection and context-creation plumbing is what is exercised.
        string type2 = "TlRMTVNTUAACAAAAAAAAACgAAAABggAAUnifiedS";
        var handler = new ScriptedHandler((request, count) => Unauthorized($"NTLM {type2}"));

        GitContext ctx = new();
        await using var transport = new GitHttpTransport(ctx, handler);
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitUserPassCredential("user", "pass")),
            },
        };

        await using IGitSubtransportStream stream = await transport.ActionAsync(
            "http://example.com/repo", GitSmartService.UploadPackLs, options, CancellationToken.None);

        byte[] buf = new byte[16];
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await stream.ReadAsync(buf, CancellationToken.None));

        Assert.Equal(GitErrorCategory.Net, ex.Category);
        Assert.Equal(GitAuthSchemeType.Ntlm, transport.ServerAuthSchemes);
    }

    [Fact]
    public void RedirectEnum_ValuesMatchC()
    {
        // C (remote.h:54-65): NONE=1, INITIAL=2, ALL=4; 0 is unspecified (config lookup).
        Assert.Equal(0, (int)GitRemoteRedirect.Unspecified);
        Assert.Equal(1, (int)GitRemoteRedirect.None);
        Assert.Equal(2, (int)GitRemoteRedirect.Initial);
        Assert.Equal(4, (int)GitRemoteRedirect.All);

        // Follow semantics.
        Assert.False(GitHttpTransport.AllowRedirect(GitRemoteRedirect.None, isInitial: true));
        Assert.True(GitHttpTransport.AllowRedirect(GitRemoteRedirect.Initial, isInitial: true));
        Assert.False(GitHttpTransport.AllowRedirect(GitRemoteRedirect.Initial, isInitial: false));
        Assert.True(GitHttpTransport.AllowRedirect(GitRemoteRedirect.All, isInitial: false));
    }

    [Fact]
    public async Task Redirect_FollowRedirectsConfig_MapsLikeC()
    {
        // with follow_redirects unspecified (the C# default), the connect normalization reads http.followRedirects (lookup_redirect_config, remote.c:866-904):
        // missing → INITIAL; bool → ALL/NONE; "initial" → INITIAL; invalid → GIT_ERROR_CONFIG.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(_tempDir, "r");
        await using GitRepository repo = await GitRepository.InitAsync(dir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);
        await using GitRemote remote = await repo.RemoteCreateAsync("origin", "ssh://h/r", TestContext.Current.CancellationToken);

        // Missing → Initial.
        Assert.Equal(GitRemoteRedirect.Initial, await remote.ResolveFollowRedirectsConfigAsync(ct));

        await repo.Config.SetStringAsync("http.followRedirects", "false", ct);
        Assert.Equal(GitRemoteRedirect.None, await remote.ResolveFollowRedirectsConfigAsync(ct));

        await repo.Config.SetStringAsync("http.followRedirects", "true", ct);
        Assert.Equal(GitRemoteRedirect.All, await remote.ResolveFollowRedirectsConfigAsync(ct));

        await repo.Config.SetStringAsync("http.followRedirects", "yes", ct);
        Assert.Equal(GitRemoteRedirect.All, await remote.ResolveFollowRedirectsConfigAsync(ct));

        await repo.Config.SetStringAsync("http.followRedirects", "initial", ct);
        Assert.Equal(GitRemoteRedirect.Initial, await remote.ResolveFollowRedirectsConfigAsync(ct));

        await repo.Config.SetStringAsync("http.followRedirects", "INITIAL", ct);
        Assert.Equal(GitRemoteRedirect.Initial, await remote.ResolveFollowRedirectsConfigAsync(ct));

        await repo.Config.SetStringAsync("http.followRedirects", "bogus", ct);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await remote.ResolveFollowRedirectsConfigAsync(ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
        Assert.Contains("http.followRedirects", ex.Message);
    }

    [Fact]
    public void Redirect_EnumDefaults_AreUnspecified()
    {
        // The options defaults are Unspecified (C zero-init), so the connect normalization consults the config.
        Assert.Equal(GitRemoteRedirect.Unspecified, new GitFetchOptions().FollowRedirects);
        Assert.Equal(GitRemoteRedirect.Unspecified, new GitRemoteConnectOptions().FollowRedirects);
        Assert.Equal(GitRemoteRedirect.Unspecified, new GitPushOptions().FollowRedirects);
    }

    [Fact]
    public void ProxyConfig_DefaultType_IsNone()
    {
        // GIT_PROXY_OPTIONS_INIT zero-inits type = GIT_PROXY_NONE — the managed default must not proxy through the system proxy (Auto would proxy
        // whenever HTTP(S)_PROXY was set).
        Assert.Equal(GitProxyType.None, new GitProxyConfig().Type);
    }
}
