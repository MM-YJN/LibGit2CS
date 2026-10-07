// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.Transports;

/// <summary>
/// Local filesystem transport for <c>file://</c> URLs.
/// Managed port of <c>transport_local</c> in <c>src/libgit2/transports/local.c</c>.
/// </summary>
/// <remarks>
/// Implements <see cref="IGitTransport"/> directly (not <see cref="IGitSubtransport"/> +
/// <see cref="GitSmartTransport"/>) because local transport bypasses the smart
/// protocol entirely — there is no pkt-line wire format. Instead, refs are
/// read directly from the source repository's ref database, and objects are
/// copied directly from the source ODB to the client ODB.
/// <para>
/// <b>DownloadPack</b> uses pack-based copy: collect missing objects from the
/// source repo (via <see cref="GitRevWalker"/> + <see cref="GitTree.WalkAsync"/>),
/// then build a <c>.pack</c> + <c>.idx</c> v2 pair via <see cref="GitPackWriter"/> +
/// <see cref="GitPackIndexer"/> and write them to the client's <c>objects/pack/</c>
/// directory. Matches C's <c>local_download_pack</c> (<c>local.c:583</c>) which
/// uses <c>git_packbuilder_write</c>. Objects land in a single pack file, not
/// as loose objects.
/// </para>
/// <para>
/// <b>Push</b> writes a pack into the target's <c>objects/pack/</c> (via
/// <see cref="GitPackWriter"/> + <see cref="GitPackIndexer"/> — C's
/// <c>git_packbuilder_write</c> in <c>local_push</c>) and updates the target
/// refs directly. Pushing to a non-bare repository fails with
/// <see cref="GitErrorCode.BareRepo"/> (C's <c>GIT_EBAREREPO</c>).
/// </para>
/// </remarks>
public sealed class GitLocalTransport : IGitTransport
{
    private GitRepository? _sourceRepo;
    private readonly List<GitRemoteHead> _refs = [];
    private List<GitRemoteHead> _negotiationWants = [];
    private string? _url;
    private readonly GitContext _context;

    /// <summary>
    /// The connected URL, throwing if <see cref="ConnectAsync"/> has not set it.
    /// </summary>
    private string ConnectedUrl => _url ?? throw new InvalidOperationException("transport is not connected");
    private GitDirection _direction;
    private GitRemoteConnectOptions? _connectOptions;
    private volatile bool _connected;
    private volatile bool _cancelled;
    private bool _everConnected;
    private bool _disposed;

    /// <summary>Creates a new local transport.</summary>
    internal GitLocalTransport(GitContext context)
    {
        _context = context;
    }

    /// <inheritdoc/>
    public GitRemoteCapability Capabilities => GitRemoteCapability.TipOid | GitRemoteCapability.ReachableOid;

    /// <inheritdoc/>
    public GitHashAlgorithmKind OidType => _sourceRepo?.ObjectFormat ?? GitHashAlgorithmKind.Sha1;

    /// <inheritdoc/>
    public bool IsConnected => _connected;

    /// <inheritdoc/>
    public async Task ConnectAsync(string url, GitDirection direction, GitRemoteConnectOptions? options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connected)
        {
            return;
        }

        _url = url;
        _direction = direction;
        _connectOptions = options;

        // Convert file:// URL to filesystem path
        string path = GitUrlUtils.LocalPathFromUrl(url);

        // Open the source repository
        _sourceRepo = await GitRepository.OpenAsync(path, _context, cancellationToken).ConfigureAwait(false);

        // Enumerate refs
        await StoreRefsAsync(cancellationToken).ConfigureAwait(false);

