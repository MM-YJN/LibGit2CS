using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

public sealed class TransportRegistryTests : IDisposable
{
    private readonly GitContext _ctx = new();

    // Ported from test_transport_register__custom_transport
    [Fact]
    public async Task CustomTransport_RegisterAndCreate()
    {
        _ctx.Transports.Register("something", DummyFactory);
        try
        {
            IGitTransport transport = _ctx.Transports.Create("something://somepath", _ctx);
            Assert.IsType<DummyTransport>(transport);
            await transport.DisposeAsync();
        }
        finally
        {
            // Unregister before Dispose's ClearCustom runs
            try
            {
                _ctx.Transports.Unregister("something");
            }
            catch { }
        }
    }

    // Ported from test_transport_register__custom_transport_error_doubleregister
    [Fact]
    public void CustomTransport_DoubleRegister_ThrowsExists()
    {
        _ctx.Transports.Register("something", DummyFactory);
        try
        {
            GitException ex = Assert.Throws<GitException>(() => _ctx.Transports.Register("something", DummyFactory));
            Assert.Equal(GitErrorCode.Exists, ex.Code);
        }
        finally
        {
            try
            {
                _ctx.Transports.Unregister("something");
            }
            catch { }
        }
    }

    // Ported from test_transport_register__custom_transport_error_remove_non_existing
    [Fact]
    public void CustomTransport_UnregisterNonExisting_ThrowsNotFound()
    {
        GitException ex = Assert.Throws<GitException>(() => _ctx.Transports.Unregister("something"));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // Ported from test_transport_register__custom_transport_ssh
    [Theory]
    [InlineData("ssh://somehost/somepath")]
    [InlineData("ssh+git://somehost/somepath")]
    [InlineData("git+ssh://somehost/somepath")]
    public void SshUrls_ResolveToSshTransport(string url)
    {
        // SSH URLs resolve to a stateful (non-RPC) smart transport wrapping
        // the SSH subtransport, replacing the placeholder with the
        // real SshTransport; the subtransport is internal, but IsRpc=false
        // is the visible parity signal (SSH is stateful — one persistent
        // channel for LS + action).
        IGitTransport transport = _ctx.Transports.Create(url, _ctx);
        GitSmartTransport smart = Assert.IsType<GitSmartTransport>(transport);
        Assert.False(smart.IsRpc);
    }

    [Theory]
    [InlineData("git://somehost/repo")]
    public void GitUrl_ResolveToGitTransport(string url)
    {
        IGitTransport transport = _ctx.Transports.Create(url, _ctx);
        Assert.IsType<GitSmartTransport>(transport);
        // The git:// transport is stateful (not RPC)
        Assert.False(((GitSmartTransport)transport).IsRpc);
    }

    [Theory]
    [InlineData("http://server/repo")]
    [InlineData("https://server/repo")]
    public void HttpUrls_ResolveToHttpTransport(string url)
    {
        IGitTransport transport = _ctx.Transports.Create(url, _ctx);
        Assert.IsType<GitSmartTransport>(transport);
        // The transport should be RPC mode (stateless, HTTP)
        Assert.True(((GitSmartTransport)transport).IsRpc);
    }

    [Fact]
    public void FileUrl_ResolvesToLocalTransport()
    {
        // file:// creates a LocalTransport directly (no SmartTransport wrapping)
        IGitTransport transport = _ctx.Transports.Create("file:///path/to/repo", _ctx);
        Assert.IsType<GitLocalTransport>(transport);
    }

    [Fact]
    public void ScpStyleUrl_ResolvesToSsh()
    {
        // git@somehost:path → detected as SSH (no scheme, contains ':',
        // not a Windows drive path). Resolves to a stateful smart transport.
        IGitTransport transport = _ctx.Transports.Create("git@somehost:somepath", _ctx);
        GitSmartTransport smart = Assert.IsType<GitSmartTransport>(transport);
        Assert.False(smart.IsRpc);
    }

    [Fact]
    public void UnknownUrl_WithColon_ResolvesToSsh()
    {
        // C (transport.c:82-106): ANY URL with a ':' that no registered scheme matched is re-resolved as ssh:// — including "foobar://...".
        IGitTransport transport = _ctx.Transports.Create("foobar://somepath", _ctx);
        Assert.IsType<GitSmartTransport>(transport);
    }

    [Fact]
    public void UnknownUrl_NoColon_ThrowsNotFound()
    {
        // No ':' at all → no SSH fallback either.
        GitException ex = Assert.Throws<GitException>(() => _ctx.Transports.Create("foobar", _ctx));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public void IsRegistered_BuiltInSchemes()
    {
        Assert.True(_ctx.Transports.IsRegistered("git"));
        Assert.True(_ctx.Transports.IsRegistered("http"));
        Assert.True(_ctx.Transports.IsRegistered("https"));
        Assert.True(_ctx.Transports.IsRegistered("ssh"));
        Assert.True(_ctx.Transports.IsRegistered("file"));
    }

    [Fact]
    public void IsRegistered_CustomScheme()
    {
        _ctx.Transports.Register("myproto", DummyFactory);
        try
        {
            Assert.True(_ctx.Transports.IsRegistered("myproto"));
        }
        finally
        {
            _ctx.Transports.Unregister("myproto");
        }
    }

    [Fact]
    public void IsRegistered_NotRegistered()
    {
        Assert.False(_ctx.Transports.IsRegistered("nonexistent"));
    }

    private static GitTransportRegistry.TransportFactory DummyFactory
        => _ => new DummyTransport();

    private sealed class DummyTransport : IGitTransport
    {
        public GitRemoteCapability Capabilities => GitRemoteCapability.None;
        public GitHashAlgorithmKind OidType => GitHashAlgorithmKind.Sha1;
        public bool IsConnected => false;
        public Task ConnectAsync(string url, GitDirection direction, GitRemoteConnectOptions? options, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetConnectOptions(GitRemoteConnectOptions? options) { }
        public Task<IReadOnlyList<GitRemoteHead>> LsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitRemoteHead>>([]);
        public Task NegotiateFetchAsync(GitRepository repo, GitFetchNegotiation wants, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DownloadPackAsync(GitRepository repo, GitIndexerProgress stats, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask<IReadOnlyList<GitOid>> ShallowRootsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<GitOid>>([]);
        public Task<GitPushResult> PushAsync(GitRepository repo, IReadOnlyList<GitPushSpec> specs, GitPackWriter? packWriter, GitRemoteCallbacks? callbacks, bool reportStatus, IReadOnlyList<string>? pushOptions, CancellationToken cancellationToken) => Task.FromResult(new GitPushResult { UnpackOk = true, Status = [] });
        public void Cancel() { }
        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _ctx.Dispose();
    }
}
