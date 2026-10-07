// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Remote;

/// <summary>
/// A parsed SSH URL — the user/host/port/path tuple that <c>SshTransport</c>
/// consumes. Managed port of the SSH-specific slice of libgit2's
/// <c>git_net_url_parse</c> + <c>git_net_url_parse_scp</c>
/// (<c>src/util/net.c:464-804</c>) as invoked by
/// <c>_git_ssh_setup_conn</c> (<c>transports/ssh_libssh2.c:768-808</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Supported forms</b> (parity <c>ssh_libssh2.c:791-794</c>):
/// <list type="bullet">
/// <item><c>ssh://[user@]host[:port]/path</c> — scheme-style.</item>
/// <item><c>ssh+git://...</c> / <c>git+ssh://...</c> — scheme aliases, treated
/// identically to <c>ssh://</c>.</item>
/// <item><c>[user@]host:path</c> — SCP-style (no scheme). Routed here by
/// <see cref="GitTransportRegistry"/>'s SCP-detection branch.</item>
/// </list>
/// </para>
/// <para>
/// <b>IPv6 literals</b> are detected by a verbatim port of libgit2's
/// <c>is_ipv6</c> (<c>net.c:621-644</c>) and the <c>IPV6</c> state of
/// <c>url_parse_authority</c> (<c>net.c:250-264</c>): a
/// <c>[0-9a-fA-F:]</c> character-class filter, not full RFC 4291
/// structural validation. Address resolution is deferred to the transport's
/// <c>Socket.ConnectAsync</c> (parity with libgit2 deferring to
/// <c>getaddrinfo</c>). <see cref="Host"/> preserves the inner content of the
/// brackets verbatim — no canonicalization (e.g. <c>0:0:0:0:0:0:0:1</c>
/// stays <c>0:0:0:0:0:0:0:1</c>, not <c>::1</c>) — so known_hosts lookups and
/// error messages match libgit2 byte-for-byte.
/// </para>
/// <para>
/// <b>Rejections</b> throw <see cref="GitException"/> with
/// <see cref="GitErrorCode.Invalid"/> and <see cref="GitErrorCategory.Net"/>
/// (parity <c>url_invalid</c>/<c>scp_invalid</c> in <c>net.c:615-619</c>):
/// <list type="bullet">
/// <item>Empty path → "malformed git protocol URL" (parity <c>gen_proto</c>
/// <c>ssh_libssh2.c:74-77</c>).</item>
/// <item>Path/username/host starting with <c>-</c> → cmdline-option
/// injection guard (parity <c>git_process__is_cmdline_option</c>,
/// <c>process.h:116-119</c>; the C transport checks the path only, the
/// user/host checks are defense-in-depth from <c>ssh_exec.c:142-148</c>).</item>
/// <item>Missing host, malformed IPv6 literal, invalid port —
/// libgit2-parity messages.</item>
/// </list>
/// </para>
/// <para>
/// <b>Path handling.</b> The <c>/~</c>-prefix (e.g. <c>ssh://host/~user/repo</c>)
/// is preserved verbatim — the leading <c>/</c> is stripped by the transport
/// when building the <c>git-upload-pack '&lt;path&gt;'</c> exec command (parity
/// <c>gen_proto</c> <c>ssh_libssh2.c:71-72</c>).
/// </para>
/// <para>
/// <b>LibGit2CS divergence — SCP-style port override.</b> libgit2's
/// SCP-style grammar (<c>net.c:661-804</c>) has no port syntax: the
/// <c>PORT_START</c>/<c>PORT</c>/<c>PORT_END</c> states are reachable only
/// via the bracket-counting branch, which no normal SCP-style URL triggers,
/// so <c>Port</c> is always <c>null</c> for SCP-style parses. LibGit2CS
/// extends the parser with an optional <c>scpPortOverride</c> parameter on
/// <see cref="Parse(string, int?)"/>: when supplied, the SCP-style parse
/// returns that port instead of <c>null</c>. The override is a programmer
/// input (not URL-derived), so range violations throw
/// <see cref="ArgumentOutOfRangeException"/> (not <see cref="GitException"/>).
/// Passing an override for a scheme-style URL throws
/// <see cref="ArgumentException"/> — scheme-style URLs already encode their
/// port, so a separate override is a misuse. The single-arg
/// <see cref="Parse(string)"/> overload remains 100% libgit2-parity: no
/// override, no behavior change.
/// </para>
/// </remarks>
public sealed record GitSshUrl
{
    /// <summary>
    /// The username, or <c>null</c> if the URL has no <c>user@</c> part. The
    /// transport requests a username via the credentials callback when this is
    /// null (parity <c>ssh_libssh2.c:828-836</c>).
    /// </summary>
    public string? User { get; init; }

