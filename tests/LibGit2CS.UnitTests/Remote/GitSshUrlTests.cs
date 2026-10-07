using LibGit2CS.Core;
using LibGit2CS.Remote;

namespace LibGit2CS.UnitTests.Remote;

/// <summary>
/// Unit tests for <see cref="GitSshUrl.Parse"/>. Covers scheme-style
/// (ssh://, ssh+git://, git+ssh://) and SCP-style (user@host:path) URLs,
/// IPv6 literal handling (no canonicalization), and the cmdline-option
/// injection guard. Parity reference: libgit2 <c>net.c:464-804</c>
/// (<c>git_net_url_parse</c> + <c>git_net_url_parse_scp</c>) and
/// <c>ssh_libssh2.c:768-808</c> (<c>_git_ssh_setup_conn</c>).
/// </summary>
public sealed class GitSshUrlTests
{
    // ── Scheme-style basic ───────────────────────────────────────────────

    [Fact]
    public void Parse_Ssh_Basic()
    {
        var url = GitSshUrl.Parse("ssh://host/path");
        Assert.Null(url.User);
        Assert.Equal("host", url.Host);
        Assert.Null(url.Port);
        Assert.Equal("/path", url.Path);
        Assert.False(url.IsScpStyle);
    }

    [Fact]
    public void Parse_Ssh_WithUser()
    {
        var url = GitSshUrl.Parse("ssh://user@host/path");
        Assert.Equal("user", url.User);
        Assert.Null(url.Password);
        Assert.Equal("host", url.Host);
        Assert.Null(url.Port);
        Assert.Equal("/path", url.Path);
    }

    [Fact]
    public void Parse_Ssh_WithUserPassword()
    {
        // Parity net.c:265-277 — the userinfo splits at the LAST ':'.
        var url = GitSshUrl.Parse("ssh://user:secret@host/path");
        Assert.Equal("user", url.User);
        Assert.Equal("secret", url.Password);
        Assert.Equal("host", url.Host);
    }

    [Fact]
    public void Parse_Ssh_UserInfoMultipleColons_SplitsAtLastColon()
    {
        // Parity net.c:265-277 — the backward walk takes the first ':' seen
        // from the right, so "user:pa:ss" → user "user:pa", password "ss".
        var url = GitSshUrl.Parse("ssh://user:pa:ss@host/path");
        Assert.Equal("user:pa", url.User);
        Assert.Equal("ss", url.Password);
    }

    [Fact]
    public void Parse_Ssh_UserPassword_WithExplicitPort()
    {
        var url = GitSshUrl.Parse("ssh://user:secret@host:2222/path");
        Assert.Equal("user", url.User);
        Assert.Equal("secret", url.Password);
        Assert.Equal(2222, url.Port);
    }

    [Fact]
    public void Parse_Ssh_WithExplicitPort()
    {
        var url = GitSshUrl.Parse("ssh://host:2222/path");
        Assert.Equal(2222, url.Port);
        Assert.True(url.Port.HasValue);
    }

    [Fact]
    public void Parse_Ssh_DefaultPortIsNull_NotImplicit22()
    {
        // Port is null (default 22), NOT 22 — preserves git_net_url.port_specified
        // semantics so the transport can distinguish "22 by default" from "22 explicit".
        var url = GitSshUrl.Parse("ssh://host/path");
        Assert.Null(url.Port);
    }

    [Theory]
    [InlineData("ssh+git://user@host/path")]
    [InlineData("git+ssh://user@host/path")]
    public void Parse_Ssh_SchemeAliases_TreatedAsSsh(string urlStr)
    {
        var url = GitSshUrl.Parse(urlStr);
        Assert.Equal("user", url.User);
        Assert.Equal("host", url.Host);
        Assert.Null(url.Port);
        Assert.Equal("/path", url.Path);
        Assert.False(url.IsScpStyle);
    }

    [Fact]
    public void Parse_Ssh_RootPath_RejectsAsMalformed()
    {
        // Path "/" alone → "malformed git protocol URL" — git-upload-pack '/' is
        // never a valid repository path (gen_proto ssh_libssh2.c:74-77 rejects
        // empty/whitespace paths; we extend the same rejection to "/" since
        // it's not a usable repo path either).
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://host/"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Equal(GitErrorCategory.Net, ex.Category);
        Assert.Contains("malformed git protocol URL", ex.Message);
    }

