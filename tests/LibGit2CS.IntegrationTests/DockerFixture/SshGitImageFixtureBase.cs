using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

using LibGit2CS.IntegrationTests.TestKit.Logger;

using Microsoft.Extensions.Logging;

using Xunit.Sdk;

namespace LibGit2CS.IntegrationTests.DockerFixture;

/// <summary>
/// Prepares a cached SSH Git image on first use, shared by this variant's tests.
/// Unused fixtures do no Docker work. Images survive fixture disposal and
/// subsequent runs; each test starts and disposes a fresh container.
/// </summary>
public abstract class SshGitImageFixtureBase : IAsyncLifetime
{
    private const int SshPort = 22;

    // sshd offering all 7 in-scope hostkey types + the in-scope kex/cipher/mac,
    // WITH password + pubkey auth enabled. The `\\n` escapes are expanded to
    // real newlines by `printf` in the Dockerfile RUN. The per-image auth
    // preamble (SshdConfigAuthAlpine / SshdConfigAuthDebian) is appended
    // after this shared block.
    private const string SshdConfigBase =
        "Port 22\\n" +
        "ListenAddress 0.0.0.0\\n" +
        "PermitRootLogin yes\\n" +
        "PubkeyAuthentication yes\\n" +
        "PasswordAuthentication yes\\n" +
        "PubkeyAcceptedAlgorithms +ssh-rsa,ssh-ed25519,rsa-sha2-256,rsa-sha2-512,ecdsa-sha2-nistp256,ecdsa-sha2-nistp384,ecdsa-sha2-nistp521\\n" +
        "HostKeyAlgorithms +ssh-rsa,ssh-ed25519,rsa-sha2-256,rsa-sha2-512,ecdsa-sha2-nistp256,ecdsa-sha2-nistp384,ecdsa-sha2-nistp521\\n" +
        "KexAlgorithms curve25519-sha256,ecdh-sha2-nistp256,ecdh-sha2-nistp384,ecdh-sha2-nistp521,diffie-hellman-group14-sha256\\n" +
        "Ciphers aes256-ctr,aes256-gcm@openssh.com\\n" +
        "MACs hmac-sha2-256\\n";

    // Per-image auth preamble (appended after SshdConfigBase).
    //
    // Alpine: openssh-server is built WITHOUT libpam linkage, so kbdint is
    // unavailable regardless of KbdInteractiveAuthentication / UsePAM.
    // Debian: openssh-server is PAM-linked; the default /etc/pam.d/sshd
    // pulls in pam_unix.so via @include common-auth, which drives the
    // USERAUTH_INFO_REQUEST password challenge end-to-end.
    /// <summary>Alpine auth preamble: PAM-disabled (kbdint unavailable on Alpine's openssh-server).</summary>
    protected internal const string SshdConfigAuthAlpine = "UsePAM no\\n";

    /// <summary>Debian auth preamble: PAM-enabled (drives the USERAUTH_INFO_REQUEST kbdint challenge).</summary>
    protected internal const string SshdConfigAuthDebian =
        "UsePAM yes\\n" +
        "KbdInteractiveAuthentication yes\\n" +
        "ChallengeResponseAuthentication yes\\n";

    /// <summary>
    /// Per-image Dockerfile assembly bits: FROM line, package install +
    /// hostkey bootstrap, user-creation command, and auth-specific sshd
    /// config preamble.
    /// </summary>
    protected internal sealed record SshGitImageSpec(
        string FromLine,
        string InstallAndKeygen,
        string CreateUserCmd,
        string AuthConfig);

    private readonly LoggerFactory _loggerFactory;
    private readonly DockerImageCache _imageCache;

    protected SshGitImageFixtureBase(IMessageSink messageSink)
    {
        _loggerFactory = new LoggerFactory([new XunitMessageSinkLoggerProvider(messageSink)]);
        _imageCache = new DockerImageCache("sshgit", _loggerFactory.CreateLogger<SshGitImageFixtureBase>());
    }

    /// <summary>The Dockerfile assembly bits for this fixture's variant; supplied by the subclass.</summary>
    protected abstract SshGitImageSpec Spec { get; }

