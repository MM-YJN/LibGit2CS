// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.Objects;

/// <summary>
/// A merge-input wrapper around a commit, carrying the "intent" of how the commit
/// was resolved. Managed port of libgit2's <c>src/libgit2/annotated_commit.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// In libgit2, <c>git_annotated_commit</c> is consumed by merge/rebase/cherry-pick.
/// This port delivers the type + all 6 entry points (<see cref="LookupAsync"/>, <see cref="FromCommit"/>,
/// <see cref="FromFetchHeadAsync"/>, <see cref="FromRefAsync"/>, <see cref="FromHeadAsync"/>,
/// <see cref="FromRevspecAsync"/>) plus read getters and VIRTUAL mode for
/// recursive merge base computation.
/// </para>
/// <para>
/// <b>REAL vs VIRTUAL</b>: REAL wraps a fully-resolved <see cref="Commit"/>;
/// VIRTUAL wraps an in-memory <see cref="GitIndex"/> produced by recursive merge
/// base computation (<c>create_virtual_base</c>). A VIRTUAL commit carries its
/// parent OIDs (accumulated from the merged bases) so that further merge-base
/// computation can find common ancestors across virtual commits.
/// </para>
/// </remarks>
public sealed class GitAnnotatedCommit : IDisposable
{
    private bool _disposed;

    // ── REAL constructor ───────────────────────────────────────────────

    private GitAnnotatedCommit(Commit commit, string? refName, string? remoteUrl, string description)
    {
        Type = AnnotatedCommitType.Real;
        Commit = commit;
        Ref = refName;
        RemoteUrl = remoteUrl;
        Description = description;
    }

    // ── VIRTUAL constructor ────────────────────────────────────────────

    private GitAnnotatedCommit(GitIndex index, IReadOnlyList<GitOid> parentOids)
    {
        Type = AnnotatedCommitType.Virtual;
        VirtualIndex = index;
        _parentOids = parentOids;
        Description = "merged common ancestors";
    }

    /// <summary>
    /// The type of this annotated commit: <see cref="AnnotatedCommitType.Real"/>
    /// (wraps a <see cref="Commit"/>) or <see cref="AnnotatedCommitType.Virtual"/>
    /// (wraps a merged <see cref="GitIndex"/>). Matches <c>git_annotated_commit_type</c>.
    /// </summary>
    internal AnnotatedCommitType Type { get; }

    /// <summary>
    /// The backing commit. Non-null when <see cref="Type"/> is
    /// <see cref="AnnotatedCommitType.Real"/>; null for VIRTUAL.
    /// </summary>
    public Commit? Commit { get; }

    /// <summary>
    /// The merged index for a VIRTUAL annotated commit (produced by
    /// <c>create_virtual_base</c>). Null for REAL.
    /// </summary>
    internal GitIndex? VirtualIndex { get; }

    private readonly IReadOnlyList<GitOid>? _parentOids;

    /// <summary>
    /// The OID of the backing commit. For REAL, matches <c>git_annotated_commit_id</c>.
    /// For VIRTUAL, returns <c>GitOid.Zero</c> (the virtual commit has no single OID).
    /// </summary>
    public GitOid Id => Commit?.Id ?? GitOid.Empty;

    /// <summary>
    /// The origin ref name (e.g. <c>"refs/heads/main"</c>), or null if this
    /// annotated commit was constructed from a bare OID. Matches
    /// <c>git_annotated_commit_ref</c>.
    /// </summary>
    public string? Ref { get; }

    /// <summary>
    /// The remote URL (only set by <see cref="FromFetchHeadAsync"/>); otherwise null.
    /// </summary>
    public string? RemoteUrl { get; }

    /// <summary>
    /// Human-readable description of how this commit was resolved: a ref name,
    /// revspec, or hex OID string. Used by merge tools for display.
    /// </summary>
    public string Description { get; } = string.Empty;

