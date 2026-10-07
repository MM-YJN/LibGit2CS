using System.Diagnostics;

namespace LibGit2CS.IntegrationTests.Transports.TestInfrastructure;

/// <summary>
/// Manages a private <c>ssh-agent</c> process on the test host for the
/// duration of a single integration test. The agent is started fresh, a
/// private key is loaded via <c>ssh-add</c>, the socket path is exposed
/// for use by <c>GitSshAgentCredential</c> consumers, and the agent is
/// killed on dispose.
/// </summary>
internal sealed class HostSshAgent : IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly string _tmpKeyPath;
    private readonly int _agentPid;

    private HostSshAgent(string socketPath, string tmpKeyPath, int agentPid)
    {
        _socketPath = socketPath;
        _tmpKeyPath = tmpKeyPath;
        _agentPid = agentPid;
    }

    /// <summary>The <c>SSH_AUTH_SOCK</c> path to set for the test process.</summary>
    public string SocketPath => _socketPath;

    /// <summary>
    /// True iff agent tests should run: Docker reachable +
    /// <c>ssh-agent</c> + <c>ssh-add</c> binaries present on PATH.
    /// </summary>
    private static bool ShouldRun()
    {
        if (!(File.Exists("/var/run/docker.sock")
            || (OperatingSystem.IsWindows() && Directory.Exists(@"\\.\pipe\docker_engine"))))
        {
            return false;
        }

        return BinaryExists("ssh-agent") && BinaryExists("ssh-add");
    }

    public static void SkipIfDockerOrSshAgentNotAvailable()
    {
        if (!ShouldRun())
        {
            Assert.Skip("Docker not reachable or ssh-agent not available; skipping test.");
        }
    }

    private static bool BinaryExists(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p?.WaitForExit(2000);
            return p is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a fresh <c>ssh-agent</c>, writes the key to a temp file,
    /// <c>ssh-add</c>s it, and returns the agent context.
    /// </summary>
    public static async Task<HostSshAgent> StartAsync(byte[] privateKeyBytes, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ssh-agent",
            Arguments = "-s",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using Process startProc = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ssh-agent");
        string stdout = await startProc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await startProc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (startProc.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh-agent exited {startProc.ExitCode}: {stdout}");
        }

        (string? socketPath, int pid) = ParseAgentOutput(stdout);
        if (socketPath is null || pid == 0)
        {
            throw new InvalidOperationException($"Could not parse ssh-agent output: {stdout}");
        }

        // Write the key to a temp file with safe perms (ssh-add refuses otherwise).
        string tmpKey = Path.Combine(Path.GetTempPath(), $"libgit2cs-key-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(tmpKey, privateKeyBytes, ct);
        if (!OperatingSystem.IsWindows())
        {
            using var chmod = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"600 {tmpKey}",
                UseShellExecute = false,
            });
            await WaitForExitBestEffortAsync(chmod, 5000).ConfigureAwait(false);
        }

        try
        {
            var addStart = new ProcessStartInfo
            {
                FileName = "ssh-add",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            addStart.Environment["SSH_AUTH_SOCK"] = socketPath;
            addStart.ArgumentList.Add(tmpKey);

            using Process addProc = Process.Start(addStart)
                ?? throw new InvalidOperationException("Failed to start ssh-add");
            string addStdout = await addProc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            string addStderr = await addProc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await addProc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (addProc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ssh-add exited {addProc.ExitCode}: stdout={addStdout} stderr={addStderr}");
            }
        }
        catch
        {
            TryKillAgent(pid);
            File.Delete(tmpKey);
            throw;
        }

        return new HostSshAgent(socketPath, tmpKey, pid);
    }

    /// <summary>Parse <c>SSH_AUTH_SOCK</c> + <c>SSH_AGENT_PID</c> from <c>ssh-agent -s</c> output.</summary>
    private static (string? Socket, int Pid) ParseAgentOutput(string stdout)
    {
        string? socket = null;
        int pid = 0;

        foreach (string line in stdout.Split(';', '\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("SSH_AUTH_SOCK=", StringComparison.Ordinal))
            {
                socket = trimmed.Substring("SSH_AUTH_SOCK=".Length);
            }
            else if (trimmed.StartsWith("SSH_AGENT_PID=", StringComparison.Ordinal))
            {
                string pidStr = trimmed.Substring("SSH_AGENT_PID=".Length);
                if (!int.TryParse(pidStr, out pid))
                {
                    pid = 0;
                }
            }
        }

        return (socket, pid);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var clearStart = new ProcessStartInfo
            {
                FileName = "ssh-add",
                Arguments = "-D",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            clearStart.Environment["SSH_AUTH_SOCK"] = _socketPath;
            using var clearProc = Process.Start(clearStart);
            await WaitForExitBestEffortAsync(clearProc, 2000).ConfigureAwait(false);
        }
        catch
        {
            // Ignore — best-effort.
        }

        TryKillAgent(_agentPid);

        try
        {
            if (File.Exists(_tmpKeyPath))
            {
                File.Delete(_tmpKeyPath);
            }
        }
        catch { /* best-effort */ }

        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch { /* best-effort — may not have perms */ }
    }

    private static void TryKillAgent(int pid)
    {
        if (pid == 0)
        {
            return;
        }

        try
        {
            using var killProc = Process.Start(new ProcessStartInfo
            {
                FileName = "kill",
                Arguments = $"{pid}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            killProc?.WaitForExit(2000);
        }
        catch
        {
            // Best-effort — process may already be gone.
        }
    }

    /// <summary>
    /// Waits for <paramref name="process"/> to exit, bounded by
    /// <paramref name="timeoutMs"/>. Mirrors the timeout semantics of
    /// <see cref="Process.WaitForExit(int)"/> without the synchronous block
    /// (CA1849). Returns silently on timeout or when <paramref name="process"/>
    /// is <c>null</c>.
    /// </summary>
    private static async Task WaitForExitBestEffortAsync(Process? process, int timeoutMs)
    {
        if (process is null)
        {
            return;
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Best-effort — timed out.
        }
    }
}
