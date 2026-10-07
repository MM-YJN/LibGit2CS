// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Transports;

/// <summary>
/// Parsed capabilities from the remote's ref advertisement.
/// Combines the boolean flags with the string-valued capabilities.
/// </summary>
public sealed class GitSmartCapabilitySet
{
    /// <summary>The boolean capability flags.</summary>
    public GitSmartCapabilities Flags { get; set; }

    /// <summary>Whether any capabilities were parsed.</summary>
    public bool HasCapabilities => Flags != GitSmartCapabilities.None;

    /// <summary>The object-format string (e.g. "sha1" or "sha256"), or null if not advertised.</summary>
    public string? ObjectFormat { get; set; }

    /// <summary>The agent string (e.g. "git/github-gabcdef1"), or null if not advertised.</summary>
    public string? Agent { get; set; }

    /// <summary>The symref mappings parsed from the symref capability (e.g. HEAD -> refs/heads/main).</summary>
    public List<(string Source, string Target)> Symrefs { get; } = [];
}
