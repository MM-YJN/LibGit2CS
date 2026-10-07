using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;

using Microsoft.Extensions.Logging;

using Xunit.Sdk;

// Marks GitDaemonImageFixture as an assembly-wide fixture: xUnit v3
// constructs one instance before any test in the assembly runs (calling
// InitializeAsync) and disposes it after all tests complete. Test classes
// receive it via constructor injection.
[assembly: AssemblyFixture(typeof(GitDaemonImageFixture))]

namespace LibGit2CS.IntegrationTests.DockerFixture;

/// <summary>
/// Assembly-wide fixture that builds an Alpine 3.20 image running
/// <c>git daemon</c> (the <c>git://</c> protocol on port 9418) lazily on
/// first use, and retains it after all tests complete.
/// Used by <see cref="Transports.GitDaemonTransportDockerTests"/> to
/// exercise the <c>git://</c> transport (<see cref="LibGit2CS.Transports.GitTransport"/>,
/// <see cref="LibGit2CS.Transports.GitStream"/>, <see cref="LibGit2CS.Transports.GitSocket"/>)
/// end-to-end against a real <c>git daemon</c>. Test classes receive this
/// fixture via constructor injection (xUnit v3 <c>AssemblyFixture</c>) and
/// start a fresh per-test container via <see cref="StartContainerAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate fixture.</b> The SSH fixture (<see cref="SshGitImageFixtureBase"/>)
/// builds images that run <c>sshd</c>; <c>git daemon</c> is a completely
/// different server (no auth, no hostkeys, just a bare TCP listener on
/// 9418 speaking the git-protocol wire format). Sharing the SSH image
/// infrastructure would force unrelated <c>openssh</c> installs onto the
/// daemon image, and would force <c>git-daemon</c> onto the SSH images.
/// </para>
/// <para>
/// <b>Image layout.</b> The Dockerfile installs <c>git</c> (which on
/// Alpine 3.20 ships <c>git daemon</c> as a subcommand), seeds a non-bare
/// test repo at <see cref="GitDaemonDockerFixture.RepoPath"/> with one
/// commit on <c>refs/heads/main</c>, sets
/// <c>receive.denyCurrentBranch=false</c> (so push tests can update the
/// checked-out branch on the non-bare repo), and starts
/// <c>git daemon --base-path=/repos --export-all --enable=receive-pack</c>
/// listening on 9418.
/// </para>
/// <para>
/// <b><c>--enable=receive-pack</c>.</b> The daemon refuses push by default;
/// the flag is required for the git-daemon push tests to work.
/// It opens an unauthenticated relay, but the container only binds to
/// loopback (<c>--listen=0.0.0.0</c> + Docker port mapping to
/// <c>127.0.0.1</c>), so the relay is unreachable from outside the test
/// host.
/// </para>
/// <para>
/// <b>Docker not available.</b> Initialization does no Docker work.
/// <see cref="StartContainerAsync"/> calls
/// <see cref="GitDaemonDockerFixture.SkipIfDockerNotAvailable"/> before
/// preparing the cached image, so tests skip when Docker is absent.
/// </para>
/// <para>
/// <b>Libgit2 counterpart.</b> The C reference tests <c>git://</c> via
/// <c>tests/libgit2/online/clone.c</c> and <c>fetch.c</c> against a real
/// <c>git daemon</c> process; the fixtures there rely on
/// <c>cl_fixture</c> rather than Docker, but the wire-format coverage is
/// equivalent.
/// </para>
/// </remarks>
public sealed class GitDaemonImageFixture : IAsyncLifetime
{
    private readonly LoggerFactory _loggerFactory;
    private readonly DockerImageCache _imageCache;

    public GitDaemonImageFixture(IMessageSink messageSink)
    {
        _loggerFactory = new LoggerFactory([new XunitMessageSinkLoggerProvider(messageSink)]);
        _imageCache = new DockerImageCache("gitdaemon", _loggerFactory.CreateLogger<GitDaemonImageFixture>());
    }

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    internal static string GetDockerfile()
    {
        // Build the Dockerfile inline. Alpine 3.20 ships `git daemon` as a
        // separate `git-daemon` subpackage (the main `git` package alone
        // does not register the `daemon` subcommand), so both must be
        // installed explicitly.
        //
        // The seeded repo is non-bare (despite the `.git` suffix in the
        // directory name) because creating a commit inside a bare repo
        // requires plumbing that's awkward in shell — same trade-off the
        // SSH fixture makes (see SshGitImageFixtureBase).
        // receive.denyCurrentBranch=false lets push tests update the
        // checked-out branch; the worktree goes stale but the test only
        // inspects refs and the object database.
        //
        // --export-all      serve repos without a git-daemon-export-ok marker
        // --enable=receive-pack  allow push (otherwise only fetch is served)
        // --listen=0.0.0.0  bind to all interfaces (Docker port-maps to loopback)
        // --port=9418       the git-protocol default port
        // --verbose         log connections to stderr (captured by GetLogsAsync)
        string dockerfile =
            "FROM alpine:3.20\n" +
            "RUN apk add --no-cache git git-daemon\n" +
            "RUN mkdir -p /repos && " +
            "git init " + GitDaemonDockerFixture.RepoPath + " && " +
            "cd " + GitDaemonDockerFixture.RepoPath + " && " +
            "git config user.email 'test@test' && git config user.name 'Test' && " +
            "git config receive.denyCurrentBranch false && " +
            "git checkout -b main && " +
            "echo 'hello' > README.md && git add README.md && " +
            "git commit -m 'initial'\n" +
            "EXPOSE " + GitDaemonDockerFixture.GitPort + "\n" +
            "CMD [\"git\",\"daemon\",\"--base-path=/repos\",\"--export-all\"," +
            "\"--enable=receive-pack\",\"--listen=0.0.0.0\"," +
            "\"--port=" + GitDaemonDockerFixture.GitPort + "\",\"--verbose\"]\n";

        return dockerfile.ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Starts a fresh container from the pre-built image. The container
    /// gets a random host port mapped to the in-container
    /// <see cref="GitDaemonDockerFixture.GitPort"/>. Calls
    /// <see cref="GitDaemonDockerFixture.SkipIfDockerNotAvailable"/> first,
    /// so the test is skipped when Docker is unreachable. Prepares the image lazily.
    /// </summary>
    public async Task<GitDaemonDockerContainer> StartContainerAsync(ILoggerFactory loggerFactory, CancellationToken ct)
    {
        GitDaemonDockerFixture.SkipIfDockerNotAvailable();

        IFutureDockerImage image = await _imageCache.EnsureImageAsync(GetDockerfile(), ct).ConfigureAwait(false);

        IContainer container = new ContainerBuilder(image)
            .WithPortBinding(0, GitDaemonDockerFixture.GitPort)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(GitDaemonDockerFixture.GitPort))
            .WithLogger(loggerFactory.CreateLogger<GitDaemonDockerContainer>())
            .Build();
        try
        {
            await container.StartAsync(ct).ConfigureAwait(false);
            int port = container.GetMappedPublicPort(GitDaemonDockerFixture.GitPort);
            return new GitDaemonDockerContainer(container, port);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _imageCache.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _loggerFactory.Dispose();
        }
    }
}
