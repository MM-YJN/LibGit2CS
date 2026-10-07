// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Attributes;

/// <summary> Describes the source data being filtered. Managed port of <c>git_filter_source</c> (<c>src/libgit2/filter.c:23-30</c>). </summary>
/// <param name="Repo">The repository (may be null for non-repo filtering).</param>
/// <param name="Path">The relative path of the file being filtered, or null. Byte-faithful.</param>
/// <param name="Id">The OID of the source blob (zero if unknown).</param>
/// <param name="FileMode">The file mode (zero if unknown).</param>
/// <param name="Mode">The filter direction (smudge or clean).</param>
/// <param name="Flags">The filter flags.</param>
public readonly record struct GitFilterSource(
    GitRepository? Repo,
    GitPath? Path,
    GitOid Id,
    GitFileMode FileMode,
    GitFilterMode Mode,
    GitFilterListFlags Flags)
{
    /// <summary>The repository, or null. Matches <c>git_filter_source_repo</c>.</summary>
    public GitRepository? SourceRepo => Repo;

    /// <summary>The relative path, or null. Matches <c>git_filter_source_path</c>.
    /// Decodes via <see cref="GitPath.ToUtf8String"/> for display; prefer
    /// <see cref="Path"/> for byte-faithful access.</summary>
    public string? SourcePath => Path?.ToUtf8String();

    /// <summary>The OID of the source blob, or null if unknown. Matches <c>git_filter_source_id</c>.</summary>
    public GitOid? SourceId => Id.IsZero ? null : Id;

    /// <summary>The file mode. Matches <c>git_filter_source_filemode</c>.</summary>
    public GitFileMode SourceFileMode => FileMode;

    /// <summary>The filter direction. Matches <c>git_filter_source_mode</c>.</summary>
    public GitFilterMode SourceMode => Mode;

    /// <summary>The filter flags. Matches <c>git_filter_source_flags</c>.</summary>
    public GitFilterListFlags SourceFlags => Flags;
}
