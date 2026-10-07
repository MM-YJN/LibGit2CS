// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Revwalk;
using LibGit2CS.Transports;

namespace LibGit2CS.Remote;

/// <summary>
/// Coordinates the push operation: resolves push specs, queues objects
/// for the pack builder, calls the transport push, and updates local
/// tracking refs. Managed port of <c>src/libgit2/push.c</c>.
/// </summary>
internal sealed class PushCoordinator : IDisposable
{
    private readonly GitRemote _remote;
    private readonly List<GitPushSpec> _specs = [];
    private readonly List<GitPushUpdate> _updates = [];
    private readonly List<string> _pushOptions = [];
    private readonly bool _reportStatus = true;
    private GitPushResult? _result;
    private bool _disposed;

    /// <summary>Creates a push coordinator for the given remote.</summary>
    internal PushCoordinator(GitRemote remote)
    {
        _remote = remote;
    }

    /// <summary>The accumulated push statuses (available after <see cref="FinishAsync"/>).</summary>
    internal GitPushResult? Result => _result;

    /// <summary>The resolved push specs (available after <see cref="LibGit2CS.Remote.PushCoordinator.AddRefSpecAsync(string, System.Threading.CancellationToken)"/>).</summary>
    internal IReadOnlyList<GitPushSpec> Specs => _specs;

    /// <summary>
    /// Adds a push refspec. Parses the refspec, resolves the local source OID,
    /// and finds the matching remote head for the destination OID.
    /// Matches <c>git_push_add_refspec</c> (push.c:96-123,
    /// <c>parse_refspec</c> + <c>check_lref</c> + <c>check_rref</c>).
    /// </summary>
    internal async Task AddRefSpecAsync(string refspecStr, CancellationToken cancellationToken)
        => await AddRefSpecAsync(System.Text.Encoding.UTF8.GetBytes(refspecStr), cancellationToken).ConfigureAwait(false);

