// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Collections.Immutable;

namespace LibGit2CS.Remote;

/// <summary>
/// HTTP authentication helpers. Provides challenge parsing, scheme selection,
/// and credential resolution. Replaces C's <c>auth.c</c> dispatch layer plus
/// the GSSAPI/SSPI/NTLM auth backends (~960 C LOC + ~5,400 dep LOC).
/// </summary>
internal static class AuthHandlers
{
    /// <summary>
    /// Priority order for auth schemes. Matches C's <c>auth_schemes[]</c> array
    /// in <c>httpclient.c:26-30</c>: Negotiate > NTLM > Basic.
    /// </summary>
    private static readonly ImmutableArray<GitAuthSchemeType> s_schemePriority =
    [
        GitAuthSchemeType.Negotiate,
        GitAuthSchemeType.Ntlm,
        GitAuthSchemeType.Basic,
    ];

    /// <summary>
    /// Parse <c>WWW-Authenticate</c> / <c>Proxy-Authenticate</c> header values
    /// into a list of challenges. Each header value may contain multiple
    /// comma-separated challenges for different schemes.
    /// </summary>
    public static List<AuthChallenge> ParseChallenges(IEnumerable<string> headerValues)
    {
        var challenges = new List<AuthChallenge>();

        foreach (string header in headerValues)
        {
            ParseSingleHeader(header, challenges);
        }

        return challenges;
    }

