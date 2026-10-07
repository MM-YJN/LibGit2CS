// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;
using LibGit2CS.Objects;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Checkout;

/// <summary>
/// Options for checkout operations. Managed equivalent of
/// <c>git_checkout_options</c> in <c>include/git2/checkout.h:142-185</c>.
/// Drops the C <c>version</c> field.
/// </summary>
/// <remarks>
/// <para>
/// C's <c>notify_cb</c> + <c>void *payload</c> → <see cref="Notify"/> as
/// <c>Func&lt;CheckoutNotification, bool&gt;</c> (return <c>true</c> to abort).
/// </para>
/// <para>
/// C's <c>progress_cb</c> + <c>void *payload</c> → <see cref="Progress"/> as
/// <c>IProgress&lt;CheckoutProgress&gt;</c>.
/// </para>
/// <para>
/// C's <c>perfdata_cb</c> + <c>void *payload</c> → <see cref="Perfdata"/> as
/// <c>IProgress&lt;CheckoutPerformance&gt;</c>.
/// </para>
/// </remarks>
public sealed record GitCheckoutOptions
{
    /// <summary>
    /// Checkout strategy flags. Default is <see cref="GitCheckoutStrategy.Safe"/>
    /// (refuse to overwrite uncommitted data).
    /// </summary>
    public GitCheckoutStrategy Strategy { get; init; } = GitCheckoutStrategy.Safe;

    /// <summary>
    /// If true, disable CRLF/ident/custom filters when writing to the workdir.
    /// </summary>
    public bool DisableFilters { get; init; }

    /// <summary>
    /// Mode for created directories. Default <c>0755</c>.
    /// </summary>
    public int DirMode { get; init; } = 493; // 0o755

    /// <summary>
    /// Mode for created files. <c>0</c> = use the blob's own mode.
    /// </summary>
    public int FileMode { get; init; }

    /// <summary> File open flags for created files, as POSIX <c>O_*</c> values. Default <c>O_CREAT | O_TRUNC | O_WRONLY</c> (= <c>0x241</c> on Linux). Matches
    /// C's default (checkout.c:2463-2464). Accepted but currently unused: the workdir writer uses <c>File.WriteAllBytesAsync</c>, whose semantics always match
    /// the default. </summary>
    public int FileOpenFlags { get; init; } = 0x241; // O_CREAT(0x40) | O_TRUNC(0x200) | O_WRONLY(0x01)

    /// <summary>
    /// Which <see cref="GitCheckoutNotifyFlags"/> events to notify on.
    /// </summary>
    public GitCheckoutNotifyFlags NotifyFlags { get; init; } = GitCheckoutNotifyFlags.None;

    /// <summary>
    /// Notification callback. Return <c>true</c> to abort checkout.
    /// Maps to <c>git_checkout_notify_cb</c>.
    /// </summary>
    public Func<GitCheckoutNotification, bool>? Notify { get; init; }

    /// <summary>
    /// Progress callback. Maps to <c>git_checkout_progress_cb</c>.
    /// </summary>
    public IProgress<GitCheckoutProgress>? Progress { get; init; }

    /// <summary> Pathspec — limit checkout to these paths. <c>null</c> = all paths. Byte-faithful primary; use <see cref="PathsStrings"/> for the
    /// <c>string[]</c> convenience. </summary>
    public GitPath[]? Paths { get; init; }

    /// <summary>
    /// <c>string[]</c> convenience for <see cref="Paths"/>. Setter encodes via
    /// <see cref="GitPath.FromUtf8String"/>; getter decodes via
    /// <see cref="GitPath.ToUtf8String"/>. Prefer <see cref="Paths"/> for
    /// byte-faithful paths.
    /// </summary>
    public string[]? PathsStrings
    {
        get => Paths is { } ps ? Array.ConvertAll(ps, p => p.ToUtf8String()) : null;
        init => Paths = value is null ? null : Array.ConvertAll(value, GitPath.FromUtf8String);
    }

    /// <summary>
    /// Expected workdir baseline tree. Defaults to HEAD tree.
    /// </summary>
    public GitTree? Baseline { get; init; }

    /// <summary>
    /// Override baseline with an explicit index. Mutually exclusive with
    /// <see cref="Baseline"/>.
    /// </summary>
    public GitIndex? BaselineIndex { get; init; }

    /// <summary>
    /// Alternative target directory. <c>null</c> = repo workdir root.
    /// </summary>
    public string? TargetDirectory { get; init; }

    /// <summary>Conflict marker label for the ancestor (stage 1) side. Default <c>"ancestor"</c>.</summary>
    public string? AncestorLabel { get; init; }

    /// <summary>Conflict marker label for our side (stage 2). Default <c>"ours"</c>.</summary>
    public string? OurLabel { get; init; }

    /// <summary>Conflict marker label for their side (stage 3). Default <c>"theirs"</c>.</summary>
    public string? TheirLabel { get; init; }

    /// <summary>
    /// Performance data callback. Maps to <c>git_checkout_perfdata_cb</c>.
    /// </summary>
    public IProgress<GitCheckoutPerformance>? Perfdata { get; init; }

    /// <summary>Convenience: true if <see cref="Strategy"/> includes <see cref="GitCheckoutStrategy.Force"/>.</summary>
    public bool IsForce => (Strategy & GitCheckoutStrategy.Force) != 0;

    /// <summary>Convenience: true if <see cref="Strategy"/> includes <see cref="GitCheckoutStrategy.DryRun"/>.</summary>
    public bool IsDryRun => (Strategy & GitCheckoutStrategy.DryRun) != 0;

    /// <summary>Convenience: true if the index should not be written on completion.</summary>
    public bool DontWriteIndex
        => (Strategy & GitCheckoutStrategy.DontWriteIndex) != 0 ||
        (Strategy & GitCheckoutStrategy.DontUpdateIndex) != 0;
}