    /// <summary> Adds a push refspec from raw bytes. byte-parity surface — C parses the raw spec bytes (push.c:96-123). </summary>
    internal async Task AddRefSpecAsync(byte[] refspecBytes, CancellationToken cancellationToken)
    {
        GitRefSpec refspec;
        try
        {
            refspec = GitRefSpec.Parse(refspecBytes, isFetch: false);
        }
        catch (GitException)
        {
            // C (push.c:105-107): an unparseable push refspec fails with
            // GIT_ERROR_INVALID "invalid refspec %s".
            throw new GitException(GitErrorCode.Error, $"invalid refspec {System.Text.Encoding.UTF8.GetString(refspecBytes)}", GitErrorCategory.Invalid);
        }

        var spec = new GitPushSpec { RefSpec = refspec };

        // Resolve local source OID. C (push.c:109-115, check_lref): the
        // source must resolve to an existing object — an unresolvable src is
        // an error, not a deletion.
        if (!string.IsNullOrEmpty(refspec.Source))
        {
            // C's
            // check_lref uses git_revparse_single (push.c:96-100), accepting
            // revparse expressions (HEAD~1, master^{}, short OIDs) — the
            // port's plain ref lookup rejected them.
            GitObject? obj;
            try
            {
                obj = await _remote.Owner.RevparseSingleAsync(refspec.Source, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.InvalidSpec)
            {
                throw new GitException(GitErrorCode.Error, $"not a valid reference '{refspec.Source}'", GitErrorCategory.Invalid);
            }

            if (obj is null)
            {
                throw new GitException(GitErrorCode.Error, $"src refspec '{refspec.Source}' does not match any existing object", GitErrorCategory.Reference);
            }

            using (obj)
            {
                spec.Loid = obj.Id;
            }
        }

        // C (push.c:117-120, check_rref): the destination must start with
        // "refs/".
        if (!refspec.Destination.StartsWith("refs/", StringComparison.Ordinal))
        {
            throw new GitException(GitErrorCode.Error, $"not a valid reference '{refspec.Destination}'", GitErrorCategory.Invalid);
        }

        // Find matching remote head for destination OID
        IReadOnlyList<GitRemoteHead> remoteHeads = await _remote.LsAsync(cancellationToken).ConfigureAwait(false);
        foreach (GitRemoteHead head in remoteHeads)
        {
            if (head.Name == refspec.Destination)
            {
                spec.Roid = head.Oid;
                break;
            }
        }

        _specs.Add(spec);
    }

    /// <summary> Executes the push: validates capabilities, does non-FF checks, queues objects for the pack, calls the transport push, and collects status.
    /// Matches <c>git_push_finish</c>. </summary> <param name="transport">The connected transport.</param> <param name="callbacks">Remote callbacks.</param>
    /// <param name="pushOptions">Push option strings.</param> <param name="pbParallelism">Packbuilder thread count (C's <c>git_push_options.pb_parallelism</c>,
    /// push.c:460).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task FinishAsync(IGitTransport transport, GitRemoteCallbacks? callbacks, IReadOnlyList<string>? pushOptions, int pbParallelism = 1, CancellationToken cancellationToken = default)
    {
        if (!transport.IsConnected)
        {
            // C (push.c:517-520): GIT_ERROR_NET "remote is disconnected", return -1 → code Error, category Net.
            throw new GitException(GitErrorCode.Error, "remote is disconnected", GitErrorCategory.Net);
        }

        // Copy push options
        if (pushOptions is not null)
        {
            _pushOptions.AddRange(pushOptions);
        }

        // Check push-options support for EVERY transport via its capabilities (C push.c:526-531: git_remote_capabilities + GIT_REMOTE_CAPABILITY_PUSH_OPTIONS;
        // the non-smart/local transports do not advertise push-options, so the check fails there too).
        bool hasPushOptions = _pushOptions.Count > 0;
        if (hasPushOptions && (transport.Capabilities & GitRemoteCapability.PushOptions) == 0)
        {
            // C (push.c:528-530): GIT_ERROR_INVALID "push-options not
            // supported by remote", return -1 → code Error, category Invalid.
            throw new GitException(GitErrorCode.Error, "push-options not supported by remote", GitErrorCategory.Invalid);
        }

        // Build push updates (remote OID resolution only — calculate_work, push.c:397-435). C runs the non-FF checks in queue_objects AFTER the negotiation
        // callback (push.c:469-487), so a non-FF push still invokes the callback.
        IReadOnlyList<GitRemoteHead> remoteHeads = await _remote.LsAsync(cancellationToken).ConfigureAwait(false);
        foreach (GitPushSpec spec in _specs)
        {
            // Find the remote OID for this spec's destination
            GitOid roid = default;
            foreach (GitRemoteHead head in remoteHeads)
            {
                if (head.Name == spec.RefSpec.Destination)
                {
                    roid = head.Oid;
                    spec.Roid = roid;
                    break;
                }
            }

            // C (push.c:389-390, remote.h:512-519): src = "current target of the reference" = roid (remote OLD); dst = "new target" = loid (local NEW).
            _updates.Add(new GitPushUpdate
            {
                SrcRefName = spec.RefSpec.Source,
                DstRefName = spec.RefSpec.Destination,
                Src = roid,
                Dst = spec.Loid,
            });
        }

        // Call push negotiation callback. C (push.c:469-487): the callback runs BEFORE queue_objects (the non-FF checks); a nonzero return is wrapped as
        // GIT_ERROR_CALLBACK.
        if (callbacks?.PushNegotiation is { } negotiation)
        {
            if (!await negotiation(_updates, cancellationToken).ConfigureAwait(false))
            {
                throw new GitException(GitErrorCode.User, "push aborted by negotiation callback", GitErrorCategory.Callback);
            }
        }

        // Non-FF checks (queue_objects, push.c:343-359) — run AFTER the negotiation callback, matching C's order.
        foreach (GitPushSpec spec in _specs)
        {
            GitOid roid = spec.Roid;

            // Non-FF check: if not force and remote has this ref, loid must
            // be a descendant of roid. Skip up-to-date refs (loid == roid)
            // — queue_objects does `continue` on these before the non-FF
            // check (push.c:309-310), and git_graph_descendant_of returns 0
            // when commit == ancestor (graph.c:179), so without this guard
            // an up-to-date push would be falsely rejected as non-FF.
            // Before the descendant check, queue_objects gates with
            // git_odb_exists(roid) (push.c:343-348): if the remote's tip is
            // absent from the local object database, the push is classified
            // as non-fast-forward rather than letting the merge-base walk
            // surface a "commit not found" error (git_graph_reachable_from_any
            // treats a missing commit_id as a hard error — graph.c:185-249).
            if (!spec.RefSpec.Force && !roid.IsZero && !spec.Loid.IsZero && spec.Loid != roid)
            {
                if (!await _remote.Owner.Objects.ExistsAsync(roid, cancellationToken).ConfigureAwait(false))
                {
                    throw new GitException(GitErrorCode.NonFastForward,
                        "cannot push because a reference that you are trying to update on the remote contains commits that are not present locally.",
                        GitErrorCategory.Reference);
                }

                if (!await _remote.Owner.DescendantOfAsync(spec.Loid, roid, cancellationToken).ConfigureAwait(false))
                {
                    // C (push.c:343-359, queue_objects): "cannot push
                    // non-fastforwardable reference" (GIT_ERROR_REFERENCE).
                    throw new GitException(GitErrorCode.NonFastForward,
                        "cannot push non-fastforwardable reference", GitErrorCategory.Reference);
                }
            }
        }

        // A pack-file MUST be sent if either create or update command
        // is used, even if the server already has all the necessary
        // objects. In this case the client MUST send an empty pack-file.
        // (push.c:451-455, smart_protocol.c:1226-1230)
        GitPackWriter? packWriter = null;
        bool needPack = false;
        foreach (GitPushSpec spec in _specs)
        {
            if (!spec.Loid.IsZero)
            {
                needPack = true;
                break;
            }
        }

        if (needPack)
        {
            packWriter = await BuildPackAsync(pbParallelism, cancellationToken).ConfigureAwait(false);
        }

        // Call the transport push
        _result = await transport.PushAsync(
            _remote.Owner,
            _specs,
            packWriter,
            callbacks,
            _reportStatus,
            hasPushOptions ? _pushOptions : null,
            cancellationToken).ConfigureAwait(false);

        // C (push.c:537-540, git_push_finish): a failed remote unpack fails
        // the whole push with GIT_ERROR_NET "unpacking the sent packfile
        // failed on the remote".
        if (_result is not null && !_result.UnpackOk)
        {
            throw new GitException(GitErrorCode.Error, "unpacking the sent packfile failed on the remote", GitErrorCategory.Net);
        }

        packWriter?.Dispose();
    }

    /// <summary> Builds the pack writer by queuing all objects reachable from the push specs that the remote doesn't already have. Matches
    /// <c>queue_objects</c>; the thread count mirrors C's <c>git_packbuilder_set_threads</c> (push.c:460). </summary>
    private async Task<GitPackWriter> BuildPackAsync(int threads, CancellationToken cancellationToken)
    {
        var pb = new GitPackWriter(_remote.Owner, threads);

        using var walk = new GitRevWalker(_remote.Owner);
        walk.Sort = GitSortMode.Time;

        bool hasCommits = false;

        foreach (GitPushSpec spec in _specs)
        {
            if (spec.Loid.IsZero || spec.Loid == spec.Roid)
            {
                continue;
            }

            GitObject? obj = await _remote.Owner.Objects.LookupAsync(spec.Loid, cancellationToken).ConfigureAwait(false);
            if (obj is null)
            {
                // C's
                // queue_objects fails the whole push on a missing source
                // object (push.c:309-311) instead of sending a pack missing
                // the object (the server then rejects it, or worse accepts it
                // for an already-present object).
                throw new GitException(
                    GitErrorCode.Error,
                    $"cannot find the object {spec.Loid} to push",
                    GitErrorCategory.Object);
            }

            if (obj is GitTag tag)
            {
                // C (push.c:257-282, enqueue_tag): insert EVERY tag in the
                // tag->tag->... chain, then push the final peeled target onto
                // the walk (commits) or insert it (non-commits).
                GitOid current = tag.Id;
                GitObject? currentObj = tag;
                while (currentObj is GitTag chainTag)
                {
                    await pb.InsertAsync(chainTag.Id, cancellationToken).ConfigureAwait(false);
                    GitObject? next = await _remote.Owner.Objects.LookupAsync(chainTag.Target, cancellationToken).ConfigureAwait(false);
                    if (next is null)
                    {
                        break;
                    }

                    if (currentObj != tag)
                    {
                        currentObj.Dispose();
                    }

                    currentObj = next;
                    current = chainTag.Target;
                }

                if (currentObj is Commit)
                {
                    await walk.PushAsync(current, cancellationToken).ConfigureAwait(false);
                    hasCommits = true;
                }
                else if (currentObj is not null)
                {
                    await pb.InsertAsync(current, cancellationToken).ConfigureAwait(false);
                }

                if (currentObj is not null && currentObj != tag)
                {
                    currentObj.Dispose();
                }

                // `obj` (the outer tag) is disposed by the caller.
            }
            else if (obj is Commit)
            {
                await walk.PushAsync(spec.Loid, cancellationToken).ConfigureAwait(false);
                hasCommits = true;
            }
            else
            {
                await pb.InsertAsync(spec.Loid, cancellationToken).ConfigureAwait(false);
            }

            obj.Dispose();
        }

        // Hide commits the remote already has
        IReadOnlyList<GitRemoteHead> remoteHeads = await _remote.LsAsync(cancellationToken).ConfigureAwait(false);
        foreach (GitRemoteHead head in remoteHeads)
        {
            if (head.Name.EndsWith("^{}", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                await walk.HideAsync(head.Oid, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException)
            {
                // Not a commit — skip
            }
        }

        if (hasCommits)
        {
            await pb.InsertWalkAsync(walk, cancellationToken).ConfigureAwait(false);
        }

        return pb;
    }

    /// <summary>
    /// Updates local tracking refs after a successful push. For each
    /// successful ref update, finds the matching fetch refspec and
    /// creates/updates/deletes the local tracking branch.
    /// Matches <c>git_push_update_tips</c>.
    /// </summary>
    internal async Task UpdateTipsAsync(GitRemoteCallbacks? _, string? reflogMessage = null, CancellationToken cancellationToken = default)
    {
        if (_result is null)
        {
            return;
        }

        string logMsg = reflogMessage ?? "push";

        foreach (GitPushStatus status in _result.Status)
        {
            if (!status.Ok)
            {
                continue;
            }

            // Find the push spec for this ref
            GitPushSpec? matchingSpec = null;
            foreach (GitPushSpec spec in _specs)
            {
                if (spec.RefSpec.Destination == status.Ref)
                {
                    matchingSpec = spec;
                    break;
                }
            }

            if (matchingSpec is null)
            {
                continue;
            }

            // Find a fetch refspec that matches this remote ref
            foreach (GitRefSpec fetchSpec in _remote.RefSpecs)
            {
                if (!fetchSpec.IsFetch)
                {
                    continue;
                }

                if (!fetchSpec.SrcMatches(status.Ref))
                {
                    continue;
                }

                string localRef = fetchSpec.Transform(status.Ref);

                if (matchingSpec.Loid.IsZero)
                {
                    // Delete the local tracking ref
                    try
                    {
                        await _remote.Owner.Refs.DeleteAsync(localRef, cancellationToken).ConfigureAwait(false);
                    }
                    catch (GitException) { }
                }
                else
                {
                    // Update or create the local tracking ref
                    GitReference? existing = await _remote.Owner.Refs.ResolveAsync(localRef, cancellationToken).ConfigureAwait(false);
                    if (existing is null)
                    {
                        await _remote.Owner.Refs.CreateAsync(localRef, matchingSpec.Loid, force: true, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
                    }
                    else if (existing is GitDirectReference dr && dr.Target != matchingSpec.Loid)
                    {
                        await _remote.Owner.Refs.SetTargetAsync(existing, matchingSpec.Loid, logMessage: logMsg, cancellationToken).ConfigureAwait(false);
                    }
                }

                break;
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

        _specs.Clear();
        _updates.Clear();
        _pushOptions.Clear();
        _disposed = true;
    }
}
