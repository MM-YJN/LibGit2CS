// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Config;

/// <summary> Ordered store of <see cref="GitConfigEntry"/> values with multivar tracking and last-wins lookup. Managed equivalent of libgit2's
/// <c>git_config_list</c>. </summary> <remarks> <para> In libgit2, <c>git_config_list</c> is a refcounted structure with an interned string map (for
/// <c>backend_type</c>/<c>origin_path</c> deduplication), a hashmap from key name to the last <c>config_entry_map_head</c>, and a linked list preserving
/// insertion order. The refcounting exists because raw pointers into the list's strings escape to callers via <c>git_config_entry</c>. </para> <para> In
/// managed code this collapses dramatically: <see cref="GitConfigEntry"/> is an immutable value type, the GC reclaims strings, and no refcounting is needed.
/// This class keeps the same observable semantics — last-wins <see cref="Get"/>, multivar-aware <see cref="GetUnique"/>, insertion-order iteration — using a
/// <see cref="List{T}"/> and a <see cref="Dictionary{TKey, TValue}"/> for O(1) last-entry lookup. </para> <para> The head map is keyed on the entry's raw name
/// bytes (<see cref="GitConfigEntry.NameBytes"/>) via <see cref="ConfigNameKey"/> — C's <c>git_config_list_headmap</c> is a <c>strcmp</c>-keyed hashmap over
/// <c>char *</c> name bytes (config_list.c:29, hashmap_str.h:24-27), so non-UTF-8 subsection bytes round-trip byte-exact instead of degrading to the U+FFFD
/// display decode. </para> </remarks>
internal sealed class ConfigList
{
    private readonly List<GitConfigEntry> _entries = [];
    private readonly Dictionary<ConfigNameKey, EntryHead> _heads = [];

    /// <summary>
    /// Creates an empty config list.
    /// </summary>
    public static ConfigList New() => new();

    /// <summary>
    /// Appends an entry. Subsequent <see cref="Get"/> lookups for the same key
    /// will return this entry (last-wins). If a prior entry with the same name
    /// exists, the key is marked as a multivar.
    /// </summary>
    public void Append(GitConfigEntry entry)
    {
        var key = ConfigNameKey.From(entry.NameBytes);
        if (_heads.TryGetValue(key, out EntryHead? head))
        {
            head._multivar = true;
            head._lastIndex = _entries.Count;
        }
        else
        {
            _heads[key] = new EntryHead { _lastIndex = _entries.Count };
        }

        _entries.Add(entry);
    }

    /// <summary>
    /// Returns the LAST entry for the given key (last-wins semantics), or
    /// <c>null</c> if no entry exists. Matches libgit2's <c>git_config_list_get</c>.
    /// </summary>
    public GitConfigEntry? Get(ConfigNameKey key)
    {
        if (!_heads.TryGetValue(key, out EntryHead? head))
        {
            return null;
        }

        return _entries[head._lastIndex];
    }

    /// <summary>
    /// Returns the entry for the given key only if it is unique (not a multivar)
    /// and was not pulled in via <c>[include]</c> (include_depth == 0).
    /// Matches libgit2's <c>git_config_list_get_unique</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if the key is a multivar.
    /// </exception>
    public GitConfigEntry? GetUnique(ConfigNameKey key)
    {
        if (!_heads.TryGetValue(key, out EntryHead? head))
        {
            return null;
        }

        if (head._multivar)
        {
            throw new GitException(
                GitErrorCode.Error,
                "entry is not unique due to being a multivar",
                GitErrorCategory.Config);
        }

        GitConfigEntry entry = _entries[head._lastIndex];
        if (entry.IncludeDepth != 0)
        {
            throw new GitException(
                GitErrorCode.Error,
                "entry is not unique due to being included",
                GitErrorCategory.Config);
        }

        return entry;
    }

    /// <summary>
    /// Iterates all entries in insertion order. Matches libgit2's
    /// <c>git_config_list_iterator</c>.
    /// </summary>
    public IEnumerable<GitConfigEntry> Enumerate() => _entries;

    /// <summary>
    /// Number of entries.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Creates a deep copy of this list (entries are value types, so the copy
    /// is independent). Matches libgit2's <c>git_config_list_dup</c>.
    /// </summary>
    public ConfigList Duplicate()
    {
        var copy = new ConfigList();
        foreach (GitConfigEntry entry in _entries)
        {
            // ConfigEntry is a readonly record struct; value-copy is independent.
            // We re-append (rather than bulk-copy) to rebuild the multivar/heads index.
            copy.Append(entry);
        }

        return copy;
    }

    /// <summary>
    /// Adds a duplicate of <paramref name="entry"/> to this list. The entry's
    /// strings are shared (immutable in managed code — no interning needed).
    /// Matches libgit2's <c>git_config_list_dup_entry</c>.
    /// </summary>
    public void AppendDuplicate(in GitConfigEntry entry) => Append(entry);

    /// <summary>
    /// Removes ALL entries with the given key from the list. Returns true if
    /// any entries were removed. Used by the config write path.
    /// </summary>
    public bool Remove(ConfigNameKey key)
    {
        if (!_heads.TryGetValue(key, out EntryHead? head))
        {
            return false;
        }

        _heads.Remove(key);

        // Remove all entries with this key from the list.
        _entries.RemoveAll(e => ConfigNameKey.From(e.NameBytes) == key);

        // Rebuild heads indices since we shifted indices.
        RebuildHeads();
        return true;
    }

    private void RebuildHeads()
    {
        _heads.Clear();
        for (int i = 0; i < _entries.Count; i++)
        {
            GitConfigEntry entry = _entries[i];
            var key = ConfigNameKey.From(entry.NameBytes);
            if (_heads.TryGetValue(key, out EntryHead? head))
            {
                head._multivar = true;
                head._lastIndex = i;
            }
            else
            {
                _heads[key] = new EntryHead { _lastIndex = i };
            }
        }
    }

    private sealed class EntryHead
    {
        public int _lastIndex;
        public bool _multivar;
    }
}
