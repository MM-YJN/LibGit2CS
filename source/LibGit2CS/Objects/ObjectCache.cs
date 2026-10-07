// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

using LibGit2CS.Core;

namespace LibGit2CS.Objects;

/// <summary>
/// LRU-style cache of parsed objects. Managed port of libgit2's
/// <c>src/libgit2/cache.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache cap</b>: libgit2 caps the cache at <c>git_cache__max_storage = 256 MiB</c>
/// with per-type limits (commit/tree/tag ≤ 4096 bytes, blob = always cache, delta =
/// never cache). Eviction is randomized (<c>cache_evict_entries</c> evicts
/// <c>max(8, size/2048)</c> arbitrary entries), not strict LRU.
/// </para>
/// <para>
/// This managed port uses a simple <see cref="Dictionary{TKey, TValue}"/> with
/// size tracking and randomized eviction. The cap defaults to 256 MiB.
/// </para>
/// </remarks>
internal sealed class ObjectCache : IDisposable
{
    private const long DefaultMaxStorage = 256 * 1024 * 1024; // 256 MiB

    private readonly Dictionary<GitOid, GitObject> _cache = [];
    private readonly long _maxStorage = DefaultMaxStorage;
    private long _usedStorage;
    private bool _disposed;

    /// <summary>Current number of cached objects.</summary>
    public int Count => _cache.Count;

    /// <summary>Current bytes used by cached objects.</summary>
    public long UsedStorage => _usedStorage;

    /// <summary>
    /// When true, bypasses the &lt;4096-byte size gate for trees, allowing large
    /// trees to be cached. Set by <c>BlameEngine</c> for the duration of a blame
    /// walk to avoid re-parsing the same large trees across thousands of diffs.
    /// The managed port has no mmap zero-copy reads, so the native libgit2 size
    /// gate (tuned for its mmap world) is too aggressive here. Memory is bounded
    /// by the 256 MiB cap + randomized eviction. Defaults to false (native parity).
    /// </summary>
    internal bool AllowLargeTrees { get; set; }

    /// <summary>
    /// Gets a cached object by OID. Returns null if not cached.
    /// Matches <c>git_cache_get_any</c>.
    /// </summary>
    public GitObject? Get(GitOid id)
    {
        if (_disposed)
        {
            return null;
        }

        return _cache.TryGetValue(id, out GitObject? obj) ? obj : null;
    }

    /// <summary>
    /// Stores an object in the cache. Matches <c>git_cache_store_parsed</c>.
    /// Objects exceeding the per-type limit are not cached.
    /// </summary>
    public void Put(GitOid id, GitObject obj)
    {
        if (_disposed)
        {
            return;
        }

        if (!ShouldCache(obj))
        {
            return;
        }

        // If already cached, don't double-count.
        if (_cache.ContainsKey(id))
        {
            return;
        }

        _cache[id] = obj;
        _usedStorage += obj.Size;

        // Evict if over capacity.
        if (_usedStorage > _maxStorage)
        {
            EvictEntries();
        }
    }

    /// <summary>Clears all cached objects. Matches <c>git_cache_clear</c>.</summary>
    public void Clear()
    {
        foreach (GitObject obj in _cache.Values)
        {
            obj.Dispose();
        }

        _cache.Clear();
        _usedStorage = 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }

    private bool ShouldCache(GitObject obj)
    {
        // C (cache.c:25-34, 132-136, cache_should_store): per-type max
        // {commit:4096, tree:4096, blob:0, tag:4096} with a STRICT `<` — so
        // blobs are NEVER cached and a 4096-byte object is not cached either.
        // When AllowLargeTrees is set (blame walk), trees bypass the size gate:
        // the managed port re-allocates and re-parses on every cache miss
        // (native re-reads via mmap, zero-copy), so the native policy needs a
        // different tuning for the managed world.
        return obj.Type switch
        {
            GitObjectType.Tree => AllowLargeTrees || obj.Size < 4096,
            GitObjectType.Commit or GitObjectType.Tag => obj.Size < 4096,
            _ => false,
        };
    }

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Randomized eviction matches libgit2's cache_evict_entries — not a security-sensitive random use.")]
    private void EvictEntries()
    {
        // C (cache.c:102-107): evict cache_size / 2048 entries, min 8 — the
        // count is per-cache and the eviction is unconditional (no early
        // stop; the global-storage trigger lives at the call site, cache.c:176).
        int toEvict = Math.Max(8, _cache.Count / 2048);
        int evicted = 0;

        // Materialize a list of keys to evict (can't modify dict during iteration).
        GitOid[] keys = _cache.Keys.ToArray();
        var random = new Random();

        foreach (GitOid key in keys.OrderBy(_ => random.Next()))
        {
            if (evicted >= toEvict)
            {
                break;
            }

            if (_cache.TryGetValue(key, out GitObject? obj))
            {
                _cache.Remove(key);
                _usedStorage -= obj.Size;
                obj.Dispose();
                evicted++;
            }
        }
    }
}
