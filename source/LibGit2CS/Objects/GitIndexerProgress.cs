// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Objects;

/// <summary>
/// Mutable progress accumulator for pack indexing/download. Maps to
/// <c>git_indexer_progress</c> in <c>include/git2/indexer.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// In libgit2 <c>git_indexer_progress</c> is a plain C struct passed around
/// <b>by pointer</b> (<c>git_indexer_progress *stats</c>). The smart-protocol
/// download path (<c>git_smart__download_pack</c>, <c>smart_protocol.c:677</c>),
/// the network packetsize callback (<c>network_packetsize</c>,
/// <c>smart_protocol.c:659</c>), and the indexer (<c>git_indexer_append</c>,
/// <c>indexer.c:879</c>; <c>git_indexer_commit</c>, <c>indexer.c:1229</c>) all
/// share <b>one</b> struct instance and mutate it in place — the packetsize
/// callback fires on every network chunk and reads the live
/// <c>received_objects</c>/<c>total_objects</c> counts that the indexer
/// updated on the previous chunk.
/// </para>
/// <para>
/// The faithful C# analog of a pointer to a mutable struct is a
/// <see langword="sealed"/> <b>class</b> — a reference to a mutable object shared
/// between the packetsize closure and the indexer. The indexer writes back
/// to the same instance; the closure (which fires from
/// <c>GitSmartTransport.RecvAsync</c>, outside any <c>AppendAsync</c> call)
/// sees those updates without any <c>ref</c> threading or return-tuple
/// plumbing.
/// </para>
/// <para>
/// <b>Field set.</b> Matches the C struct 1:1, including
/// <see cref="TotalObjects"/> (<c>total_objects</c> — set from the pack
/// header by the indexer) and <see cref="IndexedDeltas"/>
/// (<c>indexed_deltas</c> — incremented during delta resolution), both of
/// preserved across progress reports.
/// </para>
/// </remarks>
public sealed class GitIndexerProgress
{
    /// <summary>Number of objects in the packfile being indexed
    /// (<c>total_objects</c>). Set from the pack header by the indexer.</summary>
    public int TotalObjects { get; set; }

    /// <summary>Received objects that have been hashed
    /// (<c>indexed_objects</c>).</summary>
    public int IndexedObjects { get; set; }

    /// <summary>Objects which have been downloaded (<c>received_objects</c>).</summary>
    public int ReceivedObjects { get; set; }

    /// <summary>Locally-available objects injected to fix a thin pack
    /// (<c>local_objects</c>).</summary>
    public int LocalObjects { get; set; }

    /// <summary>Number of deltas in the packfile being indexed
    /// (<c>total_deltas</c>).</summary>
    public int TotalDeltas { get; set; }

    /// <summary>Received deltas that have been indexed
    /// (<c>indexed_deltas</c>).</summary>
    public int IndexedDeltas { get; set; }

    /// <summary>Size of the packfile received up to now, in bytes
    /// (<c>received_bytes</c>). Accumulated by the packetsize callback on
    /// every network chunk.</summary>
    public long ReceivedBytes { get; set; }

    /// <summary>Creates a zero-initialized progress accumulator.</summary>
    public GitIndexerProgress()
    {
    }
}
