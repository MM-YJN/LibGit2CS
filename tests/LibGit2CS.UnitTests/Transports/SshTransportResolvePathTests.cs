using LibGit2CS.Core;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Unit tests for <see cref="SshTransport.ResolveKnownHostsPath"/> and
/// <see cref="SshTransport.ResolveAgentSocketPath"/>. Pins the "override
/// or fall back" contract: a non-null delegate returning a non-empty path
/// wins; otherwise the default resolution (<c>~/.ssh/known_hosts</c> for
/// known-hosts, <c>null</c> for agent which downstream reads as
/// <c>$SSH_AUTH_SOCK</c>) is used.
/// </summary>
/// <remarks>
/// The known-hosts fallback tests use a differential assertion (override
/// returning <c>null</c>/<c>""</c> must equal the no-override baseline)
/// rather than asserting a specific path, so they are robust to whatever
/// <c>Environment.GetFolderPath(UserProfile)</c> resolves to on the host.
/// </remarks>
public sealed class SshTransportResolvePathTests
{
    // ── ResolveKnownHostsPath ──────────────────────────────────────────

    [Fact]
    public async Task KnownHosts_OverrideNull_ReturnsHomeFallback()
    {
        // A null-returning delegate yields the same path as no delegate at
        // all. Asserts the fallback without depending on what
        // UserProfile resolves to on the test host.
        using var ctx = new GitContext();
        await using var transport = new SshTransport(ctx, sessionFactory: null, timeProvider: null);

        string? baseline = transport.ResolveKnownHostsPath();

        ctx.Settings.KnownHostsPathOverride = () => null;
        string? withNullOverride = transport.ResolveKnownHostsPath();

        Assert.Equal(baseline, withNullOverride);
    }

    [Fact]
    public async Task KnownHosts_OverrideEmpty_ReturnsHomeFallback()
    {
        // Empty string must also fall back — a config key left unset often
        // surfaces as "" rather than null.
        using var ctx = new GitContext();
        await using var transport = new SshTransport(ctx, sessionFactory: null, timeProvider: null);

        string? baseline = transport.ResolveKnownHostsPath();

        ctx.Settings.KnownHostsPathOverride = () => string.Empty;
        string? withEmptyOverride = transport.ResolveKnownHostsPath();

        Assert.Equal(baseline, withEmptyOverride);
    }

    [Fact]
    public async Task KnownHosts_OverrideNonEmpty_WinsOverHome()
    {
        using var ctx = new GitContext();
        await using var transport = new SshTransport(ctx, sessionFactory: null, timeProvider: null);

        ctx.Settings.KnownHostsPathOverride = () => "/custom/known_hosts";
        string? result = transport.ResolveKnownHostsPath();

        Assert.Equal("/custom/known_hosts", result);
    }

    [Fact]
    public async Task KnownHosts_OverrideWhitespace_WinsOverHome()
    {
        // Whitespace is non-empty per string.IsNullOrEmpty — returned
        // verbatim. Pins that the resolver does not Trim() the override
        // return.
        using var ctx = new GitContext();
        await using var transport = new SshTransport(ctx, sessionFactory: null, timeProvider: null);

        ctx.Settings.KnownHostsPathOverride = () => "  ";
        string? result = transport.ResolveKnownHostsPath();

        Assert.Equal("  ", result);
    }

    // ── ResolveAgentSocketPath ─────────────────────────────────────────

    [Fact]
    public async Task Agent_OverrideUnset_ReturnsNullDefault()
    {
        // Baseline: no override at all → null (downstream $SSH_AUTH_SOCK
        // discovery).
        using var ctx = new GitContext();
        await using var transport = new SshTransport(ctx, sessionFactory: null, timeProvider: null);

        string? result = transport.ResolveAgentSocketPath();

        Assert.Null(result);
    }

    [Fact]
    public async Task Agent_OverrideNull_ReturnsNullDefault()
    {
        using var ctx = new GitContext();
        await using var transport = new SshTransport(ctx, sessionFactory: null, timeProvider: null);

        ctx.Settings.AgentSocketPathOverride = () => null;
        string? result = transport.ResolveAgentSocketPath();

        Assert.Null(result);
    }

    [Fact]
    public async Task Agent_OverrideEmpty_ReturnsNullDefault()
    {
        using var ctx = new GitContext();
        await using var transport = new SshTransport(ctx, sessionFactory: null, timeProvider: null);

        ctx.Settings.AgentSocketPathOverride = () => string.Empty;
        string? result = transport.ResolveAgentSocketPath();

        Assert.Null(result);
    }

    [Fact]
    public async Task Agent_OverrideNonEmpty_Wins()
    {
        using var ctx = new GitContext();
        await using var transport = new SshTransport(ctx, sessionFactory: null, timeProvider: null);

        ctx.Settings.AgentSocketPathOverride = () => "/tmp/agent.sock";
        string? result = transport.ResolveAgentSocketPath();

        Assert.Equal("/tmp/agent.sock", result);
    }
}
