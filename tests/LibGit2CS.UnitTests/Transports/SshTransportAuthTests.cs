using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Transports;
using LibGit2CS.UnitTests.Transports.TestInfrastructure;

using LibSsh2CS;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Unit tests for <see cref="SshTransport"/>'s auth retry loop and
/// credential-type dispatch (parity <c>_git_ssh_authenticate_session</c>
/// lines 292-424). Uses <see cref="FakeSshSession"/> + a fake factory — no
/// real SSH server.
/// </summary>
/// <remarks>
/// The auth loop is the only place in <see cref="SshTransport"/> where
/// retryable errors (<c>AuthenticationFailed</c>,
/// <c>PasswordExpired</c>, <c>PublicKeyUnverified</c>) trigger a credential
/// re-prompt. These tests pin that behavior plus the per-credential-type
/// dispatch (password / file publickey / memory publickey / agent /
/// keyboard-interactive).
/// </remarks>
public sealed class SshTransportAuthTests
{
    // ── IsRetryable (pure parity mapping) ───────────────────────────────

    [Theory]
    [InlineData(SshErrorCode.AuthenticationFailed)]
    [InlineData(SshErrorCode.PasswordExpired)]
    [InlineData(SshErrorCode.PublicKeyUnverified)]
    public void IsRetryable_EauthCodes_True(SshErrorCode code)
        => Assert.True(SshTransport.IsRetryable(code));

    [Theory]
    [InlineData(SshErrorCode.None)]
    [InlineData(SshErrorCode.SocketDisconnect)]
    [InlineData(SshErrorCode.Proto)]
    [InlineData(SshErrorCode.KeyfileAuthFailed)]
    public void IsRetryable_NonEauthCodes_False(SshErrorCode code)
        => Assert.False(SshTransport.IsRetryable(code));

    // ── DispatchAuthAsync (per-credential-type) ─────────────────────────

    [Fact]
    public async Task Dispatch_PasswordCredential_CallsPasswordAuth()
    {
        var session = new FakeSshSession();
        var cred = new GitUserPassCredential("user", "pass");

        await SshTransportTestAccess.DispatchAuthAsync(session, "user", cred, TestContext.Current.CancellationToken);

        Assert.Equal(1, session.PasswordAuthCalls);
        Assert.Equal(("user", "pass"), session.LastPasswordAuth);
    }

    [Fact]
    public async Task Dispatch_InteractiveCredential_CallsKeyboardInteractive()
    {
        var session = new FakeSshSession();
        var cred = new GitSshInteractiveCredential("user", (_, _, _, _) => Task.FromResult<string[]>(["response"]));

        await SshTransportTestAccess.DispatchAuthAsync(session, "user", cred, TestContext.Current.CancellationToken);

        Assert.Equal(1, session.KeyboardInteractiveCalls);
    }

    [Fact]
    public async Task Dispatch_KeyMemoryCredential_CallsPublicKeyAuthWithBytes()
    {
        var session = new FakeSshSession();
        byte[] privKey = "private-key-bytes"u8.ToArray();
        byte[] pubKey = "public-key-bytes"u8.ToArray();
        var cred = new GitSshKeyMemoryCredential("user", pubKey, privKey, passphrase: null);

        await SshTransportTestAccess.DispatchAuthAsync(session, "user", cred, TestContext.Current.CancellationToken);

        Assert.Equal(1, session.PublicKeyAuthCalls);
        Assert.Equal(privKey, session.LastPublicKeyPrivateKey);
        Assert.Equal(pubKey, session.LastPublicKeyBlob);
    }

    [Fact]
    public async Task Dispatch_AgentCredential_CallsAgentAuth()
    {
        var session = new FakeSshSession();
        var cred = new GitSshAgentCredential("user");

        await SshTransportTestAccess.DispatchAuthAsync(session, "user", cred, TestContext.Current.CancellationToken);

        Assert.Equal(1, session.AgentAuthCalls);
        Assert.Equal("user", session.LastAgentUsername);
        // No override threaded through the test-access shim → null (use $SSH_AUTH_SOCK default).
        Assert.Null(session.LastAgentSocketPath);
    }

    [Fact]
    public async Task Dispatch_AgentCredential_ThreadsAgentSocketPath()
    {
        // Proves the per-context AgentSocketPathOverride plumbing: a non-null
        // socket path reaches ISshSession.AuthenticateWithAgentAsync verbatim,
        // so SshSessionAdapter constructs `new SshAgent(path)` instead of the
        // env-var-discovery ctor. This is the seam SshTransportDockerTests.
        // Auth_Agent_Succeeds relies on (without the old SSH_AUTH_SOCK env mutation).
        var session = new FakeSshSession();
        var cred = new GitSshAgentCredential("user");

        await SshTransport.DispatchAuthAsync(session, "user", cred, agentSocketPath: "/tmp/test-agent.sock", TestContext.Current.CancellationToken);

        Assert.Equal(1, session.AgentAuthCalls);
        Assert.Equal("/tmp/test-agent.sock", session.LastAgentSocketPath);
    }

