using LibGit2CS.IntegrationTests.DockerFixture;

using Xunit.Sdk;

// Marks DebianNoKeyImageFixture as an assembly-wide fixture: xUnit v3
// constructs one instance before any test in the assembly runs (calling
// InitializeAsync) and disposes it after all tests complete. Test classes
// receive it via constructor injection.
[assembly: AssemblyFixture(typeof(DebianNoKeyImageFixture))]

namespace LibGit2CS.IntegrationTests.DockerFixture;

/// <summary>
/// Assembly-wide fixture that builds the Debian (bookworm) slim image with
/// a PAM-linked sshd (no <c>authorized_keys</c> entry), prepared on first use
/// and retained across test runs. Used by the
/// keyboard-interactive auth test, which requires <c>pam_unix.so</c> to drive
/// the <c>USERAUTH_INFO_REQUEST</c> challenge (Alpine's <c>openssh-server</c>
/// is built without libpam and ignores <c>KbdInteractiveAuthentication yes</c>).
/// Test classes receive it via constructor injection and start a fresh
/// per-test container via <see cref="SshGitImageFixtureBase.StartContainerAsync"/>.
/// </summary>
public sealed class DebianNoKeyImageFixture(IMessageSink messageSink)
    : SshGitImageFixtureBase(messageSink)
{
    /// <inheritdoc/>
    protected override SshGitImageSpec Spec => new(
        FromLine: "FROM debian:12-slim",
        // /run/sshd must exist or Debian's sshd refuses to start with
        // "Missing privilege separation directory: /run/sshd".
        InstallAndKeygen: "RUN apt-get update && apt-get install -y --no-install-recommends openssh-server git ca-certificates && rm -rf /var/lib/apt/lists/* && mkdir -p /run/sshd && ssh-keygen -A",
        // Debian uses `useradd`; Alpine uses BusyBox `adduser -D`.
        CreateUserCmd: "RUN useradd -m -s /bin/sh " + SshGitDockerFixture.TestUser + " && echo '" + SshGitDockerFixture.TestUser + ":" + SshGitDockerFixture.TestPassword + "' | chpasswd",
        AuthConfig: SshdConfigAuthDebian);
}
