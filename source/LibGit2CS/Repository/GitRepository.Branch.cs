// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;

namespace LibGit2CS.Repository;

/// <content> Branch operations. Managed port of libgit2's <c>src/libgit2/branch.c</c> public entry points that take a <c>git_repository *</c>. </content>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "1:1 port of libgit2's git_repository — the partial class intentionally mirrors the C struct's broad surface; coupling is inherent to the port.")]
public sealed partial class GitRepository
{
    // ── Read side ───────────────────────────────────────────────────────

    /// <summary>
    /// Looks up a branch by name and type. Matches <c>git_branch_lookup</c>
    /// (branch.c:349-375).
    /// </summary>
    /// <param name="branchName">The short branch name (without <c>refs/heads/</c> or <c>refs/remotes/</c> prefix).</param>
    /// <param name="branchType">The branch type to look up (<see cref="GitBranchType.Local"/>, <see cref="GitBranchType.Remote"/>, or <see cref="GitBranchType.All"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The branch reference, or null if not found.</returns>
    public async Task<GitReference?> BranchLookupAsync(string branchName, GitBranchType branchType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branchName);

        if (branchType == GitBranchType.All)
        {
            GitReference? local = await BranchLookupByPrefixAsync(branchName, isRemote: false, cancellationToken).ConfigureAwait(false);
            return local ?? await BranchLookupByPrefixAsync(branchName, isRemote: true, cancellationToken).ConfigureAwait(false);
        }

