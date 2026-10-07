using System.Text;

using LibGit2CS.Remote;
using LibGit2CS.Transports;

using LibSsh2CS;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Unit tests for <see cref="SshTransport.FindHostkeyPreference"/> (parity
/// <c>find_hostkey_preference</c>) and <see cref="SshTransport.VerifyHostKeyAsync"/>
/// (parity <c>check_certificate</c>). Uses real <see cref="SshKnownHosts"/> —
/// no fake session needed; these are pure functions of known_hosts state.
/// </summary>
public sealed class SshTransportHostKeyTests
{
    // ── FindHostkeyPreference (parity find_hostkey_preference lines 523-587) ──

    [Fact]
    public void FindHostkeyPreference_EmptyKnownHosts_EmptyString()
    {
        using var knownHosts = new SshKnownHosts();
        Assert.Equal(string.Empty, SshTransport.FindHostkeyPreference(knownHosts, "example.com", 22));
    }

    [Fact]
    public void FindHostkeyPreference_Ed25519Only()
    {
        using var knownHosts = new SshKnownHosts();
        knownHosts.ReadLine("example.com ssh-ed25519 " + Base64Ed25519Dummy, SshKnownHostFileType.OpenSsh);

        string prefs = SshTransport.FindHostkeyPreference(knownHosts, "example.com", 22);

        Assert.Equal("ssh-ed25519", prefs);
    }

    [Fact]
    public void FindHostkeyPreference_SshRsa_ExpandsToThreeWireNames()
    {
        // RFC 8332 §3.1: stored RSA is always ssh-rsa, but the server may
        // negotiate a stronger SHA-2 variant — list all three so the server
        // picks the strongest it supports.
        using var knownHosts = new SshKnownHosts();
        knownHosts.ReadLine("example.com ssh-rsa " + Base64SshRsaDummy, SshKnownHostFileType.OpenSsh);

        string prefs = SshTransport.FindHostkeyPreference(knownHosts, "example.com", 22);

        Assert.Equal("rsa-sha2-512,rsa-sha2-256,ssh-rsa", prefs);
    }

    [Fact]
    public void FindHostkeyPreference_MixedOrder_IsPriorityOrder()
    {
        // Probe order: Ed25519 > Ecdsa256 > Ecdsa384 > Ecdsa521 > SshRsa.
        using var knownHosts = new SshKnownHosts();
        knownHosts.ReadLine("example.com ssh-rsa " + Base64SshRsaDummy, SshKnownHostFileType.OpenSsh);
        knownHosts.ReadLine("example.com ecdsa-sha2-nistp256 " + Base64Ecdsa256Dummy, SshKnownHostFileType.OpenSsh);
        knownHosts.ReadLine("example.com ssh-ed25519 " + Base64Ed25519Dummy, SshKnownHostFileType.OpenSsh);

        string prefs = SshTransport.FindHostkeyPreference(knownHosts, "example.com", 22);

        Assert.Equal("ssh-ed25519,ecdsa-sha2-nistp256,rsa-sha2-512,rsa-sha2-256,ssh-rsa", prefs);
    }

    [Fact]
    public void FindHostkeyPreference_PortMismatch_UsesNoPortEntry()
    {
        // known_hosts without [host]:port form matches a host:port query
        // (KnownHosts.Check falls back to plain-host lookup).
        using var knownHosts = new SshKnownHosts();
        knownHosts.ReadLine("example.com ssh-ed25519 " + Base64Ed25519Dummy, SshKnownHostFileType.OpenSsh);

        string prefs = SshTransport.FindHostkeyPreference(knownHosts, "example.com", 2222);

        Assert.Equal("ssh-ed25519", prefs);
    }

    [Fact]
    public void FindHostkeyPreference_UnknownHost_EmptyString()
    {
        using var knownHosts = new SshKnownHosts();
        knownHosts.ReadLine("other.com ssh-ed25519 " + Base64Ed25519Dummy, SshKnownHostFileType.OpenSsh);

        Assert.Equal(string.Empty, SshTransport.FindHostkeyPreference(knownHosts, "example.com", 22));
    }

    // ── VerifyHostKeyAsync (parity check_certificate lines 627-764) ─────

    [Fact]
    public async Task VerifyHostKey_Match_ReturnsTrue()
    {
        using var knownHosts = new SshKnownHosts();
        byte[] hostKey = Ed25519HostKeyBlob();
        knownHosts.ReadLine(HostEntry("example.com", "ssh-ed25519", hostKey), SshKnownHostFileType.OpenSsh);

        bool accepted = await SshTransport.VerifyHostKeyAsync(hostKey, knownHosts, "example.com", 22, options: null, TestContext.Current.CancellationToken);

        Assert.True(accepted);
    }

    [Fact]
    public async Task VerifyHostKey_Mismatch_RejectsNoCallback()
    {
        using var knownHosts = new SshKnownHosts();
        byte[] storedKey = Ed25519HostKeyBlob();
        byte[] presentedKey = DifferentEd25519HostKeyBlob();
        knownHosts.ReadLine(HostEntry("example.com", "ssh-ed25519", storedKey), SshKnownHostFileType.OpenSsh);

        bool accepted = await SshTransport.VerifyHostKeyAsync(presentedKey, knownHosts, "example.com", 22, options: null, TestContext.Current.CancellationToken);

        Assert.False(accepted);
    }

    [Fact]
    public async Task VerifyHostKey_UnknownNoCallback_Rejects()
    {
        using var knownHosts = new SshKnownHosts();
        byte[] presentedKey = Ed25519HostKeyBlob();
        // known_hosts is empty — host is unknown.

        bool accepted = await SshTransport.VerifyHostKeyAsync(presentedKey, knownHosts, "example.com", 22, options: null, TestContext.Current.CancellationToken);

        Assert.False(accepted);
    }

    [Fact]
    public async Task VerifyHostKey_UnknownCallbackPass_Accepts()
    {
        using var knownHosts = new SshKnownHosts();
        byte[] presentedKey = Ed25519HostKeyBlob();
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                CertificateCheck = info => true,
            },
        };

        bool accepted = await SshTransport.VerifyHostKeyAsync(presentedKey, knownHosts, "example.com", 22, options, TestContext.Current.CancellationToken);

        Assert.True(accepted);
    }

    [Fact]
    public async Task VerifyHostKey_UnknownCallbackReject_Rejects()
    {
        using var knownHosts = new SshKnownHosts();
        byte[] presentedKey = Ed25519HostKeyBlob();
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                CertificateCheck = info => false,
            },
        };

        bool accepted = await SshTransport.VerifyHostKeyAsync(presentedKey, knownHosts, "example.com", 22, options, TestContext.Current.CancellationToken);

        Assert.False(accepted);
    }

    [Fact]
    public async Task VerifyHostKey_Mismatch_CallbackCanAccept()
    {
        // Parity check_certificate (ssh_libssh2.c:746-756): the callback is
        // ALWAYS invoked when set — a MISMATCH passes IsValid=false, and a
        // `true` return from the callback accepts (e.g. host-key rotation).
        using var knownHosts = new SshKnownHosts();
        byte[] storedKey = Ed25519HostKeyBlob();
        byte[] presentedKey = DifferentEd25519HostKeyBlob();
        knownHosts.ReadLine(HostEntry("example.com", "ssh-ed25519", storedKey), SshKnownHostFileType.OpenSsh);
        GitCertificateInfo? captured = null;
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                CertificateCheck = info =>
                {
                    captured = info;
                    return true;
                },
            },
        };

        bool accepted = await SshTransport.VerifyHostKeyAsync(presentedKey, knownHosts, "example.com", 22, options, TestContext.Current.CancellationToken);

        Assert.True(accepted);
        Assert.NotNull(captured);
        Assert.False(captured!.IsValid, "a MISMATCH must be reported as invalid to the callback");
    }

    [Fact]
    public async Task VerifyHostKey_Match_CallbackCanReject()
    {
        // Parity check_certificate: a MATCH passes IsValid=true, and a
        // `false` return from the callback rejects.
        using var knownHosts = new SshKnownHosts();
        byte[] hostKey = Ed25519HostKeyBlob();
        knownHosts.ReadLine(HostEntry("example.com", "ssh-ed25519", hostKey), SshKnownHostFileType.OpenSsh);
        GitCertificateInfo? captured = null;
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                CertificateCheck = info =>
                {
                    captured = info;
                    return false;
                },
            },
        };

        bool accepted = await SshTransport.VerifyHostKeyAsync(hostKey, knownHosts, "example.com", 22, options, TestContext.Current.CancellationToken);

        Assert.False(accepted);
        Assert.NotNull(captured);
        Assert.True(captured!.IsValid, "a MATCH must be reported as valid to the callback");
    }

    [Fact]
    public async Task VerifyHostKey_Unknown_PopulatesCertificateInfoHashes()
    {
        using var knownHosts = new SshKnownHosts();
        byte[] presentedKey = Ed25519HostKeyBlob();
        GitCertificateInfo? captured = null;
        var options = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks
            {
                CertificateCheck = info =>
                {
                    captured = info;
                    return true;
                },
            },
        };

        await SshTransport.VerifyHostKeyAsync(presentedKey, knownHosts, "example.com", 22, options, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(GitCertificateType.HostkeySsh, captured!.Type);
        Assert.Equal("example.com", captured.Hostname);
        Assert.Equal(presentedKey, captured.HostKey);
        Assert.NotNull(captured.HostKeySha1);
        Assert.NotNull(captured.HostKeySha256);
        Assert.Equal(20, captured.HostKeySha1!.Length);
        Assert.Equal(32, captured.HostKeySha256!.Length);
        Assert.False(captured.IsValid, "Unknown hostkey — IsValid should be false");

        // Parity with git_cert.raw_type / hostkey_len / MD5 (ssh_libssh2.c:686-739).
        Assert.Equal("ssh-ed25519", captured.HostKeyType);
        Assert.Equal(presentedKey.Length, captured.HostKeyLength);
        Assert.NotNull(captured.HostKeyMd5);
        Assert.Equal(16, captured.HostKeyMd5!.Length);
    }

    // ── Test key material (deterministic dummy blobs) ───────────────────

    // A valid base64 SSH wire-format key blob: 4-byte len + "ssh-ed25519" + 32-byte key.
    // The key bytes are arbitrary (the dummy Check key is zero), so the exact
    // contents don't matter for the preference probe (which matches on host +
    // type only). For VerifyHostKeyAsync we need round-trip-able blobs.

    private const string Base64Ed25519Dummy = "AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Base64SshRsaDummy = "AAAAB3NzaC1yc2EAAAADAQABAAAAgQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";
    private const string Base64Ecdsa256Dummy = "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    /// <summary>
    /// A real ssh-ed25519 hostkey blob (32 zero bytes — fine for testing
    /// since KnownHosts.Check matches on host + type + key bytes; the bytes
    /// just need to be consistent between store and verify).
    /// </summary>
    private static byte[] Ed25519HostKeyBlob()
    {
        // Wire format: [4-byte len]["ssh-ed25519"][4-byte len][32 zero bytes]
        string name = "ssh-ed25519";
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(ToBigEndian(name.Length));
            bw.Write(Encoding.ASCII.GetBytes(name));
            bw.Write(ToBigEndian(32));
            bw.Write(new byte[32]);
        }

        return ms.ToArray();
    }

    /// <summary>A different ed25519 blob — 0xFF key bytes (not 0x00).</summary>
    private static byte[] DifferentEd25519HostKeyBlob()
    {
        string name = "ssh-ed25519";
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(ToBigEndian(name.Length));
            bw.Write(Encoding.ASCII.GetBytes(name));
            bw.Write(ToBigEndian(32));
            bw.Write(System.Linq.Enumerable.Repeat((byte)0xFF, 32).ToArray());
        }

        return ms.ToArray();
    }

    private static byte[] ToBigEndian(int value)
    {
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    /// <summary>
    /// Format a known_hosts line from raw key bytes (base64-encoded).
    /// </summary>
    private static string HostEntry(string host, string keyTypeWireName, byte[] rawKeyBlob)
        => $"{host} {keyTypeWireName} {Convert.ToBase64String(rawKeyBlob)}";
}
