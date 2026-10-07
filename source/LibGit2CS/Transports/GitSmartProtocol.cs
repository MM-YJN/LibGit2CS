// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;
using LibGit2CS.Utils;

namespace LibGit2CS.Transports;

/// <summary>
/// Smart protocol state machine — ref storage, capability detection,
/// fetch negotiation, and push report parsing.
/// Managed port of <c>src/libgit2/transports/smart_protocol.c</c>.
/// </summary>
/// <remarks>
/// <see cref="StoreRefsAsync"/>, <see cref="DetectCapabilities"/>,
/// <see cref="RecvPacketAsync"/>.
/// <see cref="NegotiateFetchAsync"/>, <see cref="DownloadPackAsync"/>,
/// <see cref="StoreCommonAsync"/>, <see cref="WaitWhileAckAsync"/>,
/// <see cref="SetupCaps"/>, <see cref="SetupShallowRoots"/>,
/// <see cref="DownloadPackNoSidebandAsync"/>.
/// Push: <c>GeneratePushPktline</c>/<c>ParsePushReportAsync</c>/<c>PushAsync</c>.
/// </remarks>
internal static class GitSmartProtocol
{
    /// <summary>
    /// Read ref advertisements from the transport's buffer, storing them as
    /// parsed packets. Handles buffer underrun by calling <see cref="GitSmartTransport.RecvAsync"/>
    /// to refill.
    /// Ported from <c>git_smart__store_refs()</c> in <c>smart_protocol.c:28</c>.
    /// </summary>
    /// <param name="transport">The smart transport to read from.</param>
    /// <param name="flushes">Expected number of flush packets (1 for stateful, 2 for RPC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of flush packets received.</returns>
    public static async Task<int> StoreRefsAsync(GitSmartTransport transport, int flushes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var state = new GitPacketParseState();

        int flushCount = 0;
        while (flushCount < flushes)
        {
            if (!GitPacketReader.TryParse(transport.BufferData, out GitPacket? pkt, out int consumed,
                                          out GitPacketParseError error, ref state))
            {
                if (error != GitPacketParseError.BufferTooShort)
                {
                    throw CreateParseException(error, transport.BufferData);
                }

                // Incomplete pkt-line or empty buffer — refill and retry.
                int recvd = await transport.RecvAsync(cancellationToken).ConfigureAwait(false);
                if (recvd == 0)
                {
                    throw new GitException(GitErrorCode.Eof, "could not read refs from remote repository", GitErrorCategory.Net);
                }

                continue;
            }

            // Consume parsed bytes from the buffer
            transport.ConsumeBuffer(consumed);

            // Handle error packets
            if (pkt is GitErrorPacket errPkt)
            {
                throw new GitException(GitErrorCode.Error, $"remote error: {errPkt.Message}", GitErrorCategory.Net);
            }

            // Flush packets are not stored
            if (pkt is GitFlushPacket)
            {
                flushCount++;
                continue;
            }

            // Store non-flush packets in the refs list
            transport.AddRef(pkt);
        }

        return flushCount;
    }

    /// <summary>
    /// Parse capabilities from the first ref packet's capability string.
    /// Ported from <c>git_smart__detect_caps()</c> in <c>smart_protocol.c:137</c>.
    /// </summary>
    /// <param name="refPkt">The first ref packet (carries the capability string).</param>
    /// <param name="caps">The capability set to populate.</param>
    /// <returns><c>true</c> if capabilities were found; <c>false</c> if none.</returns>
    public static bool DetectCapabilities(GitRefPacket? refPkt, GitSmartCapabilitySet caps)
    {
        ArgumentNullException.ThrowIfNull(caps);

        if (refPkt is null || refPkt.Capabilities is null)
        {
            return false;
        }

        string capStr = refPkt.Capabilities;
        int i = 0;

        while (i < capStr.Length)
        {
            // Skip spaces
            while (i < capStr.Length && capStr[i] == ' ')
            {
                i++;
            }

            if (i >= capStr.Length)
            {
                break;
            }

            // Find the end of the current capability
            int end = capStr.IndexOf(' ', i, StringComparison.Ordinal);
            if (end < 0)
            {
                end = capStr.Length;
            }

            string cap = capStr[i..end];

            // Match capabilities (longer prefixes first where needed)
            if (cap == "ofs-delta")
            {
                caps.Flags |= GitSmartCapabilities.OfsDelta;
            }
            else if (cap == "multi_ack_detailed")
            {
                caps.Flags |= GitSmartCapabilities.MultiAckDetailed;
            }
            else if (cap == "multi_ack")
            {
                caps.Flags |= GitSmartCapabilities.MultiAck;
            }
            else if (cap == "include-tag")
            {
                caps.Flags |= GitSmartCapabilities.IncludeTag;
            }
            else if (cap == "side-band-64k")
            {
                caps.Flags |= GitSmartCapabilities.SideBand64k;
            }
            else if (cap == "side-band")
            {
                caps.Flags |= GitSmartCapabilities.SideBand;
            }
            else if (cap == "delete-refs")
            {
                caps.Flags |= GitSmartCapabilities.DeleteRefs;
            }
            else if (cap == "push-options")
            {
                caps.Flags |= GitSmartCapabilities.PushOptions;
            }
            else if (cap == "thin-pack")
            {
                caps.Flags |= GitSmartCapabilities.ThinPack;
            }
            else if (cap == "shallow")
            {
                caps.Flags |= GitSmartCapabilities.Shallow;
            }
            else if (cap == "allow-tip-sha1-in-want")
            {
                caps.Flags |= GitSmartCapabilities.WantTipSha1;
            }
            else if (cap == "allow-reachable-sha1-in-want")
            {
                caps.Flags |= GitSmartCapabilities.WantReachableSha1;
            }
            else if (cap.StartsWith("symref=", StringComparison.Ordinal))
            {
                AppendSymref(caps, cap["symref=".Length..]);
            }
            else if (cap.StartsWith("object-format=", StringComparison.Ordinal))
            {
                caps.ObjectFormat = cap["object-format=".Length..];
            }
            else if (cap.StartsWith("agent=", StringComparison.Ordinal))
            {
                caps.Agent = cap["agent=".Length..];
            }
            // Unknown capabilities are silently skipped

            i = end;
        }

        // The null/ENOTFOUND case was handled above (C returns
        // GIT_ENOTFOUND only when pkt or capabilities are NULL).
        return true;
    }

