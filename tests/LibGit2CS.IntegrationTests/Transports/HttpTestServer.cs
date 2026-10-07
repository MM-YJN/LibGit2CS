using System.Collections.Concurrent;
using System.Text;

using LibGit2CS.IntegrationTests.TestKit.Logger;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// In-process HTTP test server using ASP.NET Core TestHost.
/// Simulates git smart-http endpoints for testing <see cref="LibGit2CS.Transports.GitHttpTransport"/>.
/// </summary>
/// <remarks>
/// Uses <see cref="TestServer"/> (in-memory, no real TCP port) for fast,
/// deterministic tests. Each test configures the server with scripted
/// responses per endpoint and path.
/// </remarks>
public sealed class HttpTestServer : IDisposable
{
    private readonly TestServer _server;
    private readonly HttpTestConfig _config = new();

    /// <summary>Creates a new test server with default configuration.</summary>
    public HttpTestServer(ITestOutputHelper testOutputHelper)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new XunitTestOutputLoggerProvider(testOutputHelper));
        builder.WebHost.UseTestServer();
        WebApplication app = builder.Build();

        ConfigurePipeline(app);

        // Start the server without blocking (TestServer runs in-memory)
        _ = app.RunAsync();

        _server = app.GetTestServer();
    }

    /// <summary>The base URL for HTTP requests (e.g. <c>http://localhost/</c>).</summary>
    public string BaseUrl => _server.BaseAddress.ToString().TrimEnd('/');

    /// <summary>The configuration for scripted responses and auth challenges.</summary>
    public HttpTestConfig Config => _config;

    /// <summary>
    /// Create an <see cref="HttpMessageHandler"/> that talks to the in-memory server.
    /// Used to construct <see cref="HttpTransport"/> with the test server's handler.
    /// </summary>
    public HttpMessageHandler CreateHandler() => _server.CreateHandler();

    /// <summary>
    /// Create an <see cref="HttpClient"/> that talks to the in-memory server.
    /// The <see cref="HttpTransport"/> uses this via the <see cref="BaseUrl"/>.
    /// </summary>
    public HttpClient CreateClient() => _server.CreateClient();

    private void ConfigurePipeline(WebApplication app)
    {
        app.Use(DispatchMiddlewareAsync);
        app.Use(GitServiceMiddlewareAsync);
    }

    /// <summary>
    /// Pre-dispatch middleware: records the request, then short-circuits for
    /// always-return status, auth challenges (401/407), or redirects.
    /// </summary>
    private async Task DispatchMiddlewareAsync(HttpContext context, Func<Task> next)
    {
        string path = context.Request.Path.Value ?? "";
        string query = context.Request.QueryString.Value ?? "";
        string method = context.Request.Method;

        // Record the request
        _config.RecordRequest(method, path, query);

        // Always-return status (for error testing)
        if (_config.AlwaysReturnStatus > 0)
        {
            context.Response.StatusCode = _config.AlwaysReturnStatus;
            await context.Response.WriteAsync($"Error {_config.AlwaysReturnStatus}");
            return;
        }

        // Check for auth challenge (401/407)
        if (_config.AuthRequired && !_config.IsAuthenticated(context.Request))
        {
            if (_config.AuthType == "Basic")
            {
                context.Response.StatusCode = 401;
                context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{_config.AuthRealm}\"";
                await context.Response.WriteAsync("Unauthorized");
                return;
            }

            if (_config.AuthType == "Negotiate")
            {
                context.Response.StatusCode = 401;
                context.Response.Headers.WWWAuthenticate = "Negotiate";
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }

        if (_config.ProxyAuthRequired && !_config.IsProxyAuthenticated(context.Request.Headers))
        {
            context.Response.StatusCode = 407;
            context.Response.Headers.ProxyAuthenticate = $"Basic realm=\"{_config.ProxyAuthRealm}\"";
            await context.Response.WriteAsync("Proxy Authentication Required");
            return;
        }

        // Check for redirect
        if (_config.TryGetRedirect(method, path, out string? redirectLocation))
        {
            context.Response.StatusCode = _config.RedirectStatus;
            context.Response.Headers.Location = redirectLocation;
            await context.Response.WriteAsync("Redirect");
            return;
        }

        await next();
    }

    /// <summary>
    /// Smart HTTP endpoints middleware: handles <c>/info/refs</c>,
    /// <c>/git-upload-pack</c>, <c>/git-receive-pack</c>, and the
    /// <c>/error/{status}</c> test endpoint.
    /// </summary>
    private async Task GitServiceMiddlewareAsync(HttpContext context, Func<Task> next)
    {
        string path = context.Request.Path.Value ?? "";
        string method = context.Request.Method;

        // GET .../info/refs?service=git-upload-pack -> ref advertisement
        if (method == "GET" && path.EndsWith("/info/refs", StringComparison.OrdinalIgnoreCase))
        {
            string service = context.Request.Query["service"].ToString();
            string contentType = service switch
            {
                "git-upload-pack" => "application/x-git-upload-pack-advertisement",
                "git-receive-pack" => "application/x-git-receive-pack-advertisement",
                _ => "text/plain",
            };

            byte[] refAd = _config.CustomRefAdvertisement ?? BuildRefAdvertisement(service);
            context.Response.ContentType = contentType;
            await context.Response.Body.WriteAsync(refAd);
            return;
        }

        // POST .../git-upload-pack -> negotiation + pack
        if (method == "POST" && path.EndsWith("/git-upload-pack", StringComparison.OrdinalIgnoreCase))
        {
            await HandleUploadPackAsync(context);
            return;
        }

        // POST .../git-receive-pack -> accept pack, return report-status
        if (method == "POST" && path.EndsWith("/git-receive-pack", StringComparison.OrdinalIgnoreCase))
        {
            await HandleReceivePackAsync(context);
            return;
        }

        // Error endpoint: /error/{status}
        if (path.Contains("/error/", StringComparison.OrdinalIgnoreCase))
        {
            int lastSlash = path.LastIndexOf('/');
            if (lastSlash >= 0 && int.TryParse(path[(lastSlash + 1)..], out int status))
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsync($"Error {status}");
                return;
            }
        }

        await next();
    }

    private async Task HandleUploadPackAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync();
        _config.RecordUploadPackRequest(body);

        context.Response.ContentType = "application/x-git-upload-pack-result";

        if (_config.CustomUploadPackResponse is not null)
        {
            await context.Response.Body.WriteAsync(_config.CustomUploadPackResponse);
            return;
        }

        byte[] nak = "0008NAK\n"u8.ToArray();
        await context.Response.Body.WriteAsync(nak);

        if (_config.PackData is not null)
        {
            byte[] sideband = MockTransportBuilder.BuildSidebandData(_config.PackData);
            await context.Response.Body.WriteAsync(sideband);
        }
        else
        {
            byte[] flush = "0000"u8.ToArray();
            await context.Response.Body.WriteAsync(flush);
        }
    }

    private async Task HandleReceivePackAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync();
        _config.RecordReceivePackRequest(body);

        context.Response.ContentType = "application/x-git-receive-pack-result";

        if (_config.CustomReceivePackResponse is not null)
        {
            await context.Response.Body.WriteAsync(_config.CustomReceivePackResponse);
            return;
        }

        var report = new StringBuilder();
        string unpackLine = "unpack ok\n";
        int unpackLen = unpackLine.Length + 4;
        report.Append(unpackLen.ToString("x4")).Append(unpackLine);

        foreach (string refName in _config.PushRefUpdates)
        {
            string okLine = $"ok {refName}\n";
            int okLen = okLine.Length + 4;
            report.Append(okLen.ToString("x4")).Append(okLine);
        }

        report.Append("0000");
        await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(report.ToString()));
    }

    /// <summary>
    /// Build a ref advertisement for the given service.
    /// </summary>
    private static byte[] BuildRefAdvertisement(string service)
    {
        var sb = new StringBuilder();

        // RPC: leading comment
        if (service == "git-upload-pack")
        {
            sb.Append("001e# service=git-upload-pack\n");
        }
        else if (service == "git-receive-pack")
        {
            sb.Append("001f# service=git-receive-pack\n");
        }

        sb.Append("0000"); // First flush (RPC expects 2)

        string oid = "a5b8e4d4f2e1c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
        string caps = "multi_ack_detailed side-band-64k thin-pack ofs-delta agent=git/test";

        // First ref with capabilities
        string firstLine = $"{oid} refs/heads/main\0{caps}\n";
        int firstLen = firstLine.Length + 4;
        sb.Append(firstLen.ToString("x4")).Append(firstLine);

        // Additional refs
        string devLine = $"{oid} refs/heads/develop\n";
        int devLen = devLine.Length + 4;
        sb.Append(devLen.ToString("x4")).Append(devLine);

        sb.Append("0000"); // Flush
        sb.Append("0000"); // Second flush (RPC)

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _server.Dispose();
    }
}

