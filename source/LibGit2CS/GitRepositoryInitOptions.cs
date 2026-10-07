// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS;

/// <summary>
/// Options for <see cref="LibGit2CS.Repository.GitRepository.InitExtAsync"/>. Managed equivalent of
/// <c>git_repository_init_options</c>. Drops the C <c>version</c> field.
/// </summary>
/// <remarks>
/// <see cref="OriginUrl"/> and <see cref="TemplatePath"/> are covered;
/// <see cref="OriginUrl"/> is accepted but not yet wired (origin remote creation
/// is not implemented). Internal template (dirs + description + exclude
/// + hooks README) is always created unless an external template is used.
/// </remarks>
public sealed record GitRepositoryInitOptions
{
    /// <summary>Init flags (bare, mkdir, mkpath, etc.).</summary>
    public GitRepositoryInitFlags Flags { get; init; } = GitRepositoryInitFlags.Mkpath;

    /// <summary>Shared repository mode.</summary>
    public GitInitMode Mode { get; init; } = GitInitMode.SharedUmask;

    /// <summary>Alternate workdir path (null = default: path is the workdir, .git is the gitdir).</summary>
    public string? WorkdirPath { get; init; }

    /// <summary>Overrides the template description file content.</summary>
    public string? Description { get; init; }

    /// <summary>External template directory path (null = use internal template).</summary>
    public string? TemplatePath { get; init; }

    /// <summary>Initial HEAD branch name (null = <c>init.defaultBranch</c> config or <c>"master"</c>).</summary>
    public string? InitialHead { get; init; }

    /// <summary>Origin remote URL (null = no origin remote; not yet wired).</summary>
    public string? OriginUrl { get; init; }

    /// <summary>Convenience: true if <see cref="Flags"/> includes <see cref="GitRepositoryInitFlags.Bare"/>.</summary>
    public bool IsBare => (Flags & GitRepositoryInitFlags.Bare) != 0;
}