        _connected = true;
        _everConnected = true;
    }

    /// <inheritdoc/>
    public void SetConnectOptions(GitRemoteConnectOptions? options)
    {
        if (!_connected)
        {
            throw new GitException(GitErrorCode.Invalid, "cannot reconfigure a transport that is not connected", GitErrorCategory.Net);
        }

        _connectOptions = options;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GitRemoteHead>> LsAsync(CancellationToken cancellationToken = default)
    {
        // Allow returning cached refs even after disconnect — Remote.UpdateTips
        // calls LsAsync() after Disconnect() (matching the smart transport behavior
        // where refs are cached after Connect and remain available).
        // For empty source repos, _refs may be empty — that's valid.
        if (!_everConnected)
        {
            throw new GitException(GitErrorCode.Invalid, "the transport is not connected", GitErrorCategory.Net);
        }

        return Task.FromResult<IReadOnlyList<GitRemoteHead>>(_refs);
    }

    /// <inheritdoc/>
    public async Task NegotiateFetchAsync(GitRepository repo, GitFetchNegotiation wants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(wants);

        if (!_connected)
        {
            throw new GitException(GitErrorCode.Invalid, "the transport is not connected", GitErrorCategory.Net);
        }

        // Reject shallow fetch
        if (wants.Depth > 0)
        {
            throw new GitException(GitErrorCode.NotSupported, "shallow fetch is not supported by the local transport", GitErrorCategory.Net);
        }

        // C (fetch.c:142-155 + local.c): OID-refspec wants (maybe_want_oid) become heads in remote->refs and local_download_pack copies their objects. The
        // transport keeps the negotiation wants so the download phase can serve them.
        _negotiationWants = [.. wants.Refs];

        // For each remote head, check if the client already has the object.
        // Populate LocalOid from the client's refs (matching C's local_negotiate_fetch).
        for (int i = 0; i < _refs.Count; i++)
        {
            GitRemoteHead head = _refs[i];

            // Skip peeled tag entries — they don't correspond to client refs
            if (head.Name.EndsWith("^{}", StringComparison.Ordinal))
            {
                continue;
            }

            // Try to resolve the ref name in the client repo
            GitOid localOid = default;
            bool isLocal = false;

            GitReference? resolved = await repo.Refs.ResolveAsync(head.Name, cancellationToken).ConfigureAwait(false);
            if (resolved is GitDirectReference dr)
            {
                localOid = dr.Target;
                // A same-named client ref only counts as "local" when the OIDs agree.
                // Note: C's local_negotiate_fetch never compares OIDs (it only fills loid
                // and local_download_pack pushes every head into the walk; the Local/skip
                // design is C#-specific). The equality guard is needed so a trailing local
                // branch does not suppress the pack download for new remote commits.
                isLocal = !head.Oid.IsZero && head.Oid.Equals(localOid)
                    && await repo.Objects.ExistsAsync(localOid, cancellationToken).ConfigureAwait(false);
            }

            if (!isLocal)
            {
                // Check if the object exists directly (e.g. via a different ref name)
                isLocal = await repo.Objects.ExistsAsync(head.Oid, cancellationToken).ConfigureAwait(false);
                if (isLocal)
                {
                    localOid = head.Oid;
                }
            }

            _refs[i] = head with { Local = isLocal, LocalOid = localOid };
        }
    }

    /// <inheritdoc/>
    // CA1506: this method is a faithful port of C's local_download_pack,
    // which touches every object type the local transport serves; the
    // coupling count mirrors the C function and is not worth splitting.
#pragma warning disable CA1506
    public async Task DownloadPackAsync(GitRepository repo, GitIndexerProgress stats, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(stats);

        if (!_connected)
        {
            throw new GitException(GitErrorCode.Invalid, "the transport is not connected", GitErrorCategory.Net);
        }

        if (_sourceRepo is null)
        {
            throw new GitException(GitErrorCode.Invalid, "no source repository", GitErrorCategory.Net);
        }

        GitObjectDb sourceObjects = _sourceRepo.Objects;
        GitObjectDb clientObjects = repo.Objects;
        IProgress<GitTransferProgress>? transferProgress = _connectOptions?.Callbacks?.TransferProgress;

        // Reset the shared accumulator in place (matches C's memset(stats, 0, ...)
        // at the top of git_smart__download_pack; the local transport path in
        // libgit2's local.c likewise starts from a zeroed stats struct).
        stats.TotalObjects = 0;
        stats.IndexedObjects = 0;
        stats.ReceivedObjects = 0;
        stats.LocalObjects = 0;
        stats.TotalDeltas = 0;
        stats.IndexedDeltas = 0;
        stats.ReceivedBytes = 0;

        // Collect all OIDs to copy: walk from each non-local remote head
        var objectsToCopy = new HashSet<GitOid>();
        var walk = new GitRevWalker(_sourceRepo)
        {
            Sort = GitSortMode.Time
        };

        bool hasCommits = false;
        foreach (GitRemoteHead head in AllHeadsToDownload())
        {
            if (head.Local)
            {
                continue;
            }

            // Skip peeled tag entries — the tag object itself is already included
            if (head.Name.EndsWith("^{}", StringComparison.Ordinal))
            {
                continue;
            }

            // Check what type of object this is
            GitObject? obj = await sourceObjects.LookupAsync(head.Oid, cancellationToken).ConfigureAwait(false);
            if (obj is null)
            {
                continue;
            }

            if (obj is Commit)
            {
                await walk.PushAsync(head.Oid, cancellationToken).ConfigureAwait(false);
                hasCommits = true;
            }
            else
            {
                // Tag, blob, or tree — insert directly
                objectsToCopy.Add(head.Oid);
                if (obj is GitTag tag)
                {
                    // Also collect the tag's target chain
                    await CollectTagObjectsAsync(sourceObjects, tag.Target, objectsToCopy, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Walk commits and collect their trees
        if (hasCommits)
        {
            // Hide objects the client already has — only hide commits (RevWalker
            // only walks commits; hiding non-commits throws "not a committish").
            await foreach (GitOid existingOid in clientObjects.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                GitObject? existingObj = await clientObjects.LookupAsync(existingOid, cancellationToken).ConfigureAwait(false);
                if (existingObj is Commit)
                {
                    // Only hide commits the SOURCE repo also has: a client-only commit (local
                    // divergence not pushed) is unreachable from the source walk and HideAsync
                    // would throw NotFound (C local.c treats this as a no-op).
                    if (await sourceObjects.ExistsAsync(existingOid, cancellationToken).ConfigureAwait(false))
                    {
                        await walk.HideAsync(existingOid, cancellationToken).ConfigureAwait(false);
                    }
                }

                existingObj?.Dispose();
            }

            await foreach (GitOid commitOid in walk.WalkAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_cancelled)
                {
                    throw new GitException(GitErrorCode.User, "the fetch was cancelled by the user", GitErrorCategory.Net);
                }

                objectsToCopy.Add(commitOid);

                Commit? commit = await sourceObjects.LookupAsync<Commit>(commitOid, cancellationToken).ConfigureAwait(false);
                if (commit is null)
                {
                    continue;
                }

                // Walk the tree and collect all blob/tree OIDs
                GitTree? tree = await sourceObjects.LookupAsync<GitTree>(commit.Tree, cancellationToken).ConfigureAwait(false);
                if (tree is not null)
                {
                    objectsToCopy.Add(tree.Id);
                    await foreach ((_, GitTreeEntry entry) in tree.WalkAsync(GitTreeWalkMode.PreOrder, cancellationToken).ConfigureAwait(false))
                    {
                        if (_cancelled)
                        {
                            throw new GitException(GitErrorCode.User, "the fetch was cancelled by the user", GitErrorCategory.Net);
                        }

                        if (entry.IsTree)
                        {
                            objectsToCopy.Add(entry.Id);
                        }
                        else if (!entry.IsGitLink)
                        {
                            objectsToCopy.Add(entry.Id);
                        }
                    }
                }
            }
        }

        walk.Dispose();

        // Build a pack from the collected objects and write it to the client's
        // objects/pack/ directory (matching C's local_download_pack: git_packbuilder_write).
        // Objects land in a .pack + .idx v2 pair, not as loose files.
        if (objectsToCopy.Count == 0)
        {
            return;
        }

        if (_cancelled)
        {
            throw new GitException(GitErrorCode.User, "the fetch was cancelled by the user", GitErrorCategory.Net);
        }

        // Filter out objects the client already has
        var toInsert = new List<GitOid>(objectsToCopy.Count);
        foreach (GitOid oid in objectsToCopy)
        {
            if (!await clientObjects.ExistsAsync(oid, cancellationToken).ConfigureAwait(false))
            {
                toInsert.Add(oid);
            }
        }

        if (toInsert.Count == 0)
        {
            return;
        }

        int total = toInsert.Count;
        int inserted = 0;

        // Publish the total to the shared accumulator (the local transport
        // knows the count up front, unlike the smart-protocol path which
        // learns it from the pack header).
        stats.TotalObjects = total;

        // C (local.c:637-667, local_download_pack): "Counting objects" is emitted via sideband_progress after the walk (twice — the format string carries "\r"
        // and the second message appends "\n"), then "Compressing objects: X% (i/n)" per object during the write.
        IProgress<string>? sidebandProgress = _connectOptions?.Callbacks?.SidebandProgress;
        if (sidebandProgress is not null && total > 0)
        {
            sidebandProgress.Report($"Counting objects {total}\r");
            sidebandProgress.Report($"Counting objects {total}\r\n");
        }

        var pb = new GitPackWriter(_sourceRepo);
        try
        {
            foreach (GitOid oid in toInsert)
            {
                if (_cancelled)
                {
                    throw new GitException(GitErrorCode.User, "the fetch was cancelled by the user", GitErrorCategory.Net);
                }

                await pb.InsertAsync(oid, cancellationToken).ConfigureAwait(false);
                inserted++;

                // Mirror the per-object report into the shared accumulator so
                // remote.Stats reflects the local fetch (the smart-protocol
                // path does this via the indexer writing to the same stats).
                stats.ReceivedObjects = inserted;
                stats.IndexedObjects = inserted;

                transferProgress?.Report(new GitTransferProgress(
                        TotalObjects: total,
                        IndexedObjects: inserted,
                        ReceivedObjects: inserted,
                        LocalObjects: 0,
                        TotalDeltas: 0,
                        IndexedDeltas: 0,
                        ReceivedBytes: 0));
            }

            // Bridge TransferProgress → PackProgress for the pack write phase
            IProgress<GitPackProgress>? packProgress = null;
            if (transferProgress is not null)
            {
                packProgress = new Progress<GitPackProgress>(p =>
                {
                    transferProgress.Report(new GitTransferProgress(
                        TotalObjects: total,
                        IndexedObjects: total,
                        ReceivedObjects: total,
                        LocalObjects: 0,
                        TotalDeltas: 0,
                        IndexedDeltas: 0,
                        ReceivedBytes: 0));
                });
            }

            // C (local.c:526-551, local_counting): during the pack write the
            // sideband reports "Compressing objects: X% (i/n)" + ", done\n"
            // on completion (else "\r") — local.c:517-518, 533-536.
            if (sidebandProgress is not null && total > 0)
            {
                IProgress<GitPackProgress>? sidebandBridge = packProgress;
                packProgress = new Progress<GitPackProgress>(p =>
                {
                    sidebandBridge?.Report(p);
                    double perc = ((double)p.Current / Math.Max(1, p.Total)) * 100;
                    string suffix = p.Current == p.Total ? ", done\n" : "\r";
                    sidebandProgress.Report($"Compressing objects: {perc.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}% ({p.Current}/{p.Total}){suffix}");
                });
            }

            string packDir = Path.Join(repo.Path, "objects", "pack");
            await pb.WriteToDirectoryAsync(packDir, packProgress, cancellationToken).ConfigureAwait(false);

            // C (local.c:509-515, 677-688): the local download accumulates
            // received_bytes over the pack chunks fed to the writepack, so
            // the final value is the written pack size (not 0).
            string? writtenPack = Directory.EnumerateFiles(packDir, "pack-*.pack")
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
            if (writtenPack is not null)
            {
                stats.ReceivedBytes = new FileInfo(writtenPack).Length;
                transferProgress?.Report(new GitTransferProgress(
                    TotalObjects: total,
                    IndexedObjects: total,
                    ReceivedObjects: total,
                    LocalObjects: 0,
                    TotalDeltas: 0,
                    IndexedDeltas: 0,
                    ReceivedBytes: stats.ReceivedBytes));
            }
        }
        finally
        {
            pb.Dispose();
        }

        // Refresh the client repo's pack backends so the newly-written pack
        // is visible to Lookups. The pack file was written to
        // <repo>/objects/pack/ by WriteToDirectoryAsync, but the GitObjectDb's
        // PackObjectBackend was loaded at construction time and doesn't know
        // about the new pack. Matches GitSmartProtocol.DownloadPackAsync.
        await repo.Objects.RefreshPackBackendsAsync(cancellationToken).ConfigureAwait(false);
    }
#pragma warning restore CA1506
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<GitOid>> ShallowRootsAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<GitOid>>(Array.Empty<GitOid>());
    }

    /// <inheritdoc/>
    // CA1506: this method is a faithful port of C's local_push, which
    // touches the pack writer, refs, and progress bridges; the coupling
    // count mirrors the C function and is not worth splitting.
#pragma warning disable CA1506
    public async Task<GitPushResult> PushAsync(GitRepository repo, IReadOnlyList<GitPushSpec> specs, GitPackWriter? packWriter, GitRemoteCallbacks? callbacks, bool reportStatus, IReadOnlyList<string>? pushOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(specs);

        if (!_connected)
        {
            throw new GitException(GitErrorCode.Invalid, "the transport is not connected", GitErrorCategory.Net);
        }

        // For local push, we need access to the target repository
        string targetPath = GitUrlUtils.LocalPathFromUrl(ConnectedUrl);
        GitRepository targetRepo = await GitRepository.OpenAsync(targetPath, _context, cancellationToken).ConfigureAwait(false);
        try
        {
            // Cannot push to a non-bare repository (matches C's GIT_EBAREREPO)
            if (!targetRepo.IsBare)
            {
                // C (local.c:416-420): GIT_EBAREREPO with the GIT_ERROR_INVALID
                // class and "local push doesn't (yet) support pushing to
                // non-bare repos.".
                throw new GitException(GitErrorCode.BareRepo, "local push doesn't (yet) support pushing to non-bare repos.", GitErrorCategory.Invalid);
            }

            var status = new List<GitPushStatus>();

            // Determine if we need a pack
            bool needPack = false;
            foreach (GitPushSpec spec in specs)
            {
                if (!spec.Loid.IsZero)
                {
                    needPack = true;
                    break;
                }
            }

            // Build and write the pack if needed
            if (needPack)
            {
                GitPackWriter pb = packWriter ?? new GitPackWriter(repo);

                // Hide objects the target already has
                using var walk = new GitRevWalker(repo);
                walk.Sort = GitSortMode.Time;

                foreach (GitPushSpec spec in specs)
                {
                    if (spec.Loid.IsZero)
                    {
                        continue;
                    }

                    // Insert the pushed commit and its tree
                    await pb.InsertAsync(spec.Loid, cancellationToken).ConfigureAwait(false);
                }

                // Hide commits the target already has
                await foreach (GitOid existingOid in targetRepo.Objects.EnumerateAsync(cancellationToken).ConfigureAwait(false))
                {
                    GitObject? existingObj = await targetRepo.Objects.LookupAsync(existingOid, cancellationToken).ConfigureAwait(false);
                    if (existingObj is Commit)
                    {
                        try
                        {
                            await walk.HideAsync(existingOid, cancellationToken).ConfigureAwait(false);
                        }
                        catch (GitException) { }
                    }
                    existingObj?.Dispose();
                }

                await pb.InsertWalkAsync(walk, cancellationToken).ConfigureAwait(false);

                // C (local.c:371-380, local_push): pack-write progress routes to push_transfer_progress(received_objects, total_objects, received_bytes) via
                // transfer_to_push_transfer — NOT pack_progress.
                IProgress<GitPushTransferProgress>? pushTransfer = callbacks?.PushTransferProgress;
                IProgress<GitPackProgress>? packProgress = pushTransfer is null
                    ? callbacks?.PackProgress
                    : new Progress<GitPackProgress>(p =>
                    {
                        pushTransfer.Report(new GitPushTransferProgress(
                            Current: p.Current,
                            Total: p.Total,
                            Bytes: 0));
                    });

                // Write the pack to the target repo's objects/pack/ directory
                string packDir = Path.Join(targetRepo.Path, "objects", "pack");
                await pb.WriteToDirectoryAsync(packDir, packProgress, cancellationToken).ConfigureAwait(false);

                // C's git_packbuilder_write reports the final byte count.
                if (pushTransfer is not null)
                {
                    string? writtenPack = Directory.EnumerateFiles(packDir, "pack-*.pack")
                        .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                        .FirstOrDefault();
                    pushTransfer.Report(new GitPushTransferProgress(
                        Current: pb.ObjectCount,
                        Total: pb.ObjectCount,
                        Bytes: writtenPack is null ? 0 : new FileInfo(writtenPack).Length));
                }
            }

            // Update refs on the target repository
            foreach (GitPushSpec spec in specs)
            {
                string refName = spec.RefSpec.Destination;

                if (spec.Loid.IsZero)
                {
                    // Delete the ref. C (local.c:356-366,
                    // local_push_update_remote_ref): deleting a ref that
                    // does not exist is SUCCESS (GIT_ENOTFOUND swallowed) —
                    // the status is "ok".
                    // when the ref was missing.
                    try
                    {
                        GitReference? existingRef = await targetRepo.Refs.LookupAsync(refName, cancellationToken).ConfigureAwait(false);
                        if (existingRef is not null)
                        {
                            await targetRepo.Refs.DeleteAsync(refName, cancellationToken).ConfigureAwait(false);
                        }

                        status.Add(new GitPushStatus { Ok = true, Ref = refName, Message = null });
                    }
                    catch (GitException ex)
                    {
                        status.Add(new GitPushStatus { Ok = false, Ref = refName, Message = ex.Message });
                    }
                }
                else
                {
                    // Create or update the ref
                    try
                    {
                        GitReference? existing = await targetRepo.Refs.ResolveAsync(refName, cancellationToken).ConfigureAwait(false);
                        if (existing is null)
                        {
                            await targetRepo.Refs.CreateAsync(refName, spec.Loid, force: spec.RefSpec.Force, logMessage: "push", cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await targetRepo.Refs.SetTargetAsync(existing, spec.Loid, logMessage: "push", cancellationToken).ConfigureAwait(false);
                        }

                        status.Add(new GitPushStatus { Ok = true, Ref = refName, Message = null });
                    }
                    catch (GitException ex)
                    {
                        status.Add(new GitPushStatus { Ok = false, Ref = refName, Message = ex.Message });
                    }
                }

                // Fire push update reference callback
                callbacks?.PushUpdateReference?.Invoke(refName, status[^1].Ok ? null : status[^1].Message);
            }

            return new GitPushResult { UnpackOk = true, Status = status };
        }
        finally
        {
            await targetRepo.DisposeAsync().ConfigureAwait(false);
        }
    }
#pragma warning restore CA1506

    /// <inheritdoc/>
    public void Cancel() => _cancelled = true;

    /// <inheritdoc/>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        // Dispose the source repo but keep refs cached — Remote.UpdateTips
        // calls LsAsync() after Disconnect(), so refs must remain available.
        if (_sourceRepo is not null)
        {
            await _sourceRepo.DisposeAsync().ConfigureAwait(false);
        }
        _sourceRepo = null;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
    }

    /// <summary> The heads whose objects may need downloading: the advertised refs plus the negotiation wants. C inserts OID-refspec wants (maybe_want_oid,
    /// fetch.c:142-155) into remote-&gt;refs so local_download_pack copies their objects — the C# transport keeps them separately because the advertisement and
    /// the wants are distinct lists. Deduplicated by OID. </summary>
    private IEnumerable<GitRemoteHead> AllHeadsToDownload()
    {
        var seen = new HashSet<GitOid>();
        foreach (GitRemoteHead head in _refs)
        {
            if (seen.Add(head.Oid))
            {
                yield return head;
            }
        }

        foreach (GitRemoteHead head in _negotiationWants)
        {
            if (seen.Add(head.Oid))
            {
                yield return head;
            }
        }
    }

    private async Task StoreRefsAsync(CancellationToken cancellationToken)
    {
        if (_sourceRepo is null)
        {
            throw new GitException(GitErrorCode.Invalid, "no source repository", GitErrorCategory.Net);
        }

        _refs.Clear();

        // Enumerate all ref names and sort alphabetically (matching C's store_refs)
        var refNames = new List<string>();
        await foreach (GitReference reference in _sourceRepo.Refs.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            refNames.Add(reference.NameKey.ToUtf8StringStrict());
        }
        // C's store_refs sorts with git__strcmp_cb (byte order,
        // local.c:171) — byte order, not UTF-16 ordinal order, so non-BMP ref
        // names sort correctly.
        refNames.Sort(Utils.AsciiText.BytewiseCompare);

        // Add HEAD first (fetch direction only)
        if (_direction == GitDirection.Fetch)
        {
            await AddRefAsync("HEAD", cancellationToken).ConfigureAwait(false);
        }

        // Add each ref
        foreach (string? name in refNames)
        {
            await AddRefAsync(name, cancellationToken).ConfigureAwait(false);
        }

        // Remove duplicates (C deduplicates after sorting)
        // HEAD may duplicate a ref if HEAD is a direct ref — keep the first occurrence
        var seen = new HashSet<string>();
        for (int i = _refs.Count - 1; i >= 0; i--)
        {
            if (!seen.Add(_refs[i].Name))
            {
                _refs.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Add a ref to the <see cref="_refs"/> list.
    /// Matches <c>add_ref</c> in <c>local.c:67-149</c>.
    /// </summary>
    private async Task AddRefAsync(string name, CancellationToken cancellationToken)
    {
        if (_sourceRepo is null)
        {
            return;
        }

        // Lookup the ref (not resolved — we need to know if it's symbolic)
        GitReference? raw = await _sourceRepo.Refs.LookupAsync(name, cancellationToken).ConfigureAwait(false);

        if (raw is null)
        {
            // Empty repos often have HEAD pointing to a nonexistent branch — that's OK
            if (name == GitReferences.HeadFile)
            {
                return;
            }

            return;
        }

        // Resolve to a direct ref to get the OID
        GitReference? resolved = await _sourceRepo.Refs.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            // Dangling symref — skip (unless HEAD, which is OK in empty repos)
            return;
        }

        if (resolved is not GitDirectReference dr)
        {
            return;
        }

        // Determine symref target
        string? symrefTarget = null;
        if (raw is GitSymbolicReference sym)
        {
            symrefTarget = sym.TargetNameKey.ToUtf8StringStrict();
        }

        // Create the remote head
        var head = new GitRemoteHead(
            Local: false,
            Oid: dr.Target,
            LocalOid: default,
            Name: name,
            SymrefTarget: symrefTarget);

        _refs.Add(head);

        // For fetch direction only: if the ref points to an annotated tag, peel it
        // and add a second RemoteHead with the "^{}" suffix (matching C's add_ref).
        // C (local.c:114-148) peels with git_tag_peel and adds the "^{}" head
        // whatever the target type is — a tag pointing at a tree or blob gets
        // a "^{}" head too.
        if (_direction == GitDirection.Fetch)
        {
            GitObject? obj = await _sourceRepo.Objects.LookupAsync(dr.Target, cancellationToken).ConfigureAwait(false);
            if (obj is GitTag tag)
            {
                // Peel the tag chain to its first non-tag target (C
                // git_tag_peel, tag.c) and add the "^{}" head whatever the
                // target type is — a tag pointing at a tree or blob gets a
                // "^{}" head too.
                GitObject? peeled = tag;
                while (peeled is GitTag inner)
                {
                    peeled = await _sourceRepo.Objects.LookupAsync(inner.Target, cancellationToken).ConfigureAwait(false);
                    if (peeled is null)
                    {
                        break;
                    }
                }

                if (peeled is not null && peeled.Id != tag.Id)
                {
                    _refs.Add(new GitRemoteHead(
                        Local: false,
                        Oid: peeled.Id,
                        LocalOid: default,
                        Name: name + "^{}",
                        SymrefTarget: null));
                }
            }
        }
    }

    /// <summary>
    /// Recursively collects a tag's target closure. Matches the C's
    /// <c>git_packbuilder_insert_recur</c> (pack-objects.c:1541-1576) used for
    /// non-commit refs by the local transport (transports/local.c:614-629):
    /// the tag chain AND the peeled target's full closure — a commit brings
    /// its whole tree, a tree brings its subtree, a blob is inserted as-is.
    /// </summary>
    private static async Task CollectTagObjectsAsync(GitObjectDb odb, GitOid targetOid, HashSet<GitOid> set, CancellationToken cancellationToken)
    {
        GitObject? obj = await odb.LookupAsync(targetOid, cancellationToken).ConfigureAwait(false);
        if (obj is null)
        {
            return;
        }

        try
        {
            if (obj is GitTag tag)
            {
                set.Add(tag.Id);
                await CollectTagObjectsAsync(odb, tag.Target, set, cancellationToken).ConfigureAwait(false);
            }
            else if (obj is Commit commit)
            {
                set.Add(commit.Id);
                await CollectTreeClosureAsync(odb, commit.Tree, set, cancellationToken).ConfigureAwait(false);
            }
            else if (obj is GitTree tree)
            {
                set.Add(tree.Id);
                await CollectTreeClosureEntriesAsync(odb, tree, set, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Blob.
                set.Add(targetOid);
            }
        }
        finally
        {
            obj.Dispose();
        }
    }

    /// <summary>
    /// Collects a tree and every entry beneath it (gitlinks skipped). Matches
    /// the C's <c>git_packbuilder_insert_tree</c> walk.
    /// </summary>
    private static async Task CollectTreeClosureAsync(GitObjectDb odb, GitOid treeOid, HashSet<GitOid> set, CancellationToken cancellationToken)
    {
        GitTree? tree = await odb.LookupAsync<GitTree>(treeOid, cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return;
        }

        try
        {
            set.Add(tree.Id);
            await CollectTreeClosureEntriesAsync(odb, tree, set, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tree.Dispose();
        }
    }

    private static async Task CollectTreeClosureEntriesAsync(GitObjectDb odb, GitTree tree, HashSet<GitOid> set, CancellationToken cancellationToken)
    {
        await foreach ((_, GitTreeEntry entry) in tree.WalkAsync(GitTreeWalkMode.PreOrder, cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsTree)
            {
                await CollectTreeClosureAsync(odb, entry.Id, set, cancellationToken).ConfigureAwait(false);
            }
            else if (!entry.IsGitLink)
            {
                set.Add(entry.Id);
            }
        }
    }
}
