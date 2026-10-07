using LibGit2CS.IntegrationTests.DockerFixture;

using Xunit.Sdk;

// Marks AlpineNoKeyImageFixture as an assembly-wide fixture: xUnit v3
// constructs one instance before any test in the assembly runs (calling
// InitializeAsync) and disposes it after all tests complete. Test classes
// receive it via constructor injection.
[assembly: AssemblyFixture(typeof(AlpineNoKeyImageFixture))]

namespace LibGit2CS.IntegrationTests.DockerFixture;

/// <summary>
/// Assembly-wide fixture that builds the Alpine 3.20 image with no
/// <c>authorized_keys</c> entry (password auth), prepared on first use and
/// retained across test runs. Used by the
/// majority of tests (clone/fetch/push, known_hosts, rekey,
/// blame/reset/rebase/merge/stash/notes/submodule). Test classes receive it
/// via constructor injection (xUnit v3 <c>AssemblyFixture</c>) and start a
/// fresh per-test container via <see cref="SshGitImageFixtureBase.StartContainerAsync"/>.
/// </summary>
public sealed class AlpineNoKeyImageFixture(IMessageSink messageSink)
    : SshGitImageFixtureBase(messageSink)
{
    /// <inheritdoc/>
    protected override SshGitImageSpec Spec => new(
        FromLine: "FROM alpine:3.20",
        InstallAndKeygen: "RUN apk add --no-cache openssh-server git && ssh-keygen -A",
        CreateUserCmd: "RUN adduser -D " + SshGitDockerFixture.TestUser + " && echo '" + SshGitDockerFixture.TestUser + ":" + SshGitDockerFixture.TestPassword + "' | chpasswd",
        AuthConfig: SshdConfigAuthAlpine);
}