/// <summary>
/// Configuration for the HTTP test server. Controls auth, redirects,
/// and scripted responses.
/// </summary>
public sealed class HttpTestConfig
{
    /// <summary>Whether server authentication is required.</summary>
    public bool AuthRequired { get; set; }

    /// <summary>If set to a non-zero value, all requests return this status code.</summary>
    public int AlwaysReturnStatus { get; set; }

    /// <summary>Auth type: "Basic" or "Negotiate".</summary>
    public string AuthType { get; set; } = "Basic";

    /// <summary>Auth realm for the WWW-Authenticate header.</summary>
    public string AuthRealm { get; set; } = "git";

    /// <summary>Expected username for Basic auth.</summary>
    public string ExpectedUsername { get; set; } = "user";

    /// <summary>Expected password for Basic auth.</summary>
    public string ExpectedPassword { get; set; } = "pass";

    /// <summary>Whether proxy authentication is required.</summary>
    public bool ProxyAuthRequired { get; set; }

    /// <summary>Proxy auth realm.</summary>
    public string ProxyAuthRealm { get; set; } = "proxy";

    /// <summary>Expected proxy username.</summary>
    public string ProxyExpectedUsername { get; set; } = "proxyuser";

    /// <summary>Expected proxy password.</summary>
    public string ProxyExpectedPassword { get; set; } = "proxypass";