    /// <summary>
    /// The password portion of <c>user:password@</c> in a scheme-style URL, or
    /// <c>null</c> when the URL carries no password. Split at the LAST ':' of
    /// the userinfo (parity with net.c:265-277, whose backward walk takes the
    /// first ':' seen from the right — so <c>ssh://user:pa:ss@host</c> yields
    /// user <c>"user:pa"</c>, password <c>"ss"</c>). The whole <c>user:pass</c>
    /// userinfo is never kept in <see cref="User"/>, so <c>ssh://user:pass@…</c>
    /// URLs authenticate with the password portion rather than sending it
    /// verbatim as the username.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// The host, never null. For IPv6 literals the brackets are stripped and
    /// the inner content is preserved verbatim (no canonicalization).
    /// </summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// The port, or <c>null</c> when the URL has no explicit port (default 22).
    /// Parity with <c>git_net_url.port_specified</c> — distinguishes "port 22
    /// by default" from "port 22 explicit". The transport resolves
    /// <c>int port = url.Port ?? 22</c> before <c>KnownHosts.Check</c> and
    /// <c>Socket.ConnectAsync</c>.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>
    /// The path, never null and never empty. For scheme-style URLs the leading
    /// <c>/</c> is preserved; for SCP-style URLs there is no leading <c>/</c>.
    /// <c>/~</c>-prefixed paths are returned as-is (the transport strips the
    /// leading <c>/</c> when building the exec command).
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// <c>true</c> for SCP-style URLs (<c>user@host:path</c>), <c>false</c>
    /// for scheme-style (<c>ssh://...</c>). SCP-style has no port syntax.
    /// </summary>
    public bool IsScpStyle { get; init; }

    /// <summary>
    /// Parse an SSH URL. Accepts <c>ssh://</c>, <c>ssh+git://</c>,
    /// <c>git+ssh://</c>, and SCP-style <c>[user@]host:path</c> forms.
    /// Throws <see cref="GitException"/> on malformed input.
    /// </summary>
    /// <param name="url">The URL to parse.</param>
    /// <returns>The parsed <see cref="GitSshUrl"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is null.</exception>
    /// <exception cref="GitException">Malformed URL (see class remarks for the
    /// full rejection list).</exception>
    public static GitSshUrl Parse(string url)
        => Parse(url, scpPortOverride: null);

