// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Transports;

/// <summary>
/// Factory for an <see cref="ISshSession"/> that is not yet bound to a
/// transport. Production <see cref="SshSessionFactory"/> wraps a fresh
/// <c>LibSsh2CS.SshSession</c>; tests inject a fake factory that returns
/// scripted <see cref="ISshSession"/> instances.
/// </summary>
/// <remarks>
/// The session is transport-unbound: the caller passes the connected stream
/// to <see cref="ISshSession.HandshakeAsync"/> separately. This mirrors
/// libssh2's split between <c>libssh2_session_init()</c> (no socket) and
/// <c>libssh2_session_handshake(session, socket)</c> (binds + handshakes).
/// </remarks>
internal interface ISshSessionFactory
{
    /// <summary>
    /// Creates a new transport-unbound session. The caller passes the
    /// connected stream to <see cref="ISshSession.HandshakeAsync"/>; the
    /// session does not take ownership of it.
    /// </summary>
    ISshSession Create();
}
