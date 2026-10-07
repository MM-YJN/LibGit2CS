// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Remote;

using LibSsh2CS;

namespace LibGit2CS.Transports;

/// <summary>
/// Smart subtransport for SSH (<c>ssh://</c>, <c>ssh+git://</c>,
/// <c>git+ssh://</c>, and SCP-style <c>user@host:path</c>). Managed port of
/// <c>ssh_subtransport</c> + <c>_git_ssh_session_create</c> +
/// <c>_git_ssh_setup_conn</c> + <c>_git_ssh_authenticate_session</c> +
/// <c>find_hostkey_preference</c> + <c>check_certificate</c> in
/// <c>src/libgit2/transports/ssh_libssh2.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Stateful (<c>IsRpc=false</c>): a single SSH session + channel is opened
/// during the <c>*Ls</c> service call and reused for the subsequent
/// <c>UploadPack</c>/<c>ReceivePack</c> call. Matches the sibling
/// <see cref="GitTransport"/> structure.
/// </para>
/// <para>
/// <b>TCP ownership</b>: the transport opens the TCP socket itself (parity
/// <c>_git_ssh_setup_conn</c> lines 791-819 — libgit2 does NOT delegate TCP
/// to libssh2). The raw <see cref="Socket"/> is wrapped as a
/// <see cref="NetworkStream"/> and handed to the SSH session. The transport
/// owns the socket lifetime.
/// </para>
/// <para>
/// <b>Known-hosts</b>: <c>~/.ssh/known_hosts</c> is loaded before handshake;
/// a missing file is NOT an error (known_hosts stays empty). The hostkey
/// preference is probed and set on the session BEFORE handshake so the
/// server picks a key type present in known_hosts when possible.
/// </para>
/// <para>
/// <b>Auth retry</b>: on <c>AuthenticationFailed</c> /
/// <c>PasswordExpired</c> / <c>PublicKeyUnverified</c>, the auth-method
/// list is refreshed and the credentials callback is re-invoked. Loops
/// until success or a null credential (cancelled by user).
/// </para>
/// <para>
/// <b>Keepalive</b>: the transport owns a <see cref="PeriodicTimer"/> that
/// drives <see cref="ISshSession.SendKeepAliveAsync"/> for long-lived
/// fetch/push over idle links. Default OFF — wire parity with the reference
/// libgit2 + libssh2, which never send SSH keepalive packets; opt in via
/// <c>GitRemoteCallbacks.KeepaliveIntervalSeconds &gt; 0</c>.
/// LibSsh2CS itself stays passive (the caller owns the pump).
/// </para>
/// <para>
/// <b>Testability</b>: the transport depends on <see cref="ISshSessionFactory"/>
/// rather than constructing <see cref="SshSessionAdapter"/> directly. The
/// production factory <see cref="SshSessionFactory"/> is the default; tests
/// inject fakes that script handshake/auth/channel outcomes without
/// driving a real SSH server.
/// </para>
/// </remarks>
internal sealed class SshTransport : IGitSubtransport, IAsyncDisposable
{
    private const int DefaultSshPort = 22;

    private readonly ISshSessionFactory _sessionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly GitContext _context;

    private ISshSession? _session;
    // CA2213: _socket IS disposed in CloseAsync via the `if (_socket is { } socket)`
    // branch (Socket.Shutdown + socket.Dispose). The analyzer can't track the
    // pattern-match disposal, so suppress the field-level warning.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "_socket IS disposed in CloseAsync via the `if (_socket is { } socket)` branch (Socket.Shutdown + socket.Dispose). The analyzer can't track the pattern-match disposal.")]
    private Socket? _socket;
    private SshStream? _currentStream;
    private PeriodicTimer? _keepaliveTimer;
    private CancellationTokenSource? _keepaliveCts;
    private Task? _keepalivePumpTask;
    private bool _disposed;

    /// <summary>
    /// Creates a new SSH subtransport using the owning context and optional
    /// session factory and time provider. Production callers use the default
    /// factory; tests may inject a fake factory and time provider.
    /// </summary>
    /// <param name="context">The owning context.</param>
    /// <param name="sessionFactory">
    /// The factory, or <c>null</c> to use the default
    /// <see cref="SshSessionFactory"/>.
    /// </param>
    /// <param name="timeProvider">
    /// The time provider for the keepalive timer, or <c>null</c> to use
    /// <see cref="TimeProvider.System"/>. Tests inject
    /// <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c> to
    /// advance the clock deterministically.
    /// </param>
    internal SshTransport(GitContext context, ISshSessionFactory? sessionFactory = null, TimeProvider? timeProvider = null)
    {
        _context = context;
        _sessionFactory = sessionFactory ?? new SshSessionFactory();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return service switch
        {
            GitSmartService.UploadPackLs or GitSmartService.ReceivePackLs => OpenServiceStreamAsync(url, service, options, cancellationToken),
            GitSmartService.UploadPack or GitSmartService.ReceivePack => Task.FromResult<IGitSubtransportStream>(ReuseStream(service)),
            _ => throw new GitException(GitErrorCode.Invalid, $"unknown SSH service: {service}", GitErrorCategory.Net),
        };
    }