    /// <summary>
    /// Parse a symref capability value (e.g. "HEAD:refs/heads/main") and add
    /// it to the symref list.
    /// Ported from <c>append_symref()</c> in <c>smart_protocol.c:89</c>.
    /// </summary>
    private static void AppendSymref(GitSmartCapabilitySet caps, string value)
    {
        // C (smart_protocol.c:89-135): the symref mapping value is parsed as
        // a fetch refspec (git_refspec__parse(value, true)); an unparseable
        // value fails with GIT_ERROR_NET "remote sent invalid symref" and
        // aborts the connect.
        // (Note: "symref=HEAD" without a colon is a VALID fetch refspec —
        // src HEAD, dst empty — and is accepted by C.)
        GitRefSpec spec;
        try
        {
            spec = GitRefSpec.Parse(value, isFetch: true);
        }
        catch (GitException)
        {
            throw new GitException(GitErrorCode.Error, "remote sent invalid symref", GitErrorCategory.Net);
        }

        caps.Symrefs.Add((spec.Source, spec.Destination));
    }

    /// <summary>
    /// Receive and parse a single packet from the transport, refilling the
    /// buffer on underrun. Capabilities are already parsed, so
    /// <see cref="GitPacketParseState.SeenCapabilities"/> is set to <c>true</c>.
    /// Ported from <c>recv_pkt()</c> in <c>smart_protocol.c:265</c>.
    /// </summary>
    /// <param name="transport">The smart transport to read from.</param>
    /// <param name="oidType">The OID type for parsing OID-containing packets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed packet.</returns>
    public static async Task<GitPacket> RecvPacketAsync(GitSmartTransport transport, GitHashAlgorithmKind oidType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var state = new GitPacketParseState
        {
            OidType = oidType,
            SeenCapabilities = true,
        };

        while (true)
        {
            if (GitPacketReader.TryParse(transport.BufferData, out GitPacket? pkt, out int consumed,
                                         out GitPacketParseError error, ref state))
            {
                transport.ConsumeBuffer(consumed);
                return pkt;
            }

            if (error != GitPacketParseError.BufferTooShort)
            {
                throw CreateParseException(error, transport.BufferData);
            }

            int recvd = await transport.RecvAsync(cancellationToken).ConfigureAwait(false);
            if (recvd == 0)
            {
                throw new GitException(GitErrorCode.Eof, "could not read from remote repository", GitErrorCategory.Net);
            }
        }
    }

    /// <summary>
    /// Map a fatal <see cref="GitPacketParseError"/> (any value other than
    /// <see cref="GitPacketParseError.BufferTooShort"/>) to the
    /// <see cref="GitException"/> thrown by the smart-protocol receive loop.
    /// <see cref="GitPacketParseError.BufferTooShort"/> is intentionally not
    /// handled here — callers handle that case (refill + retry) before falling
    /// back to this helper.
    /// </summary>
    /// <param name="error">The fatal parse outcome (never <see cref="GitPacketParseError.BufferTooShort"/>).</param>
    /// <param name="buffer">The receive buffer (used for C's "PACK" detection).</param>
    /// <returns>The <see cref="GitException"/> to throw.</returns>
    private static GitException CreateParseException(GitPacketParseError error, ReadOnlySpan<byte> buffer)
    {
        return error switch
        {
            // C (smart_pkt.c:613-615): a length in 1..3 is a bare -1 with no
            // message.
            GitPacketParseError.InvalidLength => new GitException(GitErrorCode.Error, "invalid pkt-line length from remote", GitErrorCategory.Net),
            // C (smart_pkt.c:594-597): "bad packet length", or "unexpected
            // pack file" for a raw "PACK" prefix.
            GitPacketParseError.InvalidHexLength => new GitException(
                GitErrorCode.Error,
                buffer.StartsWith("PACK"u8) ? "unexpected pack file" : "bad packet length",
                GitErrorCategory.Net),
            // C (smart_pkt.c:617-621): "Invalid empty packet".
            GitPacketParseError.InvalidEmptyPacket => new GitException(GitErrorCode.Error, "Invalid empty packet", GitErrorCategory.Net),
            GitPacketParseError.InvalidPayload => new GitException(GitErrorCode.Error, "malformed pkt-line payload from remote", GitErrorCategory.Net),
            _ => new GitException(GitErrorCode.Error, "pkt-line parse failed", GitErrorCategory.Net),
        };
    }

    /// <summary>
    /// Maximum number of "have" batches sent during negotiation.
    /// Matches <c>GIT_SMART__MAX_HAVES</c> (256) in libgit2.
    /// </summary>
    public const int MaxHaves = 256;

    /// <summary>
    /// Number of "have" lines per batch before flushing + reading ACKs.
    /// Matches the hardcoded <c>20</c> in <c>git_smart__negotiate_fetch</c>.
    /// </summary>
    public const int HavesPerBatch = 20;

    /// <summary>
    /// Network transfer progress threshold (100 KB) for the packetsize callback.
    /// Matches <c>NETWORK_XFER_THRESHOLD</c> in <c>smart_protocol.c</c>.
    /// </summary>
    public const int NetworkXferThreshold = 100 * 1024;

