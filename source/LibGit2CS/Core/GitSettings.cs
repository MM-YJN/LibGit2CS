// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary> Library-wide settings. Managed port of libgit2's <c>settings.c</c> + <c>git_libgit2_opts</c>. </summary> <remarks> <para> libgit2 exposes a
/// varargs <c>git_libgit2_opts(int key,...)</c> dispatch. In the managed port, each option becomes a typed property. The full <c>GIT_OPT_*</c> enum is
/// preserved in <see cref="LibraryOption"/> for fidelity with the C API; properties are implemented as they become relevant. </para> <para>
/// Per-context instance class carried by <see cref="GitContext.Settings"/>. Its members are instance properties: <see
/// cref="KnownHostsPathOverride"/>, <see cref="AgentSocketPathOverride"/>, and the strictness flags <see cref="StrictObjectCreation"/>,
/// <see cref="StrictHashVerification"/>, <see cref="OwnerValidation"/>. Callers reach the directory resolver directly via <see cref="GitContext.Dirs"/>. </para> </remarks>
public sealed class GitSettings
{
    /// <summary>
    /// When non-null, the SSH transport invokes this function to resolve
    /// the SSH agent socket path. A non-empty return value is used
    /// verbatim; a <c>null</c> or empty return falls back to the default
    /// <c>$SSH_AUTH_SOCK</c> environment-variable discovery. Per-context
    /// Set on the context that owns the transport
    /// (<c>ctx.Settings.AgentSocketPathOverride</c>). Lets a consumer wire
    /// an optional configuration path without losing the default when the
    /// config key is unset.
    /// </summary>
    public Func<string?>? AgentSocketPathOverride { get; set; }

    /// <summary>
    /// When non-null, the SSH transport invokes this function to resolve
    /// the <c>known_hosts</c> path. A non-empty return value is used
    /// verbatim; a <c>null</c> or empty return falls back to the default
    /// <c>~/.ssh/known_hosts</c> resolution. Set on
    /// the context that owns the transport
    /// (<c>ctx.Settings.KnownHostsPathOverride</c>). Lets a consumer wire
    /// an optional configuration path without losing the default when the
    /// config key is unset.
    /// </summary>
    public Func<string?>? KnownHostsPathOverride { get; set; }

    // --- Strictness / validation flags (per-context instance) ---

    /// <summary>
    /// Whether to strictly validate object format on read/write.
    /// Maps to <c>GIT_OPT_ENABLE_STRICT_OBJECT_CREATION</c> + <c>git_object__strict_input_validation</c>.
    /// Default: <c>true</c>.
    /// </summary>
    public bool StrictObjectCreation { get; set; } = true;

    /// <summary>
    /// Whether to verify hashes when reading objects from disk.
    /// Maps to <c>GIT_OPT_ENABLE_STRICT_HASH_VERIFICATION</c> + <c>git_odb__strict_hash_verification</c>.
    /// Default: <c>true</c>.
    /// </summary>
    public bool StrictHashVerification { get; set; } = true;

    /// <summary>
    /// Whether to validate repository ownership (safe.directory) when opening repos.
    /// Maps to <c>GIT_OPT_GET/SET_OWNER_VALIDATION</c> + <c>git_repository__validate_ownership</c>.
    /// Default: <c>true</c>.
    /// </summary>
    public bool OwnerValidation { get; set; } = true;

    /// <summary>
    /// The HTTP User-Agent product string. Matches
    /// <c>GIT_OPT_SET_USER_AGENT_PRODUCT</c> (default "git/2.0",
    /// settings.c:97-104). An empty string omits the product.
    /// </summary>
    public string UserAgentProduct { get; set; } = "git/2.0";

    /// <summary>
    /// The HTTP User-Agent comment. Matches <c>GIT_OPT_SET_USER_AGENT</c>
    /// (default "libgit2 1.9.4"). An empty string omits the comment.
    /// </summary>
    public string UserAgentComment { get; set; } = "libgit2 1.9.4";
}