    private static void ParseSingleHeader(string header, List<AuthChallenge> challenges)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return;
        }

        int start = 0;
        int len = header.Length;

        while (start < len)
        {
            // Skip whitespace
            while (start < len && char.IsWhiteSpace(header[start]))
            {
                start++;
            }

            if (start >= len)
            {
                break;
            }

            // Find the scheme name (up to space or end)
            int schemeEnd = start;
            while (schemeEnd < len && header[schemeEnd] != ' ' && header[schemeEnd] != ',')
            {
                schemeEnd++;
            }

            string schemeName = header[start..schemeEnd].Trim();
            start = schemeEnd;

            // Find parameters (up to the next scheme name or end)
            string? parameters = null;

            // Skip whitespace after scheme name
            while (start < len && char.IsWhiteSpace(header[start]))
            {
                start++;
            }

            // Read params until we hit another known scheme or end of string
            int paramStart = start;
            int paramEnd = start;

            while (paramEnd < len)
            {
                // Look for the end of this challenge: comma followed by a known scheme
                if (header[paramEnd] == ',')
                {
                    // Check if the next non-space token is a known scheme
                    int peek = paramEnd + 1;
                    while (peek < len && char.IsWhiteSpace(header[peek]))
                    {
                        peek++;
                    }

                    int peekEnd = peek;
                    while (peekEnd < len && header[peekEnd] != ' ' && header[peekEnd] != ',')
                    {
                        peekEnd++;
                    }

                    if (peek < peekEnd && TryMatchScheme(header[peek..peekEnd]) != GitAuthSchemeType.None)
                    {
                        break;
                    }

                    paramEnd++;
                }
                else
                {
                    paramEnd++;
                }
            }

            if (paramEnd > paramStart)
            {
                parameters = header[paramStart..paramEnd].Trim();
                if (parameters.Length == 0)
                {
                    parameters = null;
                }
            }

            start = paramEnd + 1; // skip the comma

            GitAuthSchemeType scheme = TryMatchScheme(schemeName);
            if (scheme != GitAuthSchemeType.None)
            {
                challenges.Add(new AuthChallenge(scheme, parameters));
            }
        }
    }

    private static GitAuthSchemeType TryMatchScheme(string name)
    {
        if (name.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            return GitAuthSchemeType.Basic;
        }

        if (name.Equals("Negotiate", StringComparison.OrdinalIgnoreCase))
        {
            return GitAuthSchemeType.Negotiate;
        }

        if (name.Equals("NTLM", StringComparison.OrdinalIgnoreCase))
        {
            return GitAuthSchemeType.Ntlm;
        }

        return GitAuthSchemeType.None;
    }

    /// <summary> Select the best auth scheme from the available challenges for the ACQUIRED credential. Ported from <c>bestscheme_and_challenge</c> in
    /// <c>httpclient.c:464-487</c>: schemes are tried in priority order and the first whose credtypes intersect the credential's type wins. A user/pass
    /// credential therefore falls back to Basic (or NTLM when offered) even when the server also advertises Negotiate. </summary> <param name="challenges">The
    /// parsed auth challenges from the 401/407 response.</param> <param name="credential">The acquired credential (must be non-null).</param> <returns>The best
    /// supported scheme, or <see cref="GitAuthSchemeType.None"/> if none match.</returns>
    public static GitAuthSchemeType SelectBestScheme(List<AuthChallenge> challenges, GitCredential credential)
    {
        GitAuthSchemeType available = GitAuthSchemeType.None;
        foreach (AuthChallenge challenge in challenges)
        {
            available |= challenge.Scheme;
        }

        foreach (GitAuthSchemeType scheme in s_schemePriority)
        {
            if ((available & scheme) != 0 &&
                (CredentialTypesFor(scheme) & credential.Type) != 0)
            {
                return scheme;
            }
        }

        return GitAuthSchemeType.None;
    }

    /// <summary> Determine the <see cref="GitCredentialType"/> bitmask expected for the given auth scheme. Matches C's <c>git_http_auth_scheme.credtypes</c>
    /// (httpclient.c:26-30): Negotiate → DEFAULT, NTLM → USERPASS_PLAINTEXT, Basic → USERPASS_PLAINTEXT. </summary>
    public static GitCredentialType CredentialTypesFor(GitAuthSchemeType scheme)
    {
        return scheme switch
        {
            GitAuthSchemeType.Basic => GitCredentialType.UserPassPlaintext,
            GitAuthSchemeType.Negotiate => GitCredentialType.Default,
            GitAuthSchemeType.Ntlm => GitCredentialType.UserPassPlaintext,
            _ => GitCredentialType.None,
        };
    }

    /// <summary>
    /// Finds the raw challenge (including parameters, e.g. the NTLM type-2
    /// token) for the given scheme. Ported from
    /// <c>challenge_for_context()</c> (httpclient.c:492-508).
    /// </summary>
    public static string? ChallengeForScheme(List<AuthChallenge> challenges, GitAuthSchemeType scheme)
    {
        foreach (AuthChallenge challenge in challenges)
        {
            if (challenge.Scheme == scheme)
            {
                return challenge.Parameters is { Length: > 0 } p
                    ? $"{SchemeName(scheme)} {p}"
                    : SchemeName(scheme);
            }
        }

        return null;
    }

    private static string SchemeName(GitAuthSchemeType scheme)
    {
        return scheme switch
        {
            GitAuthSchemeType.Basic => "Basic",
            GitAuthSchemeType.Negotiate => "Negotiate",
            GitAuthSchemeType.Ntlm => "NTLM",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Extract username/password from a URL's <c>UserInfo</c> portion.
    /// Ported from <c>apply_url_credentials()</c> in <c>http.c:102-128</c>.
    /// </summary>
    /// <param name="url">The remote URL (e.g. <c>http://user:pass@host/path</c>).</param>
    /// <param name="allowedTypes">The credential types allowed by the auth scheme.</param>
    /// <returns>The credential, or null (GIT_PASSTHROUGH) if none applies.</returns>
    public static GitCredential? ApplyUrlCredentials(Uri url, GitCredentialType allowedTypes)
    {
        string userInfo = url.UserInfo;
        if (string.IsNullOrEmpty(userInfo))
        {
            return null;
        }

        string username;
        string password;

        int colon = userInfo.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            username = Uri.UnescapeDataString(userInfo[..colon]);
            password = Uri.UnescapeDataString(userInfo[(colon + 1)..]);
        }
        else
        {
            username = Uri.UnescapeDataString(userInfo);
            password = string.Empty;
        }

        // C (http.c:102-128, apply_url_credentials): USERPASS_PLAINTEXT wins
        // whenever allowed — a userpass credential is produced even with an
        // EMPTY username/password (so the callback is NOT invoked); with only
        // DEFAULT allowed and BOTH empty, a DEFAULT credential is produced.
        if ((allowedTypes & GitCredentialType.UserPassPlaintext) != 0)
        {
            return new GitUserPassCredential(username, password);
        }

        if ((allowedTypes & GitCredentialType.Default) != 0 &&
            username.Length == 0 && password.Length == 0)
        {
            return new GitDefaultCredential();
        }

        return null; // GIT_PASSTHROUGH
    }

    /// <summary>
    /// Resolve a credential for HTTP authentication. Tries URL-embedded
    /// credentials first, then calls the credential callback.
    /// Ported from <c>handle_auth()</c> in <c>http.c:130-173</c>.
    /// </summary>
    /// <param name="callbacks">Remote callbacks (contains <see cref="GitRemoteCallbacks.Credentials"/>).</param>
    /// <param name="url">The current request URL (may have been redirected).</param>
    /// <param name="allowedTypes">The credential types allowed by the auth scheme.</param>
    /// <param name="urlCredPresented">Whether URL credentials were already tried.</param>
    /// <param name="username">The username from the URL (if any), for the credential callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The credential (or null) and the updated <paramref name="urlCredPresented"/> flag.</returns>
    public static async Task<(GitCredential? Credential, bool UrlCredPresented)> ResolveCredentialAsync(
        GitRemoteCallbacks? callbacks,
        Uri url,
        GitCredentialType allowedTypes,
        bool urlCredPresented,
        string? username,
        CancellationToken cancellationToken)
    {
        // Try URL-embedded credentials first (once). C (http.c:143-150):
        // gated on USERPASS_PLAINTEXT; the DEFAULT-from-URL branch lives
        // inside apply_url_credentials.
        if (!urlCredPresented)
        {
            urlCredPresented = true;

            if ((allowedTypes & GitCredentialType.UserPassPlaintext) != 0)
            {
                GitCredential? urlCred = ApplyUrlCredentials(url, allowedTypes);
                if (urlCred is not null)
                {
                    return (urlCred, urlCredPresented);
                }
            }
        }

        // Fall back to the credential callback
        if (callbacks?.Credentials is { } cred)
        {
            GitCredential? result = await cred(allowedTypes, username, url.ToString(), cancellationToken).ConfigureAwait(false);
            return (result, urlCredPresented);
        }

        return (null, urlCredPresented);
    }

    /// <summary>
    /// Resolve a credential for proxy authentication.
    /// </summary>
    /// <param name="proxyConfig">Proxy configuration (contains <see cref="GitProxyConfig.Credentials"/>).</param>
    /// <param name="allowedTypes">The credential types allowed by the proxy auth scheme.</param>
    /// <param name="username">The username from the proxy URL (if any).</param>
    /// <param name="url">The proxy URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="GitCredential"/>, or null if no credential is available.</returns>
    public static async Task<GitCredential?> ResolveProxyCredentialAsync(
        GitProxyConfig? proxyConfig,
        GitCredentialType allowedTypes,
        string? username,
        string? url,
        CancellationToken cancellationToken)
    {
        if (proxyConfig?.Credentials is { } cred)
        {
            return await cred(allowedTypes, username, url, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
