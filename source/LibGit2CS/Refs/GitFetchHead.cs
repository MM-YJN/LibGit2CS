// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Utils;

namespace LibGit2CS.Refs;

/// <summary>
/// Reads and writes <c>.git/FETCH_HEAD</c>. Managed port of
/// <c>src/libgit2/fetchhead.c</c>.
/// </summary>
internal static class GitFetchHead
{
    /// <summary>The <c>FETCH_HEAD</c> filename.</summary>
    public const string FileName = "FETCH_HEAD";

    /// <summary>
    /// Read all entries from <c>.git/FETCH_HEAD</c>.
    /// Matches <c>git_repository_fetchhead_foreach</c> in <c>fetchhead.c:272</c>.
    /// </summary>
    /// <param name="gitdir">The <c>.git</c> directory path.</param>
    /// <param name="algorithm">The hash algorithm for parsing OIDs.</param>
    /// <param name="cancellationToken">Cancellation token, propagated by <c>await foreach</c>.</param>
    /// <returns>An async-enumerable of <see cref="GitFetchHeadEntry"/>.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> with <see cref="GitErrorCategory.FetchHead"/>
    /// for malformed content, matching <c>fetchhead_ref_parse</c>
    /// (fetchhead.c:170-270) and the "no EOL" check (fetchhead.c:317-321).
    /// </exception>
    public static async IAsyncEnumerable<GitFetchHeadEntry> ReadAsync(string gitdir, GitHashAlgorithmKind algorithm, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string path = Path.Join(gitdir, FileName);
        if (!File.Exists(path))
        {
            yield break;
        }

        int oidHexSize = GitOid.HexSizeFor(algorithm);
        string content = await AsyncFileIO.ReadAllTextWithNoBomAsync(path, cancellationToken).ConfigureAwait(false);

        // C (fetchhead.c:299-321): git__strsep splits on '\n'; an
        // unterminated final segment is NOT a line — it triggers
        // "no EOL at line N+1".
        int lineNum = 0;
        int start = 0;
        bool prevIsMerge = false; // C reuses ONE is_merge across lines
        while (start < content.Length)
        {
            int nl = content.IndexOf('\n', start, StringComparison.Ordinal);
            if (nl < 0)
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"no EOL at line {lineNum + 1}",
                    GitErrorCategory.FetchHead);
            }

            string line = content[start..nl];
            start = nl + 1;
            lineNum++;