    /// <summary>
    /// Parse an SSH URL with an optional SCP-style port override. Behaves
    /// identically to <see cref="Parse(string)"/> when
    /// <paramref name="scpPortOverride"/> is <c>null</c> (libgit2 parity:
    /// SCP-style URLs produce <c>Port == null</c>). When the override is
    /// non-null, SCP-style (<c>[user@]host:path</c>) parses set
    /// <see cref="Port"/> to the override value instead of <c>null</c>.
    /// </summary>
    /// <param name="url">The URL to parse.</param>
    /// <param name="scpPortOverride">Optional port for SCP-style URLs. See
    /// the class remarks "LibGit2CS divergence" section for semantics and
    /// rationale.</param>
    /// <returns>The parsed <see cref="GitSshUrl"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scpPortOverride"/>
    /// is non-null and outside 1..65535.</exception>
    /// <exception cref="ArgumentException"><paramref name="scpPortOverride"/>
    /// is non-null and <paramref name="url"/> is scheme-style (scheme-style
    /// URLs already encode their port — a separate override is a misuse).</exception>
    /// <exception cref="GitException">Malformed URL (see class remarks for the
    /// full rejection list).</exception>
    public static GitSshUrl Parse(string url, int? scpPortOverride)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (scpPortOverride is { } p && (p < 1 || p > 65535))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scpPortOverride),
                scpPortOverride,
                "scpPortOverride must be in the range 1..65535.");
        }

        return IsUrl(url)
            ? ParseSchemeStyle(url, scpPortOverride)
            : ParseScpStyle(url, scpPortOverride);
    }

    // ── Scheme-style: ssh://[user@]host[:port]/path ────────────────────────

    private static GitSshUrl ParseSchemeStyle(string url, int? scpPortOverride)
    {
        if (scpPortOverride is not null)
        {
            // Scheme-style URLs encode their own port — a separate override
            // is a programmer error. Strict-by-design: catches callers that
            // blanket-set the override across a mix of URL schemes.
            throw new ArgumentException(
                "scpPortOverride cannot be set for scheme-style SSH URLs " +
                "(ssh://host[:port]/path) — the URL already encodes its port.",
                nameof(scpPortOverride));
        }
        // Strip the scheme prefix (ssh://, ssh+git://, git+ssh://). All three
        // are treated identically (parity ssh_libssh2.c:791-792 +
        // GitTransportRegistry's three scheme entries).
        string rest = StripScheme(url);

        // url_parse_authority (net.c:182-321) walks the authority backwards
        // from the first '/' (the path separator). Everything before the '/'
        // is the authority [user[:pass]@]host[:port]; everything from the '/'
        // onward is the path.
        int slashIdx = rest.IndexOf('/', StringComparison.Ordinal);
        if (slashIdx < 0)
        {
            // No path → malformed (parity gen_proto ssh_libssh2.c:74-77,
            // which rejects empty path). libgit2's url_parse_path would set
            // path=null here; the SSH transport rejects that as malformed.
            throw new GitException(GitErrorCode.Invalid, "malformed git protocol URL", GitErrorCategory.Net);
        }

        string authority = rest[..slashIdx];
        string path = rest[slashIdx..];

        // url_parse_authority walks backwards; we walk forwards here because
        // the C backward-walk exists only to disambiguate non-RFC-compliant
        // URLs with '@' in the username (e.g. "user@name@host:port/path" from
        // Google Code). The forward walk with rightmost-@-split is equivalent
        // for well-formed URLs and easier to follow; the unit-test coverage
        // confirms identical outputs.
        string? user = null;
        string? password = null;
        string hostPort = authority;

        int atIdx = authority.LastIndexOf('@', StringComparison.Ordinal);
        if (atIdx >= 0)
        {
            string userinfo = authority[..atIdx];
            hostPort = authority[(atIdx + 1)..];

            // Split user:password at the LAST ':' (parity net.c:265-277 —
            // see Password's doc).
            int colonIdx = userinfo.LastIndexOf(':', StringComparison.Ordinal);
            if (colonIdx >= 0)
            {
                password = userinfo[(colonIdx + 1)..];
                user = userinfo[..colonIdx];
            }
            else
            {
                user = userinfo;
            }
        }

        // Split host:port. IPv6 literals are bracketed: [::1]:port or [::1].
        // The bracketed span is the host; anything after ']' must be ':port'.
        string host;
        int? port = null;

        if (hostPort.Length > 0 && hostPort[0] == '[')
        {
            int closeIdx = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (closeIdx < 0)
            {
                throw new GitException(GitErrorCode.Invalid, "malformed IPv6 literal in ssh URL", GitErrorCategory.Net);
            }

            // Inner content of the brackets is the host, verbatim (no
            // normalization). Validate the character class (parity is_ipv6
            // net.c:621-644 + url_parse_authority IPV6 state net.c:250-264).
            string inner = hostPort[1..closeIdx];
            if (!IsValidIpv6Inner(inner))
            {
                throw new GitException(GitErrorCode.Invalid, "malformed IPv6 literal in ssh URL", GitErrorCategory.Net);
            }

            host = inner;

            // After ']' we expect either end-of-string or ':port'.
            if (closeIdx + 1 < hostPort.Length)
            {
                if (hostPort[closeIdx + 1] != ':')
                {
                    throw new GitException(GitErrorCode.Invalid, "malformed hostname", GitErrorCategory.Net);
                }

                string portStr = hostPort[(closeIdx + 2)..];
                port = ParsePort(portStr);
            }
        }
        else
        {
            // Non-bracketed host. A bare ':' separates host from port — but
            // only the LAST ':' (in case the host is a malformed IPv6 literal,
            // which we reject). For well-formed non-IPv6 hosts there's at
            // most one ':' (the port separator).
            int colonIdx = hostPort.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx >= 0)
            {
                host = hostPort[..colonIdx];
                string portStr = hostPort[(colonIdx + 1)..];
                port = ParsePort(portStr);
            }
            else
            {
                host = hostPort;
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            throw new GitException(GitErrorCode.Invalid, "missing host in ssh URL", GitErrorCategory.Net);
        }

        ValidateNoCmdlineOption(user, host, path);

        if (string.IsNullOrEmpty(path) || path == "/")
        {
            // libgit2's url_parse_path accepts "/" (sets path="/"), but
            // gen_proto rejects empty/whitespace paths at exec time. Treat
            // the "/"-only case as malformed here too, since git-upload-pack
            // '/' is never a valid repository path. This is a small
            // defense-in-depth beyond the C code, but it matches what the
            // transport would observe anyway.
            throw new GitException(GitErrorCode.Invalid, "malformed git protocol URL", GitErrorCategory.Net);
        }

        return new GitSshUrl
        {
            User = user,
            Password = password,
            Host = host,
            Port = port,
            Path = path,
            IsScpStyle = false,
        };
    }

    // ── SCP-style: [user@]host:path (no scheme, no port) ───────────────────

    private static GitSshUrl ParseScpStyle(string url, int? scpPortOverride)
    {
        // git_net_url_parse_scp (net.c:661-804). The state machine walks
        // forward tracking: NONE → USER → HOST_START → HOST → PATH_START.
        // IPv6 literals are bracketed: [::1]:path. There is NO port syntax
        // in SCP-style (parity net.c:722 — ':' after host always starts the
        // path, never a port). The LibGit2CS scpPortOverride parameter is a
        // programmer-supplied out-of-band port (see class remarks); it does
        // not change the URL grammar, only the returned Port value.
        string? user = null;
        string host;
        string path;

        if (url.Length == 0)
        {
            throw new GitException(GitErrorCode.Invalid, "empty ssh URL", GitErrorCategory.Net);
        }

        // NONE state: look for the first meaningful character.
        //   '@' or ':' at position 0 → scp_invalid (net.c:684-686).
        //   '[' → IPv6 literal OR bracket-counted hostname.
        //   otherwise → if the rest has '@', enter USER; else HOST.
        if (url[0] is '@' or ':')
        {
            throw new GitException(GitErrorCode.Invalid, $"unexpected '{url[0]}' in scp-style URL", GitErrorCategory.Net);
        }

        if (url[0] == '[')
        {
            // IPv6 literal — find the closing ']' (parity IPV6 state net.c:738-741).
            int closeIdx = url.IndexOf(']', StringComparison.Ordinal);
            if (closeIdx < 0)
            {
                throw new GitException(GitErrorCode.Invalid, "malformed IPv6 literal in ssh URL", GitErrorCategory.Net);
            }

            string inner = url[1..closeIdx];
            if (!IsValidIpv6Inner(inner))
            {
                throw new GitException(GitErrorCode.Invalid, "malformed IPv6 literal in ssh URL", GitErrorCategory.Net);
            }

            host = inner;

            // After ']' there must be ':path' (parity HOST_END state net.c:732-736).
            if (closeIdx + 1 >= url.Length || url[closeIdx + 1] != ':')
            {
                throw new GitException(GitErrorCode.Invalid, "unexpected character after ipv6 address", GitErrorCategory.Net);
            }

            path = url[(closeIdx + 2)..];
        }
        else
        {
            // USER or HOST. has_at (net.c:646-659): does the string up to
            // the first ':' contain an '@'? If so, enter USER — C's USER
            // state breaks at the FIRST '@' (net.c:687-693), so the user is
            // the prefix before the first '@' and any further '@' remains
            // part of the host (HOST ignores '@').
            int firstColon = url.IndexOf(':', StringComparison.Ordinal);
            if (firstColon < 0)
            {
                // No ':' at all → no path → scp_invalid (net.c:782-783).
                throw new GitException(GitErrorCode.Invalid, "path is required", GitErrorCategory.Net);
            }

            int atIdx = url.IndexOf('@', StringComparison.Ordinal);
            if (atIdx > 0 && atIdx < firstColon)
            {
                user = url[..atIdx];
            }
            else if (atIdx == 0)
            {
                // Leading '@' — already rejected above, defensive.
                throw new GitException(GitErrorCode.Invalid, "unexpected '@' in scp-style URL", GitErrorCategory.Net);
            }

            // Host starts right after the user (or at 0).
            int hostStart = (atIdx > 0 && atIdx < firstColon) ? atIdx + 1 : 0;

            // HOST_START (net.c:696-699): a '[' right after the user opens a
            // bracketed IPv6 literal (IPV6 state); anything else enters HOST.
            if (url[hostStart] == '[')
            {
                // C
                // enters IPV6 from HOST_START, so 'user@[::1]:path' parses
                // host '::1' and path 'path' (host brackets stripped, per
                // this class's convention).
                // FIRST ':' — inside the literal — yielding host '[' and
                // path '1]:path'.
                int closeIdx = url.IndexOf(']', hostStart + 1, StringComparison.Ordinal);
                if (closeIdx < 0)
                {
                    throw new GitException(GitErrorCode.Invalid, "malformed IPv6 literal in ssh URL", GitErrorCategory.Net);
                }

                string inner = url[(hostStart + 1)..closeIdx];
                if (!IsValidIpv6Inner(inner))
                {
                    throw new GitException(GitErrorCode.Invalid, "malformed IPv6 literal in ssh URL", GitErrorCategory.Net);
                }

                host = inner;

                // IPV6_END (net.c:742-749): after ']' must come ':' (the
                // path separator).
                if (closeIdx + 1 >= url.Length || url[closeIdx + 1] != ':')
                {
                    throw new GitException(GitErrorCode.Invalid, "unexpected character after ipv6 address", GitErrorCategory.Net);
                }

                path = url[(closeIdx + 2)..];
            }
            else
            {
                // HOST state (net.c:700-727): scan to the first ':' — the
                // path separator (SCP-style has no port syntax). A stray ']'
                // is rejected (net.c:714-717 "unexpected ']'").
                int colon = url.IndexOf(':', hostStart, StringComparison.Ordinal);
                if (colon < 0)
                {
                    throw new GitException(GitErrorCode.Invalid, "path is required", GitErrorCategory.Net);
                }

                string hostPortion = url[hostStart..colon];
                if (hostPortion.Contains(']', StringComparison.Ordinal))
                {
                    throw new GitException(GitErrorCode.Invalid, "unexpected ']' in scp-style URL", GitErrorCategory.Net);
                }

                host = hostPortion;

                // Path is everything after the ':'.
                path = url[(colon + 1)..];
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            throw new GitException(GitErrorCode.Invalid, "missing host in scp-style URL", GitErrorCategory.Net);
        }

        if (string.IsNullOrEmpty(path))
        {
            throw new GitException(GitErrorCode.Invalid, "path is required", GitErrorCategory.Net);
        }

        ValidateNoCmdlineOption(user, host, path);

        // SCP-style has no port syntax in libgit2 (parity net.c:793-798 →
        // Port = null). The LibGit2CS scpPortOverride is the single
        // out-of-band way to attach a port to an SCP-style parse.
        return new GitSshUrl
        {
            User = user,
            Host = host,
            Port = scpPortOverride,
            Path = path,
            IsScpStyle = true,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsUrl(string url)
    {
        // git_net_str_is_url (net.c:95) — true if the string contains "://".
        // SSH scheme aliases (ssh://, ssh+git://, git+ssh://) all match.
        return url.Contains("://", StringComparison.Ordinal);
    }

    private static string StripScheme(string url)
    {
        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        return url[(schemeEnd + 3)..];
    }

    // Parity is_ipv6 (net.c:621-644) inner-content filter: [0-9a-fA-F:] only.
    // No structural validation (no RFC 4291 grammar check) — matches libgit2
    // deferring address resolution to getaddrinfo. Returns false for empty
    // (an empty bracket pair "[]" is not a valid IPv6 literal in libgit2
    // because is_ipv6 requires colons > 1).
    //
    // KNOWN LIMITATION (parity-faithful): zone-ids (e.g. [fe80::1%eth0]) are
    // REJECTED — libgit2's is_ipv6 has no special handling for '%', so the
    // non-hex chars in the zone-id (e.g. 't', 'h' in "eth0") fail the filter.
    // Link-local IPv6 SSH URLs are unsupported, matching libgit2. To lift
    // this, a future divergence would add '%' acceptance here (and in
    // url_parse_authority's IPV6 state, net.c:250-264).
    private static bool IsValidIpv6Inner(string inner)
    {
        if (inner.Length == 0)
        {
            return false;
        }

        int colons = 0;
        foreach (char c in inner)
        {
            if (c == ':')
            {
                colons++;
                continue;
            }

            if (c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))
            {
                continue;
            }

            return false;
        }

        return colons > 1;
    }

    private static int ParsePort(string portStr)
    {
        // C's
        // url_invalid uses fixed strings and never echoes the raw URL
        // (net.c:176-179), so user:password from
        // ssh://user:secret@host:badport/path cannot leak into exception
        // messages, logs, or error-reporting pipelines.
        if (portStr.Length == 0)
        {
            throw new GitException(GitErrorCode.Invalid, "invalid port in ssh URL", GitErrorCategory.Net);
        }

        if (!int.TryParse(portStr, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port))
        {
            throw new GitException(GitErrorCode.Invalid, "invalid port in ssh URL", GitErrorCategory.Net);
        }

        if (port is < 1 or > 65535)
        {
            throw new GitException(GitErrorCode.Invalid, "invalid port in ssh URL", GitErrorCategory.Net);
        }

        return port;
    }

    // Cmdline-option injection guard. The C transport checks only the path
    // (git_process__is_cmdline_option at ssh_libssh2.c:801); the user/host
    // checks are defense-in-depth from ssh_exec.c:142-148 (the exec
    // transport this port drops, but the guard is still meaningful since
    // these strings reach the SSH wire and server-side
    // AuthorizedKeysCommand plumbing).
    private static void ValidateNoCmdlineOption(string? user, string host, string path)
    {
        if (user is { Length: > 0 } && user[0] == '-')
        {
            throw new GitException(GitErrorCode.Invalid, "username begins with '-'", GitErrorCategory.Net);
        }

        if (host.Length > 0 && host[0] == '-')
        {
            throw new GitException(GitErrorCode.Invalid, "host begins with '-'", GitErrorCategory.Net);
        }

        if (path.Length > 0 && path[0] == '-')
        {
            throw new GitException(GitErrorCode.Invalid, "path begins with '-'", GitErrorCategory.Net);
        }
    }
}
