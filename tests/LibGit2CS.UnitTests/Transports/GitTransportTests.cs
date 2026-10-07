using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Transports;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Transports;

public sealed class GitTransportTests
{
    // ── GenerateProtocol format tests ──────────────────────────────────

    [Fact]
    public void GenerateProtocol_BasicUrl()
    {
        using PooledByteBufferWriter result = GitStream.GenerateProtocol(
            "git-upload-pack", "/repo.git", "example.com");

        string hex = Encoding.ASCII.GetString(result.WrittenSpan);
        Assert.StartsWith("00", hex);
        Assert.Contains("git-upload-pack /repo.git\0host=example.com\0", hex);
    }

    [Fact]
    public void GenerateProtocol_ReceivePack()
    {
        using PooledByteBufferWriter result = GitStream.GenerateProtocol(
            "git-receive-pack", "/repo.git", "example.com");

        string hex = Encoding.ASCII.GetString(result.WrittenSpan);
        Assert.StartsWith("00", hex);
        Assert.Contains("git-receive-pack /repo.git\0host=example.com\0", hex);
    }

    [Fact]
    public void GenerateProtocol_TildePath()
    {
        // C: if repo[1]=='~' advance (skip the leading slash before ~)
        using PooledByteBufferWriter result = GitStream.GenerateProtocol(
            "git-upload-pack", "/~user/repo.git", "example.com");

        string hex = Encoding.ASCII.GetString(result.WrittenSpan);
        // The ~ should cause the leading / to be stripped: "~user/repo.git"
        Assert.Contains("git-upload-pack ~user/repo.git\0", hex);
    }

    [Fact]
    public void GenerateProtocol_LengthPrefixIsCorrect()
    {
        using PooledByteBufferWriter result = GitStream.GenerateProtocol(
            "git-upload-pack", "/path", "host");

        string hex = Encoding.ASCII.GetString(result.WrittenSpan);
        // Parse the 4-hex-digit length prefix
        int len = Convert.ToInt32(hex[..4], 16);
        Assert.Equal(result.WrittenCount, len);
    }

    [Fact]
    public void GenerateProtocol_MalformedUrl_NoSlash_Throws()
    {
        Assert.Throws<GitException>(() =>
            GitStream.GenerateProtocol("git-upload-pack", "noslash", "host"));
    }

    // ── Service dispatch tests ─────────────────────────────────────────

    [Fact]
    public async Task Action_UploadPackLs_CreatesStream()
    {
        await using var transport = new GitTransport();

        // This will try to connect to somehost:9418 — but we're testing the dispatch
        // logic, not actual network I/O. The stream creation will fail at socket
        // connect, which is expected. We verify the service dispatch is correct
        // by checking that UploadPackLs doesn't throw "must call LS first".
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await transport.ActionAsync("git://somehost/repo", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken));

        // Should NOT be "must call LS before action" — should be a socket connect error
        Assert.DoesNotContain("must call LS", ex.Message);
    }

    [Fact]
    public async Task Action_UploadPack_WithoutLs_Throws()
    {
        await using var transport = new GitTransport();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ActionAsync("git://somehost/repo", GitSmartService.UploadPack, null, TestContext.Current.CancellationToken));

        Assert.Contains("must call LS", ex.Message);
    }

    [Fact]
    public async Task Action_ReceivePack_WithoutLs_Throws()
    {
        await using var transport = new GitTransport();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ActionAsync("git://somehost/repo", GitSmartService.ReceivePack, null, TestContext.Current.CancellationToken));

        Assert.Contains("must call LS", ex.Message);
    }

    // ── URL parsing tests ──────────────────────────────────────────────

    [Fact]
    public async Task UrlParsing_DefaultPort()
    {
        // Verify that git://host/path correctly extracts host and uses default port 9418
        await using var transport = new GitTransport();

        // The Action call for UploadPackLs will parse the URL and try to connect
        // to host:9418. We verify parsing is correct by checking no URL parse error.
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await transport.ActionAsync("git://somehost/path/to/repo.git", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken));

        // Should not be a "malformed git:// URL" error
        Assert.DoesNotContain("malformed", ex.Message);
    }

    [Fact]
    public async Task UrlParsing_ExplicitPort()
    {
        await using var transport = new GitTransport();

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await transport.ActionAsync("git://somehost:9999/repo.git", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken));

        // Should not be a URL parse error — it should parse and then fail the connect.
        Assert.DoesNotContain("malformed", ex.Message);
        Assert.DoesNotContain("invalid port", ex.Message);
    }

    [Fact]
    public async Task UrlParsing_MalformedUrl_Throws()
    {
        await using var transport = new GitTransport();

        // No slash after host
        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ActionAsync("git://noslash", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UrlParsing_MalformedPort_Throws()
    {
        await using var transport = new GitTransport();

        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ActionAsync("git://host:abc/repo", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UrlParsing_EmptyHost_Throws()
    {
        await using var transport = new GitTransport();

        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.ActionAsync("git:///repo", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken));
    }

    // ── Close / Dispose tests ──────────────────────────────────────────

    [Fact]
    public async Task Close_AfterNoConnection_DoesNotThrow()
    {
        await using var transport = new GitTransport();
        await transport.CloseAsync(TestContext.Current.CancellationToken); // Should be a no-op
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var transport = new GitTransport();
        await transport.DisposeAsync();
        await transport.DisposeAsync(); // Should not throw
    }

    [Fact]
    public async Task Action_AfterDispose_Throws()
    {
        var transport = new GitTransport();
        await transport.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await transport.ActionAsync("git://host/repo", GitSmartService.UploadPackLs, null, TestContext.Current.CancellationToken));
    }

    // ── Registry integration tests ─────────────────────────────────────

    [Theory]
    [InlineData("git://somehost/repo")]
    public async Task Registry_CreatesSmartTransportForGit(string url)
    {
        var ctx = new GitContext();
        IGitTransport transport = ctx.Transports.Create(url, ctx);
        Assert.IsType<GitSmartTransport>(transport);
        Assert.False(((GitSmartTransport)transport).IsRpc);
    }

    [Theory]
    [InlineData("git://somehost/repo")]
    public async Task Registry_GitTransport_ConnectFails_GracefulError(string url)
    {
        // git:// transport should create a SmartTransport, not throw at factory time
        var ctx = new GitContext();
        IGitTransport transport = ctx.Transports.Create(url, ctx);

        // Connecting to a non-existent host should throw a connection error,
        // not a "not implemented" error
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await transport.ConnectAsync(url, GitDirection.Fetch, null, TestContext.Current.CancellationToken));

        // Should NOT contain "not yet implemented"
        Assert.DoesNotContain("not yet implemented", ex.Message);
    }
}