    /// <summary>
    /// Read and store ACK packets accumulated during multi_ack negotiation.
    /// Ported from <c>store_common()</c> in <c>smart_protocol.c:311</c>.
    /// </summary>
    /// <param name="transport">The smart transport.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task StoreCommonAsync(GitSmartTransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        GitHashAlgorithmKind oidType = transport.OidType;

        while (true)
        {
            GitPacket pkt = await RecvPacketAsync(transport, oidType, cancellationToken).ConfigureAwait(false);

            // In RPC mode, skip shallow/unshallow/flush packets
            if (transport.IsRpc && pkt is GitShallowPacket or GitUnshallowPacket or GitFlushPacket)
            {
                continue;
            }

            if (pkt is not GitAckPacket ackPkt)
            {
                return;
            }

            transport.AddCommon(ackPkt.Oid);
        }
    }

    /// <summary>
    /// After sending <c>done</c>, consume remaining ACK packets until a NAK
    /// or a non-continue ACK arrives. Ported from <c>wait_while_ack()</c>
    /// in <c>smart_protocol.c:341</c>.
    /// </summary>
    /// <param name="transport">The smart transport.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WaitWhileAckAsync(GitSmartTransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        GitHashAlgorithmKind oidType = transport.OidType;

        while (true)
        {
            GitPacket pkt = await RecvPacketAsync(transport, oidType, cancellationToken).ConfigureAwait(false);

            if (pkt is GitNakPacket)
            {
                break;
            }

            if (pkt is not GitAckPacket ack)
            {
                continue;
            }

            if (ack.Status is not GitAckStatus.Continue and
                not GitAckStatus.Common and
                not GitAckStatus.Ready)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Validate and adjust capabilities based on fetch negotiation parameters.
    /// Ported from <c>setup_caps()</c> in <c>smart_protocol.c:378</c>.
    /// </summary>
    public static void SetupCaps(GitSmartCapabilitySet caps, GitFetchNegotiation wants)
    {
        ArgumentNullException.ThrowIfNull(caps);
        ArgumentNullException.ThrowIfNull(wants);

        if (wants.Depth > 0)
        {
            if ((caps.Flags & GitSmartCapabilities.Shallow) == 0)
            {
                throw new GitException(GitErrorCode.Invalid, "server doesn't support shallow", GitErrorCategory.Net);
            }
        }
        else
        {
            caps.Flags &= ~GitSmartCapabilities.Shallow;
        }
    }

    /// <summary>
    /// Copy shallow root OIDs from the negotiation parameters into the transport.
    /// Ported from <c>setup_shallow_roots()</c> in <c>smart_protocol.c:392</c>.
    /// </summary>
    public static void SetupShallowRoots(GitSmartTransport transport, GitFetchNegotiation wants)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(wants);

        transport.ClearShallowRoots();

        foreach (GitOid oid in wants.ShallowRoots)
        {
            transport.AddShallowRoot(oid);
        }
    }

    /// <summary>
    /// Main fetch negotiation state machine. Sends wants, walks local commits
    /// via RevWalker, sends haves in batches of 20, handles 3 ACK modes
    /// (single-ACK, multi_ack, multi_ack_detailed), sends done, and consumes
    /// the final ACK/NAK. Ported from <c>git_smart__negotiate_fetch</c>
    /// in <c>smart_protocol.c:410</c>.
    /// </summary>
    /// <param name="transport">The smart transport.</param>
    /// <param name="repo">The local repository for revwalk.</param>
    /// <param name="wants">The fetch negotiation parameters (refs, shallow, depth).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task NegotiateFetchAsync(GitSmartTransport transport, GitRepository repo, GitFetchNegotiation wants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(wants);

        SetupCaps(transport.SmartCaps, wants);
        SetupShallowRoots(transport, wants);

        bool isMultiAck = (transport.SmartCaps.Flags & GitSmartCapabilities.MultiAck) != 0 ||
                          (transport.SmartCaps.Flags & GitSmartCapabilities.MultiAckDetailed) != 0;
        bool isRpc = transport.IsRpc;
        GitHashAlgorithmKind oidType = transport.OidType;

        // Build the initial wants buffer
        using var data = new PooledByteBufferWriter();
        WriteWantsToBuffer(data, wants, transport.SmartCaps.Flags, oidType);

        // Set up revwalk to enumerate local commits
        using var walk = new GitRevWalker(repo);
        walk.Sort = GitSortMode.Time;
        await walk.PushGlobAsync("refs/*", cancellationToken).ConfigureAwait(false);

        // Shallow handshake (only if depth > 0)
        if (wants.Depth > 0)
        {
            await transport.NegotiationStepAsync(data.WrittenMemory, cancellationToken).ConfigureAwait(false);

            if (!isRpc)
            {
                data.ResetWrittenCount();
            }

            while (true)
            {
                GitPacket pkt = await RecvPacketAsync(transport, oidType, cancellationToken).ConfigureAwait(false);

                if (pkt is GitShallowPacket shallowPkt)
                {
                    transport.AddShallowRoot(shallowPkt.Oid);
                }
                else if (pkt is GitUnshallowPacket unshallowPkt)
                {
                    transport.RemoveShallowRoot(unshallowPkt.Oid);
                }
                else if (pkt is GitFlushPacket)
                {
                    break; // Server is done
                }
                else
                {
                    throw new GitException(GitErrorCode.Invalid, "unexpected packet type during shallow handshake", GitErrorCategory.Net);
                }
            }
        }

        // Main negotiation loop (max 256 haves)
        int i = 0;
        await foreach (GitOid oid in walk.WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            GitPacketWriter.WriteHave(data, oid);
            i++;

            if (i % HavesPerBatch == 0)
            {
                if (transport.IsCancelled)
                {
                    throw new GitException(GitErrorCode.User, "the fetch was cancelled by the user", GitErrorCategory.Net);
                }

                GitPacketWriter.WriteFlush(data);
                await transport.NegotiationStepAsync(data.WrittenMemory, cancellationToken).ConfigureAwait(false);
                data.ResetWrittenCount();

                if (isMultiAck)
                {
                    await StoreCommonAsync(transport, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    GitPacket pkt = await RecvPacketAsync(transport, oidType, cancellationToken).ConfigureAwait(false);
                    if (pkt is GitAckPacket)
                    {
                        break;
                    }

                    if (pkt is GitNakPacket)
                    {
                        continue;
                    }

                    throw new GitException(GitErrorCode.Invalid, "unexpected pkt type", GitErrorCategory.Net);
                }
            }

            if (transport.Common.Count > 0)
            {
                break;
            }

            // RPC mode: re-send wants + common haves each batch
            if (i % HavesPerBatch == 0 && isRpc)
            {
                WriteWantsToBuffer(data, wants, transport.SmartCaps.Flags, oidType);
                foreach (GitOid commonOid in transport.Common)
                {
                    GitPacketWriter.WriteHave(data, commonOid);
                }
            }

            if (i >= MaxHaves)
            {
                break;
            }
        }

        // Send done
        // RPC mode with common objects: rebuild wants + all common haves
        if (isRpc && transport.Common.Count > 0)
        {
            data.ResetWrittenCount();
            WriteWantsToBuffer(data, wants, transport.SmartCaps.Flags, oidType);
            foreach (GitOid commonOid in transport.Common)
            {
                GitPacketWriter.WriteHave(data, commonOid);
            }
        }

        GitPacketWriter.WriteDone(data);

        if (transport.IsCancelled)
        {
            throw new GitException(GitErrorCode.User, "the fetch was cancelled", GitErrorCategory.Net);
        }

        await transport.NegotiationStepAsync(data.WrittenMemory, cancellationToken).ConfigureAwait(false);
        data.ResetWrittenCount();

        // Consume final ACK/NAK
        if (!isMultiAck)
        {
            GitPacket pkt = await RecvPacketAsync(transport, oidType, cancellationToken).ConfigureAwait(false);
            if (pkt is not GitAckPacket and not GitNakPacket)
            {
                throw new GitException(GitErrorCode.Invalid, "unexpected pkt type", GitErrorCategory.Net);
            }
        }
        else
        {
            await WaitWhileAckAsync(transport, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Return the shallow root OIDs from the transport.
    /// Ported from <c>git_smart__shallow_roots</c> in <c>smart_protocol.c:604</c>.
    /// </summary>
    public static IReadOnlyList<GitOid> GetShallowRoots(GitSmartTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return transport.ShallowRootsList;
    }

    /// <summary>
    /// Download the pack without side-band demux — stream raw pack data
    /// directly to the writepack. Ported from <c>no_sideband()</c>
    /// in <c>smart_protocol.c:623</c>.
    /// </summary>
    /// <param name="transport">The smart transport.</param>
    /// <param name="writepack">The writepack (pack indexer).</param>
    /// <param name="stats">Progress accumulator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task DownloadPackNoSidebandAsync(GitSmartTransport transport, IGitWritePack writepack, GitIndexerProgress stats, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(writepack);
        int recvd;

        do
        {
            if (transport.IsCancelled)
            {
                throw new GitException(GitErrorCode.User, "the fetch was cancelled by the user", GitErrorCategory.Net);
            }

            if (transport.BufferData.Length > 0)
            {
                if (!await writepack.AppendAsync(transport.BufferDataMemory, stats, cancellationToken).ConfigureAwait(false))
                {
                    throw new GitException(GitErrorCode.Error, "writepack append failed", GitErrorCategory.Net);
                }
            }

            // Consume the buffer
            transport.ConsumeBuffer(transport.BufferData.Length);

            recvd = await transport.RecvAsync(cancellationToken).ConfigureAwait(false);
        } while (recvd > 0);

        if (!await writepack.CommitAsync(stats, cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(GitErrorCode.Error, "writepack commit failed", GitErrorCategory.Net);
        }
    }

    /// <summary>
    /// Download the pack from the remote after negotiation. If the server
    /// supports side-band, demuxes data/progress/error channels. Otherwise,
    /// streams raw pack data directly. Ported from
    /// <c>git_smart__download_pack</c> in <c>smart_protocol.c:677</c>.
    /// </summary>
    /// <param name="transport">The smart transport.</param>
    /// <param name="repo">The local repository for object writing.</param>
    /// <param name="stats">Progress accumulator (reset and updated).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task DownloadPackAsync(GitSmartTransport transport, GitRepository repo, GitIndexerProgress stats, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(stats);

        // Reset stats in place. Matches C's memset(stats, 0, sizeof(*stats))
        // (smart_protocol.c:691) — the same struct instance is then mutated by
        // the indexer (git_indexer_append) and read live by the packetsize
        // callback below.
        stats.TotalObjects = 0;
        stats.IndexedObjects = 0;
        stats.ReceivedObjects = 0;
        stats.LocalObjects = 0;
        stats.TotalDeltas = 0;
        stats.IndexedDeltas = 0;
        stats.ReceivedBytes = 0;

        GitRemoteCallbacks? callbacks = transport.ConnectOptions?.Callbacks;
        IProgress<GitTransferProgress>? transferProgress = callbacks?.TransferProgress;
        IProgress<string>? sidebandProgress = callbacks?.SidebandProgress;

        // Set up packetsize callback for transfer progress reporting.
        // Faithful port of C's network_packetsize (smart_protocol.c:659): the
        // closure captures the shared stats instance by reference (it is a
        // sealed class — the C# analog of C's git_indexer_progress *stats
        // pointer). The indexer (GitPackIndexer.AppendAsync) mutates
        // stats.ReceivedObjects / stats.TotalObjects in place as it parses
        // the stream, so this closure — which fires from
        // GitSmartTransport.RecvAsync on every network chunk, *outside* any
        // AppendAsync call — sees the live counts on every firing, closing the
        // feedback loop between the indexer and the transport.
        long lastFiredBytes = 0;
        if (transferProgress is not null)
        {
            transport.PacketsizeCallback = (received) =>
            {
                // Accumulate bytes into the shared stats (matches C's
                // npp->stats->received_bytes += received, smart_protocol.c:664).
                stats.ReceivedBytes += received;
                if (stats.ReceivedBytes - lastFiredBytes > NetworkXferThreshold)
                {
                    lastFiredBytes = stats.ReceivedBytes;
                    transferProgress.Report(ToTransferProgress(stats));
                }

                return true; // continue
            };

            // Report any buffered data from negotiation
            if (transport.BufferData.Length > 0 && !transport.IsCancelled)
            {
                transport.PacketsizeCallback(transport.BufferData.Length);
            }
        }

        // Create the writepack — keep-as-pack indexer (writes .pack + .idx v2)
        string packDir = Path.Join(repo.Path, "objects", "pack");
        var writepack = new GitPackIndexer(packDir, repo.ObjectFormat, repo.Objects);
        try
        {
            bool hasSideband = (transport.SmartCaps.Flags & GitSmartCapabilities.SideBand) != 0 ||
                               (transport.SmartCaps.Flags & GitSmartCapabilities.SideBand64k) != 0;

            if (!hasSideband)
            {
                await DownloadPackNoSidebandAsync(transport, writepack, stats, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                GitHashAlgorithmKind oidType = transport.OidType;

                while (true)
                {
                    if (transport.IsCancelled)
                    {
                        throw new GitException(GitErrorCode.User, "the fetch was cancelled by the user", GitErrorCategory.Net);
                    }

                    GitPacket pkt = await RecvPacketAsync(transport, oidType, cancellationToken).ConfigureAwait(false);

                    if (transport.IsCancelled)
                    {
                        throw new GitException(GitErrorCode.User, "the fetch was cancelled by the user", GitErrorCategory.Net);
                    }

                    if (pkt is GitProgressPacket progressPkt)
                    {
                        sidebandProgress?.Report(progressPkt.Data);
                    }
                    else if (pkt is GitDataPacket dataPkt)
                    {
                        if (dataPkt.Data.Length > 0)
                        {
                            if (!await writepack.AppendAsync(dataPkt.Data, stats, cancellationToken).ConfigureAwait(false))
                            {
                                throw new GitException(GitErrorCode.Error, "writepack append failed", GitErrorCategory.Net);
                            }
                        }
                    }
                    else if (pkt is GitFlushPacket)
                    {
                        break; // End of packfile
                    }
                    // C (smart_protocol.c:721-758): the sideband demux loop has
                    // NO GIT_PKT_ERR arm — a band-3 "fatal: ..." packet falls
                    // through every branch, is freed, and the loop keeps
                    // reading (the error surfaces later as GIT_EEOF "could not
                    // read from remote repository", or not at all) rather than
                    // aborting the fetch immediately with "remote error: ...".
                }

                // Trailing progress report — matches C's
                // if (npp.callback && npp.stats->received_bytes > npp.last_fired_bytes)
                // (smart_protocol.c:776-780): fires only if the last per-chunk
                // report didn't already cover the final byte count.
                if (transferProgress is not null && stats.ReceivedBytes > lastFiredBytes)
                {
                    transferProgress.Report(ToTransferProgress(stats));
                }

                if (!await writepack.CommitAsync(stats, cancellationToken).ConfigureAwait(false))
                {
                    throw new GitException(GitErrorCode.Error, "writepack commit failed", GitErrorCategory.Net);
                }
            }
        }
        finally
        {
            // Clean up packetsize callback
            transport.PacketsizeCallback = null;
            await writepack.DisposeAsync().ConfigureAwait(false);
        }

        // Refresh the repo's pack backends so the newly-written pack is
        // visible to Lookups. The pack file was written to
        // <repo>/objects/pack/ by CommitAsync, but the GitObjectDb's
        // PackObjectBackend was loaded at construction time and doesn't
        // know about the new pack.
        await repo.Objects.RefreshPackBackendsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Snapshot the shared <see cref="GitIndexerProgress"/> accumulator into
    /// an immutable <see cref="GitTransferProgress"/> for the
    /// <see cref="GitRemoteCallbacks.TransferProgress"/> callback. The C
    /// port forwards the live <c>git_indexer_progress *stats</c> directly to
    /// the user callback (<c>smart_protocol.c:670</c>); the managed port
    /// instead reports an immutable snapshot (the consumer cannot mutate the
    /// shared accumulator through it).
    /// </summary>
    /// <param name="stats">Live accumulator (read only).</param>
    /// <returns>An immutable snapshot of the current counts.</returns>
    private static GitTransferProgress ToTransferProgress(GitIndexerProgress stats)
        => new(
            TotalObjects: stats.TotalObjects,
            IndexedObjects: stats.IndexedObjects,
            ReceivedObjects: stats.ReceivedObjects,
            LocalObjects: stats.LocalObjects,
            TotalDeltas: stats.TotalDeltas,
            IndexedDeltas: stats.IndexedDeltas,
            ReceivedBytes: stats.ReceivedBytes);

    /// <summary>
    /// Write wants (with capabilities, shallow roots, and depth) to a byte buffer.
    /// Uses <see cref="GitPacketWriter.WriteWants"/> with a StringBuilder, then converts.
    /// </summary>
    private static void WriteWantsToBuffer(IBufferWriter<byte> data, GitFetchNegotiation wants, GitSmartCapabilities caps, GitHashAlgorithmKind oidType)
    {
        GitPacketWriter.WriteWants(data, wants.Refs, caps, oidType, wants.ShallowRoots, wants.Depth);
    }

    // ── Push protocol ─────────────────────────────────────────────────

    /// <summary>
    /// Generate push pkt-line commands: <c>&lt;old-id&gt; &lt;new-id&gt; &lt;refname&gt;\0&lt;capabilities&gt;</c>
    /// for each push spec. Ported from <c>gen_pktline</c> in <c>smart_protocol.c:795</c>.
    /// </summary>
    /// <param name="buffer">The output buffer.</param>
    /// <param name="specs">The push specs with resolved local/remote OIDs.</param>
    /// <param name="reportStatus">Whether to request the report-status capability.</param>
    /// <param name="oidType">The OID hash algorithm.</param>
    /// <param name="hasPushOptions">Whether push options were negotiated.</param>
    internal static void GeneratePushPktline(
        IBufferWriter<byte> buffer,
        IReadOnlyList<GitPushSpec> specs,
        bool reportStatus,
        bool hasPushOptions,
        GitHashAlgorithmKind oidType)
    {
        int oidHexSize = GitOid.HexSizeFor(oidType);

        using var specBuffer = new PooledByteBufferWriter();

        for (int i = 0; i < specs.Count; i++)
        {
            specBuffer.ResetWrittenCount();
            GitPushSpec spec = specs[i];

            // Build the command: "<old-oid> <new-oid> <refname>"
            _ = spec.Roid.IsZero ? GitPacketWriter.WriteZeros(specBuffer, oidHexSize) : GitPacketWriter.WriteOidHex(specBuffer, spec.Roid);
            specBuffer.Write(" "u8);
            _ = spec.Loid.IsZero ? GitPacketWriter.WriteZeros(specBuffer, oidHexSize) : GitPacketWriter.WriteOidHex(specBuffer, spec.Loid);
            specBuffer.Write(" "u8);
            GitPacketWriter.WriteUtf8(specBuffer, spec.RefSpec.Destination);

            // First ref carries capabilities after a NUL
            if (i == 0)
            {
                specBuffer.Write("\0"u8);

                // C (smart_protocol.c:834-848): "Core git always starts their
                // capabilities string with a space" — every capability,
                // including the first, is preceded by a space, so the pkt
                // length and bytes match the wire format.
                if (reportStatus)
                {
                    specBuffer.Write(" report-status"u8);
                }

                if (hasPushOptions)
                {
                    specBuffer.Write(" push-options"u8);
                }

                specBuffer.Write(" side-band-64k"u8);
            }

            specBuffer.Write("\n"u8);

            // Convert to pkt-line with length prefix
            int totalLen = 4 + specBuffer.WrittenCount;
            GitPacketWriter.WriteHexLength(buffer, totalLen);
            buffer.Write(specBuffer.WrittenSpan);
        }

        // Flush
        GitPacketWriter.WriteFlush(buffer);
    }

    /// <summary>
    /// Parse the push report from the server. Reads packets until a flush,
    /// processing UNPACK, OK, and NG packets. If side-band is active, demuxes
    /// data packets first. Ported from <c>parse_report</c> in
    /// <c>smart_protocol.c:959</c>.
    /// </summary>
    /// <param name="transport">The smart transport.</param>
    /// <param name="status">Receives the per-ref push statuses.</param>
    /// <param name="sidebandProgress">Optional sideband progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The unpack-ok status and the per-ref statuses list.</returns>
    internal static async Task<(bool UnpackOk, List<GitPushStatus> Status)> ParsePushReportAsync(
        GitSmartTransport transport,
        List<GitPushStatus> status,
        IProgress<string>? sidebandProgress,
        CancellationToken cancellationToken)
    {
        bool unpackOk = false;
        GitHashAlgorithmKind oidType = transport.OidType;
        bool hasSideband = (transport.SmartCaps.Flags & GitSmartCapabilities.SideBand) != 0 ||
                           (transport.SmartCaps.Flags & GitSmartCapabilities.SideBand64k) != 0;

        // Buffer for inner pkt-lines that span multiple side-band data
        // packets. Parity with data_pkt_buf in parse_report (smart_protocol.c:965).
        using var sidebandBuffer = new PooledByteBufferWriter();

        while (true)
        {
            GitPacket pkt = await RecvPacketAsync(transport, oidType, cancellationToken).ConfigureAwait(false);

            if (pkt is GitFlushPacket)
            {
                // C (smart_protocol.c:1028-1038): when the outer flush
                // arrives, leftover bytes in the side-band data buffer mean
                // the server sent a partial inner pkt-line —
                // "incomplete pack data pkt-line" (GIT_ERROR) — rather than
                // returning whatever had been parsed.
                if (sidebandBuffer.WrittenCount > 0)
                {
                    throw new GitException(GitErrorCode.Error, "incomplete pack data pkt-line", GitErrorCategory.Net);
                }

                break;
            }
            else if (pkt is GitErrorPacket errPkt)
            {
                // C (smart_protocol.c:984-987): the outer report loop reports
                // an ERR packet as "report-status: Error reported: %s".
                throw new GitException(GitErrorCode.Error, $"report-status: Error reported: {errPkt.Message}", GitErrorCategory.Net);
            }
            else if (pkt is GitProgressPacket progressPkt)
            {
                // Side-band progress channel (band 0x02) — already demuxed
                // by GitPacketReader, which stripped the band byte.
                sidebandProgress?.Report(progressPkt.Data);
            }
            else if (pkt is GitDataPacket dataPkt && hasSideband)
            {
                // Side-band data channel (band 0x01) — GitPacketReader already
                // stripped the band byte, so dataPkt.Data is the INNER pkt-line
                // stream (e.g. "000eunpack ok\n0017ok refs/heads/main\n0000").
                // Parse it directly, buffering across packets when an inner
                // pkt-line is split. Parity with add_push_report_sideband_pkt
                // (smart_protocol.c:904-957), which parses data_pkt->data
                // directly — there is NO second band byte here.
                sidebandBuffer.Write(dataPkt.Data);

                while (sidebandBuffer.WrittenCount > 0)
                {
                    if (sidebandBuffer.WrittenCount < 4)
                    {
                        break; // Not enough for the length prefix yet.
                    }

                    int lineLen = ParsePktLineLength(sidebandBuffer.WrittenSpan);
                    if (lineLen < 0)
                    {
                        break; // Incomplete pkt-line — wait for more data.
                    }

                    if (lineLen == 0)
                    {
                        // Inner flush — end of the report-status stream.
                        // C (add_push_report_pkt returns GIT_ITEROVER, which
                        // add_push_report_sideband_pkt treats as non-fatal):
                        // parsing continues until the OUTER flush, so the outer
                        // flush is never left unread in the transport buffer.
                        sidebandBuffer.Consume(4);
                        continue;
                    }

                    if (sidebandBuffer.WrittenCount < lineLen)
                    {
                        break; // Full pkt-line not yet available.
                    }

                    var state = new GitPacketParseState { OidType = oidType, SeenCapabilities = true };
                    GitPacket reportPkt = GitPacketReader.Parse(sidebandBuffer.WrittenSpan, out int consumed, ref state);
                    sidebandBuffer.Consume(consumed);

                    ProcessReportPacket(reportPkt, ref unpackOk, status);
                }
            }
            else
            {
                // Non-side-band: the pkt-line IS the report-status packet.
                ProcessReportPacket(pkt, ref unpackOk, status);
            }
        }

        return (unpackOk, status);
    }

    /// <summary>
    /// Parses a 4-hex-digit pkt-line length prefix from a byte buffer.
    /// Returns the length (including the 4-byte prefix), 0 for flush, or -1 for incomplete.
    /// </summary>
    private static int ParsePktLineLength(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 4)
        {
            return -1;
        }

        int len = 0;
        for (int i = 0; i < 4; i++)
        {
            char c = (char)buffer[i];
            int val = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (val < 0)
            {
                // C (parse_len in smart_pkt.c): a non-hex length digit is
                // "bad packet length" (GIT_ERROR, -1).
                throw new GitException(GitErrorCode.Error, "bad packet length", GitErrorCategory.Net);
            }

            len = (len << 4) | val;
        }

        return len;
    }

    /// <summary>
    /// Processes a single report-status packet (UNPACK, OK, or NG).
    /// Ported from <c>add_push_report_pkt</c> (smart_protocol.c:866-882) —
    /// any other packet type is "report-status: protocol error".
    /// </summary>
    private static void ProcessReportPacket(GitPacket pkt, ref bool unpackOk, List<GitPushStatus> status)
    {
        if (pkt is GitUnpackPacket unpack)
        {
            unpackOk = unpack.UnpackOk;
        }
        else if (pkt is GitOkPacket ok)
        {
            status.Add(new GitPushStatus { Ok = true, Ref = ok.Ref, Message = null });
        }
        else if (pkt is GitNgPacket ng)
        {
            status.Add(new GitPushStatus { Ok = false, Ref = ng.Ref, Message = ng.Message });
        }
        else
        {
            throw new GitException(GitErrorCode.Error, "report-status: protocol error", GitErrorCategory.Net);
        }
    }

    /// <summary> Validate the push report against the push specs and SORT the status list in place. Ported from <c>update_refs_from_report</c>
    /// (smart_protocol.c:1067-1102): the report must contain exactly one status per spec, and after sorting both sides by ref name each status must match the
    /// spec's dst — else "report-status: protocol error". C sorts push->status IN PLACE, so status callbacks and the push result fire in ref-name order.
    /// </summary>
    private static void ValidatePushReport(IReadOnlyList<GitPushSpec> specs, List<GitPushStatus> status)
    {
        if (specs.Count != status.Count)
        {
            throw new GitException(GitErrorCode.Error, "report-status: protocol error", GitErrorCategory.Net);
        }

        // C (smart_protocol.c:1088-1089): git_vector_sort in place with
        // push_status_ref_cmp (strcmp byte order) — byte order, not UTF-16
        // ordinal order, so non-BMP ref names sort correctly.
        status.Sort(static (a, b) => Utils.AsciiText.BytewiseCompare(a.Ref, b.Ref));
        var sortedSpecs = specs.OrderBy(s => s.RefSpec.Destination, Comparer<string>.Create(Utils.AsciiText.BytewiseCompare)).ToList();

        for (int i = 0; i < sortedSpecs.Count; i++)
        {
            if (!string.Equals(sortedSpecs[i].RefSpec.Destination, status[i].Ref, StringComparison.Ordinal))
            {
                throw new GitException(GitErrorCode.Error, "report-status: protocol error", GitErrorCategory.Net);
            }
        }
    }

    /// <summary>
    /// Execute the push operation: send ref update commands, upload the pack
    /// (if any), and parse the report-status response. Ported from
    /// <c>git_smart__push</c> in <c>smart_protocol.c:1185</c>.
    /// </summary>
    /// <param name="transport">The smart transport.</param>
    /// <param name="specs">The push specs with resolved local/remote OIDs.</param>
    /// <param name="packWriter">Optional pre-populated pack writer.</param>
    /// <param name="callbacks">Remote callbacks.</param>
    /// <param name="reportStatus">Whether to request the report-status capability.</param>
    /// <param name="pushOptions">Optional push option strings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The push result.</returns>
    /// <param name="_">Unused callback argument.</param>
    internal static async Task<GitPushResult> PushAsync(
        GitSmartTransport transport,
        GitRepository _,
        IReadOnlyList<GitPushSpec> specs,
        GitPackWriter? packWriter,
        GitRemoteCallbacks? callbacks,
        bool reportStatus,
        IReadOnlyList<string>? pushOptions,
        CancellationToken cancellationToken)
    {
        if (specs.Count == 0)
        {
            // C (smart_protocol.c:795-861, gen_pktline): even with ZERO ref commands the client still opens the stream and writes the "0000" flush pkt
            // (smart_protocol.c:860).
            IGitSubtransportStream emptyStream = await transport.GetPushStreamAsync(cancellationToken).ConfigureAwait(false);
            await emptyStream.WriteAsync("0000"u8.ToArray(), cancellationToken).ConfigureAwait(false);

            // C fires the final push_transfer_progress unconditionally
            // (smart_protocol.c:1254-1263) — a zero-spec push still reports
            // the terminal 0/0/0.
            callbacks?.PushTransferProgress?.Report(new GitPushTransferProgress(Current: 0, Total: 0, Bytes: 0));

            return new GitPushResult { UnpackOk = true, Status = [] };
        }

        // Determine if we need a pack (any spec with non-zero loid)
        bool needPack = false;
        foreach (GitPushSpec spec in specs)
        {
            if (!spec.Loid.IsZero)
            {
                needPack = true;
                break;
            }
        }

        bool hasPushOptions = pushOptions is { Count: > 0 };

        // Prepare the pack if needed
        if (needPack && packWriter is not null)
        {
            await packWriter.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }

        // Get the push stream
        IGitSubtransportStream stream = await transport.GetPushStreamAsync(cancellationToken).ConfigureAwait(false);

        // Build the push command pkt-lines
        using var buffer = new PooledByteBufferWriter();
        GeneratePushPktline(buffer, specs, reportStatus, hasPushOptions, transport.OidType);

        // Write push options (if any) — between two flush packets, after the ref commands
        if (hasPushOptions && pushOptions is not null)
        {
            foreach (string opt in pushOptions)
            {
                // C (smart_protocol.c:853-858): each push option is written
                // as "%04x%s" with strlen(option)+4 — no trailing newline
                // (4+len+1 with an appended "\n" would change the pkt length
                // and payload bytes on the wire).
                int optLen = 4 + Encoding.UTF8.GetByteCount(opt);
                GitPacketWriter.WriteHexLength(buffer, optLen);
                GitPacketWriter.WriteUtf8(buffer, opt);
            }
            GitPacketWriter.WriteFlush(buffer);
        }

        // Send the commands + flush
        await stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        // Send the pack (if needed)
        long bytesWritten = 0;
        if (needPack && packWriter is not null)
        {
            IProgress<GitPackProgress>? packProgress = callbacks?.PackProgress;

            // Stream the pack directly to the transport — no intermediate
            // MemoryStream buffer. For HTTP, each Write appends to the temp
            // file (chunked mode). For git://, each Write sends over the
            // socket (true streaming). Eliminates the 2× pack-size memory
            // overhead of the previous buffer-then-send approach.
            var adapter = new SubtransportStreamAdapter(stream);
            await using ConfiguredAsyncDisposable adapterDisposable = adapter.ConfigureAwait(false);
            await packWriter.WriteAsync(adapter, packProgress, cancellationToken).ConfigureAwait(false);
            bytesWritten = adapter.BytesWritten;
        }

        // C fires the
        // final push_transfer_progress unconditionally
        // (smart_protocol.c:1254-1263), including delete-only pushes (no
        // pack), so progress consumers always see a terminal 0/0/0 report.
        callbacks?.PushTransferProgress?.Report(new GitPushTransferProgress(
            Current: packWriter?.ObjectCount ?? 0,
            Total: packWriter?.ObjectCount ?? 0,
            Bytes: bytesWritten));

        // For SSH (stateful transport), the server's git-receive-pack
        // reads the pack until EOF on the write side. Send EOF after the
        // pack so the server knows the pack is complete and can send the
        // report-status. For HTTP (RPC), the pack is sent as a POST body
        // and the server reads it from the Content-Length, so no EOF is
        // needed.
        if (stream is SshStream sshStream)
        {
            await sshStream.SendWriteEofAsync(cancellationToken).ConfigureAwait(false);
        }

        // Parse the report-status response
        var status = new List<GitPushStatus>();
        bool unpackOk;

        if (reportStatus)
        {
            (unpackOk, status) = await ParsePushReportAsync(transport, status, callbacks?.SidebandProgress, cancellationToken).ConfigureAwait(false);

            // C (smart_protocol.c:1228-1233): update_refs_from_report runs
            // when the report has statuses; a report that omits a status or
            // names a different ref is "report-status: protocol error".
            if (status.Count > 0)
            {
                ValidatePushReport(specs, status);

                // C (smart_protocol.c:1265-1271): the merge-joined refs update the transport's cached ref advertisement, so a still-connected ls reflects the
                // pushed state.
                transport.UpdateRefsFromPush(specs, status);
            }
        }
        else
        {
            // No report-status — assume success
            unpackOk = true;
        }

        // Fire PushUpdateReference callbacks
        if (callbacks?.PushUpdateReference is { } pushUpdateRef)
        {
            foreach (GitPushStatus s in status)
            {
                pushUpdateRef(s.Ref, s.Ok ? null : s.Message);
            }
        }

        return new GitPushResult { UnpackOk = unpackOk, Status = status };
    }
}
