// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Transports;

namespace LibGit2CS.Remote;

/// <summary>
/// Transport registry — URL scheme dispatch and custom transport registration.
/// Managed port of <c>src/libgit2/transport.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built-in transports dispatch by URL scheme:
/// <c>git://</c> → smart+git subtransport, <c>http://</c>/<c>https://</c> →
/// smart+http subtransport, <c>ssh://</c> → smart+ssh subtransport,
/// <c>file://</c> → local transport. SCP-style (<c>user@host:path</c>) → ssh.
/// Custom transports register via <see cref="Register"/>.
/// </para>
/// <para>
/// Per-context: each <see cref="GitContext"/> owns its own
/// registry instance, so two repositories in the same process never share
/// custom transport registrations.
/// </para>
/// </remarks>
public sealed class GitTransportRegistry
{
    /// <summary>
    /// Factory function that creates a transport for a given URL.
    /// Maps to <c>git_transport_cb</c>.
    /// </summary>
    /// <param name="context">The owning context.</param>
    /// <returns>A new <see cref="IGitTransport"/> instance.</returns>
    public delegate IGitTransport TransportFactory(GitContext context);

    private sealed record TransportDefinition(string Prefix, TransportFactory Factory, object? Param);

    private readonly List<TransportDefinition> _customTransports = [];
    private readonly Lock _lock = new();

    // Built-in transport definitions, populated in the instance constructor.
    private readonly TransportDefinition[] _builtInTransports;

    /// <summary>
    /// Creates a new transport registry with the built-in transports
    /// (git://, http://, https://, file://, ssh://) pre-registered.
    /// </summary>
    public GitTransportRegistry()
    {
        _builtInTransports =
        [
            // git:// → smart transport + git subtransport
            new("git://", context => CreateSmartTransport(context, _ => new GitTransport(), isRpc: false), null),
            // http:// → smart transport + http subtransport
            new("http://", context => CreateSmartTransport(context, ctx => new GitHttpTransport(ctx), isRpc: true), null),
            // https:// → smart transport + http subtransport
            new("https://", context => CreateSmartTransport(context, ctx => new GitHttpTransport(ctx), isRpc: true), null),
            // file:// → local transport
            new("file://", context => new GitLocalTransport(context), null),
            // ssh:// → smart transport + ssh subtransport
            new("ssh://", context => CreateSmartTransport(context, ctx => new SshTransport(ctx), isRpc: false), null),
            // ssh+git:// → smart transport + ssh subtransport
            new("ssh+git://", context => CreateSmartTransport(context, ctx => new SshTransport(ctx), isRpc: false), null),
            // git+ssh:// → smart transport + ssh subtransport
            new("git+ssh://", context => CreateSmartTransport(context, ctx => new SshTransport(ctx), isRpc: false), null),
        ];
    }

    private static GitSmartTransport CreateSmartTransport(GitContext context, Func<GitContext, IGitSubtransport> factory, bool isRpc)
    {
        var definition = new SubtransportDefinition(factory, isRpc, null);
        return new GitSmartTransport(definition, context);
    }

