// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Diff;

/// <summary>
/// Options for parsing a patch from text. Managed equivalent of
/// <c>git_patch_options</c> (<c>src/libgit2/patch.h:55-68</c>).
/// </summary>
public sealed record GitPatchParseOptions
{
    /// <summary>Number of leading path components to strip; defaults to one.</summary>
    public int PrefixLength { get; init; } = 1;
    /// <summary>The object ID algorithm; defaults to SHA-1.</summary>
    public GitHashAlgorithmKind OidType { get; init; } = GitHashAlgorithmKind.Sha1;
}
