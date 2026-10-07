// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// All <c>git_libgit2_opts</c> option keys. Maps 1:1 to libgit2's
/// <c>git_libgit2_opt_t</c>. Used for interop fidelity; the typed properties on
/// <see cref="GitSettings"/> are the primary access in managed code.
/// </summary>
internal enum LibraryOption
{
    GetMwindowSize = 0,
    SetMwindowSize = 1,
    GetMwindowMappedLimit = 2,
    SetMwindowMappedLimit = 3,
    GetSearchPath = 4,
    SetSearchPath = 5,
    SetCacheObjectLimit = 6,
    SetCacheMaxSize = 7,
    EnableCaching = 8,
    GetCachedMemory = 9,
    GetTemplatePath = 10,
    SetTemplatePath = 11,
    SetSslCertLocations = 12,
    SetUserAgent = 13,
    EnableStrictObjectCreation = 14,
    EnableStrictSymbolicRefCreation = 15,
    SetSslCiphers = 16,
    GetUserAgent = 17,
    EnableOfsDelta = 18,
    EnableFsyncGitdir = 19,
    GetWindowsSharemode = 20,
    SetWindowsSharemode = 21,
    EnableStrictHashVerification = 22,
    SetAllocator = 23,
    EnableUnsavedIndexSafety = 24,
    GetPackMaxObjects = 25,
    SetPackMaxObjects = 26,
    DisablePackKeepFileChecks = 27,
    EnableHttpExpectContinue = 28,
    GetMwindowFileLimit = 29,
    SetMwindowFileLimit = 30,
    SetOdbPackedPriority = 31,
    SetOdbLoosePriority = 32,
    GetExtensions = 33,
    SetExtensions = 34,
    GetOwnerValidation = 35,
    SetOwnerValidation = 36,
    GetHomedir = 37,
    SetHomedir = 38,
    SetServerConnectTimeout = 39,
    GetServerConnectTimeout = 40,
    SetServerTimeout = 41,
    GetServerTimeout = 42,
    SetUserAgentProduct = 43,
    GetUserAgentProduct = 44,
    AddSslX509Cert = 45,
}
