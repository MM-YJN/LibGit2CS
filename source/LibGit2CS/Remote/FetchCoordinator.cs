// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.Remote;

/// <summary>
/// Coordinates the fetch operation: filters wants, marks local refs,
/// drives negotiation, and downloads the pack. Managed port of
/// <c>src/libgit2/fetch.c</c>.
/// </summary>
internal static class FetchCoordinator
{
    /// <summary>
    /// Check if a remote head matches refspecs or tag policy and should be
    /// included in the wants list. Matches <c>maybe_want</c> in <c>fetch.c:24</c>.
    /// </summary>
    internal static bool MaybeWant(
        GitRemoteHead head,
        IReadOnlyList<GitRefSpec> refspecs,
        GitRefSpec tagSpec,
        GitAutoTagOption tagOpt)
    {
        // C (fetch.c:28-32): maybe_want gates on the FULL git_reference_name_is_valid check (refs.c:1367-1370, which uses ALLOW_ONELEVEL — "HEAD" is valid),
        // so malformed advertised names never become want candidates.
        if (GitReferences.NormalizeName(head.Name, GitReferenceFormatFlags.AllowOneLevel) is null)
        {
            return false;
        }

        // All tags if tagopt is All. C (fetch.c:35-41): a tag matched by the
        // ALL-tags tag spec is wanted REGARDLESS of negative refspecs — the
        // matching_refspec NULL result is only consulted when match is 0.
        if (tagOpt == GitAutoTagOption.All && tagSpec.SrcMatches(head.Name))
        {
            return true;
        }

        // C (remote.c, git_remote__matching_refspec): a linear scan over the
        // active FETCH refspecs — a matching NEGATIVE refspec excludes the
        // head immediately (returns NULL), and the first matching positive
        // spec (in list order) makes it wanted.
        bool positive = false;
        foreach (GitRefSpec spec in refspecs)
        {
            if (!spec.IsFetch)
            {
                continue;
            }

            if (spec.SrcMatchesNegative(head.Name))
            {
                return false;
            }

            if (!positive && spec.SrcMatches(head.Name))
            {
                positive = true;
            }
        }

        return positive;
    }

    /// <summary> Filter remote heads against refspecs and tag policy to produce the wants list. Marks heads whose objects we already have locally and sets <see
    /// cref="GitRemote.NeedPack"/> when any matched head's object is missing locally (or the fetch is shallow). Matches <c>filter_wants</c> in
    /// <c>fetch.c:97</c> combined with <c>mark_local</c> in <c>fetch.c:52</c>. Also handles explicitly specified OID refspecs via <c>maybe_want_oid</c>
    /// (fetch.c:142-155): a hex-OID source requires the <see cref="GitRemoteCapability.TipOid"/>/<see cref="GitRemoteCapability.ReachableOid"/> capabilities,
    /// else GIT_ERROR_INVALID "cannot fetch a specific object from the remote repository". </summary>
    internal static async Task<List<GitRemoteHead>> FilterWantsAsync(
        IReadOnlyList<GitRemoteHead> remoteHeads,
        IReadOnlyList<GitRefSpec> refspecs,
        GitAutoTagOption tagOpt,
        GitRemote remote,
        int depth,
        GitRemoteCapability capabilities,
        CancellationToken cancellationToken)
    {
        GitObjectDb odb = remote.Owner.Objects;
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);
        var wants = new List<GitRemoteHead>();

        foreach (GitRemoteHead head in remoteHeads)
        {
            // C's maybe_want does NOT skip the symref HEAD — it validates the
            // name and matches it against the (dwimmed) refspecs, so a
            // no-refspec fetch wants the remote's HEAD.
            if (MaybeWant(head, refspecs, tagSpec, tagOpt))
            {
                // Check if we already have this object. A shallow fetch
                // (depth != 0) always needs the pack, mirroring mark_local's
                // `nego.depth == GIT_FETCH_DEPTH_FULL` guard (fetch.c:66-70).
                bool isLocal = depth == 0 && await odb.ExistsAsync(head.Oid, cancellationToken).ConfigureAwait(false);

                // mark_local: if not local, we need a pack (fetch.c:70)
                if (!isLocal)
                {
                    remote.NeedPack = true;
                }

                wants.Add(head with { Local = isLocal });
            }
        }

