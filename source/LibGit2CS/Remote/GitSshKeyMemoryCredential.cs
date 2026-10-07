// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// In-memory SSH public-key credential. Maps to
/// <c>git_credential_ssh_key_memory</c> /
/// <c>git_credential_ssh_key_memory_new</c>
/// (<c>transports/credential.c:200-226</c>).
/// </summary>
/// <remarks>
/// Carries the raw key bytes (no file paths). The transport parses them via
/// LibSsh2CS's <c>SshPemParser.ParseOpenSshPrivateKey</c> and authenticates via
/// <c>SshUserAuth.AuthenticateWithPublicKeyAsync</c>.
/// <para>
/// <see cref="PublicKey"/> is nullable — parity with LibSsh2CS's
/// <c>SshUserAuth.AuthenticateWithPublicKeyAsync</c> overload,
/// which derives the public key blob from the parsed private key when the
/// caller passes null.
/// </para>
/// <para>
/// <see cref="Dispose"/> zeroes the byte arrays holding key material.
/// </para>
/// </remarks>
public sealed class GitSshKeyMemoryCredential : GitCredential
{
    private byte[]? _publicKey;
    private byte[]? _privateKey;

    /// <summary>
    /// Creates a new in-memory SSH key credential.
    /// </summary>
    /// <param name="username">The SSH username (required, non-empty).</param>
    /// <param name="publicKey">
    /// SSH wire-format public key blob, or <c>null</c> to derive from
    /// <paramref name="privateKey"/> (parity with LibSsh2CS's
    /// <c>SshUserAuth.AuthenticateWithPublicKeyAsync</c> overload).
    /// </param>
    /// <param name="privateKey">The raw private key file bytes (PEM armor intact).</param>
    /// <param name="passphrase">Passphrase for an encrypted private key, or <c>null</c>.</param>
    public GitSshKeyMemoryCredential(string username, byte[]? publicKey, byte[] privateKey, string? passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentNullException.ThrowIfNull(privateKey);

        Username = username;
        _publicKey = publicKey;
        _privateKey = privateKey;
        Passphrase = passphrase;
    }

    /// <inheritdoc/>
    public override string? Username { get; }

    /// <summary>
    /// The SSH wire-format public key blob, or <c>null</c> to derive from
    /// <see cref="PrivateKey"/>. Returns a reference to the internal array
    /// (callers must not mutate); zeroed on <see cref="Dispose"/>.
    /// </summary>
    public byte[]? PublicKey => _publicKey;

    /// <summary>
    /// The raw private key file bytes (PEM armor intact). Returns a reference
    /// to the internal array (callers must not mutate); zeroed on
    /// <see cref="Dispose"/>.
    /// </summary>
    public byte[]? PrivateKey => _privateKey;

    /// <summary>The passphrase for an encrypted private key, or <c>null</c>.</summary>
    public string? Passphrase { get; }

    /// <inheritdoc/>
    public override GitCredentialType Type => GitCredentialType.SshMemory;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Zero the sensitive material (parity intent with C's free-and-null
            // pattern; .NET strings are immutable so we can't zero Passphrase,
            // but byte arrays can be cleared).
            if (_privateKey is not null)
            {
                Array.Clear(_privateKey);
                _privateKey = null;
            }

            if (_publicKey is not null)
            {
                Array.Clear(_publicKey);
                _publicKey = null;
            }
        }

        base.Dispose(disposing);
    }
}
