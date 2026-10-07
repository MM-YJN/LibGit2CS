using LibGit2CS.IntegrationTests.DockerFixture;

using Xunit.Sdk;

// Marks AlpineWithKeyImageFixture as an assembly-wide fixture: xUnit v3
// constructs one instance before any test in the assembly runs (calling
// InitializeAsync) and disposes it after all tests complete. Test classes
// receive it via constructor injection.
[assembly: AssemblyFixture(typeof(AlpineWithKeyImageFixture))]

namespace LibGit2CS.IntegrationTests.DockerFixture;

/// <summary>
/// Assembly-wide fixture that builds the Alpine 3.20 image with the test
/// Ed25519 public key (<c>Fixtures/ssh/ed25519_key.pub</c>) installed in
/// <c>authorized_keys</c>, prepared on first use and retained across test
/// runs. Used by the publickey-file,
/// publickey-memory, and agent auth tests (which all share one image). Test
/// classes receive it via constructor injection and start a fresh per-test
/// container via <see cref="SshGitImageFixtureBase.StartContainerAsync"/>.
/// </summary>
public sealed class AlpineWithKeyImageFixture(IMessageSink messageSink)
    : SshGitImageFixtureBase(messageSink)
{
    /// <inheritdoc/>
    protected override SshGitImageSpec Spec => new(
        FromLine: "FROM alpine:3.20",
        InstallAndKeygen: "RUN apk add --no-cache openssh-server git && ssh-keygen -A",
        CreateUserCmd: "RUN adduser -D " + SshGitDockerFixture.TestUser + " && echo '" + SshGitDockerFixture.TestUser + ":" + SshGitDockerFixture.TestPassword + "' | chpasswd",
        AuthConfig: SshdConfigAuthAlpine);

    /// <summary>
    /// The test Ed25519 public key, loaded from the embedded fixture resource
    /// at build time and baked into the image's <c>authorized_keys</c>.
    /// </summary>
    protected override string? AuthorizedKey => FixtureLoader.LoadText("Fixtures.ssh.ed25519_key.pub");
}