    /// <summary>Redirect status code (301, 302, 307, 308).</summary>
    public int RedirectStatus { get; set; } = 301;

    /// <summary>Redirect mappings: (method, path) -> location.</summary>
    private readonly ConcurrentDictionary<(string Method, string Path), string> _redirects = new();

    /// <summary>Ref updates expected in a push (for report-status response).</summary>
    public List<string> PushRefUpdates { get; set; } = new() { "refs/heads/main" };

    /// <summary>Custom ref advertisement bytes (overrides default).</summary>
    public byte[]? CustomRefAdvertisement { get; set; }

    /// <summary>Custom upload-pack response bytes (overrides default NAK + pack).</summary>
    public byte[]? CustomUploadPackResponse { get; set; }

    /// <summary>Custom receive-pack response bytes (overrides default report-status).</summary>
    public byte[]? CustomReceivePackResponse { get; set; }

    /// <summary>Pack data for side-band response.</summary>
    public byte[]? PackData { get; set; }

    /// <summary>Recorded requests for assertions.</summary>
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    /// <summary>Recorded upload-pack POST bodies.</summary>
    private readonly ConcurrentQueue<string> _uploadPackRequests = new();

    /// <summary>Recorded receive-pack POST bodies.</summary>
    private readonly ConcurrentQueue<string> _receivePackRequests = new();

    /// <summary>All recorded requests.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToList();

    /// <summary>Recorded upload-pack POST bodies.</summary>
    public IReadOnlyList<string> UploadPackRequests => _uploadPackRequests.ToList();

    /// <summary>Recorded receive-pack POST bodies.</summary>
    public IReadOnlyList<string> ReceivePackRequests => _receivePackRequests.ToList();

    /// <summary>Add a redirect: requests to (method, path) -> 301/302 to location.</summary>
    public void AddRedirect(string method, string path, string location)
    {
        _redirects[(method.ToUpperInvariant(), path)] = location;
    }

    /// <summary>Check if this request should be redirected. Matches by suffix.</summary>
    internal bool TryGetRedirect(string method, string path, out string? location)
    {
        foreach (KeyValuePair<(string Method, string Path), string> kv in _redirects)
        {
            if (string.Equals(kv.Key.Method, method, StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(kv.Key.Path, StringComparison.OrdinalIgnoreCase))
            {
                location = kv.Value;
                return true;
            }
        }
        location = null;
        return false;
    }

    /// <summary>Check if the request is authenticated.</summary>
    internal bool IsAuthenticated(HttpRequest request)
    {
        IHeaderDictionary headers = request.Headers;
        string? authHeader = headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader))
        {
            return false;
        }

        if (AuthType == "Basic")
        {
            if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string token = authHeader["Basic ".Length..];
            try
            {
                byte[] decoded = Convert.FromBase64String(token);
                string decodedStr = Encoding.UTF8.GetString(decoded);
                int colon = decodedStr.IndexOf(':');
                if (colon < 0)
                {
                    return false;
                }

                string user = decodedStr[..colon];
                string pass = decodedStr[(colon + 1)..];
                return user == ExpectedUsername && pass == ExpectedPassword;
            }
            catch
            {
                return false;
            }
        }

        if (AuthType == "Negotiate")
        {
            // For testing: accept any non-empty Negotiate token
            return authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Check if the request is proxy-authenticated.</summary>
    internal bool IsProxyAuthenticated(IHeaderDictionary headers)
    {
        string? authHeader = HeadersUtil.GetHeader(headers, "Proxy-Authorization");
        if (string.IsNullOrEmpty(authHeader))
        {
            return false;
        }

        if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string token = authHeader["Basic ".Length..];
        try
        {
            byte[] decoded = Convert.FromBase64String(token);
            string decodedStr = Encoding.UTF8.GetString(decoded);
            int colon = decodedStr.IndexOf(':');
            if (colon < 0)
            {
                return false;
            }

            string user = decodedStr[..colon];
            string pass = decodedStr[(colon + 1)..];
            return user == ProxyExpectedUsername && pass == ProxyExpectedPassword;
        }
        catch
        {
            return false;
        }
    }

    internal void RecordRequest(string method, string path, string query)
    {
        _requests.Enqueue(new RecordedRequest(method, path, query));
    }

    internal void RecordUploadPackRequest(string body)
    {
        _uploadPackRequests.Enqueue(body);
    }

    internal void RecordReceivePackRequest(string body)
    {
        _receivePackRequests.Enqueue(body);
    }
}

/// <summary>A recorded HTTP request for test assertions.</summary>
public sealed record RecordedRequest(string Method, string Path, string Query);

/// <summary>Helper for case-insensitive header lookup.</summary>
internal static class HeadersUtil
{
    public static string? GetHeader(IHeaderDictionary headers, string name)
    {
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> kv in headers)
        {
            if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value.ToString();
            }
        }
        return null;
    }
}
