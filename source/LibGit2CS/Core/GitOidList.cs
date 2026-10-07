// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Core;

/// <summary>
/// A list of <see cref="GitOid"/> values. Managed wrapper around
/// <c>List&lt;GitOid&gt;</c>, replacing libgit2's <c>oidarray.c</c>.
/// </summary>
public sealed class GitOidList
{
    private readonly List<GitOid> _list;

    /// <summary>Creates an empty list.</summary>
    public GitOidList() => _list = [];

    /// <summary>Creates an empty list with the given initial capacity.</summary>
    public GitOidList(int capacity) => _list = new(capacity);

    /// <summary>Number of OIDs.</summary>
    public int Count => _list.Count;

    /// <summary>Gets or sets the OID at the given index.</summary>
    public GitOid this[int index]
    {
        get => _list[index];
        set => _list[index] = value;
    }

    /// <summary>Adds an OID to the end of the list.</summary>
    public void Add(GitOid oid) => _list.Add(oid);

    /// <summary>Adds a range of OIDs.</summary>
    public void AddRange(IEnumerable<GitOid> oids) => _list.AddRange(oids);

    /// <summary>Removes all OIDs.</summary>
    public void Clear() => _list.Clear();

    /// <summary>True if the list contains the given OID.</summary>
    public bool Contains(GitOid oid) => _list.Contains(oid);

    /// <summary>Removes the first occurrence of the given OID.</summary>
    public bool Remove(GitOid oid) => _list.Remove(oid);

    /// <summary>Sorts the list using the default OID comparison.</summary>
    public void Sort() => _list.Sort();

    /// <summary>Exposes the underlying OIDs as a read-only list.</summary>
    public IReadOnlyList<GitOid> AsReadOnly() => _list;

    /// <summary>Returns an enumerator over the OIDs.</summary>
    public List<GitOid>.Enumerator GetEnumerator() => _list.GetEnumerator();
}