    /// <summary>
    /// Wraps an existing loaded commit as an annotated commit. The description
    /// defaults to the commit's hex OID. Matches
    /// <c>git_annotated_commit_from_commit</c> (annotated_commit.c:91-96).
    /// </summary>
    public static GitAnnotatedCommit FromCommit(Commit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return new GitAnnotatedCommit(commit, refName: null, remoteUrl: null, description: commit.Id.ToString());
    }

    /// <summary>
    /// Loads a commit by OID from <paramref name="repo"/>'s object database and
    /// wraps it as an annotated commit. The description defaults to the commit's
    /// hex OID. Matches <c>git_annotated_commit_lookup</c>
    /// (annotated_commit.c:83-89) + <c>annotated_commit_init_from_id</c> (58-81).
    /// Internal — the public entry point is
    /// <see cref="GitRepository.AnnotatedCommitLookupAsync"/>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if <paramref name="id"/> doesn't exist.
    /// <see cref="GitErrorCode.Mismatch"/> if <paramref name="id"/> exists but isn't a commit.
    /// </exception>
    internal static async Task<GitAnnotatedCommit> LookupAsync(GitRepository repo, GitOid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        Commit commit;
        try
        {
            commit = await repo.Objects.LookupAsync<Commit>(id, cancellationToken).ConfigureAwait(false)
                ?? throw new GitException(
                    GitErrorCode.NotFound,
                    $"annotated commit lookup failed: {id} not found",
                    GitErrorCategory.Object);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.Mismatch)
        {
            // C (object.c:124-128): git_commit_lookup on a wrong-type OID
            // returns GIT_ENOTFOUND "the requested type does not match the
            // type in the ODB".
            throw new GitException(
                GitErrorCode.NotFound,
                "the requested type does not match the type in the ODB",
                GitErrorCategory.Object);
        }

        return new GitAnnotatedCommit(commit, refName: null, remoteUrl: null, description: id.ToString());
    }

    /// <summary>
    /// Constructs an annotated commit from a fetch-head entry (OID + branch name +
    /// remote URL). Matches <c>git_annotated_commit_from_fetchhead</c>
    /// (annotated_commit.c:178-201).
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if <paramref name="id"/> doesn't exist.
    /// <see cref="GitErrorCode.Mismatch"/> if <paramref name="id"/> exists but isn't a commit.
    /// </exception>
    internal static async Task<GitAnnotatedCommit> FromFetchHeadAsync(GitRepository repo, GitOid id, string branchName, string remoteUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(branchName);
        ArgumentNullException.ThrowIfNull(remoteUrl);

        Commit commit = await repo.Objects.LookupAsync<Commit>(id, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"annotated commit from fetchhead failed: {id} not found",
                GitErrorCategory.Object);

        // libgit2 sets description = branch_name; ref_name = branch_name; remote_url = remote_url.
        return new GitAnnotatedCommit(commit, refName: branchName, remoteUrl: remoteUrl, description: branchName);
    }

