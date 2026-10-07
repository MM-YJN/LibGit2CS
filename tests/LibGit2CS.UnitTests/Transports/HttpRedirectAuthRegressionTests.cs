using System.Net;
using System.Net.Http.Headers;

using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

// ApplyRedirect follows C's git_net_url_apply_redirect (net.c:933-982):
// it rejects a scheme change unless the target is https, and rejects host
// changes when offsite redirects are not allowed, so a https→http downgrade
// never sends the cached Authorization header over plaintext to the
// attacker host.
//
// The auth context is recreated from the CURRENT credential + host
// (httpclient.c apply_credentials uses the CURRENT
// request->credentials on every request), so stale tokens are not replayed
// after re-challenges and foreign-host tokens are not sent preemptively
// after redirects.
public sealed class HttpRedirectAuthRegressionTests
{
    private static GitHttpTransport ConnectedTransport(string url)
    {
        var transport = new GitHttpTransport(new GitContext());
        transport.ActionAsync(url, GitSmartService.UploadPackLs, null, default);
        return transport;
    }

    // ── redirect validation ────────────────────────────────────────

    [Fact]
    public void ApplyRedirect_SchemeDowngrade_Throws()
    {
        GitHttpTransport transport = ConnectedTransport("https://example.com/repo");

        // C (net.c:949-956): "cannot redirect from 'https' to 'http'".
        GitException ex = Assert.Throws<GitException>(() =>
            transport.ApplyRedirect("http://attacker.example/evil", allowOffsite: true));
        Assert.Equal(GitErrorCategory.Net, ex.Category);
        Assert.Contains("cannot redirect from 'https' to 'http'", ex.Message);

        // The server URL must be unchanged.
        Assert.Equal("https", transport.ServerUrl!.Scheme);
        Assert.Equal("example.com", transport.ServerUrl.Host);
    }

    [Fact]
    public void ApplyRedirect_SchemeChange_ToHttps_IsAllowed()
    {
        GitHttpTransport transport = ConnectedTransport("http://example.com/repo");

        // C: a scheme change is legal when the target is https.
        transport.ApplyRedirect("https://example.com/repo", allowOffsite: true);
        Assert.Equal("https", transport.ServerUrl!.Scheme);
    }

    [Fact]
    public void ApplyRedirect_OffsiteHost_RejectedWhenDisallowed()
    {
        GitHttpTransport transport = ConnectedTransport("https://example.com/repo");

        // C (net.c:958-966): "cannot redirect from 'example.com' to 'other.com'".
        GitException ex = Assert.Throws<GitException>(() =>
            transport.ApplyRedirect("https://other.com/repo", allowOffsite: false));
        Assert.Equal(GitErrorCategory.Net, ex.Category);
        Assert.Contains("cannot redirect from 'example.com' to 'other.com'", ex.Message);
        Assert.Equal("example.com", transport.ServerUrl!.Host);
    }

    [Fact]
    public void ApplyRedirect_OffsiteHost_AllowedWhenPermitted()
    {
        GitHttpTransport transport = ConnectedTransport("https://example.com/repo");

        // Under FollowRedirects=All (or an initial redirect), offsite is OK.
        transport.ApplyRedirect("https://other.com/repo", allowOffsite: true);
        Assert.Equal("other.com", transport.ServerUrl!.Host);
    }

    [Fact]
    public void ApplyRedirect_PathOnly_SameHost_IsAllowed()
    {
        GitHttpTransport transport = ConnectedTransport("https://example.com/repo");

        // C (net.c:940-944): a location starting with '/' is a same-host
        // path redirect — always legal.
        transport.ApplyRedirect("/other/path", allowOffsite: false);
        Assert.Equal("example.com", transport.ServerUrl!.Host);
        Assert.Equal("/other/path", transport.ServerUrl!.AbsolutePath);
    }

    // ── auth context recreation ────────────────────────────────────

    private static HttpResponseMessage NewUnauthorizedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"test\""));
        return response;
    }

    [Fact]
    public async Task ServerAuth_RedirectToOtherHost_RecreatesContext()
    {
        var transport = new GitHttpTransport(new GitContext());
        await transport.ActionAsync("https://a.example/repo", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken);

        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(new GitUserPassCredential("userA", "passA")),
            },
        };

        bool ok = await transport.HandleServerAuthAsync(NewUnauthorizedResponse(), options, TestContext.Current.CancellationToken);
        Assert.True(ok);
        AuthContext? ctxA = transport.ServerAuthContext;
        Assert.NotNull(ctxA);
        Assert.Equal("a.example", ctxA!.Host);
        Assert.Equal("Basic " + Convert.ToBase64String("userA:passA"u8), ctxA.NextToken(null));

        // The server redirects (allowed under the follow policy) and the NEW
        // host sends a 401. The context must be recreated for the new host;
        // the first host's context (and preemptive token) must not be
        // replayed to the foreign host.
        transport.ApplyRedirect("https://b.example/repo", allowOffsite: true);
        ok = await transport.HandleServerAuthAsync(NewUnauthorizedResponse(), options, TestContext.Current.CancellationToken);
        Assert.True(ok);

        AuthContext? ctxB = transport.ServerAuthContext;
        Assert.NotNull(ctxB);
        Assert.NotSame(ctxA, ctxB);
        Assert.Equal("b.example", ctxB!.Host);
        Assert.Equal("Basic " + Convert.ToBase64String("userA:passA"u8), ctxB.NextToken(null));
    }

    [Fact]
    public async Task ServerAuth_ReChallengeWithCorrectedCredential_UsesNewCredential()
    {
        var transport = new GitHttpTransport(new GitContext());
        await transport.ActionAsync("https://a.example/repo", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken);

        int call = 0;
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) =>
                {
                    call++;
                    return Task.FromResult<GitCredential?>(call == 1
                        ? new GitUserPassCredential("user1", "wrong")
                        : new GitUserPassCredential("user2", "right"));
                },
            },
        };

        bool ok = await transport.HandleServerAuthAsync(NewUnauthorizedResponse(), options, TestContext.Current.CancellationToken);
        Assert.True(ok);
        AuthContext? ctx1 = transport.ServerAuthContext;
        Assert.NotNull(ctx1);
        Assert.Equal("Basic " + Convert.ToBase64String("user1:wrong"u8), ctx1!.NextToken(null));

        // Same host re-challenge; the callback returns CORRECTED credentials.
        // C (httpclient.c:586): the token is derived from the CURRENT
        // request->credentials, so the corrected credential is used.
        ok = await transport.HandleServerAuthAsync(NewUnauthorizedResponse(), options, TestContext.Current.CancellationToken);
        Assert.True(ok);

        AuthContext? ctx2 = transport.ServerAuthContext;
        Assert.NotNull(ctx2);
        Assert.NotSame(ctx1, ctx2);
        Assert.Equal("Basic " + Convert.ToBase64String("user2:right"u8), ctx2!.NextToken(null));
    }
}
