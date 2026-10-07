using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;
using LibGit2CS.IntegrationTests.Transports.TestInfrastructure;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;
using LibGit2CS.Status;
using LibGit2CS.Transports;

using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Docker-based integration tests for <see cref="SshTransport"/>: real
/// OpenSSH server in a container with <c>git</c> installed and a seeded
/// server-side repo. Exercises clone/fetch/push, 4 auth methods (password,
/// publickey file, publickey memory, agent), known_hosts verification, and
/// rekey under load.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gating.</b> All tests no-op when Docker is not reachable (socket on
/// Unix, named pipe on Windows) — <see cref="SshGitImageFixtureBase.StartContainerAsync"/>
/// calls <see cref="SshGitDockerFixture.SkipIfDockerNotAvailable"/> internally.
/// The agent test has a stricter gate (also requires <c>ssh-agent</c> +
/// <c>ssh-add</c> on PATH).
/// </para>
/// <para>
/// <b>Known hosts.</b> The container's host keys are ephemeral (regenerated
/// at image build time). Tests that don't specifically test known_hosts
/// behavior install a permissive <c>CertificateCheck</c> callback that
/// accepts any hostkey. The known_hosts-specific tests inject a real
/// or tampered known_hosts file via <c>ctx.Settings.KnownHostsPathOverride</c>.
/// </para>
/// <para>
/// <b>Keyboard-interactive uses a Debian container.</b> Alpine's
/// <c>openssh-server</c> is built without libpam linkage and ignores
/// <c>KbdInteractiveAuthentication yes</c>, so the kbdint test requests the
/// Debian image variant (PAM-linked sshd) from
/// <see cref="DebianNoKeyImageFixture"/>. All other tests use
/// the default Alpine image. Kbdint dispatch is also exercised at the
/// unit-test level via <c>SshTransportAuthTests</c> (fake-backed
/// <c>ISshSession</c>).
/// </para>
/// </remarks>
public sealed class SshTransportDockerTests : IDisposable
{
    private readonly LoggerFactory _loggerFactory;
    private readonly AlpineNoKeyImageFixture _alpineNoKey;
    private readonly AlpineWithKeyImageFixture _alpineWithKey;
    private readonly DebianNoKeyImageFixture _debianNoKey;