    [Fact]
    public void Parse_Ssh_NoPath_RejectsAsMalformed()
    {
        // No path component at all (no trailing '/') → malformed.
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://host"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Equal("malformed git protocol URL", ex.Message);
    }

    // ── SCP-style ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_Scp_Basic()
    {
        var url = GitSshUrl.Parse("user@host:path");
        Assert.Equal("user", url.User);
        Assert.Equal("host", url.Host);
        Assert.Null(url.Port);  // SCP has no port syntax
        Assert.Equal("path", url.Path);
        Assert.True(url.IsScpStyle);
    }

    [Fact]
    public void Parse_Scp_NoUser()
    {
        var url = GitSshUrl.Parse("host:path");
        Assert.Null(url.User);
        Assert.Equal("host", url.Host);
        Assert.Equal("path", url.Path);
        Assert.True(url.IsScpStyle);
    }

    [Fact]
    public void Parse_Scp_PathWithSlashes()
    {
        var url = GitSshUrl.Parse("user@host:dir/sub/git");
        Assert.Equal("dir/sub/git", url.Path);
    }

    [Fact]
    public void Parse_Scp_NoPortSyntax()
    {
        // SCP-style does NOT support host:port:path syntax — the first ':'
        // is always the path separator (parity net.c:722). This holds even
        // under the LibGit2CS port-override extension: the override is an
        // out-of-band parameter to Parse, not part of the URL grammar, so
        // the URL string "user@host:22:path" still parses "22:path" as the
        // path. See Parse_Scp_WithPortOverride_Applies below.
        var url = GitSshUrl.Parse("user@host:22:path");
        Assert.Equal("host", url.Host);
        Assert.Equal("22:path", url.Path);  // The "22:path" is part of the path
        Assert.Null(url.Port);
    }

    // ── SCP-style port override (LibGit2CS divergence) ──────────────────
    // libgit2's SCP-style grammar (net.c:661-804) has no port syntax — the
    // PORT_START state is unreachable for normal URLs. LibGit2CS extends
    // Parse with an optional scpPortOverride parameter that attaches a port
    // to the returned record without changing the URL grammar. The override
    // is a programmer input (not URL-derived), so range violations throw
    // ArgumentOutOfRangeException (not GitException). Passing an override for
    // a scheme-style URL throws ArgumentException (the URL already encodes
    // its own port — a separate override is a misuse).

    [Fact]
    public void Parse_Scp_WithPortOverride_Applies()
    {
        var url = GitSshUrl.Parse("user@host:path", scpPortOverride: 2222);
        Assert.Equal("user", url.User);
        Assert.Equal("host", url.Host);
        Assert.Equal(2222, url.Port);
        Assert.True(url.Port.HasValue);
        Assert.Equal("path", url.Path);
        Assert.True(url.IsScpStyle);
    }

    [Fact]
    public void Parse_Scp_NoUser_WithPortOverride_Applies()
    {
        // Override works for the user-less SCP form too.
        var url = GitSshUrl.Parse("host:path", scpPortOverride: 2222);
        Assert.Null(url.User);
        Assert.Equal("host", url.Host);
        Assert.Equal(2222, url.Port);
        Assert.Equal("path", url.Path);
        Assert.True(url.IsScpStyle);
    }

    [Fact]
    public void Parse_Scp_PortOverride_Null_PreservesParity()
    {
        // Regression guard: null override == single-arg Parse behavior.
        // SCP-style Port MUST stay null (parity with libgit2 net.c:793-798).
        var withOverride = GitSshUrl.Parse("user@host:path", scpPortOverride: null);
        var singleArg = GitSshUrl.Parse("user@host:path");
        Assert.Null(withOverride.Port);
        Assert.Equal(singleArg, withOverride);
    }