    /// <summary>
    /// Constructs an annotated commit by peeling a reference. Matches
    /// <c>git_annotated_commit_from_ref</c> (annotated_commit.c). The reference
    /// is resolved and its target peeled to a commit.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if the reference doesn't resolve to a commit.
    /// </exception>
    internal static async Task<GitAnnotatedCommit> FromRefAsync(GitRepository repo, GitReference @ref, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(@ref);

        // Resolve the symbolic chain to a direct reference.
        GitReference? resolved = await repo.Refs.ResolveAsync(@ref.Name, cancellationToken).ConfigureAwait(false);
        if (resolved is not GitDirectReference direct)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{@ref.Name}' does not resolve to a direct reference",
                GitErrorCategory.Reference);
        }

        GitObject obj = await repo.Objects.LookupAsync(direct.Target, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"object {direct.Target} not found",
                GitErrorCategory.Object);

        try
        {
            Commit commit = await obj.PeelAsync<Commit>(cancellationToken).ConfigureAwait(false);
            return new GitAnnotatedCommit(commit, refName: @ref.Name, remoteUrl: null, description: @ref.Name);
        }
        finally
        {
            if (obj is not Objects.Commit)
            {
                obj.Dispose();
            }
        }
    }

    /// <summary>
    /// Constructs an annotated commit by resolving HEAD. Matches
    /// <c>git_annotated_commit_from_head</c>. The ref name is recorded as
    /// <c>"HEAD"</c> (not the underlying branch HEAD points at). Internal — the
    /// public entry point is
    /// <see cref="GitRepository.AnnotatedCommitFromHeadAsync"/>.
    /// </summary>
    internal static async Task<GitAnnotatedCommit> FromHeadAsync(GitRepository repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        GitReference head = await repo.Refs.ResolveAsync(GitReferences.HeadFile, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                "HEAD is not set",
                GitErrorCategory.Reference);

        if (head is not GitDirectReference direct)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                "HEAD does not resolve to a direct reference",
                GitErrorCategory.Reference);
        }

        GitObject obj = await repo.Objects.LookupAsync(direct.Target, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(GitErrorCode.NotFound, $"object {direct.Target} not found", GitErrorCategory.Object);

        try
        {
            Commit commit = await obj.PeelAsync<Commit>(cancellationToken).ConfigureAwait(false);
            return new GitAnnotatedCommit(commit, refName: GitReferences.HeadFile, remoteUrl: null, description: GitReferences.HeadFile);
        }
        catch (GitException)
        {
            throw new GitException(GitErrorCode.Mismatch, "HEAD does not point at a commit", GitErrorCategory.Object);
        }
        finally
        {
            if (obj is not Objects.Commit)
            {
                obj.Dispose();
            }
        }
    }

    /// <summary>
    /// Constructs an annotated commit by parsing a revision specification
    /// (e.g. <c>"HEAD~3"</c>, <c>"master"</c>, <c>"a65fedf3"</c>). Matches
    /// <c>git_annotated_commit_from_revspec</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if the revspec doesn't resolve.
    /// <see cref="GitErrorCode.Mismatch"/> if it resolves to a non-commit.
    /// </exception>
    internal static async Task<GitAnnotatedCommit> FromRevspecAsync(GitRepository repo, string revspec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(revspec);

        GitObject obj = await GitRevParser.ParseSingleAsync(repo, revspec, cancellationToken).ConfigureAwait(false)
            ?? throw new GitException(
                GitErrorCode.NotFound,
                $"revspec '{revspec}' did not resolve to an object",
                GitErrorCategory.Invalid);

        try
        {
            Commit commit = await obj.PeelAsync<Commit>(cancellationToken).ConfigureAwait(false);
            return new GitAnnotatedCommit(commit, refName: null, remoteUrl: null, description: revspec);
        }
        finally
        {
            if (obj is not Objects.Commit)
            {
                obj.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Commit?.Dispose();
    }

    // ── VIRTUAL factory + parent OID accessor ──────────────────────────

    /// <summary>
    /// Creates a VIRTUAL annotated commit wrapping the merged index produced
    /// by recursive merge base computation (<c>create_virtual_base</c>).
    /// The parent OIDs are accumulated from the two merged annotated commits
    /// so that further merge-base computation can find common ancestors.
    /// </summary>
    internal static GitAnnotatedCommit CreateVirtual(GitIndex index, IReadOnlyList<GitOid> parentOids)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(parentOids);
        return new GitAnnotatedCommit(index, parentOids);
    }

    /// <summary>
    /// The parent OIDs of this annotated commit, for merge-base computation.
    /// REAL returns <c>[Commit.Id]</c>; VIRTUAL returns the accumulated parent
    /// list from the two merged bases. Matches the C <c>insert_head_ids</c>
    /// logic (merge.c:2251-2273).
    /// </summary>
    internal IReadOnlyList<GitOid> ParentOids
    {
        get
        {
            if (Type == AnnotatedCommitType.Real && Commit is not null)
            {
                return [Commit.Id];
            }

            return _parentOids ?? [];
        }
    }
}
