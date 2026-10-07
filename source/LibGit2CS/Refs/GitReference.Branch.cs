// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Refs;

/// <content> Branch operations on a reference. Managed port of libgit2's <c>src/libgit2/branch.c</c> entry points that take a <c>git_reference *</c>. These
/// methods are guarded by <see cref="IsBranch"/>/<see cref="IsRemote"/> polymorphic checks rather than living on a branch-specific subtype — mirroring
/// libgit2's <c>git_reference *</c>-first-arg convention. </content>
public abstract partial record GitReference
{
    /// <summary>The raw short branch name. Throws if this reference is not a branch.</summary>
    public ReadOnlyMemory<byte> ShortNameBytes()
    {
        if (IsBranch)
        {
            return NameBytes[GitReferences.RefsHeadsDir.Length..];
        }
        if (IsRemote)
        {
            return NameBytes[GitReferences.RefsRemotesDir.Length..];
        }
        throw new GitException(GitErrorCode.InvalidSpec, $"reference '{Name}' is neither a local nor a remote branch", GitErrorCategory.Reference);
    }

    // ── Read side ───────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the short branch name from a reference. Matches
    /// <c>git_branch_name</c> (branch.c:377-399).
    /// </summary>
    /// <returns>The short name (e.g. <c>master</c> for <c>refs/heads/master</c>).</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.InvalidSpec"/> if the reference is neither a local
    /// nor a remote branch.
    /// </exception>
    public string ShortName()
    {
        if (IsBranch)
        {
            return Name[GitReferences.RefsHeadsDir.Length..];
        }

        if (IsRemote)
        {
            return Name[GitReferences.RefsRemotesDir.Length..];
        }

        throw new GitException(
            GitErrorCode.InvalidSpec,
            $"reference '{Name}' is neither a local nor a remote branch",
            GitErrorCategory.Reference);
    }

