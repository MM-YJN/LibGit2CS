// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Compile-time feature flags. Matches libgit2's <c>git_feature_t</c>.
/// </summary>
[Flags]
internal enum LibraryFeatures
{
    /// <summary>Threading support. (Always set in the managed port.)</summary>
    Threads = 1 << 0,

    /// <summary>HTTPS support (smart HTTP transport).</summary>
    Https = 1 << 1,

    /// <summary>SSH support. Backed by LibSsh2CS (managed port of libssh2).</summary>
    Ssh = 1 << 2,

    /// <summary>Nanosecond-resolution file timestamps.</summary>
    Nsec = 1 << 3,

    /// <summary>HTTP parser backend.</summary>
    HttpParser = 1 << 4,

    /// <summary>Regular expression support.</summary>
    Regex = 1 << 5,

    /// <summary>i18n / iconv support.</summary>
    I18n = 1 << 6,

    /// <summary>NTLM authentication (via the HTTP transport).</summary>
    AuthNtlm = 1 << 7,

    /// <summary>Negotiate authentication (via the HTTP transport).</summary>
    AuthNegotiate = 1 << 8,

    /// <summary>Compression (zlib).</summary>
    Compression = 1 << 9,

    /// <summary>SHA-1 hashing.</summary>
    Sha1 = 1 << 10,

    /// <summary>SHA-256 hashing.</summary>
    Sha256 = 1 << 11,
}
