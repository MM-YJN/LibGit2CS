// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;

namespace LibGit2CS.Diff;

/// <summary>
/// Options controlling diff generation. Managed equivalent of
/// <c>git_diff_options</c> in <c>include/git2/diff.h:388-471</c>.
/// </summary>
/// <remarks>
/// The C <c>version</c> field is dropped (managed ABI).
/// Defaults match <c>GIT_DIFF_OPTIONS_INIT</c>: flags=0, ignore_submodules
/// =Unspecified, context_lines=3, id_abbrev=7, max_size=512MB, prefixes
/// "a/"/"b/".
/// </remarks>
public sealed record GitDiffOptions
{
    /// <summary>Combination of <see cref="GitDiffOptionsFlags"/>.</summary>
    public GitDiffOptionsFlags Flags { get; init; }

    /// <summary>Submodule-ignore policy.</summary>
    public GitDiffIgnoreSubmodules IgnoreSubmodules { get; init; } = GitDiffIgnoreSubmodules.Unspecified;

    /// <summary> Pathspec patterns limiting the diff, or null for no limit. Byte-faithful primary; use <see cref="PathSpecStrings"/> for the <c>string[]</c>
    /// convenience. </summary>
    public GitPath[]? PathSpecs { get; init; }

    /// <summary>
    /// <c>string[]</c> convenience for <see cref="PathSpecs"/>. Setter encodes
    /// via <see cref="GitPath.FromUtf8String"/>; getter decodes via
    /// <see cref="GitPath.ToUtf8String"/>. Prefer <see cref="PathSpecs"/> for
    /// byte-faithful paths.
    /// </summary>
    public string[]? PathSpecStrings
    {
        get => PathSpecs is { } ps ? Array.ConvertAll(ps, p => p.ToUtf8String()) : null;
        init => PathSpecs = value is null ? null : Array.ConvertAll(value, GitPath.FromUtf8String);
    }

    /// <summary>Number of unchanged context lines around each hunk. Default 3.</summary>
    public int ContextLines { get; init; } = 3;

    /// <summary>Max unchanged lines between adjacent changes before a hunk split. Default 0.</summary>
    public int InterHunkLines { get; init; }

    /// <summary>Hex length for abbreviated OIDs in output. 0 = auto (core.abbrev or 7).</summary>
    public int IdAbbrevLength { get; init; } = 7;

    /// <summary>
    /// Size (bytes) above which a blob is auto-marked binary. Default 512MB.
    /// Negative disables the size check.
    /// </summary>
    public long MaxSize { get; init; } = 512L * 1024 * 1024;

    /// <summary>Virtual prefix for old file names in hunk headers. Default "a/".</summary>
    public string? OldPrefix { get; init; }

    /// <summary>Virtual prefix for new file names in hunk headers. Default "b/".</summary>
    public string? NewPrefix { get; init; }

    /// <summary>
    /// OID algorithm for this diff. Matches <c>git_diff_options.oid_type</c>
    /// (<c>include/git2/diff.h:445</c>). Defaults to <see cref="GitHashAlgorithmKind.Sha1"/>
    /// (matching <c>GIT_OID_SHA1</c>); generators seed this from
    /// <c>GitRepository.ObjectFormat</c> so blob/buffer diffs and patch-id
    /// computation use the repository's native algorithm.
    /// </summary>
    public GitHashAlgorithmKind OidType { get; init; } = GitHashAlgorithmKind.Sha1;

    /// <summary>Optional per-delta notification callback (called during delta generation).</summary>
    public GitDiffNotificationCallback? Notify { get; init; }

    /// <summary>Optional progress callback (called per path during the iterator walk).</summary>
    public IProgress<GitDiffProgress>? Progress { get; init; }

    /// <summary>Default options matching <c>GIT_DIFF_OPTIONS_INIT</c>.</summary>
    public static GitDiffOptions Default { get; } = new();
}
