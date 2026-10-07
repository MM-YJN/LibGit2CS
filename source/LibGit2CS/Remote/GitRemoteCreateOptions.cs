// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary> Options for creating a remote. Managed equivalent of <c>git_remote_create_options</c> in <c>include/git2/remote.h</c>. Drops the C <c>version</c>
/// field. Consumed by <see cref="GitRemote.CreateWithOptsAsync"/>. </summary>
public sealed record GitRemoteCreateOptions
{
    /// <summary>
    /// The remote name. Null creates an anonymous remote (no config writes,
    /// no default fetchspec, no tag downloads).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Fetch refspecs to add instead of the default. When set, the default
    /// fetchspec is skipped regardless of
    /// <see cref="GitRemoteCreateFlags.SkipDefaultFetchSpec"/> (matching C's
    /// <c>opts-&gt;fetchspec</c>).
    /// </summary>
    public IReadOnlyList<string>? FetchRefSpecs { get; init; }

    /// <summary>
    /// Flags from <see cref="GitRemoteCreateFlags"/>:
    /// <see cref="GitRemoteCreateFlags.SkipInsteadOf"/> and
    /// <see cref="GitRemoteCreateFlags.SkipDefaultFetchSpec"/>.
    /// </summary>
    public uint Flags { get; init; }
}