    /// <summary>
    /// Optional authorized_key line to bake into the image's
    /// <c>authorized_keys</c> (<see cref="AlpineWithKeyImageFixture"/> loads
    /// the test Ed25519 public key). Null by default.
    /// </summary>
    protected virtual string? AuthorizedKey => null;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    internal string GetDockerfile()
    {
        SshGitImageSpec spec = Spec;

        string sshdConfig = SshdConfigBase + spec.AuthConfig;
        string dockerfile =
            spec.FromLine + "\n" +
            spec.InstallAndKeygen + "\n" +
            $"RUN printf '{sshdConfig}' > /etc/ssh/sshd_config\n" +
            spec.CreateUserCmd + "\n" +
            "RUN mkdir -p /home/" + SshGitDockerFixture.TestUser + "/.ssh && chmod 700 /home/" + SshGitDockerFixture.TestUser + "/.ssh\n";

        if (AuthorizedKey is not null)
        {
            // Base64-encode the key line to avoid any shell-quoting issues
            // in the Dockerfile RUN. The container decodes it before writing
            // to authorized_keys. This sidesteps single-quote, double-quote,
            // backslash, and newline handling entirely.
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(AuthorizedKey.TrimEnd()));
            dockerfile += "RUN echo '" + b64 + "' | base64 -d > /home/" + SshGitDockerFixture.TestUser +
                "/.ssh/authorized_keys && chmod 600 /home/" + SshGitDockerFixture.TestUser +
                "/.ssh/authorized_keys && chown -R " + SshGitDockerFixture.TestUser + ":" + SshGitDockerFixture.TestUser +
                " /home/" + SshGitDockerFixture.TestUser + "/.ssh\n";
        }

        // Seed a non-bare test repo at /home/testuser/repo.git with one
        // commit on refs/heads/main. We use a non-bare repo (despite the
        // .git suffix) because creating a commit inside a bare repo requires
        // plumbing that's awkward in shell. The .git suffix is just
        // cosmetic — it's what git URLs typically point at. The repo has
        // a working tree but the SSH transport only reads .git/ objects,
        // refs, and HEAD via git-upload-pack.
        // We also set receive.denyCurrentBorder=false so push tests work
        // (otherwise git-receive-pack rejects pushes to the checked-out
        // branch on a non-bare repo).
        dockerfile +=
            "RUN git init /home/" + SshGitDockerFixture.TestUser + "/repo.git && " +
            "cd /home/" + SshGitDockerFixture.TestUser + "/repo.git && " +
            "git config user.email 'test@test' && git config user.name 'Test' && " +
            "git config receive.denyCurrentBranch false && " +
            "git checkout -b main && " +
            "echo 'hello' > README.md && git add README.md && " +
            "git commit -m 'initial'\n";
        dockerfile += "RUN chown -R " + SshGitDockerFixture.TestUser + ":" + SshGitDockerFixture.TestUser + " /home/" + SshGitDockerFixture.TestUser + "/repo.git\n";

        dockerfile += "EXPOSE 22\nCMD [\"/usr/sbin/sshd\", \"-D\", \"-e\"]\n";

        return dockerfile.ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Starts a fresh container from this fixture's pre-built image. The
    /// container gets a random host port mapped to port 22. Calls
    /// <see cref="SshGitDockerFixture.SkipIfDockerNotAvailable"/> first, so the
    /// test is skipped when Docker is unreachable. Prepares the image lazily.
    /// </summary>
    public async Task<SshGitDockerContainer> StartContainerAsync(ILoggerFactory loggerFactory, CancellationToken ct)
    {
        SshGitDockerFixture.SkipIfDockerNotAvailable();

        IFutureDockerImage image = await _imageCache.EnsureImageAsync(GetDockerfile(), ct).ConfigureAwait(false);

        IContainer container = new ContainerBuilder(image)
            .WithPortBinding(0, SshPort)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(SshPort))
            .WithLogger(loggerFactory.CreateLogger<SshGitDockerContainer>())
            .Build();
        try
        {
            await container.StartAsync(ct).ConfigureAwait(false);
            int port = container.GetMappedPublicPort(SshPort);
            return new SshGitDockerContainer(container, port);
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
