// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;
using LibGit2CS.Utils;

namespace LibGit2CS.Pack;

/// <summary>
/// Pack builder: enumerates objects from a repository, computes deltas
/// against a sliding window, and writes a git pack file (<c>.pack</c>).
/// Managed port of <c>src/libgit2/pack-objects.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-threaded.</b> The C code supports multi-threaded delta
/// computation via a work-stealing thread pool. This managed port runs
/// single-threaded — delta computation is pure CPU work that the JIT
/// optimizes well; the threading complexity (mutex/condvar/work-stealing)
/// is not worth the ~2× speedup for programmatic pack creation.
/// </para>
/// <para>
/// <b>Pack format</b>:
/// <code>
/// "PACK" (4 bytes)
/// version 2 (4 bytes, big-endian)
/// object count (4 bytes, big-endian)
/// [objects...]
/// SHA-1/SHA-256 trailer (20 or 32 bytes)
/// </code>
/// </para>
/// <para>
/// Each object: variable-length type+size header, optional REF_DELTA base OID,
/// zlib-compressed body. All deltas use REF_DELTA (not OFS_DELTA) matching
/// libgit2's pack-objects.c.
/// </para>
/// <para>
/// <b>Parity (pack-objects):</b> the delta search runs over a <em>copy</em>
/// of the object list filtered
/// to [50, big-file-threshold] bytes and sorted by C's <c>type_size_sort</c>
/// (raw type descending, name-hash descending, size descending, recency), the
/// circular window keeps the <em>nearest</em> successful base (C overwrites on
/// every success, walking far→near, with the shallower-same-size preference),
/// the write order is computed over the original insertion-ordered list with
/// the tagged-tip phases and <c>write_one</c> base-first recursion, and
/// <c>InsertWalkAsync</c> marks trees/blobs reachable from hidden commits
/// uninteresting before walking so the pack holds the incremental object set.
/// </para>
/// </remarks>
public sealed class GitPackWriter : IDisposable
{
    /// <summary>Default sliding window size for delta search (matches <c>GIT_PACK_WINDOW</c>).</summary>
    private const int DefaultWindow = 10;

    /// <summary>Maximum delta chain depth (matches <c>GIT_PACK_DEPTH</c>).</summary>
    private const int MaxDepth = 50;

    /// <summary>Default big file threshold — objects larger than this are not delta-compressed.</summary>
    private const long DefaultBigFileThreshold = 512 * 1024 * 1024;

    private readonly GitRepository _repo;
    private readonly GitObjectDb _odb;
    private readonly GitHashAlgorithmKind _algorithm;
    private readonly List<PackObject> _objectList = [];
    private readonly Dictionary<GitOid, PackObject> _objectIndex = [];
    private readonly int _window = DefaultWindow;
    private long _bigFileThreshold = DefaultBigFileThreshold;
    private long _windowMemoryLimit;
    private bool _configRead;
    private bool _prepared;
    private bool _disposed;

    /// <summary> Creates a pack builder for the given repository. </summary> <param name="repo">The repository whose objects are packed.</param> <param
    /// name="threads">Packbuilder thread count — C's <c>git_packbuilder_set_threads</c> (pack-objects.c:177-189, push.c:460). 0 means auto-detect (C's
    /// prepare_pack maps 0 to <c>git__online_cpus</c>); the managed delta search is single-threaded, so any value produces the same pack as C's single-threaded
    /// path (bit-exact for the default 1; larger values are accepted for API parity — C's threaded delta partitioning is not ported).</param>
    internal GitPackWriter(GitRepository repo, int threads = 1)
    {
        ArgumentNullException.ThrowIfNull(repo);
        _repo = repo;
        _odb = repo.Objects;
        _algorithm = repo.ObjectFormat;
        Threads = threads;
    }

    /// <summary> The packbuilder thread count (0 = auto). Accepted for API parity with <c>git_packbuilder_set_threads</c>; the managed delta search is
    /// single-threaded. </summary>
    public int Threads { get; }

    /// <summary>
    /// Reads the pack.* configuration, matching C's <c>packbuilder_config</c>
    /// (pack-objects.c:90-128): <c>pack.deltaCacheSize</c> sets the
    /// big-file threshold (the C quirk — the same key drives both
    /// max_delta_cache_size and big_file_threshold) and <c>pack.windowMemory</c>
    /// sets the window memory limit.
    /// </summary>
    private ValueTask ReadConfigAsync(CancellationToken cancellationToken)
    {
        if (_configRead)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(ReadConfigSlowAsync(cancellationToken));
    }

