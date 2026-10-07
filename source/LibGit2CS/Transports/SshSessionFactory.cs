// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using LibSsh2CS;

namespace LibGit2CS.Transports;

/// <summary>
/// Default <see cref="ISshSessionFactory"/>: wraps a fresh
/// <see cref="SshSession"/> in <see cref="SshSessionAdapter"/>.
/// </summary>
/// <remarks>
/// The optional <c>rekeyPolicy</c> constructor argument (integration-test seam)
/// lets integration tests install a tiny <see cref="RekeyPolicy"/> threshold on
/// the underlying session before handshake — used by
/// <c>SshTransportDockerTests.Rekey_UnderLoad_Succeeds</c> to force a rekey
/// mid-transfer. Production callers use the parameterless overload (the
/// session defaults to <see cref="RekeyPolicy.Default"/> — OpenSSH's 4 GB /
/// 2^31 packets / 1 hour).
/// </remarks>
internal sealed class SshSessionFactory : ISshSessionFactory
{
    private readonly RekeyPolicy? _rekeyPolicy;

    /// <summary>
    /// Creates a factory that produces sessions with the default
    /// <see cref="RekeyPolicy"/>.
    /// </summary>
    public SshSessionFactory()
    {
    }

    /// <summary>
    /// Creates a factory that produces sessions with the given
    /// <see cref="RekeyPolicy"/> applied before handshake. Test-only seam.
    /// </summary>
    /// <param name="rekeyPolicy">
    /// The policy to set on each created <see cref="SshSession"/>, or
    /// <c>null</c> to leave the default.
    /// </param>
    internal SshSessionFactory(RekeyPolicy? rekeyPolicy)
    {
        _rekeyPolicy = rekeyPolicy;
    }

    /// <inheritdoc/>
    public ISshSession Create()
    {
        var session = new SshSession();
        if (_rekeyPolicy is not null)
        {
            session.RekeyPolicy = _rekeyPolicy;
        }

        return new SshSessionAdapter(session);
    }
}