            GitFetchHeadEntry entry = ParseLine(line, lineNum, oidHexSize, algorithm, prevIsMerge);
            prevIsMerge = entry.IsMerge;
            yield return entry;
        }
    }

    /// <summary>
    /// Write entries to <c>.git/FETCH_HEAD</c> (overwriting any existing file).
    /// Matches <c>git_fetchhead_write</c> in <c>fetchhead.c:142</c>.
    /// </summary>
    /// <param name="gitdir">The <c>.git</c> directory path.</param>
    /// <param name="entries">The entries to write (will be sorted).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteAsync(string gitdir, IReadOnlyList<GitFetchHeadEntry> entries, CancellationToken cancellationToken = default)
    {
        string path = Path.Join(gitdir, FileName);

        // Sort entries: merge first, then by ref name
        var sorted = new List<GitFetchHeadEntry>(entries);
        sorted.Sort();

        string content;
        const int InitialCapacity = 512;
        using (var sb = new ValueStringBuilder(InitialCapacity))
        {
            foreach (GitFetchHeadEntry entry in sorted)
            {
                sb.Append(entry.Format());
                sb.Append('\n');
            }
            content = sb.ToString();
        }

        await AsyncFileIO.WriteAllTextWithNoBomAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Append one sorted batch to <c>.git/FETCH_HEAD</c>.
    /// Matches a single <c>git_fetchhead_write</c> call
    /// (<c>GIT_FILEBUF_APPEND</c>) — used per refspec by
    /// <c>git_remote_update_tips</c> (remote.c:2109-2157) so the file holds
    /// each spec's sorted batch in spec order.
    /// </summary>
    public static async Task AppendAsync(string gitdir, IReadOnlyList<GitFetchHeadEntry> entries, CancellationToken cancellationToken = default)
    {
        string path = Path.Join(gitdir, FileName);

        var sorted = new List<GitFetchHeadEntry>(entries);
        sorted.Sort();

        string content;
        const int InitialCapacity = 512;
        using (var sb = new ValueStringBuilder(InitialCapacity))
        {
            foreach (GitFetchHeadEntry entry in sorted)
            {
                sb.Append(entry.Format());
                sb.Append('\n');
            }
            content = sb.ToString();
        }

        await AsyncFileIO.AppendAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Truncate <c>.git/FETCH_HEAD</c> (create empty file).
    /// Matches <c>truncate_fetchhead</c> in <c>remote.c:2095</c>.
    /// </summary>
    public static async Task TruncateAsync(string gitdir, CancellationToken cancellationToken = default)
    {
        string path = Path.Join(gitdir, FileName);
        await AsyncFileIO.WriteAllTextWithNoBomAsync(path, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sanitize a remote URL by stripping embedded credentials.
    /// Matches <c>sanitized_remote_url</c> in <c>fetchhead.c:40</c>: the URL
    /// is parsed structurally (git_net_url_parse) and re-formatted
    /// (git_net_url_fmt) with the username/password removed; on parse failure
    /// the original string is returned.
    /// </summary>
    public static string SanitizeRemoteUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!TryParseSchemeUrl(url, out string scheme, out string host, out string? port, out string path, out string? query))
        {
            return url; // C's fallback: git_net_url_parse failed
        }

        // git_net_url_fmt (net.c:1041-1063): scheme://host[:port]/path[?query]
        // — the port is omitted when it is the scheme's default; the path
        // defaults to "/" for hierarchical URLs.
        const int InitialCapacity = 256;
        using var sb = new ValueStringBuilder(InitialCapacity);
        sb.Append(scheme);
        sb.Append("://");
        sb.Append(host);
        if (port is not null)
        {
            sb.Append(':');
            sb.Append(port);
        }

        sb.Append(path);
        if (query is not null)
        {
            sb.Append('?');
            sb.Append(query);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parse a <c>scheme://[user[:pass]@]host[:port]/path[?query]</c> URL
    /// structurally, mirroring <c>git_net_url_parse</c> (net.c:459-567) for
    /// the fields <c>sanitized_remote_url</c> uses. Userinfo (which
    /// sanitization strips) is not surfaced. Returns <c>false</c> when the
    /// input is not a parseable scheme URL (relative paths, SCP-style
    /// <c>user@host:path</c>, empty hosts, non-numeric ports) — matching the
    /// C parse-failure fallback.
    /// </summary>
    private static bool TryParseSchemeUrl(string url, out string scheme, out string host, out string? port, out string path, out string? query)
    {
        scheme = string.Empty;
        host = string.Empty;
        port = null;
        path = string.Empty;
        query = null;

        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false; // no scheme — relative path or scp-style
        }

        string rawScheme = url[..schemeEnd];
        foreach (char c in rawScheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '+' and not '-' and not '.')
            {
                return false; // is_valid_scheme_char (net.c:89-94)
            }
        }

        scheme = rawScheme.ToLowerInvariant();

        int restStart = schemeEnd + 3;
        int pathStart = url.IndexOf('/', restStart, StringComparison.Ordinal);
        int queryStart = url.IndexOf('?', restStart, StringComparison.Ordinal);
        int fragStart = url.IndexOf('#', restStart, StringComparison.Ordinal);

        int authorityEnd = url.Length;
        if (pathStart >= 0)
        {
            authorityEnd = Math.Min(authorityEnd, pathStart);
        }

        if (queryStart >= 0)
        {
            authorityEnd = Math.Min(authorityEnd, queryStart);
        }

        if (fragStart >= 0)
        {
            authorityEnd = Math.Min(authorityEnd, fragStart);
        }

        string authority = url[restStart..authorityEnd];
        if (authority.Length == 0)
        {
            return false; // empty host
        }

        // Strip userinfo: the LAST '@' splits it off (url_parse_authority
        // walks backwards, net.c:182-240).
        int at = authority.LastIndexOf('@', StringComparison.Ordinal);
        string hostPort = at < 0 ? authority : authority[(at + 1)..];
        if (hostPort.Length == 0)
        {
            return false;
        }

        if (hostPort[0] == '[')
        {
            // IPv6 literal.
            int close = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            host = hostPort[1..close];
            string rest = hostPort[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                port = rest[1..];
            }
        }
        else
        {
            int colon = hostPort.LastIndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                host = hostPort[..colon];
                port = hostPort[(colon + 1)..];
            }
            else
            {
                host = hostPort;
            }
        }

        if (host.Length == 0)
        {
            return false;
        }

        if (port is not null)
        {
            if (port.Length == 0 || !port.All(char.IsAsciiDigit))
            {
                return false; // malformed port (url_invalid "malformed hostname")
            }

            if (IsDefaultPort(scheme, port))
            {
                port = null; // git_net_url_fmt omits default ports
            }
        }

        if (pathStart >= 0)
        {
            int pathEnd = url.Length;
            if (queryStart >= 0)
            {
                pathEnd = Math.Min(pathEnd, queryStart);
            }

            if (fragStart >= 0)
            {
                pathEnd = Math.Min(pathEnd, fragStart);
            }

            path = url[pathStart..pathEnd];
        }
        else
        {
            path = "/"; // hierarchical URL without a path (net.c:413-414)
        }

        if (queryStart >= 0)
        {
            int queryEnd = url.Length;
            if (fragStart >= 0)
            {
                queryEnd = Math.Min(queryEnd, fragStart);
            }

            query = url[(queryStart + 1)..queryEnd];
        }

        return true;
    }

    private static bool IsDefaultPort(string scheme, string port)
        => (scheme == "http" && port == "80") ||
           (scheme == "https" && port == "443") ||
           (scheme == "git" && port == "9418") ||
           ((scheme == "ssh" || scheme == "ssh+git" || scheme == "git+ssh") && port == "22");

    /// <summary>
    /// Parse a single FETCH_HEAD line into a <see cref="GitFetchHeadEntry"/>.
    /// Matches <c>fetchhead_ref_parse</c> in <c>fetchhead.c:170</c>.
    /// </summary>
    private static GitFetchHeadEntry ParseLine(string line, long lineNum, int oidHexSize, GitHashAlgorithmKind algorithm, bool previousIsMerge)
    {
        if (line.Length == 0)
        {
            throw new GitException(GitErrorCode.Error, $"empty line in FETCH_HEAD line {lineNum}", GitErrorCategory.FetchHead);
        }

        // Compat with old git clients that wrote FETCH_HEAD like a loose ref: no tab means the whole line is the OID and the entry is a merge. C
        // (fetchhead.c:186-192): for "<oid>\t" (a trailing tab with an empty rest) *is_merge is NOT assigned and RETAINS the previous line's value —
        // it is not reset to false per line.
        int firstTab = line.IndexOf('\t', StringComparison.Ordinal);
        string oidStr;
        int restStart;
        bool isMerge;
        if (firstTab < 0)
        {
            oidStr = line;
            restStart = line.Length;
            isMerge = true;
        }
        else
        {
            oidStr = line[..firstTab];
            restStart = firstTab + 1;
            isMerge = previousIsMerge;
        }

        if (oidStr.Length != oidHexSize)
        {
            throw new GitException(GitErrorCode.Error, $"invalid object ID in FETCH_HEAD line {lineNum}", GitErrorCategory.FetchHead);
        }

        if (!GitOid.TryParse(oidStr, algorithm, out GitOid oid))
        {
            throw new GitException(GitErrorCode.Error, $"invalid object ID in FETCH_HEAD line {lineNum}", GitErrorCategory.FetchHead);
        }

        string? refName = null;
        string? remoteUrl = null;

        // Parse new data from newer git clients (fetchhead.c:194-267).
        if (restStart < line.Length)
        {
            string rest = line[restStart..];
            int secondTab = rest.IndexOf('\t', StringComparison.Ordinal);
            if (secondTab < 0)
            {
                throw new GitException(GitErrorCode.Error, $"invalid description data in FETCH_HEAD line {lineNum}", GitErrorCategory.FetchHead);
            }

            string mergeStr = rest[..secondTab];
            if (mergeStr.Length == 0)
            {
                isMerge = true;
            }
            else if (mergeStr == "not-for-merge")
            {
                isMerge = false;
            }
            else
            {
                throw new GitException(GitErrorCode.Error, $"invalid for-merge entry in FETCH_HEAD line {lineNum}", GitErrorCategory.FetchHead);
            }

            string desc = rest[(secondTab + 1)..];
            string? type = null;
            string? name = null;

            if (desc.StartsWith("branch '", StringComparison.Ordinal))
            {
                type = "refs/heads/";
                name = desc[8..];
            }
            else if (desc.StartsWith("tag '", StringComparison.Ordinal))
            {
                type = "refs/tags/";
                name = desc[5..];
            }
            else if (desc.StartsWith('\'', StringComparison.Ordinal))
            {
                name = desc[1..];
            }

            if (name is not null)
            {
                // C (fetchhead.c:222-229): strstr(name, "' ") must be found
                // AND be followed by "of " (i.e. the description must be
                // "<name>' of <url>").
                int qIdx = name.IndexOf("' ", StringComparison.Ordinal);
                if (qIdx < 0 || qIdx + 5 > name.Length ||
                    !name.AsSpan(qIdx).StartsWith("' of ", StringComparison.Ordinal))
                {
                    throw new GitException(GitErrorCode.Error, $"invalid description in FETCH_HEAD line {lineNum}", GitErrorCategory.FetchHead);
                }

                refName = type + name[..qIdx];
                remoteUrl = name[(qIdx + 5)..];
            }
            else
            {
                remoteUrl = desc;
            }
        }
        else
        {
            // Old format: just the OID (with optional trailing tab) — no
            // merge flag was parsed (fetchhead.c:186-192).
        }

        return new GitFetchHeadEntry(oid, isMerge, refName, remoteUrl);
    }
}