    private async Task ReadConfigSlowAsync(CancellationToken cancellationToken)
    {
        _configRead = true;
        _bigFileThreshold = await _repo.Config.GetInt64Async("pack.deltaCacheSize", DefaultBigFileThreshold, cancellationToken).ConfigureAwait(false);
        _windowMemoryLimit = await _repo.Config.GetInt64Async("pack.windowMemory", 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The number of objects inserted so far.</summary>
    public int ObjectCount => _objectList.Count;

    /// <summary>
    /// Inserts an object by OID. Reads the object's type and size from the ODB.
    /// Idempotent — inserting the same OID twice is a no-op.
    /// Matches <c>git_packbuilder_insert</c> with <c>name = NULL</c> (name
    /// hash 0). The tree/blob walk paths pass names via the named overload.
    /// </summary>
    public async Task InsertAsync(GitOid oid, CancellationToken cancellationToken = default)
        => await InsertAsync(oid, (GitPath?)null, progress: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Inserts an object with progress reporting but no name (hash 0).</summary>
    public async Task InsertAsync(GitOid oid, IProgress<GitPackProgress>? progress, CancellationToken cancellationToken = default)
        => await InsertAsync(oid, (GitPath?)null, progress, cancellationToken).ConfigureAwait(false);

    /// <summary> Inserts an object by OID, reporting C's ADDING_OBJECTS progress (pack-objects.c:257-271). <paramref name="name"/> is the object's path for the
    /// name hash (C's <c>git_packbuilder_insert</c> name argument; NULL → hash 0). Matches pack-objects.c:248 (<c>po->hash = name_hash(name)</c>) — the hash
    /// feeds <c>type_size_sort</c>'s delta-base ordering. </summary>
    public async Task InsertAsync(GitOid oid, string? name, IProgress<GitPackProgress>? progress, CancellationToken cancellationToken = default)
        => await InsertAsync(oid, name is null ? null : GitPath.FromUtf8String(name), progress, cancellationToken).ConfigureAwait(false);

    /// <summary> Inserts an object by OID with a byte-faithful name. byte-parity surface — C's <c>name_hash</c> folds the raw name bytes
    /// (pack-objects.c:70-88), so tree-walk names must not round-trip through a lossy UTF-8 decode (non-UTF-8 names would hash differently and change
    /// delta-base selection → non-bit-exact pack). </summary>
    public async Task InsertAsync(GitOid oid, GitPath? name, IProgress<GitPackProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (_objectIndex.ContainsKey(oid))
        {
            return;
        }

        GitObjectHeader? header = await _odb.ReadHeaderAsync(oid, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            throw new GitException(GitErrorCode.NotFound, $"object {oid} not found", GitErrorCategory.Odb);
        }

        var po = new PackObject
        {
            Id = oid,
            Type = header.Value.Type,
            Size = header.Value.Size,
            Hash = NameHash(name is { } n ? n.Span : ReadOnlySpan<byte>.Empty),
            InsertionIndex = _objectList.Count,
        };

        _objectList.Add(po);
        _objectIndex[oid] = po;

        // C (pack-objects.c:255): any new insert after prepare resets
        // done=false so the next prepare re-runs the delta search over ALL
        // objects (pack-objects.c:1336-1343).
        _prepared = false;

        // C (pack-objects.c:257-271): ADDING_OBJECTS progress with the running
        // object count (total 0).
        progress?.Report(new GitPackProgress(Stage: 0, Current: _objectList.Count, Total: 0));
    }

    /// <summary>
    /// Port of <c>name_hash</c> (pack-objects.c:70-88): a sortable hash of
    /// the last sixteen non-whitespace characters (whitespace skipped,
    /// later characters weigh more). Null name → 0. UTF-8 convenience —
    /// the byte-parity surface is <see cref="NameHash(ReadOnlySpan{byte})"/>.
    /// </summary>
    internal static uint NameHash(string? name)
        => name is null ? 0 : NameHash(Encoding.UTF8.GetBytes(name));

    /// <summary> Port of <c>name_hash</c> (pack-objects.c:70-88) over raw bytes. byte-parity surface — C folds the raw on-disk name bytes; the string
    /// overload re-encodes a lossy decode, which diverges for non-UTF-8 names. An empty span (the empty name) folds to 0 like C's <c>name_hash("")</c>.
    /// </summary>
    internal static uint NameHash(ReadOnlySpan<byte> name)
    {
        uint hash = 0;
        foreach (byte c in name)
        {
            if (c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r')
            {
                continue;
            }

            hash = (hash >> 2) + ((uint)c << 24);
        }

        return hash;
    }

    /// <summary> Inserts a tree and all its contents recursively. Matches <c>git_packbuilder_insert_tree</c> (pack-objects.c:1526-1539): the walk runs even
    /// when the tree was already inserted (via <c>git_packbuilder_insert</c>) — a bare tree insert does NOT suppress the subtree walk. Entry names are the
    /// root-relative paths with '/' separators, matching <c>cb_tree_walk</c>'s name for the hash (pack-objects.c:1493-1510). The walk names are inserted as
    /// raw bytes (C's <c>cb_tree_walk</c> passes the raw path buffer; the string bridge would corrupt non-UTF-8 names). </summary>
    public async Task InsertTreeAsync(GitOid treeOid, CancellationToken cancellationToken = default)
    {
        await InsertAsync(treeOid, cancellationToken).ConfigureAwait(false);

        GitTree? tree = await _odb.LookupAsync<GitTree>(treeOid, cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return;
        }

        await foreach ((GitPath entryPath, GitTreeEntry entry) in tree.WalkAsync(GitTreeWalkMode.PreOrder, cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsTree)
            {
                await InsertAsync(entry.Id, entryPath, null, cancellationToken).ConfigureAwait(false);
            }
            else if (!entry.IsGitLink)
            {
                await InsertAsync(entry.Id, entryPath, null, cancellationToken).ConfigureAwait(false);
            }
        }

        tree.Dispose();
    }

    /// <summary>
    /// Inserts a commit and its tree. Matches <c>git_packbuilder_insert_commit</c>
    /// (pack-objects.c:1511-1524) — the commit's parents are NOT inserted.
    /// </summary>
    public async Task InsertCommitAsync(GitOid commitOid, CancellationToken cancellationToken = default)
    {
        await InsertAsync(commitOid, cancellationToken).ConfigureAwait(false);

        Commit? commit = await _odb.LookupAsync<Commit>(commitOid, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return;
        }

        try
        {
            await InsertTreeAsync(commit.Tree, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commit.Dispose();
        }
    }

    /// <summary> Recursively inserts an object by type. Matches <c>git_packbuilder_insert_recur</c> (pack-objects.c:1541-1576): blobs are inserted directly,
    /// trees via <see cref="InsertTreeAsync"/>, commits via <see cref="InsertCommitAsync"/>, and tags are inserted with their target recursively. The name is
    /// threaded through for BLOB and TAG inserts (C passes it at pack-objects.c:1554 and:1563). </summary>
    public async Task InsertRecurAsync(GitOid id, string? name, CancellationToken cancellationToken = default)
    {
        GitObject? obj = await _odb.LookupAsync(id, cancellationToken).ConfigureAwait(false);
        if (obj is null)
        {
            throw new GitException(GitErrorCode.NotFound, $"object {id} not found", GitErrorCategory.Odb);
        }

        try
        {
            switch (obj)
            {
                case GitBlob:
                    await InsertAsync(id, name, null, cancellationToken).ConfigureAwait(false);
                    break;
                case GitTree:
                    await InsertTreeAsync(id, cancellationToken).ConfigureAwait(false);
                    break;
                case Commit:
                    await InsertCommitAsync(id, cancellationToken).ConfigureAwait(false);
                    break;
                case GitTag tag:
                    await InsertAsync(id, name, null, cancellationToken).ConfigureAwait(false);
                    await InsertRecurAsync(tag.Target, null, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new GitException(GitErrorCode.Invalid, "unknown object type", GitErrorCategory.Invalid);
            }
        }
        finally
        {
            obj.Dispose();
        }
    }

    /// <summary>
    /// Inserts all commits (and their trees) from a revwalk. Matches
    /// <c>git_packbuilder_insert_walk</c> (pack-objects.c:1786-1821):
    /// the trees/blobs reachable from every hidden (uninteresting) commit
    /// are marked uninteresting <em>before</em> the walk, and objects
    /// already marked uninteresting are skipped even when they are also
    /// reachable from an interesting commit — so the pack contains exactly
    /// the incremental object set the remote does not have.
    /// </summary>
    public async Task InsertWalkAsync(GitRevWalker walk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(walk);

        var walkObjects = new Dictionary<GitOid, WalkObject>();

        // mark_edges_uninteresting (pack-objects.c:1684-1705): the commits
        // are already uninteresting; mark their trees and blobs.
        foreach (CommitListNode root in walk.UserInput)
        {
            if (!root.IsUninteresting)
            {
                continue;
            }

            Commit? commit = await _odb.LookupAsync<Commit>(root.Oid, cancellationToken).ConfigureAwait(false);
            if (commit is null)
            {
                continue;
            }

            await MarkTreeUninterestingAsync(commit.Tree, walkObjects, cancellationToken).ConfigureAwait(false);
            commit.Dispose();
        }

        // Walk down each tree up to the blobs and insert them, stopping
        // when uninteresting (pack-objects.c:1802-1819).
        await foreach (GitOid commitOid in walk.WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            WalkObject obj = GetOrCreateWalkObject(walkObjects, commitOid);
            if (obj.Seen || obj.Uninteresting)
            {
                continue;
            }

            await InsertWalkCommitAsync(commitOid, walkObjects, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class WalkObject
    {
        public bool Seen;
        public bool Uninteresting;
    }

    private static WalkObject GetOrCreateWalkObject(Dictionary<GitOid, WalkObject> map, GitOid oid)
    {
        if (!map.TryGetValue(oid, out WalkObject? obj))
        {
            obj = new WalkObject();
            map[oid] = obj;
        }

        return obj;
    }

    private async Task MarkTreeUninterestingAsync(GitOid treeOid, Dictionary<GitOid, WalkObject> walkObjects, CancellationToken cancellationToken)
    {
        WalkObject obj = GetOrCreateWalkObject(walkObjects, treeOid);
        if (obj.Uninteresting)
        {
            return;
        }

        obj.Uninteresting = true;

        GitTree? tree = await _odb.LookupAsync<GitTree>(treeOid, cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return;
        }

        for (int e = 0; e < tree.EntryCount; e++)
        {
            GitTreeEntry entry = tree.EntryByIndex(e) ?? default;
            if (entry.IsTree)
            {
                await MarkTreeUninterestingAsync(entry.Id, walkObjects, cancellationToken).ConfigureAwait(false);
            }
            else if (!entry.IsGitLink)
            {
                GetOrCreateWalkObject(walkObjects, entry.Id).Uninteresting = true;
            }
        }

        tree.Dispose();
    }

    private async Task InsertWalkCommitAsync(GitOid commitOid, Dictionary<GitOid, WalkObject> walkObjects, CancellationToken cancellationToken)
    {
        WalkObject obj = GetOrCreateWalkObject(walkObjects, commitOid);
        obj.Seen = true;

        await InsertAsync(commitOid, cancellationToken).ConfigureAwait(false);

        Commit? commit = await _odb.LookupAsync<Commit>(commitOid, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return;
        }

        await InsertWalkTreeAsync(commit.Tree, walkObjects, cancellationToken).ConfigureAwait(false);
        commit.Dispose();
    }

    private async Task InsertWalkTreeAsync(GitOid treeOid, Dictionary<GitOid, WalkObject> walkObjects, CancellationToken cancellationToken)
    {
        // pack_objects_insert_tree (pack-objects.c:1707-1757).
        WalkObject obj = GetOrCreateWalkObject(walkObjects, treeOid);
        if (obj.Seen || obj.Uninteresting)
        {
            return;
        }

        obj.Seen = true;

        await InsertAsync(treeOid, cancellationToken).ConfigureAwait(false);

        GitTree? tree = await _odb.LookupAsync<GitTree>(treeOid, cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return;
        }

        for (int e = 0; e < tree.EntryCount; e++)
        {
            GitTreeEntry entry = tree.EntryByIndex(e) ?? default;
            if (entry.IsTree)
            {
                await InsertWalkTreeAsync(entry.Id, walkObjects, cancellationToken).ConfigureAwait(false);
            }
            else if (!entry.IsGitLink)
            {
                if (GetOrCreateWalkObject(walkObjects, entry.Id).Uninteresting)
                {
                    continue;
                }

                // C (pack-objects.c:1746-1748): blobs are inserted with the entry NAME (basename) for the name hash over the raw name bytes (C folds the raw name bytes).
                await InsertAsync(entry.Id, entry.Name, null, cancellationToken).ConfigureAwait(false);
            }
        }

        tree.Dispose();
    }

    /// <summary>
    /// Runs delta computation: sorts a filtered copy of the object list,
    /// applies the sliding window delta search, and selects the best delta
    /// base for each object. Must be called before <see cref="WriteAsync"/>.
    /// Matches <c>git_packbuilder__prepare</c> (pack-objects.c:1336-1382).
    /// </summary>
    public async ValueTask PrepareAsync(CancellationToken cancellationToken = default)
        => await PrepareAsync(progress: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Prepares pack objects and deltas, optionally reporting progress.</summary>
    public async ValueTask PrepareAsync(IProgress<GitPackProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (_prepared)
        {
            return;
        }

        await ReadConfigAsync(cancellationToken).ConfigureAwait(false);

        // C (pack-objects.c:1349-1352): report the start of the deltafication
        // stage (DELTAFICATION, 0, nr_objects).
        progress?.Report(new GitPackProgress(Stage: 1, Current: 0, Total: _objectList.Count));

        // C (pack-objects.c:1357-1362): the delta list is a COPY of the
        // object pointers, filtered to the size limits; the original
        // object_list keeps its insertion (recency) order for the write
        // order.
        var deltaList = new List<PackObject>(_objectList.Count);
        foreach (PackObject po in _objectList)
        {
            if (po.Size < 50 || po.Size > _bigFileThreshold)
            {
                continue;
            }

            deltaList.Add(po);
        }

        if (deltaList.Count > 1)
        {
            // git__tsort with type_size_sort (stable; the comparator's
            // recency tiebreak makes it a total order anyway).
            deltaList.Sort(TypeSizeSort);
            await FindDeltasAsync(deltaList, progress, cancellationToken).ConfigureAwait(false);
        }

        // C (pack-objects.c:1377): report the forced final DELTAFICATION
        // (nr_objects, nr_objects).
        progress?.Report(new GitPackProgress(Stage: 1, Current: _objectList.Count, Total: _objectList.Count));

        _prepared = true;
    }

    /// <summary>
    /// Sort comparator matching <c>type_size_sort</c> (pack-objects.c:701-727):
    /// type <b>descending</b> by raw enum value (TAG=4, BLOB=3, TREE=2,
    /// COMMIT=1), then name hash descending, then size descending, then
    /// insertion order (newest first).
    /// </summary>
    private static int TypeSizeSort(PackObject a, PackObject b)
    {
        int typeCmp = b.Type.CompareTo(a.Type);
        if (typeCmp != 0)
        {
            return typeCmp;
        }

        int hashCmp = b.Hash.CompareTo(a.Hash);
        if (hashCmp != 0)
        {
            return hashCmp;
        }

        int sizeCmp = b.Size.CompareTo(a.Size);
        if (sizeCmp != 0)
        {
            return sizeCmp;
        }

        // C (pack-objects.c, type_size_sort): the final tiebreak compares
        // the git_pobject ADDRESSES — contiguously allocated in insertion
        // order, so lower addresses (= earlier insertions) sort FIRST. The
        // port used b-vs-a here (newest first), which reversed the delta
        // direction for equal-size same-type objects (golden pack probe).
        return a.InsertionIndex.CompareTo(b.InsertionIndex);
    }

    /// <summary>
    /// One slot of the circular delta window. Matches C's
    /// <c>struct unpacked</c> (pack-objects.c:940-953): the object, its
    /// current delta depth in the window, and cached data/index.
    /// </summary>
    private sealed class Unpacked
    {
        public PackObject? Object;
        public int Depth;
        // Stable ODB memory, retained without copying and shared with Index.Src.
        public ReadOnlyMemory<byte>? Data;
        public DeltaEncoder.DeltaIndex? Index;
    }

    /// <summary>
    /// Frees a window slot's cached data and index. Matches <c>free_unpacked</c>
    /// (pack-objects.c:915-937); returns the freed bytes.
    /// </summary>
    private static long FreeUnpacked(Unpacked n)
    {
        long freed = 0;
        if (n.Data is not null)
        {
            freed = n.Data.Value.Length;
            n.Data = null;
        }

        if (n.Index is not null)
        {
            freed += n.Index.TableMemorySize;
            n.Index = null;
        }

        return freed;
    }

    /// <summary>
    /// Sliding window delta search over the sorted delta list. Matches
    /// <c>find_deltas</c> (pack-objects.c:955-1082, single-threaded): a
    /// circular window of <c>GIT_PACK_WINDOW + 1</c> slots; each object is
    /// tried against the window from the <em>farthest</em> slot to the
    /// nearest, and every successful delta REPLACES the previous one (the
    /// nearest successful base wins), subject to the shallower-same-size
    /// preference. The winning base is rotated to the front of the window
    /// to keep it longer.
    /// </summary>
    private async Task FindDeltasAsync(
        List<PackObject> list,
        IProgress<GitPackProgress>? progress,
        CancellationToken cancellationToken)
    {
        int window = _window + 1;
        var array = new Unpacked[window];
        for (int i = 0; i < window; i++)
        {
            array[i] = new Unpacked();
        }

        int idx = 0;
        int count = 0;
        long memUsage = 0;
        int deltified = 0;

        foreach (PackObject po in list)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // C (pack-objects.c:982-984): free the slot's cached data before
            // reusing it for the new object (mem_usage -= free_unpacked(n)).
            Unpacked n = array[idx];
            memUsage -= FreeUnpacked(n);
            n.Object = po;
            n.Depth = 0;

            // C (pack-objects.c:995-1003): enforce the window memory limit by
            // evicting the oldest slots, including cached data and delta indexes.
            while (_windowMemoryLimit != 0 && memUsage > _windowMemoryLimit && count > 1)
            {
                int tail = (idx + window - count) % window;
                memUsage -= FreeUnpacked(array[tail]);
                count--;
            }

            // max_depth: C consults po->delta_child (check_delta_limit),
            // which is only built at write time — NULL during this pass.
            const int maxDepth = MaxDepth;
            int bestBase = -1;

            // j = window; while (--j > 0) — distances window-1 down to 1
            // (farthest first; the nearest success overwrites).
            for (int j = window - 1; j >= 1; j--)
            {
                int otherIdx = idx + j;
                if (otherIdx >= window)
                {
                    otherIdx -= window;
                }

                Unpacked m = array[otherIdx];
                if (m.Object is null)
                {
                    break;
                }

                (int ret, long memAdded) = await TryDeltaAsync(n, m, maxDepth, cancellationToken).ConfigureAwait(false);
                memUsage += memAdded;
                if (ret < 0)
                {
                    break; // type mismatch — stop trying
                }

                if (ret > 0)
                {
                    bestBase = otherIdx;
                }
            }

            // If we made n a delta and it is already at max depth, leaving
            // it in the window is pointless — evict it first (C's continue
            // also skips the idx/count advance).
            if (n.Object.Delta is not null && maxDepth <= n.Depth)
            {
                continue;
            }

            // Move the best delta base up in the window, after the currently
            // deltified object, to keep it longer (pack-objects.c:1067-1079).
            if (n.Object.Delta is not null && bestBase >= 0)
            {
                Unpacked swap = array[bestBase];
                int dist = (window + idx - bestBase) % window;
                int dst = bestBase;
                while (dist-- > 0)
                {
                    int src = (dst + 1) % window;
                    array[dst] = array[src];
                    dst = src;
                }

                array[dst] = swap;
            }

            idx++;
            if (count + 1 < window)
            {
                count++;
            }

            if (idx >= window)
            {
                idx = 0;
            }

            // C (pack-objects.c:984-988): per-object DELTAFICATION progress
            // with the running deltified count.
            if (po.Delta is not null)
            {
                deltified++;
            }

            progress?.Report(new GitPackProgress(Stage: 1, Current: deltified, Total: list.Count));
        }
    }

    /// <summary>
    /// Tries to delta <paramref name="trg"/> against <paramref name="src"/>.
    /// Matches <c>try_delta</c> (pack-objects.c:753-895). Returns -1 for a
    /// type mismatch (aborts the window walk), 1 on a successful delta,
    /// 0 otherwise.
    /// </summary>
    private async Task<(int Ret, long MemAdded)> TryDeltaAsync(Unpacked trg, Unpacked src, int maxDepth, CancellationToken cancellationToken)
    {
        PackObject trgObject = trg.Object!;
        PackObject srcObject = src.Object!;

        // Don't bother doing diffs between different types.
        if (trgObject.Type != srcObject.Type)
        {
            return (-1, 0);
        }

        // Let's not bust the allowed depth.
        if (src.Depth >= maxDepth)
        {
            return (0, 0);
        }

        // Now some size filtering heuristics.
        long trgSize = trgObject.Size;
        long maxSize;
        long refDepth;
        if (trgObject.Delta is null)
        {
            maxSize = trgSize / 2 - 20;
            refDepth = 1;
        }
        else
        {
            maxSize = trgObject.DeltaSize;
            refDepth = trg.Depth;
        }

        maxSize = maxSize * (maxDepth - src.Depth) / (maxDepth - refDepth + 1);
        if (maxSize == 0)
        {
            return (0, 0);
        }

        long srcSize = srcObject.Size;
        long sizediff = srcSize < trgSize ? trgSize - srcSize : 0;
        if (sizediff >= maxSize)
        {
            return (0, 0);
        }

        if (trgSize < srcSize / 32)
        {
            return (0, 0);
        }

        // Load data — C caches it in the window slot (try_delta,
        // pack-objects.c:790-830), along with the reusable source index. ODB
        // buffers remain stable after object disposal; retain only the memory.
        // Charge each slot its logical length even if the ODB also retains it.
        long memAdded = 0;
        if (src.Data is null)
        {
            GitObject? sourceObj = await _odb.LookupAsync(srcObject.Id, cancellationToken).ConfigureAwait(false);
            if (sourceObj is null)
            {
                return (0, memAdded);
            }

            src.Data = sourceObj.Raw;
            sourceObj.Dispose();
            memAdded += src.Data.Value.Length;
        }

        if (trg.Data is null)
        {
            GitObject? targetObj = await _odb.LookupAsync(trgObject.Id, cancellationToken).ConfigureAwait(false);
            if (targetObj is null)
            {
                return (0, memAdded);
            }

            trg.Data = targetObj.Raw;
            targetObj.Dispose();
            memAdded += trg.Data.Value.Length;
        }

        if (src.Index is null)
        {
            src.Index = DeltaEncoder.BuildIndexFromRetainedBuffer(src.Data.Value);
            if (src.Index is not null)
            {
                memAdded += src.Index.TableMemorySize;
            }
        }

        byte[]? delta = src.Index is null
            ? []
            : DeltaEncoder.CreateFromIndex(src.Index, trg.Data.Value, maxSize > int.MaxValue ? int.MaxValue : (int)maxSize);

        // git_delta_create_from_index returns GIT_EBUFS when the delta
        // exceeds max_size → no delta.
        if (delta is null)
        {
            return (0, memAdded);
        }

        if (trgObject.Delta is not null)
        {
            // Prefer only shallower same-sized deltas.
            if (delta.Length == trgObject.DeltaSize && src.Depth + 1 >= trg.Depth)
            {
                return (0, memAdded);
            }
        }

        trgObject.Delta = srcObject;
        trgObject.DeltaSize = delta.Length;
        trgObject.DeltaData = delta;
        trg.Depth = src.Depth + 1;
        return (1, memAdded);
    }

    /// <summary>
    /// Marks objects that are at the tip of tags. Matches
    /// <c>git_tag_foreach</c> + <c>cb_tag_foreach</c> (pack-objects.c:512-527):
    /// the TAG OBJECT itself (not the peeled target — the C has a TODO) is
    /// marked when it is in the pack.
    /// </summary>
    private async Task MarkTaggedTipsAsync(CancellationToken cancellationToken)
    {
        await foreach (RefNameKey refName in _repo.Refs.ListNameKeysAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (!refName.StartsWith("refs/tags/"u8))
            {
                continue;
            }

            GitReference? reference = await _repo.Refs.LookupAsync(refName, cancellationToken).ConfigureAwait(false);
            if (reference is not GitDirectReference direct)
            {
                continue;
            }

            if (_objectIndex.TryGetValue(direct.Target, out PackObject? po))
            {
                po.Tagged = true;
            }
        }
    }

    /// <summary>
    /// C (pack-objects.c:623-627, compute_write_order): the write order must
    /// cover every object — a mismatch is "invalid write order". The two write paths
    /// used different header counts (order.Count vs _objectList.Count) with
    /// no local check, so a traversal regression would silently produce a
    /// wrong-count pack.
    /// </summary>
    private static void ValidateWriteOrderCount(int orderCount, int objectCount)
    {
        if (orderCount != objectCount)
        {
            throw new GitException(GitErrorCode.Invalid, "invalid write order", GitErrorCategory.Invalid);
        }
    }

    /// <summary>
    /// Computes the write order over the ORIGINAL insertion-ordered object
    /// list. Matches <c>compute_write_order</c> (pack-objects.c:529-631):
    /// recency order until a tagged tip, then the tagged tips, then the
    /// remaining commits and tags, then the trees, then the rest in
    /// delta-family order. The <c>write_one</c> pass resolves bases first.
    /// </summary>
    private async Task<List<PackObject>> ComputeWriteOrderAsync(CancellationToken cancellationToken)
    {
        var order = new List<PackObject>(_objectList.Count);

        // Reset per-write state and fully connect the delta_child/sibling
        // network, making delta_sibling sorted in original recency order
        // (pack-objects.c:540-557).
        for (int r = _objectList.Count; r > 0;)
        {
            PackObject po = _objectList[--r];
            po.Tagged = false;
            po.Filled = false;
            po.Recursing = false;
            po.Written = false;
            po.DeltaChild = null;
            po.DeltaSibling = null;
        }

        for (int r = _objectList.Count; r > 0;)
        {
            PackObject po = _objectList[--r];
            if (po.Delta is null)
            {
                continue;
            }

            po.DeltaSibling = po.Delta.DeltaChild;
            po.Delta.DeltaChild = po;
        }

        await MarkTaggedTipsAsync(cancellationToken).ConfigureAwait(false);

        // Give the objects in the original recency order until we see a
        // tagged tip.
        int i = 0;
        for (; i < _objectList.Count; i++)
        {
            PackObject po = _objectList[i];
            if (po.Tagged)
            {
                break;
            }

            AddToWriteOrder(order, po);
        }

        int lastUntagged = i;

        // Then fill all the tagged tips.
        for (; i < _objectList.Count; i++)
        {
            PackObject po = _objectList[i];
            if (po.Tagged)
            {
                AddToWriteOrder(order, po);
            }
        }

        // And then all remaining commits and tags.
        for (i = lastUntagged; i < _objectList.Count; i++)
        {
            PackObject po = _objectList[i];
            if (po.Type is not (GitObjectType.Commit or GitObjectType.Tag))
            {
                continue;
            }

            AddToWriteOrder(order, po);
        }

        // And then all the trees.
        for (i = lastUntagged; i < _objectList.Count; i++)
        {
            PackObject po = _objectList[i];
            if (po.Type != GitObjectType.Tree)
            {
                continue;
            }

            AddToWriteOrder(order, po);
        }

        // Finally all the rest in really tight order.
        for (i = lastUntagged; i < _objectList.Count; i++)
        {
            PackObject po = _objectList[i];
            if (!po.Filled)
            {
                AddFamilyToOrder(order, po);
            }
        }

        return order;
    }

    private static void AddToWriteOrder(List<PackObject> order, PackObject po)
    {
        if (po.Filled)
        {
            return;
        }

        order.Add(po);
        po.Filled = true;
    }

    /// <summary>
    /// Adds a delta chain to the write order: the root first, then all
    /// descendants (children and their siblings, depth-first). Matches
    /// <c>add_family_to_write_order</c> + <c>add_descendants_to_write_order</c>
    /// (pack-objects.c:492-532).
    /// </summary>
    private static void AddFamilyToOrder(List<PackObject> order, PackObject po)
    {
        PackObject root = po;
        while (root.Delta is not null)
        {
            root = root.Delta;
        }

        AddDescendantsToOrder(order, root);
    }

    private static void AddDescendantsToOrder(List<PackObject> order, PackObject po)
    {
        PackObject? current = po;
        bool addToOrder = true;
        while (current is not null)
        {
            if (addToOrder)
            {
                // Add this node and all its siblings.
                AddToWriteOrder(order, current);
                for (PackObject? s = current.DeltaSibling; s is not null; s = s.DeltaSibling)
                {
                    AddToWriteOrder(order, s);
                }
            }

            // Drop down a level to add left subtree nodes if possible.
            if (current.DeltaChild is not null)
            {
                addToOrder = true;
                current = current.DeltaChild;
            }
            else
            {
                addToOrder = false;
                // Our sibling might have some children, it is next.
                if (current.DeltaSibling is not null)
                {
                    current = current.DeltaSibling;
                    continue;
                }

                // Go back to our parent node.
                current = current.Delta;
                while (current is not null && current.DeltaSibling is null)
                {
                    current = current.Delta;
                }

                if (current is null)
                {
                    return; // done — we hit our original root node
                }

                // Pass it off to the sibling at this level (pack-objects.c:496-498). Without this advance, the next iteration re-descends into the
                // already-visited first child of the parent and oscillates forever on branching delta chains.
                current = current.DeltaSibling;
            }
        }
    }

    private enum WriteOneStatus
    {
        /// <summary>Already written.</summary>
        Skip = -1,

        /// <summary>Already scheduled to be written (cycle).</summary>
        Recursive = 2,

        /// <summary>Normal.</summary>
        Written = 1,
    }

    /// <summary>
    /// Writes the pack to a stream. Matches <c>write_pack</c> +
    /// <c>git_packbuilder_foreach</c>: the write order from
    /// <c>compute_write_order</c> is written with the <c>write_one</c>
    /// recursion (delta base before dependent, cycles broken) and the
    /// do/while re-scan loop.
    /// </summary>
    /// <param name="output">The output stream.</param>
    /// <param name="progress">Optional pack progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteAsync(Stream output, IProgress<GitPackProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        await PrepareAsync(progress, cancellationToken).ConfigureAwait(false);

        List<PackObject> order = await ComputeWriteOrderAsync(cancellationToken).ConfigureAwait(false);
        ValidateWriteOrderCount(order.Count, _objectList.Count);
        int total = order.Count;

        using var incrementalHash = GitIncrementalHash.Create(_algorithm);

        // Write pack header: "PACK" + version 2 + object count (all big-endian)
        byte[] header = new byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), total);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        incrementalHash.AppendData(header);

        int written = 0;

        // do { nr_written = 0; for (...) write_one; } while (nr_remaining && ...)
        // (pack-objects.c:664-674) — the recursion resolves every object in
        // one pass; the loop shape is kept for parity.
        int remaining = total;
        do
        {
            int passWritten = 0;
            foreach (PackObject po in order)
            {
                if (await WriteOneAsync(po, async (data, ct) =>
                {
                    await output.WriteAsync(data, ct).ConfigureAwait(false);
                    incrementalHash.AppendData(data.Span);
                    written++;
                    // C (pack-objects.c, write_pack): NO progress is reported while writing — the packbuilder stage enum has only ADDING_OBJECTS (0) and
                    // DELTAFICATION (1); write progress flows through the indexer callback.
                    return data.Length;
                }, cancellationToken).ConfigureAwait(false) == WriteOneStatus.Written)
                {
                    passWritten++;
                }
            }

            remaining -= passWritten;
        }
        while (remaining > 0 && written < total);

        // Write the pack trailer (SHA-1/SHA-256 of all preceding bytes).
        GitOid trailer = incrementalHash.Finalize();
        await output.WriteAsync(trailer.RawBytes.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the pack to a file via a <see cref="GitPackIndexer"/> (which writes
    /// both the <c>.pack</c> and <c>.idx</c> v2 files). Matches
    /// <c>git_packbuilder_write</c>.
    /// </summary>
    /// <param name="packDir">Directory for <c>pack-*.pack</c> / <c>pack-*.idx</c> files.</param>
    /// <param name="progress">Optional pack progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path to the written <c>.pack</c> file.</returns>
    public async Task<string> WriteToDirectoryAsync(string packDir, IProgress<GitPackProgress>? progress, CancellationToken cancellationToken = default)
    {
        await PrepareAsync(progress, cancellationToken).ConfigureAwait(false);

        List<PackObject> order = await ComputeWriteOrderAsync(cancellationToken).ConfigureAwait(false);
        ValidateWriteOrderCount(order.Count, _objectList.Count);
        int total = order.Count;

        var indexer = new GitPackIndexer(packDir, _algorithm, _odb);
        await using ConfiguredAsyncDisposable indexerDisposable = indexer.ConfigureAwait(false);
        var stats = new GitIndexerProgress();

        // C (pack-objects.c:1453-1454): core.fsyncObjectFiles enables fsync
        // on the indexer's pack writes.
        if (await _repo.Config.GetBoolAsync("core.fsyncObjectFiles", defaultValue: false, cancellationToken).ConfigureAwait(false))
        {
            indexer.SetFsync(true);
        }

        // Write pack header
        byte[] header = new byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        WriteUInt32BE(header, 4, 2);
        WriteUInt32BE(header, 8, (uint)_objectList.Count);
        await indexer.AppendAsync(header, stats, cancellationToken).ConfigureAwait(false);

        using var incrementalHash = GitIncrementalHash.Create(_algorithm);
        incrementalHash.AppendData(header);

        int writtenCount = 0;
        foreach (PackObject po in order)
        {
            await WriteOneAsync(po, async (data, ct) =>
            {
                await indexer.AppendAsync(data, stats, ct).ConfigureAwait(false);
                incrementalHash.AppendData(data.Span);
                writtenCount++;
                // C: no stage-2 progress during write_pack.
                return data.Length;
            }, cancellationToken).ConfigureAwait(false);
        }

        // Write the pack trailer (SHA-1/SHA-256 of all preceding bytes).
        byte[] trailer = incrementalHash.Finalize().RawBytes.ToArray();
        await indexer.AppendAsync(trailer, stats, cancellationToken).ConfigureAwait(false);

        await indexer.CommitAsync(stats, cancellationToken).ConfigureAwait(false);
        string? packPath = indexer.PackPath;
        Debug.Assert(packPath is not null, "PackPath is set after CommitAsync");
        return packPath;
    }

    /// <summary>
    /// Writes one object with its delta base first. Matches <c>write_one</c>
    /// (pack-objects.c:416-449): recursing into the base guarantees
    /// base-before-dependent; a cycle breaks by dropping the delta.
    /// </summary>
    private async Task<WriteOneStatus> WriteOneAsync(PackObject po, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<long>> sink, CancellationToken cancellationToken)
    {
        if (po.Recursing)
        {
            return WriteOneStatus.Recursive;
        }

        if (po.Written)
        {
            return WriteOneStatus.Skip;
        }

        if (po.Delta is not null)
        {
            po.Recursing = true;
            WriteOneStatus status = await WriteOneAsync(po.Delta, sink, cancellationToken).ConfigureAwait(false);

            // We cannot depend on this one (cycle).
            if (status == WriteOneStatus.Recursive)
            {
                po.Delta = null;
            }
        }

        po.Written = true;
        po.Recursing = false;

        await WriteObjectAsync(po, sink, cancellationToken).ConfigureAwait(false);
        return WriteOneStatus.Written;
    }

    /// <summary>
    /// Serializes a single object to its pack representation (header +
    /// optional base OID + compressed body) and hands it to
    /// <paramref name="sink"/>.
    /// </summary>
    private async Task<long> WriteObjectAsync(PackObject po, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<long>> sink, CancellationToken cancellationToken)
    {
        using var buffer = new PooledByteBufferWriter();

        if (po.Delta is not null)
        {
            // Write as REF_DELTA.
            byte[] deltaData;
            if (po.DeltaData is not null)
            {
                deltaData = po.DeltaData;
            }
            else
            {
                // Defensive fallback for release-mode safety.
                Debug.Assert(condition: false, message: "DeltaData should be set whenever Delta is set; recomputing as fallback.");
                GitObject? baseObj = await _odb.LookupAsync(po.Delta.Id, cancellationToken).ConfigureAwait(false);
                GitObject? targetObj = await _odb.LookupAsync(po.Id, cancellationToken).ConfigureAwait(false);
                if (baseObj is null || targetObj is null)
                {
                    baseObj?.Dispose();
                    targetObj?.Dispose();
                    throw new GitException(GitErrorCode.NotFound, $"missing object for delta", GitErrorCategory.Odb);
                }

                byte[]? computed = DeltaEncoder.Create(baseObj.Raw, targetObj.Raw, targetObj.Raw.Length);
                deltaData = computed ?? [];
                baseObj.Dispose();
                targetObj.Dispose();
            }

            // Pack object header: type=REF_DELTA (7), size=delta length
            WritePackObjectHeader(buffer, GitObjectType.RefDelta, deltaData.Length);

            // REF_DELTA base OID (20 or 32 bytes)
            buffer.Write(po.Delta.Id.RawBytes);

            // Compressed delta data
            Zlib.CompressLooseObject(buffer, deltaData);
        }
        else
        {
            // Write as full object.
            GitObject? obj = await _odb.LookupAsync(po.Id, cancellationToken).ConfigureAwait(false);
            if (obj is null)
            {
                throw new GitException(GitErrorCode.NotFound, $"object {po.Id} not found", GitErrorCategory.Odb);
            }

            WritePackObjectHeader(buffer, po.Type, obj.Raw.Length);

            Zlib.CompressLooseObject(buffer, obj.Raw.Span);

            obj.Dispose();
        }

        return await sink(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a pack object header: first byte is (type &lt;&lt; 4) | (size &amp; 15),
    /// MSB=1 means continuation. Subsequent bytes: 7 bits each, MSB=1 for more.
    /// Delegates to <see cref="PackEncoding.WriteObjectHeader"/>.
    /// </summary>
    private static void WritePackObjectHeader(PooledByteBufferWriter output, GitObjectType type, long size)
    {
        Span<byte> hdr = output.GetSpan(16);
        int len = PackEncoding.WriteObjectHeader(hdr, type, size);
        output.Advance(len);
    }

    private static void WriteUInt32BE(byte[] buf, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset, 4), value);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _objectList.Clear();
        _objectIndex.Clear();
        _disposed = true;
    }

    /// <summary>
    /// A single object in the pack. Matches <c>git_pobject</c> in pack-objects.h.
    /// </summary>
    private sealed class PackObject
    {
        public GitOid Id;
        public GitObjectType Type;
        public long Size;
        /// <summary>The name hash from insert time (C's <c>po-&gt;hash</c>; 0 when no name).</summary>
        /// <summary>Name hash (C's <c>po->hash</c>, pack-objects.c:248).</summary>
        public uint Hash;

        /// <summary>Insertion (recency) index — C's pointer-order tiebreak.</summary>
        public int InsertionIndex;

        public PackObject? Delta;

        /// <summary>The raw delta bytes (C's <c>delta_data</c> before zlib).</summary>
        public byte[]? DeltaData;

        /// <summary>The delta size (C's <c>delta_size</c>).</summary>
        public long DeltaSize;

        /// <summary>True if this object is at the tip of a repo tag.</summary>
        public bool Tagged;

        /// <summary>True if already added to the write order.</summary>
        public bool Filled;

        /// <summary>True while being written (cycle detection).</summary>
        public bool Recursing;

        /// <summary>True once written.</summary>
        public bool Written;

        public PackObject? DeltaChild;
        public PackObject? DeltaSibling;
    }
}
