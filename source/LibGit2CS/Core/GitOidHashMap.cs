// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Diagnostics.CodeAnalysis;

namespace LibGit2CS.Core;

/// <summary>
/// A dictionary keyed by <see cref="GitOid"/>. Managed wrapper around
/// <c>Dictionary&lt;GitOid, T&gt;</c>, replacing libgit2's <c>hashmap_oid.h</c>.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class GitOidHashMap<T> where T : class
{
    private readonly Dictionary<GitOid, T> _map;

    /// <summary>Creates an empty map with the default capacity.</summary>
    public GitOidHashMap() => _map = [];

    /// <summary>Creates an empty map with the given initial capacity.</summary>
    public GitOidHashMap(int capacity) => _map = new(capacity);

    /// <summary>Number of entries.</summary>
    public int Count => _map.Count;

    /// <summary>Gets or sets the value for the given OID.</summary>
    [SuppressMessage("Design", "CA1043:Use Integral Or String Argument For Indexers", Justification = "GitOid-keyed indexer is the whole point of this type.")]
    public T? this[GitOid oid]
    {
        get => _map.GetValueOrDefault(oid);
        set
        {
            if (value is null)
            {
                _map.Remove(oid);
            }
            else
            {
                _map[oid] = value;
            }
        }
    }

    /// <summary>Adds an entry. Throws if the key already exists.</summary>
    public void Add(GitOid oid, T value) => _map.Add(oid, value);

    /// <summary>Removes the entry for the given OID.</summary>
    public bool Remove(GitOid oid) => _map.Remove(oid);

    /// <summary>True if the map contains the given OID.</summary>
    public bool ContainsKey(GitOid oid) => _map.ContainsKey(oid);

    /// <summary>Tries to get the value for the given OID.</summary>
    public bool TryGetValue(GitOid oid, [MaybeNullWhen(false)] out T value) => _map.TryGetValue(oid, out value);

    /// <summary>Removes all entries.</summary>
    public void Clear() => _map.Clear();
}
