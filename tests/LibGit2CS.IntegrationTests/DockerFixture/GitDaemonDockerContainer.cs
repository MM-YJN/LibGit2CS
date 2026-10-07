using DotNet.Testcontainers.Containers;

namespace LibGit2CS.IntegrationTests.DockerFixture;

/// <summary>
/// A running <c>git daemon</c> container started from the pre-built image
/// owned by <see cref="GitDaemonImageFixture"/>. One instance per test
/// (via <see cref="GitDaemonImageFixture.StartContainerAsync"/>); disposed
/// with <c>await using</c> at the end of the test. Each container has a
/// fresh copy of the seeded repo, so tests that mutate server state via
/// <see cref="ExecAsync"/> don't affect each other.
/// </summary>
/// <remarks>
/// Mirrors <see cref="SshGitDockerContainer"/> minus the SSH-specific
/// helpers (<c>GetHostKeyAsync</c> / <c>ConnectTcpAsync</c>): the
/// <c>git://</c> protocol has no hostkeys and tests connect via
/// <see cref="LibGit2CS.Remote.GitRemote"/>, not raw TCP.
/// </remarks>
public sealed class GitDaemonDockerContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public GitDaemonDockerContainer(IContainer container, int port)
    {
        _container = container;
        Port = port;
    }

    /// <summary>
    /// Random host port mapped to in-container
    /// <see cref="GitDaemonDockerFixture.GitPort"/> (9418).
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Runs an arbitrary shell command in the container as root. Returns
    /// the stdout. Used by tests that need to mutate the server-side repo
    /// (e.g. add a second commit before a fetch test).
    /// </summary>
    public async Task<string> ExecAsync(string command, CancellationToken ct)
    {
        ExecResult result = await _container.ExecAsync(["sh", "-c", command], ct)
            .ConfigureAwait(false);
        if (result.ExitCode is not 0)
        {
            throw new InvalidOperationException(
                $"Command failed in container: {command}\nexit={result.ExitCode}\nstdout={result.Stdout}\nstderr={result.Stderr}");
        }

        return result.Stdout;
    }

    /// <summary>
    /// Returns the container's stdout+stderr logs (for diagnostics).
    /// <c>git daemon --verbose</c> logs each connection to stderr.
    /// </summary>
    public async Task<string> GetContainerLogsAsync(CancellationToken ct)
    {
        (string stdout, string stderr) = await _container.GetLogsAsync(DateTime.MinValue, DateTime.MaxValue, false, ct)
            .ConfigureAwait(false);
        return stderr + stdout;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
