// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Remote;

/// <summary>
/// A parsed authentication challenge from a <c>WWW-Authenticate</c> or
/// <c>Proxy-Authenticate</c> response header.
/// </summary>
internal sealed record AuthChallenge(GitAuthSchemeType Scheme, string? Parameters);