        return await BranchLookupByPrefixAsync(branchName, isRemote: branchType == GitBranchType.Remote, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitReference?> BranchLookupByPrefixAsync(string branchName, bool isRemote, CancellationToken cancellationToken)
    {
        string prefix = isRemote ? GitReferences.RefsRemotesDir : GitReferences.RefsHeadsDir;
        string refName = prefix + branchName;
        return await Refs.LookupAsync(refName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Iterates over branches of the given type(s). Matches
    /// <c>git_branch_iterator_new</c> + <c>git_branch_next</c>
    /// (branch.c:238-296).
    /// </summary>
    public async IAsyncEnumerable<(GitReference Branch, GitBranchType Type)> BranchForEachAsync(
        GitBranchType listFlags, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (GitReference reference in Refs.ListAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (reference.IsBranch && (listFlags & GitBranchType.Local) != 0)
            {
                yield return (reference, GitBranchType.Local);
            }
            else if (reference.IsRemote && (listFlags & GitBranchType.Remote) != 0)
            {
                yield return (reference, GitBranchType.Remote);
            }
        }
    }

    // ── Upstream (read) ─────────────────────────────────────────────────

    /// <summary>
    /// Resolves the upstream (tracking) reference name for a local branch.
    /// Matches <c>git_branch__upstream_name</c> (branch.c:427-490).
    /// </summary>
    public async ValueTask<string> BranchUpstreamNameAsync(string refName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refName);

        if (!refName.StartsWith(GitReferences.RefsHeadsDir, StringComparison.Ordinal))
        {
            // C (branch.c:48-54, not_a_local_branch): GIT_ERROR (-1), not
            // GIT_EINVALIDSPEC.
            throw new GitException(
                GitErrorCode.Error,
                $"reference '{refName}' is not a local branch.",
                GitErrorCategory.Invalid);
        }

        string shortName = refName[GitReferences.RefsHeadsDir.Length..];

        // read entries once — ValueBytes for the byte-domain "." compare (C's strcmp, branch.c:465) and Value for the string downstream (fetch-spec key +
        // refspec matching).
        GitConfigEntry? remoteEntry = await Config.GetEntryAsync($"branch.{shortName}.remote", cancellationToken).ConfigureAwait(false);
        GitConfigEntry? mergeEntry = await Config.GetEntryAsync($"branch.{shortName}.merge", cancellationToken).ConfigureAwait(false);

        if (remoteEntry is null || mergeEntry is null
            || remoteEntry.Value.ValueBytes is not { Length: > 0 } remoteBytes
            || mergeEntry.Value.ValueBytes is not { Length: > 0 })
        {
            throw new GitException(
                GitErrorCode.NotFound,
                // C (branch.c:458-463): the message uses the FULL refname.
                $"branch '{refName}' does not have an upstream",
                GitErrorCategory.Reference);
        }

        if (ConfigKeyName.AsciiEquals(remoteBytes.Span, "."u8))
        {
            return mergeEntry.Value.Value!;
        }

        string remoteName = remoteEntry.Value.Value!;
        string mergeName = mergeEntry.Value.Value!;

        // byte-domain fetch-spec read + byte refspec matching (C reads config values as raw bytes and wildmatches raw bytes).
        IReadOnlyList<byte[]> fetchSpecs = await Config.GetMultiBytesAsync($"remote.{remoteName}.fetch", cancellationToken).ConfigureAwait(false);
        byte[] mergeNameBytes = Encoding.UTF8.GetBytes(mergeName);
        foreach (byte[] fetchSpec in fetchSpecs)
        {
            var spec = GitRefSpec.Parse(fetchSpec, isFetch: true);
            if (spec.SrcMatches(mergeNameBytes))
            {
                return Encoding.UTF8.GetString(spec.Transform(mergeNameBytes));
            }
        }

        // C (branch.c:473-475): a remote with no matching refspec fails
        // with a BARE GIT_ENOTFOUND (no message set).
        throw new GitException(
            GitErrorCode.NotFound,
            string.Empty,
            GitErrorCategory.Reference);
    }

    /// <summary>
    /// Resolves the upstream reference for a local branch and returns it.
    /// Matches <c>git_branch_upstream</c> (branch.c:628-649).
    /// </summary>
    public async ValueTask<GitReference?> BranchUpstreamAsync(string refName, CancellationToken cancellationToken = default)
    {
        string upstreamName = await BranchUpstreamNameAsync(refName, cancellationToken).ConfigureAwait(false);
        return await Refs.ResolveAsync(upstreamName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the <c>branch.&lt;name&gt;.remote</c> config value.
    /// Matches <c>git_branch_upstream_remote</c> (branch.c:517-531).
    /// </summary>
    public async ValueTask<string?> BranchUpstreamRemoteAsync(string refName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refName);

        string shortName = BranchGetShortNameOrThrow(refName);
        return await Config.GetStringAsync($"branch.{shortName}.remote", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the <c>branch.&lt;name&gt;.merge</c> config value.
    /// Matches <c>git_branch_upstream_merge</c> (branch.c:533-547).
    /// </summary>
    public async ValueTask<string?> BranchUpstreamMergeAsync(string refName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refName);

        string shortName = BranchGetShortNameOrThrow(refName);
        return await Config.GetStringAsync($"branch.{shortName}.merge", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines the remote name for a branch. Reads
    /// <c>branch.&lt;name&gt;.remote</c> from config; if not set, iterates
    /// all remotes and checks their fetch refspecs for a match against the
    /// branch name. Returns the first matching remote, or null if no match.
    /// </summary>
    /// <remarks>
    /// This is a C#-specific convenience method. libgit2 only provides
    /// <see cref="BranchUpstreamRemoteAsync"/> (config read); this method adds a
    /// fallback to refspec matching for branches without explicit
    /// <c>branch.*.remote</c> config.
    /// </remarks>
    public async ValueTask<string?> BranchRemoteNameAsync(string refName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refName);

        string shortName = BranchGetShortNameOrThrow(refName);

        // First, check config.
        string? configRemote = await Config.GetStringAsync($"branch.{shortName}.remote", cancellationToken).ConfigureAwait(false);
        if (configRemote is not null)
        {
            return configRemote;
        }

        // Fallback: iterate remotes and check fetch refspecs.
        string fullName = refName.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? refName
            : $"refs/heads/{shortName}";

        IReadOnlyList<string> remotes = await Remote.GitRemote.ListAsync(this, cancellationToken).ConfigureAwait(false);
        foreach (string remoteName in remotes)
        {
            Remote.GitRemote remote = await Remote.GitRemote.LookupAsync(this, remoteName, cancellationToken).ConfigureAwait(false);
            foreach (GitRefSpec spec in remote.RefSpecs)
            {
                if (!spec.IsFetch)
                {
                    continue;
                }

                if (spec.SrcMatches(Encoding.UTF8.GetBytes(fullName)))
                {
                    return remoteName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the upstream <see cref="Remote.GitRemote"/> for a local branch.
    /// This is a C#-specific convenience that composes <see cref="BranchRemoteNameAsync"/>
    /// with <see cref="Remote.GitRemote.LookupAsync"/>. <see cref="BranchUpstreamAsync"/> returns
    /// the tracking <em>reference</em> (e.g. <c>refs/remotes/origin/main</c>);
    /// this method returns the <see cref="Remote.GitRemote"/> object itself.
    /// </summary>
    /// <returns>
    /// The upstream <see cref="Remote.GitRemote"/>, or null when the branch has
    /// no upstream, when the upstream is local (<c>branch.&lt;name&gt;.remote
    /// = "."</c>), or when the configured remote no longer exists in config.
    /// </returns>
    public async ValueTask<Remote.GitRemote?> BranchRemoteAsync(string refName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refName);

        string? name = await BranchRemoteNameAsync(refName, cancellationToken).ConfigureAwait(false);
        if (name is null or ".")
        {
            return null;
        }

        // Remote.Lookup throws NotFound if the remote section was removed
        // after branch.<name>.remote was written. Treat that as no remote.
        try
        {
            return await Remote.GitRemote.LookupAsync(this, name, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException ex) when (ex.Code == GitErrorCode.NotFound)
        {
            return null;
        }
    }

    // ── Write side ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new branch pointing at a commit. Matches
    /// <c>git_branch_create</c> (branch.c:130-141).
    /// </summary>
    /// <param name="branchName">The short branch name (without <c>refs/heads/</c>).</param>
    /// <param name="target">The commit OID the branch will point at.</param>
    /// <param name="force">If true, overwrite an existing branch (unless it's the current HEAD).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created branch reference.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.InvalidSpec"/> if the name is invalid.
    /// <see cref="GitErrorCode.Exists"/> if the branch exists and <paramref name="force"/> is false.
    /// <see cref="GitErrorCode.Invalid"/> if <paramref name="force"/> is true but the branch is the current HEAD.
    /// </exception>
    public async Task<GitReference> BranchCreateAsync(string branchName, GitOid target, bool force = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branchName);

        string hex = target.ToString();
        return await BranchCreateInternalAsync(branchName, target, from: hex, force: force, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new branch pointing at an annotated commit. Matches
    /// <c>git_branch_create_from_annotated</c> (branch.c:143-152). Uses the
    /// annotated commit's description for the reflog message.
    /// </summary>
    public async Task<GitReference> BranchCreateFromAnnotatedAsync(string branchName, GitAnnotatedCommit target, bool force = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branchName);
        ArgumentNullException.ThrowIfNull(target);

        return await BranchCreateInternalAsync(branchName, target.Id, from: target.Description, force: force, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitReference> BranchCreateInternalAsync(string branchName, GitOid target, string from, bool force, CancellationToken cancellationToken)
    {
        // C (branch.c:73-84): branch_name_is_valid checks ONLY the leading
        // dash and "HEAD" — a bare -1 (GIT_ERROR) with class
        // GIT_ERROR_REFERENCE. Anything else (e.g. "inv@{id") passes here and
        // fails later in git_reference_create's name normalization with
        // GIT_EINVALIDSPEC.
        if (branchName.Length > 0 && (branchName[0] == '-' || branchName == "HEAD"))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"'{branchName}' is not a valid branch name",
                GitErrorCategory.Reference);
        }

        // When force is requested, refuse to overwrite the current HEAD branch.
        if (force && !IsBare)
        {
            GitReference? existing = await BranchLookupByPrefixAsync(branchName, isRemote: false, cancellationToken).ConfigureAwait(false);
            if (existing is not null && await existing.IsHeadAsync(cancellationToken).ConfigureAwait(false))
            {
                // C (branch.c:99-105): -1 (GIT_ERROR), GIT_ERROR_REFERENCE.
                throw new GitException(
                    GitErrorCode.Error,
                    $"cannot force update branch '{branchName}' as it is the current HEAD of the repository",
                    GitErrorCategory.Reference);
            }
        }

        string canonicalName = GitReferences.RefsHeadsDir + branchName;
        string logMessage = $"branch: Created from {from}";

        return await Refs.CreateAsync(canonicalName, target, force, logMessage, cancellationToken).ConfigureAwait(false);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string BranchGetShortNameOrThrow(string refName)
    {
        if (!refName.StartsWith(GitReferences.RefsHeadsDir, StringComparison.Ordinal))
        {
            // C (branch.c:48-54, not_a_local_branch): GIT_ERROR (-1), not
            // GIT_EINVALIDSPEC.
            throw new GitException(
                GitErrorCode.Error,
                $"reference '{refName}' is not a local branch.",
                GitErrorCategory.Invalid);
        }

        return refName[GitReferences.RefsHeadsDir.Length..];
    }
}
