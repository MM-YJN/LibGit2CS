// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

namespace LibGit2CS.Remote;

/// <summary>
/// Information about a certificate offered by a remote host. Maps to
/// <c>git_cert</c> in <c>include/git2/cert.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// Carries both TLS (X.509) and SSH (hostkey) certificate material in one
/// record. TLS callers populate <see cref="Type"/>/<see cref="IsValid"/>/
/// <see cref="Hostname"/> and leave the SSH fields null. SSH callers
/// additionally populate <see cref="HostKey"/> with the raw server hostkey
/// blob and <see cref="HostKeySha1"/>/<see cref="HostKeySha256"/> with its
/// SHA-1 / SHA-256 digests (parity with <c>check_certificate</c>,
/// <c>ssh_libssh2.c:627-764</c>).
/// </para>
/// <para>
/// Named init-only record (not positional) — the SSH hostkey fields are
/// optional, which reads more naturally with named-init syntax than with
/// optional trailing positional parameters. Existing TLS construction
/// sites use the named-init form.
/// </para>
/// </remarks>
public sealed record GitCertificateInfo
{
    /// <summary>
    /// The certificate type (X.509 for TLS, hostkey for SSH).
    /// </summary>
    public required GitCertificateType Type { get; init; }

    /// <summary>
    /// For TLS: whether the system trust store validated the chain. For SSH:
    /// whether the hostkey matched a <c>known_hosts</c> entry (set by the
    /// transport before invoking the callback).
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// The hostname the connection was made to. Used by the callback to
    /// match against pinned hosts.
    /// </summary>
    public required string Hostname { get; init; }

    // ── SSH-only fields (null for TLS) ───────────────────────────────────

    /// <summary>
    /// The raw server hostkey blob (the SSH wire-format public-key bytes).
    /// Null for TLS certificates.
    /// </summary>
    public byte[]? HostKey { get; init; }

    /// <summary>
    /// SHA-1 digest of <see cref="HostKey"/> (raw bytes, not hex). Null for
    /// TLS certificates. Kept for parity with
    /// <c>libssh2_hostkey_hash(LIBSSH2_HOSTKEY_HASH_SHA1)</c>.
    /// </summary>
    public byte[]? HostKeySha1 { get; init; }

    /// <summary>
    /// SHA-256 digest of <see cref="HostKey"/> (raw bytes, not hex). Null
    /// for TLS certificates. Kept for parity with
    /// <c>libssh2_hostkey_hash(LIBSSH2_HOSTKEY_HASH_SHA256)</c>.
    /// </summary>
    public byte[]? HostKeySha256 { get; init; }

    /// <summary>
    /// The SSH hostkey algorithm wire name (e.g. <c>"ssh-ed25519"</c>,
    /// <c>"rsa-sha2-512"</c>), derived from the first SSH string of
    /// <see cref="HostKey"/>, or <c>null</c> for unrecognized/malformed
    /// blobs. Parity with <c>git_cert.raw_type</c> (the C fills the
    /// <c>LIBSSH2_HOSTKEY_TYPE_*</c> value, ssh_libssh2.c:686-739) — the
    /// wire name is the self-contained C# equivalent (callbacks can reject
    /// e.g. legacy RSA by name without depending on LibSsh2CS enums).
    /// Null for TLS certificates.
    /// </summary>
    public string? HostKeyType { get; init; }

    /// <summary>
    /// MD5 digest of <see cref="HostKey"/> (raw bytes, not hex). Parity with
    /// the C's <c>LIBSSH2_HOSTKEY_HASH_MD5</c> fingerprint
    /// (ssh_libssh2.c:697-706). Null for TLS certificates.
    /// </summary>
    [SuppressMessage("Security", "CA5351:Do not use insecure cryptographic algorithms", Justification = "MD5 used for parity hostkey fingerprinting (libgit2's git_cert exposes it), not security")]
    public byte[]? HostKeyMd5 { get; init; }

    /// <summary>
    /// The hostkey blob content length in bytes (the SSH string body, not
    /// the outer length prefix). Parity with <c>cert-&gt;hostkey_len</c>
    /// (ssh_libssh2.c:739). Null for TLS certificates.
    /// </summary>
    public int? HostKeyLength { get; init; }
}
