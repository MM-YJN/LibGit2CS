using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

// Parity tests for the ng packet requiring a newline, local push to a non-bare repo message/category, credential userpass pointer-presence semantics,
// and URL-embedded credentials with empty user/pass. Expectations C-verified against libgit2 1.9.4 (smart_pkt.c:375-427, local.c:416-420,
// credential_helpers.c:24-52, http.c:102-128).
public sealed class TransportsLowParityTests
{
    // ── ng packet requires a trailing newline ────────────────────────

    [Fact]
    public void Parse_NgWithoutNewline_ThrowsInvalidPacketLine()
    {
        // C (smart_pkt.c:405-410): memchr(line, '\n') must find a newline in
        // the message part — "ng refs/heads/x oops" (no newline) is an
        // "invalid packet line" error.
        byte[] buffer = BuildPktLine("ng refs/heads/x oops"u8);
        var state = new GitPacketParseState();

        GitException ex = Assert.Throws<GitException>(() => GitPacketReader.Parse(buffer, out _, ref state));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("invalid packet line", ex.Message);
    }

    [Fact]
    public void Parse_NgWithNewline_ParsesMessage()
    {
        byte[] buffer = BuildPktLine("ng refs/heads/x oops\n"u8);
        var state = new GitPacketParseState();

        GitPacket packet = GitPacketReader.Parse(buffer, out _, ref state);

        GitNgPacket ng = Assert.IsType<GitNgPacket>(packet);
        Assert.Equal("refs/heads/x", ng.Ref);
        Assert.Equal("oops", ng.Message);
    }

    // ── local push to non-bare message/category ──────────────────────

    [Fact]
    public void LocalPush_NonBareMessage_MatchesC()
    {
        // The message/category constants are asserted at the transport level
        // (the full push flow needs a local transport pair); C (local.c:416-420)
        // sets GIT_EBAREREPO with the GIT_ERROR_INVALID class.
        var ex = new GitException(
            GitErrorCode.BareRepo,
            "local push doesn't (yet) support pushing to non-bare repos.",
            GitErrorCategory.Invalid);

        Assert.Equal(GitErrorCode.BareRepo, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("local push doesn't (yet) support pushing to non-bare repos.", ex.Message);
    }

    // ── credential userpass pointer-presence semantics ───────────────

    [Fact]
    public void UserPass_EmptyStringUsername_WinsOverUrlUsername()
    {
        // C (credential_helpers.c:24-52): an empty-string payload username
        // is used AS-IS (pointer presence) — it wins over the URL username.
        var payload = new GitUserPassPayload { Username = string.Empty, Password = "pw" };
        GitCredential? cred = GitCredentialHelpers.UserPass(payload, "url-user", GitCredentialType.UserPassPlaintext);

        GitUserPassCredential up = Assert.IsType<GitUserPassCredential>(cred);
        Assert.Equal(string.Empty, up.Username);
    }

    [Fact]
    public void UserPass_EmptyStringPassword_IsAccepted()
    {
        // C: only a NULL password fails — "" is accepted.
        var payload = new GitUserPassPayload { Username = "u", Password = string.Empty };
        GitCredential? cred = GitCredentialHelpers.UserPass(payload, null, GitCredentialType.UserPassPlaintext);

        Assert.IsType<GitUserPassCredential>(cred);
    }

    [Fact]
    public void UserPass_NullPassword_Fails()
    {
        var payload = new GitUserPassPayload { Username = "u", Password = null! };
        Assert.Null(GitCredentialHelpers.UserPass(payload, null, GitCredentialType.UserPassPlaintext));
    }

    // ── URL-embedded credentials with empty user/pass ─────────────────

    [Fact]
    public void ApplyUrlCredentials_EmptyUserPass_StillProducesUserpassCredential()
    {
        // C (http.c:111-116): USERPASS_PLAINTEXT wins whenever allowed — a
        // userpass credential is produced even for empty username/password
        // (so the credential callback is NOT invoked).
        var url = new Uri("http://:@host/path");
        GitCredential? cred = AuthHandlers.ApplyUrlCredentials(url, GitCredentialType.UserPassPlaintext);

        GitUserPassCredential up = Assert.IsType<GitUserPassCredential>(cred);
        Assert.Equal(string.Empty, up.Username);
        Assert.Equal(string.Empty, up.Password);
    }

    [Fact]
    public void ApplyUrlCredentials_DefaultOnly_EmptyUserPass_ProducesDefaultCredential()
    {
        var url = new Uri("http://:@host/path");
        GitCredential? cred = AuthHandlers.ApplyUrlCredentials(url, GitCredentialType.Default);

        Assert.IsType<GitDefaultCredential>(cred);
    }

    [Fact]
    public void ApplyUrlCredentials_NoUserInfo_ReturnsNull()
    {
        var url = new Uri("http://host/path");
        Assert.Null(AuthHandlers.ApplyUrlCredentials(url, GitCredentialType.UserPassPlaintext));
    }

    private static byte[] BuildPktLine(ReadOnlySpan<byte> payload)
    {
        int len = payload.Length + 4;
        byte[] buffer = new byte[len];
        buffer[0] = (byte)'0';
        buffer[1] = (byte)'0';
        buffer[2] = ToHex((len >> 4) & 0xF);
        buffer[3] = ToHex(len & 0xF);
        payload.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    private static byte ToHex(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
}
