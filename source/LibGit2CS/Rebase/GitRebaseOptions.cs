// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Merge;

namespace LibGit2CS.Rebase;

/// <summary>
/// Options for rebase operations. Matches <c>git_rebase_options</c> in
/// <c>include/git2/rebase.h:32-114</c>. Drops the C <c>version</c> and
/// <c>payload</c> fields.
/// </summary>
public sealed record GitRebaseOptions
{
    /// <summary>
    /// Instruct other Git tools that this is a quiet rebase. No effect on
    /// libgit2 directly; written to <c>.git/rebase-merge/quiet</c> for
    /// interoperability. Default <c>false</c>.
    /// </summary>
    public bool Quiet { get; init; }

    /// <summary>
    /// If <c>true</c>, perform an in-memory rebase — no FS state files, no
    /// HEAD rewinding, no working directory interference. Default <c>false</c>.
    /// </summary>
    public bool InMemory { get; init; }

    /// <summary>
    /// Notes ref to rewrite when finishing the rebase. If <c>null</c>, the
    /// <c>notes.rewriteRef</c> config is examined (unless
    /// <c>notes.rewrite.rebase</c> is <c>false</c>). If both are unset, notes
    /// are not rewritten. Default <c>null</c>.
    /// </summary>
    public string? RewriteNotesRef { get; init; }

    /// <summary>
    /// Merge options controlling tree merging during <c>GitRebase.NextAsync</c>.
    /// Default <c>null</c> (use <see cref="GitMergeOptions.Default"/>).
    /// </summary>
    public GitMergeOptions? MergeOptions { get; init; }

    /// <summary>
    /// Checkout options for the init (<see cref="LibGit2CS.Repository.GitRepository.RebaseInitAsync"/>),
    /// next (<see cref="GitRebase.NextAsync"/>), and abort
    /// (<see cref="GitRebase.AbortAsync"/>) operations. During abort,
    /// <see cref="GitCheckoutStrategy.Force"/> is implied to match git semantics.
    /// </summary>
    public GitCheckoutOptions? CheckoutOptions { get; init; }

    /// <summary>
    /// Optional callback to override commit creation in <see cref="GitRebase.CommitAsync"/>.
    /// If <c>null</c>, the default commit-creation path is used. If the callback
    /// returns <c>null</c> (with <see cref="GitErrorCode.PassThrough"/>), the
    /// default commit creation path is used.
    /// </summary>
    public CommitCreateCallback? CommitCreateCallback { get; init; }

    /// <summary>Default options — matches <c>GIT_REBASE_OPTIONS_INIT</c>.</summary>
    public static GitRebaseOptions Default { get; } = new();
}