    /// <summary>
    /// Open a new SSH session + channel for the given service. Matches
    /// <c>_git_ssh_setup_conn</c> (<c>ssh_libssh2.c:768-908</c>).
    /// </summary>
    [SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of _git_ssh_setup_conn (ssh_libssh2.c:768-908); the method is intentionally monolithic to match the C reference.")]
    private async Task<IGitSubtransportStream> OpenServiceStreamAsync(
        string url,
        GitSmartService service,
        GitRemoteConnectOptions? options,
        CancellationToken cancellationToken)
    {
        var parsed = GitSshUrl.Parse(url, options?.ScpPortOverride);
        int port = parsed.Port ?? DefaultSshPort;

        // Close any existing stream (shouldn't have one for a fresh LS call).
        if (_currentStream is not null)
        {
            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
        }

        // Parity _git_ssh_setup_conn lines 791-819: open the TCP socket
        // ourselves. Address resolution (IPv4/IPv6/DNS) is handled by
        // Dns.GetHostAddressesAsync (parity with libgit2 deferring to
        // getaddrinfo). No System.Net.IPAddress parsing needed at the URL
        // layer; the resolved address picks InterNetwork or InterNetworkV6
        // based on what the host actually offers.
        //
        // We resolve before constructing the Socket because
        // DnsEndPoint.AddressFamily returns AddressFamily.Unspecified until
        // resolution, and `new Socket(AddressFamily.Unspecified, ...)` throws
        // "Protocol not supported" on Linux. Using IPEndPoint after resolution
        // gives us a concrete AddressFamily for the Socket constructor.
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(parsed.Host, cancellationToken).ConfigureAwait(false);

        // Parity streams/socket.c:185, 224-231: the C iterates EVERY
        // getaddrinfo result (connect_with_timeout on each ai_family until one
        // succeeds), so a host with both A and AAAA records still connects when
        // the preferred family's address is unreachable.
        if (addresses.Length == 0)
        {
            throw new GitException(GitErrorCode.Invalid,
                $"could not resolve host '{parsed.Host}'", GitErrorCategory.Net);
        }

        Socket? socket = null;
        Exception? lastConnectError = null;
        foreach (IPAddress address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            candidate.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            try
            {
                await candidate.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                socket = candidate;
                break;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                lastConnectError = ex;
                candidate.Dispose();
                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        if (socket is null)
        {
            // Every candidate failed with a caught SocketException (other
            // exceptions propagate from the loop), so lastConnectError is set.
            var socketEx = (SocketException)lastConnectError!;
            throw new GitException(GitErrorCode.Error,
                $"could not connect to '{parsed.Host}:{port}': {socketEx.Message}", GitErrorCategory.Net, socketEx);
        }

        _socket = socket;
        var networkStream = new NetworkStream(socket, ownsSocket: true);

        ISshSession session = _sessionFactory.Create();
        _session = session;

        try
        {
            // Load known_hosts and probe hostkey preference BEFORE handshake.
            using SshKnownHosts knownHosts = await LoadKnownHostsAsync(cancellationToken).ConfigureAwait(false);
            string preference = FindHostkeyPreference(knownHosts, parsed.Host, port);
            if (preference.Length > 0)
            {
                session.SetHostKeyPreference(preference);
            }

            // Handshake with hostkey verification bound through the verify
            // callback. The callback closes over knownHosts + the parsed
            // host + port + the user's CertificateCheck callback.
            try
            {
                await session.HandshakeAsync(
                    networkStream,
                    (hostKey, exchangeHash, ct) => VerifyHostKeyAsync(hostKey, knownHosts, parsed.Host, port, options, ct),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SshException ex) when (ex.ErrorCode == SshErrorCode.KeyExchangeFailure)
            {
                // Parity ssh_libssh2.c:758-761: a rejected hostkey (known_hosts
                // mismatch/unknown, or the CertificateCheck callback returning
                // false — the ONLY paths that raise KeyExchangeFailure) is
                // surfaced as GIT_ECERTIFICATE with libgit2's exact message.
                throw new GitException(GitErrorCode.Certificate,
                    "invalid or unknown remote ssh hostkey", GitErrorCategory.Ssh, ex);
            }

            // Username resolution.
            string username = parsed.User ?? await RequestUsernameAsync(url, options, cancellationToken).ConfigureAwait(false);

            // Parity ssh_libssh2.c:837-840: a URL with user:password pre-builds
            // a credential that is tried before the credentials callback.
            GitCredential? urlCredential = parsed.Password is null
                ? null
                : new GitUserPassCredential(parsed.User!, parsed.Password);

            // Auth retry loop (parity _git_ssh_authenticate_session lines 292-424).
            // Resolve the per-context agent-socket override once (mirrors
            // ResolveKnownHostsPath); null means use the $SSH_AUTH_SOCK default.
            string? agentSocketPath = ResolveAgentSocketPath();
            try
            {
                await AuthenticateAsync(session, username, options, agentSocketPath, url, urlCredential, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                urlCredential?.Dispose();
            }

            // Configure keepalive. Default OFF for wire parity: the reference
            // libgit2 ssh transport (ssh_libssh2.c) never sends SSH keepalive
            // packets — only HTTP connection keepalive exists upstream — so
            // the keepalive pump is an opt-in extension
            // (GitRemoteCallbacks.KeepaliveIntervalSeconds > 0).
            int interval = options?.Callbacks?.KeepaliveIntervalSeconds ?? 0;
            StartKeepalivePump(session, interval, cancellationToken);

            // Open the channel + create the stream (lazy exec on first I/O).
            ISshChannel channel = await session.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            _currentStream = new SshStream(channel, service, parsed.Path);
        }
        catch
        {
            // Partial-failure cleanup: tear down everything we've built so
            // far before propagating the error.
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return _currentStream;
    }

    /// <summary>
    /// Return the existing stream for the non-LS service call.
    /// Matches <c>_git_uploadpack</c> / <c>_git_receivepack</c> reuse
    /// (parity <c>GitTransport.cs:119-127</c>).
    /// </summary>
    private SshStream ReuseStream(GitSmartService service)
    {
        if (_currentStream is null)
        {
            // Parity ssh_libssh2.c:932, 959 — libgit2's exact message texts.
            string message = service == GitSmartService.UploadPack
                ? "must call UPLOADPACK_LS before UPLOADPACK"
                : "must call RECEIVEPACK_LS before RECEIVEPACK";
            throw new GitException(GitErrorCode.Invalid, message, GitErrorCategory.Net);
        }

        return _currentStream;
    }

    /// <summary>
    /// Load <c>~/.ssh/known_hosts</c>. A missing file is NOT an error —
    /// returns an empty <see cref="SshKnownHosts"/> (parity with libgit2's
    /// <c>load_known_hosts</c> lines 434-520).
    /// </summary>
    private async Task<SshKnownHosts> LoadKnownHostsAsync(CancellationToken cancellationToken)
    {
        string? path = ResolveKnownHostsPath();
        var knownHosts = new SshKnownHosts();
        if (path is null)
        {
            return knownHosts;
        }

        try
        {
            await knownHosts.ReadFileAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // Missing known_hosts is not an error — known_hosts stays empty.
        }
        catch (DirectoryNotFoundException)
        {
            // Missing ~/.ssh directory is not an error either. Windows
            // distinguishes this from FileNotFoundException; on POSIX both
            // collapse to ENOENT and surface as FileNotFoundException above.
            // Parity with libgit2's load_known_hosts which treats any open
            // failure as "no known_hosts".
        }

        return knownHosts;
    }

    /// <summary>
    /// Resolve <c>known_hosts</c> to a real path. Returns <c>null</c> if
    /// neither the override nor home-dir resolution yields a path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution order:
    /// <list type="number">
    /// <item><description>If <see cref="GitSettings.KnownHostsPathOverride"/>
    /// is a non-null delegate, it is invoked. A non-empty return value is
    /// used verbatim; a <c>null</c> or empty return falls through to step
    /// 2.</description></item>
    /// <item><description>Otherwise resolve <c>~/.ssh/known_hosts</c> from
    /// the home directory (<c>USERPROFILE</c> on Windows, <c>HOME</c> on
    /// POSIX). Returns <c>null</c> if neither is set.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The override lets a consumer wire an optional configuration path
    /// (e.g. <c>() =&gt; config["SshKnownHostsPath"]</c>): an unset config
    /// key yields <c>null</c>, which falls back to the home-dir default
    /// rather than silently disabling known-hosts verification.
    /// </para>
    /// </remarks>
    internal string? ResolveKnownHostsPath()
    {
        if (_context.Settings.KnownHostsPathOverride is { } overrideFunc)
        {
            string? explicitPath = overrideFunc();
            if (!string.IsNullOrEmpty(explicitPath))
            {
                return explicitPath;
            }
        }

        string? home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = _context.Env["HOME"];
        }

        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        return Path.Join(home, ".ssh", "known_hosts");
    }

    /// <summary>
    /// Resolve the SSH agent socket path. Returns <c>null</c> to let the
    /// agent client fall back to the <c>$SSH_AUTH_SOCK</c> environment
    /// variable (the default).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution order:
    /// <list type="number">
    /// <item><description>If <see cref="GitSettings.AgentSocketPathOverride"/>
    /// is a non-null delegate, it is invoked. A non-empty return value is
    /// used verbatim; a <c>null</c> or empty return falls through to step
    /// 2.</description></item>
    /// <item><description>Otherwise return <c>null</c>, which
    /// <c>SshSessionAdapter.AuthenticateWithAgentAsync</c> interprets as
    /// "discover <c>$SSH_AUTH_SOCK</c>".</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The override lets a consumer wire an optional configuration path
    /// (e.g. <c>() =&gt; config["SshAgentSocket"]</c>): an unset config key
    /// yields <c>null</c>, which falls back to <c>$SSH_AUTH_SOCK</c>
    /// discovery rather than silently disabling agent auth.
    /// </para>
    /// </remarks>
    internal string? ResolveAgentSocketPath()
    {
        if (_context.Settings.AgentSocketPathOverride is { } overrideFunc)
        {
            string? explicitPath = overrideFunc();
            if (!string.IsNullOrEmpty(explicitPath))
            {
                return explicitPath;
            }
        }

        // A null return tells SshSessionAdapter.AuthenticateWithAgentAsync
        // to fall back to $SSH_AUTH_SOCK auto-discovery (new SshAgent()).
        return null;
    }

    /// <summary>
    /// Probe each <see cref="SshKnownHostKeyType"/> in priority order to find
    /// the hostkey types the server is known to use. Build a comma-separated
    /// preference string in priority order and set it on the session BEFORE
    /// handshake so the server picks a key type present in known_hosts.
    /// Parity with <c>find_hostkey_preference</c>
    /// (<c>ssh_libssh2.c:523-587</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Probe order</b> (matches the C order and
    /// <c>SshTransportHostKeyTests</c>):
    /// <c>Ed25519 &gt; Ecdsa256 &gt; Ecdsa384 &gt; Ecdsa521 &gt; SshRsa</c>.
    /// </para>
    /// <para>
    /// <b>RSA expansion</b>: <c>SshRsa</c> expands to three wire names —
    /// <c>rsa-sha2-512,rsa-sha2-256,ssh-rsa</c> — because RFC 8332 §3.1
    /// mandates stored RSA keys are always <c>ssh-rsa</c> regardless of
    /// signing variant. The server's negotiated algorithm will be one of
    /// these (parity with the C switch table and
    /// <c>SshTransportHostKeyTests.FindHostkeyPreference_SshRsa_ExpandsToThreeWireNames</c>).
    /// </para>
    /// <para>
    /// A <c>Mismatch</c> result (hostname matches but key differs) still
    /// counts as "known for this key type" — the hostkey-preference probe is
    /// about algorithm selection, not key validation. The verify callback
    /// does key validation separately.
    /// </para>
    /// </remarks>
    internal static string FindHostkeyPreference(SshKnownHosts knownHosts, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(knownHosts);
        ArgumentNullException.ThrowIfNull(host);

        // The dummy key passed to Check — a 1-byte zero array. Check matches
        // on hostname + key type, not key bytes, for the purpose of
        // discovering which types the host is known for. A Mismatch result
        // (host matches, key differs) still means the host IS known for
        // that key type.
        byte[] dummyKey = [0];

        var prefs = new List<string>();
        foreach (SshKnownHostKeyType type in s_hostkeyProbeOrder)
        {
            SshKnownHostCheckResult result = knownHosts.Check(host, port, dummyKey, type);
            if (result.Status != SshKnownHostCheckStatus.NotFound)
            {
                foreach (string wireName in WireNamesFor(type))
                {
                    prefs.Add(wireName);
                }
            }
        }

        return string.Join(",", prefs);
    }

    private static readonly ImmutableArray<SshKnownHostKeyType> s_hostkeyProbeOrder =
    [
        SshKnownHostKeyType.Ed25519,
        SshKnownHostKeyType.Ecdsa256,
        SshKnownHostKeyType.Ecdsa384,
        SshKnownHostKeyType.Ecdsa521,
        SshKnownHostKeyType.SshRsa,
    ];

    /// <summary>
    /// Map a <see cref="SshKnownHostKeyType"/> to its wire name(s). SshRsa
    /// expands to three (RFC 8332 §3.1); all others map to a single wire
    /// name. Parity with the C hostkey wire-name table.
    /// </summary>
    private static string[] WireNamesFor(SshKnownHostKeyType type) => type switch
    {
        SshKnownHostKeyType.Ed25519 => ["ssh-ed25519"],
        SshKnownHostKeyType.Ecdsa256 => ["ecdsa-sha2-nistp256"],
        SshKnownHostKeyType.Ecdsa384 => ["ecdsa-sha2-nistp384"],
        SshKnownHostKeyType.Ecdsa521 => ["ecdsa-sha2-nistp521"],
        // RFC 8332 §3.1: stored RSA is always ssh-rsa, but the server may
        // negotiate a stronger SHA-2 signing variant. List all three so the
        // server picks the strongest it supports.
        SshKnownHostKeyType.SshRsa => ["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"],
        _ => [],
    };

    /// <summary>
    /// Verify a server hostkey against known_hosts and (if unknown) the
    /// user's <see cref="GitRemoteCallbacks.CertificateCheck"/> callback.
    /// Parity with <c>check_certificate</c>
    /// (<c>ssh_libssh2.c:627-764</c>).
    /// </summary>
    /// <returns><c>true</c> to accept the hostkey; <c>false</c> to reject
    /// (aborts the handshake with <c>SshErrorCode.KeyExchangeFailure</c>).</returns>
    [SuppressMessage("Security", "CA5351:Do not use insecure cryptographic algorithms", Justification = "MD5 hostkey fingerprint for git_cert parity (ssh_libssh2.c:697-706), not security")]
    internal static async Task<bool> VerifyHostKeyAsync(
        byte[] hostKey,
        SshKnownHosts knownHosts,
        string host,
        int port,
        GitRemoteConnectOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostKey);
        ArgumentNullException.ThrowIfNull(knownHosts);
        ArgumentNullException.ThrowIfNull(host);
        cancellationToken.ThrowIfCancellationRequested();

        // Derive the KnownHostKeyType from the hostkey blob's first SSH
        // string (the key-type name). The session also exposes the
        // negotiated HostKeyAlgorithm, but we're invoked BEFORE the
        // negotiation completes — we only have the blob.
        SshKnownHostKeyType keyType = DetectKnownHostKeyType(hostKey);

        SshKnownHostCheckResult result = knownHosts.Check(host, port, hostKey, keyType);

        // libgit2's check_certificate ALWAYS invokes the callback when one is
        // set (ssh_libssh2.c:746-756): the computed validity (cert_valid) is
        // passed in and the callback's return is honored in BOTH directions —
        // a `true` return accepts even a MISMATCH, a `false` return rejects
        // even a MATCH.
        bool certValid = result.Status == SshKnownHostCheckStatus.Match;

        Func<GitCertificateInfo, bool>? callback = options?.Callbacks?.CertificateCheck;
        if (callback is null)
        {
            // No callback: accept only a MATCH (the C's `if (cert_valid)`
            // gate; anything else → "invalid or unknown remote ssh hostkey").
            return certValid;
        }

        var info = new GitCertificateInfo
        {
            Type = GitCertificateType.HostkeySsh,
            IsValid = certValid,
            Hostname = host,
            HostKey = hostKey,
            HostKeyType = DetectHostKeyTypeName(hostKey),
            HostKeySha1 = SHA1Hash(hostKey),
            HostKeySha256 = SHA256.HashData(hostKey),
            HostKeyMd5 = MD5.HashData(hostKey),
            HostKeyLength = hostKey.Length,
        };

        // The CertificateCheck callback is sync per AGENTS.md (BCL TLS
        // callback has no async variant). Wrap in Task.FromResult to satisfy
        // the verify delegate's async contract.
        return await Task.FromResult(callback(info)).ConfigureAwait(false);
    }

    /// <summary>
    /// Read the first SSH string from the hostkey blob and map it to a
    /// <see cref="SshKnownHostKeyType"/>. Returns
    /// <see cref="SshKnownHostKeyType.Unknown"/> for unrecognized types.
    /// </summary>
    private static SshKnownHostKeyType DetectKnownHostKeyType(byte[] hostKey)
    {
        if (hostKey.Length < 4)
        {
            return SshKnownHostKeyType.Unknown;
        }

        // SSH string: 4-byte big-endian length, then UTF-8 bytes. The bounds
        // test subtracts 4 rather than adding it, so lengths in
        // [0x7FFFFFFD, int.MaxValue] cannot overflow and throw
        // ArgumentOutOfRangeException; they return Unknown.
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(hostKey.AsSpan(0, 4));
        if (len < 0 || len > hostKey.Length - 4)
        {
            return SshKnownHostKeyType.Unknown;
        }

        string name = Encoding.UTF8.GetString(hostKey, 4, len);
        return name switch
        {
            "ssh-ed25519" => SshKnownHostKeyType.Ed25519,
            "ecdsa-sha2-nistp256" => SshKnownHostKeyType.Ecdsa256,
            "ecdsa-sha2-nistp384" => SshKnownHostKeyType.Ecdsa384,
            "ecdsa-sha2-nistp521" => SshKnownHostKeyType.Ecdsa521,
            "ssh-rsa" => SshKnownHostKeyType.SshRsa,
            "ssh-dss" => SshKnownHostKeyType.SshDss,
            _ => SshKnownHostKeyType.Unknown,
        };
    }

    /// <summary>
    /// Reads the first SSH string (the algorithm name) from the hostkey blob,
    /// or returns <c>null</c> for a malformed blob. Feeds
    /// <see cref="GitCertificateInfo.HostKeyType"/> (parity with the C's
    /// <c>raw_type</c> derivation, ssh_libssh2.c:686-739).
    /// </summary>
    private static string? DetectHostKeyTypeName(byte[] hostKey)
    {
        if (hostKey.Length < 4)
        {
            return null;
        }

        // same overflow-safe bounds test as DetectKnownHostKeyType —
        // "4 + len" overflowed for lengths in [0x7FFFFFFD, int.MaxValue].
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(hostKey.AsSpan(0, 4));
        if (len < 0 || len > hostKey.Length - 4)
        {
            return null;
        }

        return Encoding.UTF8.GetString(hostKey, 4, len);
    }

    [SuppressMessage("Security", "CA5350:Do not use insecure cryptographic algorithms", Justification = "SHA-1 used for parity hostkey fingerprinting, not security")]
    private static byte[] SHA1Hash(byte[] data) => SHA1.HashData(data);

    /// <summary>
    /// Request the username via the credentials callback with the
    /// <see cref="GitCredentialType.Username"/> flag. Used when the URL has
    /// no <c>user@</c> prefix.
    /// </summary>
    private static async Task<string> RequestUsernameAsync(string url, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
    {
        Func<GitCredentialType, string?, string?, CancellationToken, Task<GitCredential?>>? callback = options?.Callbacks?.Credentials;
        if (callback is null)
        {
            throw new GitException(GitErrorCode.Auth, "no username in URL and no credentials callback", GitErrorCategory.Ssh);
        }

        GitCredential? cred = await callback(GitCredentialType.Username, url, null, cancellationToken).ConfigureAwait(false);
        if (cred is null || string.IsNullOrEmpty(cred.Username))
        {
            throw new GitException(GitErrorCode.Auth, "authentication cancelled by user", GitErrorCategory.Ssh);
        }

        return cred.Username!;
    }

    /// <summary>
    /// Run the auth retry loop. Parity with
    /// <c>_git_ssh_authenticate_session</c>
    /// (<c>ssh_libssh2.c:292-424</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Method discovery</b>: <see cref="ISshSession.GetAuthMethodsAsync"/>
    /// is called once initially; on retryable failure
    /// (<c>AuthenticationFailed</c>/<c>PasswordExpired</c>/
    /// <c>PublicKeyUnverified</c>), it's refreshed and the credentials
    /// callback is re-invoked.
    /// </para>
    /// <para>
    /// <b>Credential dispatch</b> (parity lines 292-424):
    /// <list type="bullet">
    /// <item><see cref="GitUserPassCredential"/> → password auth</item>
    /// <item><see cref="GitSshKeyCredential"/> → file-based publickey auth
    /// (private key read via BCL <c>File.ReadAllBytesAsync</c>, parsed by
    /// <c>SshPemParser</c>)</item>
    /// <item><see cref="GitSshKeyMemoryCredential"/> → in-memory publickey auth</item>
    /// <item><see cref="GitSshAgentCredential"/> → SSH agent auth</item>
    /// <item><see cref="GitSshInteractiveCredential"/> → keyboard-interactive</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Exit conditions</b>: success (return), null credential (throw
    /// <see cref="GitErrorCode.Auth"/>), or a non-retryable error (throw
    /// <see cref="GitErrorCode.Error"/>).
    /// </para>
    /// </remarks>
    /// <param name="agentSocketPath">
    /// Resolved agent socket path (from
    /// <see cref="GitSettings.AgentSocketPathOverride"/> on the transport's
    /// context), or <c>null</c> to use the <c>$SSH_AUTH_SOCK</c> default.
    /// Threaded through to <see cref="DispatchAuthAsync"/> for the
    /// <see cref="GitSshAgentCredential"/> branch.
    /// </param>
    /// <param name="url">
    /// The full remote URL, passed through to the credentials callback
    /// (parity: libgit2 passes <c>t->owner->url</c>, ssh_libssh2.c:393-398).
    /// </param>
    /// <param name="urlCredential">
    /// A credential pre-built from the URL's <c>user:password@</c> userinfo,
    /// tried BEFORE the credentials callback (parity ssh_libssh2.c:837-848),
    /// or <c>null</c>.
    /// </param>
    /// <param name="session">The SSH session.</param>
    /// <param name="username">The SSH username.</param>
    /// <param name="options">Options controlling this operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task AuthenticateAsync(ISshSession session, string username, GitRemoteConnectOptions? options,
        string? agentSocketPath, string? url, GitCredential? urlCredential, CancellationToken cancellationToken)
    {
        Func<GitCredentialType, string?, string?, CancellationToken, Task<GitCredential?>>? callback = options?.Callbacks?.Credentials;

        // Parity list_auth_methods (ssh_libssh2.c:1029-1057): map the server's
        // advertised methods to the credential-type mask passed to the
        // callback, so a callback returning any credential type triggers the
        // C's unsupported-type re-prompt (ssh_libssh2.c:415-419).
        string[] methods = await session.GetAuthMethodsAsync(username, cancellationToken).ConfigureAwait(false);
        GitCredentialType allowedTypes = MapAuthMethods(methods);

        GitCredential? cred = urlCredential;
        try
        {
            // Parity ssh_libssh2.c:845-848: a credential pre-built from the URL
            // is tried BEFORE the callback, gated on the server advertising
            // its type. On failure the loop below re-prompts.
            if (cred is not null && (cred.Type & allowedTypes) != 0)
            {
                try
                {
                    await DispatchAuthAsync(session, username, cred, agentSocketPath, cancellationToken).ConfigureAwait(false);
                    return; // success
                }
                catch (SshException ex) when (IsRetryable(ex.ErrorCode))
                {
                    // Fall through to the credentials-callback loop.
                }
                catch (SshException ex)
                {
                    // Non-retryable — surface with libgit2's message shape
                    // (ssh_libssh2.c:376-380: "failed to authenticate SSH
                    // session: %s").
                    throw new GitException(GitErrorCode.Error,
                        $"failed to authenticate SSH session: {ex.Message}", GitErrorCategory.Ssh, ex);
                }
            }

            while (true)
            {
                cred?.Dispose();
                cred = null;

                if (callback is null)
                {
                    throw new GitException(GitErrorCode.Auth, "no credentials callback provided", GitErrorCategory.Ssh);
                }

                cred = await callback(allowedTypes, url, username, cancellationToken).ConfigureAwait(false);
                if (cred is null)
                {
                    throw new GitException(GitErrorCode.Auth, "authentication cancelled by user", GitErrorCategory.Ssh);
                }

                // Parity ssh_libssh2.c:859-863 — the callback's credential must
                // carry the username we're authenticating as.
                if (cred.HasUsername && cred.Username != username)
                {
                    throw new GitException(GitErrorCode.Error,
                        "username does not match previous request", GitErrorCategory.Ssh);
                }

                // a
                // credential type the server did not advertise is FATAL — C's
                // request_creds returns GIT_EAUTH "authentication callback
                // returned unsupported credentials type" (ssh_libssh2.c:415-419)
                // and the caller treats every request_creds failure as fatal
                // (ssh_libssh2.c:873).
                // the callback forever when it keeps returning an
                // unadvertised type (e.g. always SSH key against a
                // password-only server).
                if ((cred.Type & allowedTypes) == 0)
                {
                    throw new GitException(GitErrorCode.Auth,
                        "authentication callback returned unsupported credentials type", GitErrorCategory.Ssh);
                }

                try
                {
                    await DispatchAuthAsync(session, username, cred, agentSocketPath, cancellationToken).ConfigureAwait(false);
                    return; // success
                }
                catch (SshException ex) when (IsRetryable(ex.ErrorCode))
                {
                    // Refresh + retry on the next loop iteration (parity:
                    // list_auth_methods is re-run after each failed attempt,
                    // ssh_libssh2.c:865-871).
                    methods = await session.GetAuthMethodsAsync(username, cancellationToken).ConfigureAwait(false);
                    allowedTypes = MapAuthMethods(methods);
                    continue;
                }
                catch (SshException ex)
                {
                    // Non-retryable — surface with libgit2's message shape
                    // (ssh_libssh2.c:376-380).
                    throw new GitException(GitErrorCode.Error,
                        $"failed to authenticate SSH session: {ex.Message}", GitErrorCategory.Ssh, ex);
                }
            }
        }
        finally
        {
            cred?.Dispose();
            urlCredential?.Dispose();
        }
    }

    /// <summary>
    /// Maps the server-advertised auth-method names to a
    /// <see cref="GitCredentialType"/> mask. Port of <c>list_auth_methods</c>
    /// (<c>ssh_libssh2.c:1029-1057</c>): <c>publickey</c> → SSH key (file +
    /// memory), <c>password</c> → userpass, <c>keyboard-interactive</c> →
    /// interactive. Method names are matched exactly (C uses sloppy prefix
    /// matching via <c>git__prefixcmp</c>), so a nonstandard server
    /// advertising e.g. <c>password-auth</c> is treated as not offering
    /// password auth.
    /// </summary>
    internal static GitCredentialType MapAuthMethods(IReadOnlyList<string> methods)
    {
        GitCredentialType mask = 0;
        foreach (string method in methods)
        {
            switch (method)
            {
                case "publickey":
                    mask |= GitCredentialType.SshKey | GitCredentialType.SshMemory;
                    break;
                case "password":
                    mask |= GitCredentialType.UserPassPlaintext;
                    break;
                case "keyboard-interactive":
                    mask |= GitCredentialType.SshInteractive;
                    break;
            }
        }

        return mask;
    }

    /// <summary>
    /// Dispatch a credential to the matching auth method. Parity with the
    /// per-credential-type branches of
    /// <c>_git_ssh_authenticate_session</c>.
    /// </summary>
    /// <param name="agentSocketPath">
    /// Resolved agent socket path (from
    /// <see cref="GitSettings.AgentSocketPathOverride"/>), or <c>null</c>
    /// to use the <c>$SSH_AUTH_SOCK</c> default. Only consulted for the
    /// <see cref="GitSshAgentCredential"/> branch.
    /// </param>
    /// <param name="session">The SSH session.</param>
    /// <param name="username">The SSH username.</param>
    /// <param name="cred">The credentials to use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task DispatchAuthAsync(ISshSession session, string username, GitCredential cred, string? agentSocketPath, CancellationToken cancellationToken)
    {
        switch (cred)
        {
            case GitUserPassCredential password:
                await session.AuthenticateWithPasswordAsync(username, password.Password, cancellationToken).ConfigureAwait(false);
                return;

            case GitSshKeyCredential keyFile:
                {
                    byte[] privateKeyData;
                    try
                    {
                        privateKeyData = await File.ReadAllBytesAsync(keyFile.PrivateKeyPath, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
                    {
                        // Parity: libgit2's key-file failures surface as SSH
                        // errors ("failed to authenticate SSH session: …"),
                        // not raw filesystem exceptions — a missing or
                        // unreadable private-key file surfaces here as a
                        // GitException.
                        throw new GitException(GitErrorCode.Error,
                            $"Failed to read private key file '{keyFile.PrivateKeyPath}': {ex.Message}",
                            GitErrorCategory.Ssh, ex);
                    }

                    try
                    {
                        await session.AuthenticateWithPublicKeyAsync(
                            username,
                            publicKeyBlob: keyFile.PublicKeyPath is null ? null : await ReadPublicKeyFileAsync(keyFile, cancellationToken).ConfigureAwait(false),
                            privateKeyData,
                            keyFile.Passphrase,
                            cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(privateKeyData);
                    }

                    return;
                }

            case GitSshKeyMemoryCredential keyMem:
                await session.AuthenticateWithPublicKeyAsync(
                    username,
                    keyMem.PublicKey,
                    keyMem.PrivateKey ?? throw new GitException(GitErrorCode.Invalid, "private key disposed", GitErrorCategory.Ssh),
                    keyMem.Passphrase,
                    cancellationToken).ConfigureAwait(false);
                return;

            case GitSshAgentCredential:
                await session.AuthenticateWithAgentAsync(username, agentSocketPath, cancellationToken).ConfigureAwait(false);
                return;

            case GitSshInteractiveCredential interactive:
                await session.AuthenticateWithKeyboardInteractiveAsync(username, interactive.Callback, cancellationToken).ConfigureAwait(false);
                return;

            default:
                throw new GitException(GitErrorCode.Auth, $"unsupported SSH credential type: {cred.GetType().Name}", GitErrorCategory.Ssh);
        }
    }

    /// <summary>
    /// Reads the public-key file for a <see cref="GitSshKeyCredential"/>,
    /// wrapping filesystem failures as SSH errors (see the private-key read
    /// above).
    /// </summary>
    private static async Task<byte[]> ReadPublicKeyFileAsync(GitSshKeyCredential keyFile, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(keyFile.PublicKeyPath!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            throw new GitException(GitErrorCode.Error,
                $"Failed to read public key file '{keyFile.PublicKeyPath}': {ex.Message}",
                GitErrorCategory.Ssh, ex);
        }
    }

    /// <summary>
    /// Map a <see cref="SshErrorCode"/> to whether it should trigger a
    /// credential retry. Parity with <c>_git_ssh_authenticate_session</c>'s
    /// EAUTH mapping (lines 292-424).
    /// </summary>
    internal static bool IsRetryable(SshErrorCode errorCode) => errorCode switch
    {
        SshErrorCode.AuthenticationFailed => true,
        SshErrorCode.PasswordExpired => true,
        SshErrorCode.PublicKeyUnverified => true,
        _ => false,
    };

    /// <summary>
    /// Configure the keepalive timer and start the pump. Fire-and-forget;
    /// the pump observes <paramref name="cancellationToken"/> (via a linked
    /// CTS) and exits cleanly on close or cancellation.
    /// </summary>
    /// <remarks>
    /// Internal so tests can drive the pump with a
    /// <c>FakeTimeProvider</c>-backed timer + a fake session without
    /// running a full <see cref="OpenServiceStreamAsync"/>.
    /// </remarks>
    internal void StartKeepalivePump(ISshSession session, int intervalSeconds, CancellationToken cancellationToken)
    {
        if (intervalSeconds <= 0)
        {
            return;
        }

        session.ConfigureKeepAlive(wantReply: true, intervalSeconds);
        _session = session;
        _keepaliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds), _timeProvider);
        _keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _keepalivePumpTask = PumpKeepaliveAsync(_keepaliveCts.Token);
    }

    /// <summary>
    /// Pump keepalive requests on the configured interval. Fire-and-forget
    /// from <see cref="OpenServiceStreamAsync"/>; observes cancellation and
    /// exits cleanly on close.
    /// </summary>
    private async Task PumpKeepaliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _keepaliveTimer!.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_session is null)
                {
                    return;
                }

                try
                {
                    await _session.SendKeepAliveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A failed keepalive send shouldn't crash the pump; the
                    // next regular I/O will surface the real error.
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc/>
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        // Stop the keepalive pump first. CancelAsync is the async-friendly
        // variant (CA1849). After cancellation, AWAIT the pump task (with a
        // bounded timeout) so the pump has fully exited before we dispose the
        // session it was sending keepalives on. Fire-and-forget would leave a
        // window where the pump could call SendKeepAliveAsync on a session
        // whose writer was being disposed concurrently — delaying the clean
        // TCP teardown the next OpenServiceStreamAsync depends on. The pump observes cancellation
        // (and ObjectDisposedException on the timer) and exits on its own;
        // the await just makes that observable before we proceed.
        CancellationTokenSource? keepaliveCts = _keepaliveCts;
        _keepaliveCts = null;
        Task? pumpTask = _keepalivePumpTask;
        _keepalivePumpTask = null;
        if (keepaliveCts is not null)
        {
            await keepaliveCts.CancelAsync().ConfigureAwait(false);
            keepaliveCts.Dispose();
        }

        if (pumpTask is not null)
        {
            // Bounded wait: a stuck pump must not hang CloseAsync. 2 seconds
            // is generous — the pump only blocks on
            // PeriodicTimer.WaitForNextTickAsync (which honors CTS disposal)
            // and SendKeepAliveAsync (a single packet write). CancellationToken.None
            // is intentional: CloseAsync's own token cancels the session
            // dispose, NOT the pump wait — the pump observes the keepalive
            // CTS, not this token.
            var pumpWaitTimeout = TimeSpan.FromSeconds(2);
            Task completed = await Task.WhenAny(pumpTask, Task.Delay(pumpWaitTimeout, CancellationToken.None)).ConfigureAwait(false);
            if (completed != pumpTask)
            {
                // Pump didn't exit in time — best-effort: observe any
                // exception it later throws to avoid UnobservedTaskException.
                _ = pumpTask.ContinueWith(
                    t => { _ = t.Exception; },
                    TaskScheduler.Default);
            }
            else
            {
                // Pump exited — observe its exception (if any) so it isn't
                // classified as unobserved. The pump itself swallows
                // OCE/ObjectDisposed, so this is defensive.
                if (pumpTask.IsFaulted)
                {
                    _ = pumpTask.Exception;
                }
            }
        }

        _keepaliveTimer?.Dispose();
        _keepaliveTimer = null;

        // Dispose the stream (which disposes the channel).
        if (_currentStream is not null)
        {
            try
            {
                await _currentStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Channel close errors are non-fatal during shutdown.
            }

            _currentStream = null;
        }

        // Disconnect + dispose the session.
        if (_session is not null)
        {
            try
            {
                await _session.DisconnectAsync(SshDisconnectReason.ByApplication, "closing transport", "en", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort disconnect.
            }

            try
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort dispose.
            }

            _session = null;
        }

        // Close the TCP socket cleanly before disposing. Socket.Shutdown(Both)
        // triggers the kernel's FIN/ACK exchange synchronously, so the
        // server's sshd child process is fully reaped before a subsequent
        // OpenServiceStreamAsync on a DIFFERENT SshTransport opens a new
        // connection to the same port. Without this, Socket.Dispose() alone
        // releases the FD but defers the TCP FIN to the kernel's discretion,
        // which can race the next connect's SYN and produce an empty ref
        // advertisement (the server accepts the new TCP connection while the
        // previous child is still tearing down). Shutdown may throw if the
        // peer has already closed (connection reset) — swallow that, the
        // socket is being disposed regardless. (Socket.Shutdown is a sync
        // VFS metadata op per AGENTS.md — no threadpool blocking.)
        if (_socket is { } socket)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Peer already closed / connection reset — best-effort.
            }
            catch (ObjectDisposedException)
            {
                // Already disposed — nothing to do.
            }

            socket.Dispose();
        }

        _socket = null;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
    }
}
