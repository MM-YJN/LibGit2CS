using System.Net.Sockets;

using DotNet.Testcontainers.Containers;

namespace LibGit2CS.IntegrationTests.DockerFixture;

/// <summary>
/// A running git-over-SSH container started from a pre-built image owned by
/// an <see cref="SshGitImageFixtureBase"/> subclass. One instance per test
/// (via <see cref="SshGitImageFixtureBase.StartContainerAsync"/>); disposed
/// with <c>await using</c> at the end of the test. Each container has a fresh
/// copy of the seeded repo, so tests that mutate server state via
/// <see cref="ExecAsync"/> don't affect each other.
/// </summary>
public sealed class SshGitDockerContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public SshGitDockerContainer(IContainer container, int port)
    {
        _container = container;
        Port = port;
    }

    /// <summary>Random host port mapped to container port 22.</summary>
    public int Port { get; }

    /// <summary>
    /// Reads a hostkey file from the running container and returns the
    /// parsed <c>ssh-&lt;type&gt; &lt;base64-blob&gt; [comment]</c> line.
    /// Used by known_hosts tests to populate the test's known_hosts file
    /// with the real (or tampered) hostkey.
    /// </summary>
    /// <param name="keyType">
    /// The hostkey file stem, e.g. <c>ed25519</c> for
    /// <c>/etc/ssh/ssh_host_ed25519_key.pub</c>.
    /// </param>
    public async Task<string> GetHostKeyAsync(string keyType, CancellationToken ct)
    {
        ExecResult result = await _container.ExecAsync(["cat", $"/etc/ssh/ssh_host_{keyType}_key.pub"], ct)
            .ConfigureAwait(false);
        if (result.ExitCode is not 0)
        {
            throw new InvalidOperationException(
                $"Failed to read /etc/ssh/ssh_host_{keyType}_key.pub from container: " +
                $"exit={result.ExitCode}, stderr={result.Stderr}");
        }

        return result.Stdout.TrimEnd();
    }

    /// <summary>
    /// Runs an arbitrary shell command in the container as root. Returns the
    /// stdout. Used by tests that need to mutate the server-side repo (e.g.
    /// add a second commit before a fetch test), typically wrapped in
    /// <c>su testuser -c '...'</c> to run as the test user.
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
    /// Opens a raw TCP connection to the container's SSH port. Used by
    /// tests that need to drive <see cref="LibSsh2CS.SshSession"/> directly
    /// (e.g. the rekey test that needs to set <c>RekeyPolicy</c> before
    /// handshake). The returned <see cref="TcpClient"/> is owned by the
    /// caller.
    /// </summary>
    public async Task<TcpClient> ConnectTcpAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(SshGitDockerFixture.Host, Port, ct).ConfigureAwait(false);
        return tcp;
    }

    /// <summary>
    /// Returns the container's stdout+stderr logs (for diagnostics).
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