    [Fact]
    public async Task Dispatch_UnknownCredential_Throws()
    {
        var session = new FakeSshSession();
        var cred = new FakeCredential();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SshTransportTestAccess.DispatchAuthAsync(session, "user", cred, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Auth, ex.Code);
        Assert.Equal(GitErrorCategory.Ssh, ex.Category);
    }

    // ── AuthenticateAsync retry loop ────────────────────────────────────

    [Fact]
    public async Task Authenticate_RetryableError_RetriesAndSucceeds()
    {
        var session = new FakeSshSession();
        // First auth throws EAUTH, second succeeds.
        session.PublicKeyAuthResults.Enqueue(new SshException(SshErrorCode.AuthenticationFailed, "nope"));
        session.PublicKeyAuthResults.Enqueue(null); // success

        GitRemoteConnectOptions options = OptionsWith(cred => Task.FromResult<GitCredential?>(
            new GitSshKeyCredential("user", publicKeyPath: null, privateKeyPath: "/fake/key", passphrase: null)));

        // The file-based publickey path will try to read /fake/key — it'll
        // throw FileNotFoundException, not EAUTH. So use a memory credential
        // instead so the dispatch doesn't hit the filesystem.
        GitRemoteConnectOptions optionsMem = OptionsWith(cred => Task.FromResult<GitCredential?>(
            new GitSshKeyMemoryCredential("user", publicKey: null, privateKey: "key-bytes"u8.ToArray(), passphrase: null)));

        await SshTransportTestAccess.AuthenticateAsync(session, "user", optionsMem, TestContext.Current.CancellationToken);

        Assert.Equal(2, session.PublicKeyAuthCalls);
    }

    [Fact]
    public async Task Authenticate_NullCredential_ThrowsAuth()
    {
        var session = new FakeSshSession();
        GitRemoteConnectOptions options = OptionsWith(cred => Task.FromResult<GitCredential?>(null));

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SshTransportTestAccess.AuthenticateAsync(session, "user", options, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Auth, ex.Code);
        Assert.Equal(GitErrorCategory.Ssh, ex.Category);
    }

    [Fact]
    public async Task Authenticate_NoCallback_ThrowsAuth()
    {
        var session = new FakeSshSession();
        var options = new GitRemoteConnectOptions { Callbacks = new GitRemoteCallbacks() };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SshTransportTestAccess.AuthenticateAsync(session, "user", options, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Auth, ex.Code);
    }

    [Fact]
    public async Task Authenticate_NonRetryableError_ThrowsSshError()
    {
        var session = new FakeSshSession();
        session.PasswordAuthResults.Enqueue(new SshException(SshErrorCode.Proto, "protocol error"));

        GitRemoteConnectOptions options = OptionsWith(cred => Task.FromResult<GitCredential?>(
            new GitUserPassCredential("user", "pass")));

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SshTransportTestAccess.AuthenticateAsync(session, "user", options, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Ssh, ex.Category);
        Assert.Contains("protocol error", ex.Message);
    }

    [Fact]
    public async Task Authenticate_RefreshesMethodsAfterEachAttempt()
    {
        // Each retry loop iteration calls GetAuthMethodsAsync before
        // re-prompting — verify this happens.
        var session = new FakeSshSession();
        session.PasswordAuthResults.Enqueue(new SshException(SshErrorCode.AuthenticationFailed, "nope"));
        session.PasswordAuthResults.Enqueue(null); // success

        int credentialCallCount = 0;
        GitRemoteConnectOptions options = OptionsWith(cred =>
        {
            credentialCallCount++;
            return Task.FromResult<GitCredential?>(new GitUserPassCredential("user", $"pass{credentialCallCount}"));
        });

        await SshTransportTestAccess.AuthenticateAsync(session, "user", options, TestContext.Current.CancellationToken);

        // Two attempts → two GetAuthMethodsAsync calls (one per loop iteration).
        Assert.Equal(2, session.GetAuthMethodsCalls);
        Assert.Equal(2, credentialCallCount);
    }

    // ── Credential mask passed to callback ──────────────────────────────

