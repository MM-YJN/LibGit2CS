// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Repository;

/// <content> Checkout operations. Managed port of libgit2's <c>src/libgit2/checkout.c</c> public entry points. </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    /// <summary>
    /// Checks out HEAD (the current commit). Matches
    /// <c>git_checkout_head</c> (checkout.c:2802-2809).
    /// </summary>
    /// <remarks>
    /// Equivalent to <see cref="CheckoutTreeAsync"/> with <c>null</c> as the
    /// tree (which resolves to HEAD).
    /// </remarks>
    /// <param name="options">Checkout options. If null, uses defaults (Safe strategy).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CheckoutHeadAsync(GitCheckoutOptions? options = null, CancellationToken cancellationToken = default)
    {
        await CheckoutTreeAsync(null, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks out a tree (or treeish: commit/tag). Matches
    /// <c>git_checkout_tree</c> (checkout.c:2745-2800).
    /// </summary>
    /// <param name="treeish">The tree/commit/tag to check out. If null, uses HEAD.</param>
    /// <param name="options">Checkout options. If null, uses defaults (Safe strategy).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CheckoutTreeAsync(GitTree? treeish, GitCheckoutOptions? options = null, CancellationToken cancellationToken = default)
    {
        GitCheckoutOptions opts = options ?? new GitCheckoutOptions();

        ThrowIfBareForCheckout(opts);

        // If treeish is null, use HEAD's tree (matches git_checkout_tree
        // behavior when treeish is NULL).
        GitTree? targetTree = treeish;
        targetTree ??= await ResolveHeadTreeForCheckoutAsync(cancellationToken).ConfigureAwait(false);

        using CheckoutContext ctx = await CheckoutContext.CreateAsync(this, opts, cancellationToken).ConfigureAwait(false);
        await ctx.RunTreeAsync(targetTree, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks out from the index. Matches <c>git_checkout_index</c>
    /// (checkout.c:2698-2743).
    /// </summary>
    /// <param name="options">Checkout options. If null, uses defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CheckoutIndexAsync(GitCheckoutOptions? options = null, CancellationToken cancellationToken = default)
    {
        GitCheckoutOptions opts = options ?? new GitCheckoutOptions();

        ThrowIfBareForCheckout(opts);

        using CheckoutContext ctx = await CheckoutContext.CreateAsync(this, opts, cancellationToken).ConfigureAwait(false);
        await ctx.RunIndexAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks out from an explicit index. Matches <c>git_checkout_index</c>
    /// (checkout.c:2698-2743) with an explicit index parameter.
    /// </summary>
    /// <param name="index">The index to check out (may differ from the repository's index).</param>
    /// <param name="options">Checkout options. If null, uses defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CheckoutIndexAsync(GitIndex index, GitCheckoutOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        GitCheckoutOptions opts = options ?? new GitCheckoutOptions();

        ThrowIfBareForCheckout(opts);

        using CheckoutContext ctx = await CheckoutContext.CreateAsync(this, opts, index, cancellationToken).ConfigureAwait(false);
        await ctx.RunIndexAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="GitErrorCode.BareRepo"/> if this repository is bare
    /// and the checkout options do not redirect output to a target directory.
    /// Consolidates the bare-repo guard shared by the checkout entry points.
    /// Matches <c>git_repository__ensure_not_bare</c> (repository.h:216-229)
    /// called from <c>checkout_data_init</c> (checkout.c:2375-2377).
    /// </summary>
    private void ThrowIfBareForCheckout(GitCheckoutOptions opts)
    {
        if (IsBare && opts.TargetDirectory is null)
        {
            throw new GitException(
                GitErrorCode.BareRepo,
                "cannot checkout. This operation is not allowed against bare repositories.",
                GitErrorCategory.Repository);
        }
    }

    /// <summary>
    /// Resolves HEAD's tree for checkout. Returns null if HEAD resolves to
    /// a non-commit object. Throws <see cref="GitErrorCode.UnbornBranch"/>
    /// if HEAD is an unborn symbolic ref (points at a branch with no
    /// commits yet) — matches libgit2's <c>checkout_lookup_head_tree</c>
    /// (checkout.c:1928-1941) which propagates <c>GIT_EUNBORNBRANCH</c> from
    /// <c>git_repository_head</c>, and <c>git_checkout_tree</c>
    /// (checkout.c:2777-2783) which returns it directly to the caller
    /// (the <c>if (error != GIT_EUNBORNBRANCH)</c> branch only overrides
    /// the message for *other* errors).
    /// </summary>
    /// <remarks>
    /// This is the entry-point resolver used by <see cref="CheckoutTreeAsync"/>
    /// / <see cref="CheckoutHeadAsync"/>; the unborn case must THROW so the
    /// caller receives <c>GIT_EUNBORNBRANCH</c>. The baseline resolver in
    /// <see cref="CheckoutContext.ResolveHeadTreeAsync"/> is a separate path
    /// that must continue returning null for unborn (matches the swallow
    /// at checkout.c:2483-2489 used during initial checkout).
    /// </remarks>
    private async Task<GitTree?> ResolveHeadTreeForCheckoutAsync(CancellationToken cancellationToken)
    {
        GitReference? head = await Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            // Unresolved HEAD — unborn branch.
            throw new GitException(
                GitErrorCode.UnbornBranch,
                "HEAD points to an unborn branch",
                GitErrorCategory.Reference);
        }

        GitOid targetOid;
        if (head is GitDirectReference dr)
        {
            targetOid = dr.Target;
        }
        else if (head is GitSymbolicReference sr)
        {
            GitReference? resolved = await sr.TargetAsync(cancellationToken).ConfigureAwait(false);
            if (resolved is GitDirectReference dt)
            {
                targetOid = dt.Target;
            }
            else
            {
                // Symbolic ref to a branch that doesn't exist yet — unborn.
                throw new GitException(
                    GitErrorCode.UnbornBranch,
                    "HEAD points to an unborn branch",
                    GitErrorCategory.Reference);
            }
        }
        else
        {
            // Unknown reference type — treat as unborn.
            throw new GitException(
                GitErrorCode.UnbornBranch,
                "HEAD points to an unborn branch",
                GitErrorCategory.Reference);
        }

        if (targetOid.IsZero)
        {
            // Zero OID — unborn branch.
            throw new GitException(
                GitErrorCode.UnbornBranch,
                "HEAD points to an unborn branch",
                GitErrorCategory.Reference);
        }

        Commit? commit = await Objects.LookupAsync<Commit>(targetOid, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            // HEAD resolved to a non-zero OID, but the commit object is absent.
            // This is an inconsistent-DB state, not an unborn branch (those
            // return earlier). C's checkout_lookup_head_tree (checkout.c:1928)
            // propagates this as GIT_ENOTFOUND; only GIT_EUNBORNBRANCH is
            // swallowed by the caller (checkout.c:2486).
            throw new GitException(
                GitErrorCode.NotFound,
                $"HEAD points to commit {targetOid} but the object is not in the database",
                GitErrorCategory.Reference);
        }

        return await Objects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
    }
}