        // C (fetch.c:142-155): explicitly specified OID refspecs
        // (maybe_want_oid). The local repo's OID type sizes the check.
        GitHashAlgorithmKind oidType = remote.Owner.ObjectFormat;
        int oidHexSize = GitOid.HexSizeFor(oidType);
        const GitRemoteCapability oidMask = GitRemoteCapability.TipOid | GitRemoteCapability.ReachableOid;
        foreach (GitRefSpec spec in refspecs)
        {
            if (!spec.IsFetch || spec.Source.Length != oidHexSize ||
                !GitOid.TryParse(spec.Source, oidType, out GitOid oid))
            {
                continue;
            }

            if ((capabilities & oidMask) == 0)
            {
                // C (fetch.c:147-151): GIT_ERROR_INVALID.
                throw new GitException(GitErrorCode.Error,
                    "cannot fetch a specific object from the remote repository",
                    GitErrorCategory.Invalid);
            }

            // maybe_want_oid: a synthetic head named after the spec's dst
            // (or the OID when there is none); mark_local applies as usual.
            bool isLocal = depth == 0 && await odb.ExistsAsync(oid, cancellationToken).ConfigureAwait(false);
            if (!isLocal)
            {
                remote.NeedPack = true;
            }

            wants.Add(new GitRemoteHead(
                Local: isLocal,
                Oid: oid,
                LocalOid: default,
                Name: spec.Destination.Length > 0 ? spec.Destination : spec.Source,
                SymrefTarget: null));
        }

        return wants;
    }

    /// <summary>
    /// Drive the fetch negotiation: filter wants, set up negotiation params,
    /// and call <see cref="IGitTransport.NegotiateFetchAsync"/>. Matches
    /// <c>git_fetch_negotiate</c> in <c>fetch.c:170</c>.
    /// </summary>
    internal static async Task NegotiateAsync(
        IGitTransport transport,
        GitRemote remote,
        IReadOnlyList<GitRemoteHead> remoteHeads,
        IReadOnlyList<GitRefSpec> refspecs,
        GitAutoTagOption tagOpt,
        int depth,
        IReadOnlyList<GitOid> shallowRoots,
        CancellationToken cancellationToken)
    {
        GitRepository repo = remote.Owner;

        // Reset before filtering (mirrors remote->need_pack = 0; in fetch.c:175)
        remote.NeedPack = false;

        List<GitRemoteHead> wants = await FilterWantsAsync(remoteHeads, refspecs, tagOpt, remote, depth, transport.Capabilities, cancellationToken).ConfigureAwait(false);

        // Don't try to negotiate when we don't want anything (fetch.c:186)
        if (!remote.NeedPack)
        {
            return;
        }

        var negotiation = new GitFetchNegotiation(wants, shallowRoots, depth);
        await transport.NegotiateFetchAsync(repo, negotiation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drive the pack download and write shallow roots. Matches
    /// <c>git_fetch_download_pack</c> in <c>fetch.c:210</c>.
    /// </summary>
    /// <param name="transport">The transport to download from.</param>
    /// <param name="remote">The remote whose <see cref="GitRemote.NeedPack"/> flag gates the call.</param>
    /// <param name="stats">Progress accumulator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task DownloadPackAsync(
        IGitTransport transport,
        GitRemote remote,
        GitIndexerProgress stats,
        CancellationToken cancellationToken)
    {
        GitRepository repo = remote.Owner;

        // git_fetch_download_pack: if (!remote->need_pack) return 0;
        // (fetch.c:216) — short-circuit when negotiation determined there
        // is nothing to fetch. Without this, we would block forever in
        // GitSmartProtocol.DownloadPackAsync waiting for a pack the server
        // was never asked to send.
        if (!remote.NeedPack)
        {
            return;
        }

        await transport.DownloadPackAsync(repo, stats, cancellationToken).ConfigureAwait(false);

        // Write shallow roots. C (fetch.c:210-225): the shallow file is
        // rewritten unconditionally after the pack download —
        // git_repository__shallow_roots_write removes the file when the
        // root set is empty (repository.c:3837-3838), so a fetch that fully
        // unshallows the repo deletes the stale .git/shallow.
        IReadOnlyList<GitOid> shallowRoots = await transport.ShallowRootsAsync(cancellationToken).ConfigureAwait(false);
        await Grafts.WriteShallowAsync(repo.Path, shallowRoots, cancellationToken).ConfigureAwait(false);
    }
}
