namespace LibGit2CS.IntegrationTests.DockerFixture;

/// <summary>
/// Static facade for the git-over-daemon (<c>git://</c>) Docker test
/// fixture: exposes the in-container constants (<see cref="Host"/>,
/// <see cref="RepoPath"/>, <see cref="GitPort"/>) and the
/// Docker-reachability gate (<see cref="SkipIfDockerNotAvailable"/>).
/// The build-vs-run split lives in <see cref="GitDaemonImageFixture"/>
/// (one built image, shared across tests via its assembly-fixture role)
/// and <see cref="GitDaemonDockerContainer"/> (a fresh per-test container
/// started from the pre-built image).
/// </summary>
/// <remarks>
/// <para>
/// <b>Architecture.</b> Splitting image build (expensive: <c>apk</c>
/// install + repo seed) from container start (cheap: forks a new
/// <c>git daemon</c> from the image) lets the assembly fixture build the
/// image on first use and reuse it across runs. Each
/// test gets a fresh container with the clean seeded repo (one
/// commit on <c>refs/heads/main</c>), so tests that mutate server state
/// via <see cref="GitDaemonDockerContainer.ExecAsync"/> don't affect
/// each other.
/// </para>
/// <para>
/// <b>No auth.</b> Unlike the SSH fixture (<see cref="SshGitDockerFixture"/>),
/// the <c>git://</c> protocol has no authentication mechanism: the daemon
/// either serves a repo or refuses, based solely on the
/// <c>--export-all</c> / <c>git-daemon-export-ok</c> flag. Push is gated
/// by <c>--enable=receive-pack</c> on the daemon (set in the image CMD),
/// not by per-user credentials.
/// </para>
/// <para>
/// <b>Gating.</b> <see cref="ShouldRun"/> gates the tests — they no-op
/// when Docker is not reachable (socket on Unix, named pipe on Windows).
/// <see cref="GitDaemonImageFixture.StartContainerAsync"/> calls
/// <see cref="SkipIfDockerNotAvailable"/> internally, so tests don't need
/// their own skip call.
/// </para>
/// </remarks>
internal static class GitDaemonDockerFixture
{
    /// <summary>Container host as seen from the test process (always loopback).</summary>
    public const string Host = "127.0.0.1";

    /// <summary>
    /// Path inside the container to the seeded non-bare test repo, relative
    /// to <c>git daemon</c>'s <c>--base-path=/repos</c>. The daemon
    /// advertises it as <c>/test.git</c> in <c>git://</c> URLs.
    /// </summary>
    public const string RepoPath = "/repos/test.git";

    /// <summary>The in-container port <c>git daemon</c> listens on (the git-protocol default).</summary>
    public const int GitPort = 9418;

    /// <summary>
    /// True iff Docker appears reachable (socket on Unix, named pipe on
    /// Windows). Mirrors <see cref="SshGitDockerFixture.ShouldRun"/>.
    /// </summary>
    public static bool ShouldRun()
    {
        return File.Exists("/var/run/docker.sock")
            || (OperatingSystem.IsWindows() && Directory.Exists(@"\\.\pipe\docker_engine"));
    }

    /// <summary>
    /// Skips the current test when Docker is not reachable. Called
    /// internally by <see cref="GitDaemonImageFixture.StartContainerAsync"/>;
    /// tests do not need to call this themselves.
    /// </summary>
    public static void SkipIfDockerNotAvailable()
    {
        if (!ShouldRun())
        {
            Assert.Skip("Docker not reachable; skipping test.");
        }
    }
}
