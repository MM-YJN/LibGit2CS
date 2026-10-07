// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Net;
using System.Net.Security;
using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.Remote;

/// <summary>
/// State machine for a single HTTP authentication scheme. Drives the
/// challenge-response loop for Basic, Negotiate, and NTLM.
/// </summary>
/// <remarks>
/// Ported from <c>git_http_auth_context</c> in
/// <c>src/libgit2/transports/auth.h</c>. The C implementation has separate
/// vtable implementations for each scheme (<c>auth.c</c> for Basic,
/// <c>auth_gssapi.c</c> for Negotiate, <c>auth_ntlmclient.c</c>/<c>auth_sspi.c</c>
/// for NTLM). In the managed port, all three collapse into this single class,
/// with <see cref="NegotiateAuthentication"/> handling both Negotiate and NTLM.
/// </remarks>
internal sealed class AuthContext : IDisposable
{
    private readonly GitAuthSchemeType _scheme;
    private readonly GitCredential _credential;
    private readonly string _host;
    private readonly bool _isProxy;
    private readonly NegotiateAuthentication? _negotiate;
    private bool _complete;
    private bool _disposed;

    /// <summary>
    /// Whether this scheme has connection affinity (NTLM/Negotiate require
    /// the same TCP connection for all challenge-response round-trips).
    /// Maps to <c>git_http_auth_context.connection_affinity</c>.
    /// </summary>
    public bool ConnectionAffinity => _scheme is GitAuthSchemeType.Ntlm or GitAuthSchemeType.Negotiate;

    /// <summary>The auth scheme type.</summary>
    public GitAuthSchemeType Scheme => _scheme;

    /// <summary>Whether the challenge-response is complete.</summary>
    public bool IsComplete => _complete;

    /// <summary>
    /// The credential the context was created with. The HTTP transport
    /// compares this against the currently resolved credential and recreates
    /// the context when it differs, so a stale credential is never replayed.
    /// </summary>
    public GitCredential Credential => _credential;

    /// <summary>The host the context was created for (the transport
    /// recreates the context when the redirect target's host differs).</summary>
    public string Host => _host;

    private AuthContext(GitAuthSchemeType scheme, GitCredential credential, string host, bool isProxy, NegotiateAuthentication? negotiate)
    {
        _scheme = scheme;
        _credential = credential;
        _host = host;
        _isProxy = isProxy;
        _negotiate = negotiate;
    }

    /// <summary>
    /// Create an auth context for the given scheme and credential.
    /// </summary>
    /// <param name="scheme">The auth scheme (Basic, Negotiate, or NTLM).</param>
    /// <param name="credential">The credential to use (UserPassCredential for Basic/NTLM, DefaultCredential or UserPassCredential for Negotiate).</param>
    /// <param name="host">The target hostname.</param>
    /// <param name="isProxy">Whether this is for proxy authentication (true) or server authentication (false).</param>
    /// <returns>A new <see cref="AuthContext"/>.</returns>
    public static AuthContext Create(GitAuthSchemeType scheme, GitCredential credential, string host, bool isProxy)
    {
        NegotiateAuthentication? negotiate = null;

        if (scheme is GitAuthSchemeType.Negotiate or GitAuthSchemeType.Ntlm)
        {
            NetworkCredential netCred = credential switch
            {
                GitDefaultCredential => CredentialCache.DefaultNetworkCredentials,
                GitUserPassCredential up => new NetworkCredential(up.Username, up.Password),
                _ => CredentialCache.DefaultNetworkCredentials,
            };

            negotiate = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            {
                Credential = netCred,
                TargetName = host,
                RequiredProtectionLevel = ProtectionLevel.None,
            });
        }

        return new AuthContext(scheme, credential, host, isProxy, negotiate);
    }

    /// <summary>
    /// Generate the next <c>Authorization</c> (or <c>Proxy-Authorization</c>) header value.
    /// Ported from <c>git_http_auth_context.next_token</c>.
    /// </summary>
    /// <param name="challenge">The challenge value from the <c>WWW-Authenticate</c>/<c>Proxy-Authenticate</c> response, or null for the initial token.</param>
    /// <returns>The auth header value (e.g. <c>"Basic dXNlcjpwYXNz"</c>), or null if no token is available.</returns>
    public string? NextToken(string? challenge)
    {
        switch (_scheme)
        {
            case GitAuthSchemeType.Basic:
                _complete = true;
                return BasicToken();

            case GitAuthSchemeType.Negotiate:
            case GitAuthSchemeType.Ntlm:
                return NegotiateToken(challenge);

            default:
                return null;
        }
    }

    private string BasicToken()
    {
        if (_credential is not GitUserPassCredential up)
        {
            throw new GitException(GitErrorCode.Auth, "Basic auth requires a UserPassCredential", GitErrorCategory.Net);
        }

        string raw = $"{up.Username}:{up.Password}";
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        return "Basic " + Convert.ToBase64String(bytes);
    }

    private string? NegotiateToken(string? challenge)
    {
        if (_negotiate is null)
        {
            return null;
        }

        string? incomingBlob = null;
        if (!string.IsNullOrWhiteSpace(challenge))
        {
            string schemeName = _scheme == GitAuthSchemeType.Negotiate ? "Negotiate " : "NTLM ";
            if (challenge.StartsWith(schemeName, StringComparison.OrdinalIgnoreCase))
            {
                incomingBlob = challenge[schemeName.Length..].Trim();
            }
            else
            {
                incomingBlob = challenge.Trim();
            }
        }

        string? outgoing = _negotiate.GetOutgoingBlob(incomingBlob, out NegotiateAuthenticationStatusCode statusCode);

        if (statusCode is not NegotiateAuthenticationStatusCode.Completed and
            not NegotiateAuthenticationStatusCode.ContinueNeeded)
        {
            throw new GitException(GitErrorCode.Auth, $"authentication failed: {statusCode}", GitErrorCategory.Net);
        }

        if (statusCode == NegotiateAuthenticationStatusCode.Completed)
        {
            _complete = true;
        }

        if (string.IsNullOrEmpty(outgoing))
        {
            return null;
        }

        string schemePrefix = _scheme == GitAuthSchemeType.Negotiate ? "Negotiate " : "NTLM ";
        return schemePrefix + outgoing;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _negotiate?.Dispose();
        _credential.Dispose();
        _disposed = true;
    }
}
