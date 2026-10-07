// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Remote;

/// <summary>
/// Bitmask of acceptable credential types. Maps to
/// <c>git_credential_t</c> in <c>include/git2/credential.h</c>.
/// </summary>
[Flags]
public enum GitCredentialType
{
    /// <summary>No credential types accepted.</summary>
    None = 0,

    /// <summary>Plaintext username + password. (<c>GIT_CREDENTIAL_USERPASS_PLAINTEXT = 1&lt;&lt;0</c>)</summary>
    UserPassPlaintext = 1 << 0,

    /// <summary>SSH key pair (file-based). (<c>GIT_CREDENTIAL_SSH_KEY = 1&lt;&lt;1</c>)</summary>
    SshKey = 1 << 1,

    /// <summary>SSH custom authentication. (<c>GIT_CREDENTIAL_SSH_CUSTOM = 1&lt;&lt;2</c>)</summary>
    SshCustom = 1 << 2,

    /// <summary>Let the transport choose (Negotiate/NTLM). (<c>GIT_CREDENTIAL_DEFAULT = 1&lt;&lt;3</c>)</summary>
    Default = 1 << 3,

    /// <summary>SSH keyboard-interactive. (<c>GIT_CREDENTIAL_SSH_INTERACTIVE = 1&lt;&lt;4</c>)</summary>
    SshInteractive = 1 << 4,

    /// <summary>Username-only (SSH first step). (<c>GIT_CREDENTIAL_USERNAME = 1&lt;&lt;5</c>)</summary>
    Username = 1 << 5,

    /// <summary>SSH key pair (in-memory). (<c>GIT_CREDENTIAL_SSH_MEMORY = 1&lt;&lt;6</c>)</summary>
    SshMemory = 1 << 6,
}

