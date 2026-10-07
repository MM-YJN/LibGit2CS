// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Options for email (<c>git format-patch</c>) creation. Managed equivalent of
/// <c>git_email_create_options</c> (<c>include/git2/email.h:44-72</c>).
/// </summary>
/// <remarks>
/// Defaults match <c>GIT_EMAIL_CREATE_OPTIONS_INIT</c>: binary diffs included
/// (<c>SHOW_BINARY</c>), 3 context lines, rename detection ON.
/// </remarks>
public sealed record GitEmailOptions
{
    /// <summary>Output formatting flags.</summary>
    public GitEmailCreateFlags Flags { get; init; } = GitEmailCreateFlags.Default;

    /// <summary>
    /// Diff options passed to <c>git_diff__commit</c>. Default includes
    /// <see cref="GitDiffOptionsFlags.ShowBinary"/> + 3 context lines.
    /// </summary>
    public GitDiffOptions? DiffOptions { get; init; }

    /// <summary>Options for <c>git_diff_find_similar</c> (rename detection). Default: ON.</summary>
    public GitDiffFindOptions? DiffFindOptions { get; init; }

    /// <summary>
    /// Subject prefix text. <c>null</c> = <c>"PATCH"</c> (default). Empty string
    /// <c>""</c> = omit prefix text. Any other string = custom prefix.
    /// </summary>
    public string? SubjectPrefix { get; init; }

    /// <summary>Starting patch number (default 1, must not be 0).</summary>
    public int StartNumber { get; init; } = 1;

    /// <summary>
    /// Re-roll / patch-set version. <c>0</c> = none. <c>N</c> = <c>vN</c> in prefix
    /// (e.g. <c>[PATCH v3 1/5]</c>).
    /// </summary>
    public int RerollNumber { get; init; }
}
