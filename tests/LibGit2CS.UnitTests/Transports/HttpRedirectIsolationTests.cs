using System.Net;
using System.Net.Http.Headers;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

public sealed class HttpRedirectIsolationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CrossOrigin_RetryRequiresDestinationCredentials(bool acceptDestination)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var context = new GitContext();
        var callbackUrls = new List<string?>();
        GitRemoteConnectOptions options = Options((_, _, url, _) =>
        {
            callbackUrls.Add(url);
            return Task.FromResult<GitCredential?>(url == "https://a.example/repo"
                ? new GitUserPassCredential("a", "a-password")
                : acceptDestination ? new GitUserPassCredential("b", "b-password") : null);
        });
        using var handler = new ScriptedHandler((request, count) =>
        {
            if (count <= 2)
            {
                Assert.Equal("a.example", request.RequestUri!.Host);
                AssertCustomHeaders(request, expected: true);
                Assert.Equal(count == 1 ? null : Basic("a:a-password"), request.Headers.Authorization?.ToString());
                return count == 1 ? Unauthorized() : Redirect("https://b.example/repo");
            }

            Assert.Equal("b.example", request.RequestUri!.Host);
            AssertCustomHeaders(request, expected: false);
            Assert.Equal(count == 3 ? null : Basic("b:b-password"), request.Headers.Authorization?.ToString());
            return count == 3 ? Unauthorized() : Success(request);
        });
        await using var transport = new GitHttpTransport(context, handler);
        await using IGitSubtransportStream stream = await transport.ActionAsync(
            "https://a.example/repo", GitSmartService.UploadPackLs, options, ct);

        if (acceptDestination)
        {
            Assert.Equal(4, await stream.ReadAsync(new byte[16], ct));
            Assert.Equal(4, handler.RequestCount);
            // The caller still supplies the original URL for subsequent services.
            await using IGitSubtransportStream post = await transport.ActionAsync(
                "https://a.example/repo", GitSmartService.UploadPack, options, ct);
            await post.WriteAsync("0000"u8.ToArray(), ct);
            Assert.Equal(4, await post.ReadAsync(new byte[16], ct));
            Assert.Equal(5, handler.RequestCount);
        }
        else
        {
            GitException error = await Assert.ThrowsAsync<GitException>(() => stream.ReadAsync(new byte[16], ct));
            Assert.Equal(GitErrorCode.Auth, error.Code);
            Assert.Equal(3, handler.RequestCount);
        }

        Assert.Equal(["https://a.example/repo", "https://b.example/repo"], callbackUrls);
    }

    [Theory]
    [InlineData("https://a.example/repo", "/other", true, "a.example", "/other/info/refs")]
    [InlineData("https://a.example/repo", "//b.example/other", true, "a.example", "//b.example/other/info/refs")]
    [InlineData("https://a.example/repo", "https://A.EXAMPLE:443/other", true, "a.example", "/other/info/refs")]
    [InlineData("https://a.example/repo", "https://b.example/other", false, "b.example", "/other/info/refs")]
    [InlineData("https://a.example/repo", "https://a.example:444/other", false, "a.example", "/other/info/refs")]
    [InlineData("http://a.example/repo", "https://a.example/other", false, "a.example", "/other/info/refs")]
    public async Task Redirect_RequestUsesOriginBoundAuthentication(
        string initialUrl, string location, bool sameOrigin, string expectedHost, string expectedPath)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var context = new GitContext();
        using var handler = new ScriptedHandler((request, count) =>
        {
            if (count == 1)
            {
                return Unauthorized();
            }

            if (count == 2)
            {
                Assert.Equal(Basic("a:a-password"), request.Headers.Authorization?.ToString());
                return Redirect(location);
            }

            Assert.Equal(expectedHost, request.RequestUri!.Host);
            Assert.Equal(expectedPath, request.RequestUri.AbsolutePath);
            Assert.Equal(sameOrigin ? Basic("a:a-password") : null, request.Headers.Authorization?.ToString());
            AssertCustomHeaders(request, sameOrigin);
            return Success(request);
        });
        await using var transport = new GitHttpTransport(context, handler);
        await using IGitSubtransportStream stream = await transport.ActionAsync(
            initialUrl, GitSmartService.UploadPackLs, Options(), ct);
        Assert.Equal(4, await stream.ReadAsync(new byte[16], ct));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task RedirectChain_CustomHeadersReturnOnlyAtOriginalOrigin()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var context = new GitContext();
        int callbacks = 0;
        GitRemoteConnectOptions options = Options((_, _, url, _) =>
        {
            Assert.Equal("https://a.example/repo", url);
            callbacks++;
            return Task.FromResult<GitCredential?>(new GitUserPassCredential("a", "a-password"));
        });
        using var handler = new ScriptedHandler((request, count) =>
        {
            AssertCustomHeaders(request, expected: count != 3);
            Assert.Equal(count is 2 or 5 ? Basic("a:a-password") : null, request.Headers.Authorization?.ToString());
            return count switch
            {
                1 or 4 => Unauthorized(),
                2 => Redirect("https://b.example/repo"),
                3 => Redirect("https://a.example/repo"),
                _ => Success(request),
            };
        });
        await using var transport = new GitHttpTransport(context, handler);
        await using IGitSubtransportStream stream = await transport.ActionAsync(
            "https://a.example/repo", GitSmartService.UploadPackLs, options, ct);
        Assert.Equal(4, await stream.ReadAsync(new byte[16], ct));
        Assert.Equal(5, handler.RequestCount);
        Assert.Equal(2, callbacks);
    }

    [Fact]
    public async Task CustomAuthorization_IsSentOnlyToOriginalOrigin()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var context = new GitContext();
        var options = new GitRemoteConnectOptions
        {
            CustomHeaders = ["aUtHoRiZaTiOn: Bearer synthetic-token"],
        };
        using var handler = new ScriptedHandler((request, count) =>
        {
            Assert.Equal(count == 2 ? null : "Bearer synthetic-token", request.Headers.Authorization?.ToString());
            return count switch
            {
                1 => Redirect("https://b.example/repo"),
                2 => Redirect("https://a.example/repo"),
                _ => Success(request),
            };
        });
        await using var transport = new GitHttpTransport(context, handler);
        await using IGitSubtransportStream stream = await transport.ActionAsync(
            "https://a.example/repo", GitSmartService.UploadPackLs, options, ct);
        Assert.Equal(4, await stream.ReadAsync(new byte[16], ct));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task UrlCredentials_AreNotReusedAfterOriginChangeOrReturn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var context = new GitContext();
        var callbackUrls = new List<string?>();
        GitRemoteConnectOptions options = Options((_, username, url, _) =>
        {
            Assert.Null(username);
            callbackUrls.Add(url);
            return Task.FromResult<GitCredential?>(new GitUserPassCredential("callback", "password"));
        });
        using var handler = new ScriptedHandler((request, count) =>
        {
            Assert.Equal(count switch
            {
                2 => Basic("url:secret"),
                4 or 6 => Basic("callback:password"),
                _ => null,
            }, request.Headers.Authorization?.ToString());
            if (count >= 3)
            {
                Assert.Empty(request.RequestUri!.UserInfo);
            }

            return count switch
            {
                1 or 3 or 5 => Unauthorized(),
                2 => Redirect("https://redirect:secret@b.example/repo"),
                4 => Redirect("https://url:secret@a.example/repo"),
                _ => Success(request),
            };
        });
        await using var transport = new GitHttpTransport(context, handler);
        await using IGitSubtransportStream stream = await transport.ActionAsync(
            "https://url:secret@a.example/repo", GitSmartService.UploadPackLs, options, ct);
        Assert.Equal(4, await stream.ReadAsync(new byte[16], ct));
        Assert.Equal(6, handler.RequestCount);
        Assert.Equal(["https://b.example/repo", "https://a.example/repo"], callbackUrls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeadingSlashes_StayOnOriginalAuthority(bool allowOffsite)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var context = new GitContext();
        using var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Equal("a.example", request.RequestUri!.Host);
            Assert.Equal("//b.example/path/info/refs", request.RequestUri.AbsolutePath);
            return Success(request);
        });
        await using var transport = new GitHttpTransport(context, handler);
        await using IGitSubtransportStream stream = await transport.ActionAsync(
            "https://a.example/repo", GitSmartService.UploadPackLs, null, ct);
        transport.ApplyRedirect("//b.example/path", allowOffsite);
        Assert.Equal(4, await stream.ReadAsync(new byte[16], ct));
    }

    [Theory]
    [InlineData("http://a.example/repo", true)]
    [InlineData("https://b.example/repo", false)]
    public async Task RejectedRedirect_PreservesUrlAndAuthentication(string location, bool allowOffsite)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var context = new GitContext();
        using var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Equal("https://a.example/repo/info/refs?service=git-upload-pack", request.RequestUri!.AbsoluteUri);
            Assert.Equal(Basic("a:a-password"), request.Headers.Authorization?.ToString());
            AssertCustomHeaders(request, expected: true);
            return Success(request);
        });
        await using var transport = new GitHttpTransport(context, handler);
        GitRemoteConnectOptions options = Options();
        await using IGitSubtransportStream stream = await transport.ActionAsync(
            "https://a.example/repo", GitSmartService.UploadPackLs, options, ct);
        using HttpResponseMessage unauthorized = Unauthorized();
        Assert.True(await transport.HandleServerAuthAsync(unauthorized, options, ct));
        AuthContext? auth = transport.ServerAuthContext;
        Assert.Throws<GitException>(() => transport.ApplyRedirect(location, allowOffsite));
        Assert.Same(auth, transport.ServerAuthContext);
        Assert.Equal(4, await stream.ReadAsync(new byte[16], ct));
    }

    [Fact]
    public async Task OriginChange_ClearsServerSchemes_PreservesProxy_CloseResetsConnection()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var context = new GitContext();
        using var handler = new ScriptedHandler((request, count) =>
        {
            if (count == 1)
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal(Basic("proxy:secret"), request.Headers.ProxyAuthorization?.ToString());
                AssertCustomHeaders(request, expected: false);
                return Success(request);
            }

            Assert.Null(request.Headers.ProxyAuthorization);
            AssertCustomHeaders(request, expected: true);
            Assert.Equal(count == 2 ? null : Basic("fresh:secret"), request.Headers.Authorization?.ToString());
            return count == 2 ? Unauthorized() : Success(request);
        });
        await using var transport = new GitHttpTransport(context, handler);
        GitRemoteConnectOptions options = Options() with
        {
            Proxy = new GitProxyConfig
            {
                Type = GitProxyType.Specified,
                Url = "http://proxy.example",
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitUserPassCredential("proxy", "secret")),
            },
        };
        await using (IGitSubtransportStream stream = await transport.ActionAsync(
            "https://a.example/repo", GitSmartService.UploadPackLs, options, ct))
        {
            using var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            unauthorized.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("NTLM"));
            Assert.True(await transport.HandleServerAuthAsync(unauthorized, options, ct));
            Assert.True(transport.NeedsProbe);
            using var proxyAuth = new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired);
            proxyAuth.Headers.ProxyAuthenticate.Add(new AuthenticationHeaderValue("Basic"));
            Assert.True(await transport.HandleProxyAuthAsync(proxyAuth, options, ct));
            transport.ApplyRedirect("https://b.example/repo", allowOffsite: true);
            Assert.Null(transport.ServerAuthContext);
            Assert.Equal(GitAuthSchemeType.None, transport.ServerAuthSchemes);
            Assert.False(transport.NeedsProbe);
            Assert.Equal(GitAuthSchemeType.Basic, transport.ProxyAuthSchemes);
            Assert.Equal(4, await stream.ReadAsync(new byte[16], ct));
        }

        await transport.CloseAsync(ct);
        Assert.Null(transport.ServerUrl);
        Assert.Equal(GitAuthSchemeType.None, transport.ProxyAuthSchemes);
        await using IGitSubtransportStream reopened = await transport.ActionAsync(
            "https://fresh:secret@b.example/repo", GitSmartService.UploadPackLs, options, ct);
        Assert.Equal(4, await reopened.ReadAsync(new byte[16], ct));
        Assert.Equal(3, handler.RequestCount);
    }

    private static GitRemoteConnectOptions Options(
        Func<GitCredentialType, string?, string?, CancellationToken, Task<GitCredential?>>? credentials = null)
        => new()
        {
            CustomHeaders = ["Cookie: session=synthetic-secret", "X-Api-Key: synthetic-key", "X-Request-Tag: test"],
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = credentials ?? ((_, _, _, _) => Task.FromResult<GitCredential?>(new GitUserPassCredential("a", "a-password"))),
            },
        };

    private static void AssertCustomHeaders(HttpRequestMessage request, bool expected)
    {
        Assert.Equal(expected, request.Headers.Contains("Cookie"));
        Assert.Equal(expected, request.Headers.Contains("X-Api-Key"));
        Assert.Equal(expected, request.Headers.Contains("X-Request-Tag"));
    }

    private static string Basic(string credentials)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));

    private static HttpResponseMessage Unauthorized()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"test\""));
        return response;
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Success(HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("0000"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(request.Method == HttpMethod.Get
            ? "application/x-git-upload-pack-advertisement"
            : "application/x-git-upload-pack-result");
        return response;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request, ++RequestCount));
        }
    }
}