    [Fact]
    public async Task Authenticate_PassesAdvertisedMethodMaskToCallback()
    {
        // The callback must receive the mask derived from the server's
        // advertised methods (parity list_auth_methods, ssh_libssh2.c:1029-1057)
        // — NOT the old static SSH-allowed mask. The fake advertises
        // publickey + password + keyboard-interactive.
        var session = new FakeSshSession();
        GitCredentialType capturedMask = GitCredentialType.None;
        string? capturedUrl = "<unset>";
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (mask, url, _, _) =>
                {
                    capturedMask = mask;
                    capturedUrl = url;
                    return Task.FromResult<GitCredential?>(new GitUserPassCredential("user", "pass"));
                },
            },
        };

        await SshTransportTestAccess.AuthenticateAsync(
            session, "user", options, url: "ssh://user@example.com/repo", urlCredential: null,
            cancellationToken: TestContext.Current.CancellationToken);

        const GitCredentialType ExpectedMask =
            GitCredentialType.UserPassPlaintext |
            GitCredentialType.SshKey |
            GitCredentialType.SshMemory |
            GitCredentialType.SshInteractive;
        Assert.Equal(ExpectedMask, capturedMask);

        // The URL is passed through to the callback (parity t->owner->url).
        Assert.Equal("ssh://user@example.com/repo", capturedUrl);
    }

    [Fact]
    public async Task Authenticate_UnsupportedCredentialType_FailsHard()
    {
        // C's
        // request_creds returns GIT_EAUTH "authentication callback returned
        // unsupported credentials type" (ssh_libssh2.c:415-419) and the
        // caller treats every request_creds failure as FATAL
        // (ssh_libssh2.c:873) — the credential is rejected, never dispatched,
        // and never re-prompted.
        // callback forever when it keeps returning an unadvertised type (a
        // callback that always returns Default against a password-only server
        // hung indefinitely).
        var session = new FakeSshSession();
        int callCount = 0;
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) =>
                {
                    callCount++;
                    return Task.FromResult<GitCredential?>(new FakeCredential());
                },
            },
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SshTransportTestAccess.AuthenticateAsync(session, "user", options, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Auth, ex.Code);
        Assert.Equal(GitErrorCategory.Ssh, ex.Category);
        Assert.Contains("unsupported credentials type", ex.Message);
        Assert.Equal(1, callCount); // C fails hard — no re-prompt
        Assert.Equal(0, session.PasswordAuthCalls); // never dispatched
    }

    [Fact]
    public async Task Authenticate_UrlCredential_TriedBeforeCallback()
    {
        // Parity ssh_libssh2.c:837-848: a URL-derived user:password credential
        // is tried BEFORE the credentials callback — the callback is never
        // invoked when the URL credential succeeds.
        var session = new FakeSshSession();
        int credentialCallCount = 0;
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) =>
                {
                    credentialCallCount++;
                    return Task.FromResult<GitCredential?>(new GitUserPassCredential("user", "fallback"));
                },
            },
        };

        using var urlCred = new GitUserPassCredential("user", "url-pass");
        await SshTransportTestAccess.AuthenticateAsync(
            session, "user", options, url: "ssh://user:url-pass@example.com/repo", urlCredential: urlCred,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, credentialCallCount);
        Assert.Equal(1, session.PasswordAuthCalls);
        Assert.Equal("url-pass", session.LastPasswordAuth.Password);
    }

    [Fact]
    public async Task Authenticate_UrlCredentialFailsRetryable_FallsBackToCallback()
    {
        var session = new FakeSshSession();
        session.PasswordAuthResults.Enqueue(new SshException(SshErrorCode.AuthenticationFailed, "nope"));
        session.PasswordAuthResults.Enqueue(null); // success via callback cred

        int credentialCallCount = 0;
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) =>
                {
                    credentialCallCount++;
                    return Task.FromResult<GitCredential?>(new GitUserPassCredential("user", "fallback"));
                },
            },
        };

        using var urlCred = new GitUserPassCredential("user", "url-pass");
        await SshTransportTestAccess.AuthenticateAsync(
            session, "user", options, url: "ssh://user:url-pass@example.com/repo", urlCredential: urlCred,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, credentialCallCount);
        Assert.Equal(2, session.PasswordAuthCalls);
        Assert.Equal("fallback", session.LastPasswordAuth.Password);
    }

    [Fact]
    public async Task Authenticate_UsernameMismatch_Throws()
    {
        // Parity ssh_libssh2.c:859-863: a callback credential carrying a
        // different username is a hard error, not a retry.
        var session = new FakeSshSession();
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) =>
                    Task.FromResult<GitCredential?>(new GitUserPassCredential("other-user", "pass")),
            },
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SshTransportTestAccess.AuthenticateAsync(session, "user", options, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("username does not match", ex.Message);
        Assert.Equal(0, session.PasswordAuthCalls);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static GitRemoteConnectOptions OptionsWith(Func<GitCredentialType, Task<GitCredential?>> credFactory)
    {
        return new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                Credentials = (_, _, _, _) => credFactory(GitCredentialType.None),
            },
        };
    }

    private sealed class FakeCredential : GitCredential
    {
        public override string? Username => "user";
        public override GitCredentialType Type => GitCredentialType.Default;
    }
}

/// <summary>
/// Internal-access shim exposing the private auth-loop methods on
/// <see cref="SshTransport"/> to the test assembly. Lives in the test
/// project so it can use the InternalsVisibleTo grant on LibGit2CS.csproj.
/// </summary>
internal static class SshTransportTestAccess
{
    public static Task DispatchAuthAsync(ISshSession session, string username, GitCredential cred, CancellationToken cancellationToken)
        => SshTransport.DispatchAuthAsync(session, username, cred, agentSocketPath: null, cancellationToken);

    public static Task AuthenticateAsync(ISshSession session, string username, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
        => SshTransport.AuthenticateAsync(session, username, options, agentSocketPath: null, url: null, urlCredential: null, cancellationToken);

    public static Task AuthenticateAsync(ISshSession session, string username, GitRemoteConnectOptions? options,
        string? url, GitCredential? urlCredential, CancellationToken cancellationToken)
        => SshTransport.AuthenticateAsync(session, username, options, agentSocketPath: null, url, urlCredential, cancellationToken);
}