    public SshTransportDockerTests(
        AlpineNoKeyImageFixture alpineNoKey,
        AlpineWithKeyImageFixture alpineWithKey,
        DebianNoKeyImageFixture debianNoKey,
        ITestOutputHelper testOutputHelper)
    {
        _alpineNoKey = alpineNoKey;
        _alpineWithKey = alpineWithKey;
        _debianNoKey = debianNoKey;
        _loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);
    }

    public void Dispose() => _loggerFactory.Dispose();

    /// <summary>
    /// Password auth: <c>ConnectAsync</c> + <c>LsAsync</c> over SSH with
    /// <see cref="GitUserPassCredential"/> succeeds and returns the seeded
    /// <c>refs/heads/main</c> head.
    /// </summary>
    [Fact]
    public async Task Auth_Password_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
            IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);

            // Assert BEFORE DisconnectAsync — disconnect clears the cached
            // heads (parity with libgit2's ResetStreamAsync clearing _refs).
            Assert.Contains(heads, h => h.Name == "refs/heads/main");

            await remote.DisconnectAsync(ct);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Publickey auth with a file-based Ed25519 key (no passphrase).
    /// The container's <c>authorized_keys</c> is seeded with the test key's
    /// public half; the credentials callback returns a
    /// <see cref="GitSshKeyCredential"/> pointing at the on-disk private key.
    /// </summary>
    [Fact]
    public async Task Auth_PublicKeyFile_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Extract the embedded test keypair to a temp file so we can pass
        // the private-key path to GitSshKeyCredential. The keypair is shared
        // across tests that need a key (file/memory/agent).
        string keyDir = Path.Combine(Path.GetTempPath(), "libgit2cs-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyDir);
        string privKeyPath = Path.Combine(keyDir, "ed25519_key");
        await File.WriteAllBytesAsync(privKeyPath, FixtureLoader.LoadBytes("Fixtures.ssh.ed25519_key"), ct);
        if (!OperatingSystem.IsWindows())
        {
            // ssh-keygen / libssh2 reject world-readable private keys.
            using var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"600 {privKeyPath}",
                UseShellExecute = false,
            });
            if (chmod is not null)
            {
                using var chmodCts = new CancellationTokenSource(2000);
                try
                {
                    await chmod.WaitForExitAsync(chmodCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (chmodCts.IsCancellationRequested)
                {
                    // Best-effort — timed out.
                }
            }
        }

        try
        {
            await using SshGitDockerContainer fixture = await _alpineWithKey.StartContainerAsync(_loggerFactory, ct);

            string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
            var callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                    new GitSshKeyCredential(SshGitDockerFixture.TestUser, publicKeyPath: null, privKeyPath, passphrase: null)),
                CertificateCheck = _ => true,
            };
            var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

            string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-" + Guid.NewGuid().ToString("N"));
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
            try
            {
                GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
                await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
                IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);
                Assert.Contains(heads, h => h.Name == "refs/heads/main");
                await remote.DisconnectAsync(ct);
            }
            finally
            {
                try
                {
                    Directory.Delete(repoPath, recursive: true);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(keyDir, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Publickey auth with in-memory key bytes (no file path). The
    /// credentials callback returns a <see cref="GitSshKeyMemoryCredential"/>
    /// carrying the raw private-key bytes loaded from the embedded fixture.
    /// </summary>
    [Fact]
    public async Task Auth_PublicKeyMemory_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        byte[] privKeyBytes = FixtureLoader.LoadBytes("Fixtures.ssh.ed25519_key");

        await using SshGitDockerContainer fixture = await _alpineWithKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitSshKeyMemoryCredential(SshGitDockerFixture.TestUser, publicKey: null, privateKey: privKeyBytes, passphrase: null)),
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
            IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);
            Assert.Contains(heads, h => h.Name == "refs/heads/main");
            await remote.DisconnectAsync(ct);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Wrong password → the credentials callback returns a wrong
    /// password once, then returns null on the retry (cancelling auth). The
    /// transport must surface this as a <see cref="GitException"/> with
    /// <see cref="GitErrorCode.Auth"/> / <see cref="GitErrorCategory.Ssh"/>.
    /// </summary>
    [Fact]
    public async Task Auth_WrongPassword_RetriesAndFails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";

        // First invocation returns a wrong password; second returns null
        // (cancel). The transport's auth retry loop will re-invoke the
        // callback after the first AuthenticationFailed.
        int attempt = 0;
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) =>
            {
                if (attempt++ == 0)
                {
                    return Task.FromResult<GitCredential?>(
                        new GitUserPassCredential(SshGitDockerFixture.TestUser, "wrongpassword"));
                }

                return Task.FromResult<GitCredential?>(null);
            },
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);

            GitException ex = await Assert.ThrowsAsync<GitException>(() =>
                remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct));

            Assert.Equal(GitErrorCode.Auth, ex.Code);
            Assert.Equal(GitErrorCategory.Ssh, ex.Category);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── URL-embedded / re-prompt auth ──────────────────────────────────

    /// <summary>
    /// A URL with embedded credentials (<c>ssh://user:pass@…</c>)
    /// authenticates WITHOUT any credentials callback: the transport
    /// pre-builds a <see cref="GitUserPassCredential"/> from the URL's
    /// <c>user:pass</c> userinfo and tries it before prompting (parity with
    /// <c>ssh_libssh2.c:837-848</c>; the userinfo is split at the LAST
    /// colon, <c>net.c:265-277</c>).
    /// </summary>
    /// <remarks>
    /// The <c>user:pass</c> userinfo is split at the last colon and the
    /// password is used for authentication instead of being sent verbatim
    /// as the username.
    /// </remarks>
    [Fact]
    public async Task Auth_UrlEmbeddedPassword_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // user:pass embedded in the URL; NO Credentials callback.
        string url = $"ssh://{SshGitDockerFixture.TestUser}:{SshGitDockerFixture.TestPassword}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-urlcred-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
            IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);
            Assert.Contains(heads, h => h.Name == "refs/heads/main");
            await remote.DisconnectAsync(ct);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Agent fallback: with the agent socket pointing at a nonexistent
    /// path, a credentials callback that first returns a
    /// <see cref="GitSshAgentCredential"/> must be RE-PROMPTED (the
    /// agent-connect failure is converted to a retryable
    /// AuthenticationFailed, parity with <c>ssh_libssh2.c:246-251</c>) and
    /// the second answer (password) must complete the connection.
    /// </summary>
    /// <remarks>
    /// The AgentProtocol error is converted to a retryable failure, so a
    /// missing ssh-agent lets the user fall back to password/keys.
    /// </remarks>
    [Fact]
    public async Task Auth_AgentSocketUnavailable_FallsBackToPassword()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // A socket path that certainly has no agent listening behind it.
        using GitContext ctx = new();
        ctx.Settings.AgentSocketPathOverride = () => Path.Combine(Path.GetTempPath(), "libgit2cs-no-such-agent.sock");

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";

        int attempt = 0;
        GitCredentialType firstMask = GitCredentialType.None;
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (mask, _, _, _) =>
            {
                if (attempt++ == 0)
                {
                    firstMask = mask;
                    return Task.FromResult<GitCredential?>(
                        new GitSshAgentCredential(SshGitDockerFixture.TestUser));
                }

                return Task.FromResult<GitCredential?>(
                    new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword));
            },
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-agentfb-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, ctx, cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
            IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);
            Assert.Contains(heads, h => h.Name == "refs/heads/main");
            await remote.DisconnectAsync(ct);

            // The agent attempt was made against a mask advertising
            // publickey, then the callback was re-prompted exactly once
            // more for the password fallback.
            Assert.Equal(2, attempt);
            Assert.True((firstMask & GitCredentialType.SshKey) != 0,
                $"first callback mask should include SshKey (server advertises publickey), got {firstMask}");
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Username resolution + unsupported-type re-prompt: with NO
    /// <c>user@</c> in the URL, the credentials callback is first invoked
    /// with the <see cref="GitCredentialType.Username"/> mask (returning a
    /// <see cref="GitUsernameCredential"/>), then re-invoked with the
    /// server-advertised method mask + resolved username + the full URL
    /// (parity with <c>ssh_libssh2.c:393-419</c>: the mask comes from
    /// <c>list_auth_methods</c>, the URL is <c>t-&gt;owner-&gt;url</c>, and
    /// a Username credential inside the auth loop is re-prompted, not
    /// dispatched).
    /// </summary>
    /// <remarks>
    /// The advertised-method mask and URL reach the callback, and a
    /// Username-only credential is re-prompted rather than aborting the
    /// connection.
    /// </remarks>
    [Fact]
    public async Task Auth_UsernameOnlyThenPassword_Reprompts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // No user@ in the URL.
        string url = $"ssh://{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";

        var masks = new List<GitCredentialType>();
        var urls = new List<string?>();
        var usernames = new List<string?>();
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (mask, callbackUrl, username, _) =>
            {
                masks.Add(mask);
                urls.Add(callbackUrl);
                usernames.Add(username);
                return Task.FromResult<GitCredential?>(
                    mask == GitCredentialType.Username
                        ? new GitUsernameCredential(SshGitDockerFixture.TestUser)
                        : new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword));
            },
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-useronly-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
            IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);
            Assert.Contains(heads, h => h.Name == "refs/heads/main");
            await remote.DisconnectAsync(ct);

            // 1st invocation: username-only mask, full URL, no username.
            // 2nd invocation: the server-advertised mask (password +
            // publickey on the Alpine fixture) + the resolved username; the
            // Username credential returned inside the loop was re-prompted,
            // never dispatched.
            Assert.Equal(2, masks.Count);
            Assert.Equal(GitCredentialType.Username, masks[0]);
            Assert.Null(usernames[0]);
            Assert.Equal(url, urls[0]);
            Assert.Equal(url, urls[1]);
            Assert.Equal(SshGitDockerFixture.TestUser, usernames[1]);
            Assert.True((masks[1] & GitCredentialType.UserPassPlaintext) != 0,
                $"advertised mask should include UserPassPlaintext, got {masks[1]}");
            Assert.True((masks[1] & GitCredentialType.SshKey) != 0,
                $"advertised mask should include SshKey, got {masks[1]}");
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// A callback credential whose username differs from the URL's
    /// <c>user@</c> hard-fails with libgit2's exact message (parity with
    /// <c>ssh_libssh2.c:859-863</c>) instead of silently authenticating as
    /// the URL user.
    /// </summary>
    /// <remarks>
    /// The username mismatch is rejected with libgit2's exact message.
    /// </remarks>
    [Fact]
    public async Task Auth_CallbackUsernameMismatch_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            // Wrong username in the credential (correct password, so the
            // failure must come from the mismatch check, not the wire).
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential("otheruser", SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-usermiss-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(() =>
                remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct));

            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Ssh, ex.Category);
            Assert.Equal("username does not match previous request", ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// A non-retryable auth failure surfaces with libgit2's wrapper
    /// message ("failed to authenticate SSH session: …",
    /// <c>ssh_libssh2.c:376-380</c>) instead of the raw LibSsh2CS error: a
    /// garbage private key fails the parse with
    /// <c>File/Unsupported private key file format</c>, which the auth loop
    /// wraps as a <see cref="GitException"/>.
    /// </summary>
    /// <remarks>
    /// Raw <c>SshException</c>s are wrapped as <see cref="GitException"/> at
    /// the transport boundary.
    /// </remarks>
    [Fact]
    public async Task Auth_GarbageKeyFile_FailsWithLibgit2Message()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        // Armored garbage (BEGIN/END markers + non-DER body): fails the
        // parse with File/"Unsupported private key file format" — the same
        // input shape as the parser unit-coverage for this path.
        string garbageKey = """
            -----BEGIN RSA PRIVATE KEY-----
            notbase64notbase64notbase64notbase64notbase64notbase64notbase64
            -----END RSA PRIVATE KEY-----
            """;
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitSshKeyMemoryCredential(SshGitDockerFixture.TestUser, publicKey: null,
                    privateKey: System.Text.Encoding.ASCII.GetBytes(garbageKey), passphrase: null)),
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-badkey-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(() =>
                remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct));

            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Ssh, ex.Category);
            Assert.StartsWith("failed to authenticate SSH session: ", ex.Message);
            Assert.Contains("Unsupported private key file format", ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Keyboard-interactive auth ────────────────────────────────────────

    /// <summary>
    /// Keyboard-interactive auth: <c>ConnectAsync</c> + <c>LsAsync</c>
    /// over SSH with <see cref="GitSshInteractiveCredential"/> succeeds and
    /// returns the seeded <c>refs/heads/main</c> head.
    /// </summary>
    /// <remarks>
    /// Uses the Debian image variant because Alpine's <c>openssh-server</c>
    /// is built without libpam linkage — it accepts the kbdint
    /// <c>USERAUTH_REQUEST</c> but immediately responds with
    /// <c>USERAUTH_FAILURE</c> because no kbdint device driver is compiled
    /// in. Debian's PAM-linked sshd drives the
    /// <c>USERAUTH_INFO_REQUEST</c> challenge via <c>pam_unix.so</c>.
    /// <para>
    /// The strict <c>promptCount &gt;= 1</c> assertion verifies the server
    /// actually drove the kbdint loop with at least one real challenge —
    /// without it, a regression that silently fell through to password auth
    /// (e.g. if kbdint were misconfigured server-side) would still pass the
    /// test. <c>pam_unix.so</c> sends one password prompt; other PAM modules
    /// in Debian's default <c>/etc/pam.d/sshd</c> may send additional
    /// informational INFO_REQUEST messages with 0 prompts (e.g. MOTD), so
    /// the count is cumulative across all callback invocations.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Auth_KeyboardInteractive_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Debian variant: PAM-linked sshd drives a real USERAUTH_INFO_REQUEST.
        await using SshGitDockerContainer fixture = await _debianNoKey.StartContainerAsync(_loggerFactory, ct);

        // Cumulative prompt count: PAM may send multiple INFO_REQUESTs across
        // the auth + session stack (password prompt + MOTD/info). We want the
        // total number of *actual* prompts (pam_unix.so's "Password: " = 1).
        int promptCount = 0;
        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitSshInteractiveCredential(
                    SshGitDockerFixture.TestUser,
                    (_, _, prompts, _) =>
                    {
                        promptCount += prompts.Length;
                        string[] answers = new string[prompts.Length];
                        Array.Fill(answers, SshGitDockerFixture.TestPassword);
                        return Task.FromResult(answers);
                    })),
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-kbdint-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
            IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);

            // Assert BEFORE DisconnectAsync — disconnect clears the cached heads.
            Assert.Contains(heads, h => h.Name == "refs/heads/main");

            await remote.DisconnectAsync(ct);

            // Strict: confirms the server actually drove the kbdint loop with
            // at least one prompt. pam_unix.so sends exactly one ("Password: ").
            Assert.True(promptCount >= 1, $"expected >= 1 kbdint prompt, got {promptCount}");
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Clone / fetch / push ───────────────────────────────────────────────

    /// <summary>
    /// Clone over <c>ssh://</c>: <see cref="GitClone.RunAsync"/> produces a
    /// local repo with <c>refs/remotes/origin/main</c> and the seeded commit
    /// reachable from <c>HEAD</c>.
    /// </summary>
    [Fact]
    public async Task Clone_SshUrl_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-clone-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // The cloned repo should have refs/remotes/origin/main pointing
            // at the seeded commit, and HEAD should resolve to refs/heads/main.
            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            Assert.True(head is GitDirectReference);

            // The commit must be present in the local object store.
            GitOid commitOid = ((GitDirectReference)head).Target;
            Assert.False(commitOid.IsZero);
            GitObject? obj = await cloned.ObjectLookupAsync(commitOid, ct);
            Assert.NotNull(obj);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Clone over SCP-style URL (<c>user@host:path</c>): same as
    /// <see cref="Clone_SshUrl_Succeeds"/> but exercising the SCP-style URL
    /// parsing + routing path in <see cref="GitTransportRegistry"/> +
    /// <see cref="GitSshUrl"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SCP-style URLs (<c>user@host:path</c>) have no port syntax in
    /// libgit2's grammar, so the Docker fixture's random host port can't
    /// go in the URL string. Instead, the test uses the LibGit2CS-specific
    /// <see cref="GitFetchOptions.ScpPortOverride"/> /
    /// <see cref="GitRemoteConnectOptions.ScpPortOverride"/> plumbing to
    /// attach the fixture's mapped port out-of-band. The override flows
    /// <c>GitFetchOptions</c> → <c>GitClone.CloneIntoAsync</c> →
    /// <c>GitRemoteConnectOptions</c> → <c>SshTransport</c> →
    /// <see cref="GitSshUrl.Parse(string, int?)"/>.
    /// </para>
    /// <para>
    /// <b>No env-var skip.</b> Unlike the previous incarnation of this
    /// test (which no-oped unless <c>SSH_SCP_TEST_PORT=22</c> was set), the
    /// override lets the test run against any Docker-mapped port. The
    /// SCP-style URL parsing itself is also covered by
    /// <c>GitSshUrlTests.Parse_Scp_*</c>; this test exercises the full
    /// transport + auth + pack-download path end-to-end.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Clone_ScpStyle_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // SCP-style URL — no scheme, no port in the URL string. The fixture's
        // random mapped port is supplied out-of-band via ScpPortOverride.
        string url = $"{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions
        {
            FetchOptions = new GitFetchOptions
            {
                RemoteCallbacks = callbacks,
                ScpPortOverride = fixture.Port,
            },
        };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-scp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            Assert.True(head is GitDirectReference);
            GitOid commitOid = ((GitDirectReference)head).Target;
            Assert.False(commitOid.IsZero);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Fetch over SSH: clone once, then add a second commit on the
    /// server side, then <see cref="GitRemote.FetchAsync"/> updates the local
    /// <c>refs/remotes/origin/main</c> to the new commit.
    /// </summary>
    [Fact]
    public async Task Fetch_OverSsh_UpdatesRefs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-fetch-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Clone the initial repo (1 commit on main).
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // The clone creates refs/heads/main (checked out) and
            // refs/remotes/origin/main (tracking). Verify the local HEAD
            // resolves — this confirms the pack was downloaded and indexed.
            GitReference? head1 = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head1);
            Assert.True(head1 is GitDirectReference);
            GitOid oid1 = ((GitDirectReference)head1).Target;

            // Add a second commit on the server side.
            await fixture.ExecAsync(
                $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
                "git config user.email t@t && git config user.name T && " +
                "echo second > second.txt && git add second.txt && git commit -m second'", ct);

            // Fetch.
            var fetchOpts = new GitFetchOptions { RemoteCallbacks = callbacks };
            GitRemote remote = await cloned.RemoteLookupAsync("origin", ct);
            await remote.FetchAsync(refspecs: null, fetchOpts, reflogMessage: null, ct);

            // After fetch, refs/remotes/origin/main should point at the new commit.
            GitReference? remoteHead = await cloned.ReferenceResolveAsync("refs/remotes/origin/main", ct);
            Assert.NotNull(remoteHead);
            GitOid oid2 = ((GitDirectReference)remoteHead).Target;

            // The remote-tracking ref should have advanced.
            Assert.NotEqual(oid1, oid2);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Push over SSH: clone once, add a commit locally, push to the
    /// server's <c>refs/heads/main</c>, then verify the server-side ref
    /// moved (by re-querying the ref advertisement via a fresh
    /// <c>ConnectAsync</c> + <c>LsAsync</c>).
    /// </summary>
    [Fact]
    public async Task Push_OverSsh_UpdatesRemoteRefs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Clone (gives us a working repo with origin remote configured).
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // Capture the pre-push server-side oid for main.
            GitReference? preHead = await cloned.ReferenceResolveAsync("refs/remotes/origin/main", ct);
            Assert.NotNull(preHead);
            GitOid preOid = ((GitDirectReference)preHead).Target;

            // Add a local commit on top of main.
            GitOid blobOid = await cloned.ObjectWriteAsync(GitObjectType.Blob, "pushed\n"u8.ToArray(), ct);
            using GitTreeBuilder treeBld = cloned.NewTreeBuilder();
            await treeBld.InsertAsync("pushed.txt", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await treeBld.WriteAsync(ct);
            GitOid newCommit = await cloned.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [preOid],
                Author = new GitSignature("t", "t@t", new GitTime(1700000000, 0)),
                Committer = new GitSignature("t", "t@t", new GitTime(1700000000, 0)),
                Message = "pushed\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            // Push main to origin.
            var pushOpts = new GitPushOptions { RemoteCallbacks = callbacks };
            GitRemote remote = await cloned.RemoteLookupAsync("origin", ct);
            GitPushResult pushResult = await remote.PushAsync(["refs/heads/main:refs/heads/main"], pushOpts, reflogMessage: null, ct);

            // The push must succeed and parse the report-status: UnpackOk=True
            // with one per-ref status (ok refs/heads/main).
            Assert.True(pushResult.UnpackOk, $"push UnpackOk=False; status=[{string.Join(", ", pushResult.Status.Select(s => $"{(s.Ok ? "ok" : "ng")} {s.Ref}"))}]");
            Assert.Single(pushResult.Status);
            Assert.True(pushResult.Status[0].Ok);
            Assert.Equal("refs/heads/main", pushResult.Status[0].Ref);

            // Verify the server-side ref actually moved by re-connecting
            // over SSH on a FRESH bare repo + FRESH GitRemote. This exercises
            // the re-connect path that a long-lived client uses for
            // `git fetch` after `git push`: the first SSH session (push) has
            // been torn down by PushAsync's DisconnectAsync, and a second
            // session must open cleanly and read the ref advertisement.
            // Assert before DisconnectAsync — see the ordering note at the
            // assertion below.
            var verifyConnectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };
            string barePath = Path.Combine(Path.GetTempPath(), "libgit2cs-push-verify-" + Guid.NewGuid().ToString("N"));
            await using GitRepository bareRepo = await GitRepository.InitAsync(barePath, isBare: true, new GitContext(), cancellationToken: ct);
            GitRemote verifyRemote = await bareRepo.RemoteCreateAsync("origin", url, ct);
            try
            {
                await verifyRemote.ConnectAsync(GitDirection.Fetch, verifyConnectOpts, ct);
                IReadOnlyList<GitRemoteHead> heads = await verifyRemote.LsAsync(ct);

                // Assert BEFORE DisconnectAsync — GitSmartTransport.LsAsync
                // returns the internal _heads list by reference, and
                // DisconnectAsync → CloseAsync → ResetStreamAsync clears it
                // in place (parity with libgit2's git_smart__recv clearing
                // _refs). The Auth_Password_Succeeds test has the same
                // ordering constraint (see its "Assert BEFORE DisconnectAsync"
                // comment).
                GitRemoteHead? main = heads.FirstOrDefault(h => h.Name == "refs/heads/main");
                Assert.True(main is not null,
                    $"refs/heads/main not in re-connect heads=[{(heads.Count == 0 ? "(empty)" : string.Join(", ", heads.Select(h => $"{h.Name}@{h.Oid}")))}]");
                Assert.Equal(newCommit, main!.Oid);

                await verifyRemote.DisconnectAsync(ct);
            }
            finally
            {
                await verifyRemote.DisposeAsync();
                try
                {
                    Directory.Delete(barePath, recursive: true);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Known hosts ──────────────────────────────────────────────────────

    /// <summary>
    /// Known hosts match: pre-write the container's real ed25519
    /// hostkey into a known_hosts file, inject via
    /// <c>ctx.Settings.KnownHostsPathOverride</c>, connect — the
    /// hostkey verification should pass WITHOUT the
    /// <c>CertificateCheck</c> callback (the key is already known).
    /// </summary>
    [Fact]
    public async Task KnownHosts_Match_Accepts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // Fetch the container's ed25519 hostkey.
        string hostkeyLine = await fixture.GetHostKeyAsync("ed25519", ct);

        // Write a known_hosts file with the container's real hostkey.
        // The hostkey line from OpenSSH is: "ssh-ed25519 <base64> comment"
        // The known_hosts format is: "<host>:<port> ssh-ed25519 <base64>"
        // But libssh2's known_hosts also accepts just "<host> ssh-ed25519 <base64>".
        // We use the [host]:port form to match the non-default port.
        string knownHostsEntry = $"[{SshGitDockerFixture.Host}]:{fixture.Port} {hostkeyLine.Trim()}\n";
        string knownHostsPath = Path.Combine(Path.GetTempPath(), "libgit2cs-knownhosts-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(knownHostsPath, knownHostsEntry, ct);

        // Per-context override: the SSH transport reads
        // ctx.Settings.KnownHostsPathOverride. The same ctx is passed to
        // InitAsync so the transport (built from repo.Context) sees it.
        using GitContext ctx = new();
        ctx.Settings.KnownHostsPathOverride = () => knownHostsPath;

        try
        {
            string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
            var callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                    new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
                // No CertificateCheck — the hostkey is in known_hosts.
            };
            var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

            string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-kh-" + Guid.NewGuid().ToString("N"));
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, ctx, cancellationToken: ct);
            try
            {
                GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
                await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
                IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);
                Assert.Contains(heads, h => h.Name == "refs/heads/main");
                await remote.DisconnectAsync(ct);
            }
            finally
            {
                try
                {
                    Directory.Delete(repoPath, recursive: true);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            try
            {
                File.Delete(knownHostsPath);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Known hosts mismatch: write a WRONG hostkey into the known_hosts
    /// file (same hostname, different key) with NO <c>CertificateCheck</c>
    /// callback installed. The connection must fail with
    /// <see cref="GitErrorCode.Certificate"/> and libgit2's exact message
    /// ("invalid or unknown remote ssh hostkey", parity with
    /// <c>check_certificate</c> + <c>ssh_libssh2.c:758-761</c>).
    /// </summary>
    /// <remarks>
    /// Without a callback the transport accepts only a known_hosts MATCH
    /// (<c>ssh_libssh2.c:746-756</c>); the mismatch fails hostkey
    /// verification, which surfaces as <c>KeyExchangeFailure</c> from the
    /// handshake and is remapped to <c>GIT_ECERTIFICATE</c> by the transport.
    /// The callback, when set, is always invoked (covered separately by
    /// <see cref="KnownHosts_Mismatch_CallbackAccepts_Succeeds"/>).
    /// </remarks>
    [Fact]
    public async Task KnownHosts_Mismatch_Rejected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // Write a known_hosts file with a FAKE ed25519 hostkey (the key
        // type is ed25519 but the key bytes are wrong).
        string fakeHostkey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFakeWrongKeyForTestingPurposesOnly0123456789 fake";
        string knownHostsEntry = $"[{SshGitDockerFixture.Host}]:{fixture.Port} {fakeHostkey}\n";
        string knownHostsPath = Path.Combine(Path.GetTempPath(), "libgit2cs-knownhosts-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(knownHostsPath, knownHostsEntry, ct);

        // Per-context override (see KnownHosts_Match_Accepts).
        using GitContext ctx = new();
        ctx.Settings.KnownHostsPathOverride = () => knownHostsPath;

        try
        {
            string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
            var callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                    new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
                // NO CertificateCheck: without a callback only a known_hosts
                // MATCH is accepted (ssh_libssh2.c:746-756).
            };
            var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

            string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-kh-" + Guid.NewGuid().ToString("N"));
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, ctx, cancellationToken: ct);
            try
            {
                GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
                GitException ex = await Assert.ThrowsAsync<GitException>(() =>
                    remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct));

                Assert.Equal(GitErrorCode.Certificate, ex.Code);
                Assert.Equal(GitErrorCategory.Ssh, ex.Category);
                Assert.Equal("invalid or unknown remote ssh hostkey", ex.Message);
            }
            finally
            {
                try
                {
                    Directory.Delete(repoPath, recursive: true);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            try
            {
                File.Delete(knownHostsPath);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Known hosts mismatch WITH an accepting <c>CertificateCheck</c>
    /// callback: libgit2's <c>check_certificate</c> always invokes the
    /// callback with the computed validity and honors its verdict in BOTH
    /// directions — a <c>true</c> return accepts even a MISMATCH (host-key
    /// rotation scenario). The connection must succeed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback is invoked for both NOT_FOUND and MISMATCH, and its
    /// verdict is honored in both directions.
    /// </para>
    /// <para>
    /// Also asserts the <see cref="GitCertificateInfo"/> the callback
    /// receives is fully populated: <c>HostKeyType</c> (wire name derived
    /// from the blob's first SSH string), <c>HostKeyMd5</c> (16 bytes),
    /// <c>HostKeySha1</c> (20), <c>HostKeySha256</c> (32),
    /// <c>HostKeyLength</c>, and the raw <c>HostKey</c> blob
    /// (<c>ssh_libssh2.c:686-739</c> parity).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task KnownHosts_Mismatch_CallbackAccepts_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // A FAKE ed25519 hostkey in known_hosts → the check computes
        // MISMATCH, but the callback below overrides the verdict.
        string fakeHostkey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFakeWrongKeyForTestingPurposesOnly0123456789 fake";
        string knownHostsEntry = $"[{SshGitDockerFixture.Host}]:{fixture.Port} {fakeHostkey}\n";
        string knownHostsPath = Path.Combine(Path.GetTempPath(), "libgit2cs-knownhosts-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(knownHostsPath, knownHostsEntry, ct);

        using GitContext ctx = new();
        ctx.Settings.KnownHostsPathOverride = () => knownHostsPath;

        try
        {
            string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";

            GitCertificateInfo? observed = null;
            var callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                    new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
                CertificateCheck = info =>
                {
                    observed = info;
                    return true; // Accept despite the mismatch (host-key rotation).
                },
            };
            var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

            string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-kh-" + Guid.NewGuid().ToString("N"));
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, ctx, cancellationToken: ct);
            try
            {
                GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
                await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
                IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);
                Assert.Contains(heads, h => h.Name == "refs/heads/main");
                await remote.DisconnectAsync(ct);

                // The callback must have been consulted with the
                // mismatch flagged and a fully populated certificate.
                Assert.NotNull(observed);
                Assert.Equal(GitCertificateType.HostkeySsh, observed!.Type);
                Assert.False(observed.IsValid);
                Assert.Equal(SshGitDockerFixture.Host, observed.Hostname);

                Assert.NotNull(observed.HostKey);
                Assert.Equal(observed.HostKey!.Length, observed.HostKeyLength);
                Assert.Equal(16, observed.HostKeyMd5!.Length);
                Assert.Equal(20, observed.HostKeySha1!.Length);
                Assert.Equal(32, observed.HostKeySha256!.Length);

                // HostKeyType is the wire name from the blob's first SSH
                // string — self-consistent with HostKey, and one of the
                // types ssh-keygen -A installs in the container.
                Assert.NotNull(observed.HostKeyType);
                Assert.Equal(ReadHostKeyWireName(observed.HostKey), observed.HostKeyType);
                Assert.Contains(observed.HostKeyType!, new[] { "ssh-rsa", "ecdsa-sha2-nistp256", "ssh-ed25519" });
            }
            finally
            {
                try
                {
                    Directory.Delete(repoPath, recursive: true);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            try
            {
                File.Delete(knownHostsPath);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Known hosts match with a REJECTING <c>CertificateCheck</c>
    /// callback: the callback's verdict is honored even for a MATCH — a
    /// <c>false</c> return fails the connection with
    /// <see cref="GitErrorCode.Certificate"/> (parity with
    /// <c>check_certificate</c> always honoring the callback return,
    /// <c>ssh_libssh2.c:746-756</c>).
    /// </summary>
    /// <remarks>
    /// Reverse direction: the callback is invoked with <c>IsValid = true</c>
    /// and the connection still fails.
    /// </remarks>
    [Fact]
    public async Task KnownHosts_Match_CallbackRejects_Fails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // The container's REAL ed25519 hostkey → the check computes MATCH;
        // the callback below rejects anyway.
        string hostkeyLine = await fixture.GetHostKeyAsync("ed25519", ct);
        string knownHostsEntry = $"[{SshGitDockerFixture.Host}]:{fixture.Port} {hostkeyLine.Trim()}\n";
        string knownHostsPath = Path.Combine(Path.GetTempPath(), "libgit2cs-knownhosts-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(knownHostsPath, knownHostsEntry, ct);

        using GitContext ctx = new();
        ctx.Settings.KnownHostsPathOverride = () => knownHostsPath;

        try
        {
            string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";

            bool? callbackIsValid = null;
            var callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                    new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
                CertificateCheck = info =>
                {
                    callbackIsValid = info.IsValid;
                    return false; // Reject despite the match.
                },
            };
            var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

            string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-kh-" + Guid.NewGuid().ToString("N"));
            await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, ctx, cancellationToken: ct);
            try
            {
                GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
                GitException ex = await Assert.ThrowsAsync<GitException>(() =>
                    remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct));

                Assert.Equal(GitErrorCode.Certificate, ex.Code);
                Assert.Equal(GitErrorCategory.Ssh, ex.Category);
                Assert.Equal("invalid or unknown remote ssh hostkey", ex.Message);

                // The callback was consulted with the computed MATCH.
                Assert.True(callbackIsValid);
            }
            finally
            {
                try
                {
                    Directory.Delete(repoPath, recursive: true);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            try
            {
                File.Delete(knownHostsPath);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Reads the first SSH string (the key-type wire name, e.g.
    /// <c>"ssh-ed25519"</c>) from a hostkey blob.
    /// </summary>
    private static string ReadHostKeyWireName(byte[] hostKey)
    {
        int len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hostKey.AsSpan(0, 4));
        return System.Text.Encoding.ASCII.GetString(hostKey, 4, len);
    }

    // ── Agent auth ───────────────────────────────────────────────────────

    /// <summary>
    /// SSH agent auth: start a host-side ssh-agent, load the test
    /// Ed25519 key, connect to the container (which has the matching
    /// public key in authorized_keys), and authenticate via the agent.
    /// Gated on <c>ssh-agent</c> + <c>ssh-add</c> being on PATH.
    /// </summary>
    /// <remarks>
    /// The agent socket is injected via the per-context
    /// <see cref="GitSettings.AgentSocketPathOverride"/> (set on the same
    /// <see cref="GitContext"/> passed to <see cref="GitRepository.InitAsync"/>),
    /// so the transport resolves the socket from the context instead of the
    /// process-global <c>SSH_AUTH_SOCK</c> environment variable. This keeps
    /// the test free of process-global env-var mutation, so it needs no
    /// serialization against concurrent classes.
    /// </remarks>
    [Fact]
    public async Task Auth_Agent_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HostSshAgent.SkipIfDockerOrSshAgentNotAvailable();

        byte[] privKeyBytes = FixtureLoader.LoadBytes("Fixtures.ssh.ed25519_key");

        await using SshGitDockerContainer fixture = await _alpineWithKey.StartContainerAsync(_loggerFactory, ct);
        await using HostSshAgent agentCtx = await HostSshAgent.StartAsync(privKeyBytes, ct);

        // Per-context agent-socket override: the SSH transport reads
        // ctx.Settings.AgentSocketPathOverride and passes the resolved path
        // to SshAgent, so the LibSsh2CS SshAgent finds the agent without
        // touching the process-global SSH_AUTH_SOCK env var.
        using GitContext ctx = new();
        ctx.Settings.AgentSocketPathOverride = () => agentCtx.SocketPath;

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitSshAgentCredential(SshGitDockerFixture.TestUser)),
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-agent-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, ctx, cancellationToken: ct);
        try
        {
            GitRemote remote = await repo.RemoteCreateAsync("origin", url, ct);
            await remote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
            IReadOnlyList<GitRemoteHead> heads = await remote.LsAsync(ct);
            Assert.Contains(heads, h => h.Name == "refs/heads/main");
            await remote.DisconnectAsync(ct);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Rekey under load ─────────────────────────────────────────────────

    /// <summary>
    /// Rekey under load: fetch a pack over SSH with a tiny
    /// <see cref="RekeyPolicy"/> threshold (MaxBytes=1024). The fetch
    /// should succeed and the underlying session should have rekeyed at
    /// least once. Uses <see cref="CapturingSshSessionFactory"/> to inject
    /// the policy and capture the session for assertion.
    /// </summary>
    [Fact]
    public async Task Rekey_UnderLoad_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";

        // Use the CapturingSshSessionFactory with a tiny rekey threshold.
        // We construct the GitSmartTransport directly (bypassing the
        // registry) to inject the factory. MaxBytes=256 (per-direction on
        // the encrypted wire) is small enough that the seeded 3-object repo
        // — whose fetch transfers only ~1–2 KB per direction including ref
        // advertisement, pack stream, and side-band framing — still forces
        // multiple rekey cycles. At MaxBytes=1024 the same transfer only
        // triggers a single rekey, which would not exercise the
        // reset-and-fire-again path.
        var rekeyPolicy = new LibSsh2CS.RekeyPolicy { MaxBytes = 256 };
        var sessionFactory = new CapturingSshSessionFactory(rekeyPolicy);
        var ctx = new GitContext();
        var sshTransport = new SshTransport(ctx, sessionFactory, timeProvider: null);
        var definition = new SubtransportDefinition(_ => sshTransport, IsRpc: false, null);
        await using GitSmartTransport transport = new(definition, ctx);

        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string repoPath = Path.Combine(Path.GetTempPath(), "libgit2cs-rekey-" + Guid.NewGuid().ToString("N"));
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            await transport.ConnectAsync(url, GitDirection.Fetch, connectOpts, ct);
            IReadOnlyList<GitRemoteHead> heads = await transport.LsAsync(ct);
            Assert.Contains(heads, h => h.Name == "refs/heads/main");

            // Download the pack (the seeded repo has 3 objects). At
            // MaxBytes=256 the encrypted transfer triggers several rekey
            // cycles. If any rekey fails, the fetch throws.
            var wants = new GitFetchNegotiation(heads, [], Depth: 0);
            await transport.NegotiateFetchAsync(repo, wants, ct);
            var stats = new GitIndexerProgress();
            await transport.DownloadPackAsync(repo, stats, ct);
            await transport.CloseAsync(ct);

            // The fetch completed without dropping the session. Now verify the
            // auto-rekey actually fired — a silent no-op would let the transfer
            // succeed without exercising the rekey path at all.
            //
            // At MaxBytes=256 against a ~1–2 KB per-direction transfer we
            // expect ~4–8 rekey cycles. Require >= 2 to robustly exercise the
            // rekey-reset-and-fire-again path (not just a single cycle), with
            // comfortable margin below the expected count. The bound is
            // intentionally loose above 2 because the exact count depends on
            // packetization timing and server-side chunking.
            Assert.NotNull(sessionFactory.LastSession);
            int rekeyCount = sessionFactory.LastSession!.RekeyCount;
            Assert.True(rekeyCount >= 2,
                "auto-rekey should have fired at least twice during the pack transfer; RekeyCount=" + rekeyCount);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Working-tree checkout (non-bare clone) ───────────────────────────

    /// <summary>
    /// Non-bare clone checks out the working tree: the seeded
    /// <c>README.md</c> must exist on disk in the clone's working directory
    /// with the seeded content, and the index must contain a single entry
    /// for it. Exercises the post-fetch checkout path
    /// (<see cref="GitRepository.CheckoutHeadAsync"/> → <see cref="Checkout.WorkdirWriter"/>
    /// → <see cref="IO.FilesystemIterator"/>) that the bare-clone tests skip.
    /// </summary>
    [Fact]
    public async Task Clone_NonBare_ChecksOutWorkingTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-worktree-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // The working tree file must exist with the seeded content.
            string readmePath = Path.Combine(targetPath, "README.md");
            Assert.True(File.Exists(readmePath), $"README.md not checked out at {readmePath}");
            string readmeContent = await File.ReadAllTextAsync(readmePath, ct);
            Assert.Equal("hello\n", readmeContent);

            // The index must contain exactly one entry (README.md).
            GitIndex index = await cloned.GetIndexAsync(ct);
            Assert.Equal(1, index.EntryCount);
            Assert.Equal("README.md", index.EntryByIndex(0).Path.ToUtf8String());
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Status detects working-tree edits in a non-bare clone: modify
    /// <c>README.md</c> on disk + add an untracked file, then
    /// <see cref="GitRepository.StatusNewAsync"/> reports both. Exercises
    /// <see cref="GitStatusList"/>, <see cref="IgnoreContext"/>,
    /// <see cref="IO.FilesystemIterator"/>, and the index→workdir diff path.
    /// </summary>
    [Fact]
    public async Task Clone_NonBare_StatusDetectsEdits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-status-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // Sanity: a fresh clone is clean.
            using (GitStatusList clean = await cloned.StatusNewAsync(cancellationToken: ct))
            {
                Assert.Equal(0, clean.EntryCount);
            }

            // Mutate the working tree: modify README.md, add untracked new.txt.
            string readmePath = Path.Combine(targetPath, "README.md");
            await File.WriteAllTextAsync(readmePath, "hello\nMODIFIED\n", ct);
            string newPath = Path.Combine(targetPath, "untracked.txt");
            await File.WriteAllTextAsync(newPath, "I am new\n", ct);

            using GitStatusList status = await cloned.StatusNewAsync(cancellationToken: ct);

            // Expect two entries: README.md (modified) + untracked.txt (new).
            Assert.Equal(2, status.EntryCount);

            GitStatusEntry? readmeEntry = null;
            GitStatusEntry? untrackedEntry = null;
            for (int i = 0; i < status.EntryCount; i++)
            {
                GitStatusEntry e = status.GetEntry(i);
                if (e.Path.ToUtf8String() == "README.md")
                {
                    readmeEntry = e;
                }
                else if (e.Path.ToUtf8String() == "untracked.txt")
                {
                    untrackedEntry = e;
                }
            }

            Assert.NotNull(readmeEntry);
            Assert.True((readmeEntry!.Status & GitStatusFlags.WorkdirModified) != 0,
                $"README.md should be WorkdirModified, got {readmeEntry.Status}");

            Assert.NotNull(untrackedEntry);
            Assert.True((untrackedEntry!.Status & GitStatusFlags.WorkdirNew) != 0,
                $"untracked.txt should be WorkdirNew, got {untrackedEntry.Status}");
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Diff of working tree vs index in a non-bare clone: modify
    /// <c>README.md</c>, then <see cref="GitDiff.IndexToWorkdirAsync"/>
    /// reports a delta for it and the rendered patch contains the
    /// expected <c>+MODIFIED</c> line. Exercises <see cref="DiffGenerator"/>,
    /// <see cref="PatchGenerator"/>, <see cref="XdiffBridge"/>, and
    /// <see cref="GitDiff.PrintAsync"/>.
    /// </summary>
    [Fact]
    public async Task Clone_NonBare_DiffOfWorkingTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-diff-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // Modify README.md so the index→workdir diff has something to say.
            string readmePath = Path.Combine(targetPath, "README.md");
            await File.WriteAllTextAsync(readmePath, "hello\nMODIFIED\n", ct);

            using GitDiff diff = await cloned.DiffIndexToWorkdirAsync(cancellationToken: ct);

            Assert.Equal(1, diff.DeltaCount);
            GitDiffDelta delta = diff.GetDelta(0);
            Assert.Equal("README.md", delta.OldFile.Path?.ToUtf8String());
            Assert.Equal(LibGit2CS.Diff.GitDeltaStatus.Modified, delta.Status);

            // Render the patch and verify the added line is present. The
            // <c>+MODIFIED</c> line is the added line; `hello` is unchanged
            // context so it appears as ` hello` (leading space) rather than
            // as an addition/deletion.
            string patch = await diff.ToBufferTextAsync(GitDiffPrintFormat.Patch, ct);
            Assert.Contains("+MODIFIED", patch, StringComparison.Ordinal);
            Assert.Contains("README.md", patch, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Multi-ref fetch: branches + annotated tag ────────────────────────

    /// <summary>
    /// Server grows a second branch and an annotated tag; the client
    /// sees all three refs in <c>LsAsync</c>, fetches the feature branch
    /// via explicit refspec, then fetches the annotated tag via explicit
    /// refspec and peels the tag object to its target commit. Exercises
    /// annotated-tag parsing (<see cref="GitTag"/>), multi-ref refspec
    /// filtering, and the refs/remotes/* + refs/tags/* write paths.
    /// </summary>
    /// <remarks>
    /// The annotated tag is fetched via an explicit
    /// <c>+refs/tags/v1.0:refs/tags/v1.0</c> refspec rather than relying on
    /// <see cref="GitAutoTagOption.All"/> auto-download: clone's default
    /// refspec is <c>+refs/heads/*:refs/remotes/origin/*</c> (heads only),
    /// so a fresh clone never has the tag locally until either (a) the
    /// auto-tag path fires (which requires the tag's target commit to be
    /// in the local store first), or (b) an explicit tag refspec is added.
    /// The explicit-refspec path is the one we exercise here.
    /// </remarks>
    [Fact]
    public async Task Fetch_MultiBranchAndAnnotatedTag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // Server-side: branch 'feature' at main (no tag yet — annotated tags
        // advertise a peeled `refs/tags/v1.0^{}` line that the clone-time ref
        // parser doesn't strip, which hangs upload-pack negotiation; the tag
        // is created AFTER clone so the clone advertisement never sees it).
        await fixture.ExecAsync(
            $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
            "git branch feature'", ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions
        {
            FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks },
        };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-multiref-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // Confirm the feature branch came over with the default heads/* refspec.
            GitReference? featureRef = await cloned.ReferenceResolveAsync("refs/remotes/origin/feature", ct);
            Assert.NotNull(featureRef);
            Assert.True(featureRef is GitDirectReference);
            GitOid mainOid = ((GitDirectReference)featureRef).Target;

            // Server-side: NOW create the annotated tag at main.
            await fixture.ExecAsync(
                $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
                "git config user.email t@t && git config user.name T && " +
                "git tag -a v1.0 -m \"version 1.0\" main'", ct);

            // Fetch the annotated tag via an explicit tag refspec. The clone's
            // default refspec is heads/* only, so refs/tags/v1.0 doesn't exist
            // locally yet — this fetch both pulls the tag object and creates
            // the local refs/tags/v1.0 ref.
            GitRemote remote = await cloned.RemoteLookupAsync("origin", ct);
            var tagFetchOpts = new GitFetchOptions { RemoteCallbacks = callbacks };
            await remote.FetchAsync(["+refs/tags/v1.0:refs/tags/v1.0"], tagFetchOpts, reflogMessage: null, ct);

            // The annotated tag object must now be present locally and peel
            // to the main commit.
            GitReference? tagRef = await cloned.ReferenceResolveAsync("refs/tags/v1.0", ct);
            Assert.NotNull(tagRef);
            Assert.True(tagRef is GitDirectReference, "refs/tags/v1.0 should be a direct reference");
            GitOid tagObjOid = ((GitDirectReference)tagRef).Target;

            GitTag? tag = await cloned.ObjectLookupAsync<GitTag>(tagObjOid, ct);
            Assert.NotNull(tag);
            Assert.Equal("v1.0", tag!.Name);
            Assert.Equal("version 1.0\n", tag.Message);
            Assert.Equal(GitObjectType.Commit, tag.TargetType);

            // The tag's target commit must match the feature/main tip.
            Assert.Equal(mainOid, tag.Target);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Shallow fetch ────────────────────────────────────────────────────

    /// <summary>
    /// Shallow fetch with <see cref="GitFetchOptions.Depth"/>=1: with
    /// three commits server-side, the clone must succeed but only the tip
    /// commit must be reachable — the parent OID advertised by the tip is
    /// NOT in the object database, and the <c>.git/shallow</c> file is
    /// written. Exercises <see cref="FetchCoordinator"/> depth negotiation,
    /// the <see cref="GitSmartProtocol"/> deepen handshake, and
    /// <see cref="Grafts.WriteShallowAsync"/>.
    /// </summary>
    [Fact]
    public async Task Fetch_Shallow_Depth1()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // Server-side: grow main to 3 commits total (initial + 2 more).
        await fixture.ExecAsync(
            $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            "echo c2 > c2.txt && git add c2.txt && git commit -m c2 && " +
            "echo c3 > c3.txt && git add c3.txt && git commit -m c3'", ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions
        {
            FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks, Depth = 1 },
        };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-shallow-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // Tip commit must be present.
            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            Assert.True(head is GitDirectReference);
            GitOid tipOid = ((GitDirectReference)head).Target;

            Commit tip = await cloned.ObjectLookupAsync<Commit>(tipOid, ct)
                ?? throw new InvalidOperationException("tip commit missing after shallow clone");
            Assert.Equal("c3\n", tip.Message);

            // The tip advertises its parent OID in the commit text, but a
            // depth=1 shallow clone must NOT have downloaded the parent
            // object. Looking it up must return null.
            Assert.NotEmpty(tip.Parents);
            GitOid parentOid = tip.Parents[0];
            Commit? parent = await cloned.ObjectLookupAsync<Commit>(parentOid, ct);
            Assert.Null(parent);

            // .git/shallow must exist and name the tip — this is the
            // shallow boundary marker written by Grafts.WriteShallowAsync.
            string shallowPath = Path.Combine(cloned.Path, "shallow");
            Assert.True(File.Exists(shallowPath),
                $".git/shallow not written at {shallowPath}; repo.Path={cloned.Path}");
            string shallowContent = await File.ReadAllTextAsync(shallowPath, ct);
            Assert.Contains(tipOid.ToString(), shallowContent, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── BranchName option ────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitCloneOptions.BranchName"/>: when the server has
    /// both <c>main</c> and <c>feature</c> branches, cloning with
    /// <c>BranchName="feature"</c> must check out <c>feature</c> — HEAD
    /// points at <c>refs/heads/feature</c>, the working tree contains the
    /// feature-only file, and the clone's local <c>refs/heads/feature</c>
    /// tracks <c>refs/remotes/origin/feature</c>.
    /// </summary>
    /// <remarks>
    /// Exercises <see cref="GitClone"/>'s <c>UpdateHeadToBranchAsync</c>
    /// path (the <c>options.BranchName is { } branchName</c> branch in
    /// <c>CheckoutBranchAsync</c>) that the default clone tests skip —
    /// those take the <c>UpdateHeadToRemoteAsync</c> branch and pick the
    /// server's advertised HEAD.
    /// </remarks>
    [Fact]
    public async Task Clone_BranchName_ChecksOutFeature()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // Server-side: create refs/heads/feature with one extra commit
        // containing feature-only.txt, diverging from main.
        await fixture.ExecAsync(
            $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            "git checkout -b feature && " +
            "echo feature-only > feature-only.txt && git add feature-only.txt && " +
            "git commit -m feature-only && " +
            "git checkout main'", ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions
        {
            FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks },
            BranchName = "feature",
        };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-branch-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // HEAD must resolve through refs/heads/feature (symbolic ref),
            // NOT the server's default (main).
            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            Assert.True(head is GitDirectReference, "HEAD should resolve to a direct reference");
            GitOid featureTip = ((GitDirectReference)head).Target;
            Assert.False(featureTip.IsZero);

            // The symbolic HEAD must point at refs/heads/feature.
            GitReference? headSym = await cloned.ReferenceLookupAsync("HEAD", ct);
            Assert.True(headSym is GitSymbolicReference, "HEAD should be a symbolic reference");
            Assert.Equal("refs/heads/feature", ((GitSymbolicReference)headSym).TargetName);

            // The tracking ref must exist.
            GitReference? tracking = await cloned.ReferenceResolveAsync("refs/remotes/origin/feature", ct);
            Assert.NotNull(tracking);
            Assert.True(tracking is GitDirectReference);
            Assert.Equal(featureTip, ((GitDirectReference)tracking).Target);

            // Working tree must contain the feature-only file (proves we
            // checked out the feature tree, not main's).
            string featureFile = Path.Combine(targetPath, "feature-only.txt");
            Assert.True(File.Exists(featureFile),
                $"feature-only.txt missing from working tree — HEAD may have checked out the wrong branch");
            Assert.Equal("feature-only\n", await File.ReadAllTextAsync(featureFile, ct));

            // And main's README.md should also be present (feature branched off main).
            Assert.True(File.Exists(Path.Combine(targetPath, "README.md")));
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Revwalk over fetched history ─────────────────────────────────────

    /// <summary>
    /// Revwalk over fetched history: with 3 commits server-side and a
    /// full clone, <see cref="GitRevWalker"/> with
    /// <see cref="GitSortMode.Topological"/> yields exactly 3 commits in
    /// child-before-parent order, hiding the tip yields 0, and
    /// <see cref="GitRepository.DescendantOfAsync"/> confirms the
    /// tip descends from the root.
    /// </summary>
    /// <remarks>
    /// Exercises <see cref="GitRevWalker.PushHeadAsync"/>/
    /// <see cref="GitRevWalker.PushAsync"/>/
    /// <see cref="GitRevWalker.HideAsync"/>/
    /// <see cref="GitRevWalker.WalkAsync"/>, the commit-graph traversal in
    /// <see cref="CommitList"/>, and
    /// <see cref="GitRepository.DescendantOfAsync"/> — all of which
    /// are cold in the integration tests prior to this point because no
    /// clone had more than one reachable commit.
    /// </remarks>
    [Fact]
    public async Task RevWalk_OverFetchedHistory_YieldsOrderedCommits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // Grow main to 3 commits (initial + 2 more) server-side.
        await fixture.ExecAsync(
            $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            "echo c2 > c2.txt && git add c2.txt && git commit -m c2 && " +
            "echo c3 > c3.txt && git add c3.txt && git commit -m c3'", ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-revwalk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            Assert.True(head is GitDirectReference);
            GitOid tipOid = ((GitDirectReference)head).Target;

            // Topological walk from HEAD: yields all 3 commits, child-first.
            List<GitOid> topo;
            using (GitRevWalker walker = cloned.NewRevWalker())
            {
                walker.Sort = GitSortMode.Topological;
                await walker.PushHeadAsync(ct);
                topo = await walker.WalkAsync(ct).ToListAsync(ct);
            }

            Assert.Equal(3, topo.Count);
            Assert.Equal(tipOid, topo[0]);

            // Verify the topological invariant: each parent appears AFTER its
            // child in the walk order.
            var positions = topo.Select((oid, idx) => (oid, idx)).ToDictionary(x => x.oid, x => x.idx);
            for (int i = 0; i < topo.Count; i++)
            {
                Commit? c = await cloned.ObjectLookupAsync<Commit>(topo[i], ct);
                Assert.NotNull(c);
                foreach (GitOid parentId in c!.Parents)
                {
                    if (positions.TryGetValue(parentId, out int parentIdx))
                    {
                        Assert.True(parentIdx > i,
                            $"topological invariant violated: parent {parentId} at idx {parentIdx} must come after child at idx {i}");
                    }
                }
            }

            // Root commit = last in topological order (oldest ancestor). The
            // seeded history is linear so topo[^1] is the initial commit
            // (which has no parents).
            GitOid rootOid = topo[^1];
            Commit? root = await cloned.ObjectLookupAsync<Commit>(rootOid, ct);
            Assert.NotNull(root);
            Assert.Empty(root!.Parents);

            // DescendantOf: tip IS a descendant of root; root is NOT a descendant of tip.
            Assert.True(await cloned.DescendantOfAsync(tipOid, rootOid, ct),
                "tip should be a descendant of root");
            Assert.False(await cloned.DescendantOfAsync(rootOid, tipOid, ct),
                "root should NOT be a descendant of tip");

            // Hide the tip — the walk should yield nothing because every
            // reachable commit is an ancestor of (and thus hidden by) the tip.
            List<GitOid> hidden;
            using (GitRevWalker walker2 = cloned.NewRevWalker())
            {
                walker2.Sort = GitSortMode.Topological;
                await walker2.PushAsync(tipOid, ct);
                await walker2.HideAsync(tipOid, ct);
                hidden = await walker2.WalkAsync(ct).ToListAsync(ct);
            }

            Assert.Empty(hidden);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Push: non-fast-forward + force ───────────────────────────────────

    /// <summary>
    /// Non-fast-forward push: after the server advances <c>main</c>
    /// by one commit, a divergent local commit (sibling, not descendant)
    /// pushed without force must be rejected with
    /// <see cref="GitErrorCode.NonFastForward"/>; pushing the same refspec
    /// with the <c>+</c> force prefix must succeed and move the server ref.
    /// </summary>
    /// <remarks>
    /// Exercises the non-FF check in <see cref="PushCoordinator.FinishAsync"/>
    /// (<c>GitRepository.DescendantOfAsync</c> → throw) and the
    /// <c>+refspec</c> force-flag bypass in <see cref="GitRefSpec"/>, plus
    /// the server-side <c>git-receive-pack</c> accepting the rewritten ref.
    /// </remarks>
    [Fact]
    public async Task Push_NonFastForward_RejectedThenForceSucceeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = callbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-force-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Clone gives us refs/heads/main and refs/remotes/origin/main at c1.
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            GitReference? preRef = await cloned.ReferenceResolveAsync("refs/remotes/origin/main", ct);
            Assert.NotNull(preRef);
            GitOid c1 = ((GitDirectReference)preRef).Target;

            // Server-side: advance main to c2 (parent c1).
            await fixture.ExecAsync(
                $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
                "git config user.email t@t && git config user.name T && " +
                "echo server-c2 > server-c2.txt && git add server-c2.txt && git commit -m server-c2'", ct);

            // Locally: create a SIBLING commit c2' (also parent c1, different
            // tree) and point refs/heads/main at it. c2' is NOT a descendant
            // of c2 because c2 was just created server-side and we never
            // fetched it.
            GitOid blobOid = await cloned.ObjectWriteAsync(GitObjectType.Blob, "client-c2\n"u8.ToArray(), ct);
            using GitTreeBuilder treeBld = cloned.NewTreeBuilder();
            await treeBld.InsertAsync("client-c2.txt", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await treeBld.WriteAsync(ct);
            GitOid c2Prime = await cloned.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [c1],
                Author = new GitSignature("t", "t@t", new GitTime(1700000001, 0)),
                Committer = new GitSignature("t", "t@t", new GitTime(1700000001, 0)),
                Message = "client-c2\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            // Push without force → server must reject as non-FF. The
            // PushCoordinator's non-FF check throws BEFORE any pack is sent
            // (loid != roid, loid not a descendant of roid, no + force flag).
            var pushOpts = new GitPushOptions { RemoteCallbacks = callbacks };
            GitRemote remote = await cloned.RemoteLookupAsync("origin", ct);
            GitException ex = await Assert.ThrowsAsync<GitException>(() =>
                remote.PushAsync(["refs/heads/main:refs/heads/main"], pushOpts, reflogMessage: null, ct));
            Assert.Equal(GitErrorCode.NonFastForward, ex.Code);
            Assert.Equal(GitErrorCategory.Reference, ex.Category);

            // Push WITH the + force prefix → succeeds.
            var forcePushOpts = new GitPushOptions { RemoteCallbacks = callbacks };
            GitPushResult forceResult = await remote.PushAsync(
                ["+refs/heads/main:refs/heads/main"], forcePushOpts, reflogMessage: null, ct);

            Assert.True(forceResult.UnpackOk,
                $"force push UnpackOk=False; status=[{string.Join(", ", forceResult.Status.Select(s => $"{(s.Ok ? "ok" : "ng")} {s.Ref}"))}]");
            Assert.Single(forceResult.Status);
            Assert.True(forceResult.Status[0].Ok);
            Assert.Equal("refs/heads/main", forceResult.Status[0].Ref);

            // Verify the server-side ref moved to c2' by re-connecting on a
            // FRESH bare repo (Assert BEFORE DisconnectAsync — see the note
            // in Push_OverSsh_UpdatesRemoteRefs on why this ordering matters).
            var verifyConnectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };
            string barePath = Path.Combine(Path.GetTempPath(), "libgit2cs-force-verify-" + Guid.NewGuid().ToString("N"));
            await using GitRepository bareRepo = await GitRepository.InitAsync(barePath, isBare: true, new GitContext(), cancellationToken: ct);
            GitRemote verifyRemote = await bareRepo.RemoteCreateAsync("origin", url, ct);
            try
            {
                await verifyRemote.ConnectAsync(GitDirection.Fetch, verifyConnectOpts, ct);
                IReadOnlyList<GitRemoteHead> heads = await verifyRemote.LsAsync(ct);

                GitRemoteHead? main = heads.FirstOrDefault(h => h.Name == "refs/heads/main");
                Assert.True(main is not null, "refs/heads/main missing from re-connect heads");
                Assert.Equal(c2Prime, main!.Oid);

                await verifyRemote.DisconnectAsync(ct);
            }
            finally
            {
                await verifyRemote.DisposeAsync();
                try
                {
                    Directory.Delete(barePath, recursive: true);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Push: delete remote ref ──────────────────────────────────────────

    /// <summary>
    /// Push delete: with the server advertising <c>refs/heads/feature</c>,
    /// a push with the delete refspec <c>:refs/heads/feature</c> (empty
    /// source) removes the ref server-side. The report-status must show a
    /// single ok status for <c>refs/heads/feature</c>, and a subsequent
    /// <see cref="GitRemote.LsAsync"/> must NOT list the ref.
    /// </summary>
    /// <remarks>
    /// Exercises the empty-source refspec parsing in <see cref="GitRefSpec"/>
    /// and the delete path through <see cref="PushCoordinator"/> —
    /// <see cref="GitPushUpdate"/> with a zero <c>Src</c> (loid), no pack
    /// being built (<c>needPack</c> stays false), and the server-side
    /// <c>git-receive-pack</c> honoring the zero-old deletion.
    /// </remarks>
    [Fact]
    public async Task Push_DeleteRemoteRef_RemovesRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpineNoKey.StartContainerAsync(_loggerFactory, ct);

        // Server-side: create refs/heads/feature at main.
        await fixture.ExecAsync(
            $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
            "git branch feature main'", ct);

        string url = $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
        var callbacks = new GitRemoteCallbacks
        {
            Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
                new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
            CertificateCheck = _ => true,
        };
        var connectOpts = new GitRemoteConnectOptions { Callbacks = callbacks };

        string barePath = Path.Combine(Path.GetTempPath(), "libgit2cs-delete-" + Guid.NewGuid().ToString("N"));
        await using GitRepository bareRepo = await GitRepository.InitAsync(barePath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            // Sanity: server advertises feature before the delete.
            GitRemote preRemote = await bareRepo.RemoteCreateAsync("origin", url, ct);
            try
            {
                await preRemote.ConnectAsync(GitDirection.Push, connectOpts, ct);
                IReadOnlyList<GitRemoteHead> preHeads = await preRemote.LsAsync(ct);
                Assert.Contains(preHeads, h => h.Name == "refs/heads/feature");
                await preRemote.DisconnectAsync(ct);
            }
            finally
            {
                await preRemote.DisposeAsync();
            }

            // Delete push: empty source refspec. The 'origin' remote was
            // configured above — git_remote_create returns GIT_EEXISTS for
            // a duplicate name, so look it up.
            GitRemote pushRemote = await bareRepo.RemoteLookupAsync("origin", ct);
            try
            {
                var pushOpts = new GitPushOptions { RemoteCallbacks = callbacks };
                GitPushResult result = await pushRemote.PushAsync(
                    [":refs/heads/feature"], pushOpts, reflogMessage: null, ct);

                // Delete-only push sends no pack; UnpackOk must still be true
                // (the server reports it even for an empty pack-less session).
                Assert.True(result.UnpackOk,
                    $"delete push UnpackOk=False; status=[{string.Join(", ", result.Status.Select(s => $"{(s.Ok ? "ok" : "ng")} {s.Ref}"))}]");
                Assert.Single(result.Status);
                Assert.True(result.Status[0].Ok);
                Assert.Equal("refs/heads/feature", result.Status[0].Ref);
            }
            finally
            {
                await pushRemote.DisposeAsync();
            }

            // Verify: server no longer advertises feature.
            GitRemote verifyRemote = await bareRepo.RemoteLookupAsync("origin", ct);
            try
            {
                await verifyRemote.ConnectAsync(GitDirection.Fetch, connectOpts, ct);
                IReadOnlyList<GitRemoteHead> postHeads = await verifyRemote.LsAsync(ct);
                Assert.DoesNotContain(postHeads, h => h.Name == "refs/heads/feature");
                // main must still be present — only feature should be gone.
                Assert.Contains(postHeads, h => h.Name == "refs/heads/main");
                await verifyRemote.DisconnectAsync(ct);
            }
            finally
            {
                await verifyRemote.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(barePath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}
