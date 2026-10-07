// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.Transports;

/// <summary>
/// Smart transport — implements the git smart protocol v0/v1 over a
/// subtransport backend. Managed port of <c>transport_smart</c> in
/// <c>src/libgit2/transports/smart.h</c> + <c>smart.c</c>.
/// </summary>
public sealed class GitSmartTransport : IGitTransport
{
    /// <summary>Internal buffer size (64 KiB), matching <c>GIT_SMART_BUFFER_SIZE</c>.</summary>
    private const int BufferSize = 65536;

    private readonly byte[] _buffer = new byte[BufferSize];
    private int _bufferLen;
    private readonly IGitSubtransport _subtransport;
    private readonly bool _rpc;
    private IGitSubtransportStream? _currentStream;
    private string? _url;
    private GitDirection _direction;
    private GitRemoteConnectOptions? _connectOptions;
    private volatile bool _cancelled;
    private volatile bool _connected;
    private volatile bool _haveRefs;
    private bool _disposed;

    private readonly List<GitPacket> _refs = [];
    private readonly List<GitRemoteHead> _heads = [];
    private readonly GitSmartCapabilitySet _caps = new();
    private readonly List<GitOid> _common = [];
    private readonly List<GitOid> _shallowRoots = [];

    /// <summary>
    /// Creates a smart transport wrapping the given subtransport definition.
    /// The <paramref name="context"/> is passed to the subtransport factory.
    /// </summary>
    internal GitSmartTransport(SubtransportDefinition definition, GitContext context)
    {
        _subtransport = definition.Factory(context);
        _rpc = definition.IsRpc;
    }

    /// <summary>Whether this is an RPC (stateless, HTTP) transport.</summary>
    public bool IsRpc => _rpc;

    /// <summary>The parsed capabilities (available after Connect).</summary>
    public GitSmartCapabilitySet SmartCaps => _caps;

    /// <inheritdoc/>
    public GitRemoteCapability Capabilities
    {
        get
        {
            GitRemoteCapability caps = GitRemoteCapability.None;
            if ((_caps.Flags & GitSmartCapabilities.PushOptions) != 0)
            {
                caps |= GitRemoteCapability.PushOptions;
            }

            if ((_caps.Flags & GitSmartCapabilities.WantTipSha1) != 0)
            {
                caps |= GitRemoteCapability.TipOid;
            }

            if ((_caps.Flags & GitSmartCapabilities.WantReachableSha1) != 0)
            {
                caps |= GitRemoteCapability.ReachableOid;
            }

            return caps;
        }
    }

    /// <inheritdoc/>
    public GitHashAlgorithmKind OidType
    {
        get
        {
            if (_caps.ObjectFormat == null)
            {
                return GitHashAlgorithmKind.Sha1;
            }

            return _caps.ObjectFormat switch
            {
                "sha1" => GitHashAlgorithmKind.Sha1,
                "sha256" => GitHashAlgorithmKind.Sha256,
                _ => GitHashAlgorithmKind.Sha1,
            };
        }
    }

    /// <summary>The URL this transport is connected to.</summary>
    public string? Url => _url;

    /// <summary>
    /// The connected URL, throwing if <see cref="ConnectAsync"/> has not been
    /// called. Used by post-connect operations (uploadpack/receivepack) that
    /// require an established connection.
    /// </summary>
    private string ConnectedUrl => _url ?? throw new InvalidOperationException("transport is not connected");

    /// <summary>The direction (fetch or push) for this transport.</summary>
    public GitDirection Direction => _direction;

    /// <inheritdoc/>
    public bool IsConnected => _connected;

    /// <summary>Whether ref advertisements have been loaded.</summary>
    public bool HaveRefs => _haveRefs;

    /// <summary>The cached ref advertisement packets (includes comments and errors).</summary>
    public IReadOnlyList<GitPacket> Refs => _refs;

    /// <summary>The accumulated common OIDs from negotiation (multi_ack mode).</summary>
    public IReadOnlyList<GitOid> Common => _common;

    /// <summary>The shallow root OIDs negotiated during a shallow fetch.</summary>
    public IReadOnlyList<GitOid> ShallowRootsList => _shallowRoots;

    /// <summary>
    /// Add a common OID during negotiation (internal — used by SmartProtocol).
    /// </summary>
    internal void AddCommon(GitOid oid) => _common.Add(oid);

    /// <summary>
    /// Clear the common list (internal — used by SmartProtocol).
    /// </summary>
    internal void ClearCommon() => _common.Clear();

