// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary> Library initialization and version query. Managed equivalent of libgit2's <c>libgit2.c</c>. </summary> <remarks> <para> libgit2 uses a refcounted
/// init/shutdown mechanism (<c>git_libgit2_init</c> / <c>git_libgit2_shutdown</c>). In the managed port, the CLR's static-constructor guarantee replaces the
/// refcount: the type initializer runs exactly once before first use, and there is no explicit shutdown. Per-subsystem cleanup is handled by <see
/// cref="IDisposable"/> on the relevant types (e.g. <c>Repository</c>). </para> <para> <b>Context ownership.</b> <c>git_libgit2_init</c> semantics are
/// per- <c>GitContext</c>: each constructed context initializes its own registries (transports, filters, merge drivers), settings, system-directory cache, and
/// trace subscriptions. This type holds only the version/feature constants that are genuinely process-global (the library version tracks the libgit2 source
/// revision, not per-context state). <c>GitContext.Dispose</c> cascades cleanup to its sub-objects. </para> </remarks>
internal static class LibraryInit
{
    /// <summary>
    /// Major version of the libgit2 source this port tracks.
    /// </summary>
    public const int VersionMajor = 1;

    /// <summary>
    /// Minor version of the libgit2 source this port tracks.
    /// </summary>
    public const int VersionMinor = 9;

    /// <summary>
    /// Revision of the libgit2 source this port tracks.
    /// </summary>
    public const int VersionRevision = 4;

    /// <summary>
    /// Prerelease tag. Empty for stable releases.
    /// </summary>
    public static string VersionPrerelease { get; } = string.Empty;

    /// <summary>
    /// The full version string (e.g. <c>1.9.4</c>).
    /// </summary>
    public static string VersionString { get; } = $"{VersionMajor}.{VersionMinor}.{VersionRevision}";

    /// <summary> Compile-time feature flags. Matches libgit2's <c>git_libgit2_features</c>. The set mirrors the reference build: SHA256 is off (the C build has
    /// <c>GIT_EXPERIMENTAL_SHA256</c> off) and SSH is off (the C build has no libssh2); the managed port still ships a managed libssh2 implementation, but the
    /// reported flags match the C build. </summary>
    public static LibraryFeatures Features { get; } =
        LibraryFeatures.Threads |
        LibraryFeatures.HttpParser |
        LibraryFeatures.Regex |
        LibraryFeatures.Compression |
        LibraryFeatures.Sha1 |
        LibraryFeatures.Nsec;
}