    [Fact]
    public void Parse_Scp_PortOverride_DoesNotAlterUrlGrammar()
    {
        // The override attaches to Port only — Host/User/Path are parsed
        // from the URL string exactly as the single-arg path would. This
        // pins that "user@host:22:path" still treats "22:path" as the path
        // even when an override is supplied (the override is NOT a fallback
        // parser for an embedded port).
        var url = GitSshUrl.Parse("user@host:22:path", scpPortOverride: 2222);
        Assert.Equal("host", url.Host);
        Assert.Equal("22:path", url.Path);
        Assert.Equal(2222, url.Port);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(99999)]
    public void Parse_Scp_PortOverride_OutOfRange_Throws(int badPort)
    {
        // Programmer-supplied (not URL-derived) → ArgumentOutOfRangeException,
        // not GitException. Distinct from ParsePort's URL-derived range
        // check (which throws GitException with "invalid port in ssh URL").
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => GitSshUrl.Parse("user@host:path", scpPortOverride: badPort));
        Assert.Equal(badPort, ex.ActualValue);
        Assert.Contains("scpPortOverride", ex.Message);
    }

    [Fact]
    public void Parse_Scheme_PortOverride_NonNull_ThrowsArgument()
    {
        // Scheme-style URLs encode their own port — a separate override is
        // a misuse. Strict-by-design: catches callers that blanket-set the
        // override across a mix of URL schemes.
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => GitSshUrl.Parse("ssh://host:2222/path", scpPortOverride: 2223));
        Assert.Contains("scpPortOverride", ex.Message);
    }

    [Fact]
    public void Parse_Scheme_PortOverride_NonNull_NoUrlPort_ThrowsArgument()
    {
        // Even when the scheme-style URL has no explicit port (ssh://host/path),
        // passing an override is still a misuse — the caller should have used
        // ssh://host:port/path or switched to SCP-style. Strict applies
        // regardless of whether the URL has its own port.
        Assert.Throws<ArgumentException>(
            () => GitSshUrl.Parse("ssh://host/path", scpPortOverride: 2222));
    }

    [Fact]
    public void Parse_Scheme_PortOverride_Null_PreservesUrlPort()
    {
        // null override == single-arg behavior: URL's own port wins.
        var url = GitSshUrl.Parse("ssh://host:2222/path", scpPortOverride: null);
        Assert.Equal(2222, url.Port);
        Assert.False(url.IsScpStyle);
    }

    [Fact]
    public void Parse_SingleArg_DelegatesToOverrideOverload()
    {
        // The single-arg Parse is a shim over Parse(string, int?) with null.
        // This pins that contract: any change to the default-arg breaks the
        // shim, not the parity behavior.
        var a = GitSshUrl.Parse("user@host:path");
        var b = GitSshUrl.Parse("user@host:path", scpPortOverride: null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Parse_Ipv6_Scp_WithPortOverride_Applies()
    {
        // IPv6 SCP-style: [::1]:path + override → Port set, host preserved
        // verbatim (no canonicalization — same invariant as Parse_Ipv6_Scp).
        var url = GitSshUrl.Parse("[::1]:path", scpPortOverride: 2222);
        Assert.Equal("::1", url.Host);
        Assert.Null(url.User);
        Assert.Equal(2222, url.Port);
        Assert.Equal("path", url.Path);
        Assert.True(url.IsScpStyle);
    }

    // ── IPv6 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Ipv6_Ssh_WithPort()
    {
        var url = GitSshUrl.Parse("ssh://[::1]:2222/path");
        Assert.Equal("::1", url.Host);
        Assert.Equal(2222, url.Port);
        Assert.Equal("/path", url.Path);
        Assert.False(url.IsScpStyle);
    }

    [Fact]
    public void Parse_Ipv6_Ssh_NoPort()
    {
        var url = GitSshUrl.Parse("ssh://[::1]/path");
        Assert.Equal("::1", url.Host);
        Assert.Null(url.Port);
    }

    [Fact]
    public void Parse_Ipv6_Scp()
    {
        var url = GitSshUrl.Parse("[::1]:path");
        Assert.Equal("::1", url.Host);
        Assert.Null(url.User);
        Assert.Null(url.Port);
        Assert.Equal("path", url.Path);
        Assert.True(url.IsScpStyle);
    }

    [Fact]
    public void Parse_Ipv6_CanonicalNotNormalized()
    {
        // CRITICAL parity invariant: the host string is preserved verbatim,
        // NOT canonicalized. "0:0:0:0:0:0:0:1" stays as-is (NOT rewritten to "::1").
        // This matters for known_hosts lookups and error-message byte-equality
        // with libgit2. (System.Net.IPAddress.Parse would canonicalize — we
        // deliberately do NOT use it.)
        var url = GitSshUrl.Parse("ssh://[0:0:0:0:0:0:0:1]/path");
        Assert.Equal("0:0:0:0:0:0:0:1", url.Host);
    }

    [Fact]
    public void Parse_Ipv6_Scp_NoNormalization()
    {
        var url = GitSshUrl.Parse("[0:0:0:0:0:0:0:1]:path");
        Assert.Equal("0:0:0:0:0:0:0:1", url.Host);
        Assert.True(url.IsScpStyle);
    }

    [Fact]
    public void Parse_Ipv6_ZoneId_RejectsAsMalformed_ParityWithLibgit2()
    {
        // KNOWN LIMITATION carried from libgit2: the is_ipv6 char-class filter
        // (net.c:621-644) has no special handling for '%' zone-id separator, so
        // the non-hex chars in the zone (e.g. 't','h' in "eth0") fail the filter.
        // Link-local IPv6 SSH URLs are unsupported, matching libgit2.
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://[fe80::1%eth0]/path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("IPv6", ex.Message);
    }

    [Fact]
    public void Parse_Ipv6_UnmatchedOpeningBracket_Rejects()
    {
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://[::1/path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("IPv6", ex.Message);
    }

    [Fact]
    public void Parse_Ipv6_EmptyBrackets_Rejects()
    {
        // "[]" — zero colons, fails the colons > 1 filter (parity is_ipv6 net.c:634).
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://[]/path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public void Parse_Ipv6_Scp_NoClosingBracket_Rejects()
    {
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("[::1:path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("IPv6", ex.Message);
    }

    // ── /~ path preservation ─────────────────────────────────────────────

    [Fact]
    public void Parse_TildePath_Preserved()
    {
        // ssh://host/~user/repo → Path is "/~user/repo" verbatim.
        // The leading "/" is stripped by the TRANSPORT when building the
        // exec command (parity gen_proto ssh_libssh2.c:71-72), not by the parser.
        var url = GitSshUrl.Parse("ssh://host/~user/repo");
        Assert.Equal("/~user/repo", url.Path);
    }

    [Fact]
    public void Parse_Scp_TildePath_Preserved()
    {
        // SCP-style: user@host:~user/repo → Path is "~user/repo" (no leading "/").
        var url = GitSshUrl.Parse("user@host:~user/repo");
        Assert.Equal("~user/repo", url.Path);
    }

    // ── Cmdline-option injection guard ──────────────────────────────────

    [Fact]
    public void Reject_DashPath_SchemeStyle_NotRejected_PathStartsWithSlash()
    {
        // Parity: libgit2's cmdline-option check (ssh_libssh2.c:801) is on
        // url.path, which for ssh:// URLs is "/"-prefixed (e.g. "/-evil").
        // git_process__is_cmdline_option checks str[0] == '-' — "/-evil"
        // starts with '/', so libgit2 does NOT reject. The exec command
        // git-upload-pack '/-evil' is single-quoted, so the remote shell
        // won't misinterpret it as an option either. The cmdline-injection
        // guard matters for SCP-style paths (no leading '/'), tested below.
        var url = GitSshUrl.Parse("ssh://host/-evil");
        Assert.Equal("/-evil", url.Path);
    }

    [Fact]
    public void Reject_DashUser()
    {
        // Defense-in-depth from ssh_exec.c:142 (the exec transport this port
        // drops, but the guard is still meaningful for the wire).
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://-evil@host/path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("username begins with '-'", ex.Message);
    }

    [Fact]
    public void Reject_DashHost()
    {
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://-evil/path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("host begins with '-'", ex.Message);
    }

    [Fact]
    public void Reject_DashPath_ScpStyle()
    {
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("user@host:-evil"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("path begins with '-'", ex.Message);
    }

    // ── Other rejections ────────────────────────────────────────────────

    [Fact]
    public void Reject_MissingHost_Ssh()
    {
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh:///path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("host", ex.Message);
    }

    [Fact]
    public void Reject_InvalidPort_NonNumeric()
    {
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://host:abc/path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("invalid port", ex.Message);
    }

    [Fact]
    public void Reject_InvalidPort_Zero()
    {
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://host:0/path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("invalid port", ex.Message);
    }

    [Fact]
    public void Reject_InvalidPort_TooLarge()
    {
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("ssh://host:65536/path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("invalid port", ex.Message);
    }

    [Fact]
    public void Reject_Scp_EmptyPath()
    {
        // "user@host:" → empty path → scp_invalid "path is required" (net.c:782-783).
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("user@host:"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("path is required", ex.Message);
    }

    [Fact]
    public void Reject_Scp_NoColon()
    {
        // "user@host" with no ':' → no path separator → scp_invalid "path is required".
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("user@host"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("path is required", ex.Message);
    }

    [Fact]
    public void Reject_Scp_LeadingAt()
    {
        // Parity net.c:684 — '@' at position 0 is "unexpected '@'".
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse("@host:path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public void Reject_Scp_LeadingColon()
    {
        // Parity net.c:686 — ':' at position 0 is "unexpected ':'".
        GitException ex = Assert.Throws<GitException>(() => GitSshUrl.Parse(":path"));
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public void Reject_Null_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => GitSshUrl.Parse(null!));
    }

    // ── Credential subtype smoke tests ──────────────────────────────────
    // Construct each subtype and verify the Type bit + username. The
    // transport dispatch keys on Type; these tests pin the contract.

    [Fact]
    public void GitSshKeyCredential_TypeIsSshKey()
    {
        using GitSshKeyCredential cred = new("user", "/pub/path", "/priv/path", "pass");
        Assert.Equal(GitCredentialType.SshKey, cred.Type);
        Assert.Equal("user", cred.Username);
        Assert.Equal("/pub/path", cred.PublicKeyPath);
        Assert.Equal("/priv/path", cred.PrivateKeyPath);
        Assert.Equal("pass", cred.Passphrase);
    }

    [Fact]
    public void GitSshKeyCredential_NullPublicKeyPath_Accepted()
    {
        // Parity with C accepting NULL publickey (credential.c:254-257).
        using GitSshKeyCredential cred = new("user", null, "/priv/path", null);
        Assert.Null(cred.PublicKeyPath);
        Assert.Equal(GitCredentialType.SshKey, cred.Type);
    }

    [Fact]
    public void GitSshKeyCredential_EmptyUsername_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GitSshKeyCredential("", null, "/priv/path", null));
    }

    [Fact]
    public void GitSshKeyCredential_EmptyPrivateKeyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GitSshKeyCredential("user", null, "", null));
    }

    [Fact]
    public void GitSshKeyMemoryCredential_TypeIsSshMemory()
    {
        byte[] privKey = new byte[] { 1, 2, 3 };
        using GitSshKeyMemoryCredential cred = new("user", null, privKey, null);
        Assert.Equal(GitCredentialType.SshMemory, cred.Type);
        Assert.Equal("user", cred.Username);
        Assert.Null(cred.PublicKey);
        Assert.Same(privKey, cred.PrivateKey);
    }

    [Fact]
    public void GitSshKeyMemoryCredential_DisposeZerosKeyBytes()
    {
        byte[] privKey = new byte[] { 1, 2, 3 };
        byte[] pubKey = new byte[] { 4, 5 };
        GitSshKeyMemoryCredential cred = new("user", pubKey, privKey, null);
        cred.Dispose();
        Assert.Null(cred.PrivateKey);
        Assert.Null(cred.PublicKey);
        // The arrays themselves are cleared in-place.
        Assert.All(privKey, b => Assert.Equal(0, b));
        Assert.All(pubKey, b => Assert.Equal(0, b));
    }

    [Fact]
    public void GitSshAgentCredential_TypeIsSshKey()
    {
        // Agent reuses SshKey (parity git_credential_ssh_key_from_agent which
        // sets credtype = GIT_CREDENTIAL_SSH_KEY, credential.c:305).
        using GitSshAgentCredential cred = new("user");
        Assert.Equal(GitCredentialType.SshKey, cred.Type);
        Assert.Equal("user", cred.Username);
    }

    [Fact]
    public void GitSshInteractiveCredential_TypeIsSshInteractive()
    {
        static Task<string[]> Cb(string n, string i, LibSsh2CS.SshKeyboardInteractivePrompt[] p, CancellationToken ct)
            => Task.FromResult(p.Select(_ => "ans").ToArray());

        using GitSshInteractiveCredential cred = new("user", Cb);
        Assert.Equal(GitCredentialType.SshInteractive, cred.Type);
        Assert.Equal("user", cred.Username);
        Assert.NotNull(cred.Callback);
    }
}