    /// <summary>
    /// Find a transport definition by URL scheme prefix.
    /// Ported from <c>transport_find_by_url()</c>.
    /// </summary>
    private TransportDefinition? FindByUrl(ReadOnlySpan<char> url)
    {
        // Check custom transports first
        lock (_lock)
        {
            foreach (TransportDefinition def in _customTransports)
            {
                if (url.StartsWith(def.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return def;
                }
            }
        }

        // Check built-in transports
        foreach (TransportDefinition def in _builtInTransports)
        {
            if (url.StartsWith(def.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return def;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve a transport factory for the given URL, including SCP-style
    /// and local path detection.
    /// Ported from <c>transport_find_fn()</c>.
    /// </summary>
    /// <param name="url">The remote URL or local path.</param>
    /// <returns>A <see cref="TransportFactory"/> for this URL.</returns>
    /// <exception cref="GitException">If no transport can be found for the URL.</exception>
    public TransportFactory FindFactory(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        TransportDefinition? definition = FindByUrl(url);

        // SCP-style detection. C (transport.c:82-106): ANY URL containing a ':' that no registered scheme matched is re-resolved as ssh:// — including
        // "foo://host". The Windows drive-letter heuristic applies only ON Windows (C gates the local-path check on GIT_WIN32; on non-Windows "C:\foo" is
        // treated as SSH too).
        bool windowsDrive = IsWindowsDrivePath(url) && OperatingSystem.IsWindows();
        if (definition is null && !windowsDrive && url.Contains(':', StringComparison.Ordinal))
        {
            // Re-search with ssh:// scheme
            definition = FindByUrl("ssh://");
        }

        // Local path detection: if no match and path exists as a directory
        if (definition is null && Directory.Exists(url))
        {
            definition = FindByUrl("file://");
        }

        if (definition is null)
        {
            throw new GitException(GitErrorCode.NotFound, "unsupported URL protocol", GitErrorCategory.Net);
        }

        return definition.Factory;
    }

    /// <summary>
    /// Create a transport instance for the given URL.
    /// Ported from <c>git_transport_new()</c>.
    /// </summary>
    /// <param name="url">The remote URL or local path.</param>
    /// <param name="context">The owning context.</param>
    public IGitTransport Create(string url, GitContext context)
    {
        TransportFactory factory = FindFactory(url);
        return factory(context);
    }

    /// <summary>
    /// Register a custom transport for a URL scheme.
    /// Ported from <c>git_transport_register()</c>.
    /// </summary>
    /// <param name="scheme">The URL scheme (e.g. "myproto"). "://" is appended.</param>
    /// <param name="factory">The transport factory.</param>
    /// <exception cref="GitException">If the scheme is already registered.</exception>
    public void Register(string scheme, TransportFactory factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        ArgumentNullException.ThrowIfNull(factory);

        string prefix = scheme + "://";

        lock (_lock)
        {
            foreach (TransportDefinition def in _customTransports)
            {
                if (string.Equals(def.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GitException(GitErrorCode.Exists, $"transport '{scheme}' is already registered", GitErrorCategory.Net);
                }
            }

            _customTransports.Add(new TransportDefinition(prefix, factory, null));
        }
    }

    /// <summary>
    /// Unregister a custom transport.
    /// Ported from <c>git_transport_unregister()</c>.
    /// </summary>
    /// <param name="scheme">The URL scheme to unregister.</param>
    /// <exception cref="GitException">If the scheme is not registered.</exception>
    public void Unregister(string scheme)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);

        string prefix = scheme + "://";

        lock (_lock)
        {
            for (int i = 0; i < _customTransports.Count; i++)
            {
                if (string.Equals(_customTransports[i].Prefix, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _customTransports.RemoveAt(i);
                    return;
                }
            }
        }

        throw new GitException(GitErrorCode.NotFound, $"transport '{scheme}' is not registered", GitErrorCategory.Net);
    }

    /// <summary>
    /// Check if a URL looks like a Windows drive path (e.g. <c>C:\path</c>).
    /// </summary>
    private static bool IsWindowsDrivePath(ReadOnlySpan<char> url)
    {
        if (url.Length < 3)
        {
            return false;
        }

        // C:\ or C:/ pattern
        char c0 = url[0];
        return c0 is >= 'A' and <= 'Z' or >= 'a' and <= 'z' && url[1] == ':' && (url[2] == '\\' || url[2] == '/');
    }

    /// <summary>
    /// Check if a scheme is registered (built-in or custom).
    /// </summary>
    internal bool IsRegistered(string scheme)
    {
        string prefix = scheme + "://";

        lock (_lock)
        {
            foreach (TransportDefinition def in _customTransports)
            {
                if (string.Equals(def.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (TransportDefinition def in _builtInTransports)
        {
            if (string.Equals(def.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Clear all custom transports (for test cleanup).</summary>
    internal void ClearCustom()
    {
        lock (_lock)
        {
            _customTransports.Clear();
        }
    }
}
