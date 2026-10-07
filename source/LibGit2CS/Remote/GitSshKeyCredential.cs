// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// File-based SSH public-key credential. Maps to
/// <c>git_credential_ssh_key</c> / <c>git_credential_ssh_key_new</c>
/// (<c>transports/credential.c:184-198</c>).
/// </summary>
/// <remarks>
/// The transport reads the private key file via BCL
/// <c>File.ReadAllBytesAsync</c>, parses it via LibSsh2CS's
/// <c>SshPemParser.ParseOpenSshPrivateKey</c>, and authenticates
/// via <c>SshUserAuth.AuthenticateWithPublicKeyAsync</c>.
/// <para>
/// <see cref="PublicKeyPath"/> is nullable — parity with the C API, which
/// accepts a NULL publickey (the private key file then supplies the public
/// key). <see cref="PrivateKeyPath"/> is required (C asserts non-null,
/// <c>credential.c:240</c>).
/// </para>
/// </remarks>
public sealed class GitSshKeyCredential : GitCredential
{
    /// <summary>
    /// Creates a new file-based SSH key credential.
    /// </summary>
    /// <param name="username">The SSH username (required, non-empty).</param>
    /// <param name="publicKeyPath">
    /// Path to the public key file, or <c>null</c> to derive the public key
    /// from the private key file (parity with C accepting NULL publickey).
    /// </param>
    /// <param name="privateKeyPath">Path to the private key file (required).</param>
    /// <param name="passphrase">Passphrase for an encrypted private key, or <c>null</c>.</param>
    public GitSshKeyCredential(string username, string? publicKeyPath, string privateKeyPath, string? passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(privateKeyPath);

        Username = username;
        PublicKeyPath = publicKeyPath;
        PrivateKeyPath = privateKeyPath;
        Passphrase = passphrase;
    }

    /// <inheritdoc/>
    public override string? Username { get; }

    /// <summary>
    /// Path to the public key file, or <c>null</c> when the public key is
    /// to be derived from the private key file.
    /// </summary>
    public string? PublicKeyPath { get; }

    /// <summary>The path to the private key file. Never null.</summary>
    public string PrivateKeyPath { get; }

    /// <summary>The passphrase for an encrypted private key, or <c>null</c>.</summary>
    public string? Passphrase { get; }

    /// <inheritdoc/>
    public override GitCredentialType Type => GitCredentialType.SshKey;
}