    /// <summary>
    /// Returns 1 if the branch is pointed to by HEAD. Matches
    /// <c>git_branch_is_head</c> (branch.c:773-800).
    /// </summary>
    public async Task<bool> IsHeadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBranch)
        {
            return false;
        }

        GitReference? head = Owner is { } owner
            ? await owner.Refs.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(false)
            : null;
        if (head is null)
        {
            return false;
        }

        return head.NameKey == NameKey;
    }

    /// <summary>
    /// Returns true if the branch is checked out in any worktree (including
    /// the main worktree). Matches <c>git_branch_is_checked_out</c>
    /// (branch.c:179-187).
    /// </summary>
    /// <remarks>
    /// Enumerates linked worktrees via <see cref="Worktree.ListAsync"/> and checks
    /// each worktree's HEAD. The main worktree is checked via
    /// <see cref="IsHeadAsync"/>.
    /// </remarks>
    public async Task<bool> IsCheckedOutAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBranch)
        {
            return false;
        }

        GitRepository? repo = Owner;
        if (repo is null || repo.IsBare)
        {
            return false;
        }

        // Check the main worktree.
        if (await IsHeadAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // Check linked worktrees.
        foreach (Worktree wt in await Worktree.ListAsync(repo, cancellationToken).ConfigureAwait(false))
        {
            string headPath = Path.Join(wt.GitdirPath, "HEAD");
            if (!File.Exists(headPath))
            {
                continue;
            }

            byte[] headContent = await File.ReadAllBytesAsync(headPath, cancellationToken).ConfigureAwait(false);
            ReadOnlySpan<byte> trimmed = AsciiText.Rtrim(headContent);
            if (trimmed.StartsWith("ref: "u8) && trimmed[5..].SequenceEqual(NameBytes.Span))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tests whether a branch name is valid. Matches
    /// <c>git_branch_name_is_valid</c> (branch.c:802-823).
    /// </summary>
    public static bool BranchNameIsValid(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name[0] == '-' || name == "HEAD")
        {
            return false;
        }

        string full = GitReferences.RefsHeadsDir + name;
        return GitReferences.IsNameValid(full, GitReferenceFormatFlags.AllowOneLevel);
    }

    // ── Write side ──────────────────────────────────────────────────────

    /// <summary>
    /// Deletes a branch. Matches <c>git_branch_delete</c> (branch.c:189-231).
    /// </summary>
    /// <remarks>
    /// Guards: rejects non-branch refs, refuses to delete the current HEAD
    /// branch, refuses to delete a branch checked out in a linked worktree.
    /// Removes the <c>branch.&lt;name&gt;</c> config section, then deletes
    /// the ref.
    /// </remarks>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if the reference is not a branch.
    /// <see cref="GitErrorCode.Invalid"/> if the branch is the current HEAD.
    /// </exception>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBranch && !IsRemote)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"reference '{Name}' is not a valid branch",
                GitErrorCategory.Reference);
        }

        if (await IsHeadAsync(cancellationToken).ConfigureAwait(false))
        {
            // C (branch.c:206-210): returns a bare -1 (GIT_ERROR) with class
            // GIT_ERROR_REFERENCE.
            throw new GitException(
                GitErrorCode.Error,
                $"cannot delete branch '{Name}' as it is the current HEAD of the repository",
                GitErrorCategory.Reference);
        }

        if (IsBranch && await IsCheckedOutAsync(cancellationToken).ConfigureAwait(false))
        {
            // C (branch.c:214-219): -1 (GIT_ERROR), message starts with a
            // capital "Cannot" and ends with a period.
            throw new GitException(
                GitErrorCode.Error,
                $"Cannot delete branch '{Name}' as it is the current HEAD of a linked repository.",
                GitErrorCategory.Reference);
        }

        _ = NameKey.ToFileSystemString(); // Reject unsupported paths before changing config.

        // Remove the branch.<name> config section for local branches.
        if (IsBranch)
        {
            ReadOnlyMemory<byte> shortName = NameBytes[GitReferences.RefsHeadsDir.Length..];
            GitRepository repo = Owner
                ?? throw new GitException(GitErrorCode.Invalid, "branch has no owner", GitErrorCategory.Reference);
            await repo.Config.DeleteSectionAsync((byte[])[.. "branch."u8, .. shortName.Span], cancellationToken).ConfigureAwait(false);
        }

        if (Owner is { } owner)
        {
            await owner.Refs.DeleteAsync(this, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renames a local branch. Matches <c>git_branch_move</c>
    /// (branch.c:298-347).
    /// </summary>
    /// <remarks>
    /// Renames the ref (with reflog), then renames the
    /// <c>branch.&lt;oldname&gt;</c> config section to
    /// <c>branch.&lt;newname&gt;</c>.
    /// </remarks>
    /// <returns>The renamed branch reference.</returns>
    public async Task<GitReference> MoveAsync(string newBranchName, bool force = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newBranchName);

        if (!IsBranch)
        {
            // C (branch.c:48-54): not_a_local_branch → -1 (GIT_ERROR) with
            // class GIT_ERROR_INVALID and a trailing period.
            throw new GitException(
                GitErrorCode.Error,
                $"reference '{Name}' is not a local branch.",
                GitErrorCategory.Invalid);
        }

        string newRefName = GitReferences.RefsHeadsDir + newBranchName;
        byte[] logMessage = [.. "branch: renamed "u8, .. NameBytes.Span, .. " to "u8, .. Encoding.UTF8.GetBytes(newRefName)];

        GitRepository repo = Owner
            ?? throw new GitException(GitErrorCode.Invalid, "branch has no owner", GitErrorCategory.Reference);

        // First update ref, then config, so ref failure won't trash config.
        GitReference renamed = await repo.Refs.RenameAsync(this, newRefName, force, logMessage, cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<byte> oldShortName = NameBytes[GitReferences.RefsHeadsDir.Length..];
        await repo.Config.RenameSectionAsync((byte[])[.. "branch."u8, .. oldShortName.Span], Encoding.UTF8.GetBytes($"branch.{newBranchName}"), cancellationToken).ConfigureAwait(false);

        return renamed;
    }

    /// <summary>
    /// Sets or unsets the upstream for a local branch. Matches
    /// <c>git_branch_set_upstream</c> (branch.c:673-771).
    /// </summary>
    /// <param name="upstreamBranchName">The upstream branch name (short name),
    /// or null to unset the upstream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> if this reference is not a local branch.
    /// <see cref="GitErrorCode.NotFound"/> if the upstream branch cannot be found.
    /// </exception>
    public async Task SetUpstreamAsync(string? upstreamBranchName, CancellationToken cancellationToken = default)
    {
        if (!IsBranch)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                $"reference '{Name}' is not a local branch",
                GitErrorCategory.Reference);
        }

        GitRepository repo = Owner
            ?? throw new GitException(GitErrorCode.Invalid, "branch has no owner", GitErrorCategory.Reference);

        ReadOnlyMemory<byte> shortName = NameBytes[GitReferences.RefsHeadsDir.Length..];

        // Unsetting: delete branch.<name>.remote and branch.<name>.merge.
        if (upstreamBranchName is null)
        {
            await repo.Config.DeleteAsync((byte[])[.. "branch."u8, .. shortName.Span, .. ".remote"u8], cancellationToken).ConfigureAwait(false);
            await repo.Config.DeleteAsync((byte[])[.. "branch."u8, .. shortName.Span, .. ".merge"u8], cancellationToken).ConfigureAwait(false);
            return;
        }

        // Resolve the upstream to a local or remote branch.
        GitReference? upstream = await repo.BranchLookupAsync(upstreamBranchName, GitBranchType.Local, cancellationToken).ConfigureAwait(false);
        bool isLocal = upstream is not null;
        upstream ??= await repo.BranchLookupAsync(upstreamBranchName, GitBranchType.Remote, cancellationToken).ConfigureAwait(false);

        if (upstream is null)
        {
            throw new GitException(
                GitErrorCode.NotFound,
                $"cannot set upstream for branch '{shortName}'",
                GitErrorCategory.Reference);
        }

        // Local upstream: remote is ".", merge is the upstream refname.
        // Remote upstream: remote is the remote name, merge is the
        // reverse-transformed refspec.
        string remoteName;
        ReadOnlyMemory<byte> mergeRef;

        if (isLocal)
        {
            remoteName = ".";
            mergeRef = upstream.NameBytes;
        }
        else
        {
            // C (branch.c:557-626, git_branch__remote_name): the remote name is derived by matching the upstream ref against EVERY remote's fetch destination
            // refspec — NOT by path-splitting the remote-tracking name. More than one match is GIT_EAMBIGUOUS.
            string? foundRemote = null;
            GitRefSpec? foundSpec = null;
            byte[] upstreamNameBytes = upstream.NameBytes.ToArray();
            foreach (string candidate in await repo.RemoteListAsync(cancellationToken).ConfigureAwait(false))
            {
                // byte-domain fetch-spec read + byte refspec matching (C reads config values as raw bytes).
                IReadOnlyList<byte[]> fetchSpecs = await repo.Config.GetMultiBytesAsync($"remote.{candidate}.fetch", cancellationToken).ConfigureAwait(false);
                foreach (byte[] fetchSpecStr in fetchSpecs)
                {
                    var spec = GitRefSpec.Parse(fetchSpecStr, isFetch: true);
                    if (spec.DstMatches(upstreamNameBytes))
                    {
                        if (foundRemote is not null)
                        {
                            throw new GitException(
                                GitErrorCode.Ambiguous,
                                $"reference '{upstream.Name}' is ambiguous",
                                GitErrorCategory.Reference);
                        }

                        foundRemote = candidate;
                        foundSpec = spec;
                    }
                }
            }

            if (foundRemote is null || foundSpec is null)
            {
                throw new GitException(
                    GitErrorCode.NotFound,
                    $"could not determine remote for '{upstream.Name}'",
                    GitErrorCategory.Reference);
            }

            remoteName = foundRemote;

            // Reverse-transform the upstream refname through the matching
            // fetch refspec to get the merge value (refs/heads/<branch>).
            mergeRef = foundSpec.ReverseTransform(upstreamNameBytes);
        }

        await repo.Config.SetBytesAsync((byte[])[.. "branch."u8, .. shortName.Span, .. ".remote"u8], Encoding.UTF8.GetBytes(remoteName), cancellationToken).ConfigureAwait(false);
        await repo.Config.SetBytesAsync((byte[])[.. "branch."u8, .. shortName.Span, .. ".merge"u8], mergeRef, cancellationToken).ConfigureAwait(false);
    }
}