    /// <summary>
    /// Add a shallow root OID (internal — used by SmartProtocol).
    /// </summary>
    internal void AddShallowRoot(GitOid oid) => _shallowRoots.Add(oid);

    /// <summary>
    /// Remove a shallow root OID (internal — used by SmartProtocol for unshallow).
    /// </summary>
    internal void RemoveShallowRoot(GitOid oid) => _shallowRoots.Remove(oid);

    /// <summary>
    /// Clear shallow roots (internal — used by SmartProtocol).
    /// </summary>
    internal void ClearShallowRoots() => _shallowRoots.Clear();

    /// <summary>
    /// Add a parsed packet to the refs list (internal — used by SmartProtocol.StoreRefs).
    /// </summary>
    internal void AddRef(GitPacket pkt) => _refs.Add(pkt);

    /// <summary> Merge-join the push specs into the cached ref advertisement and rebuild the heads list. Ported from <c>update_refs_from_report</c>'s merge
    /// join (smart_protocol.c:1101-1151) + <c>git_smart__update_heads</c> (smart.c:78-111): refs are sorted by name, each successful spec updates its ref's OID
    /// (or adds it), zero-OID refs (deletions) are removed, and <c>t-&gt;heads</c> is rebuilt so a still-connected <c>ls</c> reflects the pushed state.
    /// </summary> <param name="specs">The push specs (sorted by destination by the caller).</param> <param name="status">The push report statuses, one per
    /// spec.</param>
    internal void UpdateRefsFromPush(IReadOnlyList<GitPushSpec> specs, IReadOnlyList<GitPushStatus> status)
    {
        // C (smart_protocol.c:1101): refs must be sorted with ref_name_cmp
        // (strcmp byte order), not UTF-16 ordinal order, so non-BMP ref names
        // sort correctly.
        _refs.Sort(static (a, b) =>
        {
            string an = a is GitRefPacket ar ? ar.Head.Name : string.Empty;
            string bn = b is GitRefPacket br ? br.Head.Name : string.Empty;
            return Utils.AsciiText.BytewiseCompare(an, bn);
        });

        var sortedSpecs = specs.OrderBy(s => s.RefSpec.Destination, Comparer<string>.Create(Utils.AsciiText.BytewiseCompare)).ToList();

        // Merge join (smart_protocol.c:1112-1132): i walks the specs, j the
        // refs; cmp < 0 → add case, cmp == 0 → update case. Additions are
        // appended (C's git_vector_insert) and the final sort places them.
        int i = 0, j = 0;
        while (i < sortedSpecs.Count && j < _refs.Count)
        {
            GitPushSpec spec = sortedSpecs[i];
            GitPushStatus pushStatus = status[i];
            var refPkt = _refs[j] as GitRefPacket;
            int cmp = refPkt is null
                ? -1
                : Utils.AsciiText.BytewiseCompare(spec.RefSpec.Destination, refPkt.Head.Name);

            if (cmp <= 0)
            {
                i++;
            }

            if (cmp >= 0)
            {
                j++;
            }

            // Add case: the spec's ref is not advertised yet.
            if (cmp < 0 && pushStatus.Message is null)
            {
                _refs.Add(new GitRefPacket(new GitRemoteHead(
                    Local: false,
                    Oid: spec.Loid,
                    LocalOid: default,
                    Name: spec.RefSpec.Destination,
                    SymrefTarget: null), Capabilities: null));
            }

            // Update case: replace the advertised OID (delete → zero → removed below).
            if (cmp == 0 && pushStatus.Message is null && refPkt is not null)
            {
                _refs[j - 1] = refPkt with { Head = refPkt.Head with { Oid = spec.Loid } };
            }
        }

        // Trailing add cases (smart_protocol.c:1134-1143).
        for (; i < sortedSpecs.Count; i++)
        {
            GitPushStatus pushStatus = status[i];
            if (pushStatus.Message is null)
            {
                _refs.Add(new GitRefPacket(new GitRemoteHead(
                    Local: false,
                    Oid: sortedSpecs[i].Loid,
                    LocalOid: default,
                    Name: sortedSpecs[i].RefSpec.Destination,
                    SymrefTarget: null), Capabilities: null));
            }
        }

        // Remove refs updated to a zero OID (smart_protocol.c:1145-1151).
        _refs.RemoveAll(pkt => pkt is GitRefPacket refPkt && refPkt.Head.Oid.IsZero);

        // C (smart_protocol.c:1153): final git_vector_sort with ref_name_cmp
        // (strcmp byte order) — byte-wise, not UTF-16 ordinal.
        _refs.Sort(static (a, b) =>
        {
            string an = a is GitRefPacket ar ? ar.Head.Name : string.Empty;
            string bn = b is GitRefPacket br ? br.Head.Name : string.Empty;
            return Utils.AsciiText.BytewiseCompare(an, bn);
        });

        // C (smart_protocol.c:1270-1271): git_smart__update_heads rebuilds
        // t->heads from the updated refs (symrefs: none here — C passes NULL).
        UpdateHeads([]);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitRemoteHead>> LsAsync(CancellationToken cancellationToken = default)
    {
        if (!_haveRefs)
        {
            throw new GitException(GitErrorCode.Invalid, "the transport has not yet loaded the refs", GitErrorCategory.Net);
        }

        return _heads;
    }

    /// <inheritdoc/>
    public async Task NegotiateFetchAsync(GitRepository repo, GitFetchNegotiation wants, CancellationToken cancellationToken = default)
    {
        await GitSmartProtocol.NegotiateFetchAsync(this, repo, wants, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DownloadPackAsync(GitRepository repo, GitIndexerProgress stats, CancellationToken cancellationToken = default)
    {
        await GitSmartProtocol.DownloadPackAsync(this, repo, stats, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<GitOid>> ShallowRootsAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<GitOid>>(_shallowRoots);
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(string url, GitDirection direction, GitRemoteConnectOptions? options, CancellationToken cancellationToken = default)
    {
        await ResetStreamAsync(closeSubtransport: true, cancellationToken).ConfigureAwait(false);

        _url = url;
        _direction = direction;
        _connectOptions = options;

        // C (smart_protocol.c:41-47): git_smart__store_refs clears the refs
        // at the start of every connect (a reconnect after disconnect must
        // not accumulate stale refs).
        _refs.Clear();

        GitSmartService service = direction == GitDirection.Fetch
            ? GitSmartService.UploadPackLs
            : GitSmartService.ReceivePackLs;

        _currentStream = await _subtransport.ActionAsync(_url, service, _connectOptions, cancellationToken).ConfigureAwait(false);

        // RPC expects 2 flushes, stateful expects 1
        int flushes = _rpc ? 2 : 1;
        await GitSmartProtocol.StoreRefsAsync(this, flushes, cancellationToken).ConfigureAwait(false);

        // For RPC, strip the first comment packet
        if (_rpc)
        {
            if (_refs.Count == 0 || _refs[0] is not GitCommentPacket)
            {
                throw new GitException(GitErrorCode.Invalid, "invalid response: expected comment packet", GitErrorCategory.Net);
            }

            _refs.RemoveAt(0);
        }

        _haveRefs = true;

        // Validate first packet is a ref
        if (_refs.Count > 0 && _refs[0] is not GitRefPacket)
        {
            throw new GitException(GitErrorCode.Invalid, "invalid response: expected ref packet", GitErrorCategory.Net);
        }

        // Detect capabilities from the first ref
        GitRefPacket? first = _refs.Count > 0 ? _refs[0] as GitRefPacket : null;
        bool capsFound = false;
        if (first != null)
        {
            try
            {
                capsFound = GitSmartProtocol.DetectCapabilities(first, _caps);
            }
            catch (GitException)
            {
                // C (smart.c:210-216): a detect_caps failure other than
                // GIT_ENOTFOUND aborts the connect with GIT_ERROR_NET
                // "invalid response" (append_symref's "remote sent invalid
                // symref" message is overwritten by the caller).
                throw new GitException(GitErrorCode.Error, "invalid response", GitErrorCategory.Net);
            }

            if (capsFound)
            {
                // If the only ref is "capabilities^{}" with zero OID, remove it (empty repo)
                if (_refs.Count == 1 &&
                    first.Head.Name == "capabilities^{}" &&
                    first.Head.Oid.IsZero)
                {
                    _refs.Clear();
                }
            }
        }

        // C (smart.c:202-218): heads are only populated when capabilities
        // were found on the first ref; without caps (or without refs) the
        // heads list stays empty and ls returns nothing.
        if (capsFound)
        {
            UpdateHeads(_caps.Symrefs);
        }

        // For RPC, reset the stream (but not the subtransport) for the next request
        if (_rpc)
        {
            await ResetStreamAsync(closeSubtransport: false, cancellationToken).ConfigureAwait(false);
        }

        _connected = true;
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
    public void Cancel()
    {
        _cancelled = true;
    }

    /// <summary>Whether the transport has been cancelled.</summary>
    public bool IsCancelled => _cancelled;

    /// <summary>The connection options.</summary>
    public GitRemoteConnectOptions? ConnectOptions => _connectOptions;

    /// <summary>
    /// Packetsize callback for progress reporting during pack download.
    /// Set by <see cref="GitSmartProtocol.DownloadPackAsync"/> to report received bytes.
    /// </summary>
    public Func<long, bool>? PacketsizeCallback { get; set; }

    /// <summary>
    /// Read data from the current stream into the internal buffer.
    /// Ported from <c>git_smart__recv()</c> in <c>smart.c:16</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of bytes read, or 0 on EOF.</returns>
    public async Task<int> RecvAsync(CancellationToken cancellationToken = default)
    {
        if (_currentStream is null)
        {
            throw new GitException(GitErrorCode.Invalid, "no current stream", GitErrorCategory.Net);
        }

        int remain = BufferSize - _bufferLen;
        if (remain == 0)
        {
            throw new GitException(GitErrorCode.Invalid, "out of buffer space", GitErrorCategory.Net);
        }

        int bytesRead = await _currentStream.ReadAsync(_buffer.AsMemory(_bufferLen, remain), cancellationToken).ConfigureAwait(false);

        _bufferLen += bytesRead;

        if (bytesRead > 0 && PacketsizeCallback is { } cb)
        {
            if (!cb(bytesRead))
            {
                throw new GitException(GitErrorCode.User, "transfer cancelled by callback", GitErrorCategory.Net);
            }
        }

        return bytesRead;
    }

    /// <summary>
    /// Get the remaining un-parsed bytes in the receive buffer.
    /// </summary>
    public ReadOnlySpan<byte> BufferData => _buffer.AsSpan(0, _bufferLen);

    /// <summary>
    /// Get the remaining un-parsed bytes in the receive buffer as a
    /// <see cref="ReadOnlyMemory{Byte}"/>. Used by callers that need to pass
    /// the buffer to async APIs (e.g. <see cref="IGitWritePack.AppendAsync"/>)
    /// without copying to a fresh <c>byte[]</c> via <see cref="ReadOnlySpan{T}.ToArray"/>.
    /// </summary>
    public ReadOnlyMemory<byte> BufferDataMemory => _buffer.AsMemory(0, _bufferLen);

    /// <summary>
    /// Consume <paramref name="consumed"/> bytes from the front of the buffer,
    /// shifting remaining data to the start.
    /// </summary>
    public void ConsumeBuffer(int consumed)
    {
        if (consumed <= 0)
        {
            return;
        }

        if (consumed >= _bufferLen)
        {
            _bufferLen = 0;
        }
        else
        {
            Array.Copy(_buffer, consumed, _buffer, 0, _bufferLen - consumed);
            _bufferLen -= consumed;
        }
    }

    /// <summary>
    /// Send negotiation data to the server (fetch direction only).
    /// Ported from <c>git_smart__negotiation_step()</c>.
    /// </summary>
    /// <param name="data">The negotiation data to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task NegotiationStepAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_rpc)
        {
            await ResetStreamAsync(closeSubtransport: false, cancellationToken).ConfigureAwait(false);
        }

        if (_direction != GitDirection.Fetch)
        {
            throw new GitException(GitErrorCode.Invalid, "this operation is only valid for fetch", GitErrorCategory.Net);
        }

        IGitSubtransportStream stream = await _subtransport.ActionAsync(ConnectedUrl, GitSmartService.UploadPack, _connectOptions, cancellationToken).ConfigureAwait(false);

        if (!_rpc)
        {
            // Stateful: stream should be the same
            if (_currentStream != stream)
            {
                _currentStream = stream;
            }
        }
        else
        {
            _currentStream = stream;
        }

        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Get the push stream for sending a pack (push direction only).
    /// Ported from <c>git_smart__get_push_stream()</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A bidirectional stream for reading/writing pkt-line data.</returns>
    public async Task<IGitSubtransportStream> GetPushStreamAsync(CancellationToken cancellationToken = default)
    {
        if (_rpc)
        {
            await ResetStreamAsync(closeSubtransport: false, cancellationToken).ConfigureAwait(false);
        }

        // Clear any leftover ref-advertisement data from the receive buffer.
        // For RPC (HTTP), the ref advertisement (GET /info/refs) and the push
        // POST are separate HTTP requests — the push response must not be
        // polluted with stale data from the ref advertisement.
        // For stateful transports (SSH, git://), the ref advertisement and the
        // push share one stream; the SSH channel can read ahead past the
        // ref-advertisement flush, leaving bytes in the buffer that
        // ParsePushReportAsync would then misread (a stray flush makes the
        // report loop break immediately, yielding UnpackOk=False /
        // Status=empty). This clear is safe: GetPushStreamAsync runs before
        // any commands/pack are sent, and the server sends nothing between
        // the ref advertisement and the client's commands, so the buffer
        // here can only hold ref-ad data already parsed into _refs/_heads.
        // (The C reference's git_smart__get_push_stream gates this on rpc
        // because its stateful buffer is naturally empty; clearing an empty
        // buffer is a no-op, so this diverges harmlessly.)
        _bufferLen = 0;

        if (_direction != GitDirection.Push)
        {
            throw new GitException(GitErrorCode.Invalid, "this operation is only valid for push", GitErrorCategory.Net);
        }

        IGitSubtransportStream stream = await _subtransport.ActionAsync(ConnectedUrl, GitSmartService.ReceivePack, _connectOptions, cancellationToken).ConfigureAwait(false);
        _currentStream = stream;
        return stream;
    }

    /// <inheritdoc/>
    public async Task<GitPushResult> PushAsync(GitRepository repo, IReadOnlyList<GitPushSpec> specs, GitPackWriter? packWriter, GitRemoteCallbacks? callbacks, bool reportStatus, IReadOnlyList<string>? pushOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        return await GitSmartProtocol.PushAsync(this, repo, specs, packWriter, callbacks, reportStatus, pushOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Update the heads list from parsed ref packets, resolving symrefs.
    /// Ported from <c>git_smart__update_heads()</c>.
    /// </summary>
    private void UpdateHeads(List<(string Source, string Target)> symrefs)
    {
        _heads.Clear();

        foreach (GitPacket pkt in _refs)
        {
            if (pkt is not GitRefPacket refPkt)
            {
                continue;
            }

            string? symrefTarget = refPkt.Head.SymrefTarget;

            // Try to resolve symrefs
            if (symrefs.Count > 0)
            {
                foreach ((string? source, string? target) in symrefs)
                {
                    if (MatchesRefSpec(source, refPkt.Head.Name))
                    {
                        symrefTarget = target;
                    }
                }
            }

            _heads.Add(refPkt.Head with { SymrefTarget = symrefTarget });
        }
    }

    private static bool MatchesRefSpec(string source, string refName)
    {
        // Simple wildcard match: "HEAD:refs/heads/main" — source is the
        // remote ref name to match. If source contains '*', use wildcard.
        if (source.Contains('*', StringComparison.Ordinal))
        {
            int star = source.IndexOf('*', StringComparison.Ordinal);
            string prefix = source[..star];
            string suffix = source[(star + 1)..];
            return refName.StartsWith(prefix, StringComparison.Ordinal) &&
                   refName.EndsWith(suffix, StringComparison.Ordinal);
        }

        return source == refName;
    }

    private async Task ResetStreamAsync(bool closeSubtransport, CancellationToken cancellationToken)
    {
        if (_currentStream is not null)
        {
            await _currentStream.DisposeAsync().ConfigureAwait(false);
        }
        _currentStream = null;

        if (closeSubtransport)
        {
            _url = null;
            await _subtransport.CloseAsync(cancellationToken).ConfigureAwait(false);
            _caps.ObjectFormat = null;
            _caps.Agent = null;
            _caps.Flags = GitSmartCapabilities.None;
            _caps.Symrefs.Clear();
            // C's
            // git_smart__close does NOT clear t->refs or t->heads
            // (smart.c:370-411) — the advertised refs/heads survive close so
            // a subsequent LsAsync (e.g. clone's checkout_branch,
            // clone.c:214-223) sees them without reconnecting. The port
            // cleared them, forcing clone to open a second TCP connection
            // and re-fire credential/hostkey callbacks. The refs are
            // cleared at the start of the next connect (C's
            // git_smart__store_refs, smart_protocol.c:41-47).
            _common.Clear();
            _shallowRoots.Clear();
            _bufferLen = 0;
        }
    }

    /// <inheritdoc/>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_connected && !_rpc && _currentStream is not null)
        {
            // Send flush to say goodbye
            try
            {
                await _currentStream.WriteAsync("0000"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort
            }
        }

        await ResetStreamAsync(closeSubtransport: true, cancellationToken).ConfigureAwait(false);
        _connected = false;
        _bufferLen = 0;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        await _subtransport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
    }
}
