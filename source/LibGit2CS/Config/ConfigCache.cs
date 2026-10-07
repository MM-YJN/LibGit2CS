// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Config;

/// <summary>
/// Per-instance atomic cache of config-derived values (booleans, enums, ints).
/// Managed port of libgit2's <c>config_cache.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// libgit2 stores this cache directly in <c>git_repository::configmap_cache[]</c>
/// as an array of atomically-loaded <c>intptr_t</c> slots, with
/// <c>GIT_CONFIGMAP_NOT_CACHED</c> as the sentinel. On every config write, the
/// cache is cleared wholesale.
/// </para>
/// <para>
/// This class is the standalone managed equivalent. It is NOT wired here — the
/// <c>Repository</c> type will own an instance and call
/// <see cref="Clear"/> on writes. The cache key (<c>TKey</c>) is whatever enum
/// the Repository defines for its cacheable configmap items.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The cache slot key (typically an enum).</typeparam>
internal sealed class ConfigCache<TKey> where TKey : struct, Enum
{
    private readonly int[] _slots;
    private readonly int _notCachedSentinel;

    /// <summary>
    /// Creates a cache with <paramref name="slotCount"/> slots. The
    /// <paramref name="notCachedSentinel"/> value marks an uninitialized slot.
    /// </summary>
    public ConfigCache(int slotCount, int notCachedSentinel = int.MinValue)
    {
        _slots = new int[slotCount];
        _notCachedSentinel = notCachedSentinel;
        Array.Fill(_slots, notCachedSentinel);
    }

    /// <summary>
    /// Gets the cached value for <paramref name="key"/>, or computes it via
    /// <paramref name="factory"/> and atomically stores the result. Thread-safe
    /// via <see cref="Interlocked.CompareExchange(ref int, int, int)"/>. Matches
    /// libgit2's <c>git_repository__configmap_lookup</c>.
    /// </summary>
    public int GetOrCompute(TKey key, Func<TKey, int> factory)
    {
        int idx = (int)(object)key;
        int value = Interlocked.CompareExchange(ref _slots[idx], 0, 0);

        if (value != _notCachedSentinel)
        {
            return value;
        }

        int computed = factory(key);
        Interlocked.CompareExchange(ref _slots[idx], computed, _notCachedSentinel);

        // Either our value won the race, or another thread's value did. Both
        // are valid; return whatever is now in the slot.
        return Interlocked.CompareExchange(ref _slots[idx], 0, 0);
    }

    /// <summary>
    /// Clears all cache slots back to the not-cached sentinel. Matches
    /// libgit2's <c>git_repository__configmap_lookup_cache_clear</c>.
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            Interlocked.Exchange(ref _slots[i], _notCachedSentinel);
        }
    }
}
